-- ═══════════════════════════════════════════════════════════════════════════════
-- pokemon_frlg.lua — game module for the Archipelago BizHawk connector.
--                    Pokémon FireRed & LeafGreen (GBA)
--
-- STATUS: location DETECTION + goal detection are REAL and SOURCE-DERIVED from the
-- official AP world worlds/pokemon_frlg (client.py + data/extracted_data.json +
-- data/locations.json) on the `vyneras/Archipelago` fork, branch frlg-stable
-- (apworld "1.0.4"). Every address, the ROM-identity gate, the goal flags and the
-- four flag-scan regions below are taken verbatim from that source — not guessed.
-- Loads crash-free on any ROM; self-disables on a non-AP / foreign cartridge.
--   Source: https://github.com/vyneras/Archipelago/tree/frlg-stable/worlds/pokemon_frlg
--   (FRLG is NOT in ArchipelagoMW/Archipelago main as of 2026-06; vyneras is the
--    upstream the apworld + .apfirered/.apleafgreen patches are built from.)
--
-- MEMORY MODEL (BizHawk GBA domains) — mirrors client.py game_watcher
-- ───────────────────────────────────────────────────────────────────
--   The FRLG AP client (a BizHawkClient) reads domain "System Bus" with FULL GBA
--   bus addresses (exactly like Pokémon Emerald), and domain "ROM" for the
--   identity check. Like Emerald, FRLG keeps its save data behind MOVING pointers
--   (anti-cheat DMA shuffle): gSaveBlock1Ptr / gSaveBlock2Ptr are re-read EVERY
--   poll and re-checked AFTER each dependent read — a change mid-read discards the
--   sample. This stands in for the apworld client's guarded_read + SAVE BLOCK
--   guards. A second guard ("IN OVERWORLD": gMain+0x38 == 1) keeps the
--   title-screen / menus from ever reporting checks (client.py guards["IN OVERWORLD"]).
--
--   FRLG covers FOUR base ROM revisions (FireRed, FireRed rev1, LeafGreen,
--   LeafGreen rev1). All RAM addresses below are IDENTICAL across all four
--   (verified from extracted_data.json misc_ram_addresses), so one map serves
--   every base ROM — FR and LG alike.
--
-- LOCATION MODEL (client.py game_watcher — four scan regions)
-- ───────────────────────────────────────────────────────────
--   The AP location id IS the in-game flag/computed offset directly: FRLG adds NO
--   base offset (locations.py: name_to_id = location_data.flag). The server's own
--   location set (ctx.server_locations) is the filter, so this module reports any
--   computed id that is BOTH set in memory AND expected by the slot — exactly the
--   apworld's `if location_id in ctx.server_locations`.
--
--     1. BASE flags (event/item/trainer/… checks). 0x120 contiguous bytes at
--        SaveBlock1+0x1130 (client.py reads it as 2× 0x90 chunks at +0x1130 and
--        +0x11C0). location_id = byte_index*8 + bit  (LSB-first).
--          (1085 base checks, ids 340..2022.)
--     2. DEXSANITY. SaveBlock1+0x0848, 0x34 bytes (caught-flags).
--        dex_number = byte*8 + bit + 1 ; location_id = 0x5000 + dex_number - 1.
--          (386 checks, ids 0x5000..0x5181.)
--     3. SHOPSANITY (+ vending machines + prizes). SaveBlock2+0xB24, 0x30 bytes.
--        location_id = 0x5200 + byte*8 + bit.
--          (166 checks, ids 0x5200..0x5374.)
--     4. FAMESANITY (Fame Checker). SaveBlock1+0x3B14, 0x40 bytes. The flags sit
--        every 4 bytes (byte_i % 4 == 0) and only bits 2..7 count; a running
--        fame_checker_index advances by 1 per candidate bit.
--        location_id = 0x6000 + fame_checker_index.
--          (96 checks, ids 0x6000..0x605F.)
--
-- GOAL (client.py: self.goal_flag in the BASE flag array)
-- ───────────────────────────────────────────────────────
--   slot_data.goal 0 = champion        → FLAG_DEFEATED_CHAMP         (1212)
--                  1 = champion_rematch → FLAG_DEFEATED_CHAMP_REMATCH (1213)
--   Both live in the BASE flag block, so is_goal_complete() reads the same region.
--
-- RECEIVE ITEM
-- ─────────────
--   items_handling for FRLG is 0b011 if remote_items else 0b001 (client.py
--   validate_rom). The DEFAULT (no remote_items) is 0b001 — the PATCHED GAME
--   grants its own locally-found items, so a solo seed plays fully and every
--   check is reported without this module writing anything. Delivering items is
--   the client's gArchipelagoReceivedItem buffer handshake (the SAME 4-write
--   protocol Pokémon Emerald uses: u16 item, u16 next-index, u8 is_filled,
--   u8 should_display, gated on the save's processed-count). That write path is
--   byte-identical to Emerald's and could be ported, but it needs in-emulator
--   confirmation on a real FRLG save before shipping a memory WRITE — so it is
--   intentionally a documented NO-OP here (never a wrong write) rather than
--   shipped unverified. See receive_item() at the bottom. Check + goal detection
--   are fully live regardless.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "pokemon_frlg"

-- Address map + flag-scan logic generated/derived from worlds/pokemon_frlg source
-- (see file header). The runtime ROM-identity gate keeps all of it from ever
-- running against a ROM revision these addresses do not belong to.
local ADDRESSES_VERIFIED = true

local SYSTEM_BUS = "System Bus"
local ROM_DOMAIN = "ROM"

-- ── RAM addresses (extracted_data.json misc_ram_addresses; identical FR/LG/rev1) ─
local SB1_PTR   = 0x03004F58     -- gSaveBlock1Ptr
local SB2_PTR   = 0x03004F5C     -- gSaveBlock2Ptr
local GMAIN     = 0x03003040     -- gMain
local OVERWORLD = GMAIN + 0x038  -- client.py guards["IN OVERWORLD"]: == 1
local RECV      = 0x0203F714     -- gArchipelagoReceivedItem (buffer, EWRAM)

-- ── SaveBlock-relative flag windows (client.py game_watcher) ──────────────────
local FLAGS_OFF      = 0x1130    -- BASE flags; 0x120 contiguous bytes (2×0x90)
local FLAGS_BYTES    = 0x120     -- 288 bytes -> 2304 flags
local DEX_OFF        = 0x0848    -- SaveBlock1: dexsanity caught-flags
local DEX_BYTES      = 0x34
local SHOP_OFF       = 0x0B24    -- SaveBlock2: shop flags
local SHOP_BYTES     = 0x30
local FAME_OFF       = 0x3B14    -- SaveBlock1: fame checker flags
local FAME_BYTES     = 0x40

-- ── AP location-id offsets (client.py top-of-file) ────────────────────────────
local DEXSANITY_OFFSET  = 0x5000
local SHOPSANITY_OFFSET = 0x5200
local FAMESANITY_OFFSET = 0x6000

-- ── Goal flags (BASE flag-array bit indices; data.constants) ──────────────────
-- slot_data.goal 0=champion, 1=champion_rematch.
local GOAL_FLAGS = {
  [0] = 1212,   -- FLAG_DEFEATED_CHAMP
  [1] = 1213,   -- FLAG_DEFEATED_CHAMP_REMATCH
}

-- ── ROM identity (client.py validate_rom) ─────────────────────────────────────
-- The 32-byte name at ROM 0x108 is the vanilla game name for the base ROM, with
-- the AP patcher appending " AP" (extracted_data.json rom_names →
-- "pokemon red version AP" / "pokemon green version AP"). client.py accepts the
-- ROM when the name STARTS WITH one of the vanilla prefixes AND is NOT exactly
-- the vanilla name (i.e. it has been patched). We mirror that here.
local ROM_PREFIX_FR  = "pokemon red version"     -- FireRed  (BASE_ROM_NAME.firered)
local ROM_PREFIX_LG  = "pokemon green version"   -- LeafGreen (BASE_ROM_NAME.leafgreen)

local POLL_INTERVAL = 0.1        -- client.py watcher_timeout = 0.125 s

-- ── Internal state ────────────────────────────────────────────────────────────
local log_fn          = nil
local rom_ok          = false
local why_disabled    = "init() not run"
local goal_flag       = nil
local server_locations = nil     -- set of AP ids the slot expects (nil = all)
local have_locations  = false
local reported        = {}       -- AP location id -> true (already returned)
local goal_reached    = false
local last_poll       = 0

local POW2 = { [0]=1, [1]=2, [2]=4, [3]=8, [4]=16, [5]=32, [6]=64, [7]=128, [8]=256 }

local function log(msg)
  if log_fn then pcall(log_fn, "[pokemon_frlg] " .. tostring(msg)) end
end

-- ── Memory helpers (defensive: never nil-crash on any BizHawk version) ────────
local mem = {}

local function resolve_memory_api()
  if not memory then return false end
  mem.read_u8    = memory.read_u8       or memory.readbyte
  mem.read_u16   = memory.read_u16_le   or memory.readword
  mem.read_u32   = memory.read_u32_le   or memory.readdword
  mem.read_range = memory.read_bytes_as_array or memory.readbyterange
  return mem.read_u8 ~= nil and mem.read_u32 ~= nil
end

local function rd(fn, addr, domain)
  if not fn then return nil end
  local ok, v = pcall(fn, addr, domain or SYSTEM_BUS)
  if ok and type(v) == "number" then return v end
  ok, v = pcall(fn, addr)                         -- older API: current domain
  if ok and type(v) == "number" then return v end
  return nil
end

local function read_u8(addr, domain)  return rd(mem.read_u8,  addr, domain) end
local function read_u32(addr, domain) return rd(mem.read_u32, addr, domain) end

-- Read `len` bytes; returns a 1-based array or nil (bulk API first, per-byte
-- fallback). read_bytes_as_array is 1-based; legacy readbyterange is 0-based —
-- both normalized to 1-based here.
local function read_range(addr, len, domain)
  if mem.read_range then
    local ok, t = pcall(mem.read_range, addr, len, domain or SYSTEM_BUS)
    if ok and type(t) == "table" then
      if t[0] ~= nil then
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

-- FRLG SaveBlock1/2 live in EWRAM (0x02000000..0x0203FFFF).
local function valid_save_ptr(p)
  return p and p >= 0x02000000 and p <= 0x0203FFFF
end

-- ── ROM identity (client.py validate_rom, name at ROM 0x108) ──────────────────
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

local function starts_with(s, prefix)
  return s:sub(1, #prefix) == prefix
end

-- AP-patched FR or LG ROM: name starts with a base prefix AND is not EXACTLY the
-- vanilla base name (client.py rejects the unpatched dump). The AP patcher
-- appends " AP", so a patched ROM always differs from the bare prefix.
local function rom_is_ap()
  local name = read_rom_name()
  if not name then return false, "ROM name not readable yet" end
  local is_fr = starts_with(name, ROM_PREFIX_FR)
  local is_lg = starts_with(name, ROM_PREFIX_LG)
  if not is_fr and not is_lg then
    return false, string.format(
      "ROM name '%s' is not a Pokémon FireRed/LeafGreen ROM — module disabled.", name)
  end
  if name == ROM_PREFIX_FR or name == ROM_PREFIX_LG then
    return false, string.format(
      "ROM is the UNPATCHED vanilla '%s' — apply the seed's %s patch. " ..
      "Checks and goal detection are DISABLED.", name,
      is_fr and ".apfirered" or ".apleafgreen")
  end
  return true, name
end

-- ── Config / location set ─────────────────────────────────────────────────────
local function load_locations(ids)
  if type(ids) ~= "table" then
    have_locations = false
    return
  end
  server_locations = {}
  local n = 0
  for _, raw in ipairs(ids) do
    local id = tonumber(raw)
    if id then server_locations[id] = true; n = n + 1 end
  end
  have_locations = n > 0
  log("server location set: " .. n .. " ids")
end

local function wanted(id)
  if server_locations == nil then return true end
  return server_locations[id] == true
end

local function emit(out, id)
  if wanted(id) and not reported[id] then
    reported[id] = true
    out[#out + 1] = id
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
  local ok, info = rom_is_ap()
  if not ok then
    rom_ok = false
    why_disabled = info
    log(why_disabled)
    -- Not fatal: the ROM may not be readable on the very first poll. poll() and
    -- is_goal_complete() re-check, so a late-loading ROM still comes online.
  else
    rom_ok = true
    log("AP ROM verified: '" .. tostring(info) .. "'")
  end

  -- Multiworld context from ap_config.json (written by the launcher).
  local cfg = (ctx and ctx.config) or {}
  local sd = cfg.slot_data
  local goal = 0
  if type(sd) == "table" then
    goal = tonumber(sd.goal) or 0
  else
    log("no slot_data in ap_config.json — assuming goal=champion")
  end
  goal_flag = GOAL_FLAGS[goal] or GOAL_FLAGS[0]

  load_locations(cfg.locations)
  if not have_locations then
    log("no server location ids in ap_config.json — check detection idle " ..
        "(connect to AP before launching)")
  end

  log(string.format("ready: goal=%d (flag %s)", goal, tostring(goal_flag)))
end

-- Re-verify the ROM lazily (it may not have been readable at init time).
local function ensure_rom()
  if rom_ok then return true end
  local ok, info = rom_is_ap()
  if ok then
    rom_ok = true
    log("AP ROM verified: '" .. tostring(info) .. "'")
  end
  return rom_ok
end

function M.poll()
  local out = {}
  if not ADDRESSES_VERIFIED then return out end
  if not ensure_rom() then return out end

  -- Mirror the client's ~0.125 s watcher cadence. os.clock() is the standard
  -- Lua CRT clock under BizHawk; guard for environments where it is unavailable
  -- (then we simply don't throttle — correctness is unaffected).
  local now = os.clock and os.clock() or nil
  if type(now) == "number" then
    if now - last_poll < POLL_INTERVAL then return out end
    last_poll = now
  end

  -- IN OVERWORLD guard (gMain+0x38 == 1) — exactly the apworld client's guard.
  if read_u8(OVERWORLD) ~= 1 then return out end

  local sb1 = read_u32(SB1_PTR)
  local sb2 = read_u32(SB2_PTR)
  if not valid_save_ptr(sb1) or not valid_save_ptr(sb2) then return out end

  -- BASE flag block: 0x120 contiguous bytes at sb1+0x1130.
  local flags = read_range(sb1 + FLAGS_OFF, FLAGS_BYTES)

  -- Dexsanity (sb1+0x0848) — the client reads these unconditionally; the
  -- server-location filter decides whether any are actually wanted.
  local dex = read_range(sb1 + DEX_OFF, DEX_BYTES)
  -- Famesanity (sb1+0x3B14).
  local fame = read_range(sb1 + FAME_OFF, FAME_BYTES)
  -- Shopsanity (sb2+0xB24).
  local shop = read_range(sb2 + SHOP_OFF, SHOP_BYTES)

  -- DMA-shuffle / overworld guard: discard the whole sample if either save
  -- pointer moved or we left the overworld while reading (guarded_read stand-in).
  if read_u32(SB1_PTR) ~= sb1 or read_u32(SB2_PTR) ~= sb2
     or read_u8(OVERWORLD) ~= 1 then
    return out
  end

  -- 1. BASE flags: location_id = byte*8 + bit (LSB-first).
  if flags then
    for i = 1, FLAGS_BYTES do
      local byte = flags[i] or 0
      if byte ~= 0 then
        for bit = 0, 7 do
          if byte % POW2[bit + 1] >= POW2[bit] then
            local id = (i - 1) * 8 + bit
            emit(out, id)
            if goal_flag and id == goal_flag and not goal_reached then
              goal_reached = true
              log("goal flag " .. goal_flag .. " set — goal complete")
            end
          end
        end
      end
    end
  end

  -- 2. Dexsanity: dex_number = byte*8 + bit + 1; id = 0x5000 + dex_number - 1.
  if dex then
    for i = 1, DEX_BYTES do
      local byte = dex[i] or 0
      if byte ~= 0 then
        for bit = 0, 7 do
          if byte % POW2[bit + 1] >= POW2[bit] then
            local dex_number = (i - 1) * 8 + bit + 1
            emit(out, DEXSANITY_OFFSET + dex_number - 1)
          end
        end
      end
    end
  end

  -- 3. Shopsanity: id = 0x5200 + byte*8 + bit.
  if shop then
    for i = 1, SHOP_BYTES do
      local byte = shop[i] or 0
      if byte ~= 0 then
        for bit = 0, 7 do
          if byte % POW2[bit + 1] >= POW2[bit] then
            emit(out, SHOPSANITY_OFFSET + (i - 1) * 8 + bit)
          end
        end
      end
    end
  end

  -- 4. Famesanity: flags every 4 bytes (byte_i % 4 == 0), bits 2..7 only; a
  --    running fame_checker_index advances by 1 per candidate bit.
  --    id = 0x6000 + fame_checker_index. (Faithful to client.py.)
  if fame then
    local fame_index = 0
    for i = 1, FAME_BYTES do
      local byte_i = i - 1
      if byte_i % 4 == 0 then
        local byte = fame[i] or 0
        for bit = 2, 7 do
          if byte % POW2[bit + 1] >= POW2[bit] then
            emit(out, FAMESANITY_OFFSET + fame_index)
          end
          fame_index = fame_index + 1
        end
      end
    end
  end

  return out
end

function M.is_goal_complete()
  if goal_reached then return true end
  if not ADDRESSES_VERIFIED or not ensure_rom() or not goal_flag then return false end

  -- Independent read so a goal can be confirmed even between poll() cadence ticks.
  if read_u8(OVERWORLD) ~= 1 then return false end
  local sb1 = read_u32(SB1_PTR)
  if not valid_save_ptr(sb1) then return false end

  local byte_index = math.floor(goal_flag / 8)
  local bit = goal_flag % 8
  local byte = read_u8(sb1 + FLAGS_OFF + byte_index)
  if read_u32(SB1_PTR) ~= sb1 or read_u8(OVERWORLD) ~= 1 then return false end
  if byte and byte % POW2[bit + 1] >= POW2[bit] then
    goal_reached = true
    return true
  end
  return false
end

-- Remote multiworld items: see the file header. items_handling defaults to 0b001
-- (the patched game grants its own found items), so solo play and check/goal
-- reporting work fully. Applying REMOTE items is the gArchipelagoReceivedItem
-- buffer handshake (byte-identical to Pokémon Emerald's 4-write protocol);
-- it is deferred until confirmed on a real FRLG save in-emulator. No-op (never a
-- wrong write) until then.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
