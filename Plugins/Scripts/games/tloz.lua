-- ═══════════════════════════════════════════════════════════════════════════════
-- tloz.lua — game module for the Archipelago BizHawk connector.
--            The Legend of Zelda (NES, NA 1.0)
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the official
-- AP world worlds/tloz (game string "The Legend of Zelda", base_id 7000). The
-- 155-entry location table was GENERATED directly from worlds/tloz/Locations.py
-- (location_table = major_locations + all_level_locations + shop_locations, +base_id)
-- by PARSING the source, not hand-copied, and cross-checked against the per-location
-- status-block offsets. The status-block / bit math, the "LOZ" PRG-ROM signature,
-- the game_mode gate and the game_mode==19 victory are replicated EXACTLY from the
-- world's BizHawk client (the v2 BizHawkClient in worlds/tloz/Client.py, PR #4832
-- "The Legend of Zelda: v2 Improvements") and cross-validated against the merged
-- main-branch legacy connector (data/lua/connector_tloz.lua), which uses the SAME
-- RAM blocks (0x067F/0x06FF/0x077F), the SAME 0x10 "visited/cleared" bit, the SAME
-- shop-slot bytes (0x0628..0x062A) and the SAME game_mode==5 gate. Mock-verified
-- through MoonSharp against synthetic NES memory. Loads crash-free on any ROM;
-- self-disables on a non-TLoZ cartridge (no "LOZ" PRG-ROM signature).
--
-- SOURCE NOTE: the world on AP main (world_version 1.0.0) shipped with a LEGACY
-- custom client + data/lua/connector_tloz.lua. The clean fixed-RAM-block BizHawk
-- client (Client.py) is the open v2 PR #4832 (branch The-Legend-of-Zelda-AP) — not
-- yet merged at time of writing. Both implementations agree on every address and
-- bit this module uses; the BizHawkClient form is the one modelled here because it
-- is the direct equivalent of the ff1/mm2 BizHawk clients. ap_ids 7000..7154.
--
-- MEMORY MODEL (BizHawk NES domains — matches Client.py TLOZClient)
-- ─────────────────────────────────────────────────────────────────────
--   The TLoZ AP client is a BizHawkClient that reads two NES domains:
--     "RAM"      — the NES 2 KB internal work RAM (client self.wram). EVERY
--                  location flag, the goal byte and the gameplay gate live here.
--                  All addresses top out at 0x077F+0x80 = 0x07FF, exactly 2 KB.
--     "PRG ROM"  — cartridge program ROM (client self.rom). Carries the "LOZ"
--                  identifier the client's validate_rom() checks, and the 4
--                  dynamic overworld offsets (see OW_DYN below).
--
--   The AP location id IS location_table[name] + 7000 (base_id). Client.py
--   game_watcher() → location_check() detects them four ways:
--
--     1. OVERWORLD majors — read 0x80 bytes at RAM 0x067F (overworld_status_block);
--        a major location is taken when block[offset] & 0x10 != 0. Two offsets are
--        STATIC (Armos Knights 0x24, Ocean Heart Container 0x5F); FOUR are DYNAMIC
--        (Starting Sword Cave / White Sword Pond / Magical Sword Grave / Letter
--        Cave) — their overworld offset depends on entrance shuffle and is read
--        from PRG ROM 0x60 (major_offsets_location, 4 bytes, in that order).
--     2. UNDERWORLD EARLY — read 0x80 bytes at RAM 0x06FF
--        (underworld_early_status_block); block[offset] & 0x10 != 0, offsets from
--        floor_location_game_offsets_early. (74 locations, Levels 1-6.)
--     3. UNDERWORLD LATE — read 0x80 bytes at RAM 0x077F
--        (underworld_late_status_block); block[offset] & 0x10 != 0, offsets from
--        floor_location_game_offsets_late. (57 locations, Levels 7-9.)
--     4. SHOPS — three shop-slot bytes (RAM 0x0628/0x0629/0x062A = Left/Middle/
--        Right). A shop slot is bought when slot_byte & shop_bit != 0, where the
--        bit identifies the shop TYPE: Arrow 0x08, Candle 0x10, Blue Ring 0x40,
--        Shield 0x20, Potion 0x04. (15 locations.)
--        TAKE-ANY caves (3) flip together: take_any_caves_checked (RAM 0x0678) >= 4
--        marks all three Take Any Item L/M/R as checked.
--
--   GOAL: Client.py check_victory() sets CLIENT_GOAL when game_mode (RAM 0x12) ==
--         19 (0x13) — the Ganon-defeated / win game state.
--
--   GATE: game_mode (RAM 0x12) must be 5 (normal gameplay, overworld or dungeon);
--         the client guards every status read on this value (base_guard_list) and
--         the legacy connector's StateOKForMainLoop() does the same. We mirror it
--         so a booting cart / menu / scroll transition can never report phantom
--         checks from a zeroed or mid-update status block.
--
-- WHAT THIS DOES (mirrors worlds/tloz/Client.py game_watcher → location_check)
--   • poll(): read the four RAM regions once, decode every wanted id with the exact
--     block/bit math above → AP ids. Gated to the slot's server location set and to
--     the game_mode==5 gate.
--   • is_goal_complete(): game_mode (RAM 0x12) == 19 — the only TLoZ goal (Ganon
--     defeated).
--   • receive_item(): NO-OP (documented). items_handling = 0b101 (Client.py sets
--     ctx.items_handling = 0b101) means the AP SERVER drives item delivery; the
--     reference client writes received items into NES RAM through a HEAVILY guarded
--     path (received_items_check increments a 16-bit obtained counter at RAM 0x0677/
--     0x067B, then per-item guarded writes to weapon/ring/heart/key/rupee bytes plus
--     an item-lift animation + SFX, every write protected by guarded_write against a
--     re-read of the same byte so a mid-frame race never corrupts the save). That
--     guarded multi-write is the piece that must be confirmed in-emulator before it
--     is wired here (a wrong RAM write corrupts the save / desyncs the counter), so
--     it is intentionally deferred rather than shipped unverified. Item delivery is
--     handled launcher-side by the connector's SYNC channel when that path is
--     enabled. Checks + goal flow regardless.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "tloz"

local ADDRESSES_VERIFIED = true   -- table generated from worlds/tloz source

-- ── Memory domains (BizHawk NES) ──────────────────────────────────────────────
local RAM    = "RAM"        -- NES 2 KB internal work RAM (client self.wram)
local PRGROM = "PRG ROM"    -- cartridge program ROM — "LOZ" identifier + dyn offsets

-- ── Addresses / constants (worlds/tloz/Rom.py + Client.py) ────────────────────
local ROM_NAME_LOCATION = 0x00     -- PRG ROM: rom_name; first 3 bytes = "LOZ"
local ROM_NAME          = "LOZ"
local MAJ_OFFSETS_LOC   = 0x60     -- PRG ROM: 4 dynamic overworld offsets
local GAME_MODE         = 0x12     -- RAM: game state (gate=5, goal=19)
local GAME_MODE_PLAY    = 5        -- normal gameplay (overworld or dungeon)
local GAME_MODE_WIN     = 19       -- Ganon defeated / win
local OW_BLOCK          = 0x067F   -- RAM: overworld status block (0x80 bytes)
local UE_BLOCK          = 0x06FF   -- RAM: underworld early status block (0x80 bytes)
local UL_BLOCK          = 0x077F   -- RAM: underworld late status block (0x80 bytes)
local STATUS_LEN        = 0x80
local VISIT_BIT         = 0x10     -- block[offset] & 0x10 => location taken
local LSHOP             = 0x0628   -- RAM: left shop slot flags
local MSHOP             = 0x0629   -- RAM: middle shop slot flags
local RSHOP             = 0x062A   -- RAM: right shop slot flags
local TAKEANY_COUNTER   = 0x0678   -- RAM: take_any_caves_checked (>=4 => all 3)

-- shop TYPE bits (Rom.py bit_positions) keyed by shop type name
local SHOP_BIT = {
  ["Arrow"]     = 0x08,
  ["Candle"]    = 0x10,
  ["Blue Ring"] = 0x40,
  ["Shield"]    = 0x20,
  ["Potion"]    = 0x04,
}

-- ── Location tables (GENERATED from worlds/tloz/Locations.py) ─────────────────
-- ap_id = location_table[name] + 7000. block[offset] & 0x10 => checked.

-- Overworld majors with a STATIC overworld-block offset (ap_id -> offset).
local OW_STATIC = {
  [7006]=36,[7007]=95,
}
-- Overworld majors whose offset is DYNAMIC (ap_id -> index into the 4-byte
-- major_offsets read from PRG ROM 0x60: 0=Starting Sword,1=White Sword,
-- 2=Magical Sword,3=Letter Cave).
local OW_DYN = {
  [7000]=0,[7001]=1,[7002]=2,[7008]=3,
}

-- Underworld early (Levels 1-6): ap_id -> offset into the 0x06FF block. (74)
local UE_LOC = {
  [7009]=127,[7010]=68,[7011]=67,[7012]=84,[7013]=53,[7014]=54,[7015]=114,[7016]=83,
  [7017]=35,[7018]=51,[7019]=116,[7020]=69,[7021]=79,[7022]=95,[7023]=111,[7024]=14,
  [7025]=13,[7026]=108,[7027]=62,[7028]=78,[7029]=126,[7030]=63,[7031]=30,[7032]=47,
  [7033]=15,[7034]=76,[7035]=90,[7036]=77,[7037]=61,[7038]=73,[7039]=42,[7040]=75,
  [7041]=107,[7042]=123,[7043]=105,[7044]=74,[7045]=91,[7046]=93,[7047]=96,[7048]=33,
  [7049]=98,[7050]=19,[7051]=3,[7052]=112,[7053]=81,[7054]=64,[7055]=1,[7056]=4,
  [7057]=70,[7058]=55,[7059]=36,[7060]=20,[7061]=22,[7062]=38,[7063]=71,[7064]=119,
  [7065]=102,[7066]=39,[7067]=85,[7068]=101,[7069]=86,[7070]=87,[7071]=117,[7072]=25,
  [7073]=104,[7074]=28,[7075]=12,[7076]=122,[7077]=88,[7078]=41,[7079]=26,[7080]=45,
  [7081]=60,[7082]=40,
}

-- Underworld late (Levels 7-9): ap_id -> offset into the 0x077F block. (57)
local UL_LOC = {
  [7083]=74,[7084]=24,[7085]=90,[7086]=42,[7087]=43,[7088]=120,[7089]=10,[7090]=109,
  [7091]=58,[7092]=105,[7093]=104,[7094]=122,[7095]=11,[7096]=27,[7097]=12,[7098]=108,
  [7099]=56,[7100]=88,[7101]=9,[7102]=15,[7103]=46,[7104]=95,[7105]=111,[7106]=60,
  [7107]=44,[7108]=92,[7109]=75,[7110]=76,[7111]=93,[7112]=94,[7113]=127,[7114]=14,
  [7115]=63,[7116]=29,[7117]=125,[7118]=110,[7119]=78,[7120]=79,[7121]=0,[7122]=39,
  [7123]=53,[7124]=97,[7125]=86,[7126]=71,[7127]=87,[7128]=17,[7129]=35,[7130]=37,
  [7131]=22,[7132]=55,[7133]=64,[7134]=18,[7135]=98,[7136]=52,[7137]=68,[7138]=21,
  [7139]=38,
}

-- Shops: {ap_id, slot (1=Left,2=Middle,3=Right), shop type}. (15)
local SHOPS = {
  {7140,1,"Arrow"},{7141,2,"Arrow"},{7142,3,"Arrow"},
  {7143,1,"Candle"},{7144,2,"Candle"},{7145,3,"Candle"},
  {7146,1,"Blue Ring"},{7147,2,"Blue Ring"},{7148,3,"Blue Ring"},
  {7149,1,"Shield"},{7150,2,"Shield"},{7151,3,"Shield"},
  {7152,1,"Potion"},{7153,2,"Potion"},{7154,3,"Potion"},
}

-- Take Any caves: all three flip together when the counter (RAM 0x0678) >= 4. (3)
local TAKEANY = { 7003, 7004, 7005 }

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil    -- cached "LOZ" identifier result
local maj_dyn_offsets  = nil    -- {[0..3]} dynamic overworld offsets, read once from ROM
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[tloz] " .. tostring(msg)) end
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

local function bit_and(a, b)
  local res, bitval = 0, 1
  while a > 0 and b > 0 do
    if a % 2 == 1 and b % 2 == 1 then res = res + bitval end
    a = math.floor(a / 2); b = math.floor(b / 2); bitval = bitval * 2
  end
  return res
end

-- ── ROM identity: the TLoZ AP ROM carries "LOZ" at PRG ROM 0x00 ───────────────
-- generate_output writes a rom_name of "LOZ<version>_<player>_<seed>" at FILE
-- offset 0x10 (the iNES header_length); BizHawk's "PRG ROM" domain is HEADERLESS
-- (excludes the 16-byte iNES header), so file 0x10 - 0x10 = PRG ROM 0x00 — the
-- read base coincides with the name start and "LOZ" lands at rom_name[:3], exactly
-- what the client's validate_rom() checks. The trailing bytes encode the world
-- version + seed, which vary per release/seed, so we match ONLY the version-
-- independent "LOZ" prefix — the name-independent detector for any TLoZ AP seed.
local function rom_is_tloz()
  if rom_ok ~= nil then return rom_ok end
  for i = 1, #ROM_NAME do
    local b = read_u8(ROM_NAME_LOCATION + i - 1, PRGROM)
    if b == nil then return false end          -- not readable yet; retry next poll
    if b ~= string.byte(ROM_NAME, i) then
      rom_ok = false
      log("non-TLoZ ROM (no 'LOZ' PRG-ROM signature) — detection idle")
      return false
    end
  end
  rom_ok = true
  log("TLoZ ROM verified ('LOZ' signature present)")
  return true
end

-- The 4 dynamic overworld offsets (entrance-shuffle dependent) live in PRG ROM at
-- 0x60..0x63, read once after the ROM identity is confirmed. Returns nil until all
-- four bytes are readable (so a transient read just retries next poll).
local function ensure_dyn_offsets()
  if maj_dyn_offsets ~= nil then return maj_dyn_offsets end
  local t = {}
  for i = 0, 3 do
    local b = read_u8(MAJ_OFFSETS_LOC + i, PRGROM)
    if b == nil then return nil end
    t[i] = b
  end
  maj_dyn_offsets = t
  log("dynamic overworld offsets read: " ..
      t[0] .. "," .. t[1] .. "," .. t[2] .. "," .. t[3])
  return maj_dyn_offsets
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

-- ── Status blocks (read once per poll) ────────────────────────────────────────
local ow_data = {}    -- offset -> byte, refreshed each poll
local ue_data = {}
local ul_data = {}

local function refresh_block(base, into)
  for i = 0, STATUS_LEN - 1 do into[i] = read_u8(base + i, RAM) end
end

local function block_set(block, offset)
  local byte = block[offset]
  if byte == nil then return false end
  return bit_and(byte, VISIT_BIT) ~= 0
end

-- ── Detection gate ────────────────────────────────────────────────────────────
-- game_mode (RAM 0x12) == 5 is normal gameplay; the client guards every status
-- read on it and the legacy connector's StateOKForMainLoop() requires it. Mirror
-- that so a booting/menu/transition frame can never report phantom checks.
local function in_game()
  local gm = read_u8(GAME_MODE, RAM)
  return gm ~= nil and gm == GAME_MODE_PLAY
end

-- Helper: record a new (wanted, not-yet-reported) check.
local function emit(new, ap_id)
  if not reported[ap_id] and wanted(ap_id) then
    reported[ap_id] = true
    new[#new + 1] = ap_id
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
  for _ in pairs(OW_STATIC) do n = n + 1 end
  for _ in pairs(OW_DYN)    do n = n + 1 end
  for _ in pairs(UE_LOC)    do n = n + 1 end
  for _ in pairs(UL_LOC)    do n = n + 1 end
  n = n + #SHOPS + #TAKEANY
  log("ready: " .. n .. " location ids")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_tloz() then return new end
  if not in_game() then return new end

  refresh_block(OW_BLOCK, ow_data)
  refresh_block(UE_BLOCK, ue_data)
  refresh_block(UL_BLOCK, ul_data)

  -- 1. Overworld majors — static offsets.
  for ap_id, off in pairs(OW_STATIC) do
    if not reported[ap_id] and wanted(ap_id) and block_set(ow_data, off) then
      emit(new, ap_id)
    end
  end
  -- 1b. Overworld majors — dynamic offsets (read from ROM once).
  local dyn = ensure_dyn_offsets()
  if dyn then
    for ap_id, idx in pairs(OW_DYN) do
      if not reported[ap_id] and wanted(ap_id) and block_set(ow_data, dyn[idx]) then
        emit(new, ap_id)
      end
    end
  end

  -- 2. Underworld early.
  for ap_id, off in pairs(UE_LOC) do
    if not reported[ap_id] and wanted(ap_id) and block_set(ue_data, off) then
      emit(new, ap_id)
    end
  end

  -- 3. Underworld late.
  for ap_id, off in pairs(UL_LOC) do
    if not reported[ap_id] and wanted(ap_id) and block_set(ul_data, off) then
      emit(new, ap_id)
    end
  end

  -- 4. Shops — slot byte & shop-type bit.
  local slot_byte = {
    [1] = read_u8(LSHOP, RAM),
    [2] = read_u8(MSHOP, RAM),
    [3] = read_u8(RSHOP, RAM),
  }
  for _, s in ipairs(SHOPS) do
    local ap_id, slot, typ = s[1], s[2], s[3]
    if not reported[ap_id] and wanted(ap_id) then
      local b = slot_byte[slot]
      local bit = SHOP_BIT[typ]
      if b ~= nil and bit ~= nil and bit_and(b, bit) ~= 0 then
        emit(new, ap_id)
      end
    end
  end

  -- 5. Take Any caves — all three when the counter >= 4.
  local tac = read_u8(TAKEANY_COUNTER, RAM)
  if tac ~= nil and tac >= 4 then
    for _, ap_id in ipairs(TAKEANY) do emit(new, ap_id) end
  end

  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_tloz() then return false end
  -- game_mode (RAM 0x12) == 19 — Ganon defeated / win game state.
  local gm = read_u8(GAME_MODE, RAM)
  return gm ~= nil and gm == GAME_MODE_WIN
end

-- Remote items: see the file header. items_handling = 0b101 — the AP server drives
-- item delivery (the reference client writes received items into NES RAM via a
-- 16-bit obtained-counter handshake at RAM 0x0677/0x067B plus per-item guarded
-- writes to inventory bytes with an item-lift animation + SFX). That guarded
-- multi-write path is deferred here until it can be confirmed in-emulator; a wrong
-- RAM write would corrupt the save / desync the counter, so this is a no-op (never
-- a wrong write) rather than shipped unverified.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
