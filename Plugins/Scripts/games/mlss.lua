-- ═══════════════════════════════════════════════════════════════════════════════
-- mlss.lua — game module for the Archipelago BizHawk connector.
--            Mario & Luigi: Superstar Saga (GBA)
--
-- STATUS: location DETECTION + goal + item delivery are SOURCE-DERIVED from the
-- official AP world worlds/mlss (Client.py + Locations.py + Rom.py + Items.py,
-- main branch — world_version 1.10.2). Every static table below (ROOM_COUNT,
-- BEANSTONES, NONBLOCK, ROOM_EXCEPTION, EREWARD, SHOP/BADGE/PANTS) was GENERATED
-- by parsing Locations.py, not hand-copied, and the check math replicates
-- MLSSClient.game_watcher byte-for-byte. Loads crash-free on any ROM and
-- self-disables on a non-AP / non-MLSS cartridge.
--
-- MEMORY MODEL (BizHawk GBA domains — exactly the domains Client.py reads)
-- ────────────────────────────────────────────────────────────────────────
--   "ROM"   cartridge: identity name @0xA0, the room→block pointer table @0x51FA00.
--   "EWRAM" work RAM: the 59-byte block/digspot flag array @0x4564, the per-flag
--           nonBlock bytes (0x42xx-0x47xx), the shop buy state (0x304A-0x304C),
--           the runtime "MLSSAP" logo guard @0x3060, the goal byte @0x4407, the
--           received-item index @0x4808 and the item-delivery mailbox @0x3057.
--   "IWRAM" internal RAM: current room (u16 LE) @0x2330, shop_init @0x3FE0.
--
-- DETECTION (three subsystems, all mirrored from game_watcher)
--   1. BLOCKS / DIGSPOTS (442 locs): scan the 59-byte flag array; for each set
--      bit derive flag_id = byte*8 + bit + 1, find_key(ROOM_COUNT) → (room,item),
--      read the ROM pointer at 0x51FA00+(room-1)*4, apply the +1/+offset math,
--      remap BEANSTONES, and the resulting ROM address IS the AP location id.
--   2. NON-BLOCK flags (moles, beanstar pieces, key items, coffee rewards):
--      EWRAM[addr] & mask, with ROOM_EXCEPTION room-gating and the EREWARD
--      sequential remap; 0xDA00xx ids are AP data-store events, NOT checks.
--   3. SHOP PURCHASES: the one-frame is_buy event (EWRAM 0x304B) → resolve the
--      bought slot via SHOP/BADGE/PANTS, then clear the flag exactly as the
--      client does (write 0x304A = 0,0).
--   All three are gated to the slot's server location set and to the in-game
--   "MLSSAP" logo so the title screen never reports a check.
--
-- GOAL (game_watcher line ~243): EWRAM[0x4407] & 0x40 (Cackletta's Soul defeated)
--   AND current room == 0x1C7.
--
-- ITEM DELIVERY (items_handling = 0b101: remote items + starting inventory)
-- ──────────────────────────────────────────────────────────────────────────
--   MLSS is NOT a self-granting ROM. The client hands the patched game each
--   received item by writing its RAM id into the EWRAM mailbox 0x3057 and the
--   game's ASM consumes it (zeroes 0x3057), then the client advances the
--   received-item counter at 0x4808. We replicate that exactly: one guarded
--   write per poll (write only while 0x3057 == 0), id_to_RAM remap, big-endian
--   counter bump. Because the server already excludes our own-world items at
--   0b101, the delivery list is the raw AP stream by absolute index — the same
--   list the official client iterates. ALL writes are gated behind the AP-ROM
--   identity AND the live "MLSSAP" logo, so the module can never write into a
--   foreign or unpatched cartridge.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "mlss"

local ADDRESSES_VERIFIED = true   -- tables generated from worlds/mlss source

-- ── Memory domains ────────────────────────────────────────────────────────────
local EWRAM = "EWRAM"
local IWRAM = "IWRAM"
local ROM   = "ROM"

-- ── Addresses / constants (worlds/mlss/Client.py) ─────────────────────────────
local ROOM_ARRAY_POINTER = 0x51FA00     -- ROM: room → block-data pointer table
local ROM_NAME_ADDR      = 0xA0         -- ROM: cartridge internal name (14 bytes)
local ROM_NAME           = "MARIO&LUIGIUA8"  -- validate_rom prefix check

local FLAGS_ARRAY_ADDR   = 0x4564       -- EWRAM: 59-byte block/digspot flag array
local FLAGS_ARRAY_LEN    = 59
local LOGO_ADDR          = 0x3060       -- EWRAM: runtime "MLSSAP" guard (6 bytes)
local LOGO               = "MLSSAP"
local ROOM_ADDR          = 0x2330       -- IWRAM: current room (u16 LE)
local SHOP_INIT_ADDR     = 0x3FE0       -- IWRAM: shop_init byte
local SHOP_SCROLL_ADDR   = 0x304A       -- EWRAM: shop scroll (& 0x1F)
local SHOP_BUY_ADDR      = 0x304B       -- EWRAM: is_buy (!= 0)
local SHOP_ADDRESS_ADDR  = 0x304C       -- EWRAM: shop address (u32 LE & 0xFFFFFF)
local GOAL_ADDR          = 0x4407       -- EWRAM: Cackletta byte (& 0x40)
local GOAL_MASK          = 0x40
local GOAL_ROOM          = 0x1C7        -- room id for the goal scene
local RECV_INDEX_ADDR    = 0x4808       -- EWRAM: received-item index (u16 BE)
local ITEM_MAILBOX_ADDR  = 0x3057       -- EWRAM: item-delivery mailbox (guarded)

local EVENT_BASE         = 0xDA0000     -- nonBlock ids >= this are data-store events

-- The badge-shop addresses where shop_init picks badge-vs-pants (game_watcher
-- line ~124): a bought slot at one of these is a badge if shop_init & 1 else pants.
local BADGE_SHOP_A = 0x3C0618
local BADGE_SHOP_B = 0x3C0684

-- ════════════════════════════ GENERATED TABLES ════════════════════════════════
-- All emitted directly from worlds/mlss/Locations.py (see file header).

-- roomCount: ORDER-SENSITIVE (find_key walks in this exact order). {room, count}
local ROOM_COUNT = {
  {0x15,2},{0x18,4},{0x19,3},{0x1A,3},{0x1B,2},{0x1E,1},{0x23,3},{0x27,1},{0x28,5},{0x29,5},
  {0x2E,4},{0x34,4},{0x37,1},{0x39,5},{0x44,1},{0x45,4},{0x46,3},{0x47,4},{0x48,3},{0x4A,2},
  {0x4B,2},{0x4C,3},{0x4D,2},{0x51,2},{0x53,5},{0x54,5},{0x55,5},{0x56,2},{0x57,1},{0x58,2},
  {0x59,2},{0x5A,3},{0x63,2},{0x68,2},{0x69,2},{0x6B,3},{0x6C,5},{0x6D,1},{0x70,3},{0x74,2},
  {0x75,2},{0x76,1},{0x77,4},{0x78,4},{0x79,4},{0x7A,1},{0x7B,1},{0x7C,5},{0x7D,7},{0x7E,3},
  {0x7F,3},{0x80,4},{0x81,3},{0x82,1},{0x83,4},{0x84,1},{0x86,5},{0x87,1},{0x89,1},{0x8A,3},
  {0x8B,2},{0x8C,2},{0x8D,2},{0x8E,5},{0x90,3},{0x93,5},{0x94,1},{0x96,1},{0x97,4},{0x98,3},
  {0x99,1},{0x9A,1},{0x9B,2},{0x9C,7},{0x9D,1},{0x9E,1},{0x9F,1},{0xA1,4},{0xA2,3},{0xA9,1},
  {0xB0,1},{0xBA,3},{0xBC,2},{0xBE,5},{0xC3,1},{0xC6,1},{0xC7,1},{0xCA,2},{0xCD,6},{0xCE,6},
  {0xCF,1},{0xDB,3},{0xDC,2},{0xDD,1},{0xDF,2},{0xE0,6},{0xE1,1},{0xE2,1},{0xE3,1},{0xE4,5},
  {0xE5,1},{0xE6,2},{0xE7,1},{0xE8,2},{0xE9,4},{0xEC,3},{0xEE,1},{0xF1,3},{0xF2,1},{0xF3,1},
  {0xF4,5},{0xF5,5},{0xF6,5},{0xF7,1},{0xFC,1},{0xFE,1},{0x102,1},{0x103,2},{0x104,1},{0x105,2},
  {0x107,2},{0x109,1},{0x10A,1},{0x10C,1},{0x10D,3},{0x10E,1},{0x10F,2},{0x110,3},{0x111,1},
  {0x112,2},{0x114,1},{0x115,1},{0x116,1},{0x117,1},{0x118,2},{0x11E,3},{0x11F,3},{0x121,4},
  {0x122,6},{0x123,1},{0x126,2},{0x128,1},{0x12A,1},{0x12B,1},{0x12E,4},{0x139,2},{0x13B,1},
  {0x13E,1},{0x147,1},{0x14E,1},{0x14F,1},{0x153,2},{0x154,2},{0x155,3},{0x158,1},{0x159,1},
  {0x15A,2},{0x15B,5},{0x15E,1},{0x161,1},{0x162,1},{0x164,2},{0x165,3},{0x168,1},{0x169,1},
  {0x16B,3},{0x16C,1},{0x171,2},{0x172,2},{0x181,1},{0x186,3},{0x187,1},{0x18D,2},{0x18E,3},
  {0x18F,3},{0x190,1},{0x191,2},{0x192,2},{0x193,2},{0x194,3},{0x195,4},{0x196,3},{0x197,3},
  {0x198,1},{0x19A,2},{0x19B,2},{0x19C,1},{0x19E,2},{0x19F,2},{0x1A3,1},{0x1A6,2},{0x1AA,1},
  {0x1B0,2},{0x1B1,2},{0x1B8,2},{0x1CA,2},{0x1D1,2},{0x1D2,3},{0x1D4,1},{0x1EB,3},{0x1F6,1},
  {0x1F7,1},
}

-- beanstones: resolved ROM pointer -> remapped location id
local BEANSTONES = {
  [0x39DC72]=0x229345,
  [0x39DCB4]=0x22954D,
  [0x39DBD1]=0x228A17,
  [0x39DC10]=0x22913A,
  [0x39DBA4]=0x22890E,
  [0x39DB7F]=0x228775,
  [0x39D73E]=0x251288,
  [0x39D746]=0x2512E1,
  [0x39D74E]=0x25122F,
  [0x39D756]=0x25117D,
  [0x39D75E]=0x2511D6,
  [0x39D76B]=0x25187B,
  [0x39D773]=0x25170B,
  [0x39D77B]=0x251767,
  [0x39D783]=0x2517C3,
  [0x39D78B]=0x25181F,
}

-- nonBlock: {EWRAM addr, mask, location id}. id>=0xDA0000 = AP data-store event (not a check)
local NONBLOCK = {
  {0x434B,0x1,0x243844},
  {0x434B,0x1,0x24387D},
  {0x4373,0x8,0x2779C8},
  {0x42F9,0x4,0x1E9403},
  {0x434B,0x10,0x1E9435},
  {0x434B,0x20,0x1E9436},
  {0x4359,0x20,0x1E9404},
  {0x4359,0x40,0x1E9405},
  {0x42F9,0x2,0x1E9430},
  {0x434B,0x4,0x242888},
  {0x4373,0x20,0x277AB2},
  {0x432D,0x20,0x1E9431},
  {0x434E,0x2,0x1E9411},
  {0x434E,0x4,0x1E9412},
  {0x4375,0x8,0x260637},
  {0x4373,0x10,0x277A45},
  {0x434D,0x8,0x1E9444},
  {0x432E,0x10,0x1E9441},
  {0x434B,0x8,0x1E9434},
  {0x42FE,0x2,0x1E943E},
  {0x42FE,0x4,0x24E628},
  {0x4301,0x10,0x250621},
  {0x42FE,0x80,0x24ED74},
  {0x4302,0x4,0x24FF18},
  {0x42FF,0x8,0x251347},
  {0x42FF,0x20,0x2513FB},
  {0x42FF,0x10,0x2513A1},
  {0x42FF,0x4,0x251988},
  {0x42FF,0x2,0x25192E},
  {0x42FF,0x1,0x2515EB},
  {0x4371,0x40,0x253515},
  {0x4371,0x80,0x253776},
  {0x4372,0x1,0x253C70},
  {0x4372,0x2,0x254324},
  {0x4372,0x4,0x254718},
  {0x4372,0x8,0x254A34},
  {0x4372,0x10,0x254E24},
  {0x472F,0x1,0x252D07},
  {0x472F,0x2,0x252D28},
  {0x472F,0x4,0x252D49},
  {0x472F,0x8,0x252D6A},
  {0x472F,0x10,0x252D8B},
  {0x472F,0x20,0x252DAC},
  {0x472F,0x40,0x252DCD},
  {0x430B,0x10,0x1E9433},
  {0x430B,0x10,0x1E9432},
  {0x430F,0x1,0x1E9440},
  {0x467E,0xFF,0x261658},
  {0x4300,0x40,0x2578E7},
  {0x4375,0x2,0x2753EA},
  {0x4373,0x1,0x277956},
  {0x4346,0x40,0x235A5B},
  {0x4346,0x80,0x235C1C},
  {0x4340,0x20,0x1E9443},
  {0x434A,0x40,0x1E9437},
  {0x434A,0x80,0x236E73},
  {0x4373,0x40,0x277B1F},
  {0x4372,0x80,0x27788E},
  {0x4372,0x80,0x2778D2},
  {0x434C,0x80,0x241000},
  {0x434D,0x1,0x240EBE},
  {0x434C,0x40,0x241155},
  {0x434D,0x2,0x241297},
  {0x434C,0x8,0x241AFA},
  {0x434C,0x10,0x241D7E},
  {0x434C,0x20,0x241C3C},
  {0x4406,0x8,0x1E9442},
  {0x4345,0x8,0x1E9408},
  {0x4345,0x4,0x1E9409},
  {0x42FF,0x80,0x251071},
  {0x42F9,0x2,0xDA0000},
  {0x433D,0x1,0xDA0001},
  {0x43FC,0x80,0xDA0002},
  {0x433D,0x2,0xDA0003},
  {0x4342,0x10,0xDA0004},
  {0x433D,0x8,0xDA0005},
  {0x430F,0x40,0xDA0006},
  {0x433D,0x10,0xDA0007},
}

-- roomException: location only counts when current room is in this set
local ROOM_EXCEPTION = {
  [0x1E9437]={[0xFE]=true,[0xFF]=true,[0x100]=true},
  [0x24ED74]={[0x94]=true,[0x95]=true,[0x96]=true,[0x99]=true},
  [0x250621]={[0x94]=true,[0x95]=true,[0x96]=true,[0x99]=true},
  [0x24FF18]={[0x94]=true,[0x95]=true,[0x96]=true,[0x99]=true},
  [0x260637]={[0x135]=true},
  [0x1E9403]={[0x4D]=true},
  [0xDA0001]={[0x79]=true,[0x192]=true,[0x193]=true},
  [0x2578E7]={[0x79]=true,[0x192]=true,[0x193]=true},
}

-- eReward: coffee-shop espresso reward remap pool (ordered)
local EREWARD = {0x253515,0x253776,0x253C70,0x254324,0x254718,0x254A34,0x254E24}
local EREWARD_SET = {[0x253515]=true,[0x253776]=true,[0x253C70]=true,[0x254324]=true,[0x254718]=true,[0x254A34]=true,[0x254E24]=true}

local SHOP = {
  [0x3C05F0]={0x3C05F0,0x3C05F2,0x3C05F4,0x3C05F8,0x3C05FC,0x3C05FE,0x3C0600,0x3C0602,0x3C0606,0x3C0608,0x3C060C,0x3C060E,0x3C0610,0x3C0614},
  [0x3C066A]={0x3C066A,0x3C066C,0x3C066E,0x3C0670,0x3C0672,0x3C0674,0x3C0676,0x3C0678,0x3C067C,0x3C0680},
}
local BADGE = {
  [0x3C0618]={0x3C0618,0x3C061A,0x3C0624,0x3C0626,0x3C0628,0x3C0632,0x3C0634,0x3C0636,0x3C0640,0x3C0642,0x3C0644,0x3C064E,0x3C0650,0x3C0652,0x3C065C,0x3C065E,0x3C0660},
  [0x3C0684]={0x3C0684,0x3C0686,0x3C0688,0x3C0692,0x3C0694,0x3C069C,0x3C069E},
}
local PANTS = {
  [0x3C0618]={0x3C061C,0x3C061E,0x3C0620,0x3C062A,0x3C062C,0x3C062E,0x3C0638,0x3C063A,0x3C063C,0x3C0646,0x3C0648,0x3C064A,0x3C0654,0x3C0656,0x3C0658,0x3C0662,0x3C0664,0x3C0666},
  [0x3C0684]={0x3C068A,0x3C068C,0x3C068E,0x3C0696,0x3C0698,0x3C06A0,0x3C06A2},
}

-- items_by_id: AP item code -> game itemID (Items.py itemList). Item delivery
-- looks up the received AP id here, then id_to_RAM-remaps the itemID.
local ITEMS_BY_ID = {
  [77771000]=0x4,[77771001]=0xA,[77771002]=0xB,[77771003]=0xC,[77771004]=0xD,
  [77771005]=0xE,[77771006]=0xF,[77771007]=0x10,[77771008]=0x11,[77771009]=0x12,
  [77771010]=0x13,[77771011]=0x14,[77771012]=0x15,[77771013]=0x16,[77771014]=0x17,
  [77771015]=0x18,[77771016]=0x19,[77771017]=0x1A,[77771018]=0x1B,[77771019]=0x1D,
  [77771020]=0x1E,[77771021]=0x20,[77771022]=0x21,[77771023]=0x22,[77771024]=0x23,
  [77771025]=0x24,[77771026]=0x25,[77771027]=0x26,[77771028]=0x31,[77771029]=0x32,
  [77771030]=0x33,[77771031]=0x34,[77771032]=0x35,[77771033]=0x36,[77771034]=0x37,
  [77771035]=0x38,[77771036]=0x39,[77771037]=0x3A,[77771038]=0x40,[77771039]=0x41,
  [77771040]=0x42,[77771041]=0x43,[77771042]=0x45,[77771043]=0x46,[77771044]=0x47,
  [77771045]=0x50,[77771046]=0x51,[77771047]=0x52,[77771048]=0x53,[77771049]=0x54,
  [77771050]=0x55,[77771051]=0x56,[77771052]=0x57,[77771053]=0x60,[77771054]=0x61,
  [77771055]=0x62,[77771056]=0x63,[77771057]=0x64,[77771058]=0x65,[77771059]=0x66,
  [77771060]=0x67,[77771061]=0x70,[77771062]=0x72,[77771063]=0x73,[77771064]=0x74,
  [77771065]=0x75,[77771066]=0x76,[77771067]=0x77,[77771068]=0x80,[77771069]=0x81,
  [77771070]=0x82,[77771071]=0x83,[77771072]=0x84,[77771073]=0x85,[77771074]=0x86,
  [77771075]=0x87,[77771076]=0x90,[77771077]=0x91,[77771078]=0x92,[77771079]=0x93,
  [77771080]=0x9F,[77771081]=0xA0,[77771082]=0xA1,[77771083]=0xA2,[77771084]=0xA3,
  [77771085]=0xA4,[77771086]=0xA5,[77771087]=0xA6,[77771088]=0xA7,[77771089]=0xA8,
  [77771090]=0xA9,[77771091]=0xAA,[77771092]=0xAB,[77771093]=0xAC,[77771094]=0xAD,
  [77771095]=0xAE,[77771096]=0xAF,[77771097]=0xB0,[77771098]=0xB1,[77771099]=0xB2,
  [77771100]=0xB3,[77771101]=0xB4,[77771102]=0xB5,[77771103]=0xB6,[77771104]=0xBD,
  [77771105]=0xC0,[77771106]=0xC1,[77771107]=0xCC,[77771108]=0xCD,[77771109]=0xCE,
  [77771110]=0xCF,[77771111]=0xD0,[77771112]=0xD1,[77771113]=0xD2,[77771114]=0xD3,
  [77771115]=0xD4,[77771116]=0xD5,[77771117]=0xD6,[77771118]=0xD7,[77771119]=0xD8,
  [77771120]=0xD9,[77771121]=0xDA,[77771122]=0xDB,[77771123]=0xDC,[77771124]=0xDD,
  [77771125]=0xDE,[77771126]=0xDF,[77771127]=0xE0,[77771128]=0xE1,[77771129]=0xE2,
  [77771130]=0xE3,[77771131]=0xE4,[77771132]=0xEB,[77771133]=0xF1,[77771134]=0xF3,
  [77771135]=0xF7,[77771136]=0xF8,[77771137]=0xF9,[77771138]=0xFA,[77771139]=0xFB,
  [77771140]=0xFC,[77771141]=0xFD,[77771142]=0xFE,[77771143]=0x1C,[77771144]=0x1F,
  [77771145]=0x3E,
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil     -- cached AP-signature result
local mem              = {}
local log_fn           = nil
local e_used           = 0       -- count of eReward flags consumed (eUsed semantics)
local local_events     = {}      -- 0xDA00xx ids already observed (no re-report)
local goal_reached     = false
local last_poll        = 0
local POLL_INTERVAL    = 0.1     -- client.py watcher_timeout ~0.125 s

-- Item stream: raw_items[absolute AP index] = { id=, player=, flags=, location= }.
-- Delivery indexes it by the game's own received-item counter (EWRAM 0x4808).
local raw_items        = {}
local legacy_next      = 0
local delivered_log    = 0
local bad_item_warned  = false

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[mlss] " .. tostring(msg)) end
end

-- ── Memory API (resolved at init; 2-arg domain form + current-domain fallback) ─
local function resolve_memory_api()
  if not memory then return false end
  mem.read_u8   = memory.read_u8      or memory.readbyte
  mem.read_u16  = memory.read_u16_le  or memory.readword
  mem.read_u32  = memory.read_u32_le  or memory.readdword
  mem.write_u8  = memory.write_u8     or memory.writebyte
  return mem.read_u8 ~= nil
end

local function rd(fn, addr, domain)
  if not fn then return nil end
  local ok, v = pcall(fn, addr, domain)
  if ok and type(v) == "number" then return v end
  ok, v = pcall(fn, addr)                       -- older API: current domain
  if ok and type(v) == "number" then return v end
  return nil
end

local function read_u8(addr, domain)  return rd(mem.read_u8,  addr, domain) end
local function read_u16(addr, domain) return rd(mem.read_u16, addr, domain) end
local function read_u32(addr, domain) return rd(mem.read_u32, addr, domain) end

local function write_u8(addr, value)
  if not mem.write_u8 then return false end
  return (pcall(mem.write_u8, addr, value, EWRAM))
end

-- ── Bit helpers (pure Lua, no bit library dependency) ─────────────────────────
local function bit_and(a, b)
  local res, bitval = 0, 1
  while a > 0 and b > 0 do
    if a % 2 == 1 and b % 2 == 1 then res = res + bitval end
    a = math.floor(a / 2); b = math.floor(b / 2); bitval = bitval * 2
  end
  return res
end

local POW2 = { [0]=1, 2, 4, 8, 16, 32, 64, 128 }

-- ── String read from a memory domain ──────────────────────────────────────────
local function read_string(addr, len, domain, drop_high)
  local out = {}
  for i = 0, len - 1 do
    local b = read_u8(addr + i, domain)
    if b == nil then return nil end
    -- Client.py builds these strings by dropping 0 bytes (logo also drops >=0x70).
    if b ~= 0 and (not drop_high or b < 0x70) then out[#out + 1] = string.char(b) end
  end
  return table.concat(out)
end

-- ── ROM identity: validate_rom checks the 14-byte name "MARIO&LUIGIUA8" ────────
local function rom_is_ap()
  if rom_ok ~= nil then return rom_ok end
  local name = read_string(ROM_NAME_ADDR, #ROM_NAME, ROM, false)
  if name == nil then return false end           -- not readable yet; retry next poll
  -- validate_rom uses startswith; the 14-byte read is exactly the prefix length.
  if name:sub(1, #ROM_NAME) ~= ROM_NAME then
    rom_ok = false
    log("non-MLSS ROM (name '" .. tostring(name) .. "') — detection idle")
    return false
  end
  rom_ok = true
  log("MLSS ROM verified (name '" .. name .. "')")
  return true
end

-- ── In-game guard: the patched game writes "MLSSAP" at EWRAM 0x3060 during real
--    play. game_watcher bails (`if logo != "MLSSAP": return`) otherwise, so the
--    title-screen demo never reports checks and items are never written early.
local function logo_present()
  return read_string(LOGO_ADDR, #LOGO, EWRAM, true) == LOGO
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

-- ── find_key(ROOM_COUNT, target) → room, item (Client.py find_key) ────────────
-- Walks ROOM_COUNT in order subtracting each room's count until leftover<=count.
local function find_key(target)
  local leftover = target
  for i = 1, #ROOM_COUNT do
    local room  = ROOM_COUNT[i][1]
    local count = ROOM_COUNT[i][2]
    if leftover > count then
      leftover = leftover - count
    else
      return room, leftover
    end
  end
  return nil, nil
end

-- ── Block/digspot flag → AP location id (game_watcher block-scan, line ~217) ──
-- Replicates the runtime ROM pointer walk exactly. Returns the AP location id,
-- or nil when the flag does not resolve to a real location.
local function block_flag_to_location(flag_id)
  local room, item = find_key(flag_id)
  if room == nil then return nil end
  local ptr = read_u32(ROOM_ARRAY_POINTER + ((room - 1) * 4), ROM)
  if ptr == nil then return nil end
  ptr = bit_and(ptr, 0xFFFFFF)
  local off_byte = read_u8(ptr, ROM)
  if off_byte == nil then return nil end
  local offset = (off_byte ~= 0) and 2 or 0
  local pointer = ptr + (item * 8) + 1 + offset
  local remap = BEANSTONES[pointer]
  if remap ~= nil then pointer = remap end
  return pointer
end

-- ── Detection: blocks/digspots ────────────────────────────────────────────────
local function scan_blocks(out)
  for byte_i = 0, FLAGS_ARRAY_LEN - 1 do
    local byte = read_u8(FLAGS_ARRAY_ADDR + byte_i, EWRAM)
    if byte and byte ~= 0 then
      for j = 0, 7 do
        if bit_and(byte, POW2[j]) ~= 0 then
          local flag_id = byte_i * 8 + (j + 1)
          local loc = block_flag_to_location(flag_id)
          if loc and not reported[loc] and wanted(loc) then
            reported[loc] = true
            out[#out + 1] = loc
          end
        end
      end
    end
  end
end

-- ── Detection: non-block flags (moles, key items, coffee rewards, …) ──────────
local function scan_nonblock(out, current_room)
  for i = 1, #NONBLOCK do
    local addr, mask, location = NONBLOCK[i][1], NONBLOCK[i][2], NONBLOCK[i][3]
    if not reported[location] then
      local byte = read_u8(addr, EWRAM)
      if byte and bit_and(byte, mask) ~= 0 then
        -- 0xDA00xx are AP data-store events, not checks: note once, never report.
        if location >= EVENT_BASE then
          if not local_events[location] then local_events[location] = true end
        else
          -- roomException: location counts only when current room is in its set.
          local exc = ROOM_EXCEPTION[location]
          local exception = true
          if exc ~= nil then
            exception = (current_room ~= nil and exc[current_room] == true)
          end
          -- eReward: the espresso reward flags map to a sequential reward pool.
          local final = location
          local skip  = false
          if EREWARD_SET[location] then
            e_used = e_used + 1
            final  = EREWARD[e_used]   -- 1-based: eReward[len(eUsed)-1] in 0-based py
            if final == nil then skip = true end
          end
          if (not skip) and exception and final and not reported[final] and wanted(final) then
            reported[final] = true
            out[#out + 1] = final
          end
        end
      end
    end
  end
end

-- ── Detection: shop purchases (one-frame is_buy event, game_watcher line ~122) ─
local function scan_shop(out, shop_init)
  local is_buy = read_u8(SHOP_BUY_ADDR, EWRAM)
  if not is_buy or is_buy == 0 then return end

  local scroll_byte = read_u8(SHOP_SCROLL_ADDR, EWRAM)
  local addr_lo = read_u32(SHOP_ADDRESS_ADDR, EWRAM)
  if scroll_byte == nil or addr_lo == nil then return end

  -- Clear the buy flag exactly as the client does (write 0x304A = 0,0).
  write_u8(SHOP_SCROLL_ADDR, 0)
  write_u8(SHOP_SCROLL_ADDR + 1, 0)

  local shop_scroll  = bit_and(scroll_byte, 0x1F)
  local shop_address = bit_and(addr_lo, 0xFFFFFF)

  local location = nil
  if shop_address ~= BADGE_SHOP_A and shop_address ~= BADGE_SHOP_B then
    local lst = SHOP[shop_address]
    if lst then location = lst[shop_scroll + 1] end
  else
    if shop_init and bit_and(shop_init, 0x1) ~= 0 then
      local lst = BADGE[shop_address]
      if lst then location = lst[shop_scroll + 1] end
    else
      local lst = PANTS[shop_address]
      if lst then location = lst[shop_scroll + 1] end
    end
  end

  if location and not reported[location] and wanted(location) then
    reported[location] = true
    out[#out + 1] = location
  end
end

-- ── id_to_RAM (Client.py id_to_RAM): remap an itemID before the mailbox write ──
local function id_to_RAM(code)
  if code >= 0x1C and code <= 0x1F then code = code + 0xE end
  if code >= 0x20 and code <= 0x26 then code = code - 0x4 end
  return code
end

-- ── Item delivery (game_watcher receive loop, line ~134) ──────────────────────
-- One guarded write per poll: the patched game's ASM reads the mailbox 0x3057
-- and zeroes it when consumed; we only write while it is 0, then advance the
-- received-item counter (u16 BE @0x4808). Gated by the caller behind rom_ok +
-- the MLSSAP logo, so it can never write into a foreign/unpatched ROM.
local function deliver_next()
  local mailbox = read_u8(ITEM_MAILBOX_ADDR, EWRAM)
  if mailbox == nil or mailbox ~= 0 then return end   -- game has not consumed yet

  local hi = read_u8(RECV_INDEX_ADDR, EWRAM)
  local lo = read_u8(RECV_INDEX_ADDR + 1, EWRAM)
  if hi == nil or lo == nil then return end
  local received_index = (hi * 0x100) + lo

  local item = raw_items[received_index]            -- 0-based stream index
  if item == nil then return end                    -- nothing new to deliver

  local game_id = ITEMS_BY_ID[item.id]
  if game_id == nil then
    if not bad_item_warned then
      bad_item_warned = true
      log("received AP item id " .. tostring(item.id) ..
          " is not an MLSS item — delivery of it skipped")
    end
    -- Advance past an unknown id so the stream is not wedged (mirrors the
    -- client, where items_by_id always resolves; a foreign id cannot block us).
    local nxt = received_index + 1
    write_u8(RECV_INDEX_ADDR, math.floor(nxt / 0x100) % 0x100)
    write_u8(RECV_INDEX_ADDR + 1, nxt % 0x100)
    return
  end

  if not write_u8(ITEM_MAILBOX_ADDR, id_to_RAM(game_id)) then return end

  local nxt = received_index + 1
  write_u8(RECV_INDEX_ADDR, math.floor(nxt / 0x100) % 0x100)
  write_u8(RECV_INDEX_ADDR + 1, nxt % 0x100)

  delivered_log = delivered_log + 1
  if delivered_log <= 5 or delivered_log % 25 == 0 then
    log(string.format("delivered item #%d (AP id %d, game id 0x%X) as index %d",
                      delivered_log, item.id, game_id, nxt))
  end
end

-- ── Module contract ───────────────────────────────────────────────────────────
function M.init(ctx)
  if ctx and type(ctx.log) == "function" then log_fn = ctx.log end
  if not ADDRESSES_VERIFIED then
    log("address tables not verified — module idle")
    return
  end
  if not resolve_memory_api() then
    log("BizHawk memory API unavailable — module idle")
    ADDRESSES_VERIFIED = false
    return
  end
  local cfg = (ctx and ctx.config) or {}
  load_locations(cfg.locations)
  log(string.format("ready: %d nonBlock flags, %d roomCount rooms, item delivery active",
                    #NONBLOCK, #ROOM_COUNT))
end

function M.poll()
  local out = {}
  if not ADDRESSES_VERIFIED then return out end
  if not rom_is_ap() then return out end

  -- Self-throttle to the client's watcher cadence (the connector polls every frame).
  -- os.clock is sandboxed-out in some Lua hosts; when absent we simply poll every
  -- frame (correct, just less throttled).
  local now = (type(os) == "table" and type(os.clock) == "function") and os.clock() or nil
  if now ~= nil then
    if now - last_poll < POLL_INTERVAL then return out end
    last_poll = now
  end

  -- In-game guard (game_watcher `if logo != "MLSSAP": return`).
  if not logo_present() then return out end

  local current_room = read_u16(ROOM_ADDR, IWRAM)
  local shop_init    = read_u8(SHOP_INIT_ADDR, IWRAM)

  -- eUsed is recomputed per poll (the client rebuilds eUsed each watcher pass).
  e_used = 0

  scan_shop(out, shop_init)
  scan_nonblock(out, current_room)
  scan_blocks(out)

  -- Goal: Cackletta defeated AND in the goal room (game_watcher line ~243).
  if not goal_reached then
    local goal_byte = read_u8(GOAL_ADDR, EWRAM)
    if goal_byte and bit_and(goal_byte, GOAL_MASK) ~= 0 and current_room == GOAL_ROOM then
      goal_reached = true
      log("goal reached (Cackletta's Soul defeated in room 0x1C7)")
    end
  end

  -- Item delivery shares this poll's verified gate (rom_ok + logo present).
  deliver_next()

  return out
end

function M.is_goal_complete()
  return goal_reached
end

-- Enqueue a received AP item by its ABSOLUTE stream index. Delivery happens in
-- poll() via the game's own received-count handshake (EWRAM 0x4808 / 0x3057).
function M.receive_item(item_id, meta)
  local id = tonumber(item_id)
  if not id then return end

  if meta and meta.index ~= nil then
    local idx = tonumber(meta.index)
    if idx and idx >= 0 then
      raw_items[idx] = {
        id       = id,
        player   = tonumber(meta.player),
        flags    = tonumber(meta.flags),
        location = tonumber(meta.location),
      }
      if idx >= legacy_next then legacy_next = idx + 1 end
      return
    end
  end

  -- Legacy plain "ITEM:<id>" line — append in arrival order.
  raw_items[legacy_next] = { id = id }
  legacy_next = legacy_next + 1
end

return M
