-- ═══════════════════════════════════════════════════════════════════════════════
-- landstalker.lua — game module for the Archipelago BizHawk connector.
--                   Landstalker: The Treasures of King Nole
--                   (Sega Genesis / Mega Drive, 1992)
--
-- Source world: worlds/landstalker (ArchipelagoMW/Archipelago, main branch,
--   world_version 1.8.7, author Dinopony). game string:
--   "Landstalker - The Treasures of King Nole".
--   https://github.com/ArchipelagoMW/Archipelago/tree/main/worlds/landstalker
--
-- STATUS — READ THIS BEFORE ASSUMING CHECKS FLOW
-- ──────────────────────────────────────────────────────────────────────────
--   The 292-entry LOCATION ID TABLE below is REAL and SOURCE-DERIVED: it was
--   GENERATED with Python from worlds/landstalker (Locations.py
--   build_location_name_to_id_table + Constants.py base ids + data/item_source.py
--   ITEM_SOURCES_JSON), not hand-copied, so the AP ids are exact:
--     chest   -> BASE_LOCATION_ID        (4000) + chestId
--     ground  -> BASE_GROUND_LOCATION_ID (4256) + groundItemId
--     shop    -> BASE_SHOP_LOCATION_ID   (4286) + shopItemId
--     reward  -> BASE_REWARD_LOCATION_ID (4336) + rewardId
--     "Gola"  -> BASE_REWARD_LOCATION_ID + 10   (4346, the win-condition location)
--   (203 chest + 29 ground + 49 shop + 10 reward + 1 "Gola" = 292 ids, range
--   4000..4346, verified collision-free.)
--
--   LIVE RAM DETECTION IS NOT WIRED, and this is a DELIBERATE, HONEST choice —
--   not an oversight. Unlike the other emulated worlds shipped here (Sonic 1,
--   FF1, CotM…), the Landstalker apworld carries NO in-repo game client: there
--   is no Client.py, no rom.py, no RAM address map, no AP ROM signature and no
--   goal flag offset to replicate. Landstalker is played through an EXTERNAL,
--   closed-source connector — `randstalker_archipelago.exe` (the "Landstalker
--   Archipelago Client", Windows only) — which connects to the AP server ITSELF
--   and reads/writes the Genesis Plus GX core's memory through its own private
--   interface. None of those addresses are published in the AP repository, so
--   there is nothing authoritative to derive a RAM map from. Inventing addresses
--   to read would be guesswork that reported phantom checks (or none), which is
--   worse than reporting nothing. So this module:
--     • loads crash-free and exposes the exact 292-id location table (so the
--       launcher/UI knows the real shape of this game's check pool), and
--     • GATES all live reporting behind ADDRESSES_VERIFIED = false — poll()
--       returns {} and is_goal_complete() returns false until a VERIFIED
--       Genesis RAM map (flag base + bit model + goal flag) is added.
--   The accompanying LandstalkerPlugin.cs sets ChecksImplemented => false for
--   exactly the same reason, and points players at the external client, so a
--   player is never left wondering why no checks are flowing.
--
--   MEMORY MODEL (when a verified map is added later)
--     Landstalker is a 68000 game — the Genesis CPU is BIG-ENDIAN, so any future
--     multi-byte read must assemble hi<<8 | lo (read_u16_be), not little-endian.
--     The BizHawk Genesis work-RAM domain is "68K RAM" and the cartridge is
--     "MD CART" (older cores expose it as "ROM"). read_u8(addr, domain) below
--     already takes the domain argument and falls back to the current domain on
--     older APIs, so the read path is ready; only the address constants + the
--     flag-bit decode are intentionally absent.
--
-- GOAL — Landstalker's AP completion_condition (Rules.py) is
--   state.has("King Nole's Treasure"), a locked event item placed on the "End"
--   location. All three goal options (beat_gola / reach_kazalt / beat_dark_nole,
--   Options.py LandstalkerGoal) funnel into that single "End" event through the
--   region rules, so "the End event fired" is the one true victory signal. The
--   in-RAM byte that the external client uses to know the End event triggered is
--   not published, so is_goal_complete() is GATED off (returns false) here.
--
-- ITEMS — the Landstalker multiworld delivers items REMOTELY (the docs confirm
--   "every item can be sent to you by other players"); the external client
--   applies received items by writing the game's memory itself. There is no
--   published write model to replicate, and re-implementing item delivery in Lua
--   without an in-emulator-verified RAM map would risk corrupting the save, so
--   receive_item is a documented no-op.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "landstalker"

-- No verified Genesis RAM map exists in the AP repo for this game (the memory
-- client is the external randstalker_archipelago.exe), so live reporting is
-- gated off. Flip to true ONLY alongside real, source-verified flag addresses.
local ADDRESSES_VERIFIED = false

-- ── Memory domains (BizHawk Genesis; ready for a future verified map) ─────────
-- 68000 work RAM is "68K RAM"; the cartridge is "MD CART" on modern Genesis
-- Plus GX builds (older cores: "ROM"). The Genesis CPU is BIG-ENDIAN.
local WORKRAM = "68K RAM"
local CART    = "MD CART"

-- ── Constants (worlds/landstalker/Constants.py + Locations.py) ────────────────
local BASE_LOCATION_ID        = 4000
local BASE_GROUND_LOCATION_ID = BASE_LOCATION_ID + 256        -- 4256
local BASE_SHOP_LOCATION_ID   = BASE_GROUND_LOCATION_ID + 30  -- 4286
local BASE_REWARD_LOCATION_ID = BASE_SHOP_LOCATION_ID + 50    -- 4336
local GOLA_LOCATION_ID        = BASE_REWARD_LOCATION_ID + 10  -- 4346 (win condition)

-- ── Location id table (GENERATED from worlds/landstalker source) ──────────────
-- The exact set of AP location ids this world can send (292 entries). Stored as
-- a set so a future verified RAM map can report ids directly; today it is the
-- authoritative shape of the check pool even while live reporting is gated.
local LOC_LIST = {
  4000,4001,4002,4003,4004,4005,4006,4007,4008,4009,4010,4011,
  4012,4013,4014,4015,4016,4017,4018,4019,4020,4021,4022,4024,
  4025,4028,4029,4031,4033,4034,4035,4036,4038,4041,4042,4043,
  4044,4045,4046,4047,4048,4049,4050,4052,4053,4054,4055,4056,
  4057,4058,4059,4060,4066,4067,4068,4069,4070,4071,4072,4073,
  4074,4075,4076,4077,4078,4079,4080,4081,4082,4083,4084,4085,
  4086,4087,4088,4089,4090,4091,4092,4093,4094,4095,4096,4097,
  4098,4099,4100,4101,4102,4103,4104,4105,4106,4107,4108,4109,
  4110,4111,4112,4113,4114,4115,4116,4117,4118,4119,4120,4121,
  4122,4123,4124,4125,4126,4127,4128,4129,4130,4131,4132,4133,
  4134,4135,4136,4137,4138,4139,4140,4141,4142,4143,4144,4145,
  4146,4147,4148,4149,4150,4151,4152,4153,4154,4155,4156,4157,
  4158,4159,4160,4161,4162,4163,4164,4165,4166,4167,4168,4169,
  4170,4171,4172,4173,4174,4175,4176,4177,4178,4179,4180,4181,
  4182,4183,4184,4185,4186,4191,4192,4193,4194,4196,4197,4198,
  4199,4200,4201,4202,4203,4204,4205,4206,4207,4208,4209,4210,
  4211,4212,4213,4214,4215,4216,4217,4218,4219,4220,4221,4257,
  4258,4259,4260,4261,4262,4263,4264,4265,4266,4267,4268,4269,
  4270,4271,4272,4273,4274,4275,4276,4277,4278,4279,4280,4281,
  4282,4283,4284,4285,4287,4288,4289,4290,4291,4292,4293,4294,
  4295,4296,4297,4298,4299,4300,4301,4302,4303,4304,4305,4306,
  4307,4308,4309,4310,4311,4312,4313,4314,4315,4316,4317,4318,
  4319,4320,4321,4322,4323,4324,4325,4326,4327,4328,4329,4330,
  4331,4332,4333,4334,4335,4336,4337,4338,4339,4340,4341,4342,
  4343,4344,4345,4346,
}

-- ap_id -> true. Built once at load from LOC_LIST.
local LOC = {}
for _, id in ipairs(LOC_LIST) do LOC[id] = true end

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local have_slot_data   = false  -- a server slot_data object was provided
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[landstalker] " .. tostring(msg)) end
end

-- ── Memory API (resolved at init; 2-arg domain form + current-domain fallback) ─
-- Ready for a future verified RAM map. The Genesis CPU is big-endian, so a
-- multi-byte read is provided as read_u16_be (hi<<8 | lo) rather than trusting a
-- core's read_u16 endianness.
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

-- Big-endian 16-bit read (Genesis / 68000): high byte at the lower address.
local function read_u16_be(addr, domain)
  local hi = read_u8(addr, domain)
  local lo = read_u8(addr + 1, domain)
  if hi == nil or lo == nil then return nil end
  return hi * 256 + lo
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

-- wanted(): kept for the future live-reporting path — gates a candidate ap_id to
-- this world's id table AND (when provided) the slot's server location set.
local function wanted(ap_id)
  if not LOC[ap_id] then return false end
  if server_locations == nil then return true end
  return server_locations[ap_id] == true
end

-- ── Module contract ───────────────────────────────────────────────────────────
function M.init(ctx)
  if ctx and type(ctx.log) == "function" then log_fn = ctx.log end
  if not resolve_memory_api() then
    log("BizHawk memory API unavailable — module idle")
    return
  end
  local cfg = (ctx and ctx.config) or {}
  load_locations(cfg.locations)
  have_slot_data = (type(cfg.slot_data) == "table")
  local n = 0; for _ in pairs(LOC) do n = n + 1 end
  log("ready: " .. n .. " known location ids (Genesis, big-endian). " ..
      "Live RAM detection is GATED — Landstalker is played through the external " ..
      "randstalker_archipelago.exe client (no in-repo RAM map to verify).")
end

-- poll(): GATED. No verified Genesis RAM map exists in the AP repo for this game
-- (the external client owns memory access), so there is no source of truth to
-- read location flags from. Returns no checks rather than inventing addresses
-- and reporting phantoms. The 292-id table above is exact and ready for a future
-- verified map; wanted() + reported{} are wired for that path.
function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  -- (Future verified path would: read the flag region from "68K RAM"/"MD CART"
  --  big-endian, then for each ap_id in LOC test its flag bit and, gated by
  --  wanted(ap_id) and not reported[ap_id], append it to `new`.)
  return new
end

-- is_goal_complete(): GATED. The AP completion_condition is
-- state.has("King Nole's Treasure") — the locked "End" event (all three goal
-- options route through it). The in-RAM byte the external client uses to detect
-- that event is not published, so this returns false until a verified goal flag
-- is added.
function M.is_goal_complete()
  if not ADDRESSES_VERIFIED then return false end
  return false
end

-- Remote items: the Landstalker multiworld delivers items from the server and
-- the external randstalker_archipelago.exe client applies them by writing the
-- game's own memory. No published write model exists to replicate, and doing it
-- blind would risk corrupting the save, so this is a documented no-op.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
