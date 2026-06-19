-- ═══════════════════════════════════════════════════════════════════════════════
-- zelda2.lua — game module for the Archipelago BizHawk connector.
--              Zelda II: The Adventure of Link (NES, USA)
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the official
-- AP world worlds/zelda2 (game string "Zelda II: The Adventure of Link", author
-- "Pink Switch"), as carried in the MultiworldGG fork of Archipelago
-- (github.com/MultiworldGG/MultiworldGG, worlds/zelda2/Client.py + game_data.py,
-- main branch). The 106-entry location set (95 flag-array + 11 NPC) was GENERATED
-- directly from worlds/zelda2/game_data.py — location_table + special_locations —
-- by PARSING the source (the AP location id IS the raw numeric key; there is NO
-- base_id offset, exactly as Client.py iterates them), and the byte/bit math, the
-- "LEGEND OF ZELDA2" PRG-ROM signature, the game_state gate and the goal trigger
-- are replicated EXACTLY from worlds/zelda2/Client.py game_watcher(). Mock-verified
-- through MoonSharp against synthetic NES memory. Loads crash-free on any ROM;
-- self-disables on a non-Zelda II cartridge (no "LEGEND OF ZELDA2" PRG-ROM
-- signature). ap_ids 0x01..0x6B.
--
-- MEMORY MODEL (BizHawk NES domains — matches Client.py Zelda2Client)
-- ───────────────────────────────────────────────────────────────────────
--   The Zelda II AP client is a BizHawkClient that reads three NES domains:
--     "RAM"      — the NES 2 KB internal work RAM. The location flag array, the
--                  game-state gate and the goal byte live here.
--     "WRAM"     — the cartridge battery-backed save RAM. The NPC-check field and
--                  the server item handshake live here.
--     "PRG ROM"  — cartridge program ROM (client validate_rom). Carries the
--                  "LEGEND OF ZELDA2" identifier the client checks at 0x1FFE0.
--
--   The AP location id IS the raw numeric key of game_data.location_table /
--   special_locations (0x01..0x6B). Client.py game_watcher() detects two ways:
--
--     1. FLAG-ARRAY locations (location_table, 95 entries) — read 0xDF bytes at
--        RAM 0x0600 (loc_array). For each id, location_table[id] = {offset, bit}.
--        ── INVERTED ── the location is CHECKED when the bit is CLEAR:
--             loc_array[offset] & (1 << bit) == 0   →  id is checked
--        (the game starts these bits SET and CLEARS one as Link takes the item;
--        Client.py: `if not location & bitmask: new_checks.append(id)`).
--     2. NPC locations (special_locations, 11 entries) — read 2 bytes at WRAM
--        0x1A18 (npc_check_field). For each id, special_locations[id] = {offset,
--        bit}, offset ∈ {0,1}. ── NORMAL ── checked when the bit is SET:
--             npc_check_field[offset] & (1 << bit) != 0   →  id is checked
--        (Client.py: `if location & bitmask: new_checks.append(id)`).
--
--   GOAL: Client.py sends CLIENT_GOAL when goal_trigger (RAM 0x076C) == 0x04 —
--         the Thunderbird-defeated / Triforce-of-Courage win state.
--
--   GATE: game_state (RAM 0x0736) must be 0x0B or 0x05 ("side-scroll mode");
--         Client.py returns early (`if game_state not in [0x0B, 0x05]: return`)
--         on any other value. This gate is LOAD-BEARING for correctness here: the
--         flag-array check is INVERTED, so a booting / title-screen / menu frame
--         with a zeroed-or-not-yet-populated 0x0600 block would read every bit as
--         CLEAR and falsely report all 95 flag-array locations at once. Mirroring
--         the exact game_state gate is what prevents those phantom checks — the
--         block is only valid once the game is in active side-scroll/overworld
--         play. (The NPC family is bit-SET and would not phantom on zeroed RAM,
--         but is gated identically for fidelity.)
--
-- WHAT THIS DOES (mirrors worlds/zelda2/Client.py game_watcher → location loop)
--   • poll(): read RAM 0x0600..0x06DF (flag array) + WRAM 0x1A18..0x1A19 (NPC
--     field) once, decode every wanted id with the exact INVERTED / NORMAL bit
--     math above → AP ids. Gated to the slot's server location set and to the
--     game_state gate.
--   • is_goal_complete(): RAM 0x076C == 0x04 — the only Zelda II goal
--     (Thunderbird defeated / Triforce of Courage).
--   • receive_item(): NO-OP (documented). items_handling = 0b111 (Client.py sets
--     ctx.items_handling = 0b111) means the AP SERVER drives ALL item delivery;
--     the reference client writes the next received item's id into WRAM 0x1A10 and
--     bumps the obtained-count at WRAM 0x1A1C, but ONLY while the game's own
--     "currently obtained item" mailbox at WRAM 0x1A10 reads 0x00 (a handshake the
--     game clears once it has consumed the item). That guarded WRAM write is the
--     piece that must be confirmed in-emulator before it is wired here (a wrong
--     WRAM write mid-frame corrupts the save / desyncs the count), so it is
--     intentionally deferred rather than shipped unverified. Item delivery is
--     handled launcher-side by the connector's SYNC channel when that path is
--     enabled. Checks + goal flow regardless.
--
--   NOTE — the client also WRITES loc_array back to RAM 0x0600 each frame (its
--   "collect" bookkeeping). This module never writes game memory: a check is a
--   one-way report, and writing flags back is exactly the kind of guarded
--   mutation deferred above. Detection does not depend on the write-back.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "zelda2"

local ADDRESSES_VERIFIED = true   -- tables generated from worlds/zelda2 source

-- ── Memory domains (BizHawk NES) ──────────────────────────────────────────────
local RAM    = "RAM"        -- NES 2 KB internal work RAM (flag array, gate, goal)
local WRAM   = "WRAM"       -- cartridge save RAM (NPC check field, item handshake)
local PRGROM = "PRG ROM"    -- cartridge program ROM — "LEGEND OF ZELDA2" identifier

-- ── Addresses / constants (worlds/zelda2/Client.py) ───────────────────────────
local ROM_NAME_LOCATION = 0x1FFE0   -- PRG ROM: 16-byte name field
local ROM_NAME          = "LEGEND OF ZELDA2"
local LOC_ARRAY_BASE    = 0x0600    -- RAM: location flag array
local LOC_ARRAY_LENGTH  = 0xDF      -- RAM: bytes read (Client.py reads 0xDF)
local NPC_FIELD_BASE    = 0x1A18    -- WRAM: NPC-check field (2 bytes)
local NPC_FIELD_LENGTH  = 2
local GAME_STATE        = 0x0736    -- RAM: game state (gate: 0x0B or 0x05)
local GAME_STATE_PLAY_A = 0x0B
local GAME_STATE_PLAY_B = 0x05
local GOAL_TRIGGER      = 0x076C    -- RAM: goal byte (== 0x04 => game complete)
local GOAL_VALUE        = 0x04

-- ── Location tables (GENERATED from worlds/zelda2/game_data.py) ───────────────
-- ap_id = raw numeric key. Each entry is {byte offset, bit index}.

-- Flag-array locations (location_table, 95) — INVERTED: checked when the bit is
-- CLEAR in loc_array[offset]  (loc_array[offset] & (1<<bit) == 0).
local LOC_TABLE = {
  [0x01]={0x10,3},[0x02]={0x15,1},[0x03]={0x16,0},[0x06]={0x13,5},
  [0x07]={0x81,3},[0x08]={0x84,7},[0x09]={0x84,2},[0x0A]={0x80,0},
  [0x0B]={0x81,4},[0x0C]={0x86,2},[0x0D]={0x07,0},[0x0E]={0x19,2},
  [0x10]={0x1C,0},[0x11]={0x19,1},[0x12]={0x06,2},[0x13]={0x08,2},
  [0x15]={0x17,5},[0x16]={0x8B,3},[0x17]={0x88,1},[0x18]={0x8F,4},
  [0x19]={0x8A,2},[0x1A]={0x8A,7},[0x1B]={0x8E,4},[0x1C]={0x90,7},
  [0x1D]={0x91,6},[0x1E]={0x24,2},[0x1F]={0x26,3},[0x20]={0x2D,6},
  [0x21]={0x2A,0},[0x22]={0x29,5},[0x23]={0x1C,5},[0x24]={0x1C,1},
  [0x27]={0xA2,6},[0x28]={0xA2,5},[0x29]={0xA3,5},[0x2A]={0xA3,2},
  [0x2B]={0xA5,6},[0x2C]={0xA5,0},[0x2E]={0xA6,5},[0x2F]={0xA6,0},
  [0x30]={0xA7,6},[0x31]={0x53,5},[0x33]={0x45,4},[0x34]={0x50,1},
  [0x35]={0x5C,0},[0x36]={0x43,2},[0x39]={0x5C,1},[0x3A]={0x32,6},
  [0x3B]={0x39,5},[0x3C]={0x32,0},[0x3D]={0xA9,2},[0x3E]={0xA8,0},
  [0x3F]={0xA8,6},[0x40]={0xA8,5},[0x41]={0xAB,4},[0x42]={0xAC,0},
  [0x43]={0xB1,0},[0x44]={0xB0,6},[0x45]={0xAD,5},[0x46]={0xAF,0},
  [0x47]={0xAE,6},[0x48]={0x92,1},[0x49]={0x93,5},[0x4A]={0x95,1},
  [0x4B]={0x97,1},[0x4C]={0x9B,7},[0x4D]={0x9B,4},[0x4E]={0x9E,3},
  [0x4F]={0x99,4},[0x50]={0x9A,7},[0x51]={0x9A,2},[0x52]={0x97,7},
  [0x53]={0x94,2},[0x54]={0x59,1},[0x55]={0x46,4},[0x56]={0x57,6},
  [0x57]={0x56,5},[0x58]={0x5F,5},[0x59]={0x5F,6},[0x5C]={0x77,2},
  [0x5D]={0x77,6},[0x5E]={0xB2,1},[0x5F]={0xB3,5},[0x60]={0xB6,3},
  [0x61]={0xBB,3},[0x62]={0xB5,6},[0x63]={0xB7,0},[0x64]={0xB6,6},
  [0x65]={0xB6,4},[0x66]={0xBA,2},[0x67]={0xBD,1},[0x68]={0xBB,4},
  [0x69]={0xBD,6},[0x6A]={0xD4,0},[0x6B]={0xDA,2},
}

-- NPC locations (special_locations, 11) — NORMAL: checked when the bit is SET in
-- npc_check_field[offset]  (npc_check_field[offset] & (1<<bit) != 0).
local NPC_TABLE = {
  [0x04]={0,0},[0x05]={0,1},[0x0F]={1,3},[0x14]={0,2},
  [0x25]={0,3},[0x26]={1,4},[0x32]={0,4},[0x37]={0,5},
  [0x38]={1,2},[0x5A]={0,7},[0x5B]={0,6},
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil    -- cached "LEGEND OF ZELDA2" identifier result
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[zelda2] " .. tostring(msg)) end
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

-- bit i (0..7) as a power-of-two mask, matching the client's (1 << bit).
local POW2 = { [0]=1, 2, 4, 8, 16, 32, 64, 128 }

-- ── ROM identity: the Zelda II AP ROM carries "LEGEND OF ZELDA2" at PRG ROM ────
-- 0x1FFE0. Client.py validate_rom() reads 16 bytes there, strips zeros, decodes
-- ASCII and checks rom_name.startswith("LEGEND OF ZELDA2"). The vanilla US ROM
-- carries this string at the same place and the AP patch preserves it, so it is
-- the name-independent detector for any Zelda II AP seed (the per-seed/version
-- name the patch writes lives elsewhere, at PRG ROM 0x3A290 in the file — a
-- different field — so it never disturbs this identifier).
local function rom_is_zelda2()
  if rom_ok ~= nil then return rom_ok end
  for i = 1, #ROM_NAME do
    local b = read_u8(ROM_NAME_LOCATION + i - 1, PRGROM)
    if b == nil then return false end          -- not readable yet; retry next poll
    if b ~= string.byte(ROM_NAME, i) then
      rom_ok = false
      log("non-Zelda II ROM (no 'LEGEND OF ZELDA2' PRG-ROM signature) — detection idle")
      return false
    end
  end
  rom_ok = true
  log("Zelda II ROM verified ('LEGEND OF ZELDA2' signature present)")
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

-- ── Read blocks once per poll ─────────────────────────────────────────────────
local loc_data = {}    -- offset -> byte (RAM 0x0600 block)
local npc_data = {}    -- offset -> byte (WRAM 0x1A18 block)

local function refresh_blocks()
  for i = 0, LOC_ARRAY_LENGTH - 1 do loc_data[i] = read_u8(LOC_ARRAY_BASE + i, RAM) end
  for i = 0, NPC_FIELD_LENGTH - 1 do npc_data[i] = read_u8(NPC_FIELD_BASE + i, WRAM) end
end

-- ── Detection gate ────────────────────────────────────────────────────────────
-- game_state (RAM 0x0736) ∈ {0x0B, 0x05} is "side-scroll mode"; Client.py returns
-- early on any other value. LOAD-BEARING: the flag-array check is inverted, so any
-- non-play frame must be rejected or it would report all 95 flag locations at once.
local function in_game()
  local s = read_u8(GAME_STATE, RAM)
  return s ~= nil and (s == GAME_STATE_PLAY_A or s == GAME_STATE_PLAY_B)
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
  local n = 0
  for _ in pairs(LOC_TABLE) do n = n + 1 end
  for _ in pairs(NPC_TABLE) do n = n + 1 end
  log("ready: " .. n .. " location ids")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_zelda2() then return new end
  if not in_game() then return new end

  refresh_blocks()

  -- 1. Flag-array locations — INVERTED: bit CLEAR => checked.
  for ap_id, e in pairs(LOC_TABLE) do
    if not reported[ap_id] and wanted(ap_id) then
      local byte = loc_data[e[1]]
      if byte ~= nil and bit_and(byte, POW2[e[2]]) == 0 then
        emit(new, ap_id)
      end
    end
  end

  -- 2. NPC locations — NORMAL: bit SET => checked.
  for ap_id, e in pairs(NPC_TABLE) do
    if not reported[ap_id] and wanted(ap_id) then
      local byte = npc_data[e[1]]
      if byte ~= nil and bit_and(byte, POW2[e[2]]) ~= 0 then
        emit(new, ap_id)
      end
    end
  end

  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_zelda2() then return false end
  if not in_game() then return false end
  -- goal_trigger (RAM 0x076C) == 0x04 — Thunderbird defeated / Triforce of Courage.
  local b = read_u8(GOAL_TRIGGER, RAM)
  return b ~= nil and b == GOAL_VALUE
end

-- Remote items: see the file header. items_handling = 0b111 — the AP server drives
-- ALL item delivery (the reference client writes the next item id into WRAM 0x1A10
-- and bumps the obtained-count at WRAM 0x1A1C, gated on the game's own 0x1A10
-- mailbox reading 0x00). That guarded WRAM write path is deferred here until it can
-- be confirmed in-emulator; a wrong WRAM write would corrupt the save / desync the
-- count, so this is a no-op (never a wrong write) rather than shipped unverified.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
