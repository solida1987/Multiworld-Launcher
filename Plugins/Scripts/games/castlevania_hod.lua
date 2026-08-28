-- ═══════════════════════════════════════════════════════════════════════════════
-- castlevania_hod.lua — game module for the Archipelago BizHawk connector.
--                       Castlevania: Harmony of Dissonance (GBA)
--
-- STATUS: location detection is SOURCE-DERIVED from the world's own
-- client.py + locations.py + rom.py (LiquidCat64/LiquidCatipelago, cvhodis
-- v2.0 hotfix). The 248-entry table was GENERATED from CVHODIS_CHECKS_INFO,
-- not transcribed. Not yet measured in a running game: the manifest keeps
-- checks_verified=false and London says so.
--
-- MEMORY MODEL (BizHawk GBA domains)
-- ──────────────────────────────────
--   One 76-byte block at EWRAM 0x310 holds two separate flag arrays, and the
--   client reads them as 32-bit little-endian words:
--     event  flags  all_flags[0x04:0x20]  ->  EWRAM 0x314, 28 bytes
--     pickup flags  all_flags[0x20:0x48]  ->  EWRAM 0x330, 40 bytes
--   A flag id packs word and bit: flag = (word_index << 5) + bit_index.
--   Because the words are little-endian that collapses to plain byte order --
--   byte = flag // 8, bit = flag % 8, counted from the array's own start --
--   which locations.py states outright ("the word starting from 0x02000330").
--   0x02000330 is the absolute GBA address of EWRAM offset 0x330, so the two
--   descriptions agree.
--
--     ap_id       = BASE_ID + code          (BASE_ID = 0xD15500000)
--     checked when  PICKUP[code // 8] & (1 << (code % 8))
--
-- WHAT THIS DOES (mirrors cvhodis/client.py game_watcher)
--   • poll(): scan the pickup array → AP ids, gated to the slot's server
--     location set AND to the in-game gate, so a title screen reports nothing.
--   • is_goal_complete(): the seed's own ending requirements, read from
--     slot_data (medium/worst/best). Every requested ending must be set.
--     The world's own quirk is kept: the Worst Ending flag only counts while
--     the Dracula Wraith intro flag is clear, because the game sets them in a
--     sequence that would otherwise read as a win.
--     ⚠ furniture_amount_required is NOT judged here -- see the note at
--     is_goal_complete.
--   • receive_item(): NO-OP, deliberately. Delivering remote items in this
--     game is the client's text-box injection path (queued text + sound +
--     index increment, all guarded on the player being interruptible), and
--     writing that unverified risks corrupting a save. items_handling 0b001
--     means the patched ROM grants its own local items, so a solo seed plays
--     and every check is still reported.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "castlevania_hod"

local ADDRESSES_VERIFIED = true   -- table generated from the world's source

-- ── Memory domains ────────────────────────────────────────────────────────────
local EWRAM = "EWRAM"
local ROM   = "ROM"

-- ── Addresses / constants (cvhodis client.py + rom.py) ────────────────────────
local GAME_STATE_ADDR      = 0xC          -- EWRAM: GAME_STATE_ADDRESS
local FLAGS_BLOCK_START    = 0x310        -- EWRAM: FLAGS_BITFIELD_START
local EVENT_FLAGS_START    = 0x314        -- block + 0x04, 28 bytes
local EVENT_FLAGS_BYTES    = 28
local PICKUP_FLAGS_START   = 0x330        -- block + 0x20, 40 bytes
local PICKUP_FLAGS_BYTES   = 40
local AP_SIG_ADDR          = 0x7FFF00     -- ROM: ARCHIPELAGO_IDENTIFIER_START
local AP_SIG               = "ARCHIPELAG03"
local GAME_STATE_GAMEPLAY  = 0x03
local GAME_STATE_CREDITS   = 0x09
local BASE_ID              = 0xD15500000

-- Event-array flag ids (same packing as the pickup array).
local FLAG_MEDIUM_ENDING   = 0x45
local FLAG_WORST_ENDING    = 0x46
local FLAG_BEST_ENDING     = 0x1F
-- The client's guard: event_flags_array[2] & 0x100 -> word 2, bit 8 -> flag id
-- (2 << 5) + 8 = 0x48, which is FLAG_DRACULA_WRAITH_INTRO in client.py.
local FLAG_DRACULA_WRAITH_INTRO = 0x48

-- ── Location table (GENERATED from cvhodis/locations.py) ──────────────────────
-- ap_id -> pickup flag id (`code`). 248 entries.
local LOC = {
  [56192139265]=1,[56192139266]=2,[56192139267]=3,[56192139268]=4,[56192139269]=5,
  [56192139270]=6,[56192139271]=7,[56192139272]=8,[56192139273]=9,[56192139274]=10,
  [56192139275]=11,[56192139276]=12,[56192139277]=13,[56192139278]=14,[56192139279]=15,
  [56192139280]=16,[56192139281]=17,[56192139282]=18,[56192139283]=19,[56192139284]=20,
  [56192139285]=21,[56192139286]=22,[56192139287]=23,[56192139288]=24,[56192139289]=25,
  [56192139290]=26,[56192139291]=27,[56192139292]=28,[56192139293]=29,[56192139294]=30,
  [56192139295]=31,[56192139296]=32,[56192139297]=33,[56192139298]=34,[56192139299]=35,
  [56192139300]=36,[56192139301]=37,[56192139302]=38,[56192139303]=39,[56192139304]=40,
  [56192139305]=41,[56192139306]=42,[56192139307]=43,[56192139308]=44,[56192139309]=45,
  [56192139310]=46,[56192139311]=47,[56192139312]=48,[56192139313]=49,[56192139314]=50,
  [56192139315]=51,[56192139316]=52,[56192139317]=53,[56192139318]=54,[56192139319]=55,
  [56192139320]=56,[56192139321]=57,[56192139322]=58,[56192139323]=59,[56192139324]=60,
  [56192139325]=61,[56192139326]=62,[56192139327]=63,[56192139328]=64,[56192139329]=65,
  [56192139330]=66,[56192139331]=67,[56192139332]=68,[56192139333]=69,[56192139334]=70,
  [56192139335]=71,[56192139336]=72,[56192139337]=73,[56192139338]=74,[56192139339]=75,
  [56192139340]=76,[56192139341]=77,[56192139342]=78,[56192139343]=79,[56192139344]=80,
  [56192139345]=81,[56192139346]=82,[56192139347]=83,[56192139348]=84,[56192139349]=85,
  [56192139350]=86,[56192139351]=87,[56192139352]=88,[56192139353]=89,[56192139354]=90,
  [56192139355]=91,[56192139356]=92,[56192139357]=93,[56192139358]=94,[56192139359]=95,
  [56192139360]=96,[56192139361]=97,[56192139362]=98,[56192139363]=99,[56192139364]=100,
  [56192139365]=101,[56192139366]=102,[56192139367]=103,[56192139368]=104,[56192139369]=105,
  [56192139370]=106,[56192139371]=107,[56192139372]=108,[56192139373]=109,[56192139374]=110,
  [56192139375]=111,[56192139376]=112,[56192139377]=113,[56192139378]=114,[56192139379]=115,
  [56192139380]=116,[56192139381]=117,[56192139382]=118,[56192139383]=119,[56192139384]=120,
  [56192139385]=121,[56192139386]=122,[56192139387]=123,[56192139388]=124,[56192139389]=125,
  [56192139390]=126,[56192139391]=127,[56192139392]=128,[56192139393]=129,[56192139394]=130,
  [56192139395]=131,[56192139396]=132,[56192139397]=133,[56192139398]=134,[56192139399]=135,
  [56192139400]=136,[56192139401]=137,[56192139402]=138,[56192139404]=140,[56192139406]=142,
  [56192139407]=143,[56192139408]=144,[56192139410]=146,[56192139412]=148,[56192139413]=149,
  [56192139417]=153,[56192139419]=155,[56192139420]=156,[56192139421]=157,[56192139423]=159,
  [56192139425]=161,[56192139426]=162,[56192139427]=163,[56192139428]=164,[56192139429]=165,
  [56192139430]=166,[56192139431]=167,[56192139433]=169,[56192139435]=171,[56192139436]=172,
  [56192139437]=173,[56192139440]=176,[56192139442]=178,[56192139445]=181,[56192139446]=182,
  [56192139447]=183,[56192139448]=184,[56192139449]=185,[56192139450]=186,[56192139451]=187,
  [56192139452]=188,[56192139453]=189,[56192139454]=190,[56192139455]=191,[56192139456]=192,
  [56192139457]=193,[56192139458]=194,[56192139459]=195,[56192139460]=196,[56192139461]=197,
  [56192139464]=200,[56192139465]=201,[56192139466]=202,[56192139467]=203,[56192139468]=204,
  [56192139469]=205,[56192139470]=206,[56192139471]=207,[56192139472]=208,[56192139474]=210,
  [56192139475]=211,[56192139477]=213,[56192139478]=214,[56192139479]=215,[56192139481]=217,
  [56192139482]=218,[56192139483]=219,[56192139484]=220,[56192139485]=221,[56192139486]=222,
  [56192139487]=223,[56192139488]=224,[56192139489]=225,[56192139491]=227,[56192139492]=228,
  [56192139495]=231,[56192139499]=235,[56192139501]=237,[56192139502]=238,[56192139504]=240,
  [56192139506]=242,[56192139507]=243,[56192139509]=245,[56192139510]=246,[56192139511]=247,
  [56192139513]=249,[56192139514]=250,[56192139515]=251,[56192139516]=252,[56192139517]=253,
  [56192139518]=254,[56192139519]=255,[56192139522]=258,[56192139523]=259,[56192139524]=260,
  [56192139525]=261,[56192139526]=262,[56192139527]=263,[56192139528]=264,[56192139529]=265,
  [56192139530]=266,[56192139531]=267,[56192139532]=268,[56192139533]=269,[56192139534]=270,
  [56192139535]=271,[56192139536]=272,[56192139537]=273,[56192139538]=274,[56192139539]=275,
  [56192139540]=276,[56192139541]=277,[56192139542]=278,[56192139543]=279,[56192139544]=280,
  [56192139545]=281,[56192139546]=282,[56192139547]=283,
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}
local server_locations = nil
local rom_ok           = nil
local mem              = {}
local log_fn           = nil
local slot_data        = nil

local function log(msg)
  if log_fn then pcall(log_fn, "[castlevania_hod] " .. tostring(msg)) end
end

-- ── Domain guard. A mistyped domain name must fail LOUDLY, never quietly fall
--    back to whichever domain the core happens to have current: that would turn
--    one wrong name into plausible-looking reads of the wrong memory.
local _ap_domains, _ap_two_arg
local _ap_domain_warned = {}

local function ap_domain_ok(domain)
  if domain == nil then return true end
  if _ap_domains == nil and memory and memory.getmemorydomainlist then
    local ok, list = pcall(memory.getmemorydomainlist)
    if ok and type(list) == "table" then
      _ap_domains = {}
      for _, d in pairs(list) do _ap_domains[tostring(d)] = true end
      local reader = memory.read_u8 or memory.readbyte
      local probe = next(_ap_domains)
      if reader and probe ~= nil then
        _ap_two_arg = (pcall(reader, 0, probe)) and true or false
      end
    end
  end
  if _ap_domains == nil then return true end  -- core exposes no list; cannot check
  if _ap_domains[domain] then return true end
  if not _ap_domain_warned[domain] then
    _ap_domain_warned[domain] = true
    log("memory domain '" .. tostring(domain) .. "' does not exist in this core"
        .. " -- access refused (never redirected to the current domain)")
  end
  return false
end

-- ── Memory API (resolved at init; 2-arg domain form + current-domain fallback) ─
local function resolve_memory_api()
  if not memory then return false end
  mem.read_u8 = memory.read_u8 or memory.readbyte
  return mem.read_u8 ~= nil
end

local function read_u8(addr, domain)
  if not mem.read_u8 then return nil end
  if not ap_domain_ok(domain) then return nil end
  local ok, v = pcall(mem.read_u8, addr, domain)
  if ok and type(v) == "number" then return v end
  if _ap_two_arg == false then  -- old API: select the domain, then read
    if domain ~= nil and memory.usememorydomain
        and not pcall(memory.usememorydomain, domain) then return nil end
    ok, v = pcall(mem.read_u8, addr)
    if ok and type(v) == "number" then return v end
  end
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
      log("non-AP ROM (no ARCHIPELAG03 signature) -- detection idle")
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

-- ── Flag arrays (read once per poll) ──────────────────────────────────────────
local pickup = {}    -- byte index -> value
local event  = {}

local function refresh_flags()
  for i = 0, PICKUP_FLAGS_BYTES - 1 do
    pickup[i] = read_u8(PICKUP_FLAGS_START + i, EWRAM)
  end
  for i = 0, EVENT_FLAGS_BYTES - 1 do
    event[i] = read_u8(EVENT_FLAGS_START + i, EWRAM)
  end
end

local function bit_in(array, code)
  local byte = array[math.floor(code / 8)]
  if byte == nil then return false end
  return bit_and(byte, POW2[code % 8]) ~= 0
end

-- ── Detection gate ────────────────────────────────────────────────────────────
local function in_gameplay()
  local s = read_u8(GAME_STATE_ADDR, EWRAM)
  return s == GAME_STATE_GAMEPLAY or s == GAME_STATE_CREDITS
end

-- ── Module contract ───────────────────────────────────────────────────────────
function M.init(ctx)
  if ctx and type(ctx.log) == "function" then log_fn = ctx.log end
  if not resolve_memory_api() then
    log("BizHawk memory API unavailable -- module idle")
    ADDRESSES_VERIFIED = false
    return
  end
  local cfg = (ctx and ctx.config) or {}
  load_locations(cfg.locations)
  if type(cfg.slot_data) == "table" then slot_data = cfg.slot_data end
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
    if not reported[ap_id] and wanted(ap_id) and bit_in(pickup, code) then
      reported[ap_id] = true
      new[#new + 1] = ap_id
    end
  end
  return new
end

---
--- The seed's ending requirements, all of them.
---
--- ⚠ The world also allows a furniture objective
--- (slot_data.furniture_amount_required), which the client judges by counting
--- set bits in a separate placed-furniture field. That count is not read here,
--- so on a furniture seed this returns true once the endings are done -- one
--- objective early, never late. Reporting a goal that has not happened is the
--- worse failure, so when furniture is required this stays false and the
--- player finishes from Archipelago's own client instead. Wiring it properly
--- needs the furniture field measured in a running game.
---
function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_ap() then return false end
  -- ⚠ The same readiness guard poll() uses. Without it the goal is judged on
  -- memory the game has not written yet: at boot the ROM signature is already
  -- valid while EWRAM is still garbage, so a stray bit reads as a finished
  -- run. That fired on Pokemon Crystal on 19 Aug -- the server took the goal,
  -- auto-collected, and released all 475 remaining items one second after the
  -- game started.
  if not in_gameplay() then return false end
  refresh_flags()

  local sd = slot_data or {}
  if sd.furniture_amount_required and sd.furniture_amount_required ~= 0 then
    return false      -- see the note above: not judged, so never claimed
  end

  local want_medium = sd.medium_ending_required
  local want_worst  = sd.worst_ending_required
  local want_best   = sd.best_ending_required

  -- No slot_data reached us: fall back to the game's own hardest ending rather
  -- than guessing a lesser one, which would end a run early.
  if want_medium == nil and want_worst == nil and want_best == nil then
    want_best = true
  end

  local function truthy(v) return v ~= nil and v ~= false and v ~= 0 end

  if truthy(want_medium) and not bit_in(event, FLAG_MEDIUM_ENDING) then return false end
  if truthy(want_best)   and not bit_in(event, FLAG_BEST_ENDING)   then return false end
  if truthy(want_worst) then
    -- The world's own rule: the sequence sets the Wraith intro flag around the
    -- worst ending, and the client refuses the ending while it is set.
    if not bit_in(event, FLAG_WORST_ENDING) then return false end
    if bit_in(event, FLAG_DRACULA_WRAITH_INTRO) then return false end
  end
  return true
end

-- Deliberately inert -- see the header.
function M.receive_item(_item_id, _meta) end

return M
