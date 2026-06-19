-- ═══════════════════════════════════════════════════════════════════════════════
-- mzm.lua — game module for the Archipelago BizHawk connector.
--           Metroid: Zero Mission (GBA)
--
-- STATUS: COMMUNITY apworld (NOT in the ArchipelagoMW/Archipelago main tree).
--   Source: https://github.com/lilDavid/Archipelago-Metroid-Zero-Mission
--   (world id "mzm", patch ".apmzm" → ".gba", world_version 0.5.3, min AP 0.6.4).
-- Location DETECTION + goal are REAL and SOURCE-DERIVED from that world's
-- client.py (MZMClient.send_game_state), locations.py and patcher/constants.py.
-- The location table was GENERATED directly from locations.py (the seven per-area
-- location_tables), not hand-copied, so it is exact. Loads crash-free on any ROM;
-- self-disables on a non-AP / non-MZM cartridge.
--
-- MEMORY MODEL (BizHawk GBA — the MZM client reads the "System Bus")
-- ──────────────────────────────────────────────────────────────────
--   The MZM AP client (a BizHawkClient) reads GBA addresses through the
--   "System Bus" domain (absolute addresses: 0x2xxxxxx EWRAM, 0x3xxxxxx IWRAM,
--   0x8xxxxxx ROM). Symbol addresses below come from the world's
--   patcher/data/extracted_symbols.json. This module reads the SAME bus so the
--   absolute addresses match the client 1:1.
--
--   LOCATIONS (client.py send_game_state):
--     gRandoLocationBitfields = 0x3005D08, an array of AREA_MAX(7) little-endian
--     u32 — one per area [Brinstar,Kraid,Norfair,Ridley,Tourian,Crateria,
--     Chozodia]. For each area the client walks that area's location_table in
--     dict order; bit N (LSB-first, starting 0) of the area word == the location
--     at ordinal N is checked. Events (id=None) sit at the END of each area
--     table — they occupy bit positions but have NO AP id, so they are simply
--     absent from LOC. Hence LOC packs (area_index, bit_in_area) as one int:
--         packed = area_index*32 + bit         (bit always < 32)
--         checked when  word[area_index] & (1 << bit)
--     ap_id = AP_MZM_ID_BASE(261300) + location id.
--
--   GOAL (client.py — CLIENT_GOAL trigger):
--     ESCAPED_CHOZODIA event set, OR gMainGameMode ∈ {GM_CHOZODIA_ESCAPE(7),
--     GM_CREDITS(8)}. Events live in gEventsTriggered = 0x2037E00 (3 × u32).
--     ESCAPED_CHOZODIA = 0x4B (75) → word 75//32 = 2, bit 75&31 = 11.
--
--   GATE (client.py is_state_read_safe / GM_INGAME):
--     gMainGameMode  = 0x3000C70 (u16), gSubGameMode1 = 0x3000C72 (u16).
--     Location bits are only meaningful while gMainGameMode == GM_INGAME(4);
--     the client only scans them then. The title-screen demo never reaches
--     GM_INGAME with a real save, so checks never report off a fresh boot.
--
-- ROM IDENTITY (client.py validate_rom):
--   ROM 0x80000A0 holds the 12-char GBA internal game title "ZEROMISSIONE"
--   (present on EVERY MZM US cart AND the AP-patched ROM — the patch keeps the
--   header title). The AP-specific seed/slot strings live at sRandoSeed
--   (0x87F7734, slot 0..63 / seed 64..127) but those are blank on a vanilla
--   cart; the title check is the stable "is this Zero Mission" gate, matching
--   the client's first guard.
--
-- WHAT THIS DOES (mirrors MZMClient.send_game_state)
--   • poll(): read the 7 area words → AP location ids, gated to the slot's
--     server location set and to GM_INGAME, so the title demo reports nothing.
--   • is_goal_complete(): ESCAPED_CHOZODIA event OR GM in {escape, credits}.
--   • receive_item(): NO-OP (documented). items_handling = 0b111 — the MZM
--     CLIENT itself injects EVERY received item (locals + remotes) via a guarded
--     gIncomingItem / gIncomingMessage text-box write path (client.py
--     handle_received_items + send_message_and_item), counting against the
--     game's own equipment/tank state. That path is intricate and must be
--     confirmed in-emulator before being reproduced launcher-side, so it is
--     intentionally GATED OUT here rather than shipped unverified (a wrong write
--     to gIncomingItem could mis-grant or soft-lock). Detection/goal — the
--     reportable half — are fully live; item delivery is the deferred piece.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "mzm"

local ADDRESSES_VERIFIED = true   -- table generated from worlds/mzm source

-- ── Memory domain ─────────────────────────────────────────────────────────────
-- The MZM client uses the "System Bus" exclusively (absolute GBA addresses).
local BUS = "System Bus"

-- ── Addresses / constants (worlds/mzm client.py + extracted_symbols.json) ─────
local GMAINGAMEMODE_ADDR   = 0x3000C70    -- IWRAM u16
local LOC_BITFIELDS_ADDR   = 0x3005D08    -- IWRAM 7 × u32 (per-area location bits)
local EVENTS_ADDR          = 0x2037E00    -- EWRAM 3 × u32 (gEventsTriggered)
local AP_SIG_ADDR          = 0x80000A0    -- ROM: GBA internal game title
local AP_SIG               = "ZEROMISSIONE"  -- 12 chars
local AREA_MAX             = 7
local GM_INGAME            = 4
local GM_CHOZODIA_ESCAPE   = 7
local GM_CREDITS           = 8
-- ESCAPED_CHOZODIA = 0x4B (75): word 75//32 = 2, bit 75 & 31 = 11.
local GOAL_EVENT_WORD      = 2
local GOAL_EVENT_BIT       = 11
local BASE_ID              = 261300       -- AP_MZM_ID_BASE

-- ── Location table (GENERATED from worlds/mzm/locations.py) ───────────────────
-- ap_id -> packed (area_index*32 + bit_in_area). 101 entries (ids 0..100).
-- checked when LOC_BITFIELDS[area_index] & (1 << bit_in_area); area_index =
-- floor(packed/32), bit_in_area = packed % 32. Events (id=None, end of each
-- area table) are intentionally absent.
local LOC = {
  [261300]=0,[261301]=1,[261302]=2,[261303]=3,[261304]=4,[261305]=5,[261306]=6,[261307]=7,
  [261308]=8,[261309]=9,[261310]=10,[261311]=11,[261312]=12,[261313]=13,[261314]=14,[261315]=15,
  [261316]=16,[261317]=17,[261318]=18,[261319]=32,[261320]=33,[261321]=34,[261322]=35,[261323]=36,
  [261324]=37,[261325]=38,[261326]=39,[261327]=40,[261328]=41,[261329]=42,[261330]=43,[261331]=44,
  [261332]=64,[261333]=65,[261334]=66,[261335]=67,[261336]=68,[261337]=69,[261338]=70,[261339]=71,
  [261340]=72,[261341]=73,[261342]=74,[261343]=75,[261344]=76,[261345]=77,[261346]=78,[261347]=79,
  [261348]=80,[261349]=81,[261350]=82,[261351]=83,[261352]=84,[261353]=96,[261354]=97,[261355]=98,
  [261356]=99,[261357]=100,[261358]=101,[261359]=102,[261360]=103,[261361]=104,[261362]=105,[261363]=106,
  [261364]=107,[261365]=108,[261366]=109,[261367]=110,[261368]=111,[261369]=112,[261370]=113,[261371]=114,
  [261372]=115,[261373]=128,[261374]=129,[261375]=160,[261376]=161,[261377]=162,[261378]=163,[261379]=164,
  [261380]=165,[261381]=166,[261382]=192,[261383]=193,[261384]=194,[261385]=195,[261386]=196,[261387]=197,
  [261388]=198,[261389]=199,[261390]=200,[261391]=201,[261392]=202,[261393]=203,[261394]=204,[261395]=205,
  [261396]=206,[261397]=207,[261398]=208,[261399]=209,[261400]=210,
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil    -- cached AP-signature result
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[mzm] " .. tostring(msg)) end
end

-- ── Memory API (resolved at init; 2-arg domain form + current-domain fallback) ─
local function resolve_memory_api()
  if not memory then return false end
  mem.read_u8     = memory.read_u8     or memory.readbyte
  mem.read_u16_le = memory.read_u16_le or memory.read_u16
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
  if mem.read_u16_le then
    local ok, v = pcall(mem.read_u16_le, addr, domain)
    if ok and type(v) == "number" then return v end
    ok, v = pcall(mem.read_u16_le, addr)
    if ok and type(v) == "number" then return v end
  end
  -- Fallback: assemble from two bytes (LSB-first).
  local lo = read_u8(addr, domain)
  local hi = read_u8(addr + 1, domain)
  if lo == nil or hi == nil then return nil end
  return lo + hi * 256
end

-- 32-bit little-endian read assembled from bytes (no signed-overflow surprises).
local function read_u32_le(addr, domain)
  local b0 = read_u8(addr, domain)
  local b1 = read_u8(addr + 1, domain)
  local b2 = read_u8(addr + 2, domain)
  local b3 = read_u8(addr + 3, domain)
  if b0 == nil or b1 == nil or b2 == nil or b3 == nil then return nil end
  return b0 + b1 * 256 + b2 * 65536 + b3 * 16777216
end

-- Test bit `n` (0..31) of a non-negative integer using float-safe arithmetic.
local function has_bit(value, n)
  if value == nil then return false end
  -- shift right by n, then test the low bit
  local shifted = math.floor(value / (2 ^ n))
  return (shifted % 2) >= 1
end

-- ── ROM identity: the GBA header title at 0x80000A0 is "ZEROMISSIONE" ──────────
local function rom_is_ap()
  if rom_ok ~= nil then return rom_ok end
  for i = 1, #AP_SIG do
    local b = read_u8(AP_SIG_ADDR + i - 1, "ROM")
    if b == nil then return false end          -- not readable yet; retry next poll
    if b ~= string.byte(AP_SIG, i) then
      rom_ok = false
      log("non-MZM ROM (no ZEROMISSIONE title) — detection idle")
      return false
    end
  end
  rom_ok = true
  log("MZM ROM verified (ZEROMISSIONE title present)")
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

-- ── Location bitfields (read once per poll) ───────────────────────────────────
local words = {}     -- area_index (0..6) -> u32 value, refreshed each poll

local function refresh_words()
  for a = 0, AREA_MAX - 1 do
    words[a] = read_u32_le(LOC_BITFIELDS_ADDR + a * 4, BUS)
  end
end

local function loc_checked(packed)
  local area = math.floor(packed / 32)
  local bit  = packed % 32
  return has_bit(words[area], bit)
end

-- ── Detection gate ────────────────────────────────────────────────────────────
local function in_gameplay()
  local gm = read_u16_le(GMAINGAMEMODE_ADDR, BUS)
  return gm == GM_INGAME
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
  log("ready: " .. n .. " location flags (community apworld 'mzm')")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_ap() then return new end
  if not in_gameplay() then return new end   -- GM_INGAME only (matches client)
  refresh_words()
  for ap_id, packed in pairs(LOC) do
    if not reported[ap_id] and wanted(ap_id) and loc_checked(packed) then
      reported[ap_id] = true
      new[#new + 1] = ap_id
    end
  end
  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_ap() then return false end
  -- ESCAPED_CHOZODIA event bit …
  local goal_word = read_u32_le(EVENTS_ADDR + GOAL_EVENT_WORD * 4, BUS)
  if has_bit(goal_word, GOAL_EVENT_BIT) then return true end
  -- … OR the game has reached the Chozodia-escape / credits states.
  local gm = read_u16_le(GMAINGAMEMODE_ADDR, BUS)
  return gm == GM_CHOZODIA_ESCAPE or gm == GM_CREDITS
end

-- Remote multiworld items: see the file header. items_handling = 0b111 — the MZM
-- client injects every received item itself through a guarded gIncomingItem /
-- gIncomingMessage write path that counts against the game's own state. That
-- path is the one piece deferred until it can be confirmed in-emulator. No-op
-- (never a wrong write) until then.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
