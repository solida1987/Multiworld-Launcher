-- ─────────────────────────────────────────────────────────────────────────────
--             Dragon Warrior (NES)  —  London RAM map
--
-- STATUS: SOURCE-DERIVED from the official AP world's own BizHawk client
--   (worlds/dragon_warrior/client.py, release read 2026-08-26). Every address,
--   domain, gate and id formula below is transcribed from that file — nothing
--   here was measured by hand or guessed.
--
-- MEMORY MODEL (BizHawk NES domains)
--   The client reads three domains, and they are NOT interchangeable:
--     "RAM"        zero-page/work RAM — map, inventory, level, flags
--     "System Bus" the CPU bus — chest state (0x601C) and the monster kill
--                  list (0x66C0) live in cartridge WRAM, only visible here
--     "PRG ROM"    the patch signature, for identifying an AP ROM
--
-- WHAT THIS DOES (mirrors client.py game_watcher)
--   Eight separate check families, each with its OWN id arithmetic. They do not
--   share a flag array, so there is no single flag_bit() shortcut:
--     1. chests    (map << 16) | (b0 << 8) | b1     — 8 slots at 0x601C
--     2. levels    a DECIMAL-IN-HEX string, see LEVEL_ID below
--     3. search    0xE00 | (ap_byte - 1)            — for 0x81/0x41/0x21
--     4. rainbow   0xFF                              — ap_byte == 0xFF
--     5. gwaelin   0x150513 (carried) / 0x050304 (returned)
--     6. equipment ap_byte itself, when it is one of EQUIPMENT_BYTES
--     7. monsters  a second decimal-in-hex string, see MONSTER_ID
--     8. goal      0xDD, on dragonlord & 0x04
--
-- ⚠⚠ TWO ID FORMULAS ARE DECIMAL DIGITS PASTED INTO A HEX LITERAL.
--   client.py builds the level id as the STRING "0xD" .. "0" .. str(level).
--   That is not 0xD0 + level: level 10 becomes the string "0xD10" = 3344,
--   while 0xD0 + 10 would be 3338. The same trick appears for monsters
--   ("0xDEF" .. hex(i)[2:]). Computing these arithmetically produces ids that
--   look entirely plausible and point at the wrong locations — the exact class
--   of error that mirrored CVLoD's checks. Both are reproduced literally here.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ─────────────────────────────────────────────────────────────────────────────

local M = {}
M.name = "dragon_warrior"

local ADDRESSES_VERIFIED = true   -- transcribed from worlds/dragon_warrior source

-- ── Domains ──────────────────────────────────────────────────────────────────
local RAM = "RAM"
local BUS = "System Bus"
local PRG = "PRG ROM"

-- ── Addresses (client.py game_watcher's single read() call) ──────────────────
local A_CURRENT_MAP   = 0x45      -- RAM  : 0 until a map is loaded
local A_CHESTS        = 0x601C    -- BUS  : 16 bytes, 8 chests x 2
local A_RECV_COUNT    = 0x0E      -- RAM  : items already granted
local A_INVENTORY     = 0xC1      -- RAM  : 4 bytes, two 4-bit slots each
local A_DRAGONLORD    = 0xE4      -- RAM  : & 0x04 = Dragonlord defeated
local A_LEVEL         = 0xC7      -- RAM
local A_AP_BYTE       = 0xB9      -- RAM  : search spots / equipment purchases
local A_STATUS        = 0xDF      -- RAM  : & 0x01 carrying Gwaelin, & 0x02 returned
local A_MONSTERS      = 0x66C0    -- BUS  : 80 bytes, 40 monsters x 2

local A_ROM_NAME      = 0x7FE0    -- PRG  : EXPECTED_ROM_NAME
local ROM_NAME        = "DWAPV"   -- client.py EXPECTED_ROM_NAME

local GOAL_LOCATION   = 0xDD      -- "Ball of Light" victory check
local DRAGONLORD_MASK = 0x04

local GWAELIN_CARRIED = 0x150513
local GWAELIN_LOVE    = 0x050304

-- client.py EQUIPMENT_BYTES
local EQUIPMENT = {
  [0x1]=true, [0x2]=true, [0x3]=true, [0x4]=true, [0x8]=true, [0xC]=true,
  [0x10]=true, [0x14]=true, [0x18]=true, [0x1C]=true, [0x20]=true, [0x40]=true,
  [0x60]=true, [0x80]=true, [0xA0]=true, [0xC0]=true, [0xE0]=true,
}
local SEARCH_BYTES = { [0x81]=true, [0x41]=true, [0x21]=true }

-- item classes, for the inventory writer (client.py)
local IMPORTANT = { [0x5]=true, [0x7]=true, [0x8]=true, [0xA]=true,
                    [0xC]=true, [0xD]=true, [0xE]=true }
local FILLER    = { [0x1]=true, [0x2]=true, [0x3]=true, [0x9]=true }
local USEFUL    = { [0x4]=true, [0x6]=true }

-- ── State ────────────────────────────────────────────────────────────────────
local mem = {}
local log_fn
local rom_ok
local reported = {}
local server_locations

local function log(msg)
  if log_fn then pcall(log_fn, "[dragon_warrior] " .. tostring(msg)) end
end

-- ── Domain guard ─────────────────────────────────────────────────────────────
--   A mistyped domain must fail LOUDLY. Silently falling back to whichever
--   domain the core has current would turn one wrong name into plausible reads
--   of the wrong memory -- and this module uses three domains that hold very
--   different things.
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
  if _ap_domains == nil then return true end
  if _ap_domains[domain] then return true end
  if not _ap_domain_warned[domain] then
    _ap_domain_warned[domain] = true
    log("memory domain '" .. tostring(domain) .. "' does not exist in this core"
        .. " -- access refused (never redirected to the current domain)")
  end
  return false
end

local function resolve_memory_api()
  if not memory then return false end
  mem.read_u8  = memory.read_u8  or memory.readbyte
  mem.write_u8 = memory.write_u8 or memory.writebyte
  return mem.read_u8 ~= nil
end

local function read_u8(addr, domain)
  if not mem.read_u8 then return nil end
  if not ap_domain_ok(domain) then return nil end
  local ok, v = pcall(mem.read_u8, addr, domain)
  if ok and type(v) == "number" then return v end
  if _ap_two_arg == false then
    if domain ~= nil and memory.usememorydomain
        and not pcall(memory.usememorydomain, domain) then return nil end
    ok, v = pcall(mem.read_u8, addr)
    if ok and type(v) == "number" then return v end
  end
  return nil
end

local function write_u8(addr, value, domain)
  if not mem.write_u8 then return false end
  if not ap_domain_ok(domain) then return false end
  local ok = pcall(mem.write_u8, addr, value, domain)
  if ok then return true end
  if _ap_two_arg == false then
    if domain ~= nil and memory.usememorydomain
        and not pcall(memory.usememorydomain, domain) then return false end
    return (pcall(mem.write_u8, addr, value)) and true or false
  end
  return false
end

local function bit_and(a, b)
  local res, bitval = 0, 1
  while a > 0 and b > 0 do
    if a % 2 == 1 and b % 2 == 1 then res = res + bitval end
    a = math.floor(a / 2); b = math.floor(b / 2); bitval = bitval * 2
  end
  return res
end

-- ⚠ See the header. client.py pastes DECIMAL digits into a HEX literal, so the
--   value is whatever that string parses to -- not an arithmetic offset.
local function level_id(level)
  local s = "0xD"
  if level < 10 then s = s .. "0" end
  return tonumber(s .. tostring(level))
end

local function monster_id(i)
  local s = "0xDEF"
  if i < 16 then s = s .. "0" end
  return tonumber(s .. string.format("%x", i))
end

-- ── ROM identity ─────────────────────────────────────────────────────────────
local function rom_is_ap()
  if rom_ok ~= nil then return rom_ok end
  for i = 1, #ROM_NAME do
    local b = read_u8(A_ROM_NAME + i - 1, PRG)
    if b == nil then return false end          -- not readable yet; retry
    if b ~= string.byte(ROM_NAME, i) then
      rom_ok = false
      log("non-AP ROM (no DWAPV signature) -- detection idle")
      return false
    end
  end
  rom_ok = true
  log("AP ROM verified (DWAPV signature present)")
  return true
end

-- ── Multiworld context ───────────────────────────────────────────────────────
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

-- ── Detection gate ───────────────────────────────────────────────────────────
--   client.py: `if current_map[0] == 0: return` -- nothing is processed until a
--   map is loaded. That is this game's readiness guard, and the goal check uses
--   the SAME one: at boot the PRG ROM signature is already valid while work RAM
--   is still garbage, and a stray bit at 0xE4 would read as a finished run.
--   (Pokemon Crystal, 19 Aug: a false goal at boot released 475 items.)
local function in_gameplay()
  local m = read_u8(A_CURRENT_MAP, RAM)
  return m ~= nil and m ~= 0
end

-- ── Module contract ──────────────────────────────────────────────────────────
function M.init(ctx)
  if ctx and type(ctx.log) == "function" then log_fn = ctx.log end
  if not resolve_memory_api() then
    log("BizHawk memory API unavailable -- module idle")
    ADDRESSES_VERIFIED = false
    return
  end
  local cfg = (ctx and ctx.config) or {}
  load_locations(cfg.locations)
  log("ready: 8 check families, source-derived from worlds/dragon_warrior")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_ap() then return new end
  if not in_gameplay() then return new end

  local current_map = read_u8(A_CURRENT_MAP, RAM)
  local function add(id)
    if id and not reported[id] and wanted(id) then
      reported[id] = true
      new[#new + 1] = id
    end
  end

  -- 1. chests on the CURRENT map: 8 slots of two bytes at 0x601C
  for i = 0, 14, 2 do
    local b0 = read_u8(A_CHESTS + i, BUS)
    local b1 = read_u8(A_CHESTS + i + 1, BUS)
    if b0 ~= nil and b1 ~= nil then
      add((current_map * 0x10000) + (b0 * 0x100) + b1)
    end
  end

  -- 2. levels reached
  local level = read_u8(A_LEVEL, RAM)
  if level ~= nil then
    for l = 1, level do add(level_id(l)) end
  end

  -- 3/4/6. the ap_byte families
  local ap_byte = read_u8(A_AP_BYTE, RAM)
  if ap_byte ~= nil then
    if SEARCH_BYTES[ap_byte] then add(0xE00 + (ap_byte - 1)) end
    if ap_byte == 0xFF then add(0xFF) end
    if EQUIPMENT[ap_byte] then add(ap_byte) end
  end

  -- 5. Gwaelin
  local status = read_u8(A_STATUS, RAM)
  if status ~= nil then
    if bit_and(status, 0x01) ~= 0 then add(GWAELIN_CARRIED) end
    if bit_and(status, 0x02) ~= 0 then add(GWAELIN_LOVE) end
  end

  -- 7. defeated monsters: 40 entries of two bytes at 0x66C0
  for i = 0, 78, 2 do
    local v = read_u8(A_MONSTERS + i, BUS)
    if v ~= nil and v > 0 then add(monster_id(i)) end
  end

  -- 8. goal check travels as a location too (client.py sends 0xDD)
  local dl = read_u8(A_DRAGONLORD, RAM)
  if dl ~= nil and bit_and(dl, DRAGONLORD_MASK) ~= 0 then add(GOAL_LOCATION) end

  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_ap() then return false end
  -- Same readiness guard as poll(). Never judge the goal on memory the game
  -- has not written yet.
  if not in_gameplay() then return false end
  local dl = read_u8(A_DRAGONLORD, RAM)
  return dl ~= nil and bit_and(dl, DRAGONLORD_MASK) ~= 0
end

-- ── Item delivery ────────────────────────────────────────────────────────────
--   items_handling = 0b111: the client grants EVERY item, including the
--   player's own, so this write path is the delivery -- not decoration. Without
--   it a seed reports checks and can never be finished.
--
--   The inventory is four bytes at 0xC1, each holding two 4-bit item slots
--   (high nibble first). client.py fills the first free nibble; if the bag is
--   full it evicts a filler item, and failing that a useful one. Quest items
--   (IMPORTANT) are always placed. 0x0E counts how many items the game has
--   already been given, and the client advances it by one per delivery.
--
--   ⚠ NOT YET SEEN IN A RUNNING GAME. The read path above is transcribed and
--   testable against synthetic memory; this write path changes save state and
--   must be watched once in an emulator before checks_verified can be claimed.
function M.receive_item(item_id, meta)
  if not ADDRESSES_VERIFIED or not rom_is_ap() then return end
  if not in_gameplay() then return end
  local item = tonumber(item_id)
  if item == nil then return end

  local slots = {}
  for i = 0, 3 do
    slots[i] = read_u8(A_INVENTORY + i, RAM)
    if slots[i] == nil then return end       -- incomplete read: never write
  end

  local function place(accept)
    for i = 0, 3 do
      local slot = slots[i]
      local hi = math.floor(slot / 16)
      local lo = slot % 16
      if accept(hi) then
        return i, (item * 16) + lo
      elseif accept(lo) then
        return i, (hi * 16) + item
      end
    end
    return nil
  end

  local idx, value = place(function(v) return v == 0 end)          -- free nibble
  if idx == nil and IMPORTANT[item] then
    idx, value = place(function(v) return FILLER[v] == true end)   -- evict filler
    if idx == nil then
      idx, value = place(function(v) return USEFUL[v] == true end) -- then useful
    end
  end
  if idx == nil then
    log("inventory full and nothing evictable -- item " .. item .. " not placed")
    return
  end

  if not write_u8(A_INVENTORY + idx, value, RAM) then
    log("inventory write failed -- item " .. item .. " not delivered")
    return
  end
  local n = read_u8(A_RECV_COUNT, RAM)
  if n ~= nil then write_u8(A_RECV_COUNT, (n + 1) % 256, RAM) end
end

return M
