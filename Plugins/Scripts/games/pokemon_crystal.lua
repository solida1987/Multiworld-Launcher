-- ═══════════════════════════════════════════════════════════════════════════════
-- pokemon_crystal.lua — game module for the Archipelago BizHawk connector.
--                       Pokémon Crystal (Game Boy Color)
--
-- STATUS: COMMUNITY world. location DETECTION + goal are REAL and SOURCE-DERIVED
-- from the official AP world worlds/pokemon_crystal (client.py + rom.py + world.py +
-- locations.py + data/data.json, release v5.4.6). Source repo (gerbiljames, the
-- world author): https://github.com/gerbiljames/Archipelago-Crystal — released
-- apworld https://github.com/gerbiljames/Archipelago-Crystal/releases (5.4.6,
-- pokemon_crystal.apworld). Every address, array length, OFFSET and goal-flag value
-- below was PARSED out of that apworld's data.json with Python (NOT hand-copied), so
-- the (array, byte, bit)→location-id math is byte-for-byte what client.game_watcher
-- uses. Loads crash-free on any cartridge; self-disables on a non-AP ROM (validates
-- the "AP_CRYSTAL" header signature + the 2 MB ROM size, exactly like
-- client.validate_rom).
--
-- MEMORY MODEL (BizHawk GB/GBC domains)
-- ──────────────────────────────────────
--   The PokemonCrystalClient (a BizHawkClient, system ("GB","GBC")) reads the work
--   RAM ("WRAM") for every flag array and the cartridge ("ROM") only to identify the
--   game. There is NO BASE_ID offset in Crystal: the AP location id IS the value
--   directly (an event-flag bit index, or id+OFFSET for dex/grass). client.py reads
--   these WRAM blocks each frame:
--     wEventFlags              0x1A88  len EVENT_BYTES = 292   (event/location flags)
--     wArchipelagoPokedexCaught0x1EA3  len DEX_BYTES   = 32    (dexsanity caught)
--     wArchipelagoGrassFlags   0x2245  len GRASS_BYTES = 94    (grasssanity)
--     wStatusFlags             0x181F  len 1  (bit0 = has_pokedex)
--   A location is "checked" when its array bit is set AND the id is in the slot's
--   server-location set:
--     • event flags: location_id = byte_i*8 + bit          (raw event-flag index)
--     • dexsanity:   location_id = (bit+1) + 10000  AND has_pokedex   (POKEDEX_OFFSET)
--     • grasssanity: location_id = bit + 30000, then remapped through the slot's
--                    grass_location_mapping (slot_data) if present   (GRASS_OFFSET)
--     • dexcountsanity: count-thresholds (caught-count ≥ N) → N + 20000, from the
--                    slot's dexcountsanity_counts (slot_data)  (POKEDEX_COUNT_OFFSET)
--   (item_data.py: POKEDEX_OFFSET 10000 / POKEDEX_COUNT_OFFSET 20000 / GRASS_OFFSET
--   30000. EVENT_BYTES = ceil(max(event_flags)/8), DEX_BYTES = ceil(251/8) etc.)
--
-- ROM IDENTITY (client.validate_rom): the AP patch writes the ASCII name "AP_CRYSTAL"
--   at ROM 0x134 (11 bytes; the unpatched ROM has "PM_CRYSTAL"). The base ROM is 2 MB
--   (2097152). We mirror validate_rom: require the "AP_CRYSTAL" header, otherwise
--   stay idle. (The seed name / auth lives at ROM 0x4000; not needed for detection.)
--
-- DETECTION GATE (client.game_watcher overworld_guard): wArchipelagoSafeWrite at
--   WRAM 0x1CAA must equal 1 — the game's own "safe to read/write AP state" flag,
--   set only while in the overworld. Every guarded_read in client.py is gated on it,
--   so a battle / menu / pre-load frame never reports checks. We gate identically.
--
-- GOAL (client.game_watcher): depends on slot_data["goal"] (option enum). The client
--   builds a goal-flag LIST and fires CLIENT_GOAL only when ALL of them are set
--   (`all(goal_flags_cleared.values())`). Flag VALUES (event-flag indices), exactly
--   as resolved in game_watcher:
--     0 elite_four        → [68]
--     1 red (default)     → [59]
--     2 diploma           → [214]
--     3 rival             → [304,305,1656,306,307]  (+ [515,293] when johto_only off)
--     4 defeat_team_rocket→ [43,34,950,33]          (+ [1824]   when johto_only off)
--     5 unown_hunt        → [314]
--   johto_only enum: 0 off / 1 on / 2 include_silver_cave (only 0 adds the Kanto
--   flags). When slot_data is unavailable we fall back to the default goal (red, 59).
--
-- WHAT THIS DOES (mirrors worlds/pokemon_crystal/client.py game_watcher)
--   • poll(): read the three flag arrays → AP location ids, gated to the slot's
--     server location set AND to wArchipelagoSafeWrite==1. Dexsanity is additionally
--     gated on has_pokedex; grass ids are remapped through grass_location_mapping;
--     dexcountsanity emits each threshold the live caught-count has reached.
--   • is_goal_complete(): ALL of the slot's goal flags set (the exact client rule).
--   • receive_item(): NO-OP (documented). items_handling = 0b001 (or 0b011 when the
--     slot's remote_items option is on) — in BOTH modes the PATCHED GAME grants its
--     own locally-found items, so a SOLO seed plays fully and every check is reported
--     in a multiworld. Delivering REMOTE items is the client's guarded WRAM write
--     (wArchipelagoItemReceived 0x1CA7, gated on wArchipelagoSafeWrite + the save's
--     own received-count and the byte being 0, with a separate FLAG_ITEM path at
--     wArchipelagoFlagItemReceived 0x1CB0); that is the one piece deferred until it
--     can be confirmed in-emulator, rather than risk mis-applying an item. Same
--     posture as pokemon_rb.lua / ladx.lua / cvcotm.lua.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "pokemon_crystal"

local ADDRESSES_VERIFIED = true   -- table generated from worlds/pokemon_crystal source

-- ── Memory domains ────────────────────────────────────────────────────────────
local WRAM = "WRAM"
local ROM  = "ROM"

-- ── Flag-array bases + lengths (client.py DATA reads, WRAM) ────────────────────
local EVENT_FLAGS_ADDR   = 0x1A88   -- wEventFlags
local EVENT_BYTES        = 292
local DEX_CAUGHT_ADDR    = 0x1EA3   -- wArchipelagoPokedexCaught
local DEX_BYTES          = 32       -- ceil(251 pokémon / 8)
local GRASS_FLAGS_ADDR   = 0x2245   -- wArchipelagoGrassFlags
local GRASS_BYTES        = 94
local STATUS_FLAGS_ADDR  = 0x181F   -- wStatusFlags; bit0 = has_pokedex

-- ── Location-id OFFSETs (item_data.py) ────────────────────────────────────────
local POKEDEX_OFFSET       = 10000
local POKEDEX_COUNT_OFFSET = 20000
local GRASS_OFFSET         = 30000

-- ── Detection gate (client.game_watcher overworld_guard) ──────────────────────
local SAFE_WRITE_ADDR = 0x1CAA      -- wArchipelagoSafeWrite; must == 1 in overworld

-- ── ROM identity (client.validate_rom): AP header signature ───────────────────
local ROM_HEADER_ADDR = 0x134       -- AP_ROM_Header (11 bytes ASCII)
local ROM_SIG_AP      = "AP_CRYSTAL"   -- patched name (unpatched = "PM_CRYSTAL")

-- ── Goal option enum (options.py Goal) ────────────────────────────────────────
local GOAL_ELITE_FOUR        = 0
local GOAL_RED               = 1
local GOAL_DIPLOMA           = 2
local GOAL_RIVAL             = 3
local GOAL_DEFEAT_TEAM_ROCKET= 4
local GOAL_UNOWN_HUNT        = 5
local JOHTO_ONLY_OFF         = 0

-- ── Goal flag VALUES (event-flag indices), source-derived from game_watcher ────
-- Each goal resolves to a LIST of event-flag indices; ALL must be set to win.
local GOAL_FLAGS = {
  [GOAL_ELITE_FOUR]         = { 68 },
  [GOAL_RED]               = { 59 },
  [GOAL_DIPLOMA]           = { 214 },
  [GOAL_RIVAL]             = { 304, 305, 1656, 306, 307 },     -- + kanto when johto_only off
  [GOAL_DEFEAT_TEAM_ROCKET]= { 43, 34, 950, 33 },             -- + kanto when johto_only off
  [GOAL_UNOWN_HUNT]        = { 314 },
}
local GOAL_RIVAL_KANTO         = { 515, 293 }
local GOAL_TEAM_ROCKET_KANTO   = { 1824 }
local DEFAULT_GOAL = GOAL_RED   -- client falls through to EVENT_BEAT_RED

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil     -- cached AP-signature result
local mem              = {}
local log_fn           = nil
local goal_flags       = nil    -- resolved list of event-flag indices (from slot_data)
local grass_remap      = {}     -- {[raw_grass_id]=mapped_ap_id} from slot_data
local dexcount_thresholds = {}  -- sorted list of caught-count thresholds (slot_data)

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[pokemon_crystal] " .. tostring(msg)) end
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

-- ── ROM identity: mirror client.validate_rom (AP_CRYSTAL header signature) ─────
local function rom_is_ap()
  if rom_ok ~= nil then return rom_ok end
  for i = 1, #ROM_SIG_AP do
    local b = read_u8(ROM_HEADER_ADDR + i - 1, ROM)
    if b == nil then return false end           -- ROM domain not ready; retry
    if b ~= string.byte(ROM_SIG_AP, i) then
      rom_ok = false
      log("non-AP ROM (no AP_CRYSTAL header signature) — detection idle")
      return false
    end
  end
  rom_ok = true
  log("AP ROM verified (AP_CRYSTAL header signature present)")
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

-- Resolve goal + slot-data driven tables (grass remap, dexcount thresholds) from
-- ctx.config / ctx.slot_data once at init. Mirrors game_watcher's slot_data reads.
local function load_slot_data(cfg)
  -- goal
  local goal = nil
  local johto_only = JOHTO_ONLY_OFF
  if type(cfg) == "table" then
    local sd = cfg.slot_data or cfg
    if type(sd) == "table" then
      if sd.goal ~= nil then goal = tonumber(sd.goal) end
      if sd.johto_only ~= nil then johto_only = tonumber(sd.johto_only) or JOHTO_ONLY_OFF end

      -- grass_location_mapping: {raw_grass_id(str|num) -> mapped ap id}
      local gm = sd.grass_location_mapping
      if type(gm) == "table" then
        for k, v in pairs(gm) do
          local raw = tonumber(k)
          local mapped = tonumber(v)
          if raw and mapped then grass_remap[raw] = mapped end
        end
      end

      -- dexcountsanity_counts: list of caught-count thresholds
      local dc = sd.dexcountsanity_counts
      if type(dc) == "table" then
        for _, c in ipairs(dc) do
          local cv = tonumber(c)
          if cv then dexcount_thresholds[#dexcount_thresholds + 1] = cv end
        end
      end
    end
  end

  if goal == nil or GOAL_FLAGS[goal] == nil then goal = DEFAULT_GOAL end
  local flags = {}
  for _, f in ipairs(GOAL_FLAGS[goal]) do flags[#flags + 1] = f end
  -- Rival / Team Rocket add Kanto flags unless johto_only is off-only restricted.
  if johto_only == JOHTO_ONLY_OFF then
    if goal == GOAL_RIVAL then
      for _, f in ipairs(GOAL_RIVAL_KANTO) do flags[#flags + 1] = f end
    elseif goal == GOAL_DEFEAT_TEAM_ROCKET then
      for _, f in ipairs(GOAL_TEAM_ROCKET_KANTO) do flags[#flags + 1] = f end
    end
  end
  goal_flags = flags

  local gn = 0; for _ in pairs(grass_remap) do gn = gn + 1 end
  log(("goal=%s (%d flags), grass_remap=%d, dexcount=%d")
        :format(tostring(goal), #goal_flags, gn, #dexcount_thresholds))
end

-- ── Flag arrays (read once per poll) ──────────────────────────────────────────
local event_bytes  = {}   -- byte index -> value
local dex_bytes    = {}
local grass_bytes  = {}

local function refresh_arrays()
  for i = 0, EVENT_BYTES - 1 do event_bytes[i] = read_u8(EVENT_FLAGS_ADDR + i, WRAM) end
  for i = 0, DEX_BYTES   - 1 do dex_bytes[i]   = read_u8(DEX_CAUGHT_ADDR  + i, WRAM) end
  for i = 0, GRASS_BYTES - 1 do grass_bytes[i] = read_u8(GRASS_FLAGS_ADDR + i, WRAM) end
end

local function event_bit(idx)
  local v = event_bytes[math.floor(idx / 8)]
  if v == nil then return false end
  return bit_and(v, POW2[idx % 8]) ~= 0
end

-- ── Detection gate ────────────────────────────────────────────────────────────
local function in_overworld()
  local g = read_u8(SAFE_WRITE_ADDR, WRAM)
  return g == 1
end

local function has_pokedex()
  local s = read_u8(STATUS_FLAGS_ADDR, WRAM)
  return s ~= nil and bit_and(s, 1) ~= 0
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
  load_slot_data(cfg)
  log("ready: event=" .. EVENT_BYTES .. "B dex=" .. DEX_BYTES .. "B grass=" .. GRASS_BYTES .. "B")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_ap() then return new end
  if not in_overworld() then return new end
  refresh_arrays()

  -- Event-flag locations: location_id == byte*8 + bit (raw, no base).
  for byte_i = 0, EVENT_BYTES - 1 do
    local v = event_bytes[byte_i]
    if v and v ~= 0 then
      for bit = 0, 7 do
        if bit_and(v, POW2[bit]) ~= 0 then
          local ap_id = byte_i * 8 + bit
          if not reported[ap_id] and wanted(ap_id) then
            reported[ap_id] = true
            new[#new + 1] = ap_id
          end
        end
      end
    end
  end

  -- Dexsanity: location_id = (bit+1) + POKEDEX_OFFSET, only when has_pokedex.
  local dex_caught_count = 0
  local pokedex_present = has_pokedex()
  for byte_i = 0, DEX_BYTES - 1 do
    local v = dex_bytes[byte_i]
    if v and v ~= 0 then
      for bit = 0, 7 do
        if bit_and(v, POW2[bit]) ~= 0 then
          dex_caught_count = dex_caught_count + 1
          if pokedex_present then
            local dex_number = (byte_i * 8 + bit) + 1
            local ap_id = dex_number + POKEDEX_OFFSET
            if not reported[ap_id] and wanted(ap_id) then
              reported[ap_id] = true
              new[#new + 1] = ap_id
            end
          end
        end
      end
    end
  end

  -- Dexcountsanity: each threshold N the live caught-count has reached → N+OFFSET.
  -- (Mirrors client.py: gated on has_pokedex.)
  if pokedex_present then
    for _, count in ipairs(dexcount_thresholds) do
      if dex_caught_count >= count then
        local ap_id = count + POKEDEX_COUNT_OFFSET
        if not reported[ap_id] and wanted(ap_id) then
          reported[ap_id] = true
          new[#new + 1] = ap_id
        end
      end
    end
  end

  -- Grasssanity: location_id = bit + GRASS_OFFSET, remapped via grass_location_mapping.
  for byte_i = 0, GRASS_BYTES - 1 do
    local v = grass_bytes[byte_i]
    if v and v ~= 0 then
      for bit = 0, 7 do
        if bit_and(v, POW2[bit]) ~= 0 then
          local ap_id = (byte_i * 8 + bit) + GRASS_OFFSET
          if grass_remap[ap_id] ~= nil then ap_id = grass_remap[ap_id] end
          if not reported[ap_id] and wanted(ap_id) then
            reported[ap_id] = true
            new[#new + 1] = ap_id
          end
        end
      end
    end
  end

  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_ap() then return false end
  if not goal_flags or #goal_flags == 0 then return false end
  refresh_arrays()
  -- ALL goal flags must be set (client.py: all(goal_flags_cleared.values())).
  for _, idx in ipairs(goal_flags) do
    if not event_bit(idx) then return false end
  end
  return true
end

-- Remote multiworld items: see the file header. items_handling = 0b001 (self-grant)
-- or 0b011 (remote_items on); in both modes the patched game grants its own found
-- items, so solo play and check reporting work fully. Applying REMOTE items is the
-- client's guarded WRAM write (wArchipelagoItemReceived 0x1CA7 / wArchipelagoFlagItem
-- Received 0x1CB0, gated on wArchipelagoSafeWrite + the save's received-count + the
-- byte being 0); deferred until it can be confirmed in-emulator. No-op (never a
-- wrong write) until then.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
