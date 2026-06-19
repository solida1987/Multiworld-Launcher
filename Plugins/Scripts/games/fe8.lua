-- ═══════════════════════════════════════════════════════════════════════════════
-- fe8.lua — game module for the Archipelago BizHawk connector.
--           Fire Emblem: The Sacred Stones (GBA)
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the FE8 AP
-- world (CT075/Archipelago, branch fe8/stable: worlds/fe8/client.py +
-- connector_config.py + constants.py) and the in-emulator connector
-- (CT075/fe8-archipelago, connector/lua/connector_fe8.lua, branch main). The
-- 83-entry location table was GENERATED directly from connector_config.py
-- (the patch-embedded `locations` list), not hand-copied, so it is exact.
-- Loads crash-free on any ROM; self-disables on a non-AP (unpatched) cartridge.
--
--   apworld game string : "Fire Emblem Sacred Stones"   (world dir: fe8)
--   source (apworld)    : https://github.com/CT075/Archipelago/tree/fe8/stable/worlds/fe8
--   source (connector)  : https://github.com/CT075/fe8-archipelago  (connector/lua/connector_fe8.lua)
--
-- MEMORY MODEL (BizHawk GBA domains)
-- ──────────────────────────────────
--   The FE8 AP client (a BizHawkClient) + the in-game connector read the GBA
--   work RAM ("EWRAM") and the cartridge header ("ROM"). All location "checked"
--   bits live in one flag byte array in EWRAM; the location index and the bit
--   index are the SAME number (`flag_id`):
--     ap_id      = 0xFE8000 + flag_id           (FE8_ID_PREFIX + flag_id)
--     checked    = FLAGS[flag_id // 8] & (1 << (flag_id % 8))   (LSB-first)
--   client.py game_watcher does exactly this bit walk (byte_i*8 + i, mask
--   1<<i). connector_fe8.lua reads `flags_offset = 0x026E30`, `flags_size = 12`
--   in EWRAM; client.py reads FLAGS_ADDR (System Bus 0x2026E30). We read the
--   FULL 12 bytes the connector uses (96 bits) so the recruit checks at indices
--   64-82 are covered — client.py's narrower 8-byte read misses those, but the
--   connector and connector_config (FLAGS_SIZE = 12) confirm 12 is correct.
--
-- WHAT THIS DOES (mirrors worlds/fe8/client.py game_watcher)
--   • poll(): scan the flag array → AP location ids, gated to the slot's server
--     location set and to the AP signature (the patched ROM header begins
--     "FE8AP"). The flags live in persistent save state, so no extra in-game
--     gate is required to read them — client.py reads them every watcher tick.
--   • is_goal_complete(): the goal flag. Default is "Defeat Formortiis"
--     (flag 23); slot_data.goal switches it (Clear Valni = 31, Defeat Tirado =
--     9, Clear Lagdou = 41), exactly as client.py's game_watcher does.
--   • receive_item(): NO-OP for now (documented). items_handling = 0b001 means
--     the PATCHED GAME grants its own locally-found items, so a SOLO seed plays
--     fully and every check is reported. Delivering REMOTE multiworld items is
--     the client's guarded EWRAM struct write (APReceivedItem at 0x2026E3C with
--     a "filled" handshake byte + a received-count counter, and it must only be
--     written while the game is in a SAFE proc state — player-phase / prep
--     screen). That write path needs in-emulator verification before it is
--     wired, so it is intentionally left out rather than shipped unverified
--     (would risk mis-applying items / corrupting the handshake).
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "fe8"

local ADDRESSES_VERIFIED = true   -- table generated from worlds/fe8 source

-- ── Memory domains ────────────────────────────────────────────────────────────
local EWRAM = "EWRAM"
local ROM   = "ROM"

-- ── Addresses / constants (worlds/fe8 + connector_fe8.lua) ────────────────────
-- EWRAM offsets are domain-relative (System Bus addr - 0x02000000).
local FLAGS_ARRAY_START   = 0x026E30     -- EWRAM: location/event flag array (System Bus 0x2026E30)
local FLAGS_SIZE          = 12           -- bytes (connector_fe8.lua flags_size; covers indices 0-82)
-- ROM-header game title begins "FE8AP" on any AP-patched ROM (client.py:
-- rom_name.startswith("FE8AP")). The internal title sits at ROM 0x080000A0;
-- ROM-domain offset is 0xA0.
local ROM_NAME_OFFS       = 0xA0
local AP_SIG              = "FE8AP"       -- 5-char AP marker (build-independent)
local FE8_NAME_UNPATCHED  = "FIREEMBLEM"  -- vanilla title prefix (rejected)
local BASE_ID             = 0xFE8000      -- FE8_ID_PREFIX (== 0xFE8_000 in source)

-- Goal flag indices (connector_config.py location indices). client.py picks one
-- of these from slot_data.goal; the default is Defeat Formortiis.
local GOAL_DEFEAT_FORMORTIIS = 23   -- "Defeat Formortiis"        (Goal option 0, default)
local GOAL_CLEAR_VALNI       = 31   -- "Complete Tower of Valni 8"(Goal option 1)
local GOAL_DEFEAT_TIRADO     = 9    -- "Complete Chapter 8"       (Goal option 2)
local GOAL_CLEAR_LAGDOU      = 41   -- "Complete Lagdou Ruins 10" (Goal option 3)

-- ── Location table (GENERATED from worlds/fe8/connector_config.py) ────────────
-- ap_id -> flag index. 83 entries (indices 0-82, contiguous). ap_id = BASE_ID +
-- flag_id; checked when FLAGS_ARRAY[flag_id//8] & (1 << (flag_id % 8)).
local LOC = {
  [16678912]=0,[16678913]=1,[16678914]=2,[16678915]=3,[16678916]=4,[16678917]=5,
  [16678918]=6,[16678919]=7,[16678920]=8,[16678921]=9,[16678922]=10,[16678923]=11,
  [16678924]=12,[16678925]=13,[16678926]=14,[16678927]=15,[16678928]=16,[16678929]=17,
  [16678930]=18,[16678931]=19,[16678932]=20,[16678933]=21,[16678934]=22,[16678935]=23,
  [16678936]=24,[16678937]=25,[16678938]=26,[16678939]=27,[16678940]=28,[16678941]=29,
  [16678942]=30,[16678943]=31,[16678944]=32,[16678945]=33,[16678946]=34,[16678947]=35,
  [16678948]=36,[16678949]=37,[16678950]=38,[16678951]=39,[16678952]=40,[16678953]=41,
  [16678954]=42,[16678955]=43,[16678956]=44,[16678957]=45,[16678958]=46,[16678959]=47,
  [16678960]=48,[16678961]=49,[16678962]=50,[16678963]=51,[16678964]=52,[16678965]=53,
  [16678966]=54,[16678967]=55,[16678968]=56,[16678969]=57,[16678970]=58,[16678971]=59,
  [16678972]=60,[16678973]=61,[16678974]=62,[16678975]=63,[16678976]=64,[16678977]=65,
  [16678978]=66,[16678979]=67,[16678980]=68,[16678981]=69,[16678982]=70,[16678983]=71,
  [16678984]=72,[16678985]=73,[16678986]=74,[16678987]=75,[16678988]=76,[16678989]=77,
  [16678990]=78,[16678991]=79,[16678992]=80,[16678993]=81,[16678994]=82,
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil     -- cached AP-signature result
local goal_flag        = GOAL_DEFEAT_FORMORTIIS   -- overridden by slot_data.goal at init
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[fe8] " .. tostring(msg)) end
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

local POW2 = { [0]=1, 2, 4, 8, 16, 32, 64, 128 }

-- ── ROM identity: the AP patch writes "FE8AP…" into the ROM-header title ──────
local function rom_is_ap()
  if rom_ok ~= nil then return rom_ok end
  for i = 1, #AP_SIG do
    local b = read_u8(ROM_NAME_OFFS + i - 1, ROM)
    if b == nil then return false end          -- not readable yet; retry next poll
    if b ~= string.byte(AP_SIG, i) then
      rom_ok = false
      log("non-AP ROM (no FE8AP header signature) — detection idle")
      return false
    end
  end
  rom_ok = true
  log("AP ROM verified (FE8AP header signature present)")
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

-- slot_data.goal (a Goal Choice int) selects which flag is the completion goal,
-- exactly like client.py's game_watcher. Unknown / missing -> default.
local function apply_goal(slot_data)
  if type(slot_data) ~= "table" then return end
  local g = slot_data.goal
  if type(g) ~= "number" then g = tonumber(g) end
  if     g == 0 then goal_flag = GOAL_DEFEAT_FORMORTIIS
  elseif g == 1 then goal_flag = GOAL_CLEAR_VALNI
  elseif g == 2 then goal_flag = GOAL_DEFEAT_TIRADO
  elseif g == 3 then goal_flag = GOAL_CLEAR_LAGDOU
  end
  log("goal flag = " .. goal_flag)
end

-- ── Flag array (read once per poll) ───────────────────────────────────────────
local flags = {}     -- byte index -> value, refreshed each poll

local function refresh_flags()
  for i = 0, FLAGS_SIZE - 1 do flags[i] = read_u8(FLAGS_ARRAY_START + i, EWRAM) end
end

local function flag_bit(flag_id)
  local byte = flags[math.floor(flag_id / 8)]
  if byte == nil then return false end
  return bit_and(byte, POW2[flag_id % 8]) ~= 0
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
  apply_goal(cfg.slot_data)
  local n = 0; for _ in pairs(LOC) do n = n + 1 end
  log("ready: " .. n .. " location flags")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_ap() then return new end
  refresh_flags()
  for ap_id, code in pairs(LOC) do
    if not reported[ap_id] and wanted(ap_id) and flag_bit(code) then
      reported[ap_id] = true
      new[#new + 1] = ap_id
    end
  end
  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_ap() then return false end
  refresh_flags()
  return flag_bit(goal_flag)
end

-- Remote multiworld items: see the file header. items_handling = 0b001 means the
-- patched game grants its own found items, so solo play and check reporting work
-- fully; applying REMOTE items is the client's guarded EWRAM-struct write
-- (APReceivedItem + "filled"/count handshake, only while in a safe proc state)
-- and is the one piece deferred until it can be confirmed in-emulator. No-op
-- (never a wrong write) until then.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
