-- ═══════════════════════════════════════════════════════════════════════════════
-- mk64.lua — game module for the Archipelago BizHawk connector.
--           Mario Kart 64 (Nintendo 64) — "Mario Kart 64"
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the official
-- AP world worlds/mk64 (Client.py + Locations.py + Rom.py — Edsploration's
-- MK64-Archipelago, the mk64 release branch). The 448-entry location table was
-- GENERATED directly from Locations.py (location_name_to_id, every location's
-- code), NOT hand-copied, and the flag math is replicated EXACTLY from the
-- client's game_watcher (mind the N64 big-endian byte order; the per-location
-- bits themselves are LSB-first within each byte). Loads crash-free on any ROM;
-- self-disables on a non-AP cartridge.
--
-- MEMORY MODEL (BizHawk N64 domains — matches Client.py MarioKart64Client)
-- ──────────────────────────────────────────────────────────────────────────
--   The mk64 AP client is a BizHawkClient that reads two N64 domains:
--     "RDRAM"  the console work RAM (the live game-status byte, the received-
--              item counter, and the 56-byte "locations unchecked" bit array)
--     "ROM"    the cartridge ("MK64 ARCHIPELAGO" patch signature the client
--              validates at ROM 0x20, plus the patched-in player name + seed)
--
--   N64 IS BIG-ENDIAN. The client decodes every multi-byte value with
--   int.from_bytes(..., "big") / .to_bytes(..., "big"); we assemble u16/u32 from
--   individual big-endian bytes in the SAME order (read_u8 of the lowest address
--   = most-significant byte). Single-byte flag reads need no byte order. The
--   per-location flag BITS are LSB-first inside each byte (the client tests
--   `byte & (1 << j)` for j = 0..7).
--
--   LOCATION FLAGS (Client.py game_watcher, exact):
--     locs_state = RDRAM[0x400024 .. 0x400024+56)        (56-byte "unchecked")
--     The bit array is INVERTED vs. the usual "checked" convention: a SET bit
--     means the location is still UNCHECKED/available; the game CLEARS a bit to
--     0 when that location is checked. The client keeps its own baseline
--     `unchecked_locs`, initialised to ALL 0xFF, and reports a location the
--     instant a live bit DIFFERS from its remembered baseline:
--       for byte_i, byte in enumerate(locs_state):
--         if byte ~= baseline[byte_i]:
--           for j in 0..7:
--             if (byte & (1<<j)) ~= (baseline[byte_i] & (1<<j)):
--               report ID_BASE + 8*byte_i + j        -- ID_BASE = 4660000
--           baseline[byte_i] = byte
--     We mirror this EXACTLY: a per-byte 0xFF baseline, LSB-first bit diff,
--     local flag_id = byte_i*8 + j, ap_id = flag_id + ID_BASE. Reporting is
--     gated to LOC membership AND to the slot's server location set, so the
--     locations a given seed never created (which the patch leaves at bit 0, i.e.
--     "already differs from the 0xFF baseline") are filtered out instead of
--     spuriously reported.
--
--   VALID-CONNECTION GATE (Client.py): every poll bails unless
--     game_status (RDRAM 0x400019, 1 byte) bit 0 is set — the basepatch holds
--     this bit at 1 only once a real save is live in RDRAM, so the zeroed/booting
--     state on the title screen can never report phantom checks.
--
--   GOAL (Client.py): game clear when game_status bit 1 is set
--     ((game_status >> 1) & 1), i.e. the basepatch's game_clear flag.
--
-- WHAT THIS DOES (mirrors worlds/mk64/Client.py game_watcher)
--   • poll(): read the status byte + the 56-byte flag array once, walk every bit
--     exactly as the client does (diff against the 0xFF baseline) → AP location
--     ids, gated to the slot's server location set and to the valid-connection
--     gate.
--   • is_goal_complete(): game_status bit 1 (game_clear) set.
--   • receive_item(): NO-OP (documented). items_handling = 0b001 means the
--     PATCHED GAME grants its own locally-found items, so a SOLO seed plays fully
--     and every check is reported. Delivering REMOTE multiworld items is the
--     client's guarded RDRAM write path (per-item reward staging at 0x40028E —
--     id + classification + player-name + item-name — handshaked against the
--     received-item counter at 0x40001A, the write GUARDED on that byte reading
--     back 0xFF); that is the one piece that needs in-emulator verification
--     before it is wired, so it is intentionally left out rather than shipped
--     unverified (a wrong RDRAM write corrupts the live game state).
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "mk64"

local ADDRESSES_VERIFIED = true   -- table generated from worlds/mk64 source

-- ── Memory domains (BizHawk N64) ──────────────────────────────────────────────
local RDRAM = "RDRAM"   -- console work RAM (status byte + flag array)
local ROM   = "ROM"     -- cartridge (AP signature)

-- ── Addresses / constants (worlds/mk64/Client.py + Locations.py + Rom.py) ─────
local ID_BASE             = 4660000      -- Locations.py ID_BASE

local GAME_STATUS_ADDR    = 0x400019     -- RDRAM: u8; bit0=valid conn, bit1=clear
local STATUS_CONNECTED    = 0x01         -- bit 0
local STATUS_GAME_CLEAR   = 0x02         -- bit 1

local FLAGS_ADDR          = 0x400024     -- RDRAM: 56-byte "locations unchecked"
local FLAGS_SIZE          = 56           -- SAVE_UNCHECKED_LOCATIONS_SIZE → 448 bits

local AP_SIG_ADDR         = 0x20         -- ROM: "MK64 ARCHIPELAGO" (16 bytes)
local AP_SIG              = "MK64 ARCHIPELAGO"

-- ── Location table (GENERATED from worlds/mk64/Locations.py) ──────────────────
-- ap_id -> local flag id (`code` - ID_BASE). 448 entries, contiguous 0..447,
-- exactly filling the 56-byte flag array (56*8 = 448). ap_id = ID_BASE + local;
-- the local id's byte index = local//8 and bit = local%8 (LSB-first). The values
-- double as the membership set used during the bit walk.
local LOC = {
  [4660000]=0,[4660001]=1,[4660002]=2,[4660003]=3,[4660004]=4,[4660005]=5,[4660006]=6,[4660007]=7,
  [4660008]=8,[4660009]=9,[4660010]=10,[4660011]=11,[4660012]=12,[4660013]=13,[4660014]=14,[4660015]=15,
  [4660016]=16,[4660017]=17,[4660018]=18,[4660019]=19,[4660020]=20,[4660021]=21,[4660022]=22,[4660023]=23,
  [4660024]=24,[4660025]=25,[4660026]=26,[4660027]=27,[4660028]=28,[4660029]=29,[4660030]=30,[4660031]=31,
  [4660032]=32,[4660033]=33,[4660034]=34,[4660035]=35,[4660036]=36,[4660037]=37,[4660038]=38,[4660039]=39,
  [4660040]=40,[4660041]=41,[4660042]=42,[4660043]=43,[4660044]=44,[4660045]=45,[4660046]=46,[4660047]=47,
  [4660048]=48,[4660049]=49,[4660050]=50,[4660051]=51,[4660052]=52,[4660053]=53,[4660054]=54,[4660055]=55,
  [4660056]=56,[4660057]=57,[4660058]=58,[4660059]=59,[4660060]=60,[4660061]=61,[4660062]=62,[4660063]=63,
  [4660064]=64,[4660065]=65,[4660066]=66,[4660067]=67,[4660068]=68,[4660069]=69,[4660070]=70,[4660071]=71,
  [4660072]=72,[4660073]=73,[4660074]=74,[4660075]=75,[4660076]=76,[4660077]=77,[4660078]=78,[4660079]=79,
  [4660080]=80,[4660081]=81,[4660082]=82,[4660083]=83,[4660084]=84,[4660085]=85,[4660086]=86,[4660087]=87,
  [4660088]=88,[4660089]=89,[4660090]=90,[4660091]=91,[4660092]=92,[4660093]=93,[4660094]=94,[4660095]=95,
  [4660096]=96,[4660097]=97,[4660098]=98,[4660099]=99,[4660100]=100,[4660101]=101,[4660102]=102,[4660103]=103,
  [4660104]=104,[4660105]=105,[4660106]=106,[4660107]=107,[4660108]=108,[4660109]=109,[4660110]=110,[4660111]=111,
  [4660112]=112,[4660113]=113,[4660114]=114,[4660115]=115,[4660116]=116,[4660117]=117,[4660118]=118,[4660119]=119,
  [4660120]=120,[4660121]=121,[4660122]=122,[4660123]=123,[4660124]=124,[4660125]=125,[4660126]=126,[4660127]=127,
  [4660128]=128,[4660129]=129,[4660130]=130,[4660131]=131,[4660132]=132,[4660133]=133,[4660134]=134,[4660135]=135,
  [4660136]=136,[4660137]=137,[4660138]=138,[4660139]=139,[4660140]=140,[4660141]=141,[4660142]=142,[4660143]=143,
  [4660144]=144,[4660145]=145,[4660146]=146,[4660147]=147,[4660148]=148,[4660149]=149,[4660150]=150,[4660151]=151,
  [4660152]=152,[4660153]=153,[4660154]=154,[4660155]=155,[4660156]=156,[4660157]=157,[4660158]=158,[4660159]=159,
  [4660160]=160,[4660161]=161,[4660162]=162,[4660163]=163,[4660164]=164,[4660165]=165,[4660166]=166,[4660167]=167,
  [4660168]=168,[4660169]=169,[4660170]=170,[4660171]=171,[4660172]=172,[4660173]=173,[4660174]=174,[4660175]=175,
  [4660176]=176,[4660177]=177,[4660178]=178,[4660179]=179,[4660180]=180,[4660181]=181,[4660182]=182,[4660183]=183,
  [4660184]=184,[4660185]=185,[4660186]=186,[4660187]=187,[4660188]=188,[4660189]=189,[4660190]=190,[4660191]=191,
  [4660192]=192,[4660193]=193,[4660194]=194,[4660195]=195,[4660196]=196,[4660197]=197,[4660198]=198,[4660199]=199,
  [4660200]=200,[4660201]=201,[4660202]=202,[4660203]=203,[4660204]=204,[4660205]=205,[4660206]=206,[4660207]=207,
  [4660208]=208,[4660209]=209,[4660210]=210,[4660211]=211,[4660212]=212,[4660213]=213,[4660214]=214,[4660215]=215,
  [4660216]=216,[4660217]=217,[4660218]=218,[4660219]=219,[4660220]=220,[4660221]=221,[4660222]=222,[4660223]=223,
  [4660224]=224,[4660225]=225,[4660226]=226,[4660227]=227,[4660228]=228,[4660229]=229,[4660230]=230,[4660231]=231,
  [4660232]=232,[4660233]=233,[4660234]=234,[4660235]=235,[4660236]=236,[4660237]=237,[4660238]=238,[4660239]=239,
  [4660240]=240,[4660241]=241,[4660242]=242,[4660243]=243,[4660244]=244,[4660245]=245,[4660246]=246,[4660247]=247,
  [4660248]=248,[4660249]=249,[4660250]=250,[4660251]=251,[4660252]=252,[4660253]=253,[4660254]=254,[4660255]=255,
  [4660256]=256,[4660257]=257,[4660258]=258,[4660259]=259,[4660260]=260,[4660261]=261,[4660262]=262,[4660263]=263,
  [4660264]=264,[4660265]=265,[4660266]=266,[4660267]=267,[4660268]=268,[4660269]=269,[4660270]=270,[4660271]=271,
  [4660272]=272,[4660273]=273,[4660274]=274,[4660275]=275,[4660276]=276,[4660277]=277,[4660278]=278,[4660279]=279,
  [4660280]=280,[4660281]=281,[4660282]=282,[4660283]=283,[4660284]=284,[4660285]=285,[4660286]=286,[4660287]=287,
  [4660288]=288,[4660289]=289,[4660290]=290,[4660291]=291,[4660292]=292,[4660293]=293,[4660294]=294,[4660295]=295,
  [4660296]=296,[4660297]=297,[4660298]=298,[4660299]=299,[4660300]=300,[4660301]=301,[4660302]=302,[4660303]=303,
  [4660304]=304,[4660305]=305,[4660306]=306,[4660307]=307,[4660308]=308,[4660309]=309,[4660310]=310,[4660311]=311,
  [4660312]=312,[4660313]=313,[4660314]=314,[4660315]=315,[4660316]=316,[4660317]=317,[4660318]=318,[4660319]=319,
  [4660320]=320,[4660321]=321,[4660322]=322,[4660323]=323,[4660324]=324,[4660325]=325,[4660326]=326,[4660327]=327,
  [4660328]=328,[4660329]=329,[4660330]=330,[4660331]=331,[4660332]=332,[4660333]=333,[4660334]=334,[4660335]=335,
  [4660336]=336,[4660337]=337,[4660338]=338,[4660339]=339,[4660340]=340,[4660341]=341,[4660342]=342,[4660343]=343,
  [4660344]=344,[4660345]=345,[4660346]=346,[4660347]=347,[4660348]=348,[4660349]=349,[4660350]=350,[4660351]=351,
  [4660352]=352,[4660353]=353,[4660354]=354,[4660355]=355,[4660356]=356,[4660357]=357,[4660358]=358,[4660359]=359,
  [4660360]=360,[4660361]=361,[4660362]=362,[4660363]=363,[4660364]=364,[4660365]=365,[4660366]=366,[4660367]=367,
  [4660368]=368,[4660369]=369,[4660370]=370,[4660371]=371,[4660372]=372,[4660373]=373,[4660374]=374,[4660375]=375,
  [4660376]=376,[4660377]=377,[4660378]=378,[4660379]=379,[4660380]=380,[4660381]=381,[4660382]=382,[4660383]=383,
  [4660384]=384,[4660385]=385,[4660386]=386,[4660387]=387,[4660388]=388,[4660389]=389,[4660390]=390,[4660391]=391,
  [4660392]=392,[4660393]=393,[4660394]=394,[4660395]=395,[4660396]=396,[4660397]=397,[4660398]=398,[4660399]=399,
  [4660400]=400,[4660401]=401,[4660402]=402,[4660403]=403,[4660404]=404,[4660405]=405,[4660406]=406,[4660407]=407,
  [4660408]=408,[4660409]=409,[4660410]=410,[4660411]=411,[4660412]=412,[4660413]=413,[4660414]=414,[4660415]=415,
  [4660416]=416,[4660417]=417,[4660418]=418,[4660419]=419,[4660420]=420,[4660421]=421,[4660422]=422,[4660423]=423,
  [4660424]=424,[4660425]=425,[4660426]=426,[4660427]=427,[4660428]=428,[4660429]=429,[4660430]=430,[4660431]=431,
  [4660432]=432,[4660433]=433,[4660434]=434,[4660435]=435,[4660436]=436,[4660437]=437,[4660438]=438,[4660439]=439,
  [4660440]=440,[4660441]=441,[4660442]=442,[4660443]=443,[4660444]=444,[4660445]=445,[4660446]=446,[4660447]=447,
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil     -- cached AP-signature result
local baseline         = nil     -- per-byte remembered flag state (init 0xFF)
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[mk64] " .. tostring(msg)) end
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
-- read_u16/u32 endianness. Any failed byte → nil (retry later).
--
-- read_u16_be / read_u32_be are part of the provided big-endian read API.
-- Detection + goal use only read_u8 (the status byte + the flag array bytes are
-- all single-byte). These multi-byte helpers belong to the deferred remote-item
-- path (the client stages a per-item reward and handshakes the received-item
-- counter — see receive_item), so they are provided ready for that path rather
-- than used by the scan today.
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

-- ── ROM identity: the AP patch writes "MK64 ARCHIPELAGO" at ROM 0x20 (the same
-- 16 bytes the client's validate_rom decodes and compares). Verifying it means
-- we only ever act on a patched MK64 cartridge — exactly the client's gate. ───
local function rom_is_ap()
  if rom_ok ~= nil then return rom_ok end
  for i = 1, #AP_SIG do
    local b = read_u8(AP_SIG_ADDR + i - 1, ROM)
    if b == nil then return false end          -- not readable yet; retry next poll
    if b ~= string.byte(AP_SIG, i) then
      rom_ok = false
      log("non-MK64-AP ROM (no 'MK64 ARCHIPELAGO' signature) — detection idle")
      return false
    end
  end
  rom_ok = true
  log("AP ROM verified ('MK64 ARCHIPELAGO' signature present)")
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
-- The client bails every poll unless game_status bit 0 (valid connection) is set.
local function status_byte()
  return read_u8(GAME_STATUS_ADDR, RDRAM)
end

local function is_connected(s)
  return s ~= nil and (s % 2) == 1   -- bit 0 set
end

-- ── Flag walk (diff the 56-byte array against the 0xFF baseline) ──────────────
-- Mirrors the client's game_watcher EXACTLY: a per-byte baseline initialised to
-- 0xFF, LSB-first bit diff, local flag_id = byte_i*8 + j, ap_id = flag_id +
-- ID_BASE. A bit is "newly checked" when it differs from the remembered baseline;
-- we then fold the live byte into the baseline so each transition reports once.
-- LOC-membership + server-location gating keep non-seed flag positions (which the
-- patch leaves at bit 0, already-different from 0xFF) from being reported.
local BITS = { 0x01, 0x02, 0x04, 0x08, 0x10, 0x20, 0x40, 0x80 }   -- 1 << j, LSB-first

local function ensure_baseline()
  if baseline ~= nil then return end
  baseline = {}
  for i = 0, FLAGS_SIZE - 1 do baseline[i] = 0xFF end
end

local function scan_into(new)
  ensure_baseline()
  for i = 0, FLAGS_SIZE - 1 do
    local byte = read_u8(FLAGS_ADDR + i, RDRAM)
    if byte ~= nil then
      local base = baseline[i]
      if byte ~= base then
        for j = 0, 7 do
          local bit = BITS[j + 1]
          local live_set = (byte % (bit * 2)) >= bit
          local base_set = (base % (bit * 2)) >= bit
          if live_set ~= base_set then
            local ap_id = (i * 8 + j) + ID_BASE
            if LOC[ap_id] ~= nil and not reported[ap_id] and wanted(ap_id) then
              reported[ap_id] = true
              new[#new + 1] = ap_id
            end
          end
        end
        baseline[i] = byte   -- fold in the live byte (client updates unchecked_locs[i])
      end
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
  ensure_baseline()
  local n = 0; for _ in pairs(LOC) do n = n + 1 end
  log("ready: " .. n .. " location flags (N64 big-endian, " .. FLAGS_SIZE .. "-byte array)")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_ap() then return new end
  if not is_connected(status_byte()) then return new end
  scan_into(new)
  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_ap() then return false end
  local s = status_byte()
  if not is_connected(s) then return false end
  -- game_clear is bit 1 of the status byte ((status >> 1) & 1).
  return (math.floor(s / STATUS_GAME_CLEAR) % 2) == 1
end

-- Remote multiworld items: see the file header. items_handling = 0b001 means the
-- patched game grants its own found items, so solo play and check reporting work
-- fully; applying REMOTE items is the client's guarded RDRAM staging path (item
-- id + classification + player-name + item-name at 0x40028E, handshaked against
-- the received-item counter at 0x40001A and GUARDED on the id byte reading back
-- 0xFF) and is the one piece deferred until it can be confirmed in-emulator.
-- No-op (never a wrong write) until then.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
