-- ═══════════════════════════════════════════════════════════════════════════════
-- cvlod.lua — game module for the Archipelago BizHawk connector.
--             Castlevania: Legacy of Darkness (N64)
--
-- STATUS: location detection is SOURCE-DERIVED from the world's own client.py
-- and locations.py (LiquidCat64/LiquidCatipelago, cvlod v1.1). The 99-entry
-- table was GENERATED, not transcribed. Not yet measured in a running game:
-- the manifest keeps checks_verified=false and London says so.
--
-- MEMORY MODEL (BizHawk N64 domains)
-- ──────────────────────────────────
--   The client reads a 356-byte save structure at RDRAM 0x1CAA60 and treats
--   its first 0xB8 bytes as the location flag array:
--
--     ap_id       = BASE_ID + code          (BASE_ID = 0xC10D000)
--     checked when  FLAGS[code // 8] & (0x80 >> (code % 8))
--
--   ⚠ THE BIT ORDER IS THE OPPOSITE OF HARMONY OF DISSONANCE. That game packs
--   little-endian words, so bit 0 is the LOW bit; this one walks bytes with
--   `0x80 >> i`, so bit 0 is the HIGH bit. Copying the sibling module's
--   flag_bit() would have produced ids that look sane and are wrong -- each
--   byte's checks mirrored within it.
--
--   ⚠ locations.py describes the flag array as "starting from 0x80389BE4",
--   which is NOT where the client reads. The running code wins: it reads the
--   session copy at 0x1CAA60, and that is what is implemented here.
--
-- WHAT THIS DOES (mirrors cvlod/client.py game_watcher)
--   • poll(): scan the flag array → AP ids, gated to the slot's server
--     location set and to the game state, so a title screen reports nothing.
--   • is_goal_complete(): the credits state, or the ending flag the client
--     calls "found a hidden path" (0x18D) -- the same pair it sends the goal on.
--   • receive_item(): NO-OP, deliberately. The client delivers remote items by
--     writing a reward buffer and a textbox timer inside the save structure;
--     unverified writes there risk corrupting a save. items_handling 0b001
--     means the patched ROM grants the player's own items, so a solo seed
--     plays through and every check is still reported.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "cvlod"

local ADDRESSES_VERIFIED = true   -- table generated from the world's source

-- ── Memory domains ────────────────────────────────────────────────────────────
local RDRAM = "RDRAM"
local ROM   = "ROM"

-- ── Addresses / constants (cvlod client.py) ───────────────────────────────────
local GAME_STATE_ADDR   = 0x1CAA30    -- RDRAM: gameplay / credits / other
local SAVE_STRUCT_ADDR  = 0x1CAA60    -- RDRAM: 356-byte save structure
local FLAG_BYTES        = 0xB8        -- save_struct[0x00:0xB8] is the flag array
local GAME_STATE_PLAY   = 0x03
local GAME_STATE_CREDITS = 0x0A
local BASE_ID           = 0xC10D000
local FLAG_ENDING       = 0x18D       -- "found a hidden path" -- the client's goal flag

-- ROM identity. Two separate questions: is this the right GAME, and has it
-- been patched? An unpatched cartridge leaves the AP block all zeroes.
local ROM_NAME_ADDR = 0x20
local ROM_NAME      = "CASTLEVANIA2        "
local AP_BLOCK_ADDR = 0xFFBFD0
local AP_BLOCK_LEN  = 12

-- ── Location table (GENERATED from cvlod/locations.py) ────────────────────────
-- ap_id -> flag code. 99 entries.
local LOC = {
  [202428461]=45,[202428462]=46,[202428463]=47,[202428464]=48,[202428482]=66,[202428484]=68,
  [202428486]=70,[202428487]=71,[202428488]=72,[202428489]=73,[202428492]=76,[202428493]=77,
  [202428494]=78,[202428506]=90,[202428507]=91,[202428508]=92,[202428510]=94,[202428511]=95,
  [202428512]=96,[202428513]=97,[202428514]=98,[202428515]=99,[202428516]=100,[202428517]=101,
  [202428520]=104,[202428524]=108,[202428525]=109,[202428526]=110,[202428527]=111,
  [202428528]=112,[202428529]=113,[202428530]=114,[202428531]=115,[202428532]=116,
  [202428533]=117,[202428536]=120,[202428542]=126,[202428543]=127,[202428544]=128,
  [202428545]=129,[202428546]=130,[202428547]=131,[202428548]=132,[202428549]=133,
  [202428550]=134,[202428551]=135,[202428555]=139,[202428556]=140,[202428557]=141,
  [202428558]=142,[202428559]=143,[202428560]=144,[202428572]=156,[202428573]=157,
  [202428574]=158,[202428575]=159,[202428576]=160,[202428577]=161,[202428578]=162,
  [202428579]=163,[202428580]=164,[202428581]=165,[202428582]=166,[202428583]=167,
  [202428584]=168,[202428585]=169,[202428586]=170,[202428587]=171,[202428588]=172,
  [202428818]=402,[202428819]=403,[202428820]=404,[202428821]=405,[202428822]=406,
  [202428823]=407,[202429053]=637,[202429054]=638,[202429055]=639,[202429056]=640,
  [202429057]=641,[202429059]=643,[202429105]=689,[202429106]=690,[202429107]=691,
  [202429112]=696,[202429113]=697,[202429114]=698,[202429115]=699,[202429116]=700,
  [202429117]=701,[202429118]=702,[202429119]=703,[202429120]=704,[202429121]=705,
  [202429186]=770,[202429187]=771,[202429188]=772,[202429189]=773,[202429190]=774,
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}
local server_locations = nil
local rom_ok           = nil
local mem              = {}
local log_fn           = nil

local function log(msg)
  if log_fn then pcall(log_fn, "[cvlod] " .. tostring(msg)) end
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

-- ── Memory API (resolved at init; 2-arg domain form + current-domain fallback) ─
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

local function bit_and(a, b)
  local res, bitval = 0, 1
  while a > 0 and b > 0 do
    if a % 2 == 1 and b % 2 == 1 then res = res + bitval end
    a = math.floor(a / 2); b = math.floor(b / 2); bitval = bitval * 2
  end
  return res
end

local POW2 = { [0]=1, 2, 4, 8, 16, 32, 64, 128 }

-- ── ROM identity ──────────────────────────────────────────────────────────────
local function rom_is_ap()
  if rom_ok ~= nil then return rom_ok end
  for i = 1, #ROM_NAME do
    local b = read_u8(ROM_NAME_ADDR + i - 1, ROM)
    if b == nil then return false end          -- not readable yet; retry next poll
    if b ~= string.byte(ROM_NAME, i) then
      rom_ok = false
      log("not Legend of Darkness (ROM name mismatch) -- detection idle")
      return false
    end
  end
  -- Right game, but an unpatched cartridge leaves the AP block zeroed, and its
  -- flags would be somebody else's save layout.
  local any = false
  for i = 0, AP_BLOCK_LEN - 1 do
    local b = read_u8(AP_BLOCK_ADDR + i, ROM)
    if b == nil then return false end
    if b ~= 0 then any = true end
  end
  if not any then
    rom_ok = false
    log("unpatched ROM (Archipelago block is empty) -- detection idle")
    return false
  end
  rom_ok = true
  log("AP ROM verified (CASTLEVANIA2 + patch block present)")
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

-- ── Flag array (read once per poll) ───────────────────────────────────────────
local flags = {}

local function refresh_flags()
  for i = 0, FLAG_BYTES - 1 do
    flags[i] = read_u8(SAVE_STRUCT_ADDR + i, RDRAM)
  end
end

--- ⚠ MSB-first: the client tests `byte & (0x80 >> i)`, so flag 0 is the TOP
--- bit of byte 0. The sibling Castlevania module counts the other way.
local function flag_bit(code)
  local byte = flags[math.floor(code / 8)]
  if byte == nil then return false end
  return bit_and(byte, POW2[7 - (code % 8)]) ~= 0
end

-- ── Detection gate ────────────────────────────────────────────────────────────
local function in_gameplay()
  local s = read_u8(GAME_STATE_ADDR, RDRAM)
  return s == GAME_STATE_PLAY or s == GAME_STATE_CREDITS
end

-- ── Module contract ───────────────────────────────────────────────────────────
function M.init(ctx)
  if ctx and type(ctx.log) == "function" then log_fn = ctx.log end
  if not resolve_memory_api() then
    log("BizHawk memory API unavailable -- module idle")
    ADDRESSES_VERIFIED = false
    return
  end
  local cfg = (ctx and ctx.config) or {}
  load_locations(cfg.locations)
  local n = 0; for _ in pairs(LOC) do n = n + 1 end
  log("ready: " .. n .. " location flags")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_ap() then return new end
  refresh_flags()
  if not in_gameplay() then return new end
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
  -- ⚠ The same readiness guard poll() uses. Without it the goal is judged on
  -- memory the game has not written yet: at boot the ROM is already valid
  -- while RDRAM is still garbage, so a stray bit reads as a finished run.
  -- That fired on Pokemon Crystal on 19 Aug -- the server took the goal,
  -- auto-collected, and released all 475 remaining items one second after the
  -- game started.
  if not in_gameplay() then return false end
  -- Past the gate the credits state IS the ending, so no flag is needed.
  if read_u8(GAME_STATE_ADDR, RDRAM) == GAME_STATE_CREDITS then return true end
  refresh_flags()
  return flag_bit(FLAG_ENDING)
end

-- Deliberately inert -- see the header.
function M.receive_item(_item_id, _meta) end

return M
