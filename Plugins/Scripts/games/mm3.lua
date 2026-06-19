-- ═══════════════════════════════════════════════════════════════════════════════
-- mm3.lua — game module for the Archipelago BizHawk connector.
--           Mega Man 3 (NES, USA)
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the official
-- AP world worlds/mm3 (client.py + locations.py, main branch). The 170-entry
-- location set is GENERATED directly from the client's own game_watcher() check
-- loops + the MM3_CONSUMABLE_TABLE / MM3_DOC_REMAP (parsed from client.py, not
-- hand-copied) and cross-checked 1:1 against locations.py's declared numeric ids
-- (0 extra, 0 missing). The flag/byte math is replicated EXACTLY from
-- worlds/mm3/client.py game_watcher(). Mock-verified through MoonSharp against
-- synthetic NES memory. Loads crash-free on any ROM; self-disables on a non-MM3
-- cartridge (no "MM3" PRG-ROM signature). Sibling module to mm2.lua.
--
-- LOCATION IDS HAVE NO BASE OFFSET. Unlike MM2 (whose ids are 0x88xxxx), MM3's AP
-- location ids ARE the raw small values locations.py declares (0x0001, 0x0101,
-- 0x0200, …). worlds/mm3 sets World.location_name_to_id = lookup_location_to_id,
-- which maps each name straight to those raw ids with no base_id, and client.py
-- tests them verbatim against ctx.checked_locations (e.g. `0x0001 + i`,
-- `0x0101 + i`, `0x0010 + DOC_REMAP[i]`, the raw 0x02xx consumable keys). So the
-- ids this module emits are exactly the server's location ids.
--
-- MEMORY MODEL (BizHawk NES domains — matches client.py MegaMan3Client)
-- ───────────────────────────────────────────────────────────────────────
--   The MM3 AP client is a BizHawkClient that reads two NES domains:
--     "RAM"      — the NES 2 KB internal work RAM (0x000..0x7FF). EVERY location
--                  flag, the goal byte, the init guard and the in-stage gate live
--                  here. All addresses the client touches top out at 0x689, well
--                  inside 2 KB.
--     "PRG ROM"  — the cartridge program ROM. Carries the "MM3" identifier the
--                  client's validate_rom() checks at offset 0x3F320.
--
--   client.py game_watcher() decodes locations six ways (all RAM):
--
--     1. ROBOT MASTERS — robot_masters_defeated (RAM 0x61), bit i (i 0..7):
--          defeated & (1<<i) → BOTH ids:  0x0001 + i (boss)  AND  0x0101 + i (weapon)
--     2. DOC ROBOTS — doc_robo_defeated (RAM 0x686), bit i (i 0..7):
--          defeated & (1<<i) → id  0x0010 + DOC_REMAP[i]
--          (DOC_REMAP = {0,1,2,3 →same; 4→6; 5→7; 6→4; 7→5})
--     3. RUSH ITEMS — rush_acquired (RAM 0x689), bit i (i 0..1):
--          acquired & (1<<i) → id  0x0111 + i
--     4. WILY BOSSES — completed_stages (RAM 0x687), bit i for i in {0,1,2,4}:
--          stages & (1<<i) → id  0x0009 + i      (i=3 / Wily 4 has NO boss check)
--     5. BREAK MAN — completed_stages (RAM 0x687) & 0x80 → id  0x000F
--     6. CONSUMABLES — only while bar_state==0x80 (in stage) AND
--          ((prog_state>0 and current_stage>=8) or prog_state==0). Indexed by the
--          CURRENT stage: for each (ap_id, off, bit) in MM3_CONSUMABLE_TABLE
--          [current_stage], checked when consumable_checks[off] & (1<<bit) ≠ 0.
--          consumable_checks is the 16-byte block at RAM 0x150.
--
--   GOAL: client.py sends CLIENT_GOAL when completed_stages[0] & 0x20 — i.e.
--         RAM 0x687 bit 5 (Gamma defeated / game complete).
--
--   INIT GUARD: bar_state (RAM 0xB2) is ONLY ever 0x00 or 0x80 once the game is
--         running (display-health-bar flag); client returns early on any other
--         value ("Game is not initialized"). It doubles as the in-stage tracker:
--         0x80 == currently in a stage (consumables + the consumable gate apply).
--         We mirror this so a fresh/booting cart's zeroed RAM never reports
--         phantom checks.
--
-- WHAT THIS DOES (mirrors worlds/mm3/client.py game_watcher → location loops)
--   • poll(): read the relevant RAM once, decode every wanted id with the exact
--     math above → AP ids. Gated to the slot's server location set and the init
--     guard; consumables additionally gated to in-stage + the prog/stage rule.
--   • is_goal_complete(): RAM 0x687 & 0x20 — the only MM3 goal (Gamma defeated).
--   • receive_item(): NO-OP (documented). items_handling = 0b111 (client.py sets
--     ctx.items_handling = 0b111) means the AP SERVER drives ALL item delivery.
--     The reference client writes received items into RAM (weapon energy flags,
--     robot-master / Doc-Robo stage-access unlock masks, lives / E-tanks / Rush,
--     SFX strobes and an EnergyLink path) gated on the game's own received-items
--     counter at RAM 0x688, with a multi-write that depends on the live in-stage
--     state. A wrong RAM write mid-stage corrupts the run / desyncs the counter,
--     so that guarded path is intentionally DEFERRED until it can be confirmed
--     in-emulator rather than shipped unverified. Item delivery is handled
--     launcher-side by the connector's SYNC channel when that path is enabled.
--     Checks + goal flow regardless.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "mm3"

local ADDRESSES_VERIFIED = true   -- id set generated from worlds/mm3 source

-- ── Memory domains (BizHawk NES) ──────────────────────────────────────────────
local RAM    = "RAM"        -- NES 2 KB internal work RAM (client self reads "RAM")
local PRGROM = "PRG ROM"    -- cartridge program ROM — "MM3" identifier

-- ── Addresses / constants (worlds/mm3/client.py) ──────────────────────────────
local ROM_NAME_LOCATION       = 0x3F320  -- PRG ROM: 21-byte name field; first 3 = "MM3"
local ROM_NAME                = "MM3"
local PROG_STATE              = 0x60     -- RAM: stage progression sub-state
local ROBOT_MASTERS_DEFEATED  = 0x61     -- RAM: 8 robot-master defeat flag bits
local CURRENT_STAGE           = 0x22     -- RAM: which stage we are in (consumable index)
local CONSUMABLES_BASE        = 0x150    -- RAM: 16-byte consumables flag block
local ENERGY_BAR              = 0xB2     -- RAM: init guard / in-stage (0x00 or 0x80)
local DOC_ROBOT_DEFEATED      = 0x686    -- RAM: 8 Doc-Robot defeat flag bits
local COMPLETED_STAGES        = 0x687    -- RAM: wily-boss bits + break(0x80) + goal(0x20)
local RUSH_RECEIVED           = 0x689    -- RAM: 2 Rush-acquired flag bits

local GOAL_MASK               = 0x20     -- completed_stages & 0x20 → game complete
local BREAK_MAN_MASK          = 0x80     -- completed_stages & 0x80 → Break Man check
local BREAK_MAN_ID            = 0x000F

local RBM_BOSS_ID_BASE        = 0x0001   -- robot-master boss ids:   base + bit index
local RBM_WEAPON_ID_BASE      = 0x0101   -- robot-master weapon ids: base + bit index
local DOC_ID_BASE             = 0x0010   -- doc-robot ids:           base + DOC_REMAP[i]
local RUSH_ID_BASE            = 0x0111   -- rush-item ids:           base + bit index
local WILY_BOSS_ID_BASE       = 0x0009   -- wily-boss ids:           base + bit index

local BAR_INIT                = 0x00     -- bar_state == 0x00: initialized, not in stage
local BAR_IN_STAGE            = 0x80     -- bar_state == 0x80: in a stage

-- Doc-Robot bit-index → id-offset remap (GENERATED from client.py MM3_DOC_REMAP).
local DOC_REMAP = { [0]=0, [1]=1, [2]=2, [3]=3, [4]=6, [5]=7, [6]=4, [7]=5 }

-- Wily bit indices that DO have a boss check (client loops `for i in (0,1,2,4)`).
-- i = 3 (Wily Stage 4) deliberately has no boss check.
local WILY_BOSS_BITS = { 0, 1, 2, 4 }

-- ── Consumable check table (GENERATED from client.py MM3_CONSUMABLE_TABLE) ─────
-- Keyed by the CURRENT stage index (0..17). Each entry is {ap_id, byte offset
-- into the 0x150 block, bit index}. Checked while in-stage when
-- consumable_checks[0x150 + off] & (1<<bit) ~= 0. Order preserved from source
-- (irrelevant to correctness — each entry is self-describing). 139 entries total.
local CONSUMABLES_BY_STAGE = {
  [0]  = { {0x0200,0,5}, {0x0201,3,2} },
  [1]  = { {0x0202,2,6}, {0x0203,2,5}, {0x0204,2,4}, {0x0205,2,3}, {0x0206,3,6}, {0x0207,3,5}, {0x0208,3,7}, {0x0209,4,0} },
  [2]  = { {0x020A,2,7}, {0x020B,3,0}, {0x020C,3,1}, {0x020D,3,2}, {0x020E,4,2}, {0x020F,4,3}, {0x0210,4,7}, {0x0211,5,1}, {0x0212,6,1}, {0x0213,7,0} },
  [3]  = { {0x0214,0,6}, {0x0215,1,5}, {0x0216,2,3}, {0x0217,2,7}, {0x0218,2,6}, {0x0219,2,5}, {0x021A,4,5} },
  [4]  = { {0x021B,1,3}, {0x021C,1,5}, {0x021D,1,7}, {0x021E,2,0}, {0x021F,1,6}, {0x0220,2,4}, {0x0221,2,5}, {0x0222,4,5} },
  [5]  = { {0x0223,3,0}, {0x0224,3,2}, {0x0225,4,5}, {0x0226,4,6}, {0x0227,6,4} },
  [6]  = { {0x0228,2,0}, {0x0229,2,1}, {0x022A,3,1}, {0x022B,3,2}, {0x022C,3,3}, {0x022D,3,4} },
  [7]  = { {0x022E,3,5}, {0x022F,3,4}, {0x0230,3,3}, {0x0231,3,2} },
  [8]  = { {0x0232,1,4}, {0x0233,2,1}, {0x0234,2,2}, {0x0235,2,5}, {0x0236,3,5}, {0x0237,4,2}, {0x0238,4,4}, {0x0239,5,3}, {0x023A,6,0}, {0x023B,6,1}, {0x023C,7,5} },
  [9]  = { {0x023D,3,2}, {0x023E,3,6}, {0x023F,4,5}, {0x0240,5,4} },
  [10] = { {0x0241,0,2}, {0x0242,2,4} },
  [11] = { {0x0243,4,1}, {0x0244,6,0}, {0x0245,6,1}, {0x0246,6,2}, {0x0247,6,3} },
  [12] = { {0x0248,0,0}, {0x0249,0,3}, {0x024A,0,5}, {0x024B,1,6}, {0x024C,2,7}, {0x024D,2,3}, {0x024E,2,1}, {0x024F,2,2}, {0x0250,3,5}, {0x0251,3,4}, {0x0252,3,6}, {0x0253,3,7} },
  [13] = { {0x0254,0,3}, {0x0255,0,6}, {0x0256,1,0}, {0x0257,3,0}, {0x0258,3,2}, {0x0259,3,3}, {0x025A,3,4}, {0x025B,3,5}, {0x025C,3,6}, {0x025D,4,0}, {0x025E,3,7}, {0x025F,4,1}, {0x0260,4,2} },
  [14] = { {0x0261,0,3}, {0x0262,0,2}, {0x0263,0,6}, {0x0264,1,2}, {0x0265,1,7}, {0x0266,2,0}, {0x0267,2,1}, {0x0268,2,2}, {0x0269,2,3}, {0x026A,5,2}, {0x026B,5,3} },
  [15] = { {0x026C,0,0}, {0x026D,0,1}, {0x026E,0,2}, {0x026F,0,3}, {0x0270,0,4}, {0x0271,0,6}, {0x0272,1,0}, {0x0273,1,2}, {0x0274,1,3}, {0x0275,1,1}, {0x0276,0,7}, {0x0277,3,2}, {0x0278,2,2}, {0x0279,2,3}, {0x027A,2,4}, {0x027B,2,5}, {0x027C,3,1}, {0x027D,3,0}, {0x027E,2,7}, {0x027F,2,6} },
  [16] = { {0x0280,0,0}, {0x0281,0,3}, {0x0282,0,1}, {0x0283,0,2} },
  [17] = { {0x0284,0,2}, {0x0285,0,6}, {0x0286,0,1}, {0x0287,0,5}, {0x0288,0,3}, {0x0289,0,0}, {0x028A,0,4} },
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil    -- cached MM3 identifier result
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[mm3] " .. tostring(msg)) end
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

-- bit i (0..7) as a power-of-two mask, matching the client's (1 << i).
local POW2 = { [0]=1, 2, 4, 8, 16, 32, 64, 128 }

-- ── ROM identity: the MM3 AP ROM carries "MM3" at PRG ROM 0x3F320 ─────────────
-- The client's validate_rom() checks game_name[:3] == b"MM3", reading 21 bytes at
-- PRG ROM 0x3F320. rom.py writes that 21-byte name field at HEADERED offset
-- 0x3F330; BizHawk's "PRG ROM" domain is HEADERLESS (excludes the 16-byte iNES
-- header), so 0x3F330 - 0x10 = 0x3F320 — the read base coincides with the name
-- start, and "MM3" lands at game_name[:3]. (Verified against rom.py's deathlink
-- 0x3F346 and version 0x3F34C writes, which the client likewise reads at 0x3F336 /
-- 0x3F33C = those headered offsets minus 0x10.) The trailing bytes encode the
-- apworld version, which varies per release, so we match ONLY the version-
-- independent "MM3" prefix — the name-independent detector for any MM3 AP seed.
local function rom_is_mm3()
  if rom_ok ~= nil then return rom_ok end
  for i = 1, #ROM_NAME do
    local b = read_u8(ROM_NAME_LOCATION + i - 1, PRGROM)
    if b == nil then return false end          -- not readable yet; retry next poll
    if b ~= string.byte(ROM_NAME, i) then
      rom_ok = false
      log("non-MM3 ROM (no 'MM3' PRG-ROM signature) — detection idle")
      return false
    end
  end
  rom_ok = true
  log("MM3 ROM verified ('MM3' signature present)")
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
-- bar_state (RAM 0xB2) is ONLY 0x00 or 0x80 once the game is running; the client
-- returns early on any other value ("Game is not initialized"). Mirror that so a
-- booting/menu cart's zeroed RAM can never report phantom checks. Returns the
-- raw bar value (or nil) so the caller can also use 0x80 as the in-stage gate.
local function read_bar_state()
  local b = read_u8(ENERGY_BAR, RAM)
  if b == nil then return nil end
  if b ~= BAR_INIT and b ~= BAR_IN_STAGE then return nil end
  return b
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
  -- 8 boss + 8 weapon + 8 doc + 2 rush + 4 wily + 1 break + 139 consumables = 170.
  local consumable_count = 0
  for _, list in pairs(CONSUMABLES_BY_STAGE) do consumable_count = consumable_count + #list end
  log("ready: " .. (8 + 8 + 8 + 2 + 4 + 1 + consumable_count) .. " location ids")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_mm3() then return new end
  local bar = read_bar_state()
  if bar == nil then return new end          -- not initialized → no checks

  -- 1. Robot Masters — robot_masters_defeated bit i → BOTH 0x0001+i AND 0x0101+i.
  local rbm = read_u8(ROBOT_MASTERS_DEFEATED, RAM)
  if rbm then
    for i = 0, 7 do
      if bit_and(rbm, POW2[i]) ~= 0 then
        emit(new, RBM_BOSS_ID_BASE + i)
        emit(new, RBM_WEAPON_ID_BASE + i)
      end
    end
  end

  -- 2. Doc Robots — doc_robo_defeated bit i → 0x0010 + DOC_REMAP[i].
  local doc = read_u8(DOC_ROBOT_DEFEATED, RAM)
  if doc then
    for i = 0, 7 do
      if bit_and(doc, POW2[i]) ~= 0 then emit(new, DOC_ID_BASE + DOC_REMAP[i]) end
    end
  end

  -- 3. Rush items — rush_acquired bit i → 0x0111 + i.
  local rush = read_u8(RUSH_RECEIVED, RAM)
  if rush then
    for i = 0, 1 do
      if bit_and(rush, POW2[i]) ~= 0 then emit(new, RUSH_ID_BASE + i) end
    end
  end

  -- 4/5. Completed stages — Wily bosses {0,1,2,4} → 0x0009+i; Break Man (0x80) → 0x000F.
  local stages = read_u8(COMPLETED_STAGES, RAM)
  if stages then
    for _, i in ipairs(WILY_BOSS_BITS) do
      if bit_and(stages, POW2[i]) ~= 0 then emit(new, WILY_BOSS_ID_BASE + i) end
    end
    if bit_and(stages, BREAK_MAN_MASK) ~= 0 then emit(new, BREAK_MAN_ID) end
  end

  -- 6. Consumables — only while in-stage (bar==0x80) AND the client's prog/stage
  --    rule holds: (prog_state>0 and current_stage>=8) or prog_state==0. This
  --    blocks the Break Man sub-state (prog 0x12 / stage 5), which has no
  --    consumables and never cleans the table.
  if bar == BAR_IN_STAGE then
    local prog  = read_u8(PROG_STATE, RAM)
    local stage = read_u8(CURRENT_STAGE, RAM)
    if prog ~= nil and stage ~= nil and
       ((prog > 0 and stage >= 8) or prog == 0) then
      local list = CONSUMABLES_BY_STAGE[stage]
      if list then
        for _, c in ipairs(list) do
          local ap_id, off, bit = c[1], c[2], c[3]
          if not reported[ap_id] and wanted(ap_id) then
            local b = read_u8(CONSUMABLES_BASE + off, RAM)
            if b ~= nil and bit_and(b, POW2[bit]) ~= 0 then
              reported[ap_id] = true
              new[#new + 1] = ap_id
            end
          end
        end
      end
    end
  end

  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_mm3() then return false end
  if read_bar_state() == nil then return false end
  -- completed_stages & 0x20 (RAM 0x687 bit 5) — Gamma defeated / game complete.
  local b = read_u8(COMPLETED_STAGES, RAM)
  return b ~= nil and bit_and(b, GOAL_MASK) ~= 0
end

-- Remote items: see the file header. items_handling = 0b111 — the AP server drives
-- ALL item delivery (the reference client writes them into RAM gated on the game's
-- own received-items counter at RAM 0x688, with weapon-energy flags, robot-master /
-- Doc-Robo stage-access unlock masks, lives / E-tanks / Rush, SFX strobes and
-- EnergyLink). That guarded multi-write path depends on the live in-stage state and
-- is deferred here until it can be confirmed in-emulator; a wrong RAM write would
-- corrupt the run / desync the counter, so this is a no-op (never a wrong write)
-- rather than shipped unverified.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
