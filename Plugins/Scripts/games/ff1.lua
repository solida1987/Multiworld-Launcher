-- ═══════════════════════════════════════════════════════════════════════════════
-- ff1.lua — game module for the Archipelago BizHawk connector.
--           Final Fantasy (NES, USA edition)
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the official
-- AP world worlds/ff1 (Client.py + data/locations.json, main branch). The 267-entry
-- location id list was GENERATED directly from data/locations.json (parsed, not
-- hand-copied), and the chest/NPC bit math is replicated EXACTLY from the client's
-- location_check(). Loads crash-free on any ROM; self-disables on a non-FF1
-- cartridge (no "FINAL FANTASY" PRG-ROM signature).
--
-- MEMORY MODEL (BizHawk NES domains — matches Client.py FF1Client)
-- ───────────────────────────────────────────────────────────────
--   The FF1 AP client is a BizHawkClient that reads three NES domains:
--     self.wram = "RAM"      (NES 2 KB internal work RAM; unused for checks here)
--     self.sram = "WRAM"     (the cartridge battery-backed save RAM — ALL the
--                             location flags + the title-screen guard live here)
--     self.rom  = "PRG ROM"  (cartridge program ROM — the FF identifier)
--
--   LOCATION ID == WRAM ADDRESS. worlds/ff1 stores each location's address AS its
--   AP location id (FF1Locations.get_location_name_to_address_dict returns the raw
--   address — there is NO base_id offset on locations). The client decodes each id:
--       if id <  0x200:  CHEST → index = id - 0x100,  bit mask 0x04
--       else:            NPC   → index = id - 0x200,  bit mask 0x02
--       checked when   locations_array[index] & mask != 0
--   where locations_array is the 256-byte block WRAM[0x200 .. 0x300). Because the
--   index is added back to 0x200 when reading, a CHEST id L is tested at
--   WRAM[(L-0x100)+0x200] = WRAM[L+0x100] with bit 0x04, and an NPC id L at
--   WRAM[(L-0x200)+0x200] = WRAM[L] with bit 0x02. (Client.py location_check, exact.)
--
--   GOAL: Client.py sets CLIENT_GOAL when locations_data[0xFE] & 0x02 != 0, i.e.
--         WRAM[0x2FE] & 0x02 — the "Terminated Chaos" completion event flag.
--
--   TITLE-SCREEN GUARD: status_a_location = 0x102 (WRAM). The game's first
--         character name byte is 0 on the title / character-creation screen; the
--         client refuses all reads/writes until it is non-zero. We mirror that so
--         the menu's zeroed flag array never reports phantom checks (client sets
--         its guard to 1 in that case — "neither a valid character nor the initial
--         value" — and bails). We simply gate poll()/goal on guard != 0.
--
-- WHAT THIS DOES (mirrors worlds/ff1/Client.py game_watcher → location_check)
--   • poll(): read WRAM[0x200..0x300) once, decode every wanted location id with
--     the exact chest/NPC math above → AP ids. Gated to the slot's server location
--     set and to the title-screen guard.
--   • is_goal_complete(): WRAM[0x2FE] & 0x02 — the default (and only) FF1 goal.
--   • receive_item(): NO-OP (documented). items_handling = 0b111 means the AP
--     SERVER drives item delivery; the reference client writes received items into
--     WRAM (key items, gold, consumables, weapon/armor inventory queues) gated on
--     the game's own items-obtained counter at WRAM 0x03. That write path is the
--     piece that must be confirmed in-emulator before it is wired here (a wrong
--     WRAM write corrupts the save), so it is intentionally deferred rather than
--     shipped unverified. Item delivery is handled launcher-side by the connector's
--     SYNC channel when that path is enabled. Checks + goal flow regardless.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "ff1"

local ADDRESSES_VERIFIED = true   -- id list generated from worlds/ff1 source

-- ── Memory domains (BizHawk NES) ──────────────────────────────────────────────
local WRAM = "WRAM"        -- cartridge save RAM (client self.sram) — flags + guard
local PRGROM = "PRG ROM"   -- cartridge program ROM (client self.rom) — FF identifier

-- ── Addresses / constants (worlds/ff1/Client.py) ──────────────────────────────
local ROM_NAME_LOCATION   = 0x07FFE3   -- PRG ROM: "FINAL FANTASY" identifier (13 bytes)
local ROM_NAME            = "FINAL FANTASY"
local LOCATIONS_START     = 0x200      -- WRAM: locations_array_start
local LOCATIONS_LENGTH    = 0x100      -- WRAM: locations_array_length (256 bytes)
local STATUS_A_LOCATION   = 0x102      -- WRAM: char-1 status byte (title-screen guard)
local GOAL_INDEX          = 0xFE       -- locations_data[0xFE] & 0x02 == game complete
local GOAL_MASK           = 0x02
local CHEST_FLAG          = 0x04       -- bit set when a chest location is taken
local NPC_FLAG            = 0x02       -- bit set when an NPC location is given

-- ── Location id list (GENERATED from worlds/ff1/data/locations.json) ──────────
-- Each entry IS the AP location id AND the in-game address. 267 entries. The
-- chest/NPC index + bit mask is computed from the id at poll time, exactly as the
-- client does — no per-id table needed beyond membership.
local LOC_IDS = {
  257,258,259,260,261,262,263,264,265,266,267,268,
  269,270,271,272,273,274,275,276,277,278,279,280,
  281,282,283,284,285,286,287,288,289,290,291,292,
  293,294,295,296,297,298,299,300,301,302,303,304,
  305,306,307,308,309,310,311,312,313,314,315,316,
  317,318,319,320,321,322,323,324,325,326,327,328,
  329,330,331,332,333,334,335,336,337,338,339,340,
  341,342,343,344,345,346,347,348,349,350,351,352,
  353,354,355,356,357,358,359,360,361,362,363,364,
  365,366,367,368,369,370,371,372,373,374,375,376,
  377,378,379,380,381,382,383,384,385,386,387,388,
  389,390,391,392,393,394,395,396,397,398,399,400,
  401,402,403,404,405,406,407,408,409,410,411,412,
  413,414,415,416,417,418,419,420,421,422,423,424,
  425,426,427,428,429,430,431,432,433,434,435,436,
  437,438,439,440,441,442,443,445,446,447,448,449,
  450,451,452,453,454,455,456,457,458,459,460,461,
  462,463,464,465,466,467,468,469,470,471,472,473,
  474,475,476,477,478,479,480,481,482,483,484,485,
  486,487,488,489,490,491,492,493,494,495,496,497,
  498,499,500,501,502,503,504,505,506,507,508,509,
  510,513,516,518,519,520,521,522,525,527,529,530,
  531,533,767,
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil     -- cached FF identifier result
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[ff1] " .. tostring(msg)) end
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

-- ── ROM identity: the FF1 ROM carries "FINAL FANTASY" at PRG ROM 0x07FFE3 ─────
-- This survives FFR randomization (the randomizer only ever expands MMC1→MMC3 and
-- shuffles data; the title identifier stays), so it is the right name-independent
-- detector for both vanilla and AP-randomized FF1 ROMs — exactly what the client's
-- validate_rom() checks.
local function rom_is_ff1()
  if rom_ok ~= nil then return rom_ok end
  for i = 1, #ROM_NAME do
    local b = read_u8(ROM_NAME_LOCATION + i - 1, PRGROM)
    if b == nil then return false end          -- not readable yet; retry next poll
    if b ~= string.byte(ROM_NAME, i) then
      rom_ok = false
      log("non-FF1 ROM (no 'FINAL FANTASY' PRG-ROM signature) — detection idle")
      return false
    end
  end
  rom_ok = true
  log("FF1 ROM verified ('FINAL FANTASY' signature present)")
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

-- ── Locations array (read once per poll) ──────────────────────────────────────
-- locations_data[i] == WRAM[0x200 + i], i in [0, 0x100).
local loc_data = {}     -- index -> byte value, refreshed each poll

local function refresh_loc_data()
  for i = 0, LOCATIONS_LENGTH - 1 do
    loc_data[i] = read_u8(LOCATIONS_START + i, WRAM)
  end
end

-- A location id is "checked" exactly as Client.py decodes it.
local function id_checked(ap_id)
  local index, flag
  if ap_id < 0x200 then
    index = ap_id - 0x100   -- chest
    flag  = CHEST_FLAG
  else
    index = ap_id - 0x200   -- NPC
    flag  = NPC_FLAG
  end
  local byte = loc_data[index]
  if byte == nil then return false end
  return bit_and(byte, flag) ~= 0
end

-- ── Detection gate ────────────────────────────────────────────────────────────
-- status_a (WRAM 0x102) is 0 on the title/character-creation screen; the client
-- refuses all reads while it is 0. Mirror that so the zeroed menu flag array can
-- never report phantom checks.
local function in_game()
  local s = read_u8(STATUS_A_LOCATION, WRAM)
  return s ~= nil and s ~= 0
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
  log("ready: " .. #LOC_IDS .. " location ids")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_ff1() then return new end
  if not in_game() then return new end
  refresh_loc_data()
  for _, ap_id in ipairs(LOC_IDS) do
    if not reported[ap_id] and wanted(ap_id) and id_checked(ap_id) then
      reported[ap_id] = true
      new[#new + 1] = ap_id
    end
  end
  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_ff1() then return false end
  if not in_game() then return false end
  -- goal flag lives in the same locations array; read the one byte we need.
  local byte = read_u8(LOCATIONS_START + GOAL_INDEX, WRAM)
  if byte == nil then return false end
  return bit_and(byte, GOAL_MASK) ~= 0
end

-- Remote items: see the file header. items_handling = 0b111 — the AP server drives
-- ALL item delivery (the reference client writes them into WRAM gated on the game's
-- own items-obtained counter at WRAM 0x03). That guarded-write path is deferred
-- here until it can be confirmed in-emulator; a wrong WRAM write would corrupt the
-- save, so this is a no-op (never a wrong write) rather than shipped unverified.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
