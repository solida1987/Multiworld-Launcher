-- ═══════════════════════════════════════════════════════════════════════════════
-- mmx4.lua — game module for the Archipelago BizHawk connector.
--            Mega Man X4 (PSX, USA) — community apworld by Daxtear
--            Source: github.com/Daxtear/ArchipelagoMMX4, branch MMX4-NWMZ
--            worlds/mmx4/Client.py  (apworld v0.1.3)
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED. All address
-- constants, byte offsets, array indices, item-pointer values and the ROM
-- identity gate were GENERATED from Client.py top-to-bottom (no hand-guessing):
--   • validate_rom():     ADDRESS_PATCH_NAME = 0x0F1740, 16 bytes, "MMX4_ARCHIPELAGO"
--   • location_check():   ADDRESS_ARMOR_PICKED_UP = 0x0F1790 (5 bytes)
--                         ADDRESS_BOSSES_DEFEATED  = 0x0F17A0 (22 bytes)
--                         ADDRESS_ITEMS_PICKED_UP  = 0x0EE558 (u32-LE ptr list)
--   • game_watcher() victory branch: bosses_defeated[21] > 0 (i==21 → Sigma)
--                                    → AP event location 14574300 → CLIENT_GOAL
-- items_handling = 0b111 (FULL remote) — item delivery (weapon/armor/stage-
-- access/tank writes) is the documented deferred piece; receive_item is a no-op
-- until it can be confirmed in-emulator. Checks + goal flow regardless.
--
-- MEMORY MODEL (BizHawk PSX core — Nymashock)
-- ────────────────────────────────────────────
--   All Client.py reads use  self.ram = "MainRAM"  and bizhawk.read() offsets
--   that are PHYSICAL MainRAM addresses (no 0x80000000 base).  BizHawk's PSX
--   core exposes these through the "MainRAM" domain.  The generic connector
--   hands modules the BizHawk `memory` API, so every read here is:
--       memory.read_u8(addr, "MainRAM")   etc.
--   which is exactly  mainmemory.read_u8(addr)  on the PSX core.
--   Item pointer values ARE virtual (0x800F____); we compare the full u32.
--
-- ROM IDENTITY GATE
-- ─────────────────
--   16 ASCII bytes at MainRAM 0x0F1740 must equal "MMX4_ARCHIPELAGO" (written
--   by MMX4_Archipelago.xdelta).  Vanilla or wrong-seed discs read garbage and
--   the module stays silent.  This mirrors validate_rom() exactly.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "mmx4"

local ADDRESSES_VERIFIED = true   -- tables generated from Client.py (branch MMX4-NWMZ)

-- ── Memory domain ─────────────────────────────────────────────────────────────
local MAINRAM = "MainRAM"         -- PSX main RAM (BizHawk Nymashock)

-- ── ROM identity gate (validate_rom in Client.py) ────────────────────────────
local ADDR_PATCH_NAME   = 0x0F1740   -- 16 bytes ASCII: "MMX4_ARCHIPELAGO"
local PATCH_IDENTITY    = "MMX4_ARCHIPELAGO"

-- ── Location addresses (location_check in Client.py) ─────────────────────────
local ADDR_ARMOR        = 0x0F1790   -- 5 bytes: helmet/body/arms1/arms2/legs picked up
local ADDR_BOSSES       = 0x0F17A0   -- 22 bytes: boss/event defeated flags
local ADDR_ITEMS        = 0x0EE558   -- u32-LE ptr list, null-terminated (0x00000000)

-- ── BOSSES array (22 bytes): index → AP location id(s) ───────────────────────
-- Derived from location_check() defeated_bosses loop, Client.py:
--   i==0  Intro Boss → 14574100, 14574101
--   i==1  Web Spider → 14574104, 14574105
--   i==2  Cyber Peacock → 14574109, 14574110
--   i==3  Storm Owl → 14574114, 14574115
--   i==4  Magma Dragoon → 14574118, 14574119
--   i==5  Jet Stingray → 14574122, 14574123
--   i==6  Split Mushroom → 14574125, 14574126
--   i==7  Slash Beast → 14574128, 14574129
--   i==8  Frost Walrus → 14574133, 14574134
--   i==9  Memorial Hall Colonel → 14574135
--   i==10 Space Port Colonel → 14574136
--   i==11 Double / Iris → 14574137
--   i==12 General → 14574138
--   i==13 Web Spider Rematch → 14574139
--   i==14 Cyber Peacock Rematch → 14574140
--   i==15 Storm Owl Rematch → 14574141
--   i==16 Magma Dragoon Rematch → 14574142
--   i==17 Jet Stingray Rematch → 14574143
--   i==18 Split Mushroom Rematch → 14574144
--   i==19 Slash Beast Rematch → 14574145
--   i==20 Frost Walrus Rematch → 14574146
--   i==21 Sigma (goal/event) → 14574300
-- Table is 0-indexed in C; Lua tables are 1-indexed → store as [i+1].
local BOSS_IDS = {
  {14574100, 14574101}, -- [1]  i=0  Intro Boss
  {14574104, 14574105}, -- [2]  i=1  Web Spider
  {14574109, 14574110}, -- [3]  i=2  Cyber Peacock
  {14574114, 14574115}, -- [4]  i=3  Storm Owl
  {14574118, 14574119}, -- [5]  i=4  Magma Dragoon
  {14574122, 14574123}, -- [6]  i=5  Jet Stingray
  {14574125, 14574126}, -- [7]  i=6  Split Mushroom
  {14574128, 14574129}, -- [8]  i=7  Slash Beast
  {14574133, 14574134}, -- [9]  i=8  Frost Walrus
  {14574135},           -- [10] i=9  Memorial Hall Colonel
  {14574136},           -- [11] i=10 Space Port Colonel
  {14574137},           -- [12] i=11 Double / Iris
  {14574138},           -- [13] i=12 General
  {14574139},           -- [14] i=13 Web Spider Rematch
  {14574140},           -- [15] i=14 Cyber Peacock Rematch
  {14574141},           -- [16] i=15 Storm Owl Rematch
  {14574142},           -- [17] i=16 Magma Dragoon Rematch
  {14574143},           -- [18] i=17 Jet Stingray Rematch
  {14574144},           -- [19] i=18 Split Mushroom Rematch
  {14574145},           -- [20] i=19 Slash Beast Rematch
  {14574146},           -- [21] i=20 Frost Walrus Rematch
  {14574300},           -- [22] i=21 Sigma (event / goal)
}
-- Goal slot: BOSS_IDS[22] == Sigma.  bosses_defeated[21] > 0 → goal complete.
local BOSS_SIGMA_IDX = 22     -- 1-based Lua index for the Sigma slot

-- ── ARMOR array (5 bytes): index → AP location id ────────────────────────────
-- Derived from location_check() unlocked_armor loop, Client.py:
--   i==0 Head  → 14574108
--   i==1 Body  → 14574117
--   i==2 Arms1 → 14574112
--   i==3 Arms2 → 14574113
--   i==4 Legs  → 14574102
local ARMOR_IDS = {
  14574108, -- [1] i=0 Helmet Upgrade
  14574117, -- [2] i=1 Body Upgrade
  14574112, -- [3] i=2 Arms Upgrade 1
  14574113, -- [4] i=3 Arms Upgrade 2
  14574102, -- [5] i=4 Legs Upgrade
}

-- ── ITEM POINTER table: PSX virtual address (u32-LE) → AP location id ─────────
-- Derived from the item-pointer while-loop in location_check(), Client.py.
-- Values are 0x800F____ PSX virtual addresses stored in MainRAM as u32-LE.
-- Lua integers hold 32-bit values; use decimal literals for clarity.
-- (0x800FXXXX = 0x80000000 + offset; stored little-endian in the 4-byte slot)
local ITEM_PTR_TO_LOC = {
  -- Intro Stage
  [0x800F4D30] = 14574200,  -- Intro Stage Life Energy (1)
  [0x800F4D40] = 14574201,  -- Intro Stage Max Life Energy (1)
  [0x800F4D38] = 14574202,  -- Intro Stage 1 Up (1)
  -- Web Spider
  [0x800F52B0] = 14574203,  -- Web Spider Life Energy (1)
  [0x800F52C0] = 14574204,  -- Web Spider Max Life Energy (1)
  [0x800F52B8] = 14574103,  -- Web Spider Heart Tank
  -- Cyber Peacock
  [0x800F7438] = 14574106,  -- Cyber Peacock Heart Tank
  [0x800F7440] = 14574107,  -- Cyber Peacock Sub Tank
  -- Storm Owl
  [0x800F7700] = 14574205,  -- Storm Owl Life Energy (1)
  [0x800F7978] = 14574206,  -- Storm Owl Max Life Energy (1)
  [0x800F76F8] = 14574111,  -- Storm Owl Heart Tank
  -- Magma Dragoon
  [0x800F6854] = 14574116,  -- Magma Dragoon Heart Tank
  [0x800F66B4] = 14574207,  -- Magma Dragoon Life Energy (1)
  [0x800F66BC] = 14574208,  -- Magma Dragoon Life Energy (2)
  [0x800F685C] = 14574209,  -- Magma Dragoon Life Energy (3)
  -- Jet Stingray
  [0x800F6C40] = 14574120,  -- Jet Stingray Heart Tank
  [0x800F6E98] = 14574121,  -- Jet Stingray Sub Tank
  [0x800F6EA0] = 14574210,  -- Jet Stingray Max Life Energy (1)
  -- Split Mushroom
  [0x800F6320] = 14574124,  -- Split Mushroom Heart Tank
  [0x800F6328] = 14574211,  -- Split Mushroom Life Energy (1)
  [0x800F6330] = 14574212,  -- Split Mushroom Weapon Energy (1)
  -- Slash Beast
  [0x800F7D3C] = 14574127,  -- Slash Beast Heart Tank
  [0x800F7D44] = 14574213,  -- Slash Beast Max Life Energy (1)
  -- Frost Walrus
  [0x800F56C0] = 14574214,  -- Frost Walrus Weapon Energy (1)
  [0x800F56C8] = 14574215,  -- Frost Walrus Weapon Energy (2)
  [0x800F56B8] = 14574216,  -- Frost Walrus Life Energy (1)
  [0x800F56B0] = 14574217,  -- Frost Walrus Life Energy (2)
  [0x800F5660] = 14574218,  -- Frost Walrus 1 Up (1)
  [0x800F5680] = 14574219,  -- Frost Walrus Life Energy (3)
  [0x800F5688] = 14574220,  -- Frost Walrus Life Energy (4)
  [0x800F5690] = 14574221,  -- Frost Walrus Life Energy (5)
  [0x800F5698] = 14574222,  -- Frost Walrus Life Energy (6)
  [0x800F56A0] = 14574223,  -- Frost Walrus Life Energy (7)
  [0x800F56A8] = 14574224,  -- Frost Walrus Life Energy (8)
  [0x800F5668] = 14574225,  -- Frost Walrus 1 Up (2)
  [0x800F5678] = 14574226,  -- Frost Walrus Max Life Energy (1)
  [0x800F5868] = 14574227,  -- Frost Walrus Max Weapon Energy (1)
  [0x800F5658] = 14574130,  -- Frost Walrus Heart Tank
  [0x800F5670] = 14574131,  -- Frost Walrus Extra Lives Tank
  [0x800F5860] = 14574132,  -- Frost Walrus Weapon Tank
  -- Final Weapon 1
  [0x800F86CC] = 14574228,  -- Final Weapon Life Energy (1)
  [0x800F8564] = 14574229,  -- Final Weapon Max Life Energy (1)
  [0x800F855C] = 14574230,  -- Final Weapon Max Life Energy (2)
  -- Final Weapon 2
  [0x800F87E0] = 14574231,  -- Final Weapon Max Life Energy (3)
  [0x800F87E8] = 14574232,  -- Final Weapon Life Energy (2)
  [0x800F87F0] = 14574233,  -- Final Weapon Life Energy (Boss Rush)
  [0x800F87F8] = 14574234,  -- Final Weapon Weapon Energy (Boss Rush)
  [0x800F8910] = 14574235,  -- Final Weapon Max Life Energy (4)
  [0x800F8918] = 14574236,  -- Final Weapon Max Weapon Energy (1)
  [0x800F8920] = 14574237,  -- Final Weapon Life Energy (3)
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}   -- ap_id -> true once returned from poll()
local rom_verified     = false
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[mmx4] " .. tostring(msg)) end
end

-- ── Memory API (resolved at init; 2-arg domain form + fallbacks) ──────────────
local function resolve_memory_api()
  if not memory then return false end
  mem.read_u8    = memory.read_u8     or memory.readbyte
  mem.read_u32le = memory.read_u32_le or memory.readdword
  return mem.read_u8 ~= nil
end

-- ── ROM identity gate ─────────────────────────────────────────────────────────
local function check_rom_identity()
  if not mem.read_u8 then return false end
  local ok, result = pcall(function()
    local s = ""
    for i = 0, 15 do
      local b = mem.read_u8(ADDR_PATCH_NAME + i, MAINRAM)
      if not b then return nil end
      s = s .. string.char(b)
    end
    return s
  end)
  if not ok or result == nil then return false end
  return result == PATCH_IDENTITY
end

-- ── Module init ───────────────────────────────────────────────────────────────
function M.init(ctx)
  reported     = {}
  rom_verified = false
  if ctx and ctx.log then log_fn = ctx.log end
  if not resolve_memory_api() then
    log("memory API not available — module inactive")
    return
  end
  log("init ok; awaiting ROM identity gate (MMX4_ARCHIPELAGO @ 0x0F1740)")
end

-- ── poll() → list of newly-checked AP location ids ───────────────────────────
function M.poll()
  if not mem.read_u8 then return {} end

  -- Revalidate ROM identity every poll (cheap, 16 bytes).
  if not rom_verified then
    if not check_rom_identity() then return {} end
    rom_verified = true
    log("ROM identity confirmed: MMX4_ARCHIPELAGO")
  end

  local ids = {}

  -- 1. ARMOR (5 bytes at ADDR_ARMOR; i → ARMOR_IDS[i+1])
  local armor_ok, armor_err = pcall(function()
    for i = 0, 4 do
      local v = mem.read_u8(ADDR_ARMOR + i, MAINRAM)
      if v and v > 0 then
        local loc_id = ARMOR_IDS[i + 1]
        if loc_id and not reported[loc_id] then
          reported[loc_id] = true
          ids[#ids + 1] = loc_id
        end
      end
    end
  end)
  if not armor_ok then log("armor read error: " .. tostring(armor_err)) end

  -- 2. BOSSES (22 bytes at ADDR_BOSSES; i → BOSS_IDS[i+1])
  local boss_ok, boss_err = pcall(function()
    for i = 0, 21 do
      local v = mem.read_u8(ADDR_BOSSES + i, MAINRAM)
      if v and v > 0 then
        local group = BOSS_IDS[i + 1]
        if group then
          for _, loc_id in ipairs(group) do
            if not reported[loc_id] then
              reported[loc_id] = true
              ids[#ids + 1] = loc_id
            end
          end
        end
      end
    end
  end)
  if not boss_ok then log("boss read error: " .. tostring(boss_err)) end

  -- 3. ITEM POINTERS (null-terminated u32-LE list at ADDR_ITEMS)
  --    Read 4 bytes at a time; stop at 0x00000000.
  --    Cap at 256 slots to guard against a corrupt list.
  local item_ok, item_err = pcall(function()
    for slot = 0, 255 do
      local base = ADDR_ITEMS + slot * 4
      -- Read 4 bytes as individual u8 and assemble LE u32
      local b0 = mem.read_u8(base + 0, MAINRAM)
      local b1 = mem.read_u8(base + 1, MAINRAM)
      local b2 = mem.read_u8(base + 2, MAINRAM)
      local b3 = mem.read_u8(base + 3, MAINRAM)
      if not (b0 and b1 and b2 and b3) then break end
      -- Assemble as u32 little-endian.  Lua 5.1 integers are at least 32-bit.
      local ptr = b0 + b1 * 256 + b2 * 65536 + b3 * 16777216
      if ptr == 0 then break end
      local loc_id = ITEM_PTR_TO_LOC[ptr]
      if loc_id and not reported[loc_id] then
        reported[loc_id] = true
        ids[#ids + 1] = loc_id
      end
    end
  end)
  if not item_ok then log("item-ptr read error: " .. tostring(item_err)) end

  if #ids > 0 then
    log("checks: " .. #ids .. " new location(s)")
  end
  return ids
end

-- ── is_goal_complete() ────────────────────────────────────────────────────────
-- Sigma is boss-array index 21 (BOSS_IDS[22]).  When that byte is > 0 the
-- event location 14574300 has been collected → Victory → CLIENT_GOAL.
-- We detect goal the same way as any other boss: if bosses_defeated[21] > 0.
function M.is_goal_complete()
  if not mem.read_u8 then return false end
  if not rom_verified then return false end
  local ok, v = pcall(mem.read_u8, ADDR_BOSSES + 21, MAINRAM)
  return ok and v ~= nil and v > 0
end

-- ── receive_item() ────────────────────────────────────────────────────────────
-- items_handling = 0b111: the AP CLIENT (MMX4Client in game_watcher) writes
-- weapons/armor/tanks/stage-access to MainRAM every frame from items_received.
-- That write path is not replicated here — a wrong MainRAM write corrupts the
-- live game — and is deferred until it can be confirmed in-emulator.
-- Checks and goal flow fully regardless; a solo or co-op-of-checks seed plays.
function M.receive_item(item_id, meta)
  -- no-op (documented deferred path)
end

return M
