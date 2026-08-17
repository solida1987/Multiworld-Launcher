-- ═══════════════════════════════════════════════════════════════════════════════
-- pokemon_emerald.lua — game module for the Archipelago BizHawk connector.
--
-- STATUS: IMPLEMENTED — check detection, item delivery, goal detection.
-- Logic and every address below mirror the official Archipelago 0.6.6
-- pokemon_emerald apworld client (worlds/pokemon_emerald/client.py,
-- "pokemon emerald version / AP 5" ROM revision). Full derivation with
-- citations: Research_V2/POKEMON_EMERALD_BRIDGE_2026-06-12.md.
--
-- MEMORY ACCESS (GBA via BizHawk's mGBA core)
-- ─────────────────────────────────────────────
--   Domain "System Bus" with full GBA bus addresses (exactly what the
--   apworld's _bizhawk client uses), domain "ROM" for the identity check.
--   Emerald keeps save data behind MOVING pointers (anti-cheat DMA
--   shuffle): gSaveBlock1Ptr/gSaveBlock2Ptr are re-read on EVERY poll and
--   re-checked AFTER each dependent read — a change mid-read discards the
--   sample (this stands in for the apworld's guarded_read).
--
-- SELF-GATING
-- ────────────
--   The module verifies the loaded ROM is the AP-patched revision (32-byte
--   name at ROM+0x108 == "pokemon emerald version / AP 5" — the same check
--   client.py's validate_rom performs). On a vanilla/foreign ROM it logs
--   why and stays a safe no-op: no checks, no goal, no memory writes.
--
-- ITEM DELIVERY (client.py handle_received_items, line 495)
-- ──────────────────────────────────────────────────────────
--   The patched game exposes:
--     u16 @ SaveBlock1+0x3778      how many items the save has processed
--     gArchipelagoReceivedItem (0x0203D1E8):
--       +0 u16 item (game id = AP id − 3860000)   +2 u16 next index
--       +4 u8  is_filled                          +5 u8  should_display
--   When processed-count < #delivery-list and the buffer is empty, write
--   the next item and the game consumes it (clears +4, bumps its counter).
--   The delivery list is rebuilt from the launcher's extended ITEM lines
--   (absolute stream index, player, flags, location) and filtered exactly
--   like the official client's items_handling=1/3: own-world items are
--   skipped unless slot_data.remote_items, server/starting-inventory
--   entries (player 0, location ≤ 0) are always skipped — so the save's
--   counter agrees with what the official client would have delivered.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "pokemon_emerald"

-- ── Address map ───────────────────────────────────────────────────────────────
-- Verification state (2026-06-12, AP-patched ROM from seed
-- AP_81769672980259429628 running in BizHawk/mGBA):
--   PROVEN LIVE   ROM identity read (0x108/"ROM" returned the exact AP 5
--                 name), sidecar parsing (194 locations = server truth),
--                 item transport launcher→module ("ITEM received: 3860030
--                 (index 0)"), own-item filtering, and the title-screen
--                 guards idling cleanly (~10k polls, zero errors/writes).
--   PER-SOURCE +  Flag scan offsets, received-count handshake and the
--   MOCK-PROVEN   4-write receive buffer: byte-exact mirrors of client.py
--                 (see file header), validated against a mocked GBA memory
--                 map — an in-overworld save playthrough is the remaining
--                 confirmation.
-- The runtime ROM-name gate keeps all of it from ever running against a
-- ROM revision these addresses do not belong to.
local ADDRESSES_VERIFIED = true

local SYSTEM_BUS = "System Bus"
local ROM_DOMAIN = "ROM"

local SB1_PTR        = 0x03005D8C   -- gSaveBlock1Ptr   (extracted_data.json)
local SB2_PTR        = 0x03005D90   -- gSaveBlock2Ptr
local GMAIN_CB2      = 0x030022C0 + 4        -- gMain.callback2
local CB2_OVERWORLD  = 0x080867F0 + 1        -- CB2_Overworld | thumb bit
local RECV           = 0x0203D1E8   -- gArchipelagoReceivedItem

local FLAGS_OFFSET   = 0x1450       -- SaveBlock1.flags (AP layout; client.py reads sb1+5200)
local FLAGS_BYTES    = 300          -- 2400 flags, read as 2×150 like client.py
local RECV_COUNT_OFF = 0x3778       -- u16 processed-item count (client.py sb1+14200)
local DEX_CAUGHT_OFF = 0x28         -- SaveBlock2 pokedex caught bitfield (sb2+40)
local DEX_CAUGHT_LEN = 52

local BASE_OFFSET    = 3860000      -- data.py line 17
local POKEDEX_OFFSET = 10000        -- data.py line 18
local FLAGS_COUNT    = 2400

local EXPECTED_ROM_NAME = "pokemon emerald version / AP 5"   -- client.py line 23

-- Goal flags (client.py lines 25-27): TRAINER_FLAGS_START(1280) + trainer id.
-- slot_data.goal: 0=champion 1=steven 2=norman 3=legendary_hunt(unsupported).
local GOAL_FLAGS = {
  [0] = 1280 + 335,   -- TRAINER_WALLACE  → 1615
  [1] = 1280 + 804,   -- TRAINER_STEVEN   → 2084
  [2] = 1280 + 269,   -- TRAINER_NORMAN_1 → 1549
}

local POLL_INTERVAL = 0.1           -- client.py watcher_timeout = 0.125 s

-- ── Internal state ────────────────────────────────────────────────────────────

local log_fn          = nil
local rom_ok          = false       -- AP ROM identity verified
local why_disabled    = "init() not run"
local slot_number     = 0
local remote_items    = false
local dexsanity       = false
local goal_flag       = nil
local flag_locs       = {}          -- event flag id -> AP location id
local dex_locs        = {}          -- dex number    -> AP location id
local have_locations  = false
local reported        = {}          -- AP location id -> true (already returned)
local goal_reached    = false
local last_poll       = 0

-- Item stream: raw_items is keyed by ABSOLUTE 0-based AP stream index;
-- delivery is the filtered 1-based list the save counter indexes into.
local raw_items       = {}
local legacy_next     = 0           -- append position for index-less ITEM lines
local delivery        = {}
local delivery_dirty  = false
local delivered_log   = 0           -- deliveries logged so far (sampling)
local bad_item_warned = false

-- POW2[n] = 2^n for the bit tests below (note POW2[8] = 256: the bit-7 test
-- needs `byte % 256`).
local POW2 = { [0]=1, [1]=2, [2]=4, [3]=8, [4]=16, [5]=32, [6]=64, [7]=128, [8]=256 }

local function log(msg)
  if log_fn then log_fn("[pokemon_emerald] " .. tostring(msg)) end
end

-- ── Memory helpers (defensive: never nil-crash on any BizHawk version) ────────

local mem = {}

local function resolve_memory_api()
  if not memory then return false end
  mem.read_u8    = memory.read_u8       or memory.readbyte
  mem.read_u16   = memory.read_u16_le   or memory.readword
  mem.read_u32   = memory.read_u32_le   or memory.readdword
  mem.write_u8   = memory.write_u8      or memory.writebyte
  mem.write_u16  = memory.write_u16_le  or memory.writeword
  mem.read_range = memory.read_bytes_as_array or memory.readbyterange
  return mem.read_u8 ~= nil and mem.read_u32 ~= nil
end

local function rd(fn, addr, domain)
  if not fn then return nil end
  local ok, v = pcall(fn, addr, domain or SYSTEM_BUS)
  if ok and type(v) == "number" then return v end
  ok, v = pcall(fn, addr)                       -- older API: current domain
  if ok and type(v) == "number" then return v end
  return nil
end

local function read_u8(addr, domain)  return rd(mem.read_u8,  addr, domain) end
local function read_u16(addr, domain) return rd(mem.read_u16, addr, domain) end
local function read_u32(addr, domain) return rd(mem.read_u32, addr, domain) end

local function write_u8(addr, value)
  if not mem.write_u8 then return false end
  return (pcall(mem.write_u8, addr, value, SYSTEM_BUS))
end

local function write_u16(addr, value)
  if not mem.write_u16 then return false end
  return (pcall(mem.write_u16, addr, value, SYSTEM_BUS))
end

-- Read `len` bytes; returns a 1-based array or nil. Tries the bulk API
-- first (read_bytes_as_array is 1-based, legacy readbyterange 0-based —
-- both normalized), falls back to a per-byte loop.
local function read_range(addr, len, domain)
  if mem.read_range then
    local ok, t = pcall(mem.read_range, addr, len, domain or SYSTEM_BUS)
    if ok and type(t) == "table" then
      if t[0] ~= nil then                       -- 0-based → shift to 1-based
        local out = {}
        for i = 0, len - 1 do out[i + 1] = t[i] end
        return out
      end
      if t[1] ~= nil or len == 0 then return t end
    end
  end
  local out = {}
  for i = 0, len - 1 do
    local v = read_u8(addr + i, domain)
    if not v then return nil end
    out[i + 1] = v
  end
  return out
end

local function valid_save_ptr(p)
  return p and p >= 0x02000000 and p <= 0x0203FFFF
end

-- ── ROM identity (client.py validate_rom, line 164) ───────────────────────────

local function read_rom_name()
  local bytes = read_range(0x108, 32, ROM_DOMAIN)
  if not bytes then return nil end
  local out = {}
  for i = 1, #bytes do
    local b = bytes[i]
    if b and b ~= 0 then out[#out + 1] = string.char(b) end
  end
  return table.concat(out)
end

-- ── Config / location map ─────────────────────────────────────────────────────

local function load_locations(ids)
  local n_flags, n_dex, n_other = 0, 0, 0
  for _, raw in ipairs(ids or {}) do
    local id = tonumber(raw)
    if id then
      local rel = id - BASE_OFFSET
      if rel >= 0 and rel < FLAGS_COUNT then
        flag_locs[rel] = id
        n_flags = n_flags + 1
      elseif rel >= POKEDEX_OFFSET and rel <= POKEDEX_OFFSET + 500 then
        dex_locs[rel - POKEDEX_OFFSET] = id     -- dex number (1-based)
        n_dex = n_dex + 1
      else
        n_other = n_other + 1
      end
    end
  end
  have_locations = (n_flags + n_dex) > 0
  log(string.format("location map: %d flag checks, %d dex checks, %d unmapped ids",
                    n_flags, n_dex, n_other))
end

-- ── Delivery list (mirrors ctx.items_received filtering) ──────────────────────

local function accept_item(it)
  -- Starting-inventory / server-granted entries (player 0, no real
  -- location): the official client connects with items_handling that
  -- excludes them — the patched game never expects them in its counter.
  if it.player == 0 and (it.location or 0) <= 0 then return false end
  -- Items found in OUR OWN world: the patched game grants those locally
  -- unless the seed was generated with remote_items.
  if (not remote_items) and it.player == slot_number then return false end
  return true
end

local function rebuild_delivery()
  delivery = {}
  local i = 0
  while raw_items[i] do
    local it = raw_items[i]
    -- Entries without metadata (legacy plain ITEM lines) are accepted —
    -- there is nothing to filter on.
    if it.player == nil or accept_item(it) then
      delivery[#delivery + 1] = it
    end
    i = i + 1
  end
  delivery_dirty = false
end

local function should_display(it)
  -- client.py line ~512: display if progression (flags & 1) or own find.
  local flags = it.flags or 0
  if flags % 2 == 1 then return 1 end
  if it.player ~= nil and it.player == slot_number then return 1 end
  return 0
end

-- One delivery attempt per poll (the game consumes one buffer per frame
-- anyway). sb1 was validated by the caller; re-check it right before the
-- writes (DMA shuffle guard).
local function deliver_next(sb1)
  if delivery_dirty then rebuild_delivery() end

  local count  = read_u16(sb1 + RECV_COUNT_OFF)
  local filled = read_u8(RECV + 4)
  if not count or not filled or filled ~= 0 then return end

  local nxt = delivery[count + 1]
  if not nxt then return end

  local game_id = nxt.id - BASE_OFFSET
  if game_id < 0 or game_id > 0xFFFF then
    if not bad_item_warned then
      bad_item_warned = true
      log(string.format("item id %d out of range for the receive buffer — delivery halted", nxt.id))
    end
    return
  end

  if read_u32(SB1_PTR) ~= sb1 then return end   -- save block moved mid-poll

  -- Write order mirrors client.py's batch; is_filled goes LAST so the game
  -- never sees a half-written buffer.
  write_u16(RECV + 0, game_id)
  write_u16(RECV + 2, (count + 1) % 0x10000)
  write_u8 (RECV + 5, should_display(nxt))
  write_u8 (RECV + 4, 1)

  delivered_log = delivered_log + 1
  if delivered_log <= 5 or delivered_log % 25 == 0 then
    log(string.format("delivered item #%d (AP id %d, game id %d) as index %d",
                      delivered_log, nxt.id, game_id, count + 1))
  end
end

-- ── Module contract ───────────────────────────────────────────────────────────

function M.init(ctx)
  if ctx and type(ctx.log) == "function" then log_fn = ctx.log end

  if not ADDRESSES_VERIFIED then
    why_disabled = "address map not verified"
    log(why_disabled)
    return
  end
  if not resolve_memory_api() then
    why_disabled = "BizHawk memory API unavailable"
    log(why_disabled)
    return
  end

  -- ROM identity — the gate for everything (same check as validate_rom).
  local rom_name = read_rom_name()
  if rom_name ~= EXPECTED_ROM_NAME then
    if rom_name and rom_name:find("pokemon emerald version", 1, true) == 1 then
      why_disabled = string.format(
        "ROM is '%s', expected '%s' — %s. Checks and item delivery are DISABLED.",
        rom_name, EXPECTED_ROM_NAME,
        rom_name == "pokemon emerald version"
          and "this is the UNPATCHED vanilla ROM (apply the seed's .apemerald patch)"
          or  "the patch revision does not match this module")
    else
      why_disabled = string.format(
        "ROM name '%s' is not a Pokémon Emerald AP ROM — module disabled.",
        tostring(rom_name))
    end
    log(why_disabled)
    return
  end
  rom_ok = true
  log("AP ROM verified: '" .. rom_name .. "'")

  -- Multiworld context from ap_config.json (written by the launcher).
  local cfg = ctx and ctx.config or {}
  slot_number = tonumber(cfg.slot_number) or 0

  local sd = cfg.slot_data
  local goal = 0
  if type(sd) == "table" then
    goal         = tonumber(sd.goal) or 0
    remote_items = (sd.remote_items == 1) or (sd.remote_items == true)
    dexsanity    = (sd.dexsanity == 1)    or (sd.dexsanity == true)
  else
    log("no slot_data in ap_config.json — assuming goal=champion, " ..
        "remote_items=off, dexsanity=off")
  end

  goal_flag = GOAL_FLAGS[goal]
  if goal == 3 then
    log("goal=legendary_hunt is not supported by this bridge yet — goal " ..
        "completion will NOT be auto-detected (checks/items still work)")
  end

  load_locations(cfg.locations)
  if not have_locations then
    log("no server location ids in ap_config.json — check detection idle " ..
        "(connect to AP before launching); item delivery still active")
  end

  log(string.format("ready: slot #%d, goal=%d (flag %s), remote_items=%s, dexsanity=%s",
      slot_number, goal, tostring(goal_flag), tostring(remote_items), tostring(dexsanity)))
end

function M.poll()
  local out = {}
  if not rom_ok then return out end

  -- Mirror the client's 0.125 s watcher cadence instead of hammering the
  -- bus every frame.
  local now = os.clock()
  if now - last_poll < POLL_INTERVAL then return out end
  last_poll = now

  -- Guard: only sample in the overworld (gMain.callback2 == CB2_Overworld|1)
  -- — exactly the apworld client's "IN OVERWORLD" guard.
  if read_u32(GMAIN_CB2) ~= CB2_OVERWORLD then return out end

  local sb1 = read_u32(SB1_PTR)
  local sb2 = read_u32(SB2_PTR)
  if not valid_save_ptr(sb1) or not valid_save_ptr(sb2) then return out end

  -- Flag scan: client.py reads sb1+5200/150 then sb1+5350/150.
  local flags = read_range(sb1 + FLAGS_OFFSET, 150)
  local flags2 = flags and read_range(sb1 + FLAGS_OFFSET + 150, FLAGS_BYTES - 150)
  if not flags or not flags2 then return out end
  for i = 1, #flags2 do flags[150 + i] = flags2[i] end

  local dex = nil
  if dexsanity then
    dex = read_range(sb2 + DEX_CAUGHT_OFF, DEX_CAUGHT_LEN)
  end

  -- DMA-shuffle guard: discard the sample if either save pointer moved or
  -- we left the overworld while reading (stand-in for guarded_read).
  if read_u32(SB1_PTR) ~= sb1 or read_u32(SB2_PTR) ~= sb2
     or read_u32(GMAIN_CB2) ~= CB2_OVERWORLD then
    return out
  end

  for i = 1, FLAGS_BYTES do
    local byte = flags[i] or 0
    if byte ~= 0 then
      for bit = 0, 7 do
        if byte % POW2[bit + 1] >= POW2[bit] then
          local flag_id = (i - 1) * 8 + bit
          local loc = flag_locs[flag_id]
          if loc and not reported[loc] then
            reported[loc] = true
            out[#out + 1] = loc
          end
          if goal_flag and flag_id == goal_flag and not goal_reached then
            goal_reached = true
            log("goal flag " .. goal_flag .. " set — goal complete")
          end
        end
      end
    end
  end

  if dex then
    for i = 1, DEX_CAUGHT_LEN do
      local byte = dex[i] or 0
      if byte ~= 0 then
        for bit = 0, 7 do
          if byte % POW2[bit + 1] >= POW2[bit] then
            local dex_number = (i - 1) * 8 + bit + 1
            local loc = dex_locs[dex_number]
            if loc and not reported[loc] then
              reported[loc] = true
              out[#out + 1] = loc
            end
          end
        end
      end
    end
  end

  -- Item delivery shares this poll's validated save-block pointer.
  deliver_next(sb1)

  return out
end

function M.is_goal_complete()
  return goal_reached
end

function M.receive_item(item_id, meta)
  local id = tonumber(item_id)
  if not id then return end

  if meta and meta.index ~= nil then
    local idx = tonumber(meta.index)
    if idx and idx >= 0 then
      raw_items[idx] = {
        id       = id,
        player   = tonumber(meta.player),
        flags    = tonumber(meta.flags),
        location = tonumber(meta.location),
      }
      if idx >= legacy_next then legacy_next = idx + 1 end
      delivery_dirty = true
      return
    end
  end

  -- Legacy plain "ITEM:<id>" line — append in arrival order.
  raw_items[legacy_next] = { id = id }
  legacy_next = legacy_next + 1
  delivery_dirty = true
end

return M
