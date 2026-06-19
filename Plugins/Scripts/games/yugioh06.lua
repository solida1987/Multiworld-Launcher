-- ═══════════════════════════════════════════════════════════════════════════════
-- yugioh06.lua — game module for the Archipelago BizHawk connector.
--   Yu-Gi-Oh! Ultimate Masters: World Championship Tournament 2006 (GBA)
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the official
-- AP world worlds/yugioh06 (client_bh.py + locations.py + __init__.py + rom.py,
-- main branch). The 165-entry location table was GENERATED directly by parsing
-- locations.py (Bonuses / Limited_Duels / Theme_Duels / Campaign_Opponents /
-- special / Required_Cards) together with the world's id math, NOT hand-copied,
-- so it is exact. Loads crash-free on any ROM; self-disables on a non-AP/non-YGO06
-- cartridge.
--
-- MEMORY MODEL (BizHawk GBA domains — mGBA core: "EWRAM" + "ROM")
-- ───────────────────────────────────────────────────────────────
--   The yugioh06 AP client (a BizHawkClient) reads GBA work RAM ("EWRAM") and
--   the cartridge ("ROM"). game_watcher() reads exactly these regions:
--     EWRAM 0x0    , 8  -> game-state string, must equal "YWCT2006" (save created)
--     EWRAM 0x52E8 , 32 -> LOCATION flag array (the "checked" bits)
--     EWRAM 0x5308 , 32 -> received-items bitmap   (item delivery; see receive_item)
--     EWRAM 0x5325 , 1  -> amount of money-items already applied
--     EWRAM 0x6C38 , 4  -> money (DP)
--   ROM identity (validate_rom):
--     ROM 0xA0 , 11 -> internal game name "YUGIOHWCT06"  (AP/YGO06 signature)
--     ROM 0x30 , 32 -> slot name (written by the patch; informational here)
--
-- LOCATION / ID MATH (replicates client_bh.py game_watcher exactly)
-- ─────────────────────────────────────────────────────────────────
--   For each set bit in the 32-byte LOCATION array:
--     flag_id     = byte_index*8 + bit_index            (LSB-first within a byte)
--     location_id = flag_id + 5730001                   (BASE_ID + 1 + flag_id)
--   A location is reported only when location_id ∈ the slot's server_locations.
--   (World side: start_id = 5730000; location_name_to_id[name] = value + 5730000,
--    where `value` is the locations.py number, so flag_id = value - 1. The two
--    relations agree: value + 5730000 = flag_id + 5730001  =>  flag_id = value-1.)
--
-- GOAL (client_bh.py): locations[18] & (1 << 5)  →  flag_id = 18*8 + 5 = 149.
--   That bit is the in-ROM "Campaign Final Boss Win" flag (locations.py value 150,
--   commented out of the sendable-location table but still the completion flag).
--   When it is set the client sends ClientStatus.CLIENT_GOAL.
--
-- WHAT THIS DOES (mirrors worlds/yugioh06/client_bh.py game_watcher)
--   • poll(): scan the 32-byte LOCATION flag array → AP location ids, gated to the
--     slot's server location set AND to the in-game gate (the "YWCT2006" save-state
--     string), so a fresh/menu cartridge never reports checks.
--   • is_goal_complete(): the flag at byte 18 / bit 5 (Campaign Final Boss Win) —
--     the world's single completion goal.
--   • receive_item(): NO-OP for now (documented). items_handling = 0b001: the
--     PATCHED GAME already carries its own locally-placed items (the patch writes
--     the items→locations map into ROM at 0xF310), so a SOLO seed plays fully and
--     every check is reported. Delivering REMOTE multiworld items is the client's
--     guarded EWRAM bitmap write (0x5308 items bitmap + the 0x6C38/0x5325 money
--     pair), which must be confirmed in-emulator before being wired so it can never
--     mis-write into a foreign/unpatched cartridge. The exact recipe is documented
--     on M.receive_item below. No-op (never a wrong write) until then.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "yugioh06"

local ADDRESSES_VERIFIED = true   -- table generated from worlds/yugioh06 source

-- ── Memory domains ────────────────────────────────────────────────────────────
local EWRAM = "EWRAM"
local ROM   = "ROM"

-- ── Addresses / constants (worlds/yugioh06/client_bh.py + rom.py) ─────────────
local GAME_STATE_ADDR     = 0x0          -- EWRAM: 8-byte save-state string
local GAME_STATE_STR      = "YWCT2006"   -- present once a save has been created
local LOC_ARRAY_START     = 0x52E8       -- EWRAM: 32-byte location flag array
local LOC_ARRAY_LEN       = 32
local AP_SIG_ADDR         = 0xA0         -- ROM: internal game name
local AP_SIG              = "YUGIOHWCT06" -- 11 chars (YGO06 identity)
local GOAL_BYTE           = 18           -- LOCATION array byte for the goal flag
local GOAL_MASK           = 0x20         -- bit 5  (1 << 5)  → flag_id 149
local BASE_ID             = 5730001      -- location_id = flag_id + BASE_ID

-- ── Location table (GENERATED from worlds/yugioh06/locations.py + __init__.py) ─
-- ap_id -> flag bit index (`flag_id`). 165 entries. location_id = BASE_ID+flag_id;
-- checked when LOC_ARRAY[flag_id//8] & (1 << (flag_id % 8))  (LSB-first).
local LOC = {
  [5730001]=0,[5730002]=1,[5730003]=2,[5730004]=3,[5730005]=4,[5730006]=5,
  [5730007]=6,[5730008]=7,[5730009]=8,[5730010]=9,[5730011]=10,[5730012]=11,
  [5730013]=12,[5730014]=13,[5730015]=14,[5730016]=15,[5730017]=16,[5730018]=17,
  [5730019]=18,[5730020]=19,[5730021]=20,[5730022]=21,[5730023]=22,[5730024]=23,
  [5730025]=24,[5730026]=25,[5730027]=26,[5730028]=27,[5730029]=28,[5730030]=29,
  [5730031]=30,[5730032]=31,[5730033]=32,[5730034]=33,[5730035]=34,[5730036]=35,
  [5730037]=36,[5730038]=37,[5730039]=38,[5730040]=39,[5730041]=40,[5730042]=41,
  [5730043]=42,[5730044]=43,[5730045]=44,[5730046]=45,[5730047]=46,[5730048]=47,
  [5730049]=48,[5730050]=49,[5730051]=50,[5730052]=51,[5730053]=52,[5730054]=53,
  [5730055]=54,[5730056]=55,[5730057]=56,[5730058]=57,[5730059]=58,[5730060]=59,
  [5730061]=60,[5730062]=61,[5730063]=62,[5730064]=63,[5730065]=64,[5730066]=65,
  [5730067]=66,[5730068]=67,[5730069]=68,[5730070]=69,[5730071]=70,[5730072]=71,
  [5730073]=72,[5730074]=73,[5730075]=74,[5730076]=75,[5730077]=76,[5730078]=77,
  [5730079]=78,[5730080]=79,[5730081]=80,[5730082]=81,[5730083]=82,[5730084]=83,
  [5730085]=84,[5730086]=85,[5730087]=86,[5730088]=87,[5730089]=88,[5730090]=89,
  [5730091]=90,[5730092]=91,[5730093]=92,[5730094]=93,[5730095]=94,[5730096]=95,
  [5730097]=96,[5730098]=97,[5730099]=98,[5730100]=99,[5730101]=100,[5730102]=101,
  [5730103]=102,[5730104]=103,[5730105]=104,[5730106]=105,[5730107]=106,[5730108]=107,
  [5730109]=108,[5730110]=109,[5730111]=110,[5730112]=111,[5730113]=112,[5730114]=113,
  [5730115]=114,[5730116]=115,[5730117]=116,[5730118]=117,[5730119]=118,[5730120]=119,
  [5730121]=120,[5730122]=121,[5730123]=122,[5730124]=123,[5730125]=124,[5730126]=125,
  [5730127]=126,[5730128]=127,[5730129]=128,[5730130]=129,[5730131]=130,[5730132]=131,
  [5730133]=132,[5730134]=133,[5730135]=134,[5730136]=135,[5730137]=136,[5730138]=137,
  [5730139]=138,[5730140]=139,[5730141]=140,[5730142]=141,[5730143]=142,[5730144]=143,
  [5730145]=144,[5730146]=145,[5730147]=146,[5730148]=147,[5730149]=148,[5730154]=153,
  [5730155]=154,[5730156]=155,[5730157]=156,[5730158]=157,[5730159]=158,[5730160]=159,
  [5730161]=160,[5730162]=161,[5730163]=162,[5730164]=163,[5730165]=164,[5730166]=165,
  [5730167]=166,[5730168]=167,[5730169]=168,
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil     -- cached AP-signature result
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[yugioh06] " .. tostring(msg)) end
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

-- ── ROM identity: the cartridge header carries "YUGIOHWCT06" at ROM 0xA0 ───────
local function rom_is_ap()
  if rom_ok ~= nil then return rom_ok end
  for i = 1, #AP_SIG do
    local b = read_u8(AP_SIG_ADDR + i - 1, ROM)
    if b == nil then return false end          -- not readable yet; retry next poll
    if b ~= string.byte(AP_SIG, i) then
      rom_ok = false
      log("non-YGO06 ROM (no YUGIOHWCT06 signature) — detection idle")
      return false
    end
  end
  rom_ok = true
  log("YGO06 ROM verified (YUGIOHWCT06 signature present)")
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

-- ── Location flag array (read once per poll) ──────────────────────────────────
local flags = {}     -- byte index -> value, refreshed each poll

local function refresh_flags()
  for i = 0, LOC_ARRAY_LEN - 1 do
    flags[i] = read_u8(LOC_ARRAY_START + i, EWRAM)
  end
end

local function flag_bit(flag_id)
  local byte = flags[math.floor(flag_id / 8)]
  if byte == nil then return false end
  return bit_and(byte, POW2[flag_id % 8]) ~= 0
end

-- ── Detection gate ────────────────────────────────────────────────────────────
-- client_bh.py only proceeds once the 8-byte EWRAM state string equals
-- "YWCT2006" ("make sure save was created"). Before that the menu/boot RAM is
-- meaningless, so checks must not be reported.
local function save_created()
  for i = 1, #GAME_STATE_STR do
    local b = read_u8(GAME_STATE_ADDR + i - 1, EWRAM)
    if b ~= string.byte(GAME_STATE_STR, i) then return false end
  end
  return true
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
  if not save_created() then return new end
  refresh_flags()
  for ap_id, flag_id in pairs(LOC) do
    if not reported[ap_id] and wanted(ap_id) and flag_bit(flag_id) then
      reported[ap_id] = true
      new[#new + 1] = ap_id
    end
  end
  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_ap() then return false end
  if not save_created() then return false end
  refresh_flags()
  -- locations[18] & (1 << 5): the Campaign Final Boss Win flag (flag_id 149).
  local b = flags[GOAL_BYTE]
  return b ~= nil and bit_and(b, GOAL_MASK) ~= 0
end

-- Remote multiworld items: see the file header. items_handling = 0b001 means the
-- PATCHED GAME already carries its own locally-placed items, so solo play and
-- check reporting work fully; applying REMOTE items is the client's guarded EWRAM
-- write and is the one piece deferred until it can be confirmed in-emulator.
--
-- RECIPE (from client_bh.py game_watcher / parse_items, for future wiring):
--   • Only act while save_created() is true (state == "YWCT2006").
--   • Item bitmap: read EWRAM[0x5308 .. 0x5308+31]; for each received item set
--       index = item_id - 5730001;  if index ~= 254 then
--         byte = index // 8; bit = index % 8; bitmap[byte] |= (1 << bit)
--     and write it back (a guarded write: re-read before write, abort on change).
--   • Money ("5000DP" = item index 254): money_received = count of received
--     "5000DP" items; if money_received > EWRAM[0x5325] (amount already applied)
--     then  EWRAM[0x6C38] (u32 money) += (money_received - amount) * 5000  and
--           EWRAM[0x5325] := money_received.
--   Every write MUST stay gated behind rom_is_ap() AND save_created() so it can
--   never touch a foreign/unpatched cartridge. No-op until verified in-emulator.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
