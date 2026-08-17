-- ═══════════════════════════════════════════════════════════════════════════════
-- kirby_64_-_the_crystal_shards.lua — game module for the Archipelago BizHawk connector.
--                Kirby 64: The Crystal Shards (Nintendo 64) — "Kirby 64 - The Crystal Shards"
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the AP world
-- worlds/k64 (client.py + locations.py + regions.py + consumable_info.py + rom.py)
-- on the k64cs branch of the maintainer's Archipelago fork:
--   https://github.com/Silvris/Archipelago/tree/k64cs/worlds/k64
--   (archipelago.json game "Kirby 64 - The Crystal Shards", world_version 0.3.2,
--    minimum_ap_version 0.6.4, author Silvris).
-- Every location table below was GENERATED directly from that source (the location
-- dicts in locations.py, default_levels in regions.py, the `consumables` dict in
-- consumable_info.py) with a Python parser — NOT hand-copied — and the save-flag /
-- crystal-bit / consumable-bit math + the signature gate + the goal are replicated
-- EXACTLY from client.py game_watcher (mind the N64 big-endian byte order, the
-- LITTLE-endian crystal_array words, and the big-endian 64-bit consumable masks).
-- Loads crash-free on any ROM; self-disables on a non-AP / non-K64 cartridge.
--
-- WHY THIS FORK: Kirby 64 is not in ArchipelagoMW/Archipelago main; Silvris (an AP
-- core dev) maintains it on the k64cs branch, shipped as the .apk64cs apworld. The
-- reference client (worlds/k64/client.py) is a real worlds._bizhawk.BizHawkClient —
-- this module mirrors it on the launcher's BizHawk pipe bridge. Source URL recorded
-- above so the tables can be regenerated.
--
-- MEMORY MODEL (BizHawk N64 domains — matches client.py K64Client)
-- ──────────────────────────────────────────────────────────────────────────
--   The K64 AP client reads two BizHawk N64 domains:
--     "RDRAM"  console work RAM — the save struct (every location flag + the goal
--              byte + the -HALKEN--KIRBY4- signature) and the consumable bitfield
--     "ROM"    the cartridge — the "Kirby64" + "K64" AP identity strings AND the
--              per-player stage layout table the basepatch writes at 0x1FFF230
--              (read so stage-shuffle remaps the right location id per slot)
--
--   N64 IS BIG-ENDIAN. Multi-byte values the client reads with int.from_bytes(...,
--   "big") are assembled MSB-first here (the byte at the LOWEST address is the high
--   byte). Single-byte flag reads need no byte order. ONE deliberate exception that
--   mirrors the client exactly: crystal_array is decoded with struct.unpack("I", …)
--   (NATIVE = little-endian on the client host), so its per-level u32 is read
--   LITTLE-endian; because we test individual physical bytes (bit n -> byte n//8,
--   bit n%8) the result is identical and byte-order-robust.
--
--   SIGNATURE GATE (client.py): the save base RDRAM[0xD6B00..+16) must read
--   "-HALKEN--KIRBY4-", AND the ROM must carry "Kirby64" @ 0x20 and "K64" @ 0x1FFF200.
--   GAMEPLAY GATE (client.py): game_state (RDRAM 0xBE4F0, u32 big) in 0..10 are menu
--   / demo states — the client bails there, so a booting/zeroed save can never report
--   phantom checks. Gameplay requires game_state >= 11.
--
--   LOCATIONS (client.py game_watcher, exact) — four independent groups:
--     • BOSSES (loc 0x0200..0x0205): boss_crystals[i] (RDRAM save+0xC0, 8 bytes)
--       byte i != 0 -> loc i+0x0200, for i in 0..5.
--     • STAGE COMPLETES: per physical slot, stage_array (RDRAM save+0xE0, 42 bytes)
--       byte == 0x02 means cleared. The loc id at each slot is read at RUNTIME from
--       the ROM stage table (0x1FFF230, 28 u16 big) -> honors stage shuffle.
--     • CRYSTAL SHARDS (3 per stage, loc 0x0101..0x0142): crystal_array
--       (RDRAM save+0xC8, 6 LE u32) bit (slot*8 + i) -> a crystal loc derived from
--       default_levels (static). 0x0101 + ((stage_code&0xFF)-1)*3 + i.
--     • CONSUMABLES (food / 1-Up / star, loc 0x0400..0x0761): consumable_checks
--       (RDRAM 0x500000, 0xC80 bytes); the client reads an 8-byte big-endian window
--       at a per-loc index and masks one bit. Each entry below stores the resolved
--       absolute byte + bit (verified bit-exact against the client for all 865).
--
--   GOAL (client.py): boss_crystals[6] != 0 (Zero-Two / Dark Star defeated).
--
-- WHAT THIS DOES (mirrors worlds/k64/client.py game_watcher)
--   • poll(): once the signature + gameplay gates pass, evaluate every group above
--     -> AP location ids, gated to the slot's server location set.
--   • is_goal_complete(): boss_crystals[6] != 0.
--   • receive_item(): NO-OP (documented). items_handling = 0b111 (FULL remote) — the
--     reference client DELIVERS every item (including the slot's own) by writing into
--     the live RDRAM save (recv-index handshake at save+0x174, then per-item writes:
--     copy-ability bitfields, friends, crystal count, lives, health, stars, plus the
--     DeathLink kill path). That guarded RDRAM write path is the one piece deferred
--     until it can be confirmed in-emulator, rather than shipped unverified — a wrong
--     RDRAM write corrupts the live save. Detection + goal are unaffected.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "kirby_64_-_the_crystal_shards"

local ADDRESSES_VERIFIED = true   -- tables generated from worlds/k64 source

-- ── Memory domains (BizHawk N64) ──────────────────────────────────────────────
local RDRAM = "RDRAM"   -- console work RAM (save struct + consumable bitfield)
local ROM   = "ROM"     -- cartridge (AP identity strings + patched stage table)

-- ── Addresses / constants (worlds/k64/client.py + rom.py) ─────────────────────
local K64_GAME_STATE     = 0xBE4F0      -- RDRAM: u32 big main game state
local K64_SAVE_ADDRESS   = 0xD6B00      -- RDRAM: save struct base
local K64_BOSS_CRYSTALS  = K64_SAVE_ADDRESS + 0xC0   -- 8 bytes; [6] != 0 == goal
local K64_CRYSTAL_ARRAY  = K64_SAVE_ADDRESS + 0xC8   -- 6 LE u32 (crystal bitfields)
local K64_STAGE_STATUSES = K64_SAVE_ADDRESS + 0xE0   -- 42 bytes (stage-clear flags)
local K64_CONSUMABLES    = 0x500000     -- RDRAM: 0xC80-byte consumable bitfield
local K64_LEVEL_ADDRESS  = 0x1FFF230    -- ROM: 28 u16 big — patched stage layout

local SAVE_SIG           = "-HALKEN--KIRBY4-"   -- RDRAM[save..+16) signature
local ROM_NAME_ADDR      = 0x20         -- ROM: "Kirby64" (7 bytes)
local ROM_NAME           = "Kirby64"
local ROM_K64_ADDR       = 0x1FFF200    -- ROM: "K64" (first 3 bytes of patch name)
local ROM_K64            = "K64"

local MENU_STATE_MAX     = 10           -- game_state in 0..10 == menu/demo (bail)

-- ── Location tables (GENERATED from worlds/k64 source) ─────────────────────────
local ALL_LOCS = {
  [0x1]=true,[0x2]=true,[0x3]=true,[0x4]=true,[0x5]=true,[0x6]=true,[0x7]=true,[0x8]=true,[0x9]=true,[0xA]=true,[0xB]=true,[0xC]=true,
  [0xD]=true,[0xE]=true,[0xF]=true,[0x10]=true,[0x11]=true,[0x12]=true,[0x13]=true,[0x14]=true,[0x15]=true,[0x16]=true,[0x101]=true,[0x102]=true,
  [0x103]=true,[0x104]=true,[0x105]=true,[0x106]=true,[0x107]=true,[0x108]=true,[0x109]=true,[0x10A]=true,[0x10B]=true,[0x10C]=true,[0x10D]=true,[0x10E]=true,
  [0x10F]=true,[0x110]=true,[0x111]=true,[0x112]=true,[0x113]=true,[0x114]=true,[0x115]=true,[0x116]=true,[0x117]=true,[0x118]=true,[0x119]=true,[0x11A]=true,
  [0x11B]=true,[0x11C]=true,[0x11D]=true,[0x11E]=true,[0x11F]=true,[0x120]=true,[0x121]=true,[0x122]=true,[0x123]=true,[0x124]=true,[0x125]=true,[0x126]=true,
  [0x127]=true,[0x128]=true,[0x129]=true,[0x12A]=true,[0x12B]=true,[0x12C]=true,[0x12D]=true,[0x12E]=true,[0x12F]=true,[0x130]=true,[0x131]=true,[0x132]=true,
  [0x133]=true,[0x134]=true,[0x135]=true,[0x136]=true,[0x137]=true,[0x138]=true,[0x139]=true,[0x13A]=true,[0x13B]=true,[0x13C]=true,[0x13D]=true,[0x13E]=true,
  [0x13F]=true,[0x140]=true,[0x141]=true,[0x142]=true,[0x200]=true,[0x201]=true,[0x202]=true,[0x203]=true,[0x204]=true,[0x205]=true,[0x400]=true,[0x401]=true,
  [0x402]=true,[0x403]=true,[0x404]=true,[0x405]=true,[0x406]=true,[0x407]=true,[0x408]=true,[0x409]=true,[0x40A]=true,[0x40B]=true,[0x40C]=true,[0x40D]=true,
  [0x40E]=true,[0x40F]=true,[0x410]=true,[0x411]=true,[0x412]=true,[0x413]=true,[0x414]=true,[0x415]=true,[0x416]=true,[0x417]=true,[0x418]=true,[0x419]=true,
  [0x41A]=true,[0x41B]=true,[0x41C]=true,[0x41D]=true,[0x41E]=true,[0x41F]=true,[0x420]=true,[0x421]=true,[0x422]=true,[0x423]=true,[0x424]=true,[0x425]=true,
  [0x426]=true,[0x427]=true,[0x428]=true,[0x429]=true,[0x42A]=true,[0x42B]=true,[0x42C]=true,[0x42D]=true,[0x42E]=true,[0x42F]=true,[0x430]=true,[0x431]=true,
  [0x432]=true,[0x433]=true,[0x434]=true,[0x435]=true,[0x436]=true,[0x437]=true,[0x438]=true,[0x439]=true,[0x43A]=true,[0x43B]=true,[0x43C]=true,[0x43D]=true,
  [0x43E]=true,[0x43F]=true,[0x440]=true,[0x441]=true,[0x442]=true,[0x443]=true,[0x444]=true,[0x445]=true,[0x446]=true,[0x447]=true,[0x448]=true,[0x449]=true,
  [0x44A]=true,[0x44B]=true,[0x44C]=true,[0x44D]=true,[0x44E]=true,[0x44F]=true,[0x450]=true,[0x451]=true,[0x452]=true,[0x453]=true,[0x454]=true,[0x455]=true,
  [0x456]=true,[0x457]=true,[0x458]=true,[0x459]=true,[0x45A]=true,[0x45B]=true,[0x45C]=true,[0x45D]=true,[0x45E]=true,[0x45F]=true,[0x460]=true,[0x461]=true,
  [0x462]=true,[0x463]=true,[0x464]=true,[0x465]=true,[0x466]=true,[0x467]=true,[0x468]=true,[0x469]=true,[0x46A]=true,[0x46B]=true,[0x46C]=true,[0x46D]=true,
  [0x46E]=true,[0x46F]=true,[0x470]=true,[0x471]=true,[0x472]=true,[0x473]=true,[0x474]=true,[0x475]=true,[0x476]=true,[0x477]=true,[0x478]=true,[0x479]=true,
  [0x47A]=true,[0x47B]=true,[0x47C]=true,[0x47D]=true,[0x47E]=true,[0x47F]=true,[0x480]=true,[0x481]=true,[0x482]=true,[0x483]=true,[0x484]=true,[0x485]=true,
  [0x486]=true,[0x487]=true,[0x488]=true,[0x489]=true,[0x48A]=true,[0x48B]=true,[0x48C]=true,[0x48D]=true,[0x48E]=true,[0x48F]=true,[0x490]=true,[0x491]=true,
  [0x492]=true,[0x493]=true,[0x494]=true,[0x495]=true,[0x496]=true,[0x497]=true,[0x498]=true,[0x499]=true,[0x49A]=true,[0x49B]=true,[0x49C]=true,[0x49D]=true,
  [0x49E]=true,[0x49F]=true,[0x4A0]=true,[0x4A1]=true,[0x4A2]=true,[0x4A3]=true,[0x4A4]=true,[0x4A5]=true,[0x4A6]=true,[0x4A7]=true,[0x4A8]=true,[0x4A9]=true,
  [0x4AA]=true,[0x4AB]=true,[0x4AC]=true,[0x4AD]=true,[0x4AE]=true,[0x4AF]=true,[0x4B0]=true,[0x4B1]=true,[0x4B2]=true,[0x4B3]=true,[0x4B4]=true,[0x4B5]=true,
  [0x4B6]=true,[0x4B7]=true,[0x4B8]=true,[0x4B9]=true,[0x4BA]=true,[0x4BB]=true,[0x4BC]=true,[0x4BD]=true,[0x4BE]=true,[0x4BF]=true,[0x4C0]=true,[0x4C1]=true,
  [0x4C2]=true,[0x4C3]=true,[0x4C4]=true,[0x4C5]=true,[0x4C6]=true,[0x4C7]=true,[0x4C8]=true,[0x4C9]=true,[0x4CA]=true,[0x4CB]=true,[0x4CC]=true,[0x4CD]=true,
  [0x4CE]=true,[0x4CF]=true,[0x4D0]=true,[0x4D1]=true,[0x4D2]=true,[0x4D3]=true,[0x4D4]=true,[0x4D5]=true,[0x4D6]=true,[0x4D7]=true,[0x4D8]=true,[0x4D9]=true,
  [0x4DA]=true,[0x4DB]=true,[0x4DC]=true,[0x4DD]=true,[0x4DE]=true,[0x4DF]=true,[0x4E0]=true,[0x4E1]=true,[0x4E2]=true,[0x4E3]=true,[0x4E4]=true,[0x4E5]=true,
  [0x4E6]=true,[0x4E7]=true,[0x4E8]=true,[0x4E9]=true,[0x4EA]=true,[0x4EB]=true,[0x4EC]=true,[0x4ED]=true,[0x4EE]=true,[0x4EF]=true,[0x4F0]=true,[0x4F1]=true,
  [0x4F2]=true,[0x4F3]=true,[0x4F4]=true,[0x4F5]=true,[0x4F6]=true,[0x4F7]=true,[0x4F8]=true,[0x4F9]=true,[0x4FA]=true,[0x4FB]=true,[0x4FC]=true,[0x4FD]=true,
  [0x4FE]=true,[0x4FF]=true,[0x500]=true,[0x501]=true,[0x502]=true,[0x503]=true,[0x504]=true,[0x505]=true,[0x506]=true,[0x507]=true,[0x508]=true,[0x509]=true,
  [0x50A]=true,[0x50B]=true,[0x50C]=true,[0x50D]=true,[0x50E]=true,[0x50F]=true,[0x510]=true,[0x511]=true,[0x512]=true,[0x513]=true,[0x514]=true,[0x515]=true,
  [0x516]=true,[0x517]=true,[0x518]=true,[0x519]=true,[0x51A]=true,[0x51B]=true,[0x51C]=true,[0x51D]=true,[0x51E]=true,[0x51F]=true,[0x520]=true,[0x521]=true,
  [0x522]=true,[0x523]=true,[0x524]=true,[0x525]=true,[0x526]=true,[0x527]=true,[0x528]=true,[0x529]=true,[0x52A]=true,[0x52B]=true,[0x52C]=true,[0x52D]=true,
  [0x52E]=true,[0x52F]=true,[0x530]=true,[0x531]=true,[0x532]=true,[0x533]=true,[0x534]=true,[0x535]=true,[0x536]=true,[0x537]=true,[0x538]=true,[0x539]=true,
  [0x53A]=true,[0x53B]=true,[0x53C]=true,[0x53D]=true,[0x53E]=true,[0x53F]=true,[0x540]=true,[0x541]=true,[0x542]=true,[0x543]=true,[0x544]=true,[0x545]=true,
  [0x546]=true,[0x547]=true,[0x548]=true,[0x549]=true,[0x54A]=true,[0x54B]=true,[0x54C]=true,[0x54D]=true,[0x54E]=true,[0x54F]=true,[0x550]=true,[0x551]=true,
  [0x552]=true,[0x553]=true,[0x554]=true,[0x555]=true,[0x556]=true,[0x557]=true,[0x559]=true,[0x55A]=true,[0x55B]=true,[0x55C]=true,[0x55D]=true,[0x55E]=true,
  [0x55F]=true,[0x560]=true,[0x561]=true,[0x562]=true,[0x563]=true,[0x564]=true,[0x565]=true,[0x566]=true,[0x567]=true,[0x568]=true,[0x569]=true,[0x56A]=true,
  [0x56B]=true,[0x56C]=true,[0x56D]=true,[0x56E]=true,[0x56F]=true,[0x570]=true,[0x571]=true,[0x572]=true,[0x573]=true,[0x574]=true,[0x575]=true,[0x576]=true,
  [0x577]=true,[0x578]=true,[0x579]=true,[0x57A]=true,[0x57B]=true,[0x57C]=true,[0x57D]=true,[0x57E]=true,[0x57F]=true,[0x580]=true,[0x581]=true,[0x582]=true,
  [0x583]=true,[0x584]=true,[0x585]=true,[0x586]=true,[0x587]=true,[0x588]=true,[0x589]=true,[0x58A]=true,[0x58B]=true,[0x58C]=true,[0x58D]=true,[0x58E]=true,
  [0x58F]=true,[0x590]=true,[0x591]=true,[0x592]=true,[0x593]=true,[0x594]=true,[0x595]=true,[0x596]=true,[0x597]=true,[0x598]=true,[0x599]=true,[0x59A]=true,
  [0x59B]=true,[0x59C]=true,[0x59D]=true,[0x59E]=true,[0x59F]=true,[0x5A0]=true,[0x5A1]=true,[0x5A2]=true,[0x5A3]=true,[0x5A4]=true,[0x5A5]=true,[0x5A6]=true,
  [0x5A7]=true,[0x5A8]=true,[0x5A9]=true,[0x5AA]=true,[0x5AB]=true,[0x5AC]=true,[0x5AD]=true,[0x5AE]=true,[0x5AF]=true,[0x5B0]=true,[0x5B1]=true,[0x5B2]=true,
  [0x5B3]=true,[0x5B4]=true,[0x5B5]=true,[0x5B6]=true,[0x5B7]=true,[0x5B8]=true,[0x5B9]=true,[0x5BA]=true,[0x5BB]=true,[0x5BC]=true,[0x5BD]=true,[0x5BE]=true,
  [0x5BF]=true,[0x5C0]=true,[0x5C1]=true,[0x5C2]=true,[0x5C3]=true,[0x5C4]=true,[0x5C5]=true,[0x5C6]=true,[0x5C7]=true,[0x5C8]=true,[0x5C9]=true,[0x5CA]=true,
  [0x5CB]=true,[0x5CC]=true,[0x5CD]=true,[0x5CE]=true,[0x5CF]=true,[0x5D0]=true,[0x5D1]=true,[0x5D2]=true,[0x5D3]=true,[0x5D4]=true,[0x5D5]=true,[0x5D6]=true,
  [0x5D7]=true,[0x5D8]=true,[0x5D9]=true,[0x5DA]=true,[0x5DB]=true,[0x5DC]=true,[0x5DD]=true,[0x5DE]=true,[0x5DF]=true,[0x5E0]=true,[0x5E1]=true,[0x5E2]=true,
  [0x5E3]=true,[0x5E4]=true,[0x5E5]=true,[0x5E6]=true,[0x5E7]=true,[0x5E8]=true,[0x5E9]=true,[0x5EA]=true,[0x5EB]=true,[0x5EC]=true,[0x5ED]=true,[0x5EE]=true,
  [0x5EF]=true,[0x5F0]=true,[0x5F1]=true,[0x5F2]=true,[0x5F3]=true,[0x5F4]=true,[0x5F5]=true,[0x5F6]=true,[0x5F7]=true,[0x5F8]=true,[0x5F9]=true,[0x5FA]=true,
  [0x5FB]=true,[0x5FC]=true,[0x5FD]=true,[0x5FE]=true,[0x5FF]=true,[0x600]=true,[0x601]=true,[0x602]=true,[0x603]=true,[0x604]=true,[0x605]=true,[0x606]=true,
  [0x607]=true,[0x608]=true,[0x609]=true,[0x60A]=true,[0x60B]=true,[0x60C]=true,[0x60D]=true,[0x60E]=true,[0x60F]=true,[0x610]=true,[0x611]=true,[0x612]=true,
  [0x613]=true,[0x614]=true,[0x615]=true,[0x616]=true,[0x617]=true,[0x618]=true,[0x619]=true,[0x61A]=true,[0x61B]=true,[0x61C]=true,[0x61D]=true,[0x61E]=true,
  [0x61F]=true,[0x620]=true,[0x621]=true,[0x622]=true,[0x623]=true,[0x624]=true,[0x625]=true,[0x626]=true,[0x627]=true,[0x628]=true,[0x629]=true,[0x62A]=true,
  [0x62B]=true,[0x62C]=true,[0x62D]=true,[0x62E]=true,[0x62F]=true,[0x630]=true,[0x631]=true,[0x632]=true,[0x633]=true,[0x634]=true,[0x635]=true,[0x636]=true,
  [0x637]=true,[0x638]=true,[0x639]=true,[0x63A]=true,[0x63B]=true,[0x63C]=true,[0x63D]=true,[0x63E]=true,[0x63F]=true,[0x640]=true,[0x641]=true,[0x642]=true,
  [0x643]=true,[0x644]=true,[0x645]=true,[0x646]=true,[0x647]=true,[0x648]=true,[0x649]=true,[0x64A]=true,[0x64B]=true,[0x64C]=true,[0x64D]=true,[0x64E]=true,
  [0x64F]=true,[0x650]=true,[0x651]=true,[0x652]=true,[0x653]=true,[0x654]=true,[0x655]=true,[0x656]=true,[0x657]=true,[0x658]=true,[0x659]=true,[0x65A]=true,
  [0x65B]=true,[0x65C]=true,[0x65D]=true,[0x65E]=true,[0x65F]=true,[0x660]=true,[0x661]=true,[0x662]=true,[0x663]=true,[0x664]=true,[0x665]=true,[0x666]=true,
  [0x667]=true,[0x668]=true,[0x669]=true,[0x66A]=true,[0x66B]=true,[0x66C]=true,[0x66D]=true,[0x66E]=true,[0x66F]=true,[0x670]=true,[0x671]=true,[0x672]=true,
  [0x673]=true,[0x674]=true,[0x675]=true,[0x676]=true,[0x677]=true,[0x678]=true,[0x679]=true,[0x67A]=true,[0x67B]=true,[0x67C]=true,[0x67D]=true,[0x67E]=true,
  [0x67F]=true,[0x680]=true,[0x681]=true,[0x682]=true,[0x683]=true,[0x684]=true,[0x685]=true,[0x686]=true,[0x687]=true,[0x688]=true,[0x689]=true,[0x68A]=true,
  [0x68B]=true,[0x68C]=true,[0x68D]=true,[0x68E]=true,[0x68F]=true,[0x690]=true,[0x691]=true,[0x692]=true,[0x693]=true,[0x694]=true,[0x695]=true,[0x696]=true,
  [0x697]=true,[0x698]=true,[0x699]=true,[0x69A]=true,[0x69B]=true,[0x69C]=true,[0x69D]=true,[0x69E]=true,[0x69F]=true,[0x6A0]=true,[0x6A1]=true,[0x6A2]=true,
  [0x6A3]=true,[0x6A4]=true,[0x6A5]=true,[0x6A6]=true,[0x6A7]=true,[0x6A8]=true,[0x6A9]=true,[0x6AA]=true,[0x6AB]=true,[0x6AC]=true,[0x6AD]=true,[0x6AE]=true,
  [0x6AF]=true,[0x6B0]=true,[0x6B1]=true,[0x6B2]=true,[0x6B3]=true,[0x6B4]=true,[0x6B5]=true,[0x6B6]=true,[0x6B7]=true,[0x6B8]=true,[0x6B9]=true,[0x6BA]=true,
  [0x6BB]=true,[0x6BC]=true,[0x6BD]=true,[0x6BE]=true,[0x6BF]=true,[0x6C0]=true,[0x6C1]=true,[0x6C2]=true,[0x6C3]=true,[0x6C4]=true,[0x6C5]=true,[0x6C6]=true,
  [0x6C7]=true,[0x6C8]=true,[0x6C9]=true,[0x6CA]=true,[0x6CB]=true,[0x6CC]=true,[0x6CD]=true,[0x6CE]=true,[0x6CF]=true,[0x6D0]=true,[0x6D1]=true,[0x6D2]=true,
  [0x6D3]=true,[0x6D4]=true,[0x6D5]=true,[0x6D6]=true,[0x6D7]=true,[0x6D8]=true,[0x6D9]=true,[0x6DA]=true,[0x6DB]=true,[0x6DC]=true,[0x6DD]=true,[0x6DE]=true,
  [0x6DF]=true,[0x6E0]=true,[0x6E1]=true,[0x6E2]=true,[0x6E3]=true,[0x6E4]=true,[0x6E5]=true,[0x6E6]=true,[0x6E7]=true,[0x6E8]=true,[0x6E9]=true,[0x6EA]=true,
  [0x6EB]=true,[0x6EC]=true,[0x6ED]=true,[0x6EE]=true,[0x6EF]=true,[0x6F0]=true,[0x6F1]=true,[0x6F2]=true,[0x6F3]=true,[0x6F4]=true,[0x6F5]=true,[0x6F6]=true,
  [0x6F7]=true,[0x6F8]=true,[0x6F9]=true,[0x6FA]=true,[0x6FB]=true,[0x6FC]=true,[0x6FD]=true,[0x6FE]=true,[0x6FF]=true,[0x700]=true,[0x701]=true,[0x702]=true,
  [0x703]=true,[0x704]=true,[0x705]=true,[0x706]=true,[0x707]=true,[0x708]=true,[0x709]=true,[0x70A]=true,[0x70B]=true,[0x70C]=true,[0x70D]=true,[0x70E]=true,
  [0x70F]=true,[0x710]=true,[0x711]=true,[0x712]=true,[0x713]=true,[0x714]=true,[0x715]=true,[0x716]=true,[0x717]=true,[0x718]=true,[0x719]=true,[0x71A]=true,
  [0x71B]=true,[0x71C]=true,[0x71D]=true,[0x71E]=true,[0x71F]=true,[0x720]=true,[0x721]=true,[0x722]=true,[0x723]=true,[0x724]=true,[0x725]=true,[0x726]=true,
  [0x727]=true,[0x728]=true,[0x729]=true,[0x72A]=true,[0x72B]=true,[0x72C]=true,[0x72D]=true,[0x72E]=true,[0x72F]=true,[0x730]=true,[0x731]=true,[0x732]=true,
  [0x733]=true,[0x734]=true,[0x735]=true,[0x736]=true,[0x737]=true,[0x738]=true,[0x739]=true,[0x73A]=true,[0x73B]=true,[0x73C]=true,[0x73D]=true,[0x73E]=true,
  [0x73F]=true,[0x740]=true,[0x741]=true,[0x742]=true,[0x743]=true,[0x744]=true,[0x745]=true,[0x746]=true,[0x747]=true,[0x748]=true,[0x749]=true,[0x74A]=true,
  [0x74B]=true,[0x74C]=true,[0x74D]=true,[0x74E]=true,[0x74F]=true,[0x750]=true,[0x751]=true,[0x752]=true,[0x753]=true,[0x754]=true,[0x755]=true,[0x756]=true,
  [0x757]=true,[0x758]=true,[0x759]=true,[0x75A]=true,[0x75B]=true,[0x75C]=true,[0x75D]=true,[0x75E]=true,[0x75F]=true,[0x760]=true,[0x761]=true,
}
-- 959 total locations (AP id == raw in-game code, no base offset)

-- Boss crystals: boss_crystals[i] (RDRAM save+0xC0) != 0 -> loc i+0x0200.
local BOSS_BASE = 0x0200
local BOSS_COUNT = 6

-- Stage completes: slot -> stage_array byte offset (==0x02 means cleared).
-- The loc id at each physical slot is read at runtime from the ROM level
-- table (K64_LEVEL_ADDRESS) so stage shuffle is honored exactly like client.py.
local STAGE_SLOTS = {
  {level=1,slot=0,byte=0x0,rom_index=0},
  {level=1,slot=1,byte=0x1,rom_index=1},
  {level=1,slot=2,byte=0x2,rom_index=2},
  {level=2,slot=0,byte=0x6,rom_index=4},
  {level=2,slot=1,byte=0x7,rom_index=5},
  {level=2,slot=2,byte=0x8,rom_index=6},
  {level=2,slot=3,byte=0x9,rom_index=7},
  {level=3,slot=0,byte=0xC,rom_index=9},
  {level=3,slot=1,byte=0xD,rom_index=10},
  {level=3,slot=2,byte=0xE,rom_index=11},
  {level=3,slot=3,byte=0xF,rom_index=12},
  {level=4,slot=0,byte=0x12,rom_index=14},
  {level=4,slot=1,byte=0x13,rom_index=15},
  {level=4,slot=2,byte=0x14,rom_index=16},
  {level=4,slot=3,byte=0x15,rom_index=17},
  {level=5,slot=0,byte=0x18,rom_index=19},
  {level=5,slot=1,byte=0x19,rom_index=20},
  {level=5,slot=2,byte=0x1A,rom_index=21},
  {level=5,slot=3,byte=0x1B,rom_index=22},
  {level=6,slot=0,byte=0x1E,rom_index=24},
  {level=6,slot=1,byte=0x1F,rom_index=25},
  {level=6,slot=2,byte=0x20,rom_index=26},
}

-- Crystal shards: 3 per stage. crystal_array (RDRAM save+0xC8) is 6 LE u32
-- words (one per level); bit (slot*8 + i) set -> crystal loc. loc ids come
-- from default_levels (static), exactly per client.py.
local CRYSTAL_SLOTS = {
  {word=0,bit=0,c0=0x101,c1=0x102,c2=0x103},
  {word=0,bit=8,c0=0x104,c1=0x105,c2=0x106},
  {word=0,bit=16,c0=0x107,c1=0x108,c2=0x109},
  {word=1,bit=0,c0=0x10A,c1=0x10B,c2=0x10C},
  {word=1,bit=8,c0=0x10D,c1=0x10E,c2=0x10F},
  {word=1,bit=16,c0=0x110,c1=0x111,c2=0x112},
  {word=1,bit=24,c0=0x113,c1=0x114,c2=0x115},
  {word=2,bit=0,c0=0x116,c1=0x117,c2=0x118},
  {word=2,bit=8,c0=0x119,c1=0x11A,c2=0x11B},
  {word=2,bit=16,c0=0x11C,c1=0x11D,c2=0x11E},
  {word=2,bit=24,c0=0x11F,c1=0x120,c2=0x121},
  {word=3,bit=0,c0=0x122,c1=0x123,c2=0x124},
  {word=3,bit=8,c0=0x125,c1=0x126,c2=0x127},
  {word=3,bit=16,c0=0x128,c1=0x129,c2=0x12A},
  {word=3,bit=24,c0=0x12B,c1=0x12C,c2=0x12D},
  {word=4,bit=0,c0=0x12E,c1=0x12F,c2=0x130},
  {word=4,bit=8,c0=0x131,c1=0x132,c2=0x133},
  {word=4,bit=16,c0=0x134,c1=0x135,c2=0x136},
  {word=4,bit=24,c0=0x137,c1=0x138,c2=0x139},
  {word=5,bit=0,c0=0x13A,c1=0x13B,c2=0x13C},
  {word=5,bit=8,c0=0x13D,c1=0x13E,c2=0x13F},
  {word=5,bit=16,c0=0x140,c1=0x141,c2=0x142},
}

-- Consumables: ap_id -> byte index into consumable_checks (RDRAM 0x500000,
-- 0xC80 bytes) + a 64-bit mask. The client does int.from_bytes(buf[idx:idx+8])
-- (BIG-ENDIAN) then &mask. We store the 8 mask bytes (MSB-first) and test the
-- single set bit against the matching consumable_checks byte to stay exact in
-- Lua's number range (masks reach 0x8000000000000000).
local CONSUMABLES = {
  [0x400]={byte=0x7,bit=0},
  [0x401]={byte=0x6,bit=1},
  [0x402]={byte=0x5,bit=6},
  [0x403]={byte=0x27,bit=7},
  [0x404]={byte=0x26,bit=1},
  [0x405]={byte=0x26,bit=3},
  [0x406]={byte=0x26,bit=5},
  [0x407]={byte=0x25,bit=7},
  [0x408]={byte=0x24,bit=1},
  [0x409]={byte=0x24,bit=2},
  [0x40A]={byte=0x24,bit=5},
  [0x40B]={byte=0x87,bit=0},
  [0x40C]={byte=0x87,bit=6},
  [0x40D]={byte=0x86,bit=0},
  [0x40E]={byte=0x86,bit=4},
  [0x40F]={byte=0x85,bit=5},
  [0x410]={byte=0x84,bit=6},
  [0x411]={byte=0x83,bit=0},
  [0x412]={byte=0x83,bit=1},
  [0x413]={byte=0x83,bit=3},
  [0x414]={byte=0x97,bit=0},
  [0x415]={byte=0x97,bit=1},
  [0x416]={byte=0x97,bit=4},
  [0x417]={byte=0x96,bit=2},
  [0x418]={byte=0x96,bit=4},
  [0x419]={byte=0xA7,bit=1},
  [0x41A]={byte=0xA7,bit=2},
  [0x41B]={byte=0xA7,bit=3},
  [0x41C]={byte=0xA7,bit=4},
  [0x41D]={byte=0xA7,bit=5},
  [0x41E]={byte=0xA7,bit=6},
  [0x41F]={byte=0xA7,bit=7},
  [0x420]={byte=0xA6,bit=0},
  [0x421]={byte=0xA6,bit=1},
  [0x422]={byte=0xA6,bit=2},
  [0x423]={byte=0xA6,bit=3},
  [0x424]={byte=0xA6,bit=4},
  [0x425]={byte=0xB6,bit=7},
  [0x426]={byte=0xB6,bit=2},
  [0x427]={byte=0xB6,bit=4},
  [0x428]={byte=0xB7,bit=3},
  [0x429]={byte=0xB7,bit=4},
  [0x42A]={byte=0xB6,bit=3},
  [0x42B]={byte=0xB7,bit=5},
  [0x42C]={byte=0xB7,bit=6},
  [0x42D]={byte=0xB7,bit=7},
  [0x42E]={byte=0xB6,bit=0},
  [0x42F]={byte=0xB6,bit=1},
  [0x430]={byte=0xB7,bit=1},
  [0x431]={byte=0xB5,bit=0},
  [0x432]={byte=0x107,bit=0},
  [0x433]={byte=0x107,bit=7},
  [0x434]={byte=0x106,bit=3},
  [0x435]={byte=0x106,bit=5},
  [0x436]={byte=0x106,bit=7},
  [0x437]={byte=0x105,bit=0},
  [0x438]={byte=0x117,bit=1},
  [0x439]={byte=0x127,bit=2},
  [0x43A]={byte=0x127,bit=7},
  [0x43B]={byte=0x137,bit=1},
  [0x43C]={byte=0x137,bit=3},
  [0x43D]={byte=0x137,bit=4},
  [0x43E]={byte=0x147,bit=0},
  [0x43F]={byte=0x146,bit=2},
  [0x440]={byte=0x160,bit=7},
  [0x441]={byte=0x177,bit=7},
  [0x442]={byte=0x176,bit=6},
  [0x443]={byte=0x176,bit=2},
  [0x444]={byte=0x177,bit=3},
  [0x445]={byte=0x177,bit=6},
  [0x446]={byte=0x207,bit=6},
  [0x447]={byte=0x206,bit=1},
  [0x448]={byte=0x206,bit=4},
  [0x449]={byte=0x205,bit=1},
  [0x44A]={byte=0x205,bit=7},
  [0x44B]={byte=0x204,bit=7},
  [0x44C]={byte=0x203,bit=2},
  [0x44D]={byte=0x203,bit=3},
  [0x44E]={byte=0x203,bit=6},
  [0x44F]={byte=0x217,bit=3},
  [0x450]={byte=0x217,bit=7},
  [0x451]={byte=0x216,bit=2},
  [0x452]={byte=0x216,bit=3},
  [0x453]={byte=0x216,bit=4},
  [0x454]={byte=0x216,bit=5},
  [0x455]={byte=0x246,bit=5},
  [0x456]={byte=0x245,bit=0},
  [0x457]={byte=0x245,bit=2},
  [0x458]={byte=0x245,bit=4},
  [0x459]={byte=0x245,bit=6},
  [0x45A]={byte=0x244,bit=1},
  [0x45B]={byte=0x287,bit=0},
  [0x45C]={byte=0x287,bit=4},
  [0x45D]={byte=0x284,bit=0},
  [0x45E]={byte=0x284,bit=5},
  [0x45F]={byte=0x284,bit=6},
  [0x460]={byte=0x283,bit=0},
  [0x461]={byte=0x283,bit=1},
  [0x462]={byte=0x283,bit=6},
  [0x463]={byte=0x282,bit=0},
  [0x464]={byte=0x282,bit=2},
  [0x465]={byte=0x282,bit=4},
  [0x466]={byte=0x282,bit=6},
  [0x467]={byte=0x296,bit=0},
  [0x468]={byte=0x296,bit=5},
  [0x469]={byte=0x295,bit=6},
  [0x46A]={byte=0x2A7,bit=0},
  [0x46B]={byte=0x2A7,bit=6},
  [0x46C]={byte=0x2A6,bit=1},
  [0x46D]={byte=0x2B7,bit=1},
  [0x46E]={byte=0x2B7,bit=5},
  [0x46F]={byte=0x2B6,bit=2},
  [0x470]={byte=0x2B6,bit=5},
  [0x471]={byte=0x2B5,bit=1},
  [0x472]={byte=0x2B4,bit=5},
  [0x473]={byte=0x2B4,bit=6},
  [0x474]={byte=0x2B4,bit=7},
  [0x475]={byte=0x2C7,bit=1},
  [0x476]={byte=0x2C7,bit=4},
  [0x477]={byte=0x2C6,bit=1},
  [0x478]={byte=0x2C6,bit=4},
  [0x479]={byte=0x2C6,bit=6},
  [0x47A]={byte=0x2C5,bit=1},
  [0x47B]={byte=0x2C5,bit=3},
  [0x47C]={byte=0x2C5,bit=6},
  [0x47D]={byte=0x2C4,bit=1},
  [0x47E]={byte=0x2C4,bit=3},
  [0x47F]={byte=0x2C4,bit=7},
  [0x480]={byte=0x2D6,bit=4},
  [0x481]={byte=0x2D6,bit=3},
  [0x482]={byte=0x2D6,bit=5},
  [0x483]={byte=0x2D5,bit=2},
  [0x484]={byte=0x2D5,bit=3},
  [0x485]={byte=0x2D5,bit=4},
  [0x486]={byte=0x2D5,bit=5},
  [0x487]={byte=0x2D6,bit=1},
  [0x488]={byte=0x2D7,bit=1},
  [0x489]={byte=0x2D7,bit=5},
  [0x48A]={byte=0x2D7,bit=6},
  [0x48B]={byte=0x2D7,bit=7},
  [0x48C]={byte=0x2D5,bit=1},
  [0x48D]={byte=0x307,bit=0},
  [0x48E]={byte=0x307,bit=6},
  [0x48F]={byte=0x306,bit=0},
  [0x490]={byte=0x304,bit=3},
  [0x491]={byte=0x304,bit=5},
  [0x492]={byte=0x304,bit=6},
  [0x493]={byte=0x317,bit=1},
  [0x494]={byte=0x317,bit=0},
  [0x495]={byte=0x317,bit=3},
  [0x496]={byte=0x317,bit=4},
  [0x497]={byte=0x337,bit=2},
  [0x498]={byte=0x337,bit=3},
  [0x499]={byte=0x337,bit=5},
  [0x49A]={byte=0x337,bit=7},
  [0x49B]={byte=0x347,bit=0},
  [0x49C]={byte=0x347,bit=5},
  [0x49D]={byte=0x346,bit=2},
  [0x49E]={byte=0x346,bit=6},
  [0x49F]={byte=0x345,bit=2},
  [0x4A0]={byte=0x345,bit=1},
  [0x4A1]={byte=0x357,bit=2},
  [0x4A2]={byte=0x357,bit=1},
  [0x4A3]={byte=0x357,bit=0},
  [0x4A4]={byte=0x367,bit=4},
  [0x4A5]={byte=0x367,bit=5},
  [0x4A6]={byte=0x367,bit=6},
  [0x4A7]={byte=0x366,bit=1},
  [0x4A8]={byte=0x366,bit=0},
  [0x4A9]={byte=0x366,bit=2},
  [0x4AA]={byte=0x367,bit=7},
  [0x4AB]={byte=0x366,bit=3},
  [0x4AC]={byte=0x366,bit=4},
  [0x4AD]={byte=0x366,bit=5},
  [0x4AE]={byte=0x366,bit=6},
  [0x4AF]={byte=0x365,bit=3},
  [0x4B0]={byte=0x377,bit=0},
  [0x4B1]={byte=0x377,bit=5},
  [0x4B2]={byte=0x376,bit=2},
  [0x4B3]={byte=0x376,bit=5},
  [0x4B4]={byte=0x375,bit=0},
  [0x4B5]={byte=0x376,bit=1},
  [0x4B6]={byte=0x375,bit=1},
  [0x4B7]={byte=0x377,bit=7},
  [0x4B8]={byte=0x376,bit=4},
  [0x4B9]={byte=0x376,bit=6},
  [0x4BA]={byte=0x375,bit=3},
  [0x4BB]={byte=0x374,bit=0},
  [0x4BC]={byte=0x387,bit=3},
  [0x4BD]={byte=0x387,bit=4},
  [0x4BE]={byte=0x386,bit=4},
  [0x4BF]={byte=0x386,bit=3},
  [0x4C0]={byte=0x3A7,bit=5},
  [0x4C1]={byte=0x3A7,bit=3},
  [0x4C2]={byte=0x3D7,bit=0},
  [0x4C3]={byte=0x3D6,bit=7},
  [0x4C4]={byte=0x3D5,bit=0},
  [0x4C5]={byte=0x3D5,bit=1},
  [0x4C6]={byte=0x3D5,bit=6},
  [0x4C7]={byte=0x3D6,bit=5},
  [0x4C8]={byte=0x3D6,bit=2},
  [0x4C9]={byte=0x3D6,bit=0},
  [0x4CA]={byte=0x3D7,bit=5},
  [0x4CB]={byte=0x3F7,bit=6},
  [0x4CC]={byte=0x3F7,bit=0},
  [0x4CD]={byte=0x3F7,bit=7},
  [0x4CE]={byte=0x3F6,bit=5},
  [0x4CF]={byte=0x3F6,bit=0},
  [0x4D0]={byte=0x407,bit=2},
  [0x4D1]={byte=0x407,bit=4},
  [0x4D2]={byte=0x407,bit=7},
  [0x4D3]={byte=0x406,bit=1},
  [0x4D4]={byte=0x406,bit=2},
  [0x4D5]={byte=0x406,bit=5},
  [0x4D6]={byte=0x405,bit=2},
  [0x4D7]={byte=0x405,bit=6},
  [0x4D8]={byte=0x404,bit=0},
  [0x4D9]={byte=0x404,bit=1},
  [0x4DA]={byte=0x404,bit=4},
  [0x4DB]={byte=0x404,bit=5},
  [0x4DC]={byte=0x404,bit=7},
  [0x4DD]={byte=0x403,bit=0},
  [0x4DE]={byte=0x403,bit=3},
  [0x4DF]={byte=0x403,bit=4},
  [0x4E0]={byte=0x403,bit=5},
  [0x4E1]={byte=0x403,bit=7},
  [0x4E2]={byte=0x416,bit=0},
  [0x4E3]={byte=0x415,bit=0},
  [0x4E4]={byte=0x415,bit=1},
  [0x4E5]={byte=0x417,bit=7},
  [0x4E6]={byte=0x417,bit=4},
  [0x4E7]={byte=0x417,bit=3},
  [0x4E8]={byte=0x427,bit=3},
  [0x4E9]={byte=0x426,bit=0},
  [0x4EA]={byte=0x427,bit=6},
  [0x4EB]={byte=0x426,bit=2},
  [0x4EC]={byte=0x426,bit=5},
  [0x4ED]={byte=0x425,bit=1},
  [0x4EE]={byte=0x426,bit=7},
  [0x4EF]={byte=0x425,bit=3},
  [0x4F0]={byte=0x425,bit=7},
  [0x4F1]={byte=0x437,bit=1},
  [0x4F2]={byte=0x437,bit=3},
  [0x4F3]={byte=0x437,bit=5},
  [0x4F4]={byte=0x436,bit=2},
  [0x4F5]={byte=0x436,bit=4},
  [0x4F6]={byte=0x436,bit=6},
  [0x4F7]={byte=0x450,bit=7},
  [0x4F8]={byte=0x457,bit=5},
  [0x4F9]={byte=0x457,bit=6},
  [0x4FA]={byte=0x455,bit=2},
  [0x4FB]={byte=0x455,bit=3},
  [0x4FC]={byte=0x455,bit=5},
  [0x4FD]={byte=0x455,bit=6},
  [0x4FE]={byte=0x487,bit=5},
  [0x4FF]={byte=0x485,bit=0},
  [0x500]={byte=0x484,bit=1},
  [0x501]={byte=0x484,bit=2},
  [0x502]={byte=0x484,bit=7},
  [0x503]={byte=0x497,bit=3},
  [0x504]={byte=0x496,bit=1},
  [0x505]={byte=0x496,bit=6},
  [0x506]={byte=0x495,bit=4},
  [0x507]={byte=0x494,bit=1},
  [0x508]={byte=0x4B7,bit=0},
  [0x509]={byte=0x4B7,bit=2},
  [0x50A]={byte=0x4B7,bit=3},
  [0x50B]={byte=0x4B7,bit=4},
  [0x50C]={byte=0x4B7,bit=5},
  [0x50D]={byte=0x4B7,bit=6},
  [0x50E]={byte=0x4B7,bit=7},
  [0x50F]={byte=0x4B6,bit=0},
  [0x510]={byte=0x4B6,bit=1},
  [0x511]={byte=0x4B6,bit=2},
  [0x512]={byte=0x4B6,bit=4},
  [0x513]={byte=0x4B6,bit=5},
  [0x514]={byte=0x4B6,bit=6},
  [0x515]={byte=0x4B5,bit=0},
  [0x516]={byte=0x4B5,bit=1},
  [0x517]={byte=0x4B5,bit=2},
  [0x518]={byte=0x4B5,bit=4},
  [0x519]={byte=0x4B5,bit=6},
  [0x51A]={byte=0x4B5,bit=7},
  [0x51B]={byte=0x4B4,bit=0},
  [0x51C]={byte=0x4B4,bit=2},
  [0x51D]={byte=0x4B4,bit=3},
  [0x51E]={byte=0x4B4,bit=4},
  [0x51F]={byte=0x4B4,bit=5},
  [0x520]={byte=0x4B4,bit=6},
  [0x521]={byte=0x4B4,bit=7},
  [0x522]={byte=0x4B3,bit=0},
  [0x523]={byte=0x4B3,bit=1},
  [0x524]={byte=0x4B3,bit=2},
  [0x525]={byte=0x4B3,bit=3},
  [0x526]={byte=0x4B3,bit=4},
  [0x527]={byte=0x4B3,bit=5},
  [0x528]={byte=0x4B3,bit=6},
  [0x529]={byte=0x4B3,bit=7},
  [0x52A]={byte=0x4B2,bit=1},
  [0x52B]={byte=0x4B2,bit=2},
  [0x52C]={byte=0x4B2,bit=3},
  [0x52D]={byte=0x4B2,bit=4},
  [0x52E]={byte=0x4B2,bit=5},
  [0x52F]={byte=0x4B2,bit=6},
  [0x530]={byte=0x4B2,bit=7},
  [0x531]={byte=0x4B1,bit=0},
  [0x532]={byte=0x4B1,bit=1},
  [0x533]={byte=0x4B1,bit=2},
  [0x534]={byte=0x4D7,bit=0},
  [0x535]={byte=0x4D7,bit=3},
  [0x536]={byte=0x4D7,bit=6},
  [0x537]={byte=0x4D6,bit=5},
  [0x538]={byte=0x4D5,bit=0},
  [0x539]={byte=0x4D5,bit=6},
  [0x53A]={byte=0x507,bit=7},
  [0x53B]={byte=0x506,bit=1},
  [0x53C]={byte=0x505,bit=1},
  [0x53D]={byte=0x505,bit=4},
  [0x53E]={byte=0x505,bit=6},
  [0x53F]={byte=0x516,bit=0},
  [0x540]={byte=0x516,bit=1},
  [0x541]={byte=0x516,bit=6},
  [0x542]={byte=0x516,bit=7},
  [0x543]={byte=0x515,bit=5},
  [0x544]={byte=0x515,bit=7},
  [0x545]={byte=0x537,bit=4},
  [0x546]={byte=0x537,bit=7},
  [0x547]={byte=0x536,bit=2},
  [0x548]={byte=0x536,bit=5},
  [0x549]={byte=0x527,bit=2},
  [0x54A]={byte=0x547,bit=1},
  [0x54B]={byte=0x547,bit=6},
  [0x54C]={byte=0x545,bit=2},
  [0x54D]={byte=0x567,bit=1},
  [0x54E]={byte=0x565,bit=4},
  [0x54F]={byte=0x564,bit=5},
  [0x550]={byte=0x565,bit=6},
  [0x551]={byte=0x565,bit=3},
  [0x552]={byte=0x565,bit=7},
  [0x553]={byte=0x564,bit=2},
  [0x554]={byte=0x564,bit=3},
  [0x555]={byte=0x565,bit=5},
  [0x556]={byte=0x564,bit=4},
  [0x557]={byte=0x564,bit=1},
  [0x559]={byte=0x585,bit=7},
  [0x55A]={byte=0x587,bit=6},
  [0x55B]={byte=0x585,bit=1},
  [0x55C]={byte=0x587,bit=4},
  [0x55D]={byte=0x585,bit=3},
  [0x55E]={byte=0x587,bit=5},
  [0x55F]={byte=0x596,bit=3},
  [0x560]={byte=0x596,bit=5},
  [0x561]={byte=0x5A7,bit=5},
  [0x562]={byte=0x5A7,bit=4},
  [0x563]={byte=0x5A7,bit=0},
  [0x564]={byte=0x5A7,bit=1},
  [0x565]={byte=0x5A6,bit=3},
  [0x566]={byte=0x5A6,bit=2},
  [0x567]={byte=0x5A5,bit=1},
  [0x568]={byte=0x5A5,bit=2},
  [0x569]={byte=0x5A4,bit=7},
  [0x56A]={byte=0x5A3,bit=0},
  [0x56B]={byte=0x5A4,bit=0},
  [0x56C]={byte=0x5A4,bit=1},
  [0x56D]={byte=0x5A3,bit=6},
  [0x56E]={byte=0x5A3,bit=5},
  [0x56F]={byte=0x5A2,bit=2},
  [0x570]={byte=0x5A2,bit=1},
  [0x571]={byte=0x5B7,bit=0},
  [0x572]={byte=0x5B7,bit=1},
  [0x573]={byte=0x5B7,bit=2},
  [0x574]={byte=0x5B7,bit=3},
  [0x575]={byte=0x5B7,bit=7},
  [0x576]={byte=0x5B6,bit=3},
  [0x577]={byte=0x5B4,bit=5},
  [0x578]={byte=0x5D7,bit=1},
  [0x579]={byte=0x5D7,bit=2},
  [0x57A]={byte=0x5D7,bit=3},
  [0x57B]={byte=0x5D7,bit=4},
  [0x57C]={byte=0x5D7,bit=5},
  [0x57D]={byte=0x5D7,bit=6},
  [0x57E]={byte=0x5D7,bit=7},
  [0x57F]={byte=0x5D6,bit=0},
  [0x580]={byte=0x5D6,bit=1},
  [0x581]={byte=0x5D6,bit=2},
  [0x582]={byte=0x5D6,bit=3},
  [0x583]={byte=0x5D6,bit=5},
  [0x584]={byte=0x5D6,bit=7},
  [0x585]={byte=0x5D5,bit=0},
  [0x586]={byte=0x5D5,bit=1},
  [0x587]={byte=0x5D5,bit=4},
  [0x588]={byte=0x5D5,bit=6},
  [0x589]={byte=0x607,bit=6},
  [0x58A]={byte=0x606,bit=1},
  [0x58B]={byte=0x606,bit=3},
  [0x58C]={byte=0x606,bit=4},
  [0x58D]={byte=0x604,bit=1},
  [0x58E]={byte=0x603,bit=1},
  [0x58F]={byte=0x616,bit=0},
  [0x590]={byte=0x615,bit=2},
  [0x591]={byte=0x616,bit=4},
  [0x592]={byte=0x617,bit=3},
  [0x593]={byte=0x617,bit=7},
  [0x594]={byte=0x615,bit=1},
  [0x595]={byte=0x616,bit=1},
  [0x596]={byte=0x617,bit=4},
  [0x597]={byte=0x616,bit=2},
  [0x598]={byte=0x616,bit=3},
  [0x599]={byte=0x627,bit=3},
  [0x59A]={byte=0x627,bit=6},
  [0x59B]={byte=0x627,bit=7},
  [0x59C]={byte=0x626,bit=0},
  [0x59D]={byte=0x626,bit=1},
  [0x59E]={byte=0x626,bit=2},
  [0x59F]={byte=0x626,bit=4},
  [0x5A0]={byte=0x626,bit=6},
  [0x5A1]={byte=0x626,bit=7},
  [0x5A2]={byte=0x625,bit=1},
  [0x5A3]={byte=0x625,bit=2},
  [0x5A4]={byte=0x625,bit=3},
  [0x5A5]={byte=0x625,bit=4},
  [0x5A6]={byte=0x625,bit=6},
  [0x5A7]={byte=0x625,bit=7},
  [0x5A8]={byte=0x624,bit=1},
  [0x5A9]={byte=0x624,bit=2},
  [0x5AA]={byte=0x624,bit=3},
  [0x5AB]={byte=0x624,bit=4},
  [0x5AC]={byte=0x624,bit=5},
  [0x5AD]={byte=0x624,bit=6},
  [0x5AE]={byte=0x624,bit=7},
  [0x5AF]={byte=0x623,bit=1},
  [0x5B0]={byte=0x657,bit=6},
  [0x5B1]={byte=0x657,bit=7},
  [0x5B2]={byte=0x656,bit=2},
  [0x5B3]={byte=0x656,bit=4},
  [0x5B4]={byte=0x656,bit=7},
  [0x5B5]={byte=0x655,bit=3},
  [0x5B6]={byte=0x686,bit=4},
  [0x5B7]={byte=0x687,bit=5},
  [0x5B8]={byte=0x686,bit=2},
  [0x5B9]={byte=0x687,bit=2},
  [0x5BA]={byte=0x685,bit=1},
  [0x5BB]={byte=0x687,bit=7},
  [0x5BC]={byte=0x686,bit=6},
  [0x5BD]={byte=0x686,bit=1},
  [0x5BE]={byte=0x685,bit=0},
  [0x5BF]={byte=0x686,bit=5},
  [0x5C0]={byte=0x686,bit=0},
  [0x5C1]={byte=0x687,bit=4},
  [0x5C2]={byte=0x686,bit=3},
  [0x5C3]={byte=0x687,bit=3},
  [0x5C4]={byte=0x687,bit=1},
  [0x5C5]={byte=0x687,bit=6},
  [0x5C6]={byte=0x686,bit=7},
  [0x5C7]={byte=0x696,bit=5},
  [0x5C8]={byte=0x696,bit=7},
  [0x5C9]={byte=0x695,bit=5},
  [0x5CA]={byte=0x6A7,bit=5},
  [0x5CB]={byte=0x6A7,bit=6},
  [0x5CC]={byte=0x6A7,bit=7},
  [0x5CD]={byte=0x6A6,bit=0},
  [0x5CE]={byte=0x6A6,bit=6},
  [0x5CF]={byte=0x6A6,bit=7},
  [0x5D0]={byte=0x6A6,bit=4},
  [0x5D1]={byte=0x6A6,bit=5},
  [0x5D2]={byte=0x6A5,bit=2},
  [0x5D3]={byte=0x6A5,bit=5},
  [0x5D4]={byte=0x6A4,bit=0},
  [0x5D5]={byte=0x6B7,bit=1},
  [0x5D6]={byte=0x6B7,bit=3},
  [0x5D7]={byte=0x6B7,bit=7},
  [0x5D8]={byte=0x6B6,bit=2},
  [0x5D9]={byte=0x6B6,bit=6},
  [0x5DA]={byte=0x6B5,bit=1},
  [0x5DB]={byte=0x6B5,bit=5},
  [0x5DC]={byte=0x6B5,bit=7},
  [0x5DD]={byte=0x6B4,bit=2},
  [0x5DE]={byte=0x6B4,bit=5},
  [0x5DF]={byte=0x6C7,bit=4},
  [0x5E0]={byte=0x6C7,bit=6},
  [0x5E1]={byte=0x6C6,bit=2},
  [0x5E2]={byte=0x6C6,bit=4},
  [0x5E3]={byte=0x6C6,bit=6},
  [0x5E4]={byte=0x6C5,bit=1},
  [0x5E5]={byte=0x6C5,bit=3},
  [0x5E6]={byte=0x6E7,bit=0},
  [0x5E7]={byte=0x6E7,bit=2},
  [0x5E8]={byte=0x6E7,bit=3},
  [0x5E9]={byte=0x6E7,bit=4},
  [0x5EA]={byte=0x6E7,bit=5},
  [0x5EB]={byte=0x6E7,bit=6},
  [0x5EC]={byte=0x6E7,bit=7},
  [0x5ED]={byte=0x6E6,bit=0},
  [0x5EE]={byte=0x6E6,bit=2},
  [0x5EF]={byte=0x6E6,bit=3},
  [0x5F0]={byte=0x6E6,bit=4},
  [0x5F1]={byte=0x6E6,bit=5},
  [0x5F2]={byte=0x6E6,bit=6},
  [0x5F3]={byte=0x6E5,bit=0},
  [0x5F4]={byte=0x6E4,bit=1},
  [0x5F5]={byte=0x6E4,bit=2},
  [0x5F6]={byte=0x6E4,bit=3},
  [0x5F7]={byte=0x6E4,bit=4},
  [0x5F8]={byte=0x6E4,bit=6},
  [0x5F9]={byte=0x6E4,bit=7},
  [0x5FA]={byte=0x6E3,bit=1},
  [0x5FB]={byte=0x6E3,bit=2},
  [0x5FC]={byte=0x6E3,bit=3},
  [0x5FD]={byte=0x6E3,bit=4},
  [0x5FE]={byte=0x707,bit=3},
  [0x5FF]={byte=0x706,bit=1},
  [0x600]={byte=0x706,bit=5},
  [0x601]={byte=0x706,bit=7},
  [0x602]={byte=0x706,bit=6},
  [0x603]={byte=0x705,bit=1},
  [0x604]={byte=0x704,bit=2},
  [0x605]={byte=0x717,bit=6},
  [0x606]={byte=0x716,bit=3},
  [0x607]={byte=0x715,bit=0},
  [0x608]={byte=0x715,bit=1},
  [0x609]={byte=0x715,bit=3},
  [0x60A]={byte=0x713,bit=3},
  [0x60B]={byte=0x713,bit=4},
  [0x60C]={byte=0x713,bit=5},
  [0x60D]={byte=0x747,bit=6},
  [0x60E]={byte=0x747,bit=7},
  [0x60F]={byte=0x746,bit=3},
  [0x610]={byte=0x746,bit=5},
  [0x611]={byte=0x744,bit=0},
  [0x612]={byte=0x745,bit=4},
  [0x613]={byte=0x744,bit=1},
  [0x614]={byte=0x745,bit=7},
  [0x615]={byte=0x745,bit=6},
  [0x616]={byte=0x744,bit=4},
  [0x617]={byte=0x797,bit=4},
  [0x618]={byte=0x797,bit=6},
  [0x619]={byte=0x796,bit=4},
  [0x61A]={byte=0x796,bit=6},
  [0x61B]={byte=0x795,bit=3},
  [0x61C]={byte=0x7A6,bit=5},
  [0x61D]={byte=0x7A6,bit=7},
  [0x61E]={byte=0x7B7,bit=6},
  [0x61F]={byte=0x7B5,bit=3},
  [0x620]={byte=0x7B5,bit=5},
  [0x621]={byte=0x7B5,bit=7},
  [0x622]={byte=0x7B3,bit=0},
  [0x623]={byte=0x7B3,bit=2},
  [0x624]={byte=0x7B3,bit=6},
  [0x625]={byte=0x7B2,bit=2},
  [0x626]={byte=0x7B2,bit=3},
  [0x627]={byte=0x7D7,bit=6},
  [0x628]={byte=0x7D6,bit=4},
  [0x629]={byte=0x7D6,bit=6},
  [0x62A]={byte=0x7D6,bit=7},
  [0x62B]={byte=0x7D5,bit=2},
  [0x62C]={byte=0x7D5,bit=1},
  [0x62D]={byte=0x7D5,bit=4},
  [0x62E]={byte=0x7D5,bit=6},
  [0x62F]={byte=0x7D5,bit=5},
  [0x630]={byte=0x7D5,bit=7},
  [0x631]={byte=0x7D4,bit=2},
  [0x632]={byte=0x7D4,bit=1},
  [0x633]={byte=0x7D3,bit=3},
  [0x634]={byte=0x7E6,bit=6},
  [0x635]={byte=0x7E5,bit=6},
  [0x636]={byte=0x7E4,bit=1},
  [0x637]={byte=0x7E4,bit=4},
  [0x638]={byte=0x7E7,bit=1},
  [0x639]={byte=0x7E7,bit=5},
  [0x63A]={byte=0x7E5,bit=7},
  [0x63B]={byte=0x7E6,bit=3},
  [0x63C]={byte=0x7E5,bit=1},
  [0x63D]={byte=0x7E6,bit=2},
  [0x63E]={byte=0x7E5,bit=2},
  [0x63F]={byte=0x807,bit=4},
  [0x640]={byte=0x807,bit=6},
  [0x641]={byte=0x806,bit=0},
  [0x642]={byte=0x806,bit=2},
  [0x643]={byte=0x806,bit=4},
  [0x644]={byte=0x806,bit=5},
  [0x645]={byte=0x806,bit=7},
  [0x646]={byte=0x804,bit=2},
  [0x647]={byte=0x803,bit=0},
  [0x648]={byte=0x816,bit=4},
  [0x649]={byte=0x816,bit=2},
  [0x64A]={byte=0x816,bit=3},
  [0x64B]={byte=0x815,bit=0},
  [0x64C]={byte=0x816,bit=7},
  [0x64D]={byte=0x816,bit=5},
  [0x64E]={byte=0x816,bit=6},
  [0x64F]={byte=0x815,bit=2},
  [0x650]={byte=0x815,bit=3},
  [0x651]={byte=0x815,bit=4},
  [0x652]={byte=0x815,bit=1},
  [0x653]={byte=0x827,bit=0},
  [0x654]={byte=0x827,bit=1},
  [0x655]={byte=0x827,bit=2},
  [0x656]={byte=0x827,bit=3},
  [0x657]={byte=0x827,bit=7},
  [0x658]={byte=0x826,bit=0},
  [0x659]={byte=0x825,bit=0},
  [0x65A]={byte=0x825,bit=1},
  [0x65B]={byte=0x825,bit=2},
  [0x65C]={byte=0x825,bit=5},
  [0x65D]={byte=0x825,bit=6},
  [0x65E]={byte=0x825,bit=7},
  [0x65F]={byte=0x824,bit=0},
  [0x660]={byte=0x824,bit=1},
  [0x661]={byte=0x824,bit=3},
  [0x662]={byte=0x824,bit=4},
  [0x663]={byte=0x824,bit=5},
  [0x664]={byte=0x824,bit=6},
  [0x665]={byte=0x823,bit=0},
  [0x666]={byte=0x823,bit=1},
  [0x667]={byte=0x823,bit=2},
  [0x668]={byte=0x823,bit=5},
  [0x669]={byte=0x823,bit=6},
  [0x66A]={byte=0x823,bit=7},
  [0x66B]={byte=0x822,bit=0},
  [0x66C]={byte=0x822,bit=1},
  [0x66D]={byte=0x822,bit=3},
  [0x66E]={byte=0x822,bit=6},
  [0x66F]={byte=0x821,bit=3},
  [0x670]={byte=0x821,bit=5},
  [0x671]={byte=0x820,bit=0},
  [0x672]={byte=0x820,bit=1},
  [0x673]={byte=0x820,bit=3},
  [0x674]={byte=0x847,bit=4},
  [0x675]={byte=0x847,bit=6},
  [0x676]={byte=0x846,bit=0},
  [0x677]={byte=0x846,bit=1},
  [0x678]={byte=0x846,bit=2},
  [0x679]={byte=0x844,bit=1},
  [0x67A]={byte=0x845,bit=7},
  [0x67B]={byte=0x845,bit=5},
  [0x67C]={byte=0x845,bit=2},
  [0x67D]={byte=0x845,bit=1},
  [0x67E]={byte=0x844,bit=5},
  [0x67F]={byte=0x844,bit=7},
  [0x680]={byte=0x843,bit=1},
  [0x681]={byte=0x857,bit=1},
  [0x682]={byte=0x857,bit=2},
  [0x683]={byte=0x857,bit=4},
  [0x684]={byte=0x856,bit=0},
  [0x685]={byte=0x855,bit=1},
  [0x686]={byte=0x856,bit=7},
  [0x687]={byte=0x855,bit=7},
  [0x688]={byte=0x854,bit=0},
  [0x689]={byte=0x854,bit=3},
  [0x68A]={byte=0x854,bit=7},
  [0x68B]={byte=0x887,bit=1},
  [0x68C]={byte=0x897,bit=1},
  [0x68D]={byte=0x897,bit=4},
  [0x68E]={byte=0x896,bit=3},
  [0x68F]={byte=0x896,bit=5},
  [0x690]={byte=0x896,bit=7},
  [0x691]={byte=0x894,bit=3},
  [0x692]={byte=0x894,bit=4},
  [0x693]={byte=0x894,bit=5},
  [0x694]={byte=0x894,bit=6},
  [0x695]={byte=0x893,bit=1},
  [0x696]={byte=0x893,bit=3},
  [0x697]={byte=0x893,bit=4},
  [0x698]={byte=0x893,bit=5},
  [0x699]={byte=0x893,bit=7},
  [0x69A]={byte=0x8A6,bit=2},
  [0x69B]={byte=0x8A6,bit=1},
  [0x69C]={byte=0x8A5,bit=3},
  [0x69D]={byte=0x8A4,bit=1},
  [0x69E]={byte=0x8A5,bit=5},
  [0x69F]={byte=0x8A5,bit=6},
  [0x6A0]={byte=0x8A6,bit=7},
  [0x6A1]={byte=0x8A5,bit=0},
  [0x6A2]={byte=0x8A5,bit=1},
  [0x6A3]={byte=0x8B7,bit=2},
  [0x6A4]={byte=0x8B7,bit=3},
  [0x6A5]={byte=0x8B7,bit=4},
  [0x6A6]={byte=0x8B6,bit=4},
  [0x6A7]={byte=0x8B6,bit=3},
  [0x6A8]={byte=0x8B5,bit=5},
  [0x6A9]={byte=0x8B4,bit=2},
  [0x6AA]={byte=0x8B3,bit=3},
  [0x6AB]={byte=0x8C6,bit=0},
  [0x6AC]={byte=0x8C6,bit=1},
  [0x6AD]={byte=0x8C6,bit=3},
  [0x6AE]={byte=0x8C6,bit=5},
  [0x6AF]={byte=0x8C5,bit=4},
  [0x6B0]={byte=0x8C5,bit=6},
  [0x6B1]={byte=0x8C5,bit=7},
  [0x6B2]={byte=0x8C4,bit=1},
  [0x6B3]={byte=0x907,bit=0},
  [0x6B4]={byte=0x917,bit=2},
  [0x6B5]={byte=0x915,bit=2},
  [0x6B6]={byte=0x916,bit=4},
  [0x6B7]={byte=0x915,bit=5},
  [0x6B8]={byte=0x917,bit=4},
  [0x6B9]={byte=0x916,bit=3},
  [0x6BA]={byte=0x923,bit=2},
  [0x6BB]={byte=0x925,bit=4},
  [0x6BC]={byte=0x926,bit=1},
  [0x6BD]={byte=0x925,bit=1},
  [0x6BE]={byte=0x924,bit=7},
  [0x6BF]={byte=0x924,bit=0},
  [0x6C0]={byte=0x927,bit=6},
  [0x6C1]={byte=0x925,bit=7},
  [0x6C2]={byte=0x923,bit=4},
  [0x6C3]={byte=0x967,bit=1},
  [0x6C4]={byte=0x967,bit=3},
  [0x6C5]={byte=0x967,bit=5},
  [0x6C6]={byte=0x966,bit=1},
  [0x6C7]={byte=0x966,bit=2},
  [0x6C8]={byte=0x966,bit=7},
  [0x6C9]={byte=0x963,bit=5},
  [0x6CA]={byte=0x963,bit=7},
  [0x6CB]={byte=0x962,bit=3},
  [0x6CC]={byte=0x961,bit=1},
  [0x6CD]={byte=0x961,bit=6},
  [0x6CE]={byte=0x960,bit=1},
  [0x6CF]={byte=0x977,bit=7},
  [0x6D0]={byte=0x976,bit=2},
  [0x6D1]={byte=0x976,bit=3},
  [0x6D2]={byte=0x976,bit=5},
  [0x6D3]={byte=0x976,bit=4},
  [0x6D4]={byte=0x976,bit=1},
  [0x6D5]={byte=0x976,bit=0},
  [0x6D6]={byte=0x977,bit=4},
  [0x6D7]={byte=0x977,bit=3},
  [0x6D8]={byte=0x977,bit=1},
  [0x6D9]={byte=0x987,bit=0},
  [0x6DA]={byte=0x996,bit=1},
  [0x6DB]={byte=0x997,bit=0},
  [0x6DC]={byte=0x996,bit=2},
  [0x6DD]={byte=0x997,bit=1},
  [0x6DE]={byte=0x996,bit=3},
  [0x6DF]={byte=0x995,bit=0},
  [0x6E0]={byte=0x996,bit=7},
  [0x6E1]={byte=0x995,bit=1},
  [0x6E2]={byte=0x9A7,bit=5},
  [0x6E3]={byte=0x9A6,bit=6},
  [0x6E4]={byte=0x9A4,bit=0},
  [0x6E5]={byte=0x9A4,bit=4},
  [0x6E6]={byte=0x9A5,bit=4},
  [0x6E7]={byte=0x9A5,bit=0},
  [0x6E8]={byte=0x9A5,bit=1},
  [0x6E9]={byte=0x9A5,bit=2},
  [0x6EA]={byte=0x9A6,bit=2},
  [0x6EB]={byte=0x9A7,bit=7},
  [0x6EC]={byte=0x9A7,bit=3},
  [0x6ED]={byte=0x9B7,bit=1},
  [0x6EE]={byte=0x9B7,bit=3},
  [0x6EF]={byte=0x9B7,bit=5},
  [0x6F0]={byte=0x9B7,bit=7},
  [0x6F1]={byte=0x9B7,bit=6},
  [0x6F2]={byte=0x9B6,bit=0},
  [0x6F3]={byte=0x9B6,bit=6},
  [0x6F4]={byte=0x9B5,bit=1},
  [0x6F5]={byte=0x9B5,bit=0},
  [0x6F6]={byte=0x9B6,bit=7},
  [0x6F7]={byte=0x9B5,bit=4},
  [0x6F8]={byte=0x9B5,bit=5},
  [0x6F9]={byte=0x9B5,bit=6},
  [0x6FA]={byte=0x9B5,bit=7},
  [0x6FB]={byte=0x9B4,bit=2},
  [0x6FC]={byte=0x9B4,bit=4},
  [0x6FD]={byte=0x9B4,bit=6},
  [0x6FE]={byte=0x9B4,bit=7},
  [0x6FF]={byte=0x9B3,bit=1},
  [0x700]={byte=0x9B3,bit=3},
  [0x701]={byte=0x9D7,bit=4},
  [0x702]={byte=0x9D6,bit=0},
  [0x703]={byte=0x9D6,bit=4},
  [0x704]={byte=0x9D5,bit=0},
  [0x705]={byte=0x9E4,bit=3},
  [0x706]={byte=0x9E3,bit=3},
  [0x707]={byte=0x9E3,bit=0},
  [0x708]={byte=0x9E6,bit=0},
  [0x709]={byte=0x9E7,bit=7},
  [0x70A]={byte=0x9E6,bit=1},
  [0x70B]={byte=0x9E7,bit=0},
  [0x70C]={byte=0x9E4,bit=7},
  [0x70D]={byte=0x9E4,bit=4},
  [0x70E]={byte=0x9E4,bit=6},
  [0x70F]={byte=0x9E2,bit=0},
  [0x710]={byte=0x9E6,bit=2},
  [0x711]={byte=0x9E6,bit=4},
  [0x712]={byte=0x9E6,bit=3},
  [0x713]={byte=0x9E7,bit=1},
  [0x714]={byte=0x9E3,bit=2},
  [0x715]={byte=0x9E3,bit=1},
  [0x716]={byte=0x9E4,bit=5},
  [0x717]={byte=0x9E6,bit=5},
  [0x718]={byte=0x9E7,bit=5},
  [0x719]={byte=0x9E7,bit=6},
  [0x71A]={byte=0xA07,bit=0},
  [0x71B]={byte=0xA06,bit=1},
  [0x71C]={byte=0xA05,bit=6},
  [0x71D]={byte=0xA27,bit=7},
  [0x71E]={byte=0xA26,bit=1},
  [0x71F]={byte=0xA26,bit=3},
  [0x720]={byte=0xA26,bit=5},
  [0x721]={byte=0xA25,bit=7},
  [0x722]={byte=0xA24,bit=1},
  [0x723]={byte=0xA24,bit=2},
  [0x724]={byte=0xA24,bit=5},
  [0x725]={byte=0xA87,bit=4},
  [0x726]={byte=0xA97,bit=3},
  [0x727]={byte=0xA97,bit=0},
  [0x728]={byte=0xAA7,bit=0},
  [0x729]={byte=0xAA7,bit=1},
  [0x72A]={byte=0xAA7,bit=4},
  [0x72B]={byte=0xAA7,bit=5},
  [0x72C]={byte=0xAA7,bit=7},
  [0x72D]={byte=0xAA7,bit=3},
  [0x72E]={byte=0xAB5,bit=2},
  [0x72F]={byte=0xAB4,bit=6},
  [0x730]={byte=0xAB5,bit=0},
  [0x731]={byte=0xAB7,bit=2},
  [0x732]={byte=0xAB6,bit=7},
  [0x733]={byte=0xAB6,bit=3},
  [0x734]={byte=0xAB6,bit=2},
  [0x735]={byte=0xAB6,bit=4},
  [0x736]={byte=0xAB5,bit=1},
  [0x737]={byte=0xAB5,bit=4},
  [0x738]={byte=0xAB5,bit=7},
  [0x739]={byte=0xAB5,bit=5},
  [0x73A]={byte=0xAB7,bit=5},
  [0x73B]={byte=0xAB7,bit=3},
  [0x73C]={byte=0xAB4,bit=2},
  [0x73D]={byte=0xAB4,bit=4},
  [0x73E]={byte=0xAB3,bit=0},
  [0x73F]={byte=0xAC7,bit=4},
  [0x740]={byte=0xAC7,bit=2},
  [0x741]={byte=0xAC7,bit=5},
  [0x742]={byte=0xB07,bit=0},
  [0x743]={byte=0xB07,bit=1},
  [0x744]={byte=0xB07,bit=2},
  [0x745]={byte=0xB07,bit=3},
  [0x746]={byte=0xB07,bit=4},
  [0x747]={byte=0xB07,bit=5},
  [0x748]={byte=0xB07,bit=6},
  [0x749]={byte=0xB07,bit=7},
  [0x74A]={byte=0xB06,bit=0},
  [0x74B]={byte=0xB06,bit=1},
  [0x74C]={byte=0xB06,bit=2},
  [0x74D]={byte=0xB06,bit=3},
  [0x74E]={byte=0xB06,bit=4},
  [0x74F]={byte=0xB06,bit=5},
  [0x750]={byte=0xB27,bit=1},
  [0x751]={byte=0xB27,bit=3},
  [0x752]={byte=0xB27,bit=4},
  [0x753]={byte=0xB27,bit=6},
  [0x754]={byte=0xB47,bit=1},
  [0x755]={byte=0xB47,bit=3},
  [0x756]={byte=0xB47,bit=4},
  [0x757]={byte=0xB47,bit=6},
  [0x758]={byte=0xB67,bit=1},
  [0x759]={byte=0xB67,bit=2},
  [0x75A]={byte=0xB67,bit=3},
  [0x75B]={byte=0xB67,bit=5},
  [0x75C]={byte=0xB67,bit=7},
  [0x75D]={byte=0xB87,bit=1},
  [0x75E]={byte=0xB87,bit=3},
  [0x75F]={byte=0xB87,bit=4},
  [0x760]={byte=0xB87,bit=6},
  [0x761]={byte=0xC10,bit=7},
}
-- 865 consumable checks

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil    -- cached AP-signature result
local rom_levels       = nil    -- {[slot rom_index] = loc_id} read from ROM table
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[kirby_64] " .. tostring(msg)) end
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
-- read_u16/u32 endianness. Any failed byte -> nil (retry next poll).
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

-- bit `bit` (0..7) of a byte, LSB-first, with arithmetic only (Lua 5.1-safe).
local function byte_bit(byte, bit)
  if byte == nil then return false end
  return (math.floor(byte / (2 ^ bit)) % 2) >= 1
end

-- ── ROM identity (client.validate_rom): "Kirby64" @ 0x20 AND "K64" @ 0x1FFF200.
--    Verifying both means we only ever act on a patched K64 cartridge. ─────────
local function check_string(addr, str)
  for i = 1, #str do
    local b = read_u8(addr + i - 1, ROM)
    if b == nil then return nil end             -- not readable yet; retry
    if b ~= string.byte(str, i) then return false end
  end
  return true
end

local function rom_is_ap()
  if rom_ok ~= nil then return rom_ok end
  local a = check_string(ROM_NAME_ADDR, ROM_NAME)
  if a == nil then return false end             -- retry next poll
  if a == false then
    rom_ok = false
    log("non-K64 ROM (no 'Kirby64' cartridge name) — detection idle")
    return false
  end
  local b = check_string(ROM_K64_ADDR, ROM_K64)
  if b == nil then return false end
  if b == false then
    rom_ok = false
    log("unpatched/incompatible K64 ROM (no 'K64' AP signature) — detection idle")
    return false
  end
  rom_ok = true
  log("AP ROM verified ('Kirby64' + 'K64' signatures present)")
  return true
end

-- ── ROM stage table: 28 u16 big at K64_LEVEL_ADDRESS = the patched per-slot
--    stage layout. rom_index (per STAGE_SLOTS) -> the loc id at that physical
--    slot, honoring stage shuffle exactly like client.py self.levels. Cached. ──
local function load_rom_levels()
  if rom_levels ~= nil then return rom_levels ~= false end
  local t = {}
  for i = 0, 27 do
    local v = read_u16_be(K64_LEVEL_ADDRESS + i * 2, ROM)
    if v == nil then return false end           -- not readable yet; retry
    t[i] = v
  end
  rom_levels = t
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

-- Record a newly-set location once, gated to the slot's server set + the known set.
local function add_if(new, ap_id, set)
  if set and ap_id ~= nil and ALL_LOCS[ap_id] and not reported[ap_id] and wanted(ap_id) then
    reported[ap_id] = true
    new[#new + 1] = ap_id
  end
end

-- ── Detection gate ────────────────────────────────────────────────────────────
-- True iff the save signature is present AND game_state is past the menu states.
local function gameplay_ready()
  -- Signature: RDRAM[save..+16) == "-HALKEN--KIRBY4-"
  for i = 1, #SAVE_SIG do
    local b = read_u8(K64_SAVE_ADDRESS + i - 1, RDRAM)
    if b == nil then return false end
    if b ~= string.byte(SAVE_SIG, i) then return false end
  end
  local s = read_u32_be(K64_GAME_STATE, RDRAM)
  if s == nil then return false end
  return s > MENU_STATE_MAX           -- 0..10 are menu/demo states (client bails)
end

-- ── Flag walk (mirrors client.py game_watcher new_checks build) ───────────────
local function scan_into(new)
  if not load_rom_levels() then return end

  -- BOSSES: boss_crystals[i] != 0 -> loc i+0x0200 (i in 0..5).
  for i = 0, BOSS_COUNT - 1 do
    local b = read_u8(K64_BOSS_CRYSTALS + i, RDRAM)
    add_if(new, BOSS_BASE + i, b ~= nil and b ~= 0)
  end

  -- STAGE COMPLETES: stage_array[byte] == 0x02; loc id read from the ROM table.
  for _, e in ipairs(STAGE_SLOTS) do
    local status = read_u8(K64_STAGE_STATUSES + e.byte, RDRAM)
    local loc_id = rom_levels[e.rom_index]
    add_if(new, loc_id, status == 0x02)
  end

  -- CRYSTAL SHARDS: crystal_array word (LE u32) bit (slot*8 + i) set -> crystal.
  -- We test physical bytes: bit n -> byte (n//8) within the 4-byte word.
  for _, e in ipairs(CRYSTAL_SLOTS) do
    local word_base = K64_CRYSTAL_ARRAY + e.word * 4
    for i = 0, 2 do
      local bitn = e.bit + i
      local byte = read_u8(word_base + math.floor(bitn / 8), RDRAM)
      local set  = byte_bit(byte, bitn % 8)
      local loc  = (i == 0 and e.c0) or (i == 1 and e.c1) or e.c2
      add_if(new, loc, set)
    end
  end

  -- CONSUMABLES: one resolved byte+bit per loc into the consumable bitfield.
  for ap_id, e in pairs(CONSUMABLES) do
    if not reported[ap_id] and wanted(ap_id) then
      local byte = read_u8(K64_CONSUMABLES + e.byte, RDRAM)
      add_if(new, ap_id, byte_bit(byte, e.bit))
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
  local n = 0; for _ in pairs(ALL_LOCS) do n = n + 1 end
  log("ready: " .. n .. " location flags (N64 big-endian, K64 save+ROM gates)")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_ap() then return new end          -- unpatched/wrong cart -> idle
  if not gameplay_ready() then return new end      -- title/demo/booting -> idle
  scan_into(new)
  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_ap() then return false end
  if not gameplay_ready() then return false end
  -- Goal: boss_crystals[6] != 0 (Zero-Two / Dark Star defeated).
  local b = read_u8(K64_BOSS_CRYSTALS + 6, RDRAM)
  return b ~= nil and b ~= 0
end

-- Remote multiworld items: see the file header. items_handling = 0b111 (FULL
-- remote) means the reference client DELIVERS every item — including the slot's
-- own — by writing into the live RDRAM save (recv-index handshake at save+0x174,
-- then copy-ability/friend/crystal/life/health/star writes + the DeathLink kill
-- path). That guarded RDRAM write path is deferred until it can be confirmed
-- in-emulator; mis-writing it corrupts the live save. No-op (never a wrong write)
-- until then. Location detection + goal reporting are unaffected.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
