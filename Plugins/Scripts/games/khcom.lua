-- ═══════════════════════════════════════════════════════════════════════════════
-- khcom.lua — game module for the Archipelago BizHawk connector.
--             Kingdom Hearts: Chain of Memories (GBA)
--
-- SOURCE: gaithernOrg/ArchipelagoKHCOM  (community apworld, world id / game string
--         "Kingdom Hearts Chain of Memories"). Detection + goal are GENERATED and
--         SOURCE-DERIVED from the world's own BizHawk Lua client
--         (data/lua/connector_khcom.lua) and worlds/khcom/Locations.py — the
--         location table below was produced by PARSING connector_khcom.lua's
--         define_journal_bit_location_ids() with Python (not hand-copied), and
--         cross-checked 1:1 against Locations.py (152 journal ids + Final
--         Marluxia = 153, 0 gaps). Loads crash-free on any ROM; self-disables
--         until the game is actually being played.
--
-- MEMORY MODEL (BizHawk GBA "EWRAM" domain)
-- ──────────────────────────────────────────
--   KHCOM is NOT an AP-patched ROM. The player loads a VANILLA US cartridge and
--   the community client drives everything live from GBA work RAM. Every address
--   in the source is an absolute GBA bus address 0x020xxxxx; in BizHawk's EWRAM
--   domain the offset is (addr - 0x02000000).
--
--   • LOCATIONS — "journal" completion bits. 20 bytes at EWRAM 0x39CE4..0x39CF7
--     each hold 8 location flags (LSB-first). The source maps every (byte,bit)
--     to a FULL AP location id (e.g. 0x39CE4 bit0 → 2670107). A location is
--     checked when its bit is set. (connector_khcom.lua check_journal():
--     toBits(readbyte(K))[i]==1 → send<id>; toBits is least-significant-first,
--     so table index i == bit (i-1).)
--   • GOAL / Final Marluxia — a position+floor test, not a journal bit. The
--     client (check_final_marluxia) reports location 2679999 and the run's
--     victory when, on floor 13 (floor byte 0x39BBE == 12), the two signed-16
--     fields at EWRAM 0x31F50 and 0x31F52 read 0 and 2237. Both values are
--     non-negative so an unsigned 16-bit read compares identically.
--
-- WHAT THIS DOES (mirrors connector_khcom.lua check_journal + check_final_marluxia)
--   • poll(): scan the 20 journal bytes → AP location ids (gated to the slot's
--     server location set and to an in-game gate so the title screen never
--     reports), plus the Final-Marluxia location when its condition holds.
--   • is_goal_complete(): the Final-Marluxia condition (the run's win — the
--     "Victory" item is locked to that location in the apworld).
--   • receive_item(): NO-OP (documented). items_handling = 0b111 (full remote):
--     the AP SERVER drives ALL item delivery, and the reference client applies
--     received items by an intricate live card-array path (decode the item name,
--     pick/inject specific battle-card values into 0x0203A080, rewrite deck
--     pointers, open card packs, grant gold map cards / world unlocks). That
--     guarded multi-write path depends on per-frame deck-pointer bookkeeping and
--     must be confirmed in-emulator before it is wired, so it is intentionally
--     left out rather than shipped unverified (a wrong write would corrupt the
--     deck/save). Checks + goal flow regardless; a solo seed still reports every
--     location. See the C# plugin header for the full status note.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "khcom"

local ADDRESSES_VERIFIED = true   -- table generated from worlds/khcom source

-- ── Memory domain ─────────────────────────────────────────────────────────────
local EWRAM = "EWRAM"

-- ── Addresses (connector_khcom.lua, minus the 0x02000000 GBA bus base) ────────
local JOURNAL_START   = 0x39CE4   -- EWRAM: first of 20 journal flag bytes
local JOURNAL_END     = 0x39CF7   -- EWRAM: last journal flag byte (inclusive)
local FLOOR_ADDR      = 0x39BBE   -- EWRAM: floor byte (in-game floor = byte + 1)
local PLAYTIME_ADDR   = 0x39D8C   -- EWRAM: u24 play-time counter (gate)
local MARLUXIA_A_ADDR = 0x31F50   -- EWRAM: Final Marluxia pos field A (== 0)
local MARLUXIA_B_ADDR = 0x31F52   -- EWRAM: Final Marluxia pos field B (== 2237)
local MARLUXIA_A_VAL  = 0
local MARLUXIA_B_VAL  = 2237
local FINAL_FLOOR     = 13         -- floor byte value 12 → in-game floor 13
local FINAL_MARLUXIA_ID = 2679999  -- AP location id + goal

-- ── Location table (GENERATED from connector_khcom.lua / Locations.py) ────────
-- ap_id -> { EWRAM journal-byte offset, bit index (0..7, LSB-first) }. 152
-- entries; Final Marluxia (2679999) is handled separately (position+floor test).
local LOC = {
  [2670001]={0x39CF2,7},[2670002]={0x39CE6,2},[2670003]={0x39CE6,3},[2670004]={0x39CE6,4},
  [2670005]={0x39CE6,6},[2670006]={0x39CE6,5},[2670007]={0x39CE6,1},[2670008]={0x39CF6,6},
  [2670009]={0x39CF5,1},[2670010]={0x39CF5,3},[2670101]={0x39CE7,6},[2670102]={0x39CE8,6},
  [2670103]={0x39CE7,7},[2670104]={0x39CE7,4},[2670105]={0x39CE7,5},[2670106]={0x39CF5,0},
  [2670107]={0x39CE4,0},[2670108]={0x39CE4,4},[2670109]={0x39CE6,7},[2670110]={0x39CF5,7},
  [2670111]={0x39CF4,0},[2670112]={0x39CF2,3},[2670201]={0x39CF5,5},[2670202]={0x39CF4,1},
  [2670203]={0x39CE9,2},[2670204]={0x39CE9,5},[2670205]={0x39CE9,6},[2670206]={0x39CE9,7},
  [2670207]={0x39CE9,3},[2670208]={0x39CE9,4},[2670209]={0x39CE4,5},[2670210]={0x39CF2,5},
  [2670301]={0x39CF3,7},[2670302]={0x39CE8,0},[2670303]={0x39CEA,2},[2670304]={0x39CEA,1},
  [2670305]={0x39CEA,0},[2670306]={0x39CE4,6},[2670307]={0x39CF6,7},[2670308]={0x39CF3,6},
  [2670309]={0x39CF6,5},[2670401]={0x39CF3,4},[2670402]={0x39CEB,7},[2670403]={0x39CEB,6},
  [2670404]={0x39CE5,1},[2670405]={0x39CF2,4},[2670406]={0x39CE7,0},[2670407]={0x39CF6,2},
  [2670501]={0x39CF5,4},[2670502]={0x39CF3,0},[2670503]={0x39CEA,3},[2670504]={0x39CEA,4},
  [2670505]={0x39CEA,7},[2670506]={0x39CEB,0},[2670507]={0x39CEB,1},[2670508]={0x39CEA,6},
  [2670509]={0x39CE4,7},[2670510]={0x39CF7,1},[2670511]={0x39CF6,0},[2670601]={0x39CF3,2},
  [2670602]={0x39CEB,4},[2670603]={0x39CEB,2},[2670604]={0x39CEB,5},[2670605]={0x39CEB,3},
  [2670606]={0x39CF5,2},[2670607]={0x39CE5,0},[2670608]={0x39CE4,1},[2670701]={0x39CF3,1},
  [2670702]={0x39CEC,0},[2670703]={0x39CEC,2},[2670704]={0x39CEC,3},[2670705]={0x39CEC,1},
  [2670706]={0x39CE5,2},[2670707]={0x39CF5,6},[2670801]={0x39CF3,3},[2670802]={0x39CED,0},
  [2670803]={0x39CEC,5},[2670804]={0x39CEC,6},[2670805]={0x39CEC,7},[2670806]={0x39CE5,3},
  [2670807]={0x39CF6,3},[2670901]={0x39CF4,2},[2670902]={0x39CED,2},[2670903]={0x39CED,4},
  [2670904]={0x39CED,3},[2670905]={0x39CED,1},[2670906]={0x39CE5,4},[2670907]={0x39CE4,2},
  [2670908]={0x39CE7,2},[2670909]={0x39CF6,4},[2671001]={0x39CE7,1},[2671002]={0x39CF6,1},
  [2671003]={0x39CF3,5},[2671004]={0x39CEE,1},[2671005]={0x39CED,7},[2671006]={0x39CED,6},
  [2671007]={0x39CEE,3},[2671008]={0x39CEE,0},[2671009]={0x39CEE,2},[2671010]={0x39CE9,0},
  [2671011]={0x39CED,5},[2671012]={0x39CF7,2},[2671013]={0x39CE5,5},[2671014]={0x39CF7,3},
  [2671101]={0x39CF7,0},[2671102]={0x39CE5,6},[2671201]={0x39CF4,3},[2671202]={0x39CE8,3},
  [2671203]={0x39CE8,1},[2671204]={0x39CE8,2},[2671205]={0x39CE8,5},[2671206]={0x39CE8,4},
  [2671207]={0x39CE5,7},[2671208]={0x39CE4,3},[2671209]={0x39CF2,6},[2671210]={0x39CF4,4},
  [2671211]={0x39CF7,4},[2671301]={0x39CE9,1},[2671302]={0x39CE6,0},[2671303]={0x39CF4,6},
  [2671304]={0x39CF4,7},[2671401]={0x39CF1,0},[2671402]={0x39CEF,5},[2671403]={0x39CF0,4},
  [2671404]={0x39CEF,6},[2671405]={0x39CF0,0},[2671406]={0x39CF1,7},[2671407]={0x39CEF,0},
  [2671408]={0x39CEF,4},[2671409]={0x39CF2,0},[2671410]={0x39CF2,2},[2671411]={0x39CF1,1},
  [2671412]={0x39CF1,2},[2671413]={0x39CEF,7},[2671414]={0x39CF0,6},[2671415]={0x39CEF,2},
  [2671416]={0x39CEE,6},[2671417]={0x39CF1,5},[2671418]={0x39CF0,7},[2671419]={0x39CEF,3},
  [2671420]={0x39CEE,7},[2671421]={0x39CF0,3},[2671422]={0x39CF0,2},[2671423]={0x39CF0,1},
  [2671424]={0x39CEE,4},[2671425]={0x39CEE,5},[2671426]={0x39CF2,1},[2671427]={0x39CF1,6},
  [2671428]={0x39CF0,5},[2671429]={0x39CF1,4},[2671430]={0x39CF1,3},[2671431]={0x39CEF,1},
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[khcom] " .. tostring(msg)) end
end

-- ── Memory API (resolved at init; 2-arg domain form + current-domain fallback) ─
local function resolve_memory_api()
  if not memory then return false end
  mem.read_u8     = memory.read_u8     or memory.readbyte
  mem.read_u16_le = memory.read_u16_le
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

local function read_u16(addr, domain)
  -- Prefer a real u16 reader; fall back to two byte reads (LE) so the module
  -- works on any BizHawk memory API shape (and the mock harness).
  if mem.read_u16_le then
    local ok, v = pcall(mem.read_u16_le, addr, domain)
    if ok and type(v) == "number" then return v end
    ok, v = pcall(mem.read_u16_le, addr)
    if ok and type(v) == "number" then return v end
  end
  local lo = read_u8(addr, domain)
  local hi = read_u8(addr + 1, domain)
  if lo == nil or hi == nil then return nil end
  return lo + hi * 256
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

-- ── Journal flag array (read once per poll) ───────────────────────────────────
local journal = {}     -- byte offset -> value, refreshed each poll

local function refresh_journal()
  for off = JOURNAL_START, JOURNAL_END do
    journal[off] = read_u8(off, EWRAM)
  end
end

local function journal_bit(off, bit)
  local byte = journal[off]
  if byte == nil then return false end
  return bit_and(byte, POW2[bit]) ~= 0
end

-- ── Detection gate ────────────────────────────────────────────────────────────
-- The connector's own loop gates real work on play-time > 3 (a fresh boot /
-- title screen sits at 0..1). Mirror that so journal garbage on the title
-- screen is never reported. play-time is a u24 LE counter.
local function in_game()
  local b0 = read_u8(PLAYTIME_ADDR, EWRAM)
  local b1 = read_u8(PLAYTIME_ADDR + 1, EWRAM)
  local b2 = read_u8(PLAYTIME_ADDR + 2, EWRAM)
  if b0 == nil or b1 == nil or b2 == nil then return false end
  local playtime = b0 + b1 * 256 + b2 * 65536
  return playtime > 3
end

-- ── Final Marluxia (goal + location 2679999) ──────────────────────────────────
local function final_marluxia_done()
  local floor_byte = read_u8(FLOOR_ADDR, EWRAM)
  if floor_byte == nil or (floor_byte + 1) ~= FINAL_FLOOR then return false end
  local a = read_u16(MARLUXIA_A_ADDR, EWRAM)
  local b = read_u16(MARLUXIA_B_ADDR, EWRAM)
  return a == MARLUXIA_A_VAL and b == MARLUXIA_B_VAL
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
  log("ready: " .. n .. " journal location flags + Final Marluxia")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not in_game() then return new end
  refresh_journal()
  for ap_id, where in pairs(LOC) do
    if not reported[ap_id] and wanted(ap_id) and journal_bit(where[1], where[2]) then
      reported[ap_id] = true
      new[#new + 1] = ap_id
    end
  end
  -- Final Marluxia: a position/floor test rather than a journal bit.
  if not reported[FINAL_MARLUXIA_ID] and wanted(FINAL_MARLUXIA_ID)
     and final_marluxia_done() then
    reported[FINAL_MARLUXIA_ID] = true
    new[#new + 1] = FINAL_MARLUXIA_ID
  end
  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not in_game() then return false end
  return final_marluxia_done()
end

-- Remote multiworld items: see the file header. items_handling = 0b111 (full
-- remote) means the AP server drives item delivery; the reference client applies
-- received items through an intricate live battle-card/deck-pointer injection
-- path that must be confirmed in-emulator before wiring. No-op (never a wrong
-- write) until then. Checks + goal are unaffected.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
