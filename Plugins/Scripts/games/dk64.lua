-- ═══════════════════════════════════════════════════════════════════════════════
-- dk64.lua — game module for the Archipelago BizHawk connector.
--            Donkey Kong 64 (Nintendo 64) — "Donkey Kong 64"
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the COMMUNITY
-- AP world worlds/dk64 of the repo
--   https://github.com/MultiworldGG/MultiworldGG  (worlds/dk64; archipelago.json
--   game "Donkey Kong 64", world_version 1.5.7, minimum_ap_version 0.6.5; authors
--   2dos / AlmostSeagull / Ballaam / Green Bean / Killklli / Lrauq / PoryGone /
--   Umed; DK64 Rando release at 2dos/DK64-Randomizer-Release).
-- The 857-entry location table was GENERATED directly by joining the world's own
--   worlds/dk64/archipelago/client/ap_check_ids.py  (check_names_to_id : name → AP id)
--   worlds/dk64/archipelago/client/check_flag_locations.py (location_flag_to_name :
--                                       EEPROM flag index → name, DROPSANITY_FLAG_START=0x426)
--   worlds/dk64/archipelago/client/items.py (item_ids : AP id → flag_id, fallback path)
-- on the location NAME — NOT hand-copied — and the EEPROM bit math + the shop-flag
-- formula + the victory check are replicated EXACTLY from the reference client
-- (DK64Client.py: readFlag / getCheckStatus / readChecks / is_victory). Loads
-- crash-free on any ROM; self-disables on a non-AP / unpatched cartridge.
--
-- WHY A COMMUNITY REPO: Donkey Kong 64 is NOT in ArchipelagoMW/Archipelago main.
-- MultiworldGG/MultiworldGG carries worlds/dk64 (the same source 2dos ships in the
-- DK64-Randomizer repo's archipelago/ folder). Source URLs are recorded above so
-- the table can be regenerated.
--
-- MEMORY MODEL (BizHawk N64 domains — derived from DK64Client.py + common.py)
-- ──────────────────────────────────────────────────────────────────────────
--   The DK64 AP client is a DIRECT-PROCESS-MEMORY EmuLoaderClient (PJ64 / Mupen /
--   BizHawk-DK64-Edition / RMG / ares / gopher / simple64), NOT a BizHawk Lua
--   client — but the RDRAM + ROM contents it reads are identical to BizHawk's
--   "RDRAM" / "ROM" Lua memory domains. It addresses RDRAM by ABSOLUTE N64 KSEG0
--   pointers (0x80xxxxxx) — DK64MemoryMap in common.py is a flat list of fixed
--   0x80xxxxxx addresses with NO pointer-chase for the flag/goal reads (unlike
--   Banjo-Tooie). We strip KSEG0 to a physical RDRAM offset (addr & 0x7FFFFF) and
--   read BizHawk's "RDRAM" domain there.
--
--   N64 IS BIG-ENDIAN. The few multi-byte reads the gates use (game-state bytes
--   are single-byte; this module only needs u8 for flags + state) need no byte
--   order; the big-endian u16/u32 helpers are provided for completeness and for
--   the deferred remote-item path. Single-byte flag/state reads are plain read_u8.
--
--   ROM SIGNATURE (DK64Client.run_game_loop, exact):
--     The client constructs EmuLoaderClient(signature_offset=0x759290,
--     signature_value=0x52414D42) and only proceeds once the emulator's ROM at
--     that offset reads 0x52414D42. 0x52414D42 big-endian = the ASCII bytes
--     52 41 4D 42 = "RAMB". The AP/randomizer ROM writes this marker at ROM
--     0x759290; a vanilla US cartridge has 7F 00 57 50 there (verified against the
--     real "Donkey Kong 64 (USA)" dump). We gate ALL detection on this signature
--     so a title-screen / unpatched / wrong cartridge can never report phantom
--     checks. (The DK64 randomizer also SHRINKS the ROM via static/patches/
--     shrink-dk64.bps before patching, so the playable .z64 is NOT 32 MiB and has
--     no fixed size/MD5 — the runtime "RAMB" signature is the stable detector.)
--
--   READINESS GATE (DK64Client.rom_ap_ready, exact):
--     rom_flags = u8 @ 0x807FF8C4 (ROM-side AP status byte); ready when
--     (rom_flags & 0x10) == 0x10. We mirror it as a second gate.
--
--   LOCATION FLAGS (DK64Client.readFlag / setFlag, exact — EEPROM bitfield):
--     EEPROM base = 0x807ECEA8 (physical RDRAM 0x7ECEA8). A flag with index `f`
--     lives at byte (EEPROM + (f >> 3)), bit (f & 7), tested LSB-FIRST:
--         readFlag(f) == ((read_u8(EEPROM + (f>>3)) >> (f & 7)) & 1)
--     A location is "checked" when readFlag(its flag index) == 1. The table
--     below maps AP location id → flag index, joined from the world's own data.
--
--     SHOP CHECKS: Cranky/Funky/Candy purchases have no per-location save flag in
--     location_flag_to_name; the client computes their flag from FLAG_SHOPFLAG=800
--     (DK64Client.getCheckStatus shop branch). Those computed indices are already
--     baked into the table during generation (e.g. "Japes Cranky Donkey" → 800),
--     so at runtime EVERY entry is a single readFlag — no special path needed.
--     "Shared" shop entries resolve to the DK (kong 0) flag, so several AP ids can
--     legitimately watch the SAME flag index; that is faithful to the client.
--
--   GAMEPLAY GATE (DK64Client.check_safe_gameplay, exact):
--     CurrentGamemode = u8 @ 0x80755314 must be in {6, 0xD} and
--     NextGamemode    = u8 @ 0x80755318 must be in {6, 0xA, 0xD}.
--     PLUS started_file: readFlag(0) == 1 (a save file has been started). We
--     require all three before scanning, mirroring the client's gates so the
--     zeroed/booting EEPROM on the title screen can never report phantom checks.
--
--   GOAL (DK64Client.is_victory, exact):
--     Standard win conditions (Beat K. Rool / Acquire Key 8 / Acquire Keys 3&8,
--     slot_data win_condition_item in {0,1,2}) → end_credits flag set:
--         readFlag(0x1B0) == 1                     -- FLAG index 432, end credits
--     All OTHER win conditions (the "Helm Hurry"-style goals) finish when EITHER
--     the Helm-Hurry-disabled flag is set OR the end credits play:
--         readFlag(0x3CB) == 1  (FLAG_HELM_HURRY_DISABLED, 971)  OR  readFlag(0x1B0)
--     win_condition_item + helm_hurry come from slot_data; we read them exactly as
--     the client does (defaulting to 0 = Beat K. Rool when absent).
--
-- WHAT THIS DOES (mirrors DK64Client.readChecks + is_victory)
--   • poll(): once the ROM signature + readiness + gameplay gates pass, evaluate
--     every known flag index → AP location ids, gated to the slot's server set.
--   • is_goal_complete(): win-condition-aware end-credits / helm-hurry check.
--   • receive_item(): NO-OP (documented). items_handling = 0b001 (DK64Client.launch
--     sets ctx.items_handling = 0b001) means the PATCHED GAME grants its own
--     locally-found items, so a SOLO seed plays fully and every check is reported.
--     Delivering REMOTE multiworld items is the client's intricate guarded RDRAM
--     write path (writeFedData / writeCountData / setFlag into the live save +
--     CountStruct, with a deliver-count handshake at memory_pointer+0x00 and
--     trap/deathlink/ring/tag-link interleaving). That is the one piece that needs
--     in-emulator verification before it is wired here, so it is intentionally
--     left out rather than shipped unverified (a wrong RDRAM write corrupts the
--     live save). Detection + goal reporting are fully functional regardless;
--     remote-item DELIVERY is the documented gap (see plugin ChecksImplemented).
--
--   NOT YET MODELLED (the 11 source locations with no plain EEPROM flag): the
--     "Banana Hoard" goal location (handled by the victory check, never a flag
--     scan) and the 10 "Helm <Kong> Barrel 1/2" minigame locations (no entry in
--     location_flag_to_name). These are intentionally absent from the table rather
--     than guessed; all 857 flag-backed locations are covered.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "dk64"

local ADDRESSES_VERIFIED = true   -- table generated from worlds/dk64 source

-- ── Memory domains (BizHawk N64) ──────────────────────────────────────────────
local RDRAM = "RDRAM"   -- console work RAM (EEPROM flags + game-state bytes)
local ROM   = "ROM"     -- cartridge (the "RAMB" AP signature)

-- ── Addresses / constants (worlds/dk64/archipelago, exact) ────────────────────
local KSEG0_MASK         = 0x7FFFFF    -- 0x80xxxxxx → physical RDRAM offset (8 MB)

local EEPROM_ADDR        = 0x7ECEA8    -- RDRAM: EEPROM flag bitfield base (0x807ECEA8)

local CUR_GAMEMODE_ADDR  = 0x755314    -- RDRAM: u8 CurrentGamemode (0x80755314)
local NEXT_GAMEMODE_ADDR  = 0x755318    -- RDRAM: u8 NextGamemode (0x80755318)
-- check_safe_gameplay: current in {6,0xD} and next in {6,0xA,0xD}
local CUR_OK  = { [0x6] = true, [0xD] = true }
local NEXT_OK = { [0x6] = true, [0xA] = true, [0xD] = true }

local ROM_FLAGS_ADDR     = 0x7FF8C4    -- RDRAM: u8 ROM-side AP status (0x807FF8C4)
local ROM_FLAG_AP_STATUS = 0x10        -- (rom_flags & 0x10)==0x10 → AP ready

-- ROM signature: EmuLoaderClient(signature_offset=0x759290, value=0x52414D42="RAMB")
local AP_SIG_ADDR        = 0x759290    -- ROM offset
local AP_SIG_BYTES       = { 0x52, 0x41, 0x4D, 0x42 }   -- "RAMB", big-endian order

-- Goal flag indices (is_victory):
local FLAG_STARTED       = 0           -- readFlag(0)==1 → file started
local FLAG_END_CREDITS   = 0x1B0       -- 432 — end credits (standard win)
local FLAG_HELM_HURRY    = 0x3CB       -- 971 — FLAG_HELM_HURRY_DISABLED (helm-hurry goals)

-- ── Location table (GENERATED — AP location id → EEPROM flag index) ────────────
-- 857 entries. A location is checked when readFlag(value) == 1 (LSB-first bit in
-- the EEPROM bitfield). Several AP ids may share one flag index (Shared shops →
-- the DK/kong-0 flag), exactly as DK64Client.getCheckStatus computes.
local LOC = {
  [14041140]=381,[14041141]=420,[14041142]=425,[14041143]=421,[14041144]=422,[14041145]=424,
  [14041146]=431,[14041147]=606,[14041148]=607,[14041149]=429,[14041150]=377,[14041151]=301,
  [14041152]=398,[14041153]=402,[14041154]=419,[14041155]=416,[14041156]=615,[14041157]=404,
  [14041158]=507,[14041159]=593,[14041160]=403,[14041161]=508,[14041162]=423,[14041163]=428,
  [14041164]=614,[14041165]=594,[14041166]=411,[14041167]=410,[14041168]=506,[14041169]=415,
  [14041170]=505,[14041171]=406,[14041172]=504,[14041174]=549,[14041175]=550,[14041176]=551,
  [14041177]=552,[14041178]=553,[14041179]=6,[14041180]=4,[14041181]=5,[14041182]=20,
  [14041183]=3,[14041184]=18,[14041185]=23,[14041186]=19,[14041187]=21,[14041188]=25,
  [14041189]=22,[14041190]=609,[14041191]=31,[14041192]=1,[14041193]=2,[14041194]=469,
  [14041195]=472,[14041196]=8,[14041197]=28,[14041198]=9,[14041199]=11,[14041200]=470,
  [14041201]=471,[14041202]=589,[14041203]=10,[14041204]=590,[14041205]=24,[14041206]=12,
  [14041207]=473,[14041208]=26,[14041209]=554,[14041210]=555,[14041211]=556,[14041212]=557,
  [14041213]=558,[14041214]=51,[14041215]=49,[14041216]=474,[14041217]=475,[14041218]=65,
  [14041219]=64,[14041220]=66,[14041221]=67,[14041222]=68,[14041223]=610,[14041224]=62,
  [14041225]=54,[14041226]=63,[14041227]=52,[14041228]=477,[14041229]=57,[14041230]=56,
  [14041231]=60,[14041232]=58,[14041233]=601,[14041234]=59,[14041235]=478,[14041236]=75,
  [14041237]=70,[14041238]=77,[14041239]=73,[14041240]=72,[14041241]=600,[14041242]=71,
  [14041243]=476,[14041244]=74,[14041245]=559,[14041246]=560,[14041247]=561,[14041248]=562,
  [14041249]=563,[14041250]=122,[14041251]=135,[14041252]=137,[14041253]=124,[14041254]=483,
  [14041255]=602,[14041256]=591,[14041257]=126,[14041258]=125,[14041259]=127,[14041260]=481,
  [14041261]=611,[14041262]=139,[14041263]=134,[14041264]=112,[14041265]=117,[14041266]=132,
  [14041267]=130,[14041268]=118,[14041269]=123,[14041270]=121,[14041271]=136,[14041272]=480,
  [14041273]=482,[14041274]=128,[14041275]=113,[14041276]=115,[14041277]=116,[14041278]=114,
  [14041279]=479,[14041280]=138,[14041281]=564,[14041282]=565,[14041283]=566,[14041284]=567,
  [14041285]=568,[14041286]=182,[14041287]=487,[14041288]=612,[14041289]=592,[14041290]=154,
  [14041291]=486,[14041292]=204,[14041293]=192,[14041294]=485,[14041295]=157,[14041296]=191,
  [14041297]=166,[14041298]=193,[14041299]=488,[14041300]=165,[14041301]=163,[14041302]=164,
  [14041303]=484,[14041304]=202,[14041305]=167,[14041306]=183,[14041307]=184,[14041308]=200,
  [14041309]=198,[14041310]=199,[14041311]=201,[14041312]=603,[14041313]=197,[14041314]=186,
  [14041315]=187,[14041316]=188,[14041317]=189,[14041318]=190,[14041319]=168,[14041320]=569,
  [14041321]=570,[14041322]=571,[14041323]=572,[14041324]=573,[14041325]=215,[14041326]=211,
  [14041327]=227,[14041328]=254,[14041329]=492,[14041330]=228,[14041331]=490,[14041332]=493,
  [14041333]=613,[14041334]=225,[14041335]=226,[14041336]=224,[14041337]=250,[14041338]=249,
  [14041339]=491,[14041340]=205,[14041341]=219,[14041342]=214,[14041343]=247,[14041344]=221,
  [14041345]=216,[14041346]=595,[14041347]=217,[14041348]=489,[14041349]=235,[14041350]=596,
  [14041351]=209,[14041352]=253,[14041353]=768,[14041354]=236,[14041355]=574,[14041356]=575,
  [14041357]=576,[14041358]=577,[14041359]=578,[14041360]=298,[14041361]=294,[14041362]=295,
  [14041363]=297,[14041364]=268,[14041365]=494,[14041366]=495,[14041367]=496,[14041368]=497,
  [14041369]=259,[14041370]=271,[14041371]=270,[14041372]=498,[14041373]=275,[14041374]=274,
  [14041375]=281,[14041376]=279,[14041377]=597,[14041378]=278,[14041379]=276,[14041380]=616,
  [14041381]=261,[14041382]=262,[14041383]=293,[14041384]=608,[14041385]=264,[14041386]=260,
  [14041387]=263,[14041388]=292,[14041389]=579,[14041390]=580,[14041391]=581,[14041392]=582,
  [14041393]=583,[14041394]=350,[14041395]=501,[14041396]=502,[14041397]=320,[14041398]=319,
  [14041399]=499,[14041400]=605,[14041401]=313,[14041402]=305,[14041403]=604,[14041404]=325,
  [14041405]=306,[14041406]=323,[14041407]=617,[14041408]=351,[14041409]=322,[14041410]=314,
  [14041411]=500,[14041412]=310,[14041413]=311,[14041414]=318,[14041415]=308,[14041416]=309,
  [14041417]=315,[14041418]=503,[14041419]=326,[14041420]=353,[14041421]=316,[14041422]=317,
  [14041433]=618,[14041434]=584,[14041435]=588,[14041436]=587,[14041437]=586,[14041438]=585,
  [14041439]=598,[14041440]=599,[14041441]=380,[14041442]=835,[14041443]=800,[14041444]=801,
  [14041445]=802,[14041446]=803,[14041447]=804,[14041448]=840,[14041449]=841,[14041450]=842,
  [14041451]=843,[14041452]=844,[14041453]=805,[14041454]=806,[14041455]=875,[14041456]=876,
  [14041457]=877,[14041458]=878,[14041459]=879,[14041460]=810,[14041461]=811,[14041462]=812,
  [14041463]=813,[14041464]=814,[14041465]=850,[14041466]=885,[14041467]=820,[14041468]=860,
  [14041469]=827,[14041470]=828,[14041471]=829,[14041472]=865,[14041473]=890,[14041474]=830,
  [14041475]=870,[14041476]=895,[14041477]=379,[14041478]=800,[14041479]=840,[14041480]=805,
  [14041481]=807,[14041482]=808,[14041483]=809,[14041484]=845,[14041485]=845,[14041486]=846,
  [14041487]=847,[14041488]=848,[14041489]=849,[14041490]=875,[14041491]=810,[14041492]=850,
  [14041493]=851,[14041494]=852,[14041495]=853,[14041496]=854,[14041497]=880,[14041498]=880,
  [14041499]=881,[14041500]=882,[14041501]=883,[14041502]=884,[14041503]=815,[14041504]=815,
  [14041505]=816,[14041506]=817,[14041507]=818,[14041508]=819,[14041509]=855,[14041510]=855,
  [14041511]=856,[14041512]=857,[14041513]=858,[14041514]=859,[14041515]=885,[14041516]=886,
  [14041517]=887,[14041518]=888,[14041519]=889,[14041520]=820,[14041521]=821,[14041522]=822,
  [14041523]=823,[14041524]=824,[14041525]=860,[14041526]=861,[14041527]=862,[14041528]=863,
  [14041529]=864,[14041530]=825,[14041531]=825,[14041532]=826,[14041533]=865,[14041534]=866,
  [14041535]=867,[14041536]=868,[14041537]=869,[14041538]=890,[14041539]=891,[14041540]=892,
  [14041541]=893,[14041542]=894,[14041543]=830,[14041544]=831,[14041545]=832,[14041546]=833,
  [14041547]=834,[14041548]=870,[14041549]=871,[14041550]=872,[14041551]=873,[14041552]=874,
  [14041553]=895,[14041554]=896,[14041555]=897,[14041556]=898,[14041557]=899,[14041558]=835,
  [14041559]=836,[14041560]=837,[14041561]=838,[14041562]=839,[14041563]=1022,[14041564]=1023,
  [14041565]=1024,[14041566]=1025,[14041567]=1026,[14041568]=1027,[14041569]=1028,[14041570]=1029,
  [14041571]=1030,[14041572]=1031,[14041573]=1032,[14041574]=1033,[14041575]=1034,[14041576]=1035,
  [14041577]=1036,[14041578]=1037,[14041579]=1038,[14041580]=1039,[14041581]=1040,[14041582]=1041,
  [14041583]=1042,[14041584]=1043,[14041585]=1044,[14041586]=1045,[14041587]=1046,[14041588]=1047,
  [14041589]=1048,[14041590]=1049,[14041591]=1050,[14041592]=1051,[14041593]=1052,[14041594]=1053,
  [14041595]=1054,[14041596]=1055,[14041597]=1056,[14041598]=1057,[14041599]=1058,[14041600]=1059,
  [14041601]=1060,[14041602]=1061,[14041603]=900,[14041604]=901,[14041605]=902,[14041606]=903,
  [14041607]=904,[14041608]=905,[14041609]=906,[14041610]=907,[14041611]=908,[14041612]=909,
  [14041613]=910,[14041614]=911,[14041615]=912,[14041616]=913,[14041617]=914,[14041618]=915,
  [14041619]=916,[14041620]=917,[14041621]=918,[14041622]=919,[14041623]=920,[14041624]=921,
  [14041625]=922,[14041626]=923,[14041627]=924,[14041628]=925,[14041629]=926,[14041630]=927,
  [14041631]=928,[14041632]=929,[14041633]=930,[14041634]=931,[14041635]=932,[14041636]=933,
  [14041637]=934,[14041673]=678,[14041674]=679,[14041675]=680,[14041676]=681,[14041677]=682,
  [14041678]=683,[14041679]=684,[14041680]=685,[14041681]=686,[14041682]=687,[14041683]=688,
  [14041684]=689,[14041685]=690,[14041686]=691,[14041687]=692,[14041688]=693,[14041689]=940,
  [14041690]=941,[14041691]=942,[14041692]=943,[14041693]=944,[14041694]=945,[14041695]=946,
  [14041696]=947,[14041697]=948,[14041698]=949,[14041699]=950,[14041700]=951,[14041701]=952,
  [14041702]=694,[14041703]=695,[14041704]=696,[14041705]=697,[14041706]=698,[14041707]=699,
  [14041708]=700,[14041709]=701,[14041710]=702,[14041711]=703,[14041712]=704,[14041713]=705,
  [14041714]=706,[14041715]=707,[14041716]=708,[14041717]=709,[14041718]=1062,[14041719]=1063,
  [14041720]=1064,[14041721]=1065,[14041722]=1066,[14041723]=1067,[14041724]=1068,[14041725]=1069,
  [14041726]=1070,[14041727]=1071,[14041728]=1072,[14041729]=1073,[14041730]=1074,[14041731]=1075,
  [14041732]=1076,[14041733]=1077,[14041734]=1078,[14041735]=1079,[14041736]=1080,[14041737]=1081,
  [14041738]=1082,[14041739]=1083,[14041740]=1089,[14041741]=1090,[14041742]=1091,[14041743]=1092,
  [14041744]=1093,[14041745]=1094,[14041746]=1095,[14041747]=1096,[14041748]=1097,[14041749]=1098,
  [14041750]=1099,[14041751]=1100,[14041752]=1101,[14041753]=1102,[14041754]=1103,[14041755]=1104,
  [14041756]=1105,[14041757]=1106,[14041758]=1107,[14041759]=1108,[14041760]=1109,[14041761]=1110,
  [14041762]=1111,[14041763]=1112,[14041764]=1113,[14041765]=1114,[14041766]=1115,[14041767]=1116,
  [14041768]=1117,[14041769]=1118,[14041770]=1119,[14041771]=1120,[14041772]=1121,[14041773]=1122,
  [14041774]=1123,[14041775]=1124,[14041776]=1125,[14041777]=1126,[14041778]=1127,[14041779]=1130,
  [14041780]=1131,[14041781]=1132,[14041782]=1133,[14041783]=1134,[14041784]=1135,[14041785]=1136,
  [14041786]=1137,[14041787]=1138,[14041788]=1139,[14041789]=1140,[14041790]=1141,[14041791]=1142,
  [14041792]=1143,[14041793]=1144,[14041794]=1145,[14041795]=1146,[14041796]=1147,[14041797]=1148,
  [14041798]=1149,[14041799]=1150,[14041800]=1151,[14041801]=1152,[14041802]=1153,[14041803]=1154,
  [14041804]=1155,[14041805]=1156,[14041806]=1157,[14041807]=1158,[14041808]=1159,[14041809]=1160,
  [14041810]=1161,[14041811]=1162,[14041812]=1163,[14041813]=1164,[14041814]=1165,[14041815]=1166,
  [14041816]=1167,[14041817]=1168,[14041818]=1169,[14041819]=1190,[14041820]=1191,[14041821]=1192,
  [14041822]=1193,[14041823]=1194,[14041824]=1195,[14041825]=1196,[14041826]=1197,[14041827]=1198,
  [14041828]=1199,[14041829]=1201,[14041830]=1202,[14041831]=1203,[14041832]=1204,[14041833]=1205,
  [14041834]=1206,[14041835]=1207,[14041836]=1208,[14041837]=1209,[14041838]=1210,[14041839]=1211,
  [14041840]=1212,[14041841]=1213,[14041842]=1214,[14041843]=1215,[14041844]=1216,[14041845]=1217,
  [14041846]=1218,[14041847]=1219,[14041848]=1238,[14041849]=1239,[14041850]=1240,[14041851]=1241,
  [14041852]=1242,[14041853]=1246,[14041854]=1247,[14041855]=1248,[14041856]=1249,[14041857]=1264,
  [14041858]=1265,[14041859]=1266,[14041860]=1267,[14041861]=1268,[14041862]=1269,[14041863]=1270,
  [14041864]=1271,[14041865]=1272,[14041866]=1273,[14041867]=1274,[14041868]=1275,[14041869]=1276,
  [14041870]=1277,[14041871]=1278,[14041872]=1279,[14041873]=1280,[14041874]=1281,[14041875]=1282,
  [14041876]=1283,[14041877]=1284,[14041878]=1285,[14041879]=1286,[14041880]=1287,[14041881]=1288,
  [14041882]=1289,[14041883]=1290,[14041884]=1291,[14041885]=1292,[14041886]=1293,[14041887]=1294,
  [14041888]=1295,[14041889]=1296,[14041890]=1297,[14041891]=1298,[14041892]=1299,[14041893]=1300,
  [14041894]=1301,[14041895]=1302,[14041896]=1303,[14041897]=1304,[14041898]=1305,[14041899]=1306,
  [14041900]=1307,[14041901]=1308,[14041902]=1309,[14041903]=1310,[14041904]=1311,[14041905]=1312,
  [14041906]=1313,[14041907]=1314,[14041908]=1315,[14041909]=1319,[14041910]=1320,[14041911]=1321,
  [14041912]=1325,[14041913]=1326,[14041914]=1327,[14041915]=1328,[14041916]=1329,[14041917]=1330,
  [14041918]=1331,[14041919]=1332,[14041920]=1334,[14041921]=1335,[14041922]=1341,[14041923]=1347,
  [14041924]=1370,[14041925]=1371,[14041926]=1372,[14041927]=1373,[14041928]=1374,[14041929]=1375,
  [14041930]=1376,[14041931]=1377,[14041932]=1378,[14041933]=1379,[14041934]=1380,[14041935]=1381,
  [14041936]=1382,[14041937]=1383,[14041938]=1384,[14041939]=1385,[14041940]=1391,[14041941]=1392,
  [14041942]=1393,[14041943]=1394,[14041944]=1400,[14041945]=1401,[14041946]=1402,[14041947]=1403,
  [14041948]=1404,[14041949]=1405,[14041950]=1406,[14041951]=1407,[14041952]=1408,[14041953]=1409,
  [14041954]=1410,[14041955]=1411,[14041956]=1412,[14041957]=1413,[14041958]=1414,[14041959]=1415,
  [14041960]=1416,[14041961]=1417,[14041962]=1418,[14041963]=1419,[14041964]=1420,[14041965]=1421,
  [14041966]=1423,[14041967]=1424,[14041968]=1440,[14041969]=1441,[14041970]=1442,[14041971]=1443,
  [14041972]=1444,[14041973]=1445,[14041974]=1446,[14041975]=1447,[14041976]=1448,[14041977]=1459,
  [14041978]=1460,[14041979]=1461,[14041980]=1462,[14041981]=1463,[14041982]=1464,[14041983]=1465,
  [14041984]=1466,[14041985]=1467,[14041986]=1468,[14041987]=1469,[14041988]=1470,[14041989]=1471,
  [14041990]=1472,[14041991]=1473,[14041992]=1474,[14041993]=1475,[14041994]=1476,[14041995]=1477,
  [14041996]=1478,[14041997]=1479,[14041998]=1480,[14041999]=1481,[14042000]=1482,[14042001]=1483,
  [14042002]=1484,[14042003]=1485,[14042004]=1486,[14042005]=1487,[14042006]=1488,[14042007]=1489,
  [14042329]=982,[14042330]=983,[14042331]=984,[14042332]=985,[14042333]=986,[14042334]=987,
  [14042335]=988,[14042336]=989,[14042337]=990,[14042338]=991,[14042339]=992,[14042340]=993,
  [14042341]=994,[14042342]=995,[14042343]=996,[14042344]=997,[14042345]=998,[14042346]=999,
  [14042347]=1000,[14042348]=1001,[14042349]=1002,[14042350]=1003,[14042351]=1004,[14042352]=1005,
  [14042353]=1006,[14042354]=1007,[14042355]=1008,[14042356]=1009,[14042357]=1010,[14042358]=1011,
  [14042359]=1012,[14042360]=1013,[14042361]=1014,[14042362]=1015,[14042363]=1016,
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local slot_data        = nil    -- decoded slot_data (win_condition_item, helm_hurry)
local rom_ok           = nil    -- cached AP-signature result
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[dk64] " .. tostring(msg)) end
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

-- N64 is BIG-ENDIAN: assemble multi-byte values most-significant byte first (the
-- byte at the LOWEST address is the high byte), matching the client's
-- int.from_bytes(..., "big"). Built from read_u8 so we never depend on a core's
-- read_u16/u32 endianness. Detection + goal use only read_u8 (flags + state are
-- byte-sized); these helpers are provided ready for the deferred remote-item path
-- (the client reads the deliver-count u16 + CountStruct pointer u32 there).
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

-- ── EEPROM flag read (DK64Client.readFlag, exact — LSB-first) ─────────────────
-- byte at (EEPROM + (f>>3)), bit (f & 7): (byte >> bit) & 1.
local function read_flag(f)
  local byte = read_u8(EEPROM_ADDR + math.floor(f / 8), RDRAM)
  if byte == nil then return false end
  return (math.floor(byte / (2 ^ (f % 8))) % 2) >= 1
end

-- ── ROM identity: the AP/randomizer ROM writes "RAMB" (52 41 4D 42) at ROM
-- 0x759290; EmuLoaderClient validates exactly this before it does anything.
-- Verifying it means we only ever act on a real AP-patched DK64 ROM. ───────────
local function rom_is_ap()
  if rom_ok ~= nil then return rom_ok end
  for i = 1, #AP_SIG_BYTES do
    local b = read_u8(AP_SIG_ADDR + i - 1, ROM)
    if b == nil then return false end          -- not readable yet; retry next poll
    if b ~= AP_SIG_BYTES[i] then
      rom_ok = false
      log("non-AP / unpatched DK64 ROM (no 'RAMB' signature @ 0x759290) — detection idle")
      return false
    end
  end
  rom_ok = true
  log("AP ROM verified ('RAMB' signature present @ 0x759290)")
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

-- Pull slot_data (for the goal type). DK64Client.is_victory reads
-- slot_data["win_condition_item"] (default 0 = Beat K. Rool) and
-- slot_data["helm_hurry"]. Numbers come through as Lua numbers, booleans as bools.
local function load_slot_data(sd)
  if type(sd) ~= "table" then return end
  slot_data = sd
  log("slot_data loaded (win_condition_item=" .. tostring(sd.win_condition_item) ..
      ", helm_hurry=" .. tostring(sd.helm_hurry) .. ")")
end

local function sd_num(key, default)
  if type(slot_data) ~= "table" then return default end
  local v = slot_data[key]
  if type(v) == "number" then return v end
  return default
end

local function sd_truthy(key)
  if type(slot_data) ~= "table" then return false end
  local v = slot_data[key]
  return v == true or (type(v) == "number" and v ~= 0)
end

-- ── Detection gates (DK64Client.check_safe_gameplay + started_file, exact) ────
local function in_gameplay()
  local cur = read_u8(CUR_GAMEMODE_ADDR, RDRAM)
  if cur == nil or not CUR_OK[cur] then return false end
  local nxt = read_u8(NEXT_GAMEMODE_ADDR, RDRAM)
  if nxt == nil or not NEXT_OK[nxt] then return false end
  return true
end

local function rom_ap_ready()
  local f = read_u8(ROM_FLAGS_ADDR, RDRAM)
  return f ~= nil and (math.floor(f / ROM_FLAG_AP_STATUS) % 2) >= 1   -- (f & 0x10)~=0
end

-- ── Flag walk (DK64Client.readChecks, exact — readFlag per location) ──────────
-- Only called once rom_is_ap() + rom_ap_ready() + in_gameplay() + started_file
-- have all passed (M.poll gates them), so any set bit is a real check.
local function scan_into(new)
  for ap_id, flag in pairs(LOC) do
    if not reported[ap_id] and wanted(ap_id) and read_flag(flag) then
      reported[ap_id] = true
      new[#new + 1] = ap_id
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
  load_slot_data(cfg.slot_data)
  local n = 0; for _ in pairs(LOC) do n = n + 1 end
  log("ready: " .. n .. " location flags (N64 big-endian, EEPROM bitfield)")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_ap() then return new end           -- unpatched/wrong cart → idle
  if not rom_ap_ready() then return new end         -- ROM AP status not set yet
  if not in_gameplay() then return new end           -- not in a safe game state
  if not read_flag(FLAG_STARTED) then return new end -- no save file started yet
  scan_into(new)
  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_ap() then return false end
  -- win_condition_item: 0 Beat K. Rool / 1 Acquire Key 8 / 2 Acquire Keys 3&8 use
  -- ONLY the end-credits flag. Any other win condition (helm-hurry-style) finishes
  -- on EITHER the helm-hurry-disabled flag OR the end credits. (Exact mirror of
  -- DK64Client.is_victory; helm_hurry slot_data also forces the helm-hurry path.)
  local win = sd_num("win_condition_item", 0)
  local helm_hurry = sd_truthy("helm_hurry") or (win ~= 0 and win ~= 1 and win ~= 2)

  if read_flag(FLAG_END_CREDITS) then return true end
  if helm_hurry and read_flag(FLAG_HELM_HURRY) then return true end
  return false
end

-- Remote multiworld items: see the file header. items_handling = 0b001 means the
-- patched game grants its own found items, so solo play and check reporting work
-- fully; applying REMOTE items is the client's guarded RDRAM write path
-- (writeFedData / writeCountData / setFlag into the live save + CountStruct, with a
-- deliver-count handshake at memory_pointer+0x00 and trap/deathlink/ring/tag-link
-- interleaving) and is the one piece deferred until it can be confirmed
-- in-emulator. No-op (never a wrong write) until then.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
