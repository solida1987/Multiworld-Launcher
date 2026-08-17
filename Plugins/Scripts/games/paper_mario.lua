-- ═══════════════════════════════════════════════════════════════════════════════
-- paper_mario.lua — game module for the Archipelago BizHawk connector.
--                   Paper Mario (Nintendo 64) — "Paper Mario"
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the COMMUNITY
-- AP world https://github.com/JKBSunshine/PMR_APWorld (the "Paper Mario 64
-- Randomizer" apworld; client.py game "Paper Mario", system "N64", patch_suffix
-- ".appm64"). The 711-entry location table (176 ModFlag + 535 GameFlag) was
-- GENERATED directly from the apworld source — data/data.py `checks_table`
-- (name -> (flag_type, flag_id)) joined to Locations.py's
-- location_name_to_id (location_id_prefix 8112000000 + index into
-- data/LocationsList.py's OrderedDict) — NOT hand-copied. The flag bit math is
-- replicated EXACTLY from data/data.py get_flag_value() (re-derived to a static
-- (byte_offset, bit) per location and cross-checked against the reference
-- function on 20,000 random flags + a uniqueness sweep before emission). Loads
-- crash-free on any ROM; self-disables on a non-AP / unpatched cartridge.
--
-- WHY A COMMUNITY REPO: Paper Mario is NOT in ArchipelagoMW/Archipelago main; the
-- PMR randomizer's AP world is JKBSunshine/PMR_APWorld (the de-facto world, used
-- with the Generic BizHawk Client). Source URLs are recorded here so the table
-- can be regenerated.
--
-- MEMORY MODEL (BizHawk N64 "RDRAM" + "ROM" domains — matches client.py exactly)
-- ──────────────────────────────────────────────────────────────────────────
--   The PMR AP client is a BizHawkClient that reads two N64 domains:
--     "RDRAM"  console work RAM (game mode + the Mod-Flag and Game-Flag bitmaps
--              that hold every location-checked flag, including the goal flag)
--     "ROM"    the cartridge ("PAPER MARIO         " name + the b'PMDB' patch
--              MAGIC_VALUE the client validates)
--
--   FIXED ADDRESSES — unlike Banjo-Tooie there is NO pointer-chase. The flag
--   bitmaps live at constant physical RDRAM offsets (client.py game_watcher):
--     MODE_ADDRESS       = 0x0A08F1  RDRAM u8  game mode (gate: == 4 = WORLD)
--     MF_START_ADDRESS   = 0x357000  RDRAM     Mod-Flag bitmap   (0x224 bytes)
--     GF_START_ADDRESS   = 0x0DBC70  RDRAM     Game-Flag bitmap  (0x107 bytes)
--   ROM identity (validate_rom):
--     0x20 (ROM) = "PAPER MARIO         " (20 bytes)
--     TABLE_ADDRESS 0x1D00000 (ROM) = MAGIC_VALUE b'PMDB' (4 bytes)
--
--   N64 IS BIG-ENDIAN. The client decodes every multi-byte field with
--   int.from_bytes(..., "big"); we assemble the one multi-byte read we need
--   (none for detection — game mode is a single byte) most-significant byte
--   first. The flag reads are SINGLE BYTES, so byte order does not enter the
--   flag walk — only the get_flag_value byte/bit mapping does (below).
--
--   FLAG BIT MATH (data/data.py get_flag_value, exact):
--     For a flag_id, the reference computes, over the flag-bitmap byte array:
--       flag_offset    = (flag_id // 32) * 4          -- which 4-byte word
--       flag_remainder = flag_id % 32
--       byte_index     = 3 - (flag_remainder // 8)    -- BIG-ENDIAN byte in word
--       value          = 2 ** (flag_remainder % 8)    -- LSB-FIRST bit
--       byte_start     = flag_offset + byte_index
--       checked  <=>  flag_bytes[byte_start] & value
--     We pre-computed (byte_start, bit) for every location and packed it as
--     byte_start*8 + bit into the MF / GF tables below (byte = v//8, bit = v%8).
--     So at poll time a location is checked when
--       (read_u8(<MF|GF base> + (v//8)) >> (v%8)) & 1 == 1     -- LSB-first
--     exactly mirroring get_flag_value's `byte & value == value`.
--
--   The AP LOCATION ID == 8112000000 + the location's index in LocationsList's
--   OrderedDict (Locations.py location_name_to_id). The table keys ARE those ids.
--
--   GOAL (client.py game_watcher): game clear when
--     get_flag_value(GOAL_FLAG=0x1100, mf_bytes) is true — i.e. the Mod-Flag at
--     byte 547 (0x223), bit 0. (GOAL_FLAG is an MF flag; mf_bytes is the same
--     0x224-byte Mod-Flag buffer used by the MF location walk.)
--
--   GAMEPLAY GATE (client.py): the client only sends/receives while
--     game_mode (RDRAM 0x0A08F1, u8) == GAME_MODE_WORLD (4). We mirror that so a
--     title-screen / file-select / battle / booting save can never report
--     phantom checks (the bitmaps are not authoritative outside the world state).
--
-- WHAT THIS DOES (mirrors worlds PMR client.py game_watcher)
--   • poll(): once the ROM signature + WORLD game-mode gate pass, read the two
--     flag buffers and evaluate every known flag exactly as get_flag_value does →
--     AP location ids, gated to the slot's server location set.
--   • is_goal_complete(): get_flag_value(GOAL_FLAG, mf_bytes) — MF byte 547 bit 0,
--     under the same ROM + WORLD gate.
--   • receive_item(): NO-OP (documented). items_handling = 0b101 means the server
--     does NOT send this slot's own locally-found items, so a SOLO seed plays
--     fully and every check is reported. Delivering REMOTE multiworld items is the
--     client's guarded RDRAM path: it writes the next item id (u16, shifted <<16)
--     into a KEY_RECV_BUFFER at 0x358400 ONLY under a guarded_write that checks
--     the buffer is empty AND the received-item sequence counter (ITM_RCV_SEQ,
--     0x356134 u16) is unchanged, with per-item "multiples" remapping via the
--     Unique-Item-Registry. That handshake-guarded write is the one piece that
--     needs in-emulator verification before it is wired here, so it is
--     intentionally left out rather than shipped unverified (a wrong RDRAM write
--     corrupts the live game state). Detection + goal reporting are unaffected.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "paper_mario"

local ADDRESSES_VERIFIED = true   -- tables generated from PMR_APWorld source

-- ── Memory domains (BizHawk N64) ──────────────────────────────────────────────
local RDRAM = "RDRAM"   -- console work RAM (game mode + flag bitmaps)
local ROM   = "ROM"     -- cartridge (name + b'PMDB' signature)

-- ── Addresses / constants (PMR_APWorld data/data.py + client.py) ──────────────
local MODE_ADDRESS       = 0x0A08F1     -- RDRAM: u8 game mode
local GAME_MODE_WORLD    = 4            -- the only state checks are sent in

local MF_START_ADDRESS   = 0x357000     -- RDRAM: Mod-Flag bitmap base  (0x224 bytes)
local GF_START_ADDRESS   = 0x0DBC70     -- RDRAM: Game-Flag bitmap base (0x107 bytes)

local GOAL_MF_BYTE       = 547          -- GOAL_FLAG 0x1100 -> MF byte 547 (0x223) ...
local GOAL_MF_BIT        = 0            -- ... bit 0 (get_flag_value derivation)

-- ROM identity (validate_rom): cartridge name + b'PMDB' magic at TABLE_ADDRESS.
local AP_NAME_ADDR       = 0x20         -- ROM: "PAPER MARIO         " (20 bytes)
local AP_NAME            = "PAPER MARIO         "
local AP_MAGIC_ADDR      = 0x1D00000    -- ROM: TABLE_ADDRESS, holds MAGIC_VALUE
local AP_MAGIC           = "PMDB"       -- MAGIC_VALUE b'PMDB' (4 bytes)

-- ── Location tables (GENERATED from PMR_APWorld checks_table × LocationsList) ──
-- ap_id -> packed (byte_offset*8 + bit) within the named flag bitmap (MF or GF).
-- byte = v//8, bit = v%8, tested LSB-first: (read_u8(base+byte) >> bit) & 1.
-- The keys ARE the AP location ids (8112000000 + LocationsList index).
local MF = {
  [8112000002]=4120,[8112000003]=4121,[8112000004]=4122,[8112000005]=4123,[8112000006]=4124,[8112000008]=4125,
  [8112000009]=4126,[8112000021]=4127,[8112000046]=4112,[8112000047]=4113,[8112000048]=4114,[8112000049]=4115,
  [8112000050]=4116,[8112000051]=4117,[8112000052]=4118,[8112000053]=4119,[8112000054]=4104,[8112000055]=4105,
  [8112000056]=4106,[8112000057]=4107,[8112000058]=4108,[8112000059]=4109,[8112000062]=4110,[8112000064]=4111,
  [8112000065]=4096,[8112000085]=4097,[8112000086]=4098,[8112000087]=4278,[8112000089]=4099,[8112000090]=4100,
  [8112000096]=4101,[8112000097]=4102,[8112000098]=4103,[8112000099]=4152,[8112000100]=4153,[8112000101]=4154,
  [8112000104]=4155,[8112000105]=4156,[8112000120]=4148,[8112000121]=4149,[8112000122]=4150,[8112000123]=4151,
  [8112000124]=4136,[8112000125]=4137,[8112000126]=4138,[8112000127]=4139,[8112000128]=4140,[8112000129]=4141,
  [8112000130]=4142,[8112000156]=4157,[8112000157]=4158,[8112000158]=4159,[8112000159]=4144,[8112000160]=4145,
  [8112000161]=4146,[8112000162]=153,[8112000164]=4147,[8112000180]=4272,[8112000181]=4273,[8112000182]=4274,
  [8112000183]=4275,[8112000184]=4276,[8112000185]=4277,[8112000189]=4177,[8112000193]=4143,[8112000194]=4128,
  [8112000195]=4129,[8112000196]=4130,[8112000197]=4131,[8112000198]=4132,[8112000199]=4133,[8112000200]=4134,
  [8112000201]=4135,[8112000205]=4184,[8112000206]=4185,[8112000208]=4186,[8112000209]=4178,[8112000210]=4187,
  [8112000231]=4287,[8112000233]=4188,[8112000260]=4189,[8112000267]=4190,[8112000291]=4191,[8112000292]=4176,
  [8112000296]=4179,[8112000298]=4180,[8112000299]=4181,[8112000300]=4182,[8112000301]=4183,[8112000302]=4168,
  [8112000303]=4169,[8112000306]=4170,[8112000307]=4171,[8112000311]=4172,[8112000333]=4173,[8112000351]=4174,
  [8112000352]=4175,[8112000382]=4160,[8112000385]=4161,[8112000389]=4162,[8112000390]=4163,[8112000391]=4164,
  [8112000392]=4165,[8112000393]=4166,[8112000394]=4167,[8112000395]=4216,[8112000399]=4217,[8112000400]=4218,
  [8112000406]=4219,[8112000434]=4220,[8112000435]=4221,[8112000436]=4222,[8112000437]=4223,[8112000438]=4208,
  [8112000502]=4209,[8112000513]=4210,[8112000519]=4211,[8112000520]=4212,[8112000521]=4213,[8112000522]=4214,
  [8112000523]=4215,[8112000525]=4200,[8112000526]=4201,[8112000527]=4202,[8112000528]=4203,[8112000531]=4194,
  [8112000532]=4195,[8112000533]=4196,[8112000534]=4204,[8112000535]=4205,[8112000536]=4206,[8112000537]=4207,
  [8112000538]=4192,[8112000539]=4193,[8112000543]=4197,[8112000575]=4198,[8112000592]=4199,[8112000593]=4248,
  [8112000594]=4249,[8112000597]=4250,[8112000599]=4251,[8112000600]=4252,[8112000601]=4253,[8112000604]=4254,
  [8112000605]=4255,[8112000608]=4240,[8112000611]=4241,[8112000613]=4242,[8112000615]=4243,[8112000625]=4244,
  [8112000626]=4245,[8112000628]=4246,[8112000629]=4247,[8112000630]=4232,[8112000632]=4233,[8112000633]=4234,
  [8112000636]=4235,[8112000642]=4236,[8112000643]=4237,[8112000644]=4238,[8112000645]=4239,[8112000646]=4224,
  [8112000647]=4225,[8112000653]=4226,[8112000654]=4227,[8112000656]=4228,[8112000658]=4229,[8112000659]=4230,
  [8112000660]=4231,[8112000696]=4280,[8112000697]=4281,[8112000698]=4282,[8112000699]=4283,[8112000700]=4284,
  [8112000701]=4285,[8112000709]=4286,
}
local GF = {
  [8112000000]=78,[8112000001]=124,[8112000007]=54,[8112000010]=55,[8112000011]=44,[8112000012]=42,
  [8112000013]=32,[8112000014]=33,[8112000015]=34,[8112000016]=35,[8112000017]=41,[8112000018]=45,
  [8112000019]=64,[8112000020]=6,[8112000022]=38,[8112000023]=39,[8112000024]=88,[8112000025]=89,
  [8112000026]=90,[8112000027]=91,[8112000028]=36,[8112000029]=37,[8112000030]=94,[8112000031]=82,
  [8112000032]=81,[8112000033]=72,[8112000034]=87,[8112000035]=85,[8112000036]=86,[8112000037]=76,
  [8112000038]=77,[8112000039]=73,[8112000040]=74,[8112000041]=75,[8112000042]=66,[8112000043]=123,
  [8112000044]=309,[8112000045]=236,[8112000060]=319,[8112000061]=231,[8112000063]=274,[8112000066]=310,
  [8112000067]=1688,[8112000068]=1689,[8112000069]=1690,[8112000070]=1691,[8112000071]=1692,[8112000072]=1693,
  [8112000073]=1694,[8112000074]=1695,[8112000075]=1680,[8112000076]=1681,[8112000077]=1682,[8112000078]=1683,
  [8112000079]=1684,[8112000080]=1685,[8112000081]=1686,[8112000082]=1687,[8112000083]=156,[8112000084]=259,
  [8112000088]=305,[8112000091]=306,[8112000092]=311,[8112000093]=296,[8112000094]=313,[8112000095]=297,
  [8112000102]=317,[8112000103]=318,[8112000106]=298,[8112000107]=308,[8112000108]=407,[8112000109]=392,
  [8112000110]=393,[8112000111]=394,[8112000112]=395,[8112000113]=396,[8112000114]=425,[8112000115]=397,
  [8112000116]=398,[8112000117]=399,[8112000118]=426,[8112000119]=427,[8112000131]=428,[8112000132]=384,
  [8112000133]=385,[8112000134]=429,[8112000135]=386,[8112000136]=387,[8112000137]=388,[8112000138]=389,
  [8112000139]=390,[8112000140]=391,[8112000141]=440,[8112000142]=441,[8112000143]=442,[8112000144]=443,
  [8112000145]=444,[8112000146]=445,[8112000147]=446,[8112000148]=447,[8112000149]=480,[8112000150]=508,
  [8112000151]=510,[8112000152]=511,[8112000153]=514,[8112000154]=568,[8112000155]=515,[8112000163]=513,
  [8112000165]=1779,[8112000166]=1780,[8112000167]=1781,[8112000168]=1782,[8112000169]=1783,[8112000170]=1768,
  [8112000171]=1769,[8112000172]=1770,[8112000173]=1771,[8112000174]=1772,[8112000175]=1773,[8112000176]=1774,
  [8112000177]=1775,[8112000178]=1760,[8112000179]=1761,[8112000186]=516,[8112000187]=596,[8112000188]=597,
  [8112000190]=599,[8112000191]=635,[8112000192]=636,[8112000202]=582,[8112000203]=603,[8112000204]=637,
  [8112000207]=778,[8112000211]=1717,[8112000212]=1704,[8112000213]=1707,[8112000214]=1710,[8112000215]=1697,
  [8112000216]=1700,[8112000217]=1703,[8112000218]=1754,[8112000219]=1757,[8112000220]=1744,[8112000221]=1747,
  [8112000222]=1750,[8112000223]=1737,[8112000224]=1740,[8112000225]=1743,[8112000226]=1730,[8112000227]=1733,
  [8112000228]=1784,[8112000229]=1787,[8112000230]=1790,[8112000232]=602,[8112000234]=586,[8112000235]=587,
  [8112000236]=588,[8112000237]=589,[8112000238]=604,[8112000239]=639,[8112000240]=576,[8112000241]=590,
  [8112000242]=624,[8112000243]=632,[8112000244]=606,[8112000245]=607,[8112000246]=592,[8112000247]=593,
  [8112000248]=594,[8112000249]=605,[8112000250]=591,[8112000251]=633,[8112000252]=585,[8112000253]=665,
  [8112000254]=666,[8112000255]=614,[8112000256]=669,[8112000257]=670,[8112000258]=671,[8112000259]=615,
  [8112000261]=656,[8112000262]=664,[8112000263]=683,[8112000264]=684,[8112000265]=685,[8112000266]=723,
  [8112000268]=694,[8112000269]=729,[8112000270]=725,[8112000271]=695,[8112000272]=724,[8112000273]=681,
  [8112000274]=678,[8112000275]=679,[8112000276]=728,[8112000277]=730,[8112000278]=680,[8112000279]=672,
  [8112000280]=673,[8112000281]=674,[8112000282]=675,[8112000283]=676,[8112000284]=677,[8112000285]=686,
  [8112000286]=687,[8112000287]=731,[8112000288]=733,[8112000289]=734,[8112000290]=735,[8112000293]=713,
  [8112000294]=746,[8112000295]=750,[8112000297]=736,[8112000304]=749,[8112000305]=751,[8112000308]=748,
  [8112000309]=773,[8112000310]=774,[8112000312]=807,[8112000313]=861,[8112000314]=808,[8112000315]=775,
  [8112000316]=824,[8112000317]=825,[8112000318]=809,[8112000319]=826,[8112000320]=827,[8112000321]=828,
  [8112000322]=829,[8112000323]=830,[8112000324]=831,[8112000325]=816,[8112000326]=817,[8112000327]=818,
  [8112000328]=810,[8112000329]=811,[8112000330]=862,[8112000331]=856,[8112000332]=772,[8112000334]=863,
  [8112000335]=848,[8112000336]=857,[8112000337]=849,[8112000338]=850,[8112000339]=851,[8112000340]=812,
  [8112000341]=819,[8112000342]=859,[8112000343]=858,[8112000344]=820,[8112000345]=821,[8112000346]=852,
  [8112000347]=813,[8112000348]=860,[8112000349]=853,[8112000350]=814,[8112000353]=854,[8112000354]=855,
  [8112000355]=805,[8112000356]=822,[8112000357]=823,[8112000358]=840,[8112000359]=815,[8112000360]=800,
  [8112000361]=801,[8112000362]=802,[8112000363]=803,[8112000364]=804,[8112000365]=1676,[8112000366]=895,
  [8112000367]=874,[8112000368]=877,[8112000369]=882,[8112000370]=883,[8112000371]=876,[8112000372]=925,
  [8112000373]=924,[8112000374]=927,[8112000375]=879,[8112000376]=873,[8112000377]=875,[8112000378]=954,
  [8112000379]=901,[8112000380]=902,[8112000381]=959,[8112000383]=957,[8112000384]=935,[8112000386]=985,
  [8112000387]=988,[8112000388]=989,[8112000396]=978,[8112000397]=991,[8112000398]=980,[8112000401]=983,
  [8112000402]=968,[8112000403]=970,[8112000404]=971,[8112000405]=972,[8112000407]=1013,[8112000408]=1014,
  [8112000409]=1010,[8112000410]=1011,[8112000411]=1012,[8112000412]=1007,[8112000413]=1015,[8112000414]=1000,
  [8112000415]=1001,[8112000416]=995,[8112000417]=1002,[8112000418]=1034,[8112000419]=1038,[8112000420]=1024,
  [8112000421]=1026,[8112000422]=1031,[8112000423]=1081,[8112000424]=1082,[8112000425]=1083,[8112000426]=1084,
  [8112000427]=1085,[8112000428]=1086,[8112000429]=1087,[8112000430]=1072,[8112000431]=1073,[8112000432]=1077,
  [8112000433]=1100,[8112000439]=1102,[8112000440]=1103,[8112000441]=1090,[8112000442]=1091,[8112000443]=1092,
  [8112000444]=1093,[8112000445]=1214,[8112000446]=1110,[8112000447]=1095,[8112000448]=1144,[8112000449]=1145,
  [8112000450]=1146,[8112000451]=1147,[8112000452]=1148,[8112000453]=1149,[8112000454]=1150,[8112000455]=1151,
  [8112000456]=1136,[8112000457]=1137,[8112000458]=1138,[8112000459]=1140,[8112000460]=1141,[8112000461]=1098,
  [8112000462]=1142,[8112000463]=1143,[8112000464]=1128,[8112000465]=1129,[8112000466]=1130,[8112000467]=1131,
  [8112000468]=1215,[8112000469]=1111,[8112000470]=1134,[8112000471]=1135,[8112000472]=1120,[8112000473]=1133,
  [8112000474]=1121,[8112000475]=1200,[8112000476]=1096,[8112000477]=1123,[8112000478]=1126,[8112000479]=1127,
  [8112000480]=1176,[8112000481]=1177,[8112000482]=1178,[8112000483]=1179,[8112000484]=1166,[8112000485]=1168,
  [8112000486]=1169,[8112000487]=1173,[8112000488]=1174,[8112000489]=1162,[8112000490]=1163,[8112000491]=1125,
  [8112000492]=1167,[8112000493]=1152,[8112000494]=1201,[8112000495]=1157,[8112000496]=1158,[8112000497]=1156,
  [8112000498]=1154,[8112000499]=1155,[8112000500]=1202,[8112000501]=1153,[8112000503]=1159,[8112000504]=1208,
  [8112000505]=1209,[8112000506]=1210,[8112000507]=1211,[8112000508]=1212,[8112000509]=1213,[8112000510]=1240,
  [8112000511]=1241,[8112000512]=1243,[8112000514]=1246,[8112000515]=1222,[8112000516]=1223,[8112000517]=1245,
  [8112000518]=1253,[8112000524]=1276,[8112000529]=1261,[8112000530]=1251,[8112000540]=1236,[8112000541]=1237,
  [8112000542]=1235,[8112000544]=1230,[8112000545]=1256,[8112000546]=1231,[8112000547]=1277,[8112000548]=1216,
  [8112000549]=1278,[8112000550]=1279,[8112000551]=1255,[8112000552]=1304,[8112000553]=1305,[8112000554]=1273,
  [8112000555]=1257,[8112000556]=1258,[8112000557]=1264,[8112000558]=1265,[8112000559]=1254,[8112000560]=1259,
  [8112000561]=1260,[8112000562]=1266,[8112000563]=1267,[8112000564]=1306,[8112000565]=1218,[8112000566]=1274,
  [8112000567]=1268,[8112000568]=1275,[8112000569]=1269,[8112000570]=1219,[8112000571]=1220,[8112000572]=1270,
  [8112000573]=1262,[8112000574]=1221,[8112000576]=1217,[8112000577]=1322,[8112000578]=1323,[8112000579]=1324,
  [8112000580]=1325,[8112000581]=1326,[8112000582]=1327,[8112000583]=1320,[8112000584]=1336,[8112000585]=1339,
  [8112000586]=1332,[8112000587]=1321,[8112000588]=1314,[8112000589]=1315,[8112000590]=1312,[8112000591]=1313,
  [8112000595]=1380,[8112000596]=1350,[8112000598]=1405,[8112000602]=1378,[8112000603]=1406,[8112000606]=1407,
  [8112000607]=1392,[8112000609]=1393,[8112000610]=1394,[8112000612]=1395,[8112000614]=1396,[8112000616]=1379,
  [8112000617]=1398,[8112000618]=1397,[8112000619]=1399,[8112000620]=1384,[8112000621]=1433,[8112000622]=1432,
  [8112000623]=1385,[8112000624]=1386,[8112000627]=1382,[8112000631]=1383,[8112000634]=1411,[8112000635]=1412,
  [8112000637]=1464,[8112000638]=1465,[8112000639]=1466,[8112000640]=1467,[8112000641]=1468,[8112000648]=1470,
  [8112000649]=1471,[8112000650]=1469,[8112000651]=1457,[8112000652]=1458,[8112000655]=1448,[8112000657]=1449,
  [8112000661]=1455,[8112000662]=1440,[8112000663]=1442,[8112000664]=1409,[8112000665]=1474,[8112000666]=1485,
  [8112000667]=1475,[8112000668]=1484,[8112000669]=1477,[8112000670]=1528,[8112000671]=1529,[8112000672]=1530,
  [8112000673]=1533,[8112000674]=1534,[8112000675]=1535,[8112000676]=1520,[8112000677]=1521,[8112000678]=1522,
  [8112000679]=1525,[8112000680]=1553,[8112000681]=1554,[8112000682]=1555,[8112000683]=1557,[8112000684]=1545,
  [8112000685]=1546,[8112000686]=1547,[8112000687]=1551,[8112000688]=1536,[8112000689]=1543,[8112000690]=1592,
  [8112000691]=1593,[8112000692]=1594,[8112000693]=1541,[8112000694]=1599,[8112000695]=1586,[8112000702]=1587,
  [8112000703]=1588,[8112000704]=1589,[8112000705]=1590,[8112000706]=1576,[8112000707]=1578,[8112000708]=1583,
  [8112000710]=1651,
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil     -- cached AP-signature result
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[paper_mario] " .. tostring(msg)) end
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

-- ── ROM identity: the patched cartridge carries the name "PAPER MARIO         "
-- at ROM 0x20 and the b'PMDB' MAGIC_VALUE at ROM 0x1D00000 (TABLE_ADDRESS), both
-- checked by the client's validate_rom. Verifying BOTH means we only ever act on
-- a patched PMR cartridge — exactly the client's gate. ──────────────────────────
local function rom_is_ap()
  if rom_ok ~= nil then return rom_ok end
  for i = 1, #AP_NAME do
    local b = read_u8(AP_NAME_ADDR + i - 1, ROM)
    if b == nil then return false end          -- not readable yet; retry next poll
    if b ~= string.byte(AP_NAME, i) then
      rom_ok = false
      log("non-Paper-Mario ROM (no 'PAPER MARIO' cartridge name) — detection idle")
      return false
    end
  end
  for i = 1, #AP_MAGIC do
    local b = read_u8(AP_MAGIC_ADDR + i - 1, ROM)
    if b == nil then return false end
    if b ~= string.byte(AP_MAGIC, i) then
      rom_ok = false
      log("unpatched/incompatible Paper Mario ROM (no 'PMDB' magic) — detection idle")
      return false
    end
  end
  rom_ok = true
  log("AP ROM verified ('PAPER MARIO' name + 'PMDB' magic present)")
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
-- The client only sends/receives while game_mode == GAME_MODE_WORLD (4).
local function in_world()
  local m = read_u8(MODE_ADDRESS, RDRAM)
  return m ~= nil and m == GAME_MODE_WORLD
end

-- ── Flag bit test (get_flag_value, LSB-first) ─────────────────────────────────
-- byte at (base + packed//8), test bit (packed%8), LSB-first: (byte>>bit)&1.
-- Mirrors get_flag_value's `byte & value == value` (value = 2^bit). Arithmetic
-- only (no bit ops needed in 5.1 Lua).
local function bit_set(base, packed)
  local byte = read_u8(base + math.floor(packed / 8), RDRAM)
  if byte == nil then return false end
  local bit = packed % 8
  return (math.floor(byte / (2 ^ bit)) % 2) >= 1
end

-- ── Flag walk (read both bitmaps once per poll) ───────────────────────────────
-- Mirrors client.py game_watcher's checks_table loop: for each known location,
-- read its flag from the MF or GF bitmap via get_flag_value math. Only called
-- once ROM signature + WORLD game-mode gates pass, so any set bit is a real check.
local function scan_into(new)
  for ap_id, packed in pairs(MF) do
    if not reported[ap_id] and wanted(ap_id) and bit_set(MF_START_ADDRESS, packed) then
      reported[ap_id] = true
      new[#new + 1] = ap_id
    end
  end
  for ap_id, packed in pairs(GF) do
    if not reported[ap_id] and wanted(ap_id) and bit_set(GF_START_ADDRESS, packed) then
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
  local n = 0
  for _ in pairs(MF) do n = n + 1 end
  for _ in pairs(GF) do n = n + 1 end
  log("ready: " .. n .. " location flags (N64, ModFlag+GameFlag bitmaps)")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_ap() then return new end          -- unpatched/wrong cart → idle
  if not in_world() then return new end           -- title/file-select/battle → idle
  scan_into(new)
  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_ap() then return false end
  if not in_world() then return false end
  -- get_flag_value(GOAL_FLAG=0x1100, mf_bytes): MF byte 547, bit 0.
  return bit_set(MF_START_ADDRESS, GOAL_MF_BYTE * 8 + GOAL_MF_BIT)
end

-- Remote multiworld items: see the file header. items_handling = 0b101 means the
-- server does NOT send this slot's own found items, so solo play and check
-- reporting work fully; applying REMOTE items is the client's guarded RDRAM path
-- (write the next item id u16<<16 into KEY_RECV_BUFFER 0x358400 under a
-- guarded_write keyed on the buffer being empty AND the ITM_RCV_SEQ 0x356134
-- counter unchanged, with Unique-Item-Registry "multiples" remapping) and is the
-- one piece deferred until it can be confirmed in-emulator. No-op (never a wrong
-- write) until then.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
