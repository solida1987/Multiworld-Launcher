-- ═══════════════════════════════════════════════════════════════════════════════
-- zillion.lua — game module for the Archipelago BizHawk connector.
--               Zillion (Sega Master System, US "Zillion (UE) [!]")
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the official
-- AP world worlds/zillion (client.py + id_maps.py + config.py, main branch) AND
-- the standalone randomizer library it delegates memory access to,
-- beauxq/zilliandomizer (src/zilliandomizer/zri/{memory,rai}.py +
-- low_resources/ram_info.py, master branch). Every RAM address, the in-game gate,
-- the canister-acquired bit scan, the rescue-from-character-status fold, the win
-- condition, and the ROM-to-RAM AP signature are replicated EXACTLY from that
-- source. Loads crash-free on any ROM; self-disables on a non-AP / non-Zillion
-- cartridge (no player-name signature at the ROM-to-RAM block).
--
-- IMPORTANT — WHY THE LOCATION TABLE IS NOT STATIC (unlike cvcotm/ff1)
-- ────────────────────────────────────────────────────────────────────
--   Zillion's location ids are NOT a fixed flag array. Which physical canister
--   (room + bit) corresponds to which AP location id is RANDOMIZED PER SEED.
--   The mapping lives in the `Connected` packet's slot_data as `loc_mem_to_id`:
--       key   = (item_room_index << 8) | bit_mask      (bit_mask = 1<<item_no)
--       value = the small Zillion location id           (AP id = value + base_id)
--   (worlds/zillion/id_maps.get_slot_info builds loc_memory = (room_code<<7)|mask
--    with room_code = 2*item_room_index, which equals (item_room_index<<8)|mask;
--    worlds/zillion/client.on_package asserts key>>8 in [0,74); and
--    zilliandomizer/zri/memory._process_change matches (item_room_index<<8)|can.
--    All three agree — verified by parsing the sources.)
--   The launcher forwards the seed's slot_data verbatim in ap_config.json, so this
--   module reads `ctx.config.slot_data.loc_mem_to_id` (+ `rescues` + `start_char`)
--   to know the per-seed map. With no slot_data (AP never connected) the module
--   degrades to read-only and reports nothing — exactly like the reference client,
--   which waits for slot_data before calling memory.set_generation_info().
--
-- MEMORY MODEL (BizHawk SMS domains — equivalent to the client's RetroArch reads)
-- ──────────────────────────────────────────────────────────────────────────────
--   The reference client reads SMS Z80 work RAM through RetroArch's
--   READ_CORE_RAM with `address - 0xC000` (rai.py SMS_RAM_OFFSET = 0xC000). On
--   real hardware Zillion has no battery SRAM: the whole 0xC000..0xDFFF window is
--   the single 8 KB work RAM, so the canister/door state at 0xD600/0xD700 lives
--   in that same RAM. In BizHawk's SMSHawk core this is:
--       "System Bus"  — full Z80 address space; ram_info's ABSOLUTE 0xCxxx/0xDxxx
--                       addresses read directly (this module's primary domain).
--       "Main RAM"    — the 8 KB work RAM indexed from 0; same byte at
--                       (address - 0xC000) (used as a fallback).
--   read_u8(addr) tries System Bus at the absolute address, then Main RAM at
--   addr-0xC000, so it works whichever domain layout the running core exposes.
--
-- KEY ADDRESSES (zilliandomizer/low_resources/ram_info.py)
--   current_scene_c11f        0xC11F   game scene selector (low 7 bits = scene)
--   current_hp_c143           0xC143   BCD hp
--   jj_status_c150            0xC150   character-present status (rescue fold)
--   champ_status_c160         0xC160
--   apple_status_c170         0xC170
--   cutscene_selector_c183    0xC183   win-cutscene discriminator
--   canister_state_d700       0xD700   2 bytes/room ×74: [opened, ACQUIRED]
--   rom_to_ram_data           0xD6A0   96 bytes: "<player>\0<seed6>" AP signature
--
-- WHAT THIS DOES (mirrors zilliandomizer/zri/memory.Memory.process_ram)
--   • poll(): when in-game, read the 74×2 canister block, take the ACQUIRED byte
--     of each room (0xD700+1, stride 2), OR in rescue bits derived from character
--     status, and for every set bit form key=(room<<8)|bit → loc_mem_to_id → AP id.
--     Gated to the slot's server location set too.
--   • is_goal_complete(): the source's in_win() — scene 0x8D/0x8E, or scene 0x86
--     with cutscene ∈ {1,8,9}. (client.py turns the WinEvent into the J-6 goal
--     check + CLIENT_GOAL; the launcher maps our GOAL message the same way.)
--   • receive_item(): NO-OP, and CORRECT for Zillion (documented below). The game
--     self-grants its own locally-found items; items_handling = 0b001 means the
--     server only sends OTHER players' items. Delivering those is the client's
--     guarded RAM write to the 0xC2E0 item-pickup queue (count-delta gated on the
--     game being in safe-to-write play state) — a write path that must be
--     confirmed in-emulator before shipping, so it is intentionally deferred
--     rather than risk a wrong RAM write. Checks + goal flow regardless.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "zillion"

local ADDRESSES_VERIFIED = true   -- addresses + math derived from the two sources

-- ── Memory domains (BizHawk SMSHawk) ──────────────────────────────────────────
local SYSTEM_BUS = "System Bus"   -- full Z80 space; absolute 0xCxxx/0xDxxx
local MAIN_RAM   = "Main RAM"     -- 8 KB work RAM; same byte at (addr - 0xC000)
local SMS_RAM_OFFSET = 0xC000     -- rai.py SMS_RAM_OFFSET

-- ── Addresses / constants (zilliandomizer ram_info.py + rai.py + memory.py) ───
local CURRENT_SCENE_C11F   = 0xC11F
local CUTSCENE_SEL_C183    = 0xC183
local JJ_STATUS_C150       = 0xC150
local CHAMP_STATUS_C160    = 0xC160
local APPLE_STATUS_C170    = 0xC170
local CANISTER_STATE_D700  = 0xD700
local ROM_TO_RAM_DATA      = 0xD6A0   -- 96-byte AP signature block
local ROM_TO_RAM_LEN       = 96
local CANISTER_ROOM_COUNT  = 74       -- rom_info.CANISTER_ROOM_COUNT
local BASE_ID              = 8675309  -- config.base_id

-- In-game scenes: (scene & 0x7f) in {3,4,6,7,8,9,0xb,0xc}  (memory.c11f_in_game_scenes)
local IN_GAME_SCENES = { [3]=true,[4]=true,[6]=true,[7]=true,[8]=true,[9]=true,[0x0b]=true,[0x0c]=true }

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil     -- cached AP/Zillion signature result
local mem              = {}
local log_fn           = nil

-- Per-seed location map, built from slot_data in init():
--   loc_mem_to_id[(room_index<<8)|mask] = ap_id   (ap_id = small_id + BASE_ID)
local loc_mem_to_id = nil
-- Rescue fold (slot_data.rescues): char-status address -> {room_index=, mask=}
local rescues = {}              -- list of {addr=, room_index=, mask=}

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[zillion] " .. tostring(msg)) end
end

-- ── Memory API (resolved at init; 2-arg domain form + current-domain fallback) ─
local function resolve_memory_api()
  if not memory then return false end
  mem.read_u8 = memory.read_u8 or memory.readbyte
  return mem.read_u8 ~= nil
end

-- One byte. Tries System Bus at the absolute address first (ram_info's addresses
-- are absolute Z80), then Main RAM at (addr - 0xC000), then the current domain.
local function read_u8(addr)
  if not mem.read_u8 then return nil end
  local ok, v = pcall(mem.read_u8, addr, SYSTEM_BUS)
  if ok and type(v) == "number" then return v end
  ok, v = pcall(mem.read_u8, addr - SMS_RAM_OFFSET, MAIN_RAM)
  if ok and type(v) == "number" then return v end
  ok, v = pcall(mem.read_u8, addr)            -- older API: current domain
  if ok and type(v) == "number" then return v end
  return nil
end

local function bit_and(a, b)
  local res, bitval = 0, 1
  while a > 0 and b > 0 do
    if a % 2 == 1 and b % 2 == 1 then res = res + bitval end
    a = math.floor(a / 2); b = math.floor(b / 2); bitval = bitval * 2
  end
  return res
end

-- bits(n): yield each set bit's VALUE (1,2,4,…) — mirrors memory.bits().
local function each_bit(n)
  local out = {}
  local v = 1
  while n > 0 do
    if n % 2 == 1 then out[#out + 1] = v end
    n = math.floor(n / 2); v = v * 2
  end
  return out
end

-- ── ROM/AP identity: the patch writes "<player>\0<seed6>" at 0xD6A0 ───────────
-- name_seed_from_ram(): a non-empty player name (first byte non-zero, printable)
-- means this is an AP-patched Zillion ROM that has booted far enough to copy its
-- rom-to-ram block. A blank/zero block = no AP game (or not booted yet).
local function rom_is_ap()
  if rom_ok == true then return true end   -- latch true; keep retrying while false
  local b0 = read_u8(ROM_TO_RAM_DATA)
  if b0 == nil then return false end                 -- not readable yet
  -- First byte must be a printable ASCII player-name char (not 0, not control).
  if b0 == 0 or b0 < 0x20 or b0 > 0x7e then
    return false
  end
  -- Require a null terminator within the 96-byte block (name\0seed) so we don't
  -- latch on uninitialised RAM that merely happens to start with a printable byte.
  for i = 1, ROM_TO_RAM_LEN - 1 do
    local b = read_u8(ROM_TO_RAM_DATA + i)
    if b == nil then return false end
    if b == 0 then
      rom_ok = true
      log("AP Zillion ROM verified (player-name signature at 0xD6A0)")
      return true
    end
  end
  return false
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

-- Build the per-seed loc_mem_to_id (AP-id valued) + rescue fold from slot_data.
-- slot_data shape (worlds/zillion fill_slot_data → get_slot_info):
--   start_char    : "JJ" | "Apple" | "Champ"
--   rescues       : { "0": {start_char, room_code, mask}, "1": {...} }
--   loc_mem_to_id : { "<mem>": "<small_loc_id>", ... }   (string keys/values)
local function build_generation_info(slot_data)
  if type(slot_data) ~= "table" then
    log("no slot_data — detection idle until connected (read-only)")
    return
  end

  -- loc_mem_to_id: string->string in JSON; key = (room<<8)|mask, value = small id.
  local raw = slot_data.loc_mem_to_id
  if type(raw) == "table" then
    loc_mem_to_id = {}
    local n = 0
    for k, v in pairs(raw) do
      local mem_key = tonumber(k)
      local small   = tonumber(v)
      if mem_key and small then
        loc_mem_to_id[mem_key] = small + BASE_ID
        n = n + 1
      end
    end
    log("loc_mem_to_id: " .. n .. " canister locations (per-seed)")
  else
    log("slot_data present but loc_mem_to_id missing — detection idle")
  end

  -- Rescues fold (memory.set_generation_info): a rescue canister doesn't appear
  -- in canister_state; instead its bit is set when the corresponding character is
  -- present (status byte > 0). The status address depends on start_char + which
  -- rescue (0 or 1). self.rescues[address] = (room_code // 2, mask).
  rescues = {}
  local start_char = slot_data.start_char
  local rdata = slot_data.rescues
  if type(rdata) == "table" then
    for rescue_key, info in pairs(rdata) do
      if type(info) == "table" then
        local room_code = tonumber(info.room_code)
        local mask      = tonumber(info.mask)
        local rid       = tonumber(rescue_key)   -- "0" or "1"
        if room_code and mask and rid ~= nil then
          local addr
          if start_char == "JJ" then
            addr = (rid == 0) and APPLE_STATUS_C170 or CHAMP_STATUS_C160
          elseif start_char == "Apple" then
            addr = (rid == 0) and JJ_STATUS_C150 or CHAMP_STATUS_C160
          else  -- Champ
            addr = (rid == 0) and APPLE_STATUS_C170 or JJ_STATUS_C150
          end
          rescues[#rescues + 1] = {
            addr = addr,
            room_index = math.floor(room_code / 2),  -- room_code // 2
            mask = mask,
          }
        end
      end
    end
    log("rescue fold: " .. #rescues .. " rescue(s) (start_char=" .. tostring(start_char) .. ")")
  end
end

-- ── Detection gate ────────────────────────────────────────────────────────────
local function in_game()
  local s = read_u8(CURRENT_SCENE_C11F)
  if s == nil then return false end
  return IN_GAME_SCENES[bit_and(s, 0x7f)] == true
end

-- ── Win condition (rai.in_win) ────────────────────────────────────────────────
local function in_win()
  local scene = read_u8(CURRENT_SCENE_C11F)
  if scene == nil then return false end
  if scene == 0x8d or scene == 0x8e then return true end
  if scene == 0x86 then
    local sel = read_u8(CUTSCENE_SEL_C183)
    if sel == 1 or sel == 8 or sel == 9 then return true end
  end
  return false
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
  build_generation_info(cfg.slot_data)
  log("ready (System Bus / Main RAM SMS domains)")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if loc_mem_to_id == nil then return new end       -- no per-seed map yet
  if not rom_is_ap() then return new end
  if not in_game() then return new end

  -- Read the canister state block: 2 bytes per room, take the ACQUIRED byte
  -- (the 2nd of each pair) — memory.py slice d700+1 : d700+148 : 2.
  -- Build a room_index -> acquired_byte array.
  local acquired = {}
  for room = 0, CANISTER_ROOM_COUNT - 1 do
    local b = read_u8(CANISTER_STATE_D700 + room * 2 + 1)
    acquired[room] = b or 0
  end

  -- Rescues are not in canister state; OR their bit in from character status.
  -- (memory.process_ram: if ram[address] > 0 then canisters[room] |= mask.)
  for _, r in ipairs(rescues) do
    local st = read_u8(r.addr)
    if st ~= nil and st > 0 then
      acquired[r.room_index] = (acquired[r.room_index] or 0)
      -- OR the mask in (bit_and-free OR via add when bit absent)
      if bit_and(acquired[r.room_index], r.mask) == 0 then
        acquired[r.room_index] = acquired[r.room_index] + r.mask
      end
    end
  end

  -- For each room, for each set bit `can`, key=(room<<8)|can → ap_id.
  for room = 0, CANISTER_ROOM_COUNT - 1 do
    local byte = acquired[room]
    if byte and byte > 0 then
      for _, can in ipairs(each_bit(byte)) do
        local key   = room * 256 + can       -- (room << 8) | can
        local ap_id = loc_mem_to_id[key]
        if ap_id and not reported[ap_id] and wanted(ap_id) then
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
  return in_win()
end

-- Remote items: see the file header. items_handling = 0b001 — the patched game
-- grants its own locally-found items, so solo play and check reporting work
-- fully; delivering OTHER players' items is the client's guarded RAM write to the
-- 0xC2E0 item-pickup queue (count-delta gated on the game being safe-to-write).
-- That guarded-write path is the one piece deferred until it can be confirmed
-- in-emulator; a wrong RAM write could corrupt live game state, so this is a
-- no-op (never a wrong write) rather than shipped unverified.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
