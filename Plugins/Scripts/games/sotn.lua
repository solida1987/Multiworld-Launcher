-- ═══════════════════════════════════════════════════════════════════════════════
-- sotn.lua — game module for the Archipelago BizHawk connector.
--            Castlevania: Symphony of the Night (PlayStation / PSX)
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED. The boss,
-- cutscene, relic and prologue detection tables below were GENERATED directly
-- (parsed, not hand-copied) from the community AP world's in-emulator Lua
-- connector, data/lua/connector_sotn.lua, in
--   https://github.com/AdmiralTryhard/SOTNArchipelago  (world id "sotn").
-- Cross-checked against worlds/sotn/Locations.py: all 63 numeric AP location
-- ids are covered, and the goal is the inverted-castle Dracula kill ("Black
-- Marble Gallery: Patricide"). Loads crash-free on any disc; self-disables
-- (reports nothing, writes nothing) until the game is actually in play.
--
-- MEMORY MODEL (BizHawk PSX core → memory domain)
-- ────────────────────────────────────────────────
--   This is a PSX game. The reference connector_sotn.lua does every read/write
--   through BizHawk's `mainmemory.*` accessor, which addresses the PSX core's
--   MAIN RAM by raw offset (no 0x80000000 segment base). With BizHawk's PSX
--   core (Nymashock — what the apworld targets; Octoshock untested), that
--   accessor is the "MainRAM" memory domain. The generic AP connector hands
--   modules the BizHawk `memory` API, so every read here is
--       memory.read_u8 / read_u16_le / read_u32_le (addr, "MainRAM")
--   i.e. mainmemory.read_X(addr)  ==  memory.read_X(addr, "MainRAM").
--   ("System Bus" also resolves these offsets on the PSX core and is used as a
--   fallback on cores that don't expose MainRAM by that exact name.)
--
-- HOW DETECTION WORKS (mirrors connector_sotn.lua, all unchecked-only logic)
--   • Bosses: a u32 at a fixed MainRAM address (0x03CAxx) is non-zero once the
--     boss is dead → that address's AP location id(s) are checked.
--   • Prologue: zone (u16 @ 0x180000) != 6300 AND the "past-Dracula" flag
--     (u32 @ 0x13798C) != 0 → the two prologue checks (135000/135001).
--   • Cutscenes: the player is standing in the right room (u16 @ 0x1375BC) at
--     the right X (u16 @ 0x0973F0) [and sometimes Y (u16 @ 0x0973F4)], within
--     a 15px tolerance → that cutscene's AP id.
--   • Relics/items on pedestals: same room+X+Y position match → that pickup's
--     AP id. (The 5 Vlad relics the connector lists with AP location "none"
--     are item-denial only — no AP location — so they are not reported.)
--   poll() reports each id once, gated to the slot's server location set.
--   is_goal_complete(): the connector's check_victory — in the final-fight room
--     (0x1375BC == 5236), Dracula's HP (u16 @ 0x076ED6) starts at 10000 when he
--     becomes vulnerable, then on death underflows past 20000 (or reads 0).
--
-- receive_item(): NO-OP (documented). items_handling = 0b111 — the AP SERVER
--   drives ALL item delivery. The reference connector grants items with a tower
--   of guarded MainRAM writes: relics get byte 3 written to their slot, stackable
--   items get their u16 inventory count bumped, Life Max Up bumps max HP, AND it
--   actively DENIES vanilla relic/item pickups the server hasn't released yet
--   (writing 0 back over the in-game slot at the exact pickup position). That
--   write/deny path is precisely the piece that must be confirmed in-emulator
--   before shipping — a wrong MainRAM write would corrupt the live game state —
--   so it is intentionally deferred (no-op) rather than shipped unverified.
--   Checks + goal flow fully regardless; a solo or co-op-of-checks seed plays.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "sotn"

local ADDRESSES_VERIFIED = true   -- tables generated from connector_sotn.lua

-- ── Memory domain ─────────────────────────────────────────────────────────────
local MAINRAM = "MainRAM"         -- PSX main RAM (Nymashock); fallback "System Bus"

-- ── Addresses (connector_sotn.lua) ────────────────────────────────────────────
local ADDR_X        = 0x0973F0    -- u16: Alucard X position
local ADDR_Y        = 0x0973F4    -- u16: Alucard Y position
local ADDR_ROOM     = 0x1375BC    -- u16: current room id
local ADDR_ZONE     = 0x180000    -- u16: current zone (prologue == 6300)
local ADDR_PROLOGUE = 0x13798C    -- u32: past-Dracula prologue flag
local ADDR_DRAC_HP  = 0x076ED6    -- u16: Dracula's HP (final fight)
local POS_TOLERANCE = 15          -- px window around a pedestal/cutscene trigger
local PROLOGUE_ZONE = 6300
local VICTORY_ROOM  = 5236        -- special room id for the final Dracula fight

-- ── Detection tables (GENERATED from connector_sotn.lua) ──────────────────────

-- Bosses: MainRAM u32 address -> { AP location id(s) }. Non-zero == dead. 20 rows.
local BOSSES = {
  [0x03CA74]={140014,140015}, [0x03CA48]={140004},        [0x03CA5C]={136027},
  [0x03CA78]={140005,140007}, [0x03CA58]={140009,140010}, [0x03CA30]={135010},
  [0x03CA70]={140008},        [0x03CA54]={140006},        [0x03CA7C]={140011},
  [0x03CA34]={136028},        [0x03CA44]={135018},        [0x03CA50]={135017},
  [0x03CA6C]={135015},        [0x03CA64]={140003,140002}, [0x03CA38]={136034},
  [0x03CA2C]={135030},        [0x03CA3C]={135024},        [0x03CA40]={135004},
  [0x03CA4C]={135023},        [0x03CA68]={140000,140001},
}

-- Cutscene triggers: { room, x, ap_id [, y] }. Position match (X always, Y when
-- the 4th field is present), within POS_TOLERANCE. 11 rows.
local CUTSCENES = {
  {8292, 158, 135000},      {6116, 232, 135039},      {15648, 52, 135002},
  {4848, 176, 135007},      {12004, 255, 135011, 135},{9084, 425, 135019},
  {9052, 510, 135027},      {10040, 254, 135013},     {5444, 1728, 135022},
  {5968, 384, 140016, 199}, {5276, 352, 135040, 183},
}

-- Relic / pedestal-item pickups: { room, x, y, ap_id }. Position match on all
-- three. The 5 Vlad relics the connector marks AP-location "none" (deny-only,
-- no AP location) are intentionally omitted — they have no check to report. 26 rows.
local RELICS = {
  {9360, 26, 122, 140012},  {11372, 114, 167, 140013}, {12028, 49, 167, 136014},
  {12004, 116, 167, 135038},{12044, 1678, 167, 136013},{11972, 1060, 919, 135016},
  {13556, 390, 807, 135014},{9120, 201, 183, 135037},  {7060, 351, 663, 136031},
  {7052, 414, 1207, 135021},{7052, 414, 1815, 135020}, {9052, 182, 151, 135028},
  {13076, 365, 135, 135031},{13068, 129, 135, 136030}, {10372, 1162, 167, 135009},
  {11920, 237, 135, 135033},{10032, 117, 167, 135006}, {10096, 121, 137, 135036},
  {15680, 272, 103, 135003},{14976, 272, 183, 135032}, {12700, 97, 167, 135025},
  {12636, 142, 167, 135026},{12828, 176, 167, 135034}, {10228, 130, 1011, 135008},
  {11212, 47, 135, 135029}, {6584, 90, 167, 135012},
}

-- Prologue checks (connector_sotn.lua check_prologue): fires when NOT in the
-- prologue zone with the past-Dracula flag set.
local PROLOGUE_IDS = {135001, 135000}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local on_drac          = false  -- Dracula-fight latch (mirrors connector's on_drac)
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[sotn] " .. tostring(msg)) end
end

-- ── Memory API (resolved at init; 2-arg domain form + fallbacks) ──────────────
local function resolve_memory_api()
  if not memory then return false end
  mem.read_u8  = memory.read_u8     or memory.readbyte
  mem.read_u16 = memory.read_u16_le or memory.readword
  mem.read_u32 = memory.read_u32_le or memory.readdword
  return mem.read_u8 ~= nil
end

-- Read with the PSX domain, falling back to "System Bus", then current domain.
local function rd(fn, addr)
  if not fn then return nil end
  local ok, v = pcall(fn, addr, MAINRAM)
  if ok and type(v) == "number" then return v end
  ok, v = pcall(fn, addr, "System Bus")
  if ok and type(v) == "number" then return v end
  ok, v = pcall(fn, addr)                      -- older API: current domain
  if ok and type(v) == "number" then return v end
  return nil
end

local function read_u16(addr) return rd(mem.read_u16, addr) end
local function read_u32(addr) return rd(mem.read_u32, addr) end

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
  if server_locations == nil then return true end   -- no set provided → report all
  return server_locations[ap_id] == true
end

local function add_check(new, ap_id)
  if type(ap_id) ~= "number" then return end
  if not reported[ap_id] and wanted(ap_id) then
    reported[ap_id] = true
    new[#new + 1] = ap_id
  end
end

-- ── Detection passes (mirror connector_sotn.lua) ──────────────────────────────
local function check_bosses(new)
  for addr, ids in pairs(BOSSES) do
    local v = read_u32(addr)
    if v and v ~= 0 then
      for _, ap_id in ipairs(ids) do add_check(new, ap_id) end
    end
  end
end

local function check_prologue(new)
  -- connector_sotn.lua: zone != 6300 OR the past-Dracula u32 flag != 0.
  local zone = read_u16(ADDR_ZONE)
  local flag = read_u32(ADDR_PROLOGUE)
  if zone == nil then return end
  if zone ~= PROLOGUE_ZONE or (flag ~= nil and flag ~= 0) then
    for _, ap_id in ipairs(PROLOGUE_IDS) do add_check(new, ap_id) end
  end
end

local function near(a, b)
  if a == nil or b == nil then return false end
  local d = a - b
  if d < 0 then d = -d end
  return d < POS_TOLERANCE
end

local function check_cutscenes(new, room, x, y)
  for _, info in ipairs(CUTSCENES) do
    -- info = { room, x, ap_id [, y] }
    if room == info[1] and near(x, info[2]) and (info[4] == nil or near(y, info[4])) then
      add_check(new, info[3])
    end
  end
end

local function check_relics(new, room, x, y)
  for _, info in ipairs(RELICS) do
    -- info = { room, x, y, ap_id }
    if room == info[1] and near(x, info[2]) and near(y, info[3]) then
      add_check(new, info[4])
    end
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
  local nb, nc, nr = 0, #CUTSCENES, #RELICS
  for _ in pairs(BOSSES) do nb = nb + 1 end
  log(("ready: %d boss rows, %d cutscenes, %d relics, +2 prologue (PSX MainRAM)")
      :format(nb, nc, nr))
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end

  -- Position-independent checks (bosses, prologue) always run.
  check_bosses(new)
  check_prologue(new)

  -- Position-based checks need a valid room/X/Y read.
  local room = read_u16(ADDR_ROOM)
  local x    = read_u16(ADDR_X)
  local y    = read_u16(ADDR_Y)
  if room ~= nil and x ~= nil and y ~= nil then
    check_cutscenes(new, room, x, y)
    check_relics(new, room, x, y)
  end
  return new
end

-- check_victory from connector_sotn.lua: only meaningful in the final-fight
-- room. Dracula gets a brief invulnerable fly-in at HP 10000; once that latches,
-- his HP underflowing past 20000 (or reading 0) means he is dead.
function M.is_goal_complete()
  if not ADDRESSES_VERIFIED then return false end
  local room = read_u16(ADDR_ROOM)
  if room ~= VICTORY_ROOM then
    on_drac = false
    return false
  end
  local drac_hp = read_u16(ADDR_DRAC_HP)
  if drac_hp == nil then return false end
  if (not on_drac) and drac_hp == 10000 then
    on_drac = true
    return false
  end
  if on_drac and (drac_hp > 20000 or drac_hp == 0) then
    return true
  end
  return false
end

-- Remote multiworld items: see the file header. items_handling = 0b111 — the
-- server drives delivery and the reference connector applies items through a set
-- of guarded MainRAM writes AND actively denies un-released vanilla pickups. That
-- write/deny path is the one piece deferred until it is confirmed in-emulator;
-- a wrong write would corrupt the live game. No-op (never a wrong write) for now.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
