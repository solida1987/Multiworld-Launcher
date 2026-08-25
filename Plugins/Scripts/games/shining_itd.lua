-- ═══════════════════════════════════════════════════════════════════════════════
-- shining_itd.lua — game module for the Archipelago BizHawk connector.
--                   Shining in the Darkness (Mega Drive / Genesis)
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the AP
-- world lagdotcom/Archipelago, release tag 0.0.4 (shining_itd.apworld:
-- Client.py + Constants.py + Locations.py + Goals.py). The 133-entry location
-- table and the three goal tables are GENERATED from Locations.py/Goals.py
-- (scratchpad/gen_shining_tables.py), not hand-copied. Loads crash-free on
-- any ROM; self-disables unless the cartridge header carries the game's exact
-- 32-byte international name and revision 00.
--
-- ⚠ THE RELEASE TRAP, recorded because it bit again while fetching this:
-- lagdotcom/Archipelago is ONE repository with MANY worlds, and its "latest"
-- release is Phantasy Star II. `gh release download` without an explicit tag
-- hands you a different game's client. Tag 0.0.4 is Shining in the Darkness,
-- and Client.py in that package declares game = 'Shining in the Darkness'.
--
-- MEMORY MODEL (BizHawk Genesis domains — matches Client.py SITDClient)
-- ───────────────────────────────────────────────────────────────────────
--   Client.py lines 25-26: `rom = 'MD CART'`, `ram = '68K RAM'` — the
--   client's own BizHawk domain names, used verbatim here.
--
--   "68K RAM" (work RAM, 68000 → BIG-endian for multi-byte reads):
--     ALL_FLAG block   0x1601..0x1648 (72 B)  — Constants.py: ALL_FLAG_START/
--                      ALL_FLAG_END. The comment above them records why the
--                      block starts at 0x1601: "1600 changes whenever
--                      entering battle, so ignore it".
--     HERO_MAX_HP      0x16B4 (2 B, big-endian) — Constants.py line 18;
--                      Client.py not_in_game() (135-137): max HP == 0 means
--                      no save is loaded. Used as the gameplay gate for BOTH
--                      poll and goal (the false-GOAL-at-boot rule).
--   "MD CART" (the cartridge):
--     INTERNATIONAL_NAME 0x150 (32 B) — must equal, byte for byte,
--                      'SHINING IN          THE DARKNESS' (validate_rom,
--                      Client.py 52-54).
--     VERSION          0x18C (2 B) — must be ASCII '00' (REV00 only,
--                      Client.py 55-57).
--     GOAL byte        0xFFFFF (1 B) — Constants.py GOAL_SPACE; the world's
--                      patch stamps the seed's goal id (0/1/2, Goals.py)
--                      into the ROM's last byte, and met_goal_check reads it
--                      back (Client.py 183).
--
-- LOCATION DETECTION (Client.py location_check, lines 84-103):
--   one flags read of the whole block; a location is checked when
--   `byte & mask == mask` — ALL mask bits set. ⚠ NOT a single-bit space:
--   'L3 - Entered Labyrinth L3' has mask 0x30, two bits. A POW2 lookup
--   would decode that location wrongly; the all-bits test is the client's
--   own and is what this module uses. The flags live in save-backed work
--   RAM, so re-polling after a reconnect re-derives the same set — there is
--   no baseline to corrupt and no edge to miss.
--
-- GOAL (Client.py met_goal_check, lines 180-191): the goal id names a set of
--   completion items (Goals.py); the goal is met when every location whose
--   fixed_item is in that set has been checked. The upstream client tests
--   ctx.checked_locations (server state); this module evaluates the SAME
--   locations' flags directly — equivalent, and it survives reconnects
--   because the flags are the persistent record. An id outside 0..2 (an
--   unpatched ROM's last byte, or a future goal this table does not know)
--   means the goal is never reported and the reason is logged once.
--
-- receive_item(): NO-OP (documented). items_handling = 0b101 (Client.py
--   line 60: "other, own, starting" — note the comment names all three but
--   the VALUE has bit 0b010 clear): the player's OWN items come from the
--   patched ROM itself and are never resent by the server, so a read-only
--   module loses nothing there. Items FROM OTHER WORLDS are delivered by the
--   reference client through guarded writes (inventory slot scan 0x16EA,
--   gold 0x16A4 — Client.py 139-178); that machinery depends on live game
--   state and is deferred until it can be confirmed in-emulator. Checks and
--   goal flow regardless; a solo seed is fully completable because its own
--   progression items are in its own ROM.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "shining_itd"

local ADDRESSES_VERIFIED = true   -- tables generated from the world's source

-- ── Memory domains (Client.py lines 25-26, the client's own names) ────────────
local WORKRAM = "68K RAM"
local CART    = "MD CART"

-- ── Addresses (Constants.py / Client.py, shining_itd 0.0.4) ───────────────────
local ADDR_FLAGS       = 0x1601   -- ALL_FLAG_START (0x1600 skipped: battles touch it)
local FLAGS_LEN        = 0x48     -- ALL_FLAG_END 0x1649 - start
local ADDR_HERO_MAX_HP = 0x16B4   -- 2 bytes big-endian; 0 = not in game
local ROM_NAME_ADDR    = 0x150    -- 32 bytes, exact match required
local ROM_NAME         = "SHINING IN          THE DARKNESS"
local ROM_VER_ADDR     = 0x18C    -- 2 bytes, ASCII "00" (REV00 only)
local ROM_GOAL_ADDR    = 0xFFFFF  -- GOAL_SPACE: the patch's goal id byte
local LOCATIONS = {
  { id = 5170000, addr = 0x1620, mask = 0x08 },  -- L1 - Start - Herb Chest 1
  { id = 5170001, addr = 0x1620, mask = 0x04 },  -- L1 - Start - 50g Chest
  { id = 5170002, addr = 0x1620, mask = 0x01 },  -- L1 - Start - Bronze Knife Chest
  { id = 5170003, addr = 0x1620, mask = 0x02 },  -- L1 - Start - Herb Chest 2
  { id = 5170004, addr = 0x1621, mask = 0x01 },  -- L1 - Start - Herb Chest 3
  { id = 5170005, addr = 0x1621, mask = 0x04 },  -- L1 - Start - 100g Chest
  { id = 5170006, addr = 0x163F, mask = 0x02 },  -- L1 - Start - Defeat Kaiser Krab fixed=Defeat Kaiser Krab
  { id = 5170007, addr = 0x1616, mask = 0x01 },  -- L1 - Start - Receive Dwarf's Key from Minister
  { id = 5170100, addr = 0x1621, mask = 0x20 },  -- L1 - Before Strength - Depoison Chest
  { id = 5170101, addr = 0x1621, mask = 0x40 },  -- L1 - Before Strength - Herb Chest
  { id = 5170102, addr = 0x1621, mask = 0x80 },  -- L1 - Before Strength - Wisdom Seed Chest
  { id = 5170200, addr = 0x1633, mask = 0x01 },  -- Strength - Defeat Chest Beak 1
  { id = 5170201, addr = 0x1633, mask = 0x02 },  -- Strength - Wisdom Seed Chest 1
  { id = 5170202, addr = 0x1633, mask = 0x20 },  -- Strength - Defeat Chest Beak 2
  { id = 5170203, addr = 0x1633, mask = 0x40 },  -- Strength - Wisdom Seed Chest 2
  { id = 5170204, addr = 0x1633, mask = 0x80 },  -- Strength - Depoison Chest
  { id = 5170205, addr = 0x1632, mask = 0x08 },  -- Strength - 100g Chest
  { id = 5170206, addr = 0x1632, mask = 0x10 },  -- Strength - Smelling Salts Chest
  { id = 5170207, addr = 0x1632, mask = 0x01 },  -- Strength - Herb Chest
  { id = 5170208, addr = 0x1632, mask = 0x04 },  -- Strength - Defeat Chest Beak 3
  { id = 5170209, addr = 0x1632, mask = 0x20 },  -- Strength - Woven Robe Chest
  { id = 5170210, addr = 0x163D, mask = 0x01 },  -- Strength - Meet Gila fixed=Meet Gila
  { id = 5170211, addr = 0x1633, mask = 0x10 },  -- Strength - Short Sword Chest
  { id = 5170212, addr = 0x1633, mask = 0x04 },  -- Strength - Wisdom Seed Chest 3
  { id = 5170213, addr = 0x1608, mask = 0x01 },  -- Strength - Door of Strength fixed=Complete Trial of Strength
  { id = 5170214, addr = 0x1635, mask = 0x01 },  -- Strength - Healer Fruit Chest
  { id = 5170300, addr = 0x1621, mask = 0x02 },  -- L1 - Before Courage - Smelling Salts Chest
  { id = 5170301, addr = 0x1621, mask = 0x08 },  -- L1 - Before Courage - Morning Star Chest
  { id = 5170400, addr = 0x1637, mask = 0x01 },  -- Courage - Wisdom Seed Chest 1
  { id = 5170401, addr = 0x1637, mask = 0x02 },  -- Courage - 50g Chest
  { id = 5170402, addr = 0x1637, mask = 0x10 },  -- Courage - Angel Feather Chest
  { id = 5170403, addr = 0x1637, mask = 0x20 },  -- Courage - Woven Robe Chest
  { id = 5170404, addr = 0x1637, mask = 0x40 },  -- Courage - Defeat Chest Beak
  { id = 5170405, addr = 0x1637, mask = 0x80 },  -- Courage - Morning Star Chest
  { id = 5170406, addr = 0x1637, mask = 0x04 },  -- Courage - 100g Chest
  { id = 5170407, addr = 0x1636, mask = 0x01 },  -- Courage - Depoison Chest
  { id = 5170408, addr = 0x1637, mask = 0x08 },  -- Courage - Smelling Salts Chest
  { id = 5170409, addr = 0x1636, mask = 0x04 },  -- Courage - Bronze Shield Chest
  { id = 5170410, addr = 0x1636, mask = 0x40 },  -- Courage - Healer Fruit Chest
  { id = 5170411, addr = 0x1636, mask = 0x08 },  -- Courage - Wisdom Seed Chest 2
  { id = 5170412, addr = 0x1636, mask = 0x20 },  -- Courage - Woven Hood Chest
  { id = 5170413, addr = 0x1608, mask = 0x02 },  -- Courage - Door of Courage fixed=Complete Trial of Courage
  { id = 5170414, addr = 0x163F, mask = 0x04 },  -- Courage - Defeat Tortolyde fixed=Defeat Tortolyde
  { id = 5170500, addr = 0x1621, mask = 0x10 },  -- L1 - Before Truth - 100g Chest
  { id = 5170600, addr = 0x1630, mask = 0x10 },  -- Truth - Wisdom Seed Chest
  { id = 5170601, addr = 0x1630, mask = 0x04 },  -- Truth - 50g Chest
  { id = 5170602, addr = 0x1631, mask = 0x80 },  -- Truth - Wood Staff Chest
  { id = 5170603, addr = 0x1631, mask = 0x10 },  -- Truth - Healer Fruit Chest
  { id = 5170604, addr = 0x1630, mask = 0x01 },  -- Truth - Depoison Chest
  { id = 5170605, addr = 0x1630, mask = 0x40 },  -- Truth - Defeat Ghost 1
  { id = 5170606, addr = 0x1630, mask = 0x20 },  -- Truth - Angel Feather Chest
  { id = 5170607, addr = 0x1630, mask = 0x02 },  -- Truth - False Idol Chest
  { id = 5170608, addr = 0x1631, mask = 0x20 },  -- Truth - Defeat Ghost 2
  { id = 5170609, addr = 0x1630, mask = 0x08 },  -- Truth - Smelling Salts Chest
  { id = 5170610, addr = 0x1631, mask = 0x01 },  -- Truth - Chain Mail Chest
  { id = 5170611, addr = 0x1631, mask = 0x04 },  -- Truth - Battle Axe Chest
  { id = 5170612, addr = 0x1608, mask = 0x04 },  -- Truth - Door of Truth fixed=Complete Trial of Truth
  { id = 5170700, addr = 0x163D, mask = 0x10 },  -- Truth - Idol - Defeat Doppler fixed=Defeat Doppler
  { id = 5170701, addr = 0x1631, mask = 0x08 },  -- Truth - Idol - Rune Key Chest
  { id = 5170800, addr = 0x162B, mask = 0x40 },  -- Wisdom - Map 1 Chest
  { id = 5170801, addr = 0x162B, mask = 0x80 },  -- Wisdom - Battle Axe Chest
  { id = 5170802, addr = 0x162B, mask = 0x04 },  -- Wisdom - Map 2 Chest
  { id = 5170803, addr = 0x163D, mask = 0x40 },  -- Wisdom - Meet Dai fixed=Meet Dai
  { id = 5170804, addr = 0x162A, mask = 0x04 },  -- Wisdom - Smelling Salts Chest
  { id = 5170805, addr = 0x162B, mask = 0x20 },  -- Wisdom - Flail Chest
  { id = 5170806, addr = 0x162B, mask = 0x01 },  -- Wisdom - Defeat Ghost
  { id = 5170807, addr = 0x162B, mask = 0x08 },  -- Wisdom - Dark Block Chest
  { id = 5170808, addr = 0x162A, mask = 0x01 },  -- Wisdom - Herb-Water Chest
  { id = 5170809, addr = 0x162B, mask = 0x02 },  -- Wisdom - Mithril Ore Chest
  { id = 5170810, addr = 0x1608, mask = 0x08 },  -- Wisdom - Door of Wisdom fixed=Complete Trial of Wisdom
  { id = 5170811, addr = 0x162D, mask = 0x02 },  -- Wisdom - Fire Sword Chest
  { id = 5170812, addr = 0x162D, mask = 0x01 },  -- Wisdom - 200g Chest
  { id = 5170900, addr = 0x1623, mask = 0x10 },  -- L2 - Mithril Ore Chest
  { id = 5170901, addr = 0x1622, mask = 0x01 },  -- L2 - 500g Chest
  { id = 5170902, addr = 0x1622, mask = 0x04 },  -- L2 - Depoison Chest
  { id = 5170903, addr = 0x1622, mask = 0x02 },  -- L2 - Great Axe Chest
  { id = 5170904, addr = 0x1623, mask = 0x02 },  -- L2 - Angel Feather Chest
  { id = 5170905, addr = 0x1623, mask = 0x01 },  -- L2 - Magic Hood Chest
  { id = 5170906, addr = 0x1623, mask = 0x08 },  -- L2 - Fire Staff Chest
  { id = 5170907, addr = 0x1623, mask = 0x20 },  -- L2 - Smelling Salts Chest
  { id = 5170908, addr = 0x1623, mask = 0x04 },  -- L2 - Healer Fruit Chest
  { id = 5170909, addr = 0x1622, mask = 0x08 },  -- L2 - Sun Armor Chest
  { id = 5170910, addr = 0x1623, mask = 0x80 },  -- L2 - Worn Robe Chest
  { id = 5170911, addr = 0x1623, mask = 0x40 },  -- L2 - 300g Chest
  { id = 5171000, addr = 0x1605, mask = 0x30 },  -- L3 - Entered Labyrinth L3 fixed=Enter Labyrinth L3
  { id = 5171001, addr = 0x1640, mask = 0x10 },  -- L3 - Defeat Shell Beast fixed=Defeat Shell Beast
  { id = 5171002, addr = 0x1618, mask = 0x02 },  -- L3 - Receive Medallion from Xern fixed=Medallion
  { id = 5171003, addr = 0x1625, mask = 0x20 },  -- L3 - 500g Chest
  { id = 5171004, addr = 0x1624, mask = 0x01 },  -- L3 - Mystic Rope Chest
  { id = 5171005, addr = 0x1624, mask = 0x02 },  -- L3 - Healer Fruit Chest
  { id = 5171006, addr = 0x1625, mask = 0x10 },  -- L3 - Herb-Water Chest
  { id = 5171007, addr = 0x1625, mask = 0x40 },  -- L3 - Ice Staff Chest
  { id = 5171008, addr = 0x1625, mask = 0x02 },  -- L3 - Light Helm Chest
  { id = 5171100, addr = 0x1632, mask = 0x02 },  -- Strength - Rope - Mithril Ore Chest
  { id = 5171200, addr = 0x1625, mask = 0x08 },  -- L3 - Rope - Storm Sword Chest
  { id = 5171201, addr = 0x1625, mask = 0x08 },  -- L3 - Rope - Great Flail Chest
  { id = 5171300, addr = 0x1625, mask = 0x80 },  -- L3 - Rope or Cell - Mithril Ore Chest
  { id = 5171400, addr = 0x1626, mask = 0x08 },  -- L4 - Endurostaff Chest
  { id = 5171401, addr = 0x1627, mask = 0x02 },  -- L4 - Elven Hood Chest
  { id = 5171402, addr = 0x1626, mask = 0x04 },  -- L4 - Holy Water Chest
  { id = 5171403, addr = 0x1626, mask = 0x10 },  -- L4 - Healer Fruit Chest
  { id = 5171404, addr = 0x1626, mask = 0x02 },  -- L4 - Herb-Water Chest
  { id = 5171405, addr = 0x1627, mask = 0x40 },  -- L4 - Steel Whip Chest
  { id = 5171406, addr = 0x1627, mask = 0x08 },  -- L4 - Heal Ring Chest
  { id = 5171407, addr = 0x1627, mask = 0x20 },  -- L4 - Defeat Hand Eater 1
  { id = 5171408, addr = 0x1627, mask = 0x80 },  -- L4 - Defeat Hand Eater 2
  { id = 5171409, addr = 0x1626, mask = 0x01 },  -- L4 - Frost Armor Chest
  { id = 5171410, addr = 0x163D, mask = 0x04 },  -- L4 - Defeat Dark Knight
  { id = 5171411, addr = 0x1627, mask = 0x10 },  -- L4 - Cell Key Chest
  { id = 5171412, addr = 0x1627, mask = 0x04 },  -- L4 - Miracle Herb Chest
  { id = 5171500, addr = 0x1627, mask = 0x01 },  -- L4 - Orb - Light Blade Chest
  { id = 5171600, addr = 0x1633, mask = 0x08 },  -- Strength - Cell - Forbidden Box Chest
  { id = 5171700, addr = 0x1636, mask = 0x02 },  -- Courage - Cell - Demon Staff Chest
  { id = 5171800, addr = 0x1631, mask = 0x40 },  -- Truth - Cell - Magic Ring Chest
  { id = 5171900, addr = 0x162A, mask = 0x02 },  -- Wisdom - Cell - Defeat Ghost
  { id = 5172000, addr = 0x1622, mask = 0x10 },  -- L2 - Cell - Barrier Ring Chest
  { id = 5172100, addr = 0x1625, mask = 0x01 },  -- L3 - Cell - Light Shield Chest
  { id = 5172200, addr = 0x163F, mask = 0x01 },  -- L4 - Cell - Meet Jessa fixed=Meet Jessa
  { id = 5172201, addr = 0x161A, mask = 0x02 },  -- L4 - Cell - Receive Magic Ring from King
  { id = 5172300, addr = 0x1629, mask = 0x01 },  -- L5 - Mithril Ore Chest
  { id = 5172301, addr = 0x1628, mask = 0x08 },  -- L5 - 1000g Chest
  { id = 5172302, addr = 0x1628, mask = 0x02 },  -- L5 - Magic Robe Chest
  { id = 5172303, addr = 0x1628, mask = 0x04 },  -- L5 - Defeat Hand Eater 1
  { id = 5172304, addr = 0x1628, mask = 0x10 },  -- L5 - Magic Ring Chest
  { id = 5172305, addr = 0x1629, mask = 0x08 },  -- L5 - Defeat Hand Eater 2
  { id = 5172306, addr = 0x1629, mask = 0x40 },  -- L5 - Defeat Hand Eater 3
  { id = 5172307, addr = 0x1629, mask = 0x80 },  -- L5 - Dark Scimitar Chest
  { id = 5172308, addr = 0x1628, mask = 0x01 },  -- L5 - Dark Block Chest
  { id = 5172309, addr = 0x1629, mask = 0x10 },  -- L5 - 2000g Chest
  { id = 5172310, addr = 0x1629, mask = 0x04 },  -- L5 - 200g Chest
  { id = 5172311, addr = 0x1629, mask = 0x02 },  -- L5 - Light Armor Chest
  { id = 5172312, addr = 0x1629, mask = 0x20 },  -- L5 - Miracle Herb Chest
  { id = 5172313, addr = 0x1607, mask = 0x80 },  -- L5 - Defeat Dark Sol fixed=Defeat Dark Sol
}

local GOALS = {
  [0] = {  -- Defeat Dark Sol
    { addr = 0x1607, mask = 0x80 },  -- L5 - Defeat Dark Sol
  },
  [1] = {  -- Complete Trial of Courage, Complete Trial of Strength, Complete Trial of Truth, Complete Trial of Wisdom
    { addr = 0x1608, mask = 0x01 },  -- Strength - Door of Strength
    { addr = 0x1608, mask = 0x02 },  -- Courage - Door of Courage
    { addr = 0x1608, mask = 0x04 },  -- Truth - Door of Truth
    { addr = 0x1608, mask = 0x08 },  -- Wisdom - Door of Wisdom
  },
  [2] = {  -- Complete Trial of Strength
    { addr = 0x1608, mask = 0x01 },  -- Strength - Door of Strength
  },
}
-- generator: 133 unique ids, 14 fixed-item locations

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil    -- cached YES only — see rom_is_sitd()
local mem              = {}
local log_fn           = nil
local goal_warned      = false

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[shining_itd] " .. tostring(msg)) end
end

-- ── Pure-Lua bitwise AND (the connector runs on Lua cores without bit32) ──────
local function bit_and(a, b)
  local res, bitval = 0, 1
  while a > 0 and b > 0 do
    if a % 2 == 1 and b % 2 == 1 then res = res + bitval end
    a = math.floor(a / 2); b = math.floor(b / 2); bitval = bitval * 2
  end
  return res
end

-- ── Domain guard. A mistyped domain name must fail LOUDLY, never quietly fall
--    back to whichever domain the core happens to have current: that would turn
--    one wrong name into plausible-looking reads of the wrong memory.
local _ap_domains, _ap_two_arg
local _ap_domain_warned = {}

local function ap_domain_ok(domain)
  if domain == nil then return true end
  if _ap_domains == nil and memory and memory.getmemorydomainlist then
    local ok, list = pcall(memory.getmemorydomainlist)
    if ok and type(list) == "table" then
      _ap_domains = {}
      for _, d in pairs(list) do _ap_domains[tostring(d)] = true end
      local reader = memory.read_u8 or memory.readbyte
      local probe = next(_ap_domains)
      if reader and probe ~= nil then
        _ap_two_arg = (pcall(reader, 0, probe)) and true or false
      end
    end
  end
  if _ap_domains == nil then return true end  -- core exposes no list; cannot check
  if _ap_domains[domain] then return true end
  if not _ap_domain_warned[domain] then
    _ap_domain_warned[domain] = true
    log("memory domain '" .. tostring(domain) .. "' does not exist in this core"
        .. " -- access refused (never redirected to the current domain)")
  end
  return false
end

-- ── Memory API (2-arg domain form with current-domain fallback) ───────────────
local function resolve_memory_api()
  if not memory then return false end
  mem.read_u8 = memory.read_u8 or memory.readbyte
  return mem.read_u8 ~= nil
end

local function read_u8(addr, domain)
  if not mem.read_u8 then return nil end
  if not ap_domain_ok(domain) then return nil end
  local ok, v = pcall(mem.read_u8, addr, domain)
  if ok and type(v) == "number" then return v end
  if _ap_two_arg == false then  -- old API: select the domain, then read
    if domain ~= nil and memory.usememorydomain
        and not pcall(memory.usememorydomain, domain) then return nil end
    ok, v = pcall(mem.read_u8, addr)
    if ok and type(v) == "number" then return v end
  end
  return nil
end

-- Big-endian 16-bit read (Genesis / 68000): high byte at the lower address.
local function read_u16_be(addr, domain)
  local hi = read_u8(addr, domain)
  local lo = read_u8(addr + 1, domain)
  if hi == nil or lo == nil then return nil end
  return hi * 256 + lo
end

-- ── ROM identity (Client.py validate_rom, 41-63) ──────────────────────────────
--   32 bytes at 0x150 must equal the exact international name, and the two
--   version bytes at 0x18C must be ASCII "00". Only a YES is cached
--   (reference: a NO measured before the core has the cartridge mapped must
--   not stick — re-checking a wrong ROM costs 34 byte-reads per poll).
local function rom_is_sitd()
  if rom_ok == true then return true end
  for i = 1, #ROM_NAME do
    local b = read_u8(ROM_NAME_ADDR + i - 1, CART)
    if b == nil or b ~= string.byte(ROM_NAME, i) then return false end
  end
  local v1 = read_u8(ROM_VER_ADDR, CART)
  local v2 = read_u8(ROM_VER_ADDR + 1, CART)
  if v1 ~= string.byte("0") or v2 ~= string.byte("0") then
    return false                       -- Client.py 55-57: REV00 only
  end
  rom_ok = true
  log("ROM identity confirmed: SHINING IN THE DARKNESS REV00")
  return true
end

-- ── Gameplay gate (Client.py not_in_game, 135-137) ────────────────────────────
--   Hero max HP == 0 → no save loaded (menus, boot, reset). The reference
--   client gates only its WRITES on this; here it gates poll() AND the goal,
--   because judging flags against unloaded work RAM is exactly the
--   false-GOAL-at-boot failure. Zeroed RAM passes no flag test anyway; this
--   gate is what rejects garbage that is not zero.
local function game_ready()
  local hp = read_u16_be(ADDR_HERO_MAX_HP, WORKRAM)
  -- 0xFFFF is rejected too: it is the classic uninitialised fill, and a
  -- 65535 max HP is not a value this game can produce — so the only thing
  -- the extra test can ever exclude is garbage. RESIDUAL RISK (documented,
  -- not hidden): garbage that lands between 1 and 0xFFFE with set flag bits
  -- would still be decoded; the published client has no stronger invariant
  -- (upstream runs location_check with no gate at all — this module is
  -- already stricter than its reference).
  return hp ~= nil and hp > 0 and hp < 0xFFFF
end

-- ── Server location set ───────────────────────────────────────────────────────
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

-- ── Contract ──────────────────────────────────────────────────────────────────
function M.init(ctx)
  if ctx and type(ctx.log) == "function" then log_fn = ctx.log end
  if not resolve_memory_api() then
    log("BizHawk memory API unavailable — module idle")
    ADDRESSES_VERIFIED = false
    return
  end
  local cfg = (ctx and ctx.config) or {}
  load_locations(cfg.locations)
  log("ready: " .. #LOCATIONS .. " location ids, 3 goal tables")
end

function M.poll()
  if not ADDRESSES_VERIFIED then return {} end
  if not rom_is_sitd() then return {} end
  if not game_ready() then return {} end

  -- One pass over the flag block, mirroring the client's single bulk read.
  local flags = {}
  for off = 0, FLAGS_LEN - 1 do
    local b = read_u8(ADDR_FLAGS + off, WORKRAM)
    if b == nil then return {} end     -- partial read proves nothing
    flags[off] = b
  end

  local new = {}
  for _, loc in ipairs(LOCATIONS) do
    if not reported[loc.id] and wanted(loc.id) then
      local b = flags[loc.addr - ADDR_FLAGS]
      -- Client.py 95: `byte & mask == mask` — ALL bits of the mask.
      if b ~= nil and bit_and(b, loc.mask) == loc.mask then
        reported[loc.id] = true
        new[#new + 1] = loc.id
      end
    end
  end
  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_sitd() then return false end
  if not game_ready() then return false end

  local goal_id = read_u8(ROM_GOAL_ADDR, CART)
  local goal = goal_id ~= nil and GOALS[goal_id] or nil
  if goal == nil then
    if not goal_warned then
      goal_warned = true
      log("goal byte " .. tostring(goal_id) .. " names no known goal (0..2)"
          .. " — an unpatched ROM, or a goal this table predates; the goal"
          .. " will never be reported")
    end
    return false
  end

  -- met_goal_check (Client.py 180-191): every completion location checked.
  for _, g in ipairs(goal) do
    local b = read_u8(g.addr, WORKRAM)
    if b == nil or bit_and(b, g.mask) ~= g.mask then return false end
  end
  return true
end

-- Remote items: see the file header. items_handling = 0b101 — own items come
-- from the patched ROM; items from other worlds are delivered by the reference
-- client through guarded writes (inventory scan + gold). That machinery is
-- deferred until it can be confirmed in-emulator; a wrong 68K RAM write
-- corrupts the save, so this is a no-op (never a wrong write) rather than
-- shipped unverified.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
