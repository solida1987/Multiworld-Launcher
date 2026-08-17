-- ═══════════════════════════════════════════════════════════════════════════════
-- gstla.lua — game module for the Archipelago BizHawk connector.
--             Golden Sun: The Lost Age (GBA)
--
-- COMMUNITY WORLD. Source (BizHawk client): cjmang/Archipelago, branch `gstla`
--   worlds/gstla/BizClient.py  (class GSTLAClient, game = "Golden Sun The Lost Age")
--   worlds/gstla/Rom.py        (GSTLADeltaPatch, base ROM MD5, .apgstla → .gba)
--   worlds/gstla/gen/InternalLocationData.py  (the 414-location source table)
--   world_version 0.2.3 (archipelago.json), minimum_ap_version 0.6.7.
--   apworld download: github.com/cjmang/Archipelago/releases (gstla.apworld)
--
-- STATUS: location DETECTION for the 319 standard flag-checked locations
-- (Item / Psyenergy / Hidden / Trade / Character) and the default GOAL
-- (Doom Dragon defeated) are REAL and SOURCE-DERIVED. The flag→ap_id table
-- below was GENERATED with Python directly from gen/InternalLocationData.py
-- (every InternalLocationData's `flag` and `ap_id` field), NOT hand-copied, so
-- it is exact. The module loads crash-free on any ROM and self-disables on a
-- non-GSTLA cartridge.
--
-- MEMORY MODEL (BizHawk GBA domains) — verbatim from BizClient.py
-- ───────────────────────────────────────────────────────────────
--   The GSTLA AP client (a BizHawkClient) reads GBA work RAM ("EWRAM") and the
--   cartridge ("ROM"). Location "checked" state is a BIT-ADDRESSED flag array
--   based at EWRAM 0x40 (FLAG_START). A location with flag value F is checked
--   when:
--       EWRAM[0x40 + (F >> 3)] & (1 << (F & 7))            (LSB-first bit order)
--   This is exactly BizClient._check_common_flags: for each region it walks the
--   bytes and computes `flag = i*8 + bit + initial_flag`, then maps that flag to
--   the location's ap_id via flag_map. (The regions TREASURE_8..TREASURE_F,
--   SUMMONS, INITIAL_INVENTORY, ENEMY_FLAGS, STORY_FLAGS all live inside the one
--   contiguous 0x40-based array, flags 0x0..0xFFF — so a single read of
--   EWRAM[0x40 .. 0x240] covers every standard location flag.)
--
--   "In game" gate (BizClient._is_in_game): EWRAM u16 at 0x428 must be > 1.
--   Goal (default — BizClient GoalManager, "Doom Dragon"): the Doom Dragon event
--   flag 0x778 (= 1912) set in the same array. (The source's own commented
--   DOOM_DRAGON = FLAG_START + (0x778>>3) confirms the address.)
--
-- WHAT THIS DOES (mirrors BizClient.game_watcher)
--   • poll(): read the flag array → AP location ids for the 319 standard
--     locations, gated to the slot's server location set and to the in-game
--     gate. Reports each id once.
--   • is_goal_complete(): the default "Doom Dragon defeated" flag (0x778).
--     Alternate / multi-flag / djinn-count / summon-count goals are slot_data
--     options (GoalManager.init_reqs_from_slotdata); only the default is wired
--     here (see the GATED block) since the others need the slot_data goal spec
--     and, for counts, server-stored state the in-emulator scan cannot see.
--   • receive_item(): NO-OP (documented). Although the world declares
--     items_handling = 0b111 (full remote), the PATCHED GAME itself consumes the
--     remote item stream through a save-data handshake: BizClient._receive_items
--     writes the next item id into the EWRAM "AP item slot" (0xA96) only when it
--     reads 0 there, and advances the "items received" counter (0xA72) — a
--     guarded, index-tracked protocol with co-op filtering. Replicating that
--     write path correctly (and safely, without corrupting the save counter)
--     needs in-emulator verification, so it is intentionally deferred rather
--     than shipped unverified. A SOLO seed still reports every standard check
--     and completes the Doom Dragon goal.
--
-- GATED (faithful code present, switched OFF until confirmed in-emulator):
--   • DJINN detection (72 locations). Djinn are shuffled, so a check fires on
--     POSSESSION and is remapped through a ROM table at 0xFA0000 (see
--     BizClient._load_djinn + _check_djinn_flags). The remap is reproduced below
--     but left disabled (DJINN_DETECTION_ENABLED) because the source's own
--     ram/rom naming is ambiguous and the mapping is seed-dependent — exactly
--     the kind of thing to verify live before trusting.
--   • EVENT / alternate-goal flags (23). Boss-defeat events double as optional
--     goals; the default goal needs none of them, and the full set is
--     slot_data-driven.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "gstla"

local ADDRESSES_VERIFIED = true   -- standard-location table generated from source

-- ── Feature gates (faithful but unverified paths kept OFF) ────────────────────
local DJINN_DETECTION_ENABLED = false  -- ROM-remap djinn possession (see header)

-- ── Memory domains ────────────────────────────────────────────────────────────
local EWRAM = "EWRAM"
local ROM   = "ROM"

-- ── Addresses / constants (worlds/gstla/BizClient.py + Rom.py) ────────────────
local FLAG_START          = 0x40       -- EWRAM: base of the bit-addressed flag array
local FLAG_SCAN_BYTES      = 0x200      -- EWRAM[0x40..0x240] covers flags 0x0..0xFFF
local IN_GAME_ADDR        = 0x428      -- EWRAM: u16 "in game / save opened" (>1 == in game)
local DOOM_DRAGON_FLAG    = 0x778       -- goal: Doom Dragon defeated (default goal)
-- ROM identity (validate_rom): the GBA header game-name field.
local ROM_ID_ADDR         = 0xA0
local ROM_ID              = "GOLDEN_SUN_BAGFE01"  -- 18 bytes at ROM 0xA0
-- AP signature: the AP patch writes the base64 slot name at ROM 0xFFF000 (64 B).
-- A vanilla cartridge has the GOLDEN_SUN_BAGFE01 header too, so the slot-name
-- region is what distinguishes an AP-patched ROM from a plain dump.
local SLOT_NAME_ADDR      = 0xFFF000
local SLOT_NAME_LEN       = 64
-- Djinn (GATED): possession bits + ROM remap table.
local DJINN_FLAGS_INITIAL = 0x30        -- DJINN_FLAGS.initial_flag
local DJINN_FLAGS_ADDR    = FLAG_START + math.floor(0x30 / 8)  -- EWRAM 0x46
local DJINN_FLAGS_LEN     = 0x0A
local DJINN_ROM_TABLE_ADDR = 0xFA0000
local DJINN_ROM_TABLE_LEN  = 2 * 18 * 4              -- 144 bytes

-- ── Standard location table (GENERATED from gen/InternalLocationData.py) ──────
-- ap_id -> flag (bit index in the EWRAM array). 319 entries: every non-Event,
-- non-Djinn location. Checked when EWRAM[0x40 + flag//8] & (1 << (flag%8)).
-- NOTE: a few flags are shared by >1 ap_id (e.g. 16384204 & 16384206 both use
-- flag 6 — the character-join psynergy) — all ap_ids are listed, so a set flag
-- fires every location that maps to it, matching the source's set-valued
-- flag_map.
local LOC = {
  [991776]=3841,[991784]=3842,[991812]=3934,[991824]=3858,[991832]=3843,[991840]=3844,
  [991848]=3845,[991860]=3846,[991872]=3696,[991884]=3847,[991892]=3848,[991904]=3849,
  [991916]=3850,[991928]=3851,[991940]=3852,[991948]=3853,[991968]=3854,[991976]=3855,
  [991984]=3856,[991996]=3857,[992008]=3859,[992016]=3906,[992028]=3860,[992036]=3861,
  [992060]=3862,[992068]=19,[992080]=3863,[992092]=3864,[992104]=3865,[992128]=3977,
  [992140]=2190,[992148]=3866,[992172]=3867,[992180]=3868,[992192]=3978,[992204]=3979,
  [992212]=24,[992224]=3980,[992232]=3981,[992244]=3869,[992252]=3870,[992260]=3871,
  [992268]=3872,[992280]=3873,[992304]=3875,[992312]=3876,[992324]=3877,[992332]=3878,
  [992340]=3879,[992348]=3982,[992360]=3983,[992368]=3984,[992376]=3985,[992388]=3880,
  [992396]=3881,[992404]=3882,[992416]=3883,[992424]=3884,[992432]=3986,[992444]=3885,
  [992456]=3886,[992464]=3887,[992476]=3888,[992484]=3889,[992496]=3890,[992504]=3891,
  [992512]=3892,[992520]=3697,[992532]=3893,[992540]=3894,[992552]=3895,[992564]=3896,
  [992584]=3897,[992596]=3898,[992608]=3899,[992620]=3900,[992632]=18,[992644]=3901,
  [992656]=3904,[992664]=3905,[992672]=3907,[992684]=3908,[992692]=3909,[992700]=3910,
  [992712]=3911,[992720]=3912,[992732]=3913,[992740]=3914,[992752]=3915,[992764]=3916,
  [992800]=3918,[992824]=3919,[992832]=3920,[992844]=3921,[992852]=3698,[992864]=3922,
  [992876]=3923,[992888]=3987,[992900]=3924,[992908]=3925,[992916]=3926,[992928]=3928,
  [992936]=3927,[992968]=3929,[992980]=2247,[992992]=3930,[993016]=3931,[993028]=3932,
  [993040]=3936,[993048]=3937,[993056]=3938,[993064]=3989,[993076]=3990,[993084]=3991,
  [993096]=3992,[993108]=3939,[993116]=3940,[993128]=3941,[993140]=3942,[993152]=3993,
  [993164]=3994,[993172]=3995,[993180]=3996,[993192]=3997,[993204]=3998,[993216]=3944,
  [993224]=3945,[993236]=3946,[993244]=3947,[993256]=3948,[993268]=3699,[993280]=3949,
  [993288]=3950,[993300]=3951,[993312]=3952,[993332]=3953,[993344]=3954,[993360]=3999,
  [993368]=4000,[993376]=4001,[993384]=4002,[993392]=4003,[993404]=4004,[993412]=4005,
  [993424]=20,[993432]=4006,[993444]=3955,[993456]=3956,[993464]=3957,[993476]=3700,
  [993484]=3958,[993492]=3649,[993504]=4008,[993512]=4009,[993524]=4010,[993532]=4011,
  [993540]=4012,[993548]=4013,[993560]=4014,[993572]=4015,[993584]=4016,[993592]=4017,
  [993600]=4018,[993608]=4019,[993616]=4020,[993624]=4021,[993632]=3959,[993640]=3960,
  [993652]=3961,[993664]=3962,[993672]=3963,[993680]=3964,[993692]=3965,[993700]=3966,
  [993708]=3967,[993720]=3903,[993732]=3968,[993744]=3969,[993752]=3970,[993760]=3971,
  [993768]=3972,[993788]=3973,[993796]=3974,[993808]=3975,[993816]=3976,[993828]=2377,
  [993864]=4025,[993872]=4026,[993880]=4027,[993888]=4028,[993896]=4029,[993916]=3943,
  [993924]=4031,[993936]=4032,[993948]=4033,[993960]=4034,[993984]=4035,[993996]=4036,
  [994016]=4037,[994024]=4038,[994032]=4039,[994044]=4040,[994052]=4041,[994064]=4042,
  [994072]=4043,[994084]=4044,[994096]=3935,[994108]=4045,[994116]=4046,[994124]=4047,
  [994132]=4048,[994140]=4049,[994148]=4050,[994160]=4051,[994168]=4052,[994176]=4053,
  [994184]=4054,[994192]=4055,[994200]=4056,[994208]=4057,[994216]=4058,[994224]=4059,
  [994232]=4060,[994240]=4061,[994248]=4062,[994260]=4063,[994268]=3701,[994280]=4064,
  [994288]=4065,[994300]=25,[994312]=4066,[994336]=4067,[994348]=4068,[994356]=4069,
  [994368]=4070,[994376]=4071,[994388]=3702,[994396]=4072,[994404]=4073,[994412]=4074,
  [994424]=4075,[994436]=4076,[994448]=4077,[994460]=4078,[994468]=4079,[994480]=4080,
  [994492]=4081,[994504]=4082,[994524]=4084,[994536]=3703,[994548]=4085,[994556]=4086,
  [994564]=4087,[994572]=4088,[994584]=4089,[994592]=4090,[994604]=4091,[994612]=4092,
  [994624]=4093,[994636]=4094,[994644]=3704,[994656]=4095,[994668]=3584,[994680]=3585,
  [994692]=3586,[994704]=3587,[994716]=3588,[994728]=3589,[994736]=3590,[994832]=3674,
  [994844]=16,[994856]=17,[994868]=21,[994880]=23,[994892]=26,[994904]=27,
  [994916]=28,[994928]=3675,[994936]=3676,[994944]=3677,[994952]=3678,[994960]=3679,
  [16384160]=2122,[16384162]=2168,[16384164]=2188,[16384166]=2328,[16384168]=2381,[16384170]=2618,
  [16384172]=2303,[16384174]=2424,[16384176]=2722,[16384178]=2724,[16384180]=2723,[16384182]=2721,
  [16384186]=2592,[16384188]=2553,[16384190]=2260,[16384192]=2478,[16384194]=2490,[16384196]=2554,
  [16384198]=2315,[16384200]=2373,[16384202]=4,[16384204]=6,[16384206]=6,[16384208]=4,
  [16384210]=3,[16384212]=2,[16384214]=1,[16384216]=0,[16384218]=7,[16384220]=7,
  [16384384]=3328,[16384386]=3329,[16384388]=3330,[16384390]=3331,[16384392]=3333,[16384394]=3334,
  [16384396]=3335,
}

-- ── Djinn table (GATED) — ap_id -> location flag (the shuffled/ROM flag). ─────
-- 72 entries from gen/InternalLocationData.py djinn_locations. A djinn at
-- location flag F is "checked" when the player POSSESSES the djinn that the ROM
-- remap maps to F (see resolve_djinn). Kept for when DJINN_DETECTION_ENABLED is
-- flipped on after live verification.
local DJINN = {
  [16384000]=48,[16384002]=49,[16384004]=50,[16384006]=51,[16384008]=52,[16384010]=53,
  [16384012]=54,[16384014]=55,[16384016]=56,[16384018]=57,[16384020]=58,[16384022]=59,
  [16384024]=60,[16384026]=61,[16384028]=62,[16384030]=63,[16384032]=64,[16384034]=65,
  [16384036]=68,[16384038]=69,[16384040]=70,[16384042]=71,[16384044]=72,[16384046]=73,
  [16384048]=74,[16384050]=75,[16384052]=76,[16384054]=77,[16384056]=78,[16384058]=79,
  [16384060]=80,[16384062]=81,[16384064]=82,[16384066]=83,[16384068]=84,[16384070]=85,
  [16384072]=88,[16384074]=89,[16384076]=90,[16384078]=91,[16384080]=92,[16384082]=93,
  [16384084]=94,[16384086]=95,[16384088]=96,[16384090]=97,[16384092]=98,[16384094]=99,
  [16384096]=100,[16384098]=101,[16384100]=102,[16384102]=103,[16384104]=104,[16384106]=105,
  [16384108]=108,[16384110]=109,[16384112]=110,[16384114]=111,[16384116]=112,[16384118]=113,
  [16384120]=114,[16384122]=115,[16384124]=116,[16384126]=117,[16384128]=118,[16384130]=119,
  [16384132]=120,[16384134]=121,[16384136]=122,[16384138]=123,[16384140]=124,[16384142]=125,
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil     -- cached AP-signature/identity result
local djinn_rom_to_ram = nil    -- GATED: ROM remap table (built once)
local flag_to_djinn    = {}     -- location-flag -> djinn ap_id (built from DJINN)
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[gstla] " .. tostring(msg)) end
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

local function read_u16_le(addr, domain)
  local lo = read_u8(addr, domain)
  local hi = read_u8(addr + 1, domain)
  if lo == nil or hi == nil then return nil end
  return lo + hi * 256
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
-- powers of two for 16-bit possession words (bit 0..15), used by the gated
-- djinn path; precomputed so the hot loop avoids 2^bit float ops.
local POW2_16 = {}
for _b = 0, 15 do POW2_16[_b] = 2 ^ _b end

-- ── ROM identity / AP signature ───────────────────────────────────────────────
-- 1) the GBA header game-name must be GOLDEN_SUN_BAGFE01 (validate_rom), and
-- 2) the AP patch's base64 slot name at ROM 0xFFF000 must be present (a plain
--    cartridge leaves that region as the vanilla ROM contents, not a slot name).
local function rom_is_ap()
  if rom_ok ~= nil then return rom_ok end
  -- (1) game-name header
  for i = 1, #ROM_ID do
    local b = read_u8(ROM_ID_ADDR + i - 1, ROM)
    if b == nil then return false end             -- not readable yet; retry next poll
    if b ~= string.byte(ROM_ID, i) then
      rom_ok = false
      log("non-GSTLA ROM (header != GOLDEN_SUN_BAGFE01) — detection idle")
      return false
    end
  end
  -- (2) AP slot-name signature: require some non-zero, printable bytes in the
  -- 64-byte slot-name region (base64 ASCII). A vanilla dump's bytes there are
  -- not a base64 slot string; this keeps detection idle on an unpatched ROM.
  local printable = 0
  for i = 0, SLOT_NAME_LEN - 1 do
    local b = read_u8(SLOT_NAME_ADDR + i, ROM)
    if b == nil then return false end
    if b ~= 0 then
      -- base64 alphabet is within 0x2B..0x7A; reject obviously non-text bytes
      if b >= 0x2B and b <= 0x7A then printable = printable + 1
      else printable = -1000 end                  -- non-text byte: not a slot name
    end
  end
  if printable <= 0 then
    rom_ok = false
    log("GSTLA header present but no AP slot-name signature at 0xFFF000 — " ..
        "looks like an unpatched ROM; detection idle")
    return false
  end
  rom_ok = true
  log("AP ROM verified (GOLDEN_SUN_BAGFE01 + slot-name signature)")
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

-- ── Flag array (read once per poll) ───────────────────────────────────────────
local flags = {}     -- byte index (0-based from FLAG_START) -> value, per poll

local function refresh_flags()
  for i = 0, FLAG_SCAN_BYTES - 1 do
    flags[i] = read_u8(FLAG_START + i, EWRAM)
  end
end

-- bit test for an absolute flag value F (matches _check_common_flags):
--   EWRAM[0x40 + F//8] & (1 << (F%8))
local function flag_bit(F)
  local byte = flags[math.floor(F / 8)]
  if byte == nil then return false end
  return bit_and(byte, POW2[F % 8]) ~= 0
end

-- ── Detection gate (BizClient._is_in_game: EWRAM u16 0x428 > 1) ───────────────
local function in_game()
  local v = read_u16_le(IN_GAME_ADDR, EWRAM)
  return v ~= nil and v > 1
end

-- ── Djinn ROM remap (GATED — faithful reproduction of _load_djinn) ────────────
-- Builds the table the source calls djinn_ram_to_rom (key = ROM/location flag,
-- value = RAM possession flag), read once from ROM 0xFA0000.
local function load_djinn_table()
  if djinn_rom_to_ram ~= nil then return djinn_rom_to_ram ~= false end
  local tbl = {}
  for index = 0, (18 * 4) - 1 do
    local djinn_flag = math.floor(index / 18) * 20 + (index % 18) + DJINN_FLAGS_INITIAL
    local section = read_u16_le(DJINN_ROM_TABLE_ADDR + index * 2, ROM)
    if section == nil then djinn_rom_to_ram = false; return false end
    local rom_flag = math.floor(section / 256) * 0x14 + (section % 256) + DJINN_FLAGS_INITIAL
    -- source: djinn_ram_to_rom[rom_flag] = djinn_flag
    tbl[rom_flag] = djinn_flag
  end
  djinn_rom_to_ram = tbl
  -- Precompute location-flag -> djinn ap_id once.
  for ap_id, loc_flag in pairs(DJINN) do flag_to_djinn[loc_flag] = ap_id end
  log("djinn ROM remap table loaded (" .. (18 * 4) .. " entries)")
  return true
end

-- Resolve the set of checked djinn ap_ids from the possession bits, exactly as
-- BizClient._check_djinn_flags (iterate the DJINN_FLAGS region as possession
-- bits, original_flag = pos + 0x30, look up the remap, the matching location
-- flag is the shuffled flag).
local function collect_djinn(out)
  if not load_djinn_table() then return end
  -- The remap maps ROM/location flag -> RAM flag; the source looks the *RAM*
  -- possession flag up against it. We invert to RAM -> location-flag so a
  -- possessed RAM flag yields its location flag.
  -- (Kept behind the gate precisely because this inversion direction is the
  -- subtle, seed-dependent bit to confirm in-emulator.)
  local ram_to_loc = {}
  for loc_flag, ram_flag in pairs(djinn_rom_to_ram) do ram_to_loc[ram_flag] = loc_flag end
  for i = 0, DJINN_FLAGS_LEN - 1, 2 do
    local part = read_u16_le(DJINN_FLAGS_ADDR + i, EWRAM)
    if part ~= nil then
      for bit = 0, 15 do
        -- test bit `bit` of the 16-bit possession word (LSB-first)
        if math.floor(part / POW2_16[bit]) % 2 == 1 then
          local original_flag = i * 8 + bit + DJINN_FLAGS_INITIAL
          local loc_flag = ram_to_loc[original_flag]
          if loc_flag ~= nil then
            local ap_id = flag_to_djinn[loc_flag]
            if ap_id ~= nil and not reported[ap_id] and wanted(ap_id) then
              reported[ap_id] = true
              out[#out + 1] = ap_id
            end
          end
        end
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
  local n = 0; for _ in pairs(LOC) do n = n + 1 end
  log("ready: " .. n .. " standard location flags" ..
      (DJINN_DETECTION_ENABLED and " + djinn" or " (djinn gated off)"))
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_ap() then return new end
  refresh_flags()
  if not in_game() then return new end
  for ap_id, F in pairs(LOC) do
    if not reported[ap_id] and wanted(ap_id) and flag_bit(F) then
      reported[ap_id] = true
      new[#new + 1] = ap_id
    end
  end
  if DJINN_DETECTION_ENABLED then collect_djinn(new) end
  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_ap() then return false end
  refresh_flags()
  -- Default goal: Doom Dragon defeated. (Alternate/multi/count goals are
  -- slot_data-driven and gated; see header.)
  return flag_bit(DOOM_DRAGON_FLAG)
end

-- Remote multiworld items: see the file header. The world declares
-- items_handling = 0b111, but the PATCHED GAME consumes the item stream itself
-- through an EWRAM save-data handshake (AP item slot 0xA96 / received counter
-- 0xA72) with co-op filtering; replicating those guarded writes safely needs
-- in-emulator verification, so this is intentionally a no-op until then. Never
-- a wrong write. A solo seed reports every standard check and the Doom Dragon
-- goal regardless.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
