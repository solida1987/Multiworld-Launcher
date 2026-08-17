-- ═══════════════════════════════════════════════════════════════════════════════
-- banjo_tooie.lua — game module for the Archipelago BizHawk connector.
--                   Banjo-Tooie (Nintendo 64) — "Banjo-Tooie"
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the
-- COMMUNITY AP world worlds/banjo_tooie of the repo
--   https://github.com/jjjj12212/Archipelago-BanjoTooie  (world_version 4.12.0,
--   archipelago.json game "Banjo-Tooie", minimum_ap_version 0.6.7).
-- The 1069-entry location table was GENERATED directly from
-- worlds/banjo_tooie/client/_flag_data.py (BY_CATEGORY, every (category,
-- flag_type, addr, bit, name) tuple), NOT hand-copied, and the bit math +
-- pointer-chase are replicated EXACTLY from the reference client
-- (client/state.py BTHReader + BTClient.py validate_bt_signature). Loads
-- crash-free on any ROM; self-disables on a non-AP / unpatched cartridge.
--
-- WHY A COMMUNITY REPO: Banjo-Tooie is NOT in ArchipelagoMW/Archipelago main
-- (the AP-main worlds/banjo_tooie path 404s). jjjj12212/Archipelago-BanjoTooie
-- is the de-facto world (authors jjjj12212 / g0goTBC / Austin; emu-loader by
-- Umed). Source URLs are recorded above so the table can be regenerated.
--
-- MEMORY MODEL (BizHawk N64 "RDRAM" domain — derived from the reference client)
-- ──────────────────────────────────────────────────────────────────────────
--   The reference client is a DIRECT-PROCESS-MEMORY EmuLoaderClient (PJ64 /
--   Mupen / BizHawk / ares / gopher), NOT a BizHawk Lua client — but the RDRAM
--   contents it reads are identical to BizHawk's "RDRAM" Lua memory domain. The
--   one difference is byte order of the *host storage*: that client compensates
--   for "Mupen64Plus-family byte-swap-within-word storage on LE hosts" by
--   rotating u8/u16 addresses (A xor 3 / A xor 2) and parsing pointers
--   little-endian. BizHawk's "RDRAM" Lua domain instead exposes N64 RAM in
--   LOGICAL BIG-ENDIAN, un-swapped order (exactly as the shipped cv64.lua N64
--   module reads it). So in THIS module:
--     • a u8 at physical RDRAM offset A is plain read_u8(A) — NO xor rotation
--       (the client's xor only undoes Mupen's LE word-swap, which BizHawk's
--       logical domain has already undone);
--     • a 32-bit pointer at offset A is read_u32_be(A) (N64 big-endian),
--       matching the client's int.from_bytes(...,"big") logical value.
--
--   POINTER-CHASE (BTClient.py BTHACK signature, exact):
--     The AP-patched ROM's inject_hooks() writes an "ap_memory_ptr_t" struct and
--     stores a pointer to it (AP_MEMORY_PTR) at a fixed physical RDRAM offset:
--       anchor_ptr = u32 @ RDRAM[0x400000]              (a 0x80xxxxxx KSEG0 ptr)
--       anchor     = anchor_ptr & 0x7FFFFFFF            (physical offset)
--     The struct holds 12 sub-pointers at struct offsets 0x04..0x30; the four
--     that carry location-flag bitmaps are:
--       real_flags     @ struct + 0x24   (most JIGGY/NOTE/etc. "real" save flags)
--       fake_flags     @ struct + 0x28   (cheato/amaze/station/silo "fake" flags)
--       nest_flags     @ struct + 0x2C   (egg/feather nests — no vanilla save flag)
--       signpost_flags @ struct + 0x30   (signpost reads)
--     plus pc_items @ struct + 0x14 (the AP item buffer; mumbo-token count for
--     token-based goals lives at pc_items + 62).
--
--   LOCATION FLAGS (client/state.py poll_all_locations, exact):
--     Each location btid has (flag_type, addr, bit). Checked when
--       (read_u8(<bitmap> + addr) >> bit) & 1 == 1        -- LSB-FIRST bits
--     where <bitmap> is the real/fake/nest/signpost buffer per flag_type. NEST
--     and SIGNPOST entries store a single "bytebit" index instead (byte = i//8,
--     bit = i%8). The AP LOCATION ID == the btid itself (Banjo-Tooie's
--     location_name_to_id maps name -> data.btid with NO base offset), so the
--     table keys ARE the ids we report.
--
--     SPECIAL CASES faithfully mirrored from poll_all_locations:
--       • STOPNSWAP: three btids (1230953/4/5) read real_flags, the rest
--         read fake_flags (STOPNSWAP_REAL_FLAG_BTIDS).
--       • HONEYB (5 btids): NOT per-flag — a 3-bit CUMULATIVE count from
--         fake_flags(0x98) bits 2|3|4 with weights 1|2|4 → "rewards so far"
--         (0..5); the first N HONEYB btids are then marked collected.
--       • SKIVVIES (6 btids): per-loc real flag OR a completion override at
--         real_flags(0x81,3) that marks all of them at once.
--       • SCRAT (btid 1231007): true iff real_flags(0x26,6) AND NOT
--         real_flags(0x2C,1) (healed but not yet trained).
--
--   GOAL (client/game.py check_victory, exact — depends on the seed's
--   victory_condition option carried in slot_data.options):
--     0/4/6 (Defeat HAG-1 / Wonder Wing / Boss Hunt+HAG-1) → real_flags(0x03,3)
--            ("Hag 1 Defeated", btid 1230027). This is the DEFAULT goal.
--     1 (Minigame Hunt) → mumbo_tokens(pc_items+62) >= minigame_hunt_length
--     2 (Boss Hunt)     → mumbo_tokens >= boss_hunt_length
--     3 (Jinjo Family)  → mumbo_tokens >= jinjo_family_rescue_length AND map==0x191
--     5 (Token Hunt)    → mumbo_tokens >= token_hunt_length AND current_map==0x191
--     Token goals return false when their length option is missing (never
--     auto-finish on partial data). current_map = u16 BE @ n64_ptr(struct+0x20)+6.
--
-- WHAT THIS DOES (mirrors the reference client's poller + check_victory)
--   • poll(): chase the anchor once, read the four flag buffers, evaluate every
--     known btid (incl. the four special cases) → AP location ids, gated to the
--     slot's server location set. Gated behind a valid BTHACK signature so a
--     title-screen / unpatched / wrong cartridge can never report phantom checks.
--   • is_goal_complete(): victory_condition-aware (above), from slot_data.
--   • receive_item(): NO-OP (documented). items_handling = 0b111 (FULL remote)
--     means EVERY item — including the slot's own — is delivered by the client
--     writing the per-item count into the pc_items buffer (game/write_received_
--     items). That guarded RDRAM write path (per-item index map + item-count
--     handshake, traps, deathlink/taglink) is the piece that needs in-emulator
--     verification before it is wired here, so it is intentionally left out
--     rather than shipped unverified (a wrong RDRAM write corrupts the live
--     game state). Detection + goal reporting are fully functional regardless;
--     remote-item DELIVERY is the documented gap (see plugin ChecksImplemented).
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "banjo_tooie"

local ADDRESSES_VERIFIED = true   -- tables generated from worlds/banjo_tooie source

-- ── Memory domain (BizHawk N64) ───────────────────────────────────────────────
local RDRAM = "RDRAM"   -- console work RAM (the BTHACK struct + flag buffers live here)

-- ── Pointer-chase constants (BTClient.py, exact) ──────────────────────────────
local RDRAM_BASE          = 0x80000000   -- KSEG0 start; the client's "rdram_base" logical base
local RDRAM_SIZE          = 0x800000     -- 8 MB (expansion pak; required by BT)
local ANCHOR_OFFSET       = 0x400000     -- physical RDRAM offset of AP_MEMORY_PTR
local BTHACK_STRUCT_SIZE  = 52

-- struct field offsets (BTHACK_SUB_POINTER_OFFSETS / client.state anchors)
local OFF_PC_ITEMS        = 0x14         -- pc_items buffer (mumbo-token count etc.)
local OFF_N64             = 0x20         -- n64 buffer (current_map at +6)
local OFF_REAL_FLAGS      = 0x24
local OFF_FAKE_FLAGS      = 0x28
local OFF_NEST_FLAGS      = 0x2C
local OFF_SIGNPOST_FLAGS  = 0x30
-- every sub-pointer the signature check validates (offsets 0x04..0x30)
local SUB_POINTER_OFFSETS = { 0x04, 0x08, 0x0C, 0x10, 0x14, 0x18, 0x1C,
                              0x20, 0x24, 0x28, 0x2C, 0x30 }

local N64_CURRENT_MAP     = 0x06         -- u16 BE within the n64 buffer
local END_GAME_MAP        = 0x191        -- 401 (HAG-1 room) — token-goal gate
local PC_ITEMS_MUMBOTOKEN = 62           -- pc_items + 62 = mumbo-token count (AP_ITEM_INDEX[1230798])

local GOAL_REAL_ADDR      = 0x03         -- real_flags(0x03, bit 3) = "Hag 1 Defeated"
local GOAL_REAL_BIT       = 3

-- ── Special-case flag math (client/state.py poll_all_locations, exact) ────────
local HONEYB_FAKE_ADDR    = 0x98         -- fake_flags(0x98) bits 2|3|4 → cumulative count
local HONEYB_BTIDS        = { 1230997, 1230998, 1230999, 1231000, 1231001 }
local SKIV_COMPLETE_ADDR  = 0x81         -- real_flags(0x81, bit 3) marks all skivvies done
local SKIV_COMPLETE_BIT   = 3
local SCRAT_BTID          = 1231007
local SCRAT_HEAL_ADDR, SCRAT_HEAL_BIT   = 0x26, 6   -- healed
local SCRAT_TRAIN_ADDR, SCRAT_TRAIN_BIT = 0x2C, 1   -- trained
-- STOPNSWAP btids that use real_flags instead of fake_flags (Items.py).
local STOPNSWAP_REAL = { [1230953]=true, [1230954]=true, [1230955]=true }

-- ── Location tables (GENERATED from worlds/banjo_tooie/client/_flag_data.py) ──
-- REAL/FAKE/STOPNSWAP/SKIV: value packs addr*8+bit (byte = v//8, bit = v%8).
-- NEST/SIGN: value is the raw "bytebit" index (byte = v//8, bit = v%8).
-- The keys ARE the AP location ids (== btid). HONEYB & SCRAT are NOT tables —
-- they are computed specially (above) because they are not 1:1 flag reads.
local REAL = {
  [1230027]=27,[1230046]=46,[1230521]=279,[1230522]=280,[1230523]=281,[1230524]=282,[1230525]=283,
  [1230526]=284,[1230527]=285,[1230528]=286,[1230529]=287,[1230530]=288,[1230531]=289,[1230532]=290,
  [1230533]=291,[1230534]=292,[1230535]=293,[1230536]=294,[1230537]=295,[1230538]=296,[1230539]=297,
  [1230540]=298,[1230541]=299,[1230542]=300,[1230543]=301,[1230544]=302,[1230545]=303,[1230546]=304,
  [1230547]=305,[1230548]=306,[1230549]=307,[1230550]=308,[1230551]=460,[1230552]=461,[1230553]=462,
  [1230554]=463,[1230555]=464,[1230556]=465,[1230557]=466,[1230558]=467,[1230559]=468,[1230560]=469,
  [1230561]=470,[1230562]=471,[1230563]=472,[1230564]=473,[1230565]=474,[1230566]=475,[1230567]=476,
  [1230568]=477,[1230569]=478,[1230570]=479,[1230571]=480,[1230572]=481,[1230573]=482,[1230574]=483,
  [1230575]=484,[1230576]=485,[1230577]=486,[1230578]=487,[1230579]=488,[1230580]=489,[1230581]=490,
  [1230582]=491,[1230583]=492,[1230584]=493,[1230585]=494,[1230586]=495,[1230587]=496,[1230588]=497,
  [1230589]=498,[1230590]=499,[1230591]=500,[1230592]=501,[1230593]=502,[1230594]=503,[1230595]=504,
  [1230596]=552,[1230597]=553,[1230598]=554,[1230599]=555,[1230600]=556,[1230601]=557,[1230602]=558,
  [1230603]=559,[1230604]=560,[1230605]=561,[1230606]=562,[1230607]=563,[1230608]=564,[1230609]=565,
  [1230610]=566,[1230611]=567,[1230612]=568,[1230613]=569,[1230614]=570,[1230615]=571,[1230616]=572,
  [1230617]=573,[1230618]=574,[1230619]=575,[1230620]=576,[1230621]=577,[1230622]=578,[1230623]=579,
  [1230624]=580,[1230625]=581,[1230626]=582,[1230627]=583,[1230628]=584,[1230629]=585,[1230630]=586,
  [1230631]=587,[1230632]=588,[1230633]=589,[1230634]=590,[1230635]=591,[1230636]=592,[1230637]=593,
  [1230638]=594,[1230639]=595,[1230640]=596,[1230641]=597,[1230642]=598,[1230643]=599,[1230644]=600,
  [1230645]=601,[1230646]=602,[1230647]=603,[1230648]=604,[1230649]=605,[1230650]=606,[1230651]=607,
  [1230652]=608,[1230653]=609,[1230654]=610,[1230655]=611,[1230656]=612,[1230657]=613,[1230658]=614,
  [1230659]=615,[1230660]=616,[1230661]=617,[1230662]=618,[1230663]=619,[1230664]=620,[1230665]=621,
  [1230666]=622,[1230667]=623,[1230668]=624,[1230669]=625,[1230670]=626,[1230671]=627,[1230672]=628,
  [1230673]=629,[1230674]=630,[1230675]=631,[1230676]=632,[1230677]=633,[1230678]=634,[1230679]=635,
  [1230680]=636,[1230681]=637,[1230682]=638,[1230683]=639,[1230684]=640,[1230685]=641,[1230686]=535,
  [1230687]=536,[1230688]=537,[1230689]=538,[1230690]=539,[1230691]=540,[1230692]=541,[1230693]=542,
  [1230694]=543,[1230695]=544,[1230696]=545,[1230697]=546,[1230698]=547,[1230699]=548,[1230700]=549,
  [1230701]=550,[1230702]=551,[1230703]=506,[1230704]=507,[1230705]=508,[1230706]=509,[1230707]=510,
  [1230708]=511,[1230709]=512,[1230710]=513,[1230711]=514,[1230712]=515,[1230713]=516,[1230714]=517,
  [1230715]=518,[1230716]=519,[1230717]=520,[1230718]=521,[1230719]=522,[1230720]=523,[1230721]=524,
  [1230722]=525,[1230723]=526,[1230724]=527,[1230725]=528,[1230726]=529,[1230727]=530,[1230728]=691,
  [1230729]=692,[1230730]=693,[1230731]=694,[1230732]=695,[1230733]=696,[1230734]=697,[1230735]=698,
  [1230736]=699,[1230737]=700,[1230738]=701,[1230739]=702,[1230740]=703,[1230741]=704,[1230742]=705,
  [1230743]=706,[1230744]=707,[1230745]=708,[1230746]=709,[1230747]=710,[1230748]=711,[1230749]=712,
  [1230750]=713,[1230751]=714,[1230752]=715,[1230777]=1270,[1230778]=1270,[1230781]=1079,[1230782]=1096,
  [1230783]=1113,[1230784]=1130,[1230785]=1147,[1230786]=1164,[1230787]=1181,[1230788]=1198,[1230789]=1215,
  [1230796]=94,[1230800]=1063,[1230801]=1064,[1230802]=1065,[1230803]=1066,[1230804]=1067,[1230805]=1068,
  [1230806]=1069,[1230807]=1070,[1230808]=1071,[1230809]=1072,[1230810]=1073,[1230811]=1074,[1230812]=1075,
  [1230813]=1076,[1230814]=1077,[1230815]=1078,[1230816]=1080,[1230817]=1081,[1230818]=1082,[1230819]=1083,
  [1230820]=1084,[1230821]=1085,[1230822]=1086,[1230823]=1087,[1230824]=1088,[1230825]=1089,[1230826]=1090,
  [1230827]=1091,[1230828]=1092,[1230829]=1093,[1230830]=1094,[1230831]=1095,[1230832]=1097,[1230833]=1098,
  [1230834]=1099,[1230835]=1100,[1230836]=1101,[1230837]=1102,[1230838]=1103,[1230839]=1104,[1230840]=1105,
  [1230841]=1106,[1230842]=1107,[1230843]=1108,[1230844]=1109,[1230845]=1110,[1230846]=1111,[1230847]=1112,
  [1230848]=1114,[1230849]=1115,[1230850]=1116,[1230851]=1117,[1230852]=1118,[1230853]=1119,[1230854]=1120,
  [1230855]=1121,[1230856]=1122,[1230857]=1123,[1230858]=1124,[1230859]=1125,[1230860]=1126,[1230861]=1127,
  [1230862]=1128,[1230863]=1129,[1230864]=1131,[1230865]=1132,[1230866]=1133,[1230867]=1134,[1230868]=1135,
  [1230869]=1136,[1230870]=1137,[1230871]=1138,[1230872]=1139,[1230873]=1140,[1230874]=1141,[1230875]=1142,
  [1230876]=1143,[1230877]=1144,[1230878]=1145,[1230879]=1146,[1230880]=1148,[1230881]=1149,[1230882]=1150,
  [1230883]=1151,[1230884]=1152,[1230885]=1153,[1230886]=1154,[1230887]=1155,[1230888]=1156,[1230889]=1157,
  [1230890]=1158,[1230891]=1159,[1230892]=1160,[1230893]=1161,[1230894]=1162,[1230895]=1163,[1230896]=1165,
  [1230897]=1166,[1230898]=1167,[1230899]=1168,[1230900]=1169,[1230901]=1170,[1230902]=1171,[1230903]=1172,
  [1230904]=1173,[1230905]=1174,[1230906]=1175,[1230907]=1176,[1230908]=1177,[1230909]=1178,[1230910]=1179,
  [1230911]=1180,[1230912]=1182,[1230913]=1183,[1230914]=1184,[1230915]=1185,[1230916]=1186,[1230917]=1187,
  [1230918]=1188,[1230919]=1189,[1230920]=1190,[1230921]=1191,[1230922]=1192,[1230923]=1193,[1230924]=1194,
  [1230925]=1195,[1230926]=1196,[1230927]=1197,[1230928]=1199,[1230929]=1200,[1230930]=1201,[1230931]=1202,
  [1230932]=1203,[1230933]=1204,[1230934]=1205,[1230935]=1206,[1230936]=1207,[1230937]=1208,[1230938]=1209,
  [1230939]=1210,[1230940]=1211,[1230941]=1212,[1230942]=1213,[1230943]=1214,[1231002]=1000,[1231003]=1001,
  [1231004]=1002,[1231006]=98,[1231008]=311,[1231596]=100,[1231597]=101,[1231598]=102,[1231599]=842,
  [1231600]=843,[1231601]=844,[1231608]=948,[1231609]=949,[1231610]=1252,[1231611]=1253,[1231612]=1254,
  [1231613]=1255,[1231614]=725,[1231615]=724,[1231616]=735,[1231617]=734,[1231618]=733,[1231619]=731,
  [1231620]=732,[1231621]=730,[1231622]=729,[1231623]=738,[1231624]=739,[1231625]=741,[1231626]=740,
  [1231627]=742,[1231628]=743,[1231629]=737,[1231630]=736,[1231631]=744,[1231632]=745,[1231633]=746,
  [1231634]=748,[1231635]=747,[1231636]=726,[1231637]=727,[1231638]=728,[1231639]=790,[1231640]=789,
}
local FAKE = {
  [1230753]=219,[1230754]=218,[1230755]=217,[1230756]=241,[1230757]=222,[1230758]=223,[1230759]=242,
  [1230760]=224,[1230761]=225,[1230762]=238,[1230763]=244,[1230764]=226,[1230765]=227,[1230766]=228,
  [1230767]=243,[1230768]=235,[1230769]=236,[1230770]=237,[1230771]=232,[1230772]=233,[1230773]=234,
  [1230774]=230,[1230775]=231,[1230776]=239,[1230790]=315,[1230791]=316,[1230792]=424,[1230793]=423,
  [1230794]=987,[1230795]=110,[1230992]=68,[1230993]=69,[1230994]=70,[1230995]=71,[1230996]=72,
  [1231005]=240,[1231009]=229,[1231550]=773,[1231551]=774,[1231552]=775,[1231553]=776,[1231554]=777,
  [1231555]=778,[1231556]=779,[1231557]=900,[1231558]=901,[1231559]=902,[1231560]=903,[1231561]=904,
  [1231562]=905,[1231563]=906,[1231564]=907,[1231565]=908,[1231566]=909,[1231567]=910,[1231568]=911,
  [1231569]=912,[1231570]=913,[1231571]=914,[1231572]=915,[1231573]=916,[1231574]=917,[1231575]=918,
  [1231576]=919,[1231577]=920,[1231578]=921,[1231579]=922,[1231580]=923,[1231581]=924,[1231582]=925,
  [1231583]=926,[1231584]=927,[1231585]=928,[1231586]=929,[1231587]=930,[1231588]=931,[1231589]=932,
  [1231590]=933,[1231591]=934,[1231592]=935,[1231593]=936,[1231594]=940,[1231595]=941,
}
local NEST = {
  [1231010]=0,[1231011]=1,[1231012]=2,[1231013]=3,[1231014]=4,[1231015]=5,[1231016]=6,[1231017]=7,
  [1231018]=8,[1231019]=9,[1231020]=10,[1231021]=11,[1231022]=12,[1231023]=13,[1231024]=14,[1231025]=15,
  [1231026]=16,[1231027]=17,[1231028]=18,[1231029]=19,[1231030]=20,[1231031]=21,[1231032]=22,[1231033]=23,
  [1231034]=24,[1231035]=25,[1231036]=26,[1231037]=27,[1231038]=28,[1231039]=29,[1231040]=30,[1231041]=31,
  [1231042]=32,[1231043]=33,[1231044]=34,[1231045]=35,[1231046]=36,[1231047]=37,[1231048]=38,[1231049]=39,
  [1231050]=40,[1231051]=41,[1231052]=42,[1231053]=43,[1231054]=44,[1231055]=45,[1231056]=46,[1231057]=47,
  [1231058]=48,[1231059]=49,[1231060]=50,[1231061]=51,[1231062]=52,[1231063]=53,[1231064]=54,[1231065]=55,
  [1231066]=56,[1231067]=57,[1231068]=58,[1231069]=59,[1231070]=60,[1231071]=61,[1231072]=62,[1231073]=63,
  [1231074]=64,[1231075]=65,[1231076]=66,[1231077]=67,[1231078]=68,[1231079]=69,[1231080]=71,[1231081]=72,
  [1231082]=73,[1231083]=74,[1231084]=75,[1231085]=76,[1231086]=77,[1231087]=78,[1231088]=79,[1231089]=80,
  [1231090]=81,[1231091]=82,[1231092]=83,[1231093]=84,[1231094]=85,[1231095]=86,[1231096]=87,[1231097]=88,
  [1231098]=89,[1231099]=90,[1231100]=91,[1231101]=92,[1231102]=93,[1231103]=94,[1231104]=95,[1231105]=96,
  [1231106]=97,[1231107]=98,[1231108]=99,[1231109]=100,[1231110]=101,[1231111]=102,[1231112]=103,
  [1231113]=104,[1231114]=105,[1231115]=106,[1231116]=107,[1231117]=108,[1231118]=109,[1231119]=110,
  [1231120]=111,[1231121]=112,[1231122]=113,[1231123]=114,[1231124]=115,[1231125]=116,[1231126]=117,
  [1231127]=118,[1231128]=119,[1231129]=120,[1231130]=121,[1231131]=122,[1231132]=123,[1231133]=124,
  [1231134]=125,[1231135]=126,[1231136]=127,[1231137]=128,[1231138]=129,[1231139]=130,[1231140]=131,
  [1231141]=132,[1231142]=133,[1231143]=134,[1231144]=135,[1231145]=136,[1231146]=137,[1231147]=138,
  [1231148]=139,[1231149]=140,[1231150]=141,[1231151]=142,[1231152]=143,[1231153]=144,[1231154]=145,
  [1231155]=146,[1231156]=147,[1231157]=148,[1231158]=149,[1231159]=150,[1231160]=151,[1231161]=152,
  [1231162]=153,[1231163]=154,[1231164]=155,[1231165]=156,[1231166]=157,[1231167]=158,[1231168]=159,
  [1231169]=160,[1231170]=161,[1231171]=162,[1231172]=163,[1231173]=164,[1231174]=165,[1231175]=166,
  [1231176]=167,[1231177]=168,[1231178]=169,[1231179]=170,[1231180]=171,[1231181]=172,[1231182]=173,
  [1231183]=174,[1231184]=175,[1231185]=176,[1231186]=177,[1231187]=178,[1231188]=179,[1231189]=180,
  [1231190]=181,[1231191]=182,[1231192]=183,[1231193]=184,[1231194]=185,[1231195]=186,[1231196]=187,
  [1231197]=188,[1231198]=189,[1231199]=190,[1231200]=191,[1231201]=192,[1231202]=193,[1231203]=194,
  [1231204]=195,[1231205]=196,[1231206]=197,[1231207]=198,[1231208]=199,[1231209]=200,[1231210]=201,
  [1231211]=202,[1231212]=203,[1231213]=204,[1231214]=205,[1231215]=206,[1231216]=207,[1231217]=208,
  [1231218]=209,[1231219]=210,[1231220]=211,[1231221]=212,[1231222]=213,[1231223]=214,[1231224]=215,
  [1231225]=216,[1231226]=217,[1231227]=218,[1231228]=219,[1231229]=220,[1231230]=221,[1231231]=222,
  [1231232]=223,[1231233]=224,[1231234]=225,[1231235]=226,[1231236]=227,[1231237]=228,[1231238]=229,
  [1231239]=230,[1231240]=231,[1231241]=232,[1231242]=233,[1231243]=234,[1231244]=235,[1231245]=236,
  [1231246]=237,[1231247]=238,[1231248]=239,[1231249]=240,[1231250]=241,[1231251]=242,[1231252]=243,
  [1231253]=244,[1231254]=245,[1231255]=246,[1231256]=247,[1231257]=249,[1231258]=248,[1231259]=250,
  [1231260]=251,[1231261]=252,[1231262]=253,[1231263]=254,[1231264]=255,[1231265]=256,[1231266]=257,
  [1231267]=258,[1231268]=259,[1231269]=260,[1231270]=261,[1231271]=262,[1231272]=263,[1231273]=264,
  [1231274]=265,[1231275]=266,[1231276]=267,[1231277]=268,[1231278]=269,[1231279]=270,[1231280]=271,
  [1231281]=272,[1231282]=273,[1231283]=274,[1231284]=275,[1231285]=276,[1231286]=277,[1231287]=278,
  [1231288]=279,[1231289]=280,[1231290]=281,[1231291]=282,[1231292]=283,[1231293]=284,[1231294]=285,
  [1231295]=286,[1231296]=287,[1231297]=288,[1231298]=289,[1231299]=290,[1231300]=291,[1231301]=292,
  [1231302]=293,[1231303]=294,[1231304]=295,[1231305]=296,[1231306]=297,[1231307]=298,[1231308]=299,
  [1231309]=300,[1231310]=301,[1231311]=302,[1231312]=303,[1231313]=304,[1231314]=305,[1231315]=306,
  [1231316]=307,[1231317]=308,[1231318]=309,[1231319]=310,[1231320]=311,[1231321]=312,[1231322]=313,
  [1231323]=314,[1231324]=315,[1231325]=316,[1231326]=317,[1231327]=318,[1231328]=319,[1231329]=320,
  [1231330]=321,[1231331]=322,[1231332]=323,[1231333]=324,[1231334]=325,[1231335]=334,[1231336]=326,
  [1231337]=327,[1231338]=335,[1231339]=328,[1231340]=329,[1231341]=330,[1231342]=331,[1231343]=332,
  [1231344]=333,[1231345]=336,[1231346]=337,[1231347]=338,[1231348]=339,[1231349]=340,[1231350]=341,
  [1231351]=342,[1231352]=343,[1231353]=344,[1231354]=345,[1231355]=346,[1231356]=347,[1231357]=348,
  [1231358]=349,[1231359]=353,[1231360]=350,[1231361]=351,[1231362]=352,[1231363]=354,[1231364]=355,
  [1231365]=356,[1231366]=357,[1231367]=358,[1231368]=359,[1231369]=360,[1231370]=361,[1231371]=362,
  [1231372]=363,[1231373]=364,[1231374]=365,[1231375]=366,[1231376]=367,[1231377]=368,[1231378]=369,
  [1231379]=370,[1231380]=371,[1231381]=372,[1231382]=373,[1231383]=374,[1231384]=375,[1231385]=376,
  [1231386]=377,[1231387]=378,[1231388]=379,[1231389]=380,[1231390]=381,[1231391]=382,[1231392]=383,
  [1231393]=384,[1231394]=385,[1231395]=386,[1231396]=387,[1231397]=388,[1231398]=389,[1231399]=390,
  [1231400]=391,[1231401]=392,[1231402]=393,[1231403]=394,[1231404]=395,[1231405]=396,[1231406]=397,
  [1231407]=398,[1231408]=399,[1231409]=400,[1231410]=401,[1231411]=402,[1231412]=403,[1231413]=404,
  [1231414]=405,[1231415]=406,[1231416]=407,[1231417]=408,[1231418]=409,[1231419]=410,[1231420]=411,
  [1231421]=412,[1231422]=413,[1231423]=414,[1231424]=415,[1231425]=416,[1231426]=417,[1231427]=418,
  [1231428]=419,[1231429]=420,[1231430]=421,[1231431]=422,[1231432]=423,[1231433]=424,[1231434]=425,
  [1231435]=426,[1231436]=427,[1231437]=428,[1231438]=429,[1231439]=430,[1231440]=431,[1231441]=432,
  [1231442]=433,[1231443]=434,[1231444]=435,[1231445]=436,[1231446]=437,[1231447]=438,[1231448]=439,
  [1231449]=440,[1231450]=441,[1231451]=442,[1231452]=444,[1231453]=443,[1231454]=445,[1231455]=446,
  [1231456]=447,[1231457]=448,[1231458]=449,[1231459]=450,[1231460]=451,[1231461]=452,[1231462]=453,
  [1231463]=454,[1231464]=455,[1231465]=457,[1231466]=456,[1231467]=458,[1231468]=459,[1231469]=460,
  [1231470]=461,[1231471]=462,[1231472]=463,[1231473]=464,[1231474]=465,[1231475]=466,[1231476]=467,
  [1231477]=468,[1231478]=469,[1231479]=470,[1231480]=471,[1231481]=472,[1231482]=70,
}
local SIGN = {
  [1231483]=0,[1231484]=5,[1231485]=3,[1231486]=2,[1231487]=4,[1231488]=1,[1231489]=6,[1231490]=7,
  [1231491]=8,[1231492]=9,[1231493]=10,[1231494]=11,[1231495]=12,[1231496]=13,[1231497]=16,[1231498]=15,
  [1231499]=14,[1231500]=17,[1231501]=18,[1231502]=19,[1231503]=20,[1231504]=21,[1231505]=22,[1231506]=25,
  [1231507]=24,[1231508]=23,[1231509]=26,[1231510]=27,[1231511]=28,[1231512]=29,[1231513]=30,[1231514]=31,
  [1231515]=36,[1231516]=32,[1231517]=33,[1231518]=35,[1231519]=34,[1231520]=37,[1231521]=38,[1231522]=39,
  [1231523]=40,[1231524]=41,[1231525]=42,[1231526]=43,[1231527]=48,[1231528]=49,[1231529]=50,[1231530]=51,
  [1231531]=44,[1231532]=45,[1231533]=46,[1231534]=47,[1231535]=53,[1231536]=52,[1231537]=55,[1231538]=54,
  [1231539]=56,[1231540]=57,[1231541]=59,[1231542]=58,[1231543]=60,
}
-- STOPNSWAP: mixed real/fake at poll time (STOPNSWAP_REAL gates which buffer).
local STOPNSWAP = {
  [1230953]=959,[1230954]=958,[1230955]=956,[1230956]=957,[1230957]=955,[1230958]=954,
}
-- SKIVVIES: per-loc real flag OR the real_flags(0x81,3) completion override.
local SKIV = {
  [1231602]=1033,[1231603]=1034,[1231604]=1031,[1231605]=1032,[1231606]=1030,[1231607]=1029,
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local slot_options     = nil    -- slot_data.options table (goal type + lengths)
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[banjo_tooie] " .. tostring(msg)) end
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

-- N64 is BIG-ENDIAN in BizHawk's "RDRAM" Lua domain: assemble multi-byte values
-- most-significant byte first (byte at the LOWEST address is the high byte),
-- matching the reference client's LOGICAL int.from_bytes(...,"big") pointer
-- values. Built from read_u8 so we never depend on a core's read_u16/u32
-- endianness. Any failed byte → nil (retry next poll).
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

-- ── Pointer-chase (BTClient.py is_rdram_pointer / deref / get_anchor) ─────────
local function is_rdram_pointer(value)
  return value ~= nil and value >= RDRAM_BASE and value < (RDRAM_BASE + RDRAM_SIZE)
end

-- Read a u32 pointer at physical RDRAM offset `phys`, return its PHYSICAL offset
-- (KSEG0 mask stripped) iff it is a valid 0x80xxxxxx RDRAM pointer, else nil.
local function deref_phys(phys)
  local ptr = read_u32_be(phys, RDRAM)
  if not is_rdram_pointer(ptr) then return nil end
  return ptr % 0x80000000   -- == ptr & 0x7FFFFFFF (ptr < 0x80800000, so this is exact)
end

-- Physical offset of the BTHACK struct (anchor), or nil.
local function get_anchor_phys()
  return deref_phys(ANCHOR_OFFSET)
end

-- ── Signature validation (BTClient.validate_bt_signature, exact) ──────────────
-- True iff RDRAM looks like AP-Banjo-Tooie: the anchor pointer is valid, the
-- struct fits in RDRAM, and ALL 12 sub-pointers (struct + 0x04..0x30) are
-- themselves valid RDRAM pointers (the patch's inject_hooks() populates them).
local function signature_ok()
  local anchor = get_anchor_phys()
  if anchor == nil then return false end
  if anchor + BTHACK_STRUCT_SIZE > RDRAM_SIZE then return false end
  for _, off in ipairs(SUB_POINTER_OFFSETS) do
    local ptr = read_u32_be(anchor + off, RDRAM)
    if ptr == nil then return false end            -- not readable yet; retry next poll
    if not is_rdram_pointer(ptr) then return false end
  end
  return true
end

-- Physical offset of a struct sub-buffer (e.g. real_flags), or nil.
local function sub_ptr_phys(field_offset)
  local anchor = get_anchor_phys()
  if anchor == nil then return nil end
  return deref_phys(anchor + field_offset)
end

-- ── Flag bit test (client.state.bit_at: LSB-first) ────────────────────────────
-- byte at (buffer_phys + addr), test bit `bit` (0..7), LSB-first: (byte>>bit)&1.
local function bit_at(buf_phys, addr, bit)
  if buf_phys == nil then return false end
  local byte = read_u8(buf_phys + addr, RDRAM)
  if byte == nil then return false end
  -- (byte >> bit) & 1, done with arithmetic (no bit ops needed in 5.1 Lua)
  return (math.floor(byte / (2 ^ bit)) % 2) >= 1
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

-- Pull slot_data.options out of the decoded ap_config (for the goal type).
-- slot_data mirrors the AP seed's slot_data; check_victory reads
-- slot_data["options"][...]. Numbers come through as Lua numbers.
local function load_slot_options(slot_data)
  if type(slot_data) ~= "table" then return end
  local opts = slot_data.options
  if type(opts) == "table" then
    slot_options = opts
    log("slot options loaded (victory_condition=" ..
        tostring(opts.victory_condition) .. ")")
  end
end

local function opt(key, default)
  if type(slot_options) ~= "table" then return default end
  local v = slot_options[key]
  if type(v) == "number" then return v end
  return default
end

-- ── Per-poll flag check helpers ───────────────────────────────────────────────
-- Record a newly-set location once, gated to the slot's server set.
local function add_if(new, ap_id, set)
  if set and not reported[ap_id] and wanted(ap_id) then
    reported[ap_id] = true
    new[#new + 1] = ap_id
  end
end

-- ── Flag walk ─────────────────────────────────────────────────────────────────
-- Mirrors client.state.poll_all_locations EXACTLY: read the four flag buffers
-- through the pointer-chase, then evaluate every btid by its flag_type, plus the
-- STOPNSWAP / HONEYB / SKIVVIES / SCRAT special cases. Only called once the
-- signature is verified (M.poll gates it), so any set bit is a real check.
local function scan_into(new)
  local real_buf = sub_ptr_phys(OFF_REAL_FLAGS)
  local fake_buf = sub_ptr_phys(OFF_FAKE_FLAGS)
  local nest_buf = sub_ptr_phys(OFF_NEST_FLAGS)
  local sign_buf = sub_ptr_phys(OFF_SIGNPOST_FLAGS)
  if not (real_buf and fake_buf and nest_buf and sign_buf) then return end

  -- real_flags 1:1
  for ap_id, packed in pairs(REAL) do
    add_if(new, ap_id, bit_at(real_buf, math.floor(packed / 8), packed % 8))
  end
  -- fake_flags 1:1
  for ap_id, packed in pairs(FAKE) do
    add_if(new, ap_id, bit_at(fake_buf, math.floor(packed / 8), packed % 8))
  end
  -- nest_flags (bytebit index)
  for ap_id, bb in pairs(NEST) do
    add_if(new, ap_id, bit_at(nest_buf, math.floor(bb / 8), bb % 8))
  end
  -- signpost_flags (bytebit index)
  for ap_id, bb in pairs(SIGN) do
    add_if(new, ap_id, bit_at(sign_buf, math.floor(bb / 8), bb % 8))
  end
  -- STOPNSWAP: three btids use real_flags, the rest use fake_flags.
  for ap_id, packed in pairs(STOPNSWAP) do
    local buf = STOPNSWAP_REAL[ap_id] and real_buf or fake_buf
    add_if(new, ap_id, bit_at(buf, math.floor(packed / 8), packed % 8))
  end
  -- HONEYB: cumulative 3-bit count from fake_flags(0x98) bits 2|3|4 (weights 1|2|4).
  local honeyb_count =
      (bit_at(fake_buf, HONEYB_FAKE_ADDR, 2) and 1 or 0)
    + (bit_at(fake_buf, HONEYB_FAKE_ADDR, 3) and 2 or 0)
    + (bit_at(fake_buf, HONEYB_FAKE_ADDR, 4) and 4 or 0)
  for i, ap_id in ipairs(HONEYB_BTIDS) do
    add_if(new, ap_id, (i - 1) < honeyb_count)   -- first N collected
  end
  -- SKIVVIES: per-loc real flag OR the real_flags(0x81,3) completion override.
  local skiv_complete = bit_at(real_buf, SKIV_COMPLETE_ADDR, SKIV_COMPLETE_BIT)
  for ap_id, packed in pairs(SKIV) do
    local per_loc = bit_at(real_buf, math.floor(packed / 8), packed % 8)
    add_if(new, ap_id, per_loc or skiv_complete)
  end
  -- SCRAT: healed (real 0x26,6) AND NOT trained (real 0x2C,1).
  do
    local healed = bit_at(real_buf, SCRAT_HEAL_ADDR, SCRAT_HEAL_BIT)
    local trained = bit_at(real_buf, SCRAT_TRAIN_ADDR, SCRAT_TRAIN_BIT)
    add_if(new, SCRAT_BTID, healed and not trained)
  end
end

-- ── Goal (client/game.py check_victory) ───────────────────────────────────────
-- Mumbo-token count from the pc_items buffer (pc_items + 62).
local function mumbo_tokens()
  local items = sub_ptr_phys(OFF_PC_ITEMS)
  if items == nil then return nil end
  return read_u8(items + PC_ITEMS_MUMBOTOKEN, RDRAM)
end

local function current_map()
  local n64 = sub_ptr_phys(OFF_N64)
  if n64 == nil then return nil end
  return read_u16_be(n64 + N64_CURRENT_MAP, RDRAM)
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
  load_slot_options(cfg.slot_data)
  local n = 0
  for _ in pairs(REAL) do n = n + 1 end
  for _ in pairs(FAKE) do n = n + 1 end
  for _ in pairs(NEST) do n = n + 1 end
  for _ in pairs(SIGN) do n = n + 1 end
  for _ in pairs(STOPNSWAP) do n = n + 1 end
  for _ in pairs(SKIV) do n = n + 1 end
  n = n + #HONEYB_BTIDS + 1   -- + SCRAT
  log("ready: " .. n .. " location flags (N64 big-endian, BTHACK pointer-chase)")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not signature_ok() then return new end       -- unpatched/title/wrong cart → idle
  scan_into(new)
  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not signature_ok() then return false end
  local goal_type = opt("victory_condition", 0)

  -- 0/4/6: HAG-1 defeated flag (the default goal). real_flags(0x03, bit 3).
  if goal_type == 0 or goal_type == 4 or goal_type == 6 then
    local real_buf = sub_ptr_phys(OFF_REAL_FLAGS)
    return bit_at(real_buf, GOAL_REAL_ADDR, GOAL_REAL_BIT)
  end

  -- 1/2: pure token-count hunts. False if the length option is missing.
  if goal_type == 1 or goal_type == 2 then
    local length = (goal_type == 1) and opt("minigame_hunt_length", -1)
                                     or  opt("boss_hunt_length", -1)
    if length <= 0 then return false end
    local tok = mumbo_tokens()
    return tok ~= nil and tok >= length
  end

  -- 3/5: token-count hunts that also require being on the HAG-1 map (0x191).
  if goal_type == 3 or goal_type == 5 then
    local length = (goal_type == 3) and opt("jinjo_family_rescue_length", -1)
                                     or  opt("token_hunt_length", -1)
    if length <= 0 then return false end
    if current_map() ~= END_GAME_MAP then return false end
    local tok = mumbo_tokens()
    return tok ~= nil and tok >= length
  end

  return false
end

-- Remote multiworld items: see the file header. items_handling = 0b111 (FULL
-- remote) means the client DELIVERS every item — including the slot's own — by
-- writing per-item counts into the pc_items buffer (game.write_received_items,
-- with a count handshake + trap/deathlink/taglink interleaving). That guarded
-- RDRAM write path is the one piece deferred until it can be confirmed
-- in-emulator; mis-writing it corrupts the live game. No-op (never a wrong
-- write) until then. Location detection + goal reporting are unaffected.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
