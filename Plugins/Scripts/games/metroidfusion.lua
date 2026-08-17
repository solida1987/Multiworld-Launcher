-- ═══════════════════════════════════════════════════════════════════════════════
-- metroidfusion.lua — game module for the Archipelago BizHawk connector.
--                     Metroid Fusion (GBA)
--
-- STATUS: COMMUNITY apworld (NOT in the ArchipelagoMW/Archipelago main tree).
--   Source: https://github.com/Rosalie-A/Archipelago  (branch "metroidfusion",
--   worlds/metroidfusion — world id "metroidfusion", game string "Metroid Fusion",
--   patch ".apmetfus" → ".gba", APWorld version 17, MARS-based mars_patcher).
-- Location DETECTION + goal are REAL and SOURCE-DERIVED from that world's
-- Client.py (MetroidFusionClient.game_watcher), data/memory.py, data/minor_locations.py
-- and data/major_locations.py. The two location tables below were GENERATED directly
-- from the apworld source (the ap_id assignment loop in Locations.py joined with the
-- minor/major MARS bitfield orders), not hand-copied, so they are exact. Loads
-- crash-free on any ROM; self-disables on a non-AP / non-Fusion cartridge.
--
-- MEMORY MODEL (BizHawk GBA — the Fusion client reads per-domain, EWRAM/IWRAM/ROM)
-- ──────────────────────────────────────────────────────────────────────────────
--   The Metroid Fusion AP client (a BizHawkClient) reads GBA work-RAM by domain,
--   exactly like the cvcotm client. This module reads the SAME domains so the
--   addresses match the client 1:1.
--
--   MINOR LOCATIONS (Client.py location_check, first half):
--     minor_locations_start = 0x037200 in "EWRAM", a 16-byte little-endian
--     bitfield. The client walks data/minor_locations.py `location_order` in
--     list order; for index N: byte = N//8, bit = 2**(N%8); the location is
--     checked when ewram[byte] & bit. Empty-string entries in that order are
--     placeholders (no location) and are skipped. -> MINOR maps ap_id -> N.
--
--   MAJOR LOCATIONS (Client.py location_check, second half):
--     major_locations_start = 0x06B4 in "IWRAM", a 4-byte little-endian bitfield.
--     The client walks data/major_locations.py `location_order`; same byte/bit
--     math on the enumerate index N. It explicitly `continue`s on
--     "ARC Data Room 2 -- Unused" (that name has NO ap_id but still consumes a
--     bit position, so the index of every later entry is preserved). -> MAJOR
--     maps ap_id -> N.
--
--   ap_id: the apworld uses base_id = 0 and assigns ap_id = 1,2,3,… by iterating
--     fusion_regions in order (Locations.py). location_name_to_id = {name: ap_id}.
--     Both tables below are already keyed by that final ap_id, so NO base offset
--     is applied here.
--
--   GOAL (Client.py check_victory):
--     game_mode = 0x0BDE (IWRAM, u8). The run is complete when game_mode ==
--     credits_mode (0x0B). (ctx.finished_game latches it; we report it whenever
--     the mode is observed.)
--
--   GATE (Client.py read_*_guarded — the BizHawk guarded_read condition):
--     Every location read is guarded on game_mode == ingame_mode (0x01). Location
--     bits are only meaningful in-game; the title-screen / cutscene modes never
--     satisfy the guard, so checks never report off a fresh boot or a demo.
--
-- ROM IDENTITY (Client.py validate_rom):
--   rom_name_location = 0x7FFF00 in "ROM" holds a 20-byte name the AP patch
--   writes as "MFU<ver>_<player>_<seed>"; the client accepts the ROM only when
--   its first three bytes are "MFU". That 3-char tag is present on every
--   Fusion-AP-patched ROM and absent from a vanilla cart, so it is the stable
--   "is this an AP Metroid Fusion ROM" gate, matching the client's first guard.
--
-- WHAT THIS DOES (mirrors MetroidFusionClient.game_watcher)
--   • poll(): read the EWRAM minor bitfield (16 B) + IWRAM major bitfield (4 B)
--     → AP location ids, gated to the slot's server location set and to
--     game_mode == ingame_mode, so the title/cutscene states report nothing.
--   • is_goal_complete(): game_mode == credits_mode (0x0B).
--   • receive_item(): NO-OP (documented). items_handling = 0b011 — the AP SERVER
--     drives item delivery, and the Fusion CLIENT itself injects EVERY received
--     item (locals + remotes) through a guarded multi-write path into IWRAM
--     inventory/tank/keycard state (Client.py received_items_check + sync_upgrades:
--     per-item bit toggles, ammo accumulators, the SRAM received-count handshake
--     at 0x0E01FFFE/F, and a graphics-reload flag). That path is intricate and
--     state-sensitive and must be confirmed in-emulator before being reproduced
--     launcher-side, so it is intentionally GATED OUT here rather than shipped
--     unverified (a wrong IWRAM write could mis-grant items or corrupt the save).
--     Detection/goal — the reportable half — are fully live; item delivery is the
--     deferred piece.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "metroidfusion"

local ADDRESSES_VERIFIED = true   -- tables generated from worlds/metroidfusion source

-- ── Memory domains (Client.py: minor=EWRAM, major+game_mode=IWRAM, sig=ROM) ───
local EWRAM = "EWRAM"
local IWRAM = "IWRAM"
local ROM   = "ROM"

-- ── Addresses / constants (worlds/metroidfusion/data/memory.py + Client.py) ───
local MINOR_START      = 0x037200   -- EWRAM: 16-byte minor-location bitfield
local MINOR_BYTES      = 16
local MAJOR_START      = 0x06B4     -- IWRAM: 4-byte major-location bitfield
local MAJOR_BYTES      = 4
local GAME_MODE_ADDR   = 0x0BDE     -- IWRAM u8: game mode
local INGAME_MODE      = 0x01       -- gate: location bits valid only in-game
local CREDITS_MODE     = 0x0B       -- goal: credits rolling
local AP_SIG_ADDR      = 0x7FFF00   -- ROM: 20-byte AP rom name ("MFU..._<player>_<seed>")
local AP_SIG           = "MFU"      -- first 3 bytes identify an AP Metroid Fusion ROM

-- ── Location tables (GENERATED from worlds/metroidfusion source) ──────────────
-- ap_id -> bit index N. checked when bitfield[N//8] & (1 << (N%8)). MINOR reads
-- the EWRAM 16-byte field at MINOR_START; MAJOR reads the IWRAM 4-byte field at
-- MAJOR_START. 103 minor + 23 major = 126 locations (ap_ids 1..126); the empty
-- minor placeholder and "ARC Data Room 2 -- Unused" major hold no ap_id.
local MINOR = {
  [1]=12,[2]=6,[3]=11,[4]=0,[5]=5,[6]=2,[7]=4,[8]=3,[9]=14,[13]=10,
  [14]=13,[15]=7,[16]=8,[19]=9,[20]=22,[21]=21,[22]=15,[23]=27,[24]=18,[25]=16,
  [26]=17,[27]=25,[29]=23,[30]=26,[31]=24,[32]=28,[34]=20,[35]=43,[36]=42,[37]=36,
  [39]=37,[41]=29,[42]=30,[43]=31,[44]=38,[45]=34,[46]=41,[47]=40,[49]=32,[50]=45,
  [51]=44,[52]=35,[54]=33,[55]=39,[56]=61,[57]=55,[58]=54,[60]=46,[61]=56,[62]=58,
  [63]=51,[66]=47,[68]=57,[69]=49,[70]=48,[71]=50,[72]=52,[73]=53,[74]=60,[75]=59,
  [76]=72,[77]=62,[78]=63,[79]=76,[80]=65,[81]=64,[82]=70,[84]=71,[85]=68,[86]=67,
  [87]=69,[88]=66,[89]=75,[91]=73,[92]=74,[94]=86,[95]=85,[96]=77,[97]=78,[98]=84,
  [99]=79,[101]=83,[102]=82,[103]=91,[105]=88,[106]=90,[107]=80,[108]=81,[109]=89,[110]=87,
  [112]=92,[113]=97,[114]=96,[115]=101,[116]=100,[117]=102,[118]=103,[120]=95,[121]=94,[122]=1,
  [123]=93,[125]=98,[126]=99,
}

local MAJOR = {
  [10]=1,[11]=0,[12]=22,[17]=14,[18]=23,[28]=2,[33]=20,[38]=4,[40]=3,[48]=5,
  [53]=15,[59]=7,[64]=21,[65]=12,[67]=8,[83]=6,[90]=17,[93]=18,[100]=10,[104]=11,
  [111]=16,[119]=19,[124]=9,
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil    -- cached AP-signature result
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[metroidfusion] " .. tostring(msg)) end
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

-- Test bit `n` (0..7) of a byte using float-safe arithmetic.
local POW2 = { [0]=1, 2, 4, 8, 16, 32, 64, 128 }
local function byte_has_bit(value, n)
  if value == nil then return false end
  local b = math.floor(value / POW2[n])
  return (b % 2) >= 1
end

-- ── ROM identity: AP patch writes "MFU..." at ROM 0x7FFF00 ────────────────────
local function rom_is_ap()
  if rom_ok ~= nil then return rom_ok end
  for i = 1, #AP_SIG do
    local b = read_u8(AP_SIG_ADDR + i - 1, ROM)
    if b == nil then return false end          -- not readable yet; retry next poll
    if b ~= string.byte(AP_SIG, i) then
      rom_ok = false
      log("non-AP ROM (no MFU signature) — detection idle")
      return false
    end
  end
  rom_ok = true
  log("AP Metroid Fusion ROM verified (MFU signature present)")
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

-- ── Bitfields (read once per poll) ────────────────────────────────────────────
local minor_bytes = {}     -- byte index 0..15 -> value
local major_bytes = {}     -- byte index 0..3  -> value

local function refresh_minor()
  for i = 0, MINOR_BYTES - 1 do minor_bytes[i] = read_u8(MINOR_START + i, EWRAM) end
end

local function refresh_major()
  for i = 0, MAJOR_BYTES - 1 do major_bytes[i] = read_u8(MAJOR_START + i, IWRAM) end
end

local function minor_checked(bit_index)
  local byte = minor_bytes[math.floor(bit_index / 8)]
  return byte_has_bit(byte, bit_index % 8)
end

local function major_checked(bit_index)
  local byte = major_bytes[math.floor(bit_index / 8)]
  return byte_has_bit(byte, bit_index % 8)
end

-- ── Detection gate ────────────────────────────────────────────────────────────
local function in_gameplay()
  local gm = read_u8(GAME_MODE_ADDR, IWRAM)
  return gm == INGAME_MODE
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
  local nm = 0; for _ in pairs(MINOR) do nm = nm + 1 end
  local nj = 0; for _ in pairs(MAJOR) do nj = nj + 1 end
  log("ready: " .. (nm + nj) .. " location flags (" .. nm .. " minor + " .. nj ..
      " major; community apworld 'metroidfusion')")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_ap() then return new end
  if not in_gameplay() then return new end   -- ingame_mode only (matches client guard)
  refresh_minor()
  refresh_major()
  for ap_id, bit_index in pairs(MINOR) do
    if not reported[ap_id] and wanted(ap_id) and minor_checked(bit_index) then
      reported[ap_id] = true
      new[#new + 1] = ap_id
    end
  end
  for ap_id, bit_index in pairs(MAJOR) do
    if not reported[ap_id] and wanted(ap_id) and major_checked(bit_index) then
      reported[ap_id] = true
      new[#new + 1] = ap_id
    end
  end
  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_ap() then return false end
  local gm = read_u8(GAME_MODE_ADDR, IWRAM)
  return gm == CREDITS_MODE
end

-- Remote multiworld items: see the file header. items_handling = 0b011 — the
-- Fusion client injects every received item itself through a guarded IWRAM
-- inventory/tank/keycard write path plus an SRAM received-count handshake. That
-- path is the one piece deferred until it can be confirmed in-emulator. No-op
-- (never a wrong write) until then.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
