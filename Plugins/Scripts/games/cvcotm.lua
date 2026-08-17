-- ═══════════════════════════════════════════════════════════════════════════════
-- cvcotm.lua — game module for the Archipelago BizHawk connector.
--             Castlevania: Circle of the Moon (GBA)
--
-- STATUS: location DETECTION is REAL and SOURCE-DERIVED from the official AP
-- world worlds/cvcotm (client.py + locations.py + rom.py, main branch). The
-- location table was GENERATED directly from locations.py (cvcotm_location_info),
-- not hand-copied, so it is exact. Loads crash-free on any ROM; self-disables on
-- a non-AP cartridge.
--
-- MEMORY MODEL (BizHawk GBA domains)
-- ──────────────────────────────────
--   The cvcotm AP client (a BizHawkClient) reads the GBA work RAM ("EWRAM") and
--   the cartridge ("ROM"). All location "checked" bits live in one 32-byte flag
--   array; the AP id and the bit index are the SAME number (`code`):
--     ap_id            = 0xD55C0000 + code         (BASE_ID + code)
--     checked when       EWRAM[0x25374 + code//8] & (1 << (code % 8))   (LSB-first)
--   (locations.py comment + client.py flag-set math at line ~462 both confirm.)
--
-- WHAT THIS DOES (mirrors worlds/cvcotm/client.py game_watcher)
--   • poll(): scan the flag array → AP location ids, gated to the slot's server
--     location set and to the in-game gate (state ∈ {gameplay, credits} AND the
--     intro "floor broken" flag, so the title-screen demo never reports checks).
--   • is_goal_complete(): the "Defeated Dracula II" flag (0xBC) — the default
--     completion goal. (The Battle-Arena / both goals are slot_data options; a
--     refinement, see the note on receive_item.)
--   • receive_item(): NO-OP for now (documented). items_handling = 0b001 means
--     the PATCHED GAME grants its own locally-found items, so a SOLO seed plays
--     fully and every check is reported. Delivering REMOTE multiworld items is
--     the client's intricate text-box-injection path (per-item inventory-array
--     decode + guarded writes); that is the one piece that needs in-emulator
--     verification before it is wired, so it is intentionally left out rather
--     than shipped unverified (would risk mis-applying items).
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "cvcotm"

local ADDRESSES_VERIFIED = true   -- table generated from worlds/cvcotm source

-- ── Memory domains ────────────────────────────────────────────────────────────
local EWRAM = "EWRAM"
local ROM   = "ROM"

-- ── Addresses / constants (worlds/cvcotm/client.py + rom.py) ──────────────────
local GAME_STATE_ADDR     = 0x45D8        -- EWRAM: main game-state byte
local FLAGS_ARRAY_START   = 0x25374       -- EWRAM: 32-byte location/event flag array
local AP_SIG_ADDR         = 0x7FFF00      -- ROM: ARCHIPELAGO_IDENTIFIER_START
local AP_SIG              = "ARCHIPELAG03" -- ARCHIPELAGO_IDENTIFIER (12 chars)
local GAME_STATE_GAMEPLAY = 0x06
local GAME_STATE_CREDITS  = 0x21
local INTRO_DONE_BYTE     = 6             -- flags[6] & 0x02 set during real play
local INTRO_DONE_MASK     = 0x02
local FLAG_DRACULA_II     = 0xBC          -- goal: "Defeated Dracula II"
local BASE_ID             = 0xD55C0000

-- ── Location table (GENERATED from worlds/cvcotm/locations.py) ────────────────
-- ap_id -> bitflag index (`code`). 125 entries. ap_id = BASE_ID + code; checked
-- when FLAGS_ARRAY[code//8] & (1 << (code % 8)).
local LOC = {
  [3579576373]=53,[3579576374]=54,[3579576375]=55,[3579576376]=56,[3579576377]=57,[3579576378]=58,
  [3579576379]=59,[3579576380]=60,[3579576381]=61,[3579576382]=62,[3579576383]=63,[3579576384]=64,
  [3579576385]=65,[3579576386]=66,[3579576387]=67,[3579576388]=68,[3579576389]=69,[3579576390]=70,
  [3579576391]=71,[3579576392]=72,[3579576393]=73,[3579576394]=74,[3579576395]=75,[3579576396]=76,
  [3579576397]=77,[3579576398]=78,[3579576399]=79,[3579576400]=80,[3579576401]=81,[3579576402]=82,
  [3579576403]=83,[3579576404]=84,[3579576405]=85,[3579576406]=86,[3579576407]=87,[3579576408]=88,
  [3579576409]=89,[3579576410]=90,[3579576411]=91,[3579576412]=92,[3579576413]=93,[3579576414]=94,
  [3579576415]=95,[3579576416]=96,[3579576417]=97,[3579576418]=98,[3579576419]=99,[3579576420]=100,
  [3579576421]=101,[3579576422]=102,[3579576423]=103,[3579576424]=104,[3579576425]=105,[3579576426]=106,
  [3579576427]=107,[3579576428]=108,[3579576429]=109,[3579576430]=110,[3579576431]=111,[3579576432]=112,
  [3579576433]=113,[3579576434]=114,[3579576435]=115,[3579576436]=116,[3579576437]=117,[3579576438]=118,
  [3579576439]=119,[3579576440]=120,[3579576441]=121,[3579576442]=122,[3579576443]=123,[3579576444]=124,
  [3579576445]=125,[3579576446]=126,[3579576447]=127,[3579576448]=128,[3579576449]=129,[3579576450]=130,
  [3579576451]=131,[3579576452]=132,[3579576453]=133,[3579576454]=134,[3579576455]=135,[3579576456]=136,
  [3579576457]=137,[3579576458]=138,[3579576459]=139,[3579576460]=140,[3579576461]=141,[3579576462]=142,
  [3579576463]=143,[3579576464]=144,[3579576465]=145,[3579576466]=146,[3579576467]=147,[3579576468]=148,
  [3579576469]=149,[3579576470]=150,[3579576471]=151,[3579576472]=152,[3579576473]=153,[3579576474]=154,
  [3579576475]=155,[3579576476]=156,[3579576477]=157,[3579576478]=158,[3579576479]=159,[3579576480]=160,
  [3579576481]=161,[3579576482]=162,[3579576483]=163,[3579576484]=164,[3579576485]=165,[3579576486]=166,
  [3579576487]=167,[3579576488]=168,[3579576489]=169,[3579576490]=170,[3579576491]=171,[3579576492]=172,
  [3579576494]=174,[3579576495]=175,[3579576496]=176,[3579576498]=178,[3579576560]=240,
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil     -- cached AP-signature result
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[cvcotm] " .. tostring(msg)) end
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

local POW2 = { [0]=1, 2, 4, 8, 16, 32, 64, 128 }

-- ── ROM identity: the AP patch writes "ARCHIPELAG03" at ROM 0x7FFF00 ──────────
local function rom_is_ap()
  if rom_ok ~= nil then return rom_ok end
  for i = 1, #AP_SIG do
    local b = read_u8(AP_SIG_ADDR + i - 1, ROM)
    if b == nil then return false end          -- not readable yet; retry next poll
    if b ~= string.byte(AP_SIG, i) then
      rom_ok = false
      log("non-AP ROM (no ARCHIPELAG03 signature) — detection idle")
      return false
    end
  end
  rom_ok = true
  log("AP ROM verified (ARCHIPELAG03 signature present)")
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

-- ── Flag array (read once per poll) ───────────────────────────────────────────
local flags = {}     -- byte index -> value, refreshed each poll

local function refresh_flags()
  for i = 0, 31 do flags[i] = read_u8(FLAGS_ARRAY_START + i, EWRAM) end
end

local function flag_bit(code)
  local byte = flags[math.floor(code / 8)]
  if byte == nil then return false end
  return bit_and(byte, POW2[code % 8]) ~= 0
end

-- ── Detection gate ────────────────────────────────────────────────────────────
local function in_gameplay()
  local s = read_u8(GAME_STATE_ADDR, EWRAM)
  if s ~= GAME_STATE_GAMEPLAY and s ~= GAME_STATE_CREDITS then return false end
  -- flags[6] & 0x02 is set during real play; unset on the title-screen demo.
  local f6 = flags[INTRO_DONE_BYTE]
  return f6 ~= nil and bit_and(f6, INTRO_DONE_MASK) ~= 0
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
  local n = 0; for _ in pairs(LOC) do n = n + 1 end
  log("ready: " .. n .. " location flags")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_ap() then return new end
  refresh_flags()
  if not in_gameplay() then return new end
  for ap_id, code in pairs(LOC) do
    if not reported[ap_id] and wanted(ap_id) and flag_bit(code) then
      reported[ap_id] = true
      new[#new + 1] = ap_id
    end
  end
  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_ap() then return false end
  refresh_flags()
  return flag_bit(FLAG_DRACULA_II)
end

-- Remote multiworld items: see the file header. items_handling = 0b001 means the
-- patched game grants its own found items, so solo play and check reporting work
-- fully; applying REMOTE items is the client's text-box-injection path and is the
-- one piece deferred until it can be confirmed in-emulator. No-op (never a wrong
-- write) until then.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
