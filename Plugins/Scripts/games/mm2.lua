-- ═══════════════════════════════════════════════════════════════════════════════
-- mm2.lua — game module for the Archipelago BizHawk connector.
--           Mega Man 2 (NES, USA)
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the official
-- AP world worlds/mm2 (client.py + locations.py, main branch). The 71-entry
-- location set is GENERATED directly from the client's own check loops + the
-- MM2_CONSUMABLE_TABLE (parsed from client.py, not hand-copied) and cross-checked
-- 1:1 against locations.py's declared numeric ids. The flag/byte math is replicated
-- EXACTLY from worlds/mm2/client.py game_watcher(). Mock-verified through MoonSharp
-- against synthetic NES memory. Loads crash-free on any ROM; self-disables on a
-- non-MM2 cartridge (no "MM2" PRG-ROM signature).
--
-- MEMORY MODEL (BizHawk NES domains — matches client.py MegaMan2Client)
-- ───────────────────────────────────────────────────────────────────────
--   The MM2 AP client is a BizHawkClient that reads two NES domains:
--     "RAM"      — the NES 2 KB internal work RAM (0x000..0x7FF). EVERY location
--                  flag, the goal byte, and the difficulty gate live here. All
--                  addresses the client touches top out at 0x7B3, well inside 2 KB.
--     "PRG ROM"  — the cartridge program ROM. Carries the "MM2" identifier the
--                  client's validate_rom() checks at offset 0x3FFB0.
--
--   The AP location id IS a fixed value per check (the 0x88xxxx ids from
--   locations.py). client.py game_watcher() detects them four ways:
--
--     1. ROBOT MASTER WEAPONS — robot_masters_defeated (RAM 0x8B), bit i (i 0..7):
--          robot_masters_defeated & (1<<i) != 0  →  id 0x880101 + i
--     2. ITEMS ACQUIRED — items_acquired (RAM 0x8C), bit i (i 0..2):
--          items_acquired & (1<<i) != 0           →  id 0x880111 + i
--     3. COMPLETED STAGES — completed_stages[i] (RAM 0x770 + i, i 0..0xC), a
--        BYTE-valued flag (not a bitfield):
--          completed_stages[i] != 0               →  id 0x880001 + i
--     4. CONSUMABLES — MM2_CONSUMABLE_TABLE, 47 entries each (byte offset, bit
--        mask) into the 52-byte consumables block at RAM 0x780:
--          consumables[0x780 + off] & mask != 0   →  id == table key
--
--   GOAL: client.py sends CLIENT_GOAL when completed_stages[0xD] != 0 — i.e.
--         RAM 0x77D (the 14th byte of the 0xE-byte completed-stages read). "this
--         sets on credits fade, no real better way to do this" (client comment).
--
--   GATE: difficulty (RAM 0xCB) must be 0 or 1; client returns early otherwise
--         ("Game is not initialized"). We mirror that so a fresh/booting cart's
--         zeroed RAM never reports phantom checks.
--
-- WHAT THIS DOES (mirrors worlds/mm2/client.py game_watcher → location loops)
--   • poll(): read the relevant RAM once, decode every wanted id with the exact
--     math above → AP ids. Gated to the slot's server location set and to the
--     difficulty gate.
--   • is_goal_complete(): RAM 0x77D != 0 — the only MM2 goal (Wily defeated /
--     credits fade).
--   • receive_item(): NO-OP (documented). items_handling = 0b111 (client.py sets
--     ctx.items_handling = 0b111) means the AP SERVER drives ALL item delivery;
--     the reference client writes received items into RAM (weapons_unlocked /
--     items_unlocked / lives / E-tanks / weapon+health energy queues) gated on the
--     game's own received-items counter at RAM 0x8E, with stage-access masks, SFX
--     strobes and an EnergyLink path. That guarded multi-write is the piece that
--     must be confirmed in-emulator before it is wired here (a wrong RAM write
--     mid-stage corrupts the run / desyncs the counter), so it is intentionally
--     deferred rather than shipped unverified. Item delivery is handled
--     launcher-side by the connector's SYNC channel when that path is enabled.
--     Checks + goal flow regardless.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "mm2"

local ADDRESSES_VERIFIED = true   -- id set generated from worlds/mm2 source

-- ── Memory domains (BizHawk NES) ──────────────────────────────────────────────
local RAM    = "RAM"        -- NES 2 KB internal work RAM (client self reads "RAM")
local PRGROM = "PRG ROM"    -- cartridge program ROM — "MM2" identifier

-- ── Addresses / constants (worlds/mm2/client.py) ──────────────────────────────
local ROM_NAME_LOCATION   = 0x3FFB0   -- PRG ROM: 21-byte name field; first 3 = "MM2"
local ROM_NAME            = "MM2"
local ROBOT_MASTERS_DEFEATED = 0x8B   -- RAM: 8 boss-weapon flag bits
local ITEMS_ACQUIRED      = 0x8C      -- RAM: 3 item-get flag bits
local DIFFICULTY          = 0xCB      -- RAM: difficulty (gate: must be 0 or 1)
local COMPLETED_STAGES    = 0x770     -- RAM: 0xE bytes, one per stage (byte != 0)
local CONSUMABLES_BASE    = 0x780     -- RAM: 52-byte consumables flag block
local GOAL_STAGE_INDEX    = 0xD       -- completed_stages[0xD] != 0 → game complete

local WEAPON_ID_BASE      = 0x880101  -- robot-master weapon ids: base + bit index
local ITEM_ID_BASE        = 0x880111  -- item-acquired ids: base + bit index
local STAGE_ID_BASE       = 0x880001  -- completed-stage ids: base + stage index
local STAGE_CHECK_COUNT   = 0xD       -- client loops range(0xD) = 13 stage checks

-- ── Consumable check table (GENERATED from client.py MM2_CONSUMABLE_TABLE) ─────
-- {ap_id, byte offset into the 0x780 block, bit mask}. 47 entries. Checked when
-- RAM[0x780 + offset] & mask ~= 0. Order preserved from source (irrelevant to
-- correctness — each entry is self-describing).
local CONSUMABLES = {
  {0x880201,0,8},{0x880202,16,1},{0x880203,16,2},{0x880204,16,4},
  {0x880205,16,8},{0x880206,16,16},{0x880207,16,32},{0x880208,16,64},
  {0x880209,16,128},{0x88020A,20,1},{0x88020B,20,4},{0x88020C,20,64},
  {0x88020D,21,1},{0x88020E,21,2},{0x88020F,21,4},{0x880210,24,1},
  {0x880211,24,2},{0x880212,24,4},{0x880213,28,1},{0x880214,28,2},
  {0x880215,28,4},{0x880216,33,4},{0x880217,33,8},{0x880218,37,8},
  {0x880219,37,16},{0x88021A,38,1},{0x88021B,38,2},{0x880227,38,4},
  {0x880228,38,32},{0x880229,38,128},{0x88022A,39,4},{0x88022B,39,2},
  {0x88022C,39,1},{0x88022D,38,64},{0x88022E,38,16},{0x88022F,38,8},
  {0x88021C,39,32},{0x88021D,39,64},{0x88021E,39,128},{0x88021F,41,16},
  {0x880220,42,2},{0x880221,42,4},{0x880222,42,8},{0x880223,46,1},
  {0x880224,46,2},{0x880225,46,4},{0x880226,46,8},
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil     -- cached MM2 identifier result
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[mm2] " .. tostring(msg)) end
end

-- ── Memory API (resolved at init; 2-arg domain form + current-domain fallback) ─
local function resolve_memory_api()
  if not memory then return false end
  mem.read_u8 = memory.read_u8 or memory.readbyte
  return mem.read_u8 ~= nil
end

local function read_u8(addr, domain)
  if not mem.read_u8 then return nil end
  local ok, v = pcall(mem.read_u8, addr, domain)
  if ok and type(v) == "number" then return v end
  ok, v = pcall(mem.read_u8, addr)            -- older API: current domain
  if ok and type(v) == "number" then return v end
  return nil
end

local function bit_and(a, b)
  local res, bitval = 0, 1
  while a > 0 and b > 0 do
    if a % 2 == 1 and b % 2 == 1 then res = res + bitval end
    a = math.floor(a / 2); b = math.floor(b / 2); bitval = bitval * 2
  end
  return res
end

-- bit i (0..7) as a power-of-two mask, matching the client's (1 << i).
local POW2 = { [0]=1, 2, 4, 8, 16, 32, 64, 128 }

-- ── ROM identity: the MM2 AP ROM carries "MM2" at PRG ROM 0x3FFB0 ─────────────
-- The client's validate_rom() checks game_name[:3] == b"MM2", reading 21 bytes at
-- PRG ROM 0x3FFB0. rom.py writes that 21-byte name field at headered offset
-- 0x3FFC0; BizHawk's "PRG ROM" domain is HEADERLESS (excludes the 16-byte iNES
-- header), so 0x3FFC0 - 0x10 = 0x3FFB0 — the read base coincides with the name
-- start, and "MM2" lands at game_name[:3]. (Verified against rom.py's deathlink
-- 0x3FFD5 and version 0x3FFD8 writes, which the client likewise reads at 0x3FFC5 /
-- 0x3FFC8 = those headered offsets minus 0x10.) The trailing bytes encode the
-- apworld version, which varies per release, so we match ONLY the version-
-- independent "MM2" prefix — the name-independent detector for any MM2 AP seed.
local function rom_is_mm2()
  if rom_ok ~= nil then return rom_ok end
  for i = 1, #ROM_NAME do
    local b = read_u8(ROM_NAME_LOCATION + i - 1, PRGROM)
    if b == nil then return false end          -- not readable yet; retry next poll
    if b ~= string.byte(ROM_NAME, i) then
      rom_ok = false
      log("non-MM2 ROM (no 'MM2' PRG-ROM signature) — detection idle")
      return false
    end
  end
  rom_ok = true
  log("MM2 ROM verified ('MM2' signature present)")
  return true
end

-- ── Multiworld context ────────────────────────────────────────────────────────
local function load_locations(ids)
  if type(ids) ~= "table" then return end
  server_locations = {}
  local n = 0
  for _, id in ipairs(ids) do
    local v = tonumber(id)
    if v then server_locations[v] = true; n = n + 1 end
  end
  log("server location set: " .. n .. " ids")
end

local function wanted(ap_id)
  if server_locations == nil then return true end
  return server_locations[ap_id] == true
end

-- ── Detection gate ────────────────────────────────────────────────────────────
-- difficulty (RAM 0xCB) is 0 (Normal) or 1 (Difficult) once the game is running;
-- the client returns early on any other value ("Game is not initialized"). Mirror
-- that so booting/menu RAM can never report phantom checks.
local function in_game()
  local d = read_u8(DIFFICULTY, RAM)
  return d ~= nil and (d == 0 or d == 1)
end

-- Helper: record a new (wanted, not-yet-reported) check.
local function emit(new, ap_id)
  if not reported[ap_id] and wanted(ap_id) then
    reported[ap_id] = true
    new[#new + 1] = ap_id
  end
end

-- ── Module contract ───────────────────────────────────────────────────────────
function M.init(ctx)
  if ctx and type(ctx.log) == "function" then log_fn = ctx.log end
  if not resolve_memory_api() then
    log("BizHawk memory API unavailable — module idle")
    ADDRESSES_VERIFIED = false
    return
  end
  local cfg = (ctx and ctx.config) or {}
  load_locations(cfg.locations)
  log("ready: " .. (8 + 3 + STAGE_CHECK_COUNT + #CONSUMABLES) .. " location ids")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_mm2() then return new end
  if not in_game() then return new end

  -- 1. Robot Master weapons — robot_masters_defeated bit i → 0x880101 + i.
  local rbm = read_u8(ROBOT_MASTERS_DEFEATED, RAM)
  if rbm then
    for i = 0, 7 do
      if bit_and(rbm, POW2[i]) ~= 0 then emit(new, WEAPON_ID_BASE + i) end
    end
  end

  -- 2. Items acquired — items_acquired bit i → 0x880111 + i.
  local itm = read_u8(ITEMS_ACQUIRED, RAM)
  if itm then
    for i = 0, 2 do
      if bit_and(itm, POW2[i]) ~= 0 then emit(new, ITEM_ID_BASE + i) end
    end
  end

  -- 3. Completed stages — completed_stages[i] (byte) != 0 → 0x880001 + i.
  for i = 0, STAGE_CHECK_COUNT - 1 do
    local b = read_u8(COMPLETED_STAGES + i, RAM)
    if b ~= nil and b ~= 0 then emit(new, STAGE_ID_BASE + i) end
  end

  -- 4. Consumables — consumables[0x780 + off] & mask != 0 → table key.
  for _, c in ipairs(CONSUMABLES) do
    local ap_id, off, mask = c[1], c[2], c[3]
    if not reported[ap_id] and wanted(ap_id) then
      local b = read_u8(CONSUMABLES_BASE + off, RAM)
      if b ~= nil and bit_and(b, mask) ~= 0 then
        reported[ap_id] = true
        new[#new + 1] = ap_id
      end
    end
  end

  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_mm2() then return false end
  if not in_game() then return false end
  -- completed_stages[0xD] != 0 (RAM 0x77D) — Wily defeated / credits fade.
  local b = read_u8(COMPLETED_STAGES + GOAL_STAGE_INDEX, RAM)
  return b ~= nil and b ~= 0
end

-- Remote items: see the file header. items_handling = 0b111 — the AP server drives
-- ALL item delivery (the reference client writes them into RAM gated on the game's
-- own received-items counter at RAM 0x8E, with weapon/item unlock masks, stage
-- access, E-tank/life/energy queues, SFX strobes and EnergyLink). That guarded
-- multi-write path is deferred here until it can be confirmed in-emulator; a wrong
-- RAM write would corrupt the run / desync the counter, so this is a no-op (never
-- a wrong write) rather than shipped unverified.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
