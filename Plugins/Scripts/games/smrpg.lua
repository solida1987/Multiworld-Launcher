-- ═══════════════════════════════════════════════════════════════════════════════
-- smrpg.lua — game module for the Archipelago BizHawk connector.
--            Super Mario RPG: Legend of the Seven Stars (SNES)
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the apworld
-- TheRealSolidusSnake/SMRPG_apworld (Client.py + Rom.py + Locations.py, main
-- branch — https://github.com/TheRealSolidusSnake/SMRPG_apworld). The 246-entry
-- location table was GENERATED directly from Rom.location_data ⋈
-- Locations.location_table (not hand-copied), so the addresses, bitmasks, the
-- set-when-checked polarity and the AP ids are exact. Mock-verified through
-- MoonSharp against synthetic WRAM/CARTRAM/CARTROM. Loads crash-free on any ROM;
-- self-disables on a non-AP cartridge.
--
-- MEMORY MODEL (SNI → BizHawk)
-- ────────────────────────────
--   The SMRPG client is an SNIClient (worlds.AutoSNIClient.SNIClient). SNI
--   addresses translate to BizHawk SNES memory domains:
--     SNI WRAM_START(0xF50000)+off → domain "WRAM",    offset off (= $7E0000+off)
--     SNI SRAM_START(0xE00000)+off → domain "CARTRAM", offset off (fallback "SRAM")
--     SNI ROM_START (0x000000)+off → domain "CARTROM", offset off (fallbacks
--                                     "ROM" / "System Bus") — raw ROM file offset
--   SMRPG is a SA-1 LoROM cartridge: every location/event/boss "checked" flag
--   lives in the battery-backed save region (CARTRAM, 0x2D3A..0x3D98), the
--   current-music + in-battle gate bytes live in WRAM, and the "MRPG" ROM-name
--   signature the client validates is in the LoROM header at CARTROM 0x7FC0.
--
-- WHAT THIS DOES (mirrors SMRPGClient.game_watcher in Client.py)
--   • poll(): the client's location_check scan — for every location, read its
--     CARTRAM flag byte, AND with its bit, and report the AP id when the byte
--     matches its set_when_checked polarity (bit SET for events/bosses, bit
--     CLEAR for treasure chests — chest flags clear as a chest is opened). Gated
--     to the slot's server location set AND to check_if_items_sendable() — the
--     same gate the client uses, which is CRITICAL: 158 chest flags read 0 on a
--     fresh/unloaded save and would all false-fire without it.
--   • is_goal_complete(): the client's check_victory — current music (WRAM
--     0x1D04) is one of the victory tunes {0x40,0x46,0x47,0x48,0x49}, or the
--     "Boss - Smithy Spot" flag is set (the goal the client reports as
--     CLIENT_GOAL). Star Road Restored.
--   • receive_item(): items_handling = 0b101 (this game DOES receive remote
--     multiworld items + starting inventory, but NOT its own locally-found
--     items). The client's received_items_check applies each item by CATEGORY —
--     free-slot inventory scans, the Alto→Tenor→Soprano card progression,
--     coin/flower/frog-coin read-modify-write accumulation, and party recovery —
--     all racing the game's own writes and corrupting the save on a wrong write.
--     That path needs in-emulator verification before it is wired, so it is
--     intentionally GATED (buffer-only, never writes) rather than shipped
--     unverified. The stream is still tracked so the wiring is a one-line flip
--     once confirmed. See the receive_item note at the bottom of this file.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "smrpg"

local ADDRESSES_VERIFIED = true   -- table generated from SMRPG_apworld source

-- ── Memory domains ────────────────────────────────────────────────────────────
local WRAM    = "WRAM"
local CARTRAM = "CARTRAM"     -- SNES battery SRAM in BizHawk; fallback "SRAM"
local CARTROM = "CARTROM"     -- SNES cartridge ROM in BizHawk; fallbacks below

-- ── Addresses / constants (Client.py + Rom.py module top) ─────────────────────
-- Expressed as BizHawk-domain offsets (already net of WRAM_START / SRAM_START /
-- ROM_START). SNI addresses from Rom.py are in the comments.
local ROMNAME_OFF       = 0x7FC0     -- CARTROM: LoROM header; first 4 = "MRPG" (SNI 0x007FC0)
local ROMNAME_SIG       = "MRPG"

-- check_if_items_sendable() gate bytes.
local SENDABLE1_OFF     = 0x3062     -- CARTRAM: Map/Star menu available?     (SNI 0xE03062)
local MUSIC_OFF         = 0x1D04     -- WRAM:    current music                (SNI 0xF51D04)
local SENDABLE3_OFF     = 0x3076     -- CARTRAM: is a star active?           (SNI 0xE03076)
local INBATTLE_OFF      = 0x3021     -- WRAM:    in a battle?                (SNI 0xF53021)

-- Smithy goal flag (Boss - Smithy Spot): CARTRAM byte & 0x04, set when checked.
local SMITHY_OFF        = 0x304A     -- CARTRAM (SNI 0xE0304A)
local SMITHY_BIT        = 0x04

-- Silence, battle musics, victory musics and star music are all forbidden for a
-- sendable state (Rom.nonsendable_music_values). Set form for O(1) lookup.
local NONSENDABLE_MUSIC = {
  [0x00]=true,[0x03]=true,[0x06]=true,[0x08]=true,[0x09]=true,[0x0C]=true,
  [0x19]=true,[0x1D]=true,[0x23]=true,[0x36]=true,[0x37]=true,[0x3B]=true,
  [0x3C]=true,[0x44]=true,[0x45]=true,
}
-- Music values that mean a run-ending victory is playing (Rom.victory_music_values).
local VICTORY_MUSIC = { [0x40]=true,[0x46]=true,[0x47]=true,[0x48]=true,[0x49]=true }

-- ── Location table (GENERATED from SMRPG_apworld Rom.location_data ⋈ Locations) ─
-- ap_id -> { cartram_off, bitmask, set_when_checked }. 246 entries.
--   ap_id            = Locations.location_table[name].id + 850000  (base_id)
--   flag byte        = CARTRAM[cartram_off]   (Rom.location_data[name].address - 0xE00000)
--   checked when     (byte & bitmask) ~= 0  if set_when_checked == 1   (events/bosses)
--                    (byte & bitmask) == 0  if set_when_checked == 0   (treasure chests)
local LOC = {
  [850000]={0x3052,0x40,1},[850001]={0x304D,0x8,1},[850002]={0x3082,0x1,1},[850003]={0x3057,0x20,1},[850004]={0x3055,0x4,1},
  [850005]={0x3083,0x40,1},[850006]={0x3056,0x20,1},[850007]={0x3056,0x8,1},[850008]={0x3053,0x10,1},[850009]={0x3048,0x40,1},
  [850010]={0x304C,0x40,1},[850011]={0x2DAC,0x80,1},[850012]={0x3058,0x40,1},[850013]={0x3057,0x80,1},[850014]={0x3058,0x80,1},
  [850015]={0x3086,0x1,1},[850016]={0x307C,0x20,1},[850017]={0x308A,0x4,1},[850018]={0x308A,0x20,1},[850019]={0x3093,0x10,1},
  [850020]={0x3064,0x40,1},[850021]={0x308C,0x8,1},[850022]={0x3092,0x80,1},[850023]={0x305F,0x20,1},[850024]={0x304A,0x4,1},
  [850025]={0x307E,0x1,1},[850026]={0x307D,0x80,1},[850027]={0x3093,0x40,1},[850028]={0x3054,0x4,1},[850029]={0x3093,0x80,1},
  [850030]={0x308F,0x80,1},[850031]={0x3096,0x1,1},[850032]={0x3059,0x10,1},[850033]={0x3091,0x8,1},[850034]={0x2E8E,0x20,0},
  [850035]={0x308F,0x40,1},[850036]={0x304A,0x4,1},[850037]={0x2DC0,0x10,0},[850038]={0x304D,0x8,1},[850039]={0x3083,0x10,1},
  [850040]={0x3051,0x10,1},[850041]={0x3054,0x20,1},[850042]={0x3054,0x40,1},[850043]={0x2D68,0x4,0},[850044]={0x2D3A,0x1,0},
  [850045]={0x3056,0x20,1},[850046]={0x3054,0x1,1},[850047]={0x2DCB,0x80,0},[850048]={0x3099,0x40,1},[850049]={0x2E19,0x8,0},
  [850050]={0x2E1F,0x40,0},[850051]={0x3D8F,0x20,0},[850052]={0x2DF6,0x2,1},[850053]={0x305F,0x40,1},[850054]={0x305F,0x20,1},
  [850055]={0x2E6F,0x20,0},[850056]={0x3D89,0x40,0},[850057]={0x3D89,0x80,0},[850058]={0x3D8A,0x1,0},[850059]={0x3D8A,0x2,0},
  [850060]={0x3D80,0x8,0},[850061]={0x3D80,0x10,0},[850062]={0x3D80,0x20,0},[850063]={0x3D8A,0x8,0},[850064]={0x3D82,0x1,0},
  [850065]={0x3D82,0x2,0},[850066]={0x3D82,0x4,0},[850067]={0x3D8A,0x4,0},[850068]={0x3D81,0x40,0},[850069]={0x3D81,0x20,0},
  [850070]={0x3D82,0x8,0},[850071]={0x3D83,0x2,0},[850072]={0x3D83,0x4,0},[850073]={0x3D93,0x80,0},[850074]={0x3D94,0x1,0},
  [850075]={0x3D8A,0x10,0},[850076]={0x3D8A,0x20,0},[850077]={0x3D8D,0x8,0},[850078]={0x3D8D,0x10,0},[850079]={0x3D8D,0x20,0},
  [850080]={0x3D8A,0x40,0},[850081]={0x3D85,0x80,0},[850082]={0x3D85,0x40,0},[850083]={0x3D86,0x1,0},[850084]={0x3D86,0x2,0},
  [850085]={0x3D86,0x4,0},[850086]={0x3D80,0x40,0},[850087]={0x3D8F,0x2,0},[850088]={0x3D8F,0x1,0},[850089]={0x3D8F,0x4,0},
  [850090]={0x3D8F,0x8,0},[850091]={0x3D84,0x20,0},[850092]={0x3D84,0x40,0},[850093]={0x3D93,0x4,0},[850094]={0x3D93,0x8,0},
  [850095]={0x3D93,0x10,0},[850096]={0x3D89,0x1,0},[850097]={0x3D81,0x1,0},[850098]={0x3D89,0x2,0},[850099]={0x3D80,0x80,0},
  [850100]={0x3D81,0x2,0},[850101]={0x3D89,0x4,0},[850102]={0x3D89,0x10,0},[850103]={0x3D89,0x20,0},[850104]={0x3D80,0x1,0},
  [850105]={0x3D86,0x80,0},[850106]={0x3D86,0x8,0},[850107]={0x3D86,0x10,0},[850108]={0x3D86,0x20,0},[850109]={0x3D86,0x40,0},
  [850110]={0x3D87,0x80,0},[850111]={0x3D88,0x1,0},[850112]={0x3D88,0x2,0},[850113]={0x3D88,0x4,0},[850114]={0x3D88,0x8,0},
  [850115]={0x3D88,0x10,0},[850116]={0x3D88,0x20,0},[850117]={0x3D88,0x80,0},[850118]={0x3D80,0x4,0},[850119]={0x3D87,0x1,0},
  [850120]={0x3D87,0x2,0},[850121]={0x3D87,0x4,0},[850122]={0x3D87,0x8,0},[850123]={0x3D8E,0x80,0},[850124]={0x3D8E,0x40,0},
  [850125]={0x3D93,0x2,0},[850126]={0x3D8E,0x8,0},[850127]={0x3D8E,0x2,0},[850128]={0x3D8E,0x4,0},[850129]={0x3D94,0x2,0},
  [850130]={0x3D94,0x4,0},[850131]={0x3D94,0x8,0},[850132]={0x3D94,0x20,0},[850133]={0x3D94,0x10,0},[850134]={0x3D8E,0x20,0},
  [850135]={0x3D8D,0x80,0},[850136]={0x3D8E,0x1,0},[850137]={0x3D91,0x2,0},[850138]={0x3D91,0x8,0},[850139]={0x3D8D,0x40,0},
  [850140]={0x3D92,0x20,0},[850141]={0x3D92,0x2,0},[850142]={0x3D92,0x4,0},[850143]={0x3D92,0x8,0},[850144]={0x3D92,0x10,0},
  [850145]={0x3D91,0x4,0},[850146]={0x3D84,0x80,0},[850147]={0x3D85,0x2,0},[850148]={0x3D93,0x20,0},[850149]={0x3D93,0x40,0},
  [850150]={0x3D85,0x1,0},[850151]={0x3D85,0x10,0},[850152]={0x3D85,0x20,0},[850153]={0x3D91,0x40,0},[850154]={0x3D92,0x80,0},
  [850155]={0x3D8F,0x20,0},[850156]={0x3D92,0x80,0},[850157]={0x3D93,0x1,0},[850158]={0x3D91,0x40,0},[850159]={0x3D91,0x80,0},
  [850160]={0x3D92,0x1,0},[850161]={0x3D96,0x4,0},[850162]={0x3D96,0x1,0},[850163]={0x3D96,0x2,0},[850164]={0x3D8F,0x80,0},
  [850165]={0x3D90,0x1,0},[850166]={0x3D90,0x2,0},[850167]={0x3D90,0x4,0},[850168]={0x3D97,0x80,0},[850169]={0x3D98,0x1,0},
  [850170]={0x3D98,0x2,0},[850171]={0x3D97,0x40,0},[850172]={0x3D8F,0x40,0},[850173]={0x3D97,0x8,0},[850174]={0x3D97,0x2,0},
  [850175]={0x3D97,0x20,0},[850176]={0x3D97,0x10,0},[850177]={0x3D97,0x4,0},[850178]={0x3D96,0x8,0},[850179]={0x3D96,0x40,0},
  [850180]={0x3D96,0x10,0},[850181]={0x3D96,0x80,0},[850182]={0x3D96,0x20,0},[850183]={0x3D97,0x1,0},[850184]={0x3D87,0x10,0},
  [850185]={0x3D95,0x80,0},[850186]={0x3D80,0x1,0},[850187]={0x3D80,0x1,0},[850188]={0x3D80,0x1,0},[850189]={0x3D80,0x1,0},
  [850190]={0x3D8D,0x2,0},[850191]={0x3D8D,0x4,0},[850192]={0x3D95,0x4,0},[850193]={0x3D95,0x20,0},[850194]={0x3D95,0x8,0},
  [850195]={0x3D98,0x4,0},[850196]={0x3D98,0x8,0},[850197]={0x3D95,0x10,0},[850198]={0x3D95,0x40,0},[850199]={0x3052,0x10,1},
  [850200]={0x3052,0x20,1},[850201]={0x3052,0x40,1},[850202]={0x3083,0x4,1},[850203]={0x3083,0x4,1},[850204]={0x3089,0x40,1},
  [850205]={0x3084,0x10,1},[850206]={0x3082,0x80,1},[850207]={0x3083,0x1,1},[850208]={0x3082,0x20,1},[850209]={0x304D,0x20,1},
  [850210]={0x3057,0x20,1},[850211]={0x3043,0x2,1},[850212]={0x3084,0x1,1},[850213]={0x3085,0x80,1},[850214]={0x3088,0x2,1},
  [850215]={0x3088,0x1,1},[850216]={0x3088,0x4,1},[850217]={0x3057,0x1,1},[850218]={0x3056,0x80,1},[850219]={0x3056,0x40,1},
  [850220]={0x2DC6,0x1,0},[850221]={0x2DCB,0x80,0},[850222]={0x3053,0x20,1},[850223]={0x3086,0x40,1},[850224]={0x307D,0x4,1},
  [850225]={0x307D,0x10,1},[850226]={0x3057,0x80,1},[850227]={0x2E67,0x20,0},[850228]={0x2E67,0x40,0},[850229]={0x2E67,0x80,0},
  [850230]={0x308A,0x20,1},[850231]={0x3093,0x10,1},[850232]={0x3092,0x1,1},[850233]={0x3092,0x4,1},[850234]={0x3089,0x80,1},
  [850235]={0x3094,0x80,1},[850236]={0x3093,0x2,1},[850237]={0x3098,0x40,1},[850238]={0x305F,0x80,1},[850239]={0x3084,0x8,1},
  [850240]={0x309F,0x8,1},[850241]={0x3059,0x20,1},[850242]={0x3099,0x10,1},[850243]={0x3099,0x20,1},[850244]={0x3051,0x4,1},
  [850245]={0x3051,0x8,0},
}

-- ── Internal state ────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local items_received   = {}     -- ordered remote item stream (tracked; not applied)
local slot_number      = 0
local rom_ok           = nil    -- cached "MRPG" signature result (nil until checked)
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[smrpg] " .. tostring(msg)) end
end

-- ── Memory API (resolved at init; 2-arg domain form + current-domain fallback) ─
local function resolve_memory_api()
  if not memory then return false end
  mem.read_u8  = memory.read_u8     or memory.readbyte
  mem.read_u16 = memory.read_u16_le or memory.readword
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

local function read_wram_u8(off)  return rd(mem.read_u8, off, WRAM) end

-- CARTRAM (battery SRAM) reads with a "SRAM" domain-name fallback.
local function read_cartram_u8(off)
  local ok, v = pcall(mem.read_u8, off, CARTRAM)
  if ok and type(v) == "number" then return v end
  ok, v = pcall(mem.read_u8, off, "SRAM")
  if ok and type(v) == "number" then return v end
  return nil
end

-- CARTROM reads with "ROM" / "System Bus" domain-name fallbacks (cores vary; the
-- raw-file-offset CARTROM/ROM form is what every SNES BizHawk core exposes).
local function read_cartrom_u8(off)
  local ok, v = pcall(mem.read_u8, off, CARTROM)
  if ok and type(v) == "number" then return v end
  ok, v = pcall(mem.read_u8, off, "ROM")
  if ok and type(v) == "number" then return v end
  ok, v = pcall(mem.read_u8, off, "System Bus")
  if ok and type(v) == "number" then return v end
  return nil
end

local function bit_and(a, b)
  -- 8/16-bit safe AND without the bit library (cores vary).
  local res, bitval = 0, 1
  while a > 0 and b > 0 do
    if a % 2 == 1 and b % 2 == 1 then res = res + bitval end
    a = math.floor(a / 2); b = math.floor(b / 2); bitval = bitval * 2
  end
  return res
end

-- ── ROM identity: the AP patch writes "MRPG" into the LoROM header at 0x7FC0 ───
local function rom_is_ap()
  if rom_ok ~= nil then return rom_ok end
  for i = 1, #ROMNAME_SIG do
    local b = read_cartrom_u8(ROMNAME_OFF + i - 1)
    if b == nil then return false end            -- not readable yet; retry next poll
    if b ~= string.byte(ROMNAME_SIG, i) then
      rom_ok = false
      log("non-AP ROM (no 'MRPG' signature) — detection idle, no writes")
      return false
    end
  end
  rom_ok = true
  log("AP ROM verified ('MRPG' signature present)")
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
  if server_locations == nil then return true end   -- no set provided → report all
  return server_locations[ap_id] == true
end

-- ── Detection gate (exact mirror of SMRPGClient.check_if_items_sendable) ───────
-- Without this, the 158 treasure-chest flags (set_when_checked == 0, i.e. fire
-- when their bit is 0) would ALL false-fire on a fresh/unloaded all-zero save.
-- Items are "sendable" only when the overworld is fully interactive:
--   sendable_1 (map/star menu byte) ~= 0,
--   current music NOT one of the silence/battle/victory/star tunes,
--   sendable_3 (a star active?)     == 0,
--   in-battle byte                  == 0.
local function items_sendable()
  local s1 = read_cartram_u8(SENDABLE1_OFF)
  local mu = read_wram_u8(MUSIC_OFF)
  local s3 = read_cartram_u8(SENDABLE3_OFF)
  local b4 = read_wram_u8(INBATTLE_OFF)
  if s1 == nil or mu == nil or s3 == nil or b4 == nil then return false end
  if s1 == 0 then return false end
  if NONSENDABLE_MUSIC[mu] then return false end
  if s3 ~= 0 then return false end
  if b4 ~= 0 then return false end
  return true
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
  slot_number = tonumber(cfg.slot_number) or 0
  load_locations(cfg.locations)
  local n = 0; for _ in pairs(LOC) do n = n + 1 end
  log(("ready: slot #%d, %d location flags"):format(slot_number, n))
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_ap() then return new end
  if not items_sendable() then return new end
  for ap_id, info in pairs(LOC) do
    if not reported[ap_id] and wanted(ap_id) then
      local b = read_cartram_u8(info[1])
      if b ~= nil then
        local masked = bit_and(b, info[2])
        local hit = (info[3] == 1 and masked ~= 0) or (info[3] == 0 and masked == 0)
        if hit then
          reported[ap_id] = true
          new[#new + 1] = ap_id
        end
      end
    end
  end
  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_ap() then return false end
  -- check_victory: a victory tune is playing, or the Smithy flag is already set.
  local mu = read_wram_u8(MUSIC_OFF)
  if mu ~= nil and VICTORY_MUSIC[mu] then return true end
  local sm = read_cartram_u8(SMITHY_OFF)
  return sm ~= nil and bit_and(sm, SMITHY_BIT) ~= 0
end

-- Remote multiworld items (items_handling = 0b101): see the file header. The
-- stream is recorded so the delivery wiring is a one-line flip once verified, but
-- it is intentionally NOT applied here. The client's received_items_check writes
-- by category — free-slot inventory scans, the Alto→Tenor→Soprano card
-- progression, coin/flower/frog-coin read-modify-write accumulation and party
-- recovery — every one of which races the game's own RAM writes and corrupts the
-- save on a mis-applied write. That is the one piece that needs in-emulator
-- confirmation before shipping, so this is a buffer-only no-op (never a wrong
-- write) until then.
function M.receive_item(item_id, meta)
  if type(meta) == "table" then
    items_received[#items_received + 1] = {
      item     = tonumber(item_id),
      index    = tonumber(meta.index),
      player   = tonumber(meta.player),
      flags    = tonumber(meta.flags),
      location = tonumber(meta.location),
    }
  else
    items_received[#items_received + 1] = { item = tonumber(item_id) }
  end
  -- intentionally no RAM writes (documented above)
end

return M
