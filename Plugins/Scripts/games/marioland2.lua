-- ═══════════════════════════════════════════════════════════════════════════════
-- marioland2.lua — game module for the Archipelago BizHawk connector.
--                  Super Mario Land 2: 6 Golden Coins (Game Boy)
--
-- STATUS: location DETECTION (level exits, secret exits, midway bells) and the
-- goal are REAL and SOURCE-DERIVED from the official AP world worlds/marioland2
-- (client.py + locations.py, main branch). The 60-entry exit/secret/bell location
-- table was GENERATED directly from locations.py (parsed with the same
-- enumerate(locations.items(), START_IDS) ordering the client uses, NOT
-- hand-copied), and the per-type bit/condition math is replicated EXACTLY from
-- client.py's game_watcher. Loads crash-free on any ROM; self-disables on a
-- non-SML2 cartridge (no "MARIOLAND2" ROM title).
--
-- MEMORY MODEL (BizHawk Game Boy domains — matches client.py MarioLand2Client)
-- ───────────────────────────────────────────────────────────────────────────
--   The SML2 AP client is a BizHawkClient that reads two domains here:
--     "CartRAM"  — the cartridge save RAM; ALL the static level-flag data, the
--                  game-loaded guard, the music byte, current level and the
--                  midway-point byte live here.
--     "ROM"      — the cartridge program ROM; carries the "MARIOLAND2" title.
--   (The client also reads "System Bus" for live coin-tile detection — see the
--    COINSANITY note below; that path is intentionally NOT replicated here.)
--
--   LEVEL-FLAG ARRAY: CartRAM[0x0848 .. 0x0848+42) is a 42-byte per-level array
--   (level_data). Each location carries a ram_index into it. The client decodes
--   three on-cartridge location types from raw level_data (client.py lines
--   ~144-149, exact):
--       level  →  level_data[ram_index] & 0x40   (normal/boss exit cleared)
--       secret →  level_data[ram_index] & 0x02   (secret exit taken)
--       bell   →  data.id == current_level AND midway_point == 0xFF
--                 (the midway bell of the level you are currently in)
--   (The 0x80/0x08/0x01 masks in client.py's modified_level_data are a WRITE
--    path — faking exits open for received progression items — NOT the read; we
--    detect on the raw values, exactly as the client's locations_checked does.)
--
--   GUARD (client.py lines ~100-103): the title-screen demos play levels with no
--   loaded save and no music, so the client refuses to report anything unless
--       game_loaded_check (CartRAM 0x0046, 10 bytes) == 12 34 56 78 FF*6   AND
--       music (CartRAM 0x0469) ~= 0.
--   We mirror both so the attract-mode demos never report phantom checks.
--
--   GOAL (client.py line ~233): music == 0x18 — the Mario's-Castle clear jingle.
--   The client sends CLIENT_GOAL on that exact value; is_goal_complete() mirrors.
--
-- COINSANITY / coin locations — DEFERRED (documented): the apworld also defines
--   ~2597 individual coin locations (ap ids 61+). The client detects those by
--   reading the LIVE level tilemap over the "System Bus" (0xB000 + y*256 + x for
--   a per-level coordinate list, counting tiles whose value is one of 0x7F/0x60/
--   0x07) — a runtime, position-dependent scan, not a static flag. That live path
--   is a different detection class from the cartridge flag array and needs
--   in-emulator verification (the coordinate tables + tile values vary with
--   physics/powerup state) before it is shipped; replicating it unverified would
--   risk wrong/under-counted coin checks. It is intentionally left out here. Exit,
--   secret and bell checks (the 60 on-cartridge flags) + the goal flow regardless;
--   a coinsanity seed simply reports its exit/secret/bell subset until that path
--   is confirmed.
--
-- WHAT THIS DOES (mirrors worlds/marioland2/client.py game_watcher)
--   • poll(): read the guard + level array once, then decode every wanted
--     exit/secret/bell location with the exact per-type math above → AP ids.
--     Gated to the slot's server location set and to the loaded+music guard.
--   • is_goal_complete(): music (CartRAM 0x0469) == 0x18.
--   • receive_item(): NO-OP (documented). items_handling = 0b111 — the AP SERVER
--     drives ALL item delivery, and the client applies received items by writing
--     PATCH-GENERATED ROM addresses (rom_addresses[...], unique per seed) plus
--     guarded CartRAM writes (lives/coins/level-data/difficulty). Those write
--     targets are not knowable without the per-seed patch's address map, and a
--     wrong ROM/CartRAM write corrupts the run, so remote-item delivery is
--     intentionally deferred rather than shipped unverified. Item delivery is
--     handled launcher-side by the connector's SYNC channel when that path is
--     enabled. Checks + goal flow regardless.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "marioland2"

local ADDRESSES_VERIFIED = true   -- tables generated from worlds/marioland2 source

-- ── Memory domains (BizHawk Game Boy) ─────────────────────────────────────────
local CARTRAM = "CartRAM"   -- cartridge save RAM (client "CartRAM") — flags + guard
local ROM     = "ROM"       -- cartridge program ROM (client "ROM")   — title id

-- ── Addresses / constants (worlds/marioland2/client.py) ───────────────────────
local ROM_NAME_ADDR    = 0x134     -- ROM: "MARIOLAND2" cartridge title (10 bytes)
local ROM_NAME         = "MARIOLAND2"
local GAME_LOADED_ADDR = 0x0046    -- CartRAM: 10-byte game-loaded signature
-- game_loaded_check must equal b'\x12\x34\x56\x78\xff\xff\xff\xff\xff\xff'
local GAME_LOADED_SIG  = { 0x12, 0x34, 0x56, 0x78, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }
local LEVEL_DATA_ADDR  = 0x0848    -- CartRAM: 42-byte per-level flag array
local LEVEL_DATA_LEN   = 42
local MUSIC_ADDR       = 0x0469    -- CartRAM: current music id (0 in demos)
local CURRENT_LVL_ADDR = 0x0269    -- CartRAM: current level id
local MIDWAY_PT_ADDR   = 0x02A0    -- CartRAM: midway-point byte (0xFF when reached)

local LEVEL_MASK       = 0x40      -- level: level_data[ram_index] & 0x40
local SECRET_MASK      = 0x02      -- secret: level_data[ram_index] & 0x02
local MIDWAY_REACHED   = 0xFF      -- bell: midway_point == 0xFF (in current level)
local GOAL_MUSIC       = 0x18      -- goal: music == 0x18 (castle clear)

-- ── Location tables (GENERATED from worlds/marioland2/locations.py) ───────────
-- ap_id = enumerate(locations.items(), START_IDS=1). Only the on-cartridge
-- exit/secret/bell types are here (the static level-flag detection); coin
-- locations (live tilemap) are the documented deferred path. 60 entries total:
-- 31 level + 6 secret + 23 bell.

-- LEVEL (31): ap_id -> ram_index, checked when level_data[ram_index] & 0x40.
local LOC_LEVEL = {
  [1]=0,[3]=40,[4]=1,[6]=2,[9]=4,[10]=3,
  [12]=5,[14]=36,[15]=31,[16]=16,[19]=41,[20]=17,
  [22]=11,[25]=12,[27]=13,[29]=14,[31]=35,[32]=6,
  [34]=7,[37]=8,[40]=9,[42]=38,[43]=39,[44]=26,
  [46]=27,[48]=28,[50]=29,[52]=21,[54]=22,[57]=23,
  [59]=37,
}

-- SECRET (6): ap_id -> ram_index, checked when level_data[ram_index] & 0x02.
local LOC_SECRET = {
  [7]=2,[17]=16,[23]=11,[35]=7,[38]=8,[55]=22,
}

-- BELL (23): ap_id -> {ram_index, level_id}. Checked when level_id ==
-- current_level AND midway_point == 0xFF. (ram_index is kept for fidelity /
-- traceability; the bell condition itself does not read the flag array.)
local LOC_BELL = {
  [2]={0,0},[5]={1,1},[8]={2,2},[11]={3,3},
  [13]={5,5},[18]={16,18},[21]={17,19},[24]={11,20},
  [26]={12,21},[28]={13,22},[30]={14,23},[33]={6,6},
  [36]={7,7},[39]={8,8},[41]={9,9},[45]={26,10},
  [47]={27,11},[49]={28,12},[51]={29,13},[53]={21,14},
  [56]={22,15},[58]={23,16},[60]={24,24},
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil     -- cached "MARIOLAND2" title result
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[marioland2] " .. tostring(msg)) end
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

-- ── ROM identity: the cartridge title "MARIOLAND2" sits at ROM 0x134 ──────────
-- This is the standard GB header title field, present on both the vanilla SML2
-- (1.0) ROM and the AP-patched .gb (the patch only writes data + tokens, never
-- the title) — exactly what the client's validate_rom() checks at 0x134.
local function rom_is_marioland2()
  if rom_ok ~= nil then return rom_ok end
  for i = 1, #ROM_NAME do
    local b = read_u8(ROM_NAME_ADDR + i - 1, ROM)
    if b == nil then return false end          -- not readable yet; retry next poll
    if b ~= string.byte(ROM_NAME, i) then
      rom_ok = false
      log("non-SML2 ROM (no 'MARIOLAND2' title) — detection idle")
      return false
    end
  end
  rom_ok = true
  log("SML2 ROM verified ('MARIOLAND2' title present)")
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

-- ── Level-flag array (read once per poll) ─────────────────────────────────────
local level_data = {}     -- ram_index (0-based) -> byte value, refreshed each poll

local function refresh_level_data()
  for i = 0, LEVEL_DATA_LEN - 1 do
    level_data[i] = read_u8(LEVEL_DATA_ADDR + i, CARTRAM)
  end
end

-- ── Detection gate (client.py game-loaded + music guard) ──────────────────────
-- The title-screen demos run levels with no save loaded and no music; the client
-- bails unless the 10-byte loaded signature matches AND music ~= 0. Mirror both.
local function in_game()
  for i = 1, #GAME_LOADED_SIG do
    local b = read_u8(GAME_LOADED_ADDR + i - 1, CARTRAM)
    if b == nil or b ~= GAME_LOADED_SIG[i] then return false end
  end
  local music = read_u8(MUSIC_ADDR, CARTRAM)
  return music ~= nil and music ~= 0
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
  for _ in pairs(LOC_LEVEL)  do n = n + 1 end
  for _ in pairs(LOC_SECRET) do n = n + 1 end
  for _ in pairs(LOC_BELL)   do n = n + 1 end
  log("ready: " .. n .. " exit/secret/bell location flags")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_marioland2() then return new end
  if not in_game() then return new end
  refresh_level_data()

  -- level: level_data[ram_index] & 0x40
  for ap_id, ri in pairs(LOC_LEVEL) do
    if not reported[ap_id] and wanted(ap_id) then
      local byte = level_data[ri]
      if byte ~= nil and bit_and(byte, LEVEL_MASK) ~= 0 then
        reported[ap_id] = true
        new[#new + 1] = ap_id
      end
    end
  end

  -- secret: level_data[ram_index] & 0x02
  for ap_id, ri in pairs(LOC_SECRET) do
    if not reported[ap_id] and wanted(ap_id) then
      local byte = level_data[ri]
      if byte ~= nil and bit_and(byte, SECRET_MASK) ~= 0 then
        reported[ap_id] = true
        new[#new + 1] = ap_id
      end
    end
  end

  -- bell: data.id == current_level AND midway_point == 0xFF
  local current_level = read_u8(CURRENT_LVL_ADDR, CARTRAM)
  local midway_point  = read_u8(MIDWAY_PT_ADDR, CARTRAM)
  if current_level ~= nil and midway_point == MIDWAY_REACHED then
    for ap_id, info in pairs(LOC_BELL) do
      if not reported[ap_id] and wanted(ap_id) and info[2] == current_level then
        reported[ap_id] = true
        new[#new + 1] = ap_id
      end
    end
  end

  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_marioland2() then return false end
  -- The goal jingle plays only on a loaded game, so the music guard is implied;
  -- read the one byte the client checks.
  local music = read_u8(MUSIC_ADDR, CARTRAM)
  return music ~= nil and music == GOAL_MUSIC
end

-- Remote items: see the file header. items_handling = 0b111 — the AP server drives
-- ALL item delivery, and the reference client applies items via PATCH-GENERATED
-- ROM addresses (unique per seed) plus guarded CartRAM writes. Those targets are
-- not knowable without the per-seed patch address map, and a wrong write corrupts
-- the run, so this is a no-op (never a wrong write) rather than shipped unverified.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
