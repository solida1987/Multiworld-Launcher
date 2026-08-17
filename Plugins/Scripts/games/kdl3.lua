-- ═══════════════════════════════════════════════════════════════════════════════
-- kdl3.lua — game module for the Archipelago BizHawk connector.
--           Kirby's Dream Land 3 (SNES)
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the official
-- AP world worlds/kdl3 (client.py + client_addrs.py + locations.py, main branch).
-- The consumable and star location tables were GENERATED directly from
-- client_addrs.py (consumable_addrs / star_addrs), not hand-copied, so they are
-- exact and match the client's own check (byte == 0x01 at the table offset).
-- Loads and runs crash-free on any ROM; self-disables on a non-AP cartridge.
--
-- MEMORY MODEL (SNI → BizHawk)
-- ────────────────────────────
--   The kdl3 client is an SNIClient. SNI addresses translate to BizHawk SNES
--   memory domains. EVERY kdl3 address is SRAM_1_START(0xE00000)+off, i.e. the
--   FXPAK "SRAM_1" region — which is BizHawk's battery domain "CARTRAM"
--   (fallback "SRAM" on cores that name it so). There are NO WRAM reads in this
--   game's client, so this module reads CARTRAM exclusively. Offsets below are
--   already net of SRAM_1_START, so they index CARTRAM directly.
--
--   ROM identity: the basepatch writes "KDL3" at CARTRAM 0x8100 (KDL3_ROMNAME);
--   the client also requires "halken" at 0x80F0 and "ninten" at 0x8FF0 — all
--   three are verified before this module does anything.
--
-- WHAT THIS DOES (mirrors worlds/kdl3/client.py game_watcher)
--   • poll(): the client's new-checks scan —
--       - completed stages: 30 × u16 at COMPLETED_STAGES; stage i set (==1) →
--         AP id 0x770000+i.
--       - heart stars: 5 worlds (stride 7) × 6 levels at HEART_STARS; byte set →
--         AP id 0x770100 + 6*world + level.
--       - consumables (only when the consumables flag is on): byte==1 at
--         CONSUMABLES+offset → AP id 0x770300+index  (table from client_addrs).
--       - stars (only when the stars flag is on): byte==1 at STARS+offset →
--         the star's own AP id  (table from client_addrs).
--       - bosses: u16 boss-status bitfield; bits 1,3,5,7,9 → 0x770200..0x770204.
--     All gated to the slot's server location set AND to the client's own
--     in-game gates (save loaded, not a demo, not on a title/ending BGM).
--   • is_goal_complete(): mirrors the client's four-way goal test, indexed by the
--     active save slot:
--       goal 0 (Zero)        → BOSS_BUTCH_STATUS[save] == 0x01
--       goal 1 (Boss Butch)  → BOSS_BUTCH_STATUS[save] == 0x03
--       goal 2 (MG5)         → MG5_STATUS[save]        == 0x03
--       goal 3 (Jumping)     → JUMPING_STATUS[save]    == 0x03
--   • receive_item(): NO-OP for now (documented). items_handling = 0b101 means
--     the PATCHED GAME grants its own locally-found items (plus a remote start
--     inventory), so a SOLO seed plays fully and every check is reported in a
--     multiworld. Delivering REMOTE multiworld items is the client's intricate
--     in-game item-queue path (decode the AP item id into the game's 0x10/0x20/
--     0x40/0x80 queue encoding, then splice it into an 8-slot u16 ring at
--     ITEM_QUEUE only when the live queue has a free slot, and only while a
--     "non-menu" sub-queue is allowed). That handshake needs in-emulator
--     verification before it is wired, so it is intentionally left out rather
--     than shipped unverified (would risk corrupting the in-game item ring).
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "kdl3"

-- Tables generated from worlds/kdl3/client_addrs.py — exact, not hand-copied.
local ADDRESSES_VERIFIED = true

-- ── Memory domain ─────────────────────────────────────────────────────────────
-- Everything kdl3 reads is SRAM_1 → BizHawk "CARTRAM" (fallback "SRAM").
local CARTRAM = "CARTRAM"

-- ── SNI-space constants (client.py module top), as CARTRAM offsets ────────────
-- (already net of SRAM_1_START = 0xE00000).
local ROMNAME_OFF          = 0x8100   -- "KDL3" signature (read 0x15, check [:4])
local HALKEN_OFF           = 0x80F0   -- "halken"
local NINTEN_OFF           = 0x8FF0   -- "ninten"
local GOAL_ADDR            = 0x9012   -- u8: which goal this seed uses (0..3)
local CONSUMABLE_FLAG      = 0x9018   -- u8: 1 = consumable checks enabled
local STARS_FLAG           = 0x901A   -- u8: 1 = star checks enabled
local IS_DEMO              = 0x5AD5   -- u8: >0 → demo playback, ignore
local GAME_SAVE            = 0x3617   -- u8: active save-slot index
local CURRENT_BGM          = 0x733E   -- u8: title/opening/ending BGMs gate scans
local WORLD_UNLOCK         = 0x53CB   -- u8: >6 → save not loaded, ignore
local HEART_STARS          = 0x53A7   -- 5 worlds × stride 7, 6 levels each
local BOSS_STATUS          = 0x53D5   -- u16: boss-clear bitfield
local BOSS_BUTCH_STATUS    = 0x5EEA   -- per-save (×2); goal 0/1 source
local MG5_STATUS           = 0x5EE4   -- per-save (×2); goal 2 source
local JUMPING_STATUS       = 0x5EF0   -- per-save (×2); goal 3 source
local COMPLETED_STAGES     = 0x8200   -- 30 × u16; stage i set (==1)
local CONSUMABLES          = 0xA000   -- 1920-byte consumable-collected array
local STARS                = 0xB000   -- 1920-byte star-collected array

-- AP location-id bases (client.py game_watcher).
local STAGE_BASE  = 0x770000          -- + stage index (0..29)
local HEART_BASE  = 0x770100          -- + 6*world + level
local BOSS_BASE   = 0x770200          -- + boss index (0..4)

-- BGMs that mean "not in real gameplay" (null/title/opening/save-select/endings).
local SKIP_BGM = {
  [0x00]=true, [0x21]=true, [0x22]=true, [0x23]=true,
  [0x25]=true, [0x2A]=true, [0x2B]=true,
}

-- ── Location tables (GENERATED from client_addrs.py; do not hand-edit) ─────────
-- Consumables: AP id 0x770300+index -> byte offset inside the CONSUMABLES array.
-- Reported when consumables flag is on AND CONSUMABLES[offset] == 0x01.
local LOC_CONSUMABLE = {
[7799552]=14,[7799553]=15,[7799554]=84,[7799555]=138,[7799556]=139,[7799557]=204,[7799558]=214,[7799559]=215,
[7799560]=224,[7799561]=330,[7799562]=353,[7799563]=458,[7799564]=459,[7799565]=522,[7799566]=525,[7799567]=605,
[7799568]=606,[7799569]=630,[7799570]=671,[7799571]=672,[7799572]=693,[7799573]=791,[7799574]=851,[7799575]=883,
[7799576]=971,[7799577]=985,[7799578]=986,[7799579]=1024,[7799580]=1035,[7799581]=1036,[7799582]=1038,[7799583]=1039,
[7799584]=1170,[7799585]=1171,[7799586]=1377,[7799587]=1378,[7799588]=1413,[7799589]=1494,[7799590]=1666,[7799591]=1808,
[7799592]=1809,[7799593]=1816,[7799594]=1856,[7799595]=1857,
}
-- Stars: AP id -> byte offset inside the STARS array.
-- Reported when stars flag is on AND STARS[offset] == 0x01.
local LOC_STAR = {
[7799809]=0,[7799810]=1,[7799811]=2,[7799812]=3,[7799813]=4,[7799814]=5,[7799815]=7,[7799816]=8,
[7799817]=9,[7799818]=10,[7799819]=11,[7799820]=12,[7799821]=13,[7799822]=16,[7799823]=17,[7799824]=19,
[7799825]=20,[7799826]=21,[7799827]=22,[7799828]=23,[7799829]=24,[7799830]=25,[7799831]=26,[7799832]=65,
[7799833]=66,[7799834]=67,[7799835]=68,[7799836]=69,[7799837]=70,[7799838]=71,[7799839]=72,[7799840]=73,
[7799841]=74,[7799842]=76,[7799843]=77,[7799844]=78,[7799845]=79,[7799846]=80,[7799847]=81,[7799848]=82,
[7799849]=83,[7799850]=85,[7799851]=86,[7799852]=87,[7799853]=128,[7799854]=129,[7799855]=130,[7799856]=131,
[7799857]=132,[7799858]=133,[7799859]=134,[7799860]=135,[7799861]=136,[7799862]=137,[7799863]=140,[7799864]=141,
[7799865]=142,[7799866]=143,[7799867]=144,[7799868]=145,[7799869]=146,[7799870]=147,[7799871]=148,[7799872]=149,
[7799873]=150,[7799874]=151,[7799875]=152,[7799876]=153,[7799877]=154,[7799878]=155,[7799879]=156,[7799880]=157,
[7799881]=158,[7799882]=159,[7799883]=160,[7799884]=192,[7799885]=193,[7799886]=194,[7799887]=195,[7799888]=197,
[7799889]=198,[7799890]=199,[7799891]=200,[7799892]=201,[7799893]=203,[7799894]=205,[7799895]=206,[7799896]=207,
[7799897]=208,[7799898]=209,[7799899]=210,[7799900]=211,[7799901]=212,[7799902]=213,[7799903]=216,[7799904]=217,
[7799905]=218,[7799906]=219,[7799907]=220,[7799908]=221,[7799909]=222,[7799910]=225,[7799911]=227,[7799912]=228,
[7799913]=229,[7799914]=230,[7799915]=231,[7799916]=232,[7799917]=233,[7799918]=234,[7799919]=235,[7799920]=236,
[7799921]=257,[7799922]=258,[7799923]=259,[7799924]=260,[7799925]=261,[7799926]=262,[7799927]=263,[7799928]=264,
[7799929]=265,[7799930]=266,[7799931]=267,[7799932]=268,[7799933]=270,[7799934]=271,[7799935]=272,[7799936]=273,
[7799937]=275,[7799938]=276,[7799939]=277,[7799940]=278,[7799941]=279,[7799942]=280,[7799943]=281,[7799944]=282,
[7799945]=283,[7799946]=284,[7799947]=285,[7799948]=286,[7799949]=287,[7799950]=321,[7799951]=322,[7799952]=323,
[7799953]=324,[7799954]=325,[7799955]=326,[7799956]=327,[7799957]=328,[7799958]=329,[7799959]=332,[7799960]=334,
[7799961]=335,[7799962]=336,[7799963]=337,[7799964]=340,[7799965]=341,[7799966]=342,[7799967]=343,[7799968]=345,
[7799969]=346,[7799970]=347,[7799971]=348,[7799972]=349,[7799973]=350,[7799974]=351,[7799975]=354,[7799976]=355,
[7799977]=356,[7799978]=357,[7799979]=384,[7799980]=385,[7799981]=386,[7799982]=387,[7799983]=388,[7799984]=389,
[7799985]=391,[7799986]=392,[7799987]=393,[7799988]=394,[7799989]=396,[7799990]=397,[7799991]=398,[7799992]=399,
[7799993]=400,[7799994]=401,[7799995]=402,[7799996]=403,[7799997]=404,[7799998]=449,[7799999]=450,[7800000]=451,
[7800001]=453,[7800002]=454,[7800003]=455,[7800004]=456,[7800005]=457,[7800006]=460,[7800007]=461,[7800008]=462,
[7800009]=463,[7800010]=464,[7800011]=465,[7800012]=466,[7800013]=467,[7800014]=468,[7800015]=513,[7800016]=514,
[7800017]=515,[7800018]=516,[7800019]=517,[7800020]=518,[7800021]=519,[7800022]=520,[7800023]=521,[7800024]=523,
[7800025]=524,[7800026]=527,[7800027]=528,[7800028]=529,[7800029]=531,[7800030]=532,[7800031]=533,[7800032]=534,
[7800033]=535,[7800034]=536,[7800035]=537,[7800036]=576,[7800037]=577,[7800038]=578,[7800039]=579,[7800040]=580,
[7800041]=582,[7800042]=583,[7800043]=584,[7800044]=585,[7800045]=586,[7800046]=587,[7800047]=588,[7800048]=589,
[7800049]=590,[7800050]=591,[7800051]=592,[7800052]=593,[7800053]=594,[7800054]=595,[7800055]=596,[7800056]=597,
[7800057]=598,[7800058]=599,[7800059]=600,[7800060]=601,[7800061]=602,[7800062]=603,[7800063]=604,[7800064]=607,
[7800065]=608,[7800066]=609,[7800067]=610,[7800068]=611,[7800069]=612,[7800070]=613,[7800071]=614,[7800072]=615,
[7800073]=616,[7800074]=617,[7800075]=618,[7800076]=619,[7800077]=620,[7800078]=621,[7800079]=622,[7800080]=623,
[7800081]=624,[7800082]=625,[7800083]=626,[7800084]=627,[7800085]=628,[7800086]=629,[7800087]=640,[7800088]=641,
[7800089]=642,[7800090]=643,[7800091]=644,[7800092]=645,[7800093]=646,[7800094]=647,[7800095]=648,[7800096]=649,
[7800097]=650,[7800098]=651,[7800099]=652,[7800100]=653,[7800101]=654,[7800102]=655,[7800103]=656,[7800104]=657,
[7800105]=658,[7800106]=659,[7800107]=660,[7800108]=661,[7800109]=662,[7800110]=663,[7800111]=664,[7800112]=665,
[7800113]=666,[7800114]=667,[7800115]=668,[7800116]=669,[7800117]=670,[7800118]=674,[7800119]=675,[7800120]=676,
[7800121]=677,[7800122]=678,[7800123]=679,[7800124]=680,[7800125]=681,[7800126]=682,[7800127]=683,[7800128]=684,
[7800129]=686,[7800130]=687,[7800131]=688,[7800132]=689,[7800133]=690,[7800134]=691,[7800135]=692,[7800136]=694,
[7800137]=695,[7800138]=704,[7800139]=705,[7800140]=706,[7800141]=707,[7800142]=708,[7800143]=709,[7800144]=710,
[7800145]=711,[7800146]=712,[7800147]=713,[7800148]=714,[7800149]=715,[7800150]=716,[7800151]=717,[7800152]=718,
[7800153]=719,[7800154]=720,[7800155]=721,[7800156]=722,[7800157]=723,[7800158]=724,[7800159]=725,[7800160]=726,
[7800161]=769,[7800162]=770,[7800163]=771,[7800164]=772,[7800165]=773,[7800166]=774,[7800167]=775,[7800168]=776,
[7800169]=777,[7800170]=778,[7800171]=779,[7800172]=780,[7800173]=781,[7800174]=782,[7800175]=783,[7800176]=784,
[7800177]=785,[7800178]=786,[7800179]=787,[7800180]=788,[7800181]=789,[7800182]=790,[7800183]=832,[7800184]=833,
[7800185]=834,[7800186]=835,[7800187]=836,[7800188]=837,[7800189]=838,[7800190]=839,[7800191]=840,[7800192]=841,
[7800193]=842,[7800194]=843,[7800195]=844,[7800196]=845,[7800197]=846,[7800198]=847,[7800199]=848,[7800200]=849,
[7800201]=850,[7800202]=854,[7800203]=855,[7800204]=856,[7800205]=857,[7800206]=858,[7800207]=859,[7800208]=860,
[7800209]=861,[7800210]=862,[7800211]=863,[7800212]=864,[7800213]=865,[7800214]=866,[7800215]=867,[7800216]=868,
[7800217]=869,[7800218]=870,[7800219]=871,[7800220]=872,[7800221]=873,[7800222]=874,[7800223]=875,[7800224]=876,
[7800225]=877,[7800226]=878,[7800227]=879,[7800228]=880,[7800229]=881,[7800230]=882,[7800231]=896,[7800232]=897,
[7800233]=898,[7800234]=899,[7800235]=900,[7800236]=901,[7800237]=902,[7800238]=903,[7800239]=904,[7800240]=905,
[7800241]=960,[7800242]=961,[7800243]=962,[7800244]=963,[7800245]=964,[7800246]=965,[7800247]=966,[7800248]=967,
[7800249]=968,[7800250]=969,[7800251]=970,[7800252]=972,[7800253]=973,[7800254]=974,[7800255]=975,[7800256]=977,
[7800257]=978,[7800258]=979,[7800259]=980,[7800260]=981,[7800261]=982,[7800262]=983,[7800263]=984,[7800264]=1025,
[7800265]=1026,[7800266]=1027,[7800267]=1028,[7800268]=1029,[7800269]=1030,[7800270]=1031,[7800271]=1032,[7800272]=1033,
[7800273]=1034,[7800274]=1037,[7800275]=1040,[7800276]=1041,[7800277]=1042,[7800278]=1043,[7800279]=1044,[7800280]=1045,
[7800281]=1046,[7800282]=1049,[7800283]=1050,[7800284]=1051,[7800285]=1052,[7800286]=1053,[7800287]=1054,[7800288]=1055,
[7800289]=1056,[7800290]=1057,[7800291]=1058,[7800292]=1059,[7800293]=1060,[7800294]=1061,[7800295]=1062,[7800296]=1063,
[7800297]=1064,[7800298]=1065,[7800299]=1066,[7800300]=1067,[7800301]=1068,[7800302]=1069,[7800303]=1070,[7800304]=1152,
[7800305]=1154,[7800306]=1155,[7800307]=1156,[7800308]=1157,[7800309]=1158,[7800310]=1159,[7800311]=1160,[7800312]=1161,
[7800313]=1162,[7800314]=1163,[7800315]=1164,[7800316]=1165,[7800317]=1166,[7800318]=1167,[7800319]=1168,[7800320]=1169,
[7800321]=1173,[7800322]=1174,[7800323]=1175,[7800324]=1176,[7800325]=1177,[7800326]=1178,[7800327]=1216,[7800328]=1217,
[7800329]=1218,[7800330]=1219,[7800331]=1220,[7800332]=1221,[7800333]=1222,[7800334]=1223,[7800335]=1224,[7800336]=1225,
[7800337]=1226,[7800338]=1227,[7800339]=1228,[7800340]=1229,[7800341]=1230,[7800342]=1231,[7800343]=1232,[7800344]=1233,
[7800345]=1234,[7800346]=1235,[7800347]=1236,[7800348]=1237,[7800349]=1238,[7800350]=1239,[7800351]=1240,[7800352]=1241,
[7800353]=1242,[7800354]=1243,[7800355]=1244,[7800356]=1245,[7800357]=1246,[7800358]=1247,[7800359]=1248,[7800360]=1249,
[7800361]=1250,[7800362]=1251,[7800363]=1252,[7800364]=1253,[7800365]=1254,[7800366]=1255,[7800367]=1256,[7800368]=1257,
[7800369]=1258,[7800370]=1259,[7800371]=1260,[7800372]=1261,[7800373]=1262,[7800374]=1263,[7800375]=1264,[7800376]=1265,
[7800377]=1266,[7800378]=1267,[7800379]=1268,[7800380]=1269,[7800381]=1280,[7800382]=1281,[7800383]=1282,[7800384]=1283,
[7800385]=1284,[7800386]=1285,[7800387]=1286,[7800388]=1289,[7800389]=1290,[7800390]=1291,[7800391]=1292,[7800392]=1293,
[7800393]=1294,[7800394]=1295,[7800395]=1296,[7800396]=1297,[7800397]=1298,[7800398]=1299,[7800399]=1300,[7800400]=1301,
[7800401]=1302,[7800402]=1303,[7800403]=1344,[7800404]=1345,[7800405]=1346,[7800406]=1347,[7800407]=1348,[7800408]=1349,
[7800409]=1350,[7800410]=1351,[7800411]=1352,[7800412]=1354,[7800413]=1355,[7800414]=1356,[7800415]=1357,[7800416]=1358,
[7800417]=1359,[7800418]=1360,[7800419]=1361,[7800420]=1362,[7800421]=1363,[7800422]=1365,[7800423]=1366,[7800424]=1367,
[7800425]=1368,[7800426]=1369,[7800427]=1370,[7800428]=1371,[7800429]=1372,[7800430]=1374,[7800431]=1375,[7800432]=1376,
[7800433]=1379,[7800434]=1380,[7800435]=1381,[7800436]=1382,[7800437]=1383,[7800438]=1384,[7800439]=1385,[7800440]=1386,
[7800441]=1387,[7800442]=1388,[7800443]=1389,[7800444]=1390,[7800445]=1391,[7800446]=1392,[7800447]=1393,[7800448]=1394,
[7800449]=1395,[7800450]=1396,[7800451]=1397,[7800452]=1398,[7800453]=1408,[7800454]=1409,[7800455]=1410,[7800456]=1411,
[7800457]=1412,[7800458]=1414,[7800459]=1472,[7800460]=1473,[7800461]=1474,[7800462]=1475,[7800463]=1476,[7800464]=1477,
[7800465]=1478,[7800466]=1479,[7800467]=1480,[7800468]=1481,[7800469]=1482,[7800470]=1483,[7800471]=1484,[7800472]=1486,
[7800473]=1487,[7800474]=1488,[7800475]=1489,[7800476]=1490,[7800477]=1491,[7800478]=1495,[7800479]=1496,[7800480]=1497,
[7800481]=1498,[7800482]=1499,[7800483]=1500,[7800484]=1501,[7800485]=1502,[7800486]=1503,[7800487]=1504,[7800488]=1505,
[7800489]=1506,[7800490]=1507,[7800491]=1508,[7800492]=1536,[7800493]=1537,[7800494]=1538,[7800495]=1539,[7800496]=1540,
[7800497]=1541,[7800498]=1600,[7800499]=1601,[7800500]=1602,[7800501]=1603,[7800502]=1604,[7800503]=1605,[7800504]=1606,
[7800505]=1607,[7800506]=1612,[7800507]=1613,[7800508]=1614,[7800509]=1615,[7800510]=1616,[7800511]=1617,[7800512]=1618,
[7800513]=1619,[7800514]=1620,[7800515]=1621,[7800516]=1622,[7800517]=1664,[7800518]=1665,[7800519]=1667,[7800520]=1668,
[7800521]=1670,[7800522]=1671,[7800523]=1672,[7800524]=1673,[7800525]=1674,[7800526]=1675,[7800527]=1676,[7800528]=1677,
[7800529]=1678,[7800530]=1679,[7800531]=1680,[7800532]=1681,[7800533]=1682,[7800534]=1683,[7800535]=1684,[7800536]=1685,
[7800537]=1686,[7800538]=1730,[7800539]=1732,[7800540]=1734,[7800541]=1792,[7800542]=1793,[7800543]=1794,[7800544]=1795,
[7800545]=1796,[7800546]=1797,[7800547]=1798,[7800548]=1799,[7800549]=1800,[7800550]=1801,[7800551]=1802,[7800552]=1803,
[7800553]=1804,[7800554]=1805,[7800555]=1810,[7800556]=1811,[7800557]=1812,[7800558]=1813,[7800559]=1814,[7800560]=1815,
[7800561]=1817,[7800562]=1818,[7800563]=1819,[7800564]=1820,[7800565]=1821,[7800566]=1822,[7800567]=1823,[7800568]=1824,
[7800569]=1825,[7800570]=1826,[7800571]=1827,[7800572]=1828,[7800573]=1831,[7800574]=1832,[7800575]=1858,
}

-- ── Internal state ────────────────────────────────────────────────────────────
local reported         = {}    -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local consumables_on   = nil    -- cached CONSUMABLE_FLAG (nil until first read)
local stars_on         = nil    -- cached STARS_FLAG (nil until first read)
local rom_ok           = nil    -- cached signature result (nil until checked)
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[kdl3] " .. tostring(msg)) end
end

-- ── Memory API (resolved at init; 2-arg domain form + current-domain fallback) ─
local function resolve_memory_api()
  if not memory then return false end
  mem.read_u8  = memory.read_u8     or memory.readbyte
  mem.read_u16 = memory.read_u16_le or memory.readword
  return mem.read_u8 ~= nil
end

-- Read from CARTRAM, falling back to the "SRAM" domain name on cores that use it,
-- then to the current-domain (no-domain) form on the oldest APIs.
local function read_cartram_u8(off)
  local ok, v = pcall(mem.read_u8, off, CARTRAM)
  if ok and type(v) == "number" then return v end
  ok, v = pcall(mem.read_u8, off, "SRAM")
  if ok and type(v) == "number" then return v end
  ok, v = pcall(mem.read_u8, off)
  if ok and type(v) == "number" then return v end
  return nil
end

local function read_cartram_u16(off)
  if mem.read_u16 then
    local ok, v = pcall(mem.read_u16, off, CARTRAM)
    if ok and type(v) == "number" then return v end
    ok, v = pcall(mem.read_u16, off, "SRAM")
    if ok and type(v) == "number" then return v end
    ok, v = pcall(mem.read_u16, off)
    if ok and type(v) == "number" then return v end
  end
  -- Compose from two byte reads (little-endian) as a last resort.
  local lo = read_cartram_u8(off)
  local hi = read_cartram_u8(off + 1)
  if lo == nil or hi == nil then return nil end
  return lo + hi * 256
end

local function bit_and(a, b)
  -- 16-bit safe AND without the bit library (cores vary).
  local res, bitval = 0, 1
  while a > 0 and b > 0 do
    if a % 2 == 1 and b % 2 == 1 then res = res + bitval end
    a = math.floor(a / 2); b = math.floor(b / 2); bitval = bitval * 2
  end
  return res
end

-- ── ROM identity: "KDL3" + "halken" + "ninten" must all be present ────────────
local function read_str(off, n)
  local out = {}
  for i = 0, n - 1 do
    local b = read_cartram_u8(off + i)
    if b == nil then return nil end
    out[#out + 1] = string.char(b)
  end
  return table.concat(out)
end

local function rom_is_ap()
  if rom_ok ~= nil then return rom_ok end
  local name   = read_str(ROMNAME_OFF, 4)
  local halken = read_str(HALKEN_OFF, 6)
  local ninten = read_str(NINTEN_OFF, 6)
  if name == nil or halken == nil or ninten == nil then return false end  -- retry
  rom_ok = (name == "KDL3" and halken == "halken" and ninten == "ninten")
  if rom_ok then log("AP ROM verified ('KDL3' + halken + ninten present)")
  else log("non-AP ROM (signatures absent) — detection idle, no writes") end
  return rom_ok
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
  if server_locations == nil then return true end   -- no set provided → report all
  return server_locations[ap_id] == true
end

-- ── Detection gates (mirror client.py game_watcher early returns) ─────────────
local function in_gameplay()
  -- demo playback?
  local demo = read_cartram_u8(IS_DEMO)
  if demo == nil or demo > 0x00 then return false end
  -- save actually loaded? (world unlocks byte sanity, like the client)
  local wu = read_cartram_u8(WORLD_UNLOCK)
  if wu == nil or wu > 0x06 then return false end
  -- a title/opening/ending BGM means "not in a stage we can read from"
  local bgm = read_cartram_u8(CURRENT_BGM)
  if bgm == nil or SKIP_BGM[bgm] then return false end
  return true
end

local function read_flags()
  if consumables_on == nil then
    local c = read_cartram_u8(CONSUMABLE_FLAG)
    if c ~= nil then consumables_on = (c == 0x01) end
  end
  if stars_on == nil then
    local s = read_cartram_u8(STARS_FLAG)
    if s ~= nil then stars_on = (s == 0x01) end
  end
end

local function scan_into(new)
  -- Completed stages: 30 × u16 at COMPLETED_STAGES; stage i set (==1) → 0x770000+i.
  for i = 0, 29 do
    local ap_id = STAGE_BASE + i
    if not reported[ap_id] and wanted(ap_id) then
      local v = read_cartram_u16(COMPLETED_STAGES + i * 2)
      if v == 1 then reported[ap_id] = true; new[#new + 1] = ap_id end
    end
  end

  -- Heart stars: 5 worlds (stride 7) × 6 levels; byte set → 0x770100 + 6*w + l.
  for w = 0, 4 do
    local start_ind = w * 7
    for l = 0, 5 do
      local ap_id = HEART_BASE + (6 * w) + l
      if not reported[ap_id] and wanted(ap_id) then
        local b = read_cartram_u8(HEART_STARS + start_ind + l)
        if b and b ~= 0 then reported[ap_id] = true; new[#new + 1] = ap_id end
      end
    end
  end

  -- Consumables: only when the seed enabled them; byte==1 at CONSUMABLES+offset.
  if consumables_on then
    for ap_id, off in pairs(LOC_CONSUMABLE) do
      if not reported[ap_id] and wanted(ap_id) then
        local b = read_cartram_u8(CONSUMABLES + off)
        if b == 0x01 then reported[ap_id] = true; new[#new + 1] = ap_id end
      end
    end
  end

  -- Stars: only when the seed enabled them; byte==1 at STARS+offset.
  if stars_on then
    for ap_id, off in pairs(LOC_STAR) do
      if not reported[ap_id] and wanted(ap_id) then
        local b = read_cartram_u8(STARS + off)
        if b == 0x01 then reported[ap_id] = true; new[#new + 1] = ap_id end
      end
    end
  end

  -- Bosses: u16 bitfield; bits 1,3,5,7,9 → 0x770200..0x770204 (in order).
  local boss_flag = read_cartram_u16(BOSS_STATUS)
  if boss_flag then
    local bit = 1
    for idx = 0, 4 do
      local ap_id = BOSS_BASE + idx
      if not reported[ap_id] and wanted(ap_id)
         and bit_and(boss_flag, 2 ^ bit) ~= 0 then
        reported[ap_id] = true; new[#new + 1] = ap_id
      end
      bit = bit + 2
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
  log(("ready: %d consumable + %d star location flags (plus 30 stages, "
       .. "30 heart stars, 5 bosses)")
      :format(
        (function() local n=0 for _ in pairs(LOC_CONSUMABLE) do n=n+1 end return n end)(),
        (function() local n=0 for _ in pairs(LOC_STAR) do n=n+1 end return n end)()))
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_ap() then return new end
  if not in_gameplay() then return new end
  read_flags()
  scan_into(new)
  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_ap() then return false end
  local goal = read_cartram_u8(GOAL_ADDR)
  local save = read_cartram_u8(GAME_SAVE)
  if goal == nil or save == nil then return false end

  -- Per-save status fields are u16-strided (client reads them at base + save*2).
  if goal == 0x00 then
    return read_cartram_u8(BOSS_BUTCH_STATUS + save * 2) == 0x01
  elseif goal == 0x01 then
    return read_cartram_u8(BOSS_BUTCH_STATUS + save * 2) == 0x03
  elseif goal == 0x02 then
    return read_cartram_u8(MG5_STATUS + save * 2) == 0x03
  elseif goal == 0x03 then
    return read_cartram_u8(JUMPING_STATUS + save * 2) == 0x03
  end
  return false
end

-- NO-OP (documented above). items_handling = 0b101: the patched game grants its
-- own local items + a remote start inventory, so checks/goal work end-to-end for
-- a solo seed and report fully in a multiworld. Remote multiworld item DELIVERY
-- (the in-game item-queue splice) is the deferred piece; it is intentionally not
-- wired here until it can be confirmed in-emulator.
function M.receive_item(item_id, meta)
  -- Intentionally does nothing. See the receive_item note in the header.
end

return M
