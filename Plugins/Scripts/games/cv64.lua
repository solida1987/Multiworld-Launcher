-- ═══════════════════════════════════════════════════════════════════════════════
-- cv64.lua — game module for the Archipelago BizHawk connector.
--           Castlevania (Nintendo 64) — "Castlevania 64"
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the official
-- AP world worlds/cv64 (client.py + locations.py + rom.py, main branch). The
-- 352-entry location table was GENERATED directly from locations.py
-- (location_info, every entry that carries a "code"), not hand-copied, and the
-- save-flag bit math is replicated EXACTLY from the client's game_watcher (mind
-- the N64 big-endian byte order + MSB-first flag bits). Loads crash-free on any
-- ROM; self-disables on a non-AP cartridge.
--
-- MEMORY MODEL (BizHawk N64 domains — matches client.py Castlevania64Client)
-- ──────────────────────────────────────────────────────────────────────────
--   The cv64 AP client is a BizHawkClient that reads two N64 domains:
--     "RDRAM"  the console work RAM (game state + the save struct that holds
--              every location-checked flag + the goal cutscene byte)
--     "ROM"    the cartridge (the "CASTLEVANIA" name + the "ARCHIPELAGO1" patch
--              signature the client validates)
--
--   N64 IS BIG-ENDIAN. Every multi-byte field the client reads is decoded with
--   int.from_bytes(..., "big"); we assemble u16/u32 from individual big-endian
--   bytes in the SAME order (read_u8 of the lowest address = most-significant
--   byte). Single-byte flag reads need no byte order.
--
--   LOCATION FLAGS (client.py game_watcher, exact):
--     save_struct  = RDRAM[0x389BE4 .. 0x389BE4+224)        (the 224-byte save)
--     flag_bytes   = save_struct[0x00:0x44]  ++  save_struct[0x90:0x9F]
--                    (68 contiguous bytes, then 15 more — a NON-contiguous pair
--                     of slices, concatenated)
--     for byte_i, byte in enumerate(flag_bytes):
--         for i in 0..7:
--             if byte & (0x80 >> i) ~= 0:            -- MSB-FIRST (bit i=0 → 0x80)
--                 flag_id     = byte_i*8 + i
--                 location_id = flag_id + base_id    -- base_id = 0xC64000
--                 if location_id in server_locations: report it
--     So flag_bytes index 0..67 map to RDRAM 0x389BE4..0x389C27, and index
--     68..82 map to RDRAM 0x389C74..0x389C82 (= save base + 0x90 .. +0x9E).
--
--   GOAL (client.py): game clear when the ending-cutscene value
--     cutscene_value = RDRAM[0x389EFB] (1 byte) is in 0x26..0x2E, OR the game is
--     in the Credits state (game_state == 0x0000000B).
--
--   GAMEPLAY GATE (client.py): the client only scans flags while
--     game_state (RDRAM 0x342084, u32 big) is Gameplay (0x02) or Credits (0x0B);
--     in any other state it bails. We mirror that so the zeroed/booting save
--     struct on the title screen can never report phantom checks.
--
-- WHAT THIS DOES (mirrors worlds/cv64/client.py game_watcher)
--   • poll(): read the two flag slices once, walk every bit exactly as the
--     client does → AP location ids, gated to the slot's server location set
--     and to the Gameplay/Credits game-state gate.
--   • is_goal_complete(): cutscene_value in 0x26..0x2E OR game_state==Credits.
--   • receive_item(): NO-OP (documented). items_handling = 0b001 means the
--     PATCHED GAME grants its own locally-found items, so a SOLO seed plays
--     fully and every check is reported. Delivering REMOTE multiworld items is
--     the client's intricate guarded RDRAM text-box-injection path (per-item
--     reward buffer + received-count handshake at save_struct 0xDA, with vamp/
--     deathlink interleaving); that is the one piece that needs in-emulator
--     verification before it is wired, so it is intentionally left out rather
--     than shipped unverified (a wrong RDRAM write corrupts the live save).
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "cv64"

local ADDRESSES_VERIFIED = true   -- table generated from worlds/cv64 source

-- ── Memory domains (BizHawk N64) ──────────────────────────────────────────────
local RDRAM = "RDRAM"   -- console work RAM (client reads game state + save struct)
local ROM   = "ROM"     -- cartridge (name + AP signature)

-- ── Addresses / constants (worlds/cv64/client.py + locations.py + rom.py) ─────
local BASE_ID            = 0xC64000     -- locations.py base_id

local GAME_STATE_ADDR    = 0x342084     -- RDRAM: u32 big main game-state
local GAME_STATE_GAMEPLAY= 0x00000002
local GAME_STATE_CREDITS = 0x0000000B

local SAVE_STRUCT_ADDR   = 0x389BE4     -- RDRAM: 224-byte save struct base
local CUTSCENE_ADDR      = 0x389EFB     -- RDRAM: u8 ending-cutscene value (goal)
local GOAL_CUTSCENE_LO   = 0x26         -- 0x26 <= cutscene <= 0x2E → game clear
local GOAL_CUTSCENE_HI   = 0x2E

-- flag_bytes = save_struct[0x00:0x44] (68 bytes) ++ save_struct[0x90:0x9F] (15 bytes)
local FLAG_SLICES = {
  { off = 0x00, len = 0x44 },   -- 68 bytes → flag_bytes index 0..67
  { off = 0x90, len = 0x0F },   -- 15 bytes → flag_bytes index 68..82
}

local AP_NAME_ADDR       = 0x20         -- ROM: "CASTLEVANIA         " (20 bytes)
local AP_NAME            = "CASTLEVANIA         "
local AP_SIG_ADDR        = 0xBFBFD0     -- ROM: "ARCHIPELAGO1" (12 bytes)
local AP_SIG             = "ARCHIPELAGO1"

-- ── Location table (GENERATED from worlds/cv64/locations.py) ──────────────────
-- ap_id -> in-game flag `code`. 352 entries. ap_id = BASE_ID + code; a location
-- is checked when the bit at flag_id==code is set within flag_bytes (above).
-- The values double as the membership set used during the bit walk.
local LOC = {
  [12992513]=1,[12992514]=2,[12992515]=3,[12992516]=4,[12992518]=6,[12992519]=7,[12992520]=8,[12992521]=9,
  [12992522]=10,[12992523]=11,[12992524]=12,[12992525]=13,[12992526]=14,[12992527]=15,[12992528]=16,[12992530]=18,
  [12992531]=19,[12992536]=24,[12992537]=25,[12992538]=26,[12992539]=27,[12992540]=28,[12992542]=30,[12992547]=35,
  [12992548]=36,[12992549]=37,[12992550]=38,[12992551]=39,[12992552]=40,[12992553]=41,[12992554]=42,[12992555]=43,
  [12992556]=44,[12992557]=45,[12992558]=46,[12992559]=47,[12992560]=48,[12992561]=49,[12992562]=50,[12992563]=51,
  [12992564]=52,[12992566]=54,[12992567]=55,[12992568]=56,[12992569]=57,[12992571]=59,[12992572]=60,[12992573]=61,
  [12992574]=62,[12992576]=64,[12992577]=65,[12992580]=68,[12992581]=69,[12992582]=70,[12992584]=72,[12992585]=73,
  [12992586]=74,[12992587]=75,[12992588]=76,[12992589]=77,[12992590]=78,[12992591]=79,[12992592]=80,[12992593]=81,
  [12992594]=82,[12992595]=83,[12992597]=85,[12992598]=86,[12992599]=87,[12992600]=88,[12992601]=89,[12992602]=90,
  [12992608]=96,[12992609]=97,[12992610]=98,[12992611]=99,[12992612]=100,[12992613]=101,[12992614]=102,[12992617]=105,
  [12992618]=106,[12992619]=107,[12992620]=108,[12992621]=109,[12992622]=110,[12992623]=111,[12992627]=115,[12992628]=116,
  [12992630]=118,[12992631]=119,[12992632]=120,[12992633]=121,[12992634]=122,[12992635]=123,[12992636]=124,[12992637]=125,
  [12992638]=126,[12992640]=128,[12992641]=129,[12992642]=130,[12992643]=131,[12992656]=144,[12992657]=145,[12992658]=146,
  [12992659]=147,[12992660]=148,[12992661]=149,[12992662]=150,[12992663]=151,[12992664]=152,[12992665]=153,[12992666]=154,
  [12992667]=155,[12992668]=156,[12992669]=157,[12992670]=158,[12992676]=164,[12992677]=165,[12992678]=166,[12992679]=167,
  [12992683]=171,[12992684]=172,[12992685]=173,[12992686]=174,[12992687]=175,[12992688]=176,[12992690]=178,[12992691]=179,
  [12992692]=180,[12992693]=181,[12992703]=191,[12992704]=192,[12992705]=193,[12992706]=194,[12992707]=195,[12992709]=197,
  [12992710]=198,[12992712]=200,[12992713]=201,[12992724]=212,[12992725]=213,[12992728]=216,[12992732]=220,[12992735]=223,
  [12992737]=225,[12992740]=228,[12992741]=229,[12992742]=230,[12992743]=231,[12992746]=234,[12992768]=256,[12992769]=257,
  [12992770]=258,[12992771]=259,[12992772]=260,[12992773]=261,[12992774]=262,[12992775]=263,[12992777]=265,[12992778]=266,
  [12992779]=267,[12992780]=268,[12992781]=269,[12992782]=270,[12992783]=271,[12992792]=280,[12992793]=281,[12992794]=282,
  [12992795]=283,[12992815]=303,[12992816]=304,[12992817]=305,[12992818]=306,[12992819]=307,[12992820]=308,[12992824]=312,
  [12992825]=313,[12992826]=314,[12992827]=315,[12992828]=316,[12992829]=317,[12992830]=318,[12992842]=330,[12992843]=331,
  [12992844]=332,[12992845]=333,[12992846]=334,[12992851]=339,[12992852]=340,[12992853]=341,[12992854]=342,[12992856]=344,
  [12992857]=345,[12992870]=358,[12992873]=361,[12992874]=362,[12992875]=363,[12992876]=364,[12992878]=366,[12992880]=368,
  [12992881]=369,[12992882]=370,[12992883]=371,[12992884]=372,[12992885]=373,[12992886]=374,[12992887]=375,[12992888]=376,
  [12992889]=377,[12992890]=378,[12992898]=386,[12992899]=387,[12992900]=388,[12992901]=389,[12992902]=390,[12992903]=391,
  [12992904]=392,[12992905]=393,[12992906]=394,[12992907]=395,[12992908]=396,[12992909]=397,[12992910]=398,[12992911]=399,
  [12992912]=400,[12992913]=401,[12992914]=402,[12992915]=403,[12992916]=404,[12992917]=405,[12992918]=406,[12992919]=407,
  [12992920]=408,[12992924]=412,[12992925]=413,[12992926]=414,[12992928]=416,[12992929]=417,[12992930]=418,[12992936]=424,
  [12992937]=425,[12992938]=426,[12992939]=427,[12992940]=428,[12992941]=429,[12992942]=430,[12992943]=431,[12992944]=432,
  [12992945]=433,[12992946]=434,[12992947]=435,[12992950]=438,[12992968]=456,[12992969]=457,[12992970]=458,[12992971]=459,
  [12992972]=460,[12992973]=461,[12992974]=462,[12992988]=476,[12992989]=477,[12992990]=478,[12993010]=498,[12993011]=499,
  [12993015]=503,[12993017]=505,[12993018]=506,[12993019]=507,[12993020]=508,[12993021]=509,[12993022]=510,[12993023]=511,
  [12993057]=545,[12993058]=546,[12993059]=547,[12993060]=548,[12993061]=549,[12993063]=551,[12993064]=552,[12993065]=553,
  [12993066]=554,[12993068]=556,[12993069]=557,[12993070]=558,[12993071]=559,[12993072]=560,[12993074]=562,[12993075]=563,
  [12993076]=564,[12993077]=565,[12993078]=566,[12993080]=568,[12993081]=569,[12993082]=570,[12993083]=571,[12993084]=572,
  [12993086]=574,[12993087]=575,[12993088]=576,[12993090]=578,[12993091]=579,[12993092]=580,[12993093]=581,[12993094]=582,
  [12993095]=583,[12993097]=585,[12993098]=586,[12993099]=587,[12993101]=589,[12993102]=590,[12993103]=591,[12993104]=592,
  [12993105]=593,[12993107]=595,[12993108]=596,[12993109]=597,[12993111]=599,[12993112]=600,[12993113]=601,[12993114]=602,
  [12993116]=604,[12993117]=605,[12993118]=606,[12993119]=607,[12993120]=608,[12993122]=610,[12993123]=611,[12993124]=612,
  [12993125]=613,[12993126]=614,[12993127]=615,[12993129]=617,[12993130]=618,[12993131]=619,[12993133]=621,[12993134]=622,
  [12993135]=623,[12993137]=625,[12993138]=626,[12993140]=628,[12993141]=629,[12993142]=630,[12993143]=631,[12993144]=632,
  [12993145]=633,[12993146]=634,[12993147]=635,[12993149]=637,[12993150]=638,[12993151]=639,[12993152]=640,[12993153]=641,
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil     -- cached AP-signature result
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[cv64] " .. tostring(msg)) end
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

-- N64 is BIG-ENDIAN: assemble multi-byte values most-significant byte first
-- (the byte at the LOWEST address is the high byte), matching the client's
-- int.from_bytes(..., "big"). Built from read_u8 so we never depend on a core's
-- read_u16/u32 endianness for these reads. Any failed byte → nil (retry later).
--
-- read_u16_be is part of the provided big-endian read API. Detection + goal use
-- only read_u8 (flags/cutscene) and read_u32_be (game_state); the 16-bit reads
-- the client makes (the save's received-item counter at 0xDA, written-deathlink
-- count) belong to the deferred remote-item path (see receive_item), so this
-- helper is provided ready for that path rather than used by the scan today.
local function read_u16_be(addr, domain)
  local b0 = read_u8(addr,     domain)   -- high byte
  local b1 = read_u8(addr + 1, domain)   -- low byte
  if b0 == nil or b1 == nil then return nil end
  return b0 * 0x100 + b1
end

local function read_u32_be(addr, domain)
  local b0 = read_u8(addr,     domain)   -- most-significant
  local b1 = read_u8(addr + 1, domain)
  local b2 = read_u8(addr + 2, domain)
  local b3 = read_u8(addr + 3, domain)   -- least-significant
  if b0 == nil or b1 == nil or b2 == nil or b3 == nil then return nil end
  return ((b0 * 0x1000000) + (b1 * 0x10000) + (b2 * 0x100) + b3)
end

-- ── ROM identity: the AP patch writes "ARCHIPELAGO1" at ROM 0xBFBFD0, and the
-- cartridge name "CASTLEVANIA         " sits at ROM 0x20 (both checked by the
-- client's validate_rom). Verifying BOTH means we only ever act on a patched
-- CV64 cartridge — exactly the client's gate. ───────────────────────────────
local function rom_is_ap()
  if rom_ok ~= nil then return rom_ok end
  for i = 1, #AP_NAME do
    local b = read_u8(AP_NAME_ADDR + i - 1, ROM)
    if b == nil then return false end          -- not readable yet; retry next poll
    if b ~= string.byte(AP_NAME, i) then
      rom_ok = false
      log("non-CV64 ROM (no 'CASTLEVANIA' cartridge name) — detection idle")
      return false
    end
  end
  for i = 1, #AP_SIG do
    local b = read_u8(AP_SIG_ADDR + i - 1, ROM)
    if b == nil then return false end
    if b ~= string.byte(AP_SIG, i) then
      rom_ok = false
      log("unpatched/incompatible CV64 ROM (no 'ARCHIPELAGO1' signature) — detection idle")
      return false
    end
  end
  rom_ok = true
  log("AP ROM verified ('CASTLEVANIA' + 'ARCHIPELAGO1' signatures present)")
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

-- ── Detection gate ────────────────────────────────────────────────────────────
-- The client only detects locations/goal while in the Gameplay or Credits state.
local function in_gameplay()
  local s = read_u32_be(GAME_STATE_ADDR, RDRAM)
  return s ~= nil and (s == GAME_STATE_GAMEPLAY or s == GAME_STATE_CREDITS)
end

-- ── Flag walk (read both slices once per poll) ────────────────────────────────
-- Mirrors the client's `for byte_i, byte in enumerate(flag_bytes)` loop EXACTLY:
-- a single running flag_bytes index spanning the two non-contiguous save-struct
-- slices, MSB-first bits, flag_id = byte_i*8 + i.
local BITS = { 0x80, 0x40, 0x20, 0x10, 0x08, 0x04, 0x02, 0x01 }   -- 0x80 >> i

local function scan_into(new)
  local byte_i = 0
  for _, slice in ipairs(FLAG_SLICES) do
    for k = 0, slice.len - 1 do
      local byte = read_u8(SAVE_STRUCT_ADDR + slice.off + k, RDRAM)
      if byte ~= nil and byte ~= 0 then
        for i = 0, 7 do
          if byte >= BITS[i + 1] then                 -- quick reject of low bits
            -- byte & (0x80 >> i): true if the (i)th MSB is set
            if (byte % (BITS[i + 1] * 2)) >= BITS[i + 1] then
              local ap_id = (byte_i * 8 + i) + BASE_ID
              if LOC[ap_id] ~= nil and not reported[ap_id] and wanted(ap_id) then
                reported[ap_id] = true
                new[#new + 1] = ap_id
              end
            end
          end
        end
      end
      byte_i = byte_i + 1
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
  local n = 0; for _ in pairs(LOC) do n = n + 1 end
  log("ready: " .. n .. " location flags (N64 big-endian)")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_ap() then return new end
  if not in_gameplay() then return new end
  scan_into(new)
  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_ap() then return false end
  -- Credits state is itself a clear; otherwise an ending-cutscene value in range.
  local s = read_u32_be(GAME_STATE_ADDR, RDRAM)
  if s == GAME_STATE_CREDITS then return true end
  if s ~= GAME_STATE_GAMEPLAY then return false end
  local c = read_u8(CUTSCENE_ADDR, RDRAM)
  return c ~= nil and c >= GOAL_CUTSCENE_LO and c <= GOAL_CUTSCENE_HI
end

-- Remote multiworld items: see the file header. items_handling = 0b001 means the
-- patched game grants its own found items, so solo play and check reporting work
-- fully; applying REMOTE items is the client's guarded RDRAM text-box-injection
-- path (per-item reward buffer + the save's received-item counter at 0xDA, with
-- deathlink/vamp interleaving) and is the one piece deferred until it can be
-- confirmed in-emulator. No-op (never a wrong write) until then.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
