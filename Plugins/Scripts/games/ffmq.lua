-- ═══════════════════════════════════════════════════════════════════════════════
-- ffmq.lua — game module for the Archipelago BizHawk connector.
--           Final Fantasy Mystic Quest (SNES)
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the official
-- AP world worlds/ffmq (Client.py + Regions.py + Output.py + data/rooms.py, main
-- branch). The three location tables (chest/box, NPC, battlefield) were GENERATED
-- by PARSING the source — the exact `location_table` Regions.py builds plus the
-- explicit NPC_CHECKS dict and the chest/battlefield scan ranges from Client.py —
-- not hand-copied, so they are exact (266 locations: 230 chest/box containers +
-- 16 NPC + 20 battlefield). Loads crash-free on any ROM; self-disables on a
-- non-AP/non-FFMQ cartridge.
--
-- MEMORY MODEL (SNI → BizHawk)  — same translation the A Link to the Past module
-- documents (FFMQ's client is an SNIClient too):
-- ────────────────────────────
--   SNI WRAM_START(0xF50000)+off → domain "WRAM",    offset off (= $7E0000+off)
--   SNI SRAM_START(0xE00000)+off → domain "CARTRAM", offset off (fallback "SRAM")
--   The "MQ…" ROM title the client validates lives in the SNES internal header at
--   $00:FFC0 (System Bus) / file offset 0x7FC0 (LoROM CARTROM) — read like the
--   EarthBound module's "MOM2AP" probe, multi-domain with fallbacks.
--
-- WHAT THIS DOES (mirrors worlds/ffmq/Client.py game_watcher)
--   • poll(): the client's flag scan → AP location ids, gated to the slot's
--     server location set and to the in-game gate + the live-read consistency
--     check the client performs (read 6 bytes at WRAM 0x3749 before AND after
--     the data read; both must equal {01 'F' 'F' 'M' 'Q' 'R'} or the frame is
--     skipped — guards against reading a half-updated WRAM snapshot).
--       - Chests/Boxes: GAME_FLAGS bit (0x20*8)+container, MSB-first per byte.
--       - NPCs: explicit flag index, compared against the expected boolean.
--       - Battlefields: BATTLEFIELD_DATA[idx] == 0.
--   • is_goal_complete(): COMPLETED_GAME[0] & 0x80 AND GAME_FLAGS[30] & 0x18
--     (the client's finished-game condition; AP goal = defeat the Dark King).
--   • receive_item(): NO-OP for now (documented). items_handling = 0b001 means
--     the PATCHED GAME grants its own locally-found items, so a SOLO seed plays
--     fully and every check is reported. Delivering REMOTE multiworld items is
--     the client's CARTRAM hand-off (RECEIVED_DATA at $E0:1FF0: write {code, hi,
--     lo} only when the game has consumed the previous one). That single write
--     path needs in-emulator verification before it is wired — a wrong CARTRAM
--     write would corrupt the save — so it is intentionally left out rather than
--     shipped unverified, exactly like the other SNES modules in this wave.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "ffmq"

local ADDRESSES_VERIFIED = true   -- tables generated from worlds/ffmq source

-- ── Memory domains ────────────────────────────────────────────────────────────
local WRAM    = "WRAM"
local CARTRAM = "CARTRAM"     -- SNES battery SRAM in BizHawk; fallback "SRAM"

-- ── SNI-space constants (Client.py module top), as BizHawk-domain offsets ──────
-- WRAM offsets are net of WRAM_START (0xF50000); CARTRAM offsets net of 0xE00000.
local GAME_FLAGS_OFF   = 0x0EA8        -- WRAM: 64-byte game-flag array ($F50EA8)
local GAME_FLAGS_LEN   = 64
local COMPLETED_OFF    = 0x0F22        -- WRAM: COMPLETED_GAME byte ($F50F22)
local BATTLEFIELD_OFF  = 0x0FD4        -- WRAM: 20 battlefield bytes ($F50FD4)
local BATTLEFIELD_LEN  = 20
local VALIDATE_OFF     = 0x3749        -- WRAM: 6-byte live-read consistency probe
local RECEIVED_OFF     = 0x1FF0        -- CARTRAM: RECEIVED_DATA {flag, hi, lo} ($E01FF0)
local ITEM_CODE_START  = 0x420000      -- Client.py ITEM_CODE_START

-- Live-read consistency signature: {0x01, 'F','F','M','Q','R'}.
local VALIDATE_SIG = { 0x01, 0x46, 0x46, 0x4D, 0x51, 0x52 }

-- In-game flag index (Client.py IN_GAME_FLAG = (4*8)+2 = 34), MSB-first bit.
local IN_GAME_FLAG = 34
-- Goal: GAME_FLAGS byte 30 & 0x18, AND COMPLETED byte & 0x80.
local GOAL_FLAGS_BYTE = 30
local GOAL_FLAGS_MASK = 0x18
local GOAL_COMPLETED_MASK = 0x80

-- ROM identity: the FFMQ ROM title at the SNES internal header starts with "MQ"
-- (Client.py validate_rom: rom_name[:2] == b"MQ"). Header at $00:FFC0 / 0x7FC0.
local ROM_SIG          = "MQ"
local SIG_BUS_ADDR     = 0x00FFC0      -- "System Bus": the LoROM header on the bus
local SIG_CARTROM_LO   = 0x7FC0        -- "CARTROM"/"ROM": LoROM file header offset
local SIG_CARTROM_HI   = 0xFFC0        -- HiROM offset (last-ditch fallback)

-- ── Location tables (GENERATED from worlds/ffmq source; see file header) ───────
-- CHEST: 230 entries (containers with a real location). ap_id -> absolute flag index in GAME_FLAGS (MSB-first).
local LOC_CHEST = {
  [4325376]=256,[4325377]=257,[4325378]=258,[4325379]=259,[4325380]=260,[4325381]=261,[4325382]=262,
  [4325383]=263,[4325384]=264,[4325385]=265,[4325386]=266,[4325387]=267,[4325388]=268,[4325389]=269,
  [4325390]=270,[4325391]=271,[4325392]=272,[4325393]=273,[4325394]=274,[4325395]=275,[4325396]=276,
  [4325397]=277,[4325398]=278,[4325399]=279,[4325400]=280,[4325401]=281,[4325402]=282,[4325403]=283,
  [4325404]=284,[4325406]=286,[4325407]=287,[4325409]=289,[4325410]=290,[4325416]=296,[4325417]=297,
  [4325418]=298,[4325419]=299,[4325420]=300,[4325421]=301,[4325422]=302,[4325423]=303,[4325424]=304,
  [4325425]=305,[4325426]=306,[4325427]=307,[4325429]=309,[4325430]=310,[4325431]=311,[4325432]=312,
  [4325433]=313,[4325434]=314,[4325435]=315,[4325436]=316,[4325437]=317,[4325438]=318,[4325439]=319,
  [4325440]=320,[4325441]=321,[4325442]=322,[4325443]=323,[4325444]=324,[4325445]=325,[4325446]=326,
  [4325447]=327,[4325448]=328,[4325449]=329,[4325450]=330,[4325451]=331,[4325452]=332,[4325453]=333,
  [4325454]=334,[4325455]=335,[4325456]=336,[4325457]=337,[4325458]=338,[4325459]=339,[4325460]=340,
  [4325461]=341,[4325462]=342,[4325463]=343,[4325464]=344,[4325465]=345,[4325466]=346,[4325467]=347,
  [4325468]=348,[4325469]=349,[4325470]=350,[4325471]=351,[4325472]=352,[4325473]=353,[4325474]=354,
  [4325475]=355,[4325476]=356,[4325477]=357,[4325478]=358,[4325479]=359,[4325480]=360,[4325481]=361,
  [4325482]=362,[4325483]=363,[4325484]=364,[4325485]=365,[4325486]=366,[4325487]=367,[4325488]=368,
  [4325489]=369,[4325492]=372,[4325493]=373,[4325494]=374,[4325495]=375,[4325496]=376,[4325497]=377,
  [4325498]=378,[4325499]=379,[4325500]=380,[4325501]=381,[4325502]=382,[4325503]=383,[4325504]=384,
  [4325505]=385,[4325506]=386,[4325507]=387,[4325508]=388,[4325509]=389,[4325510]=390,[4325511]=391,
  [4325512]=392,[4325513]=393,[4325514]=394,[4325515]=395,[4325516]=396,[4325517]=397,[4325518]=398,
  [4325519]=399,[4325520]=400,[4325521]=401,[4325522]=402,[4325523]=403,[4325524]=404,[4325525]=405,
  [4325526]=406,[4325527]=407,[4325528]=408,[4325529]=409,[4325530]=410,[4325531]=411,[4325532]=412,
  [4325533]=413,[4325534]=414,[4325535]=415,[4325536]=416,[4325539]=419,[4325540]=420,[4325541]=421,
  [4325542]=422,[4325543]=423,[4325544]=424,[4325545]=425,[4325546]=426,[4325547]=427,[4325548]=428,
  [4325549]=429,[4325550]=430,[4325551]=431,[4325552]=432,[4325553]=433,[4325554]=434,[4325555]=435,
  [4325556]=436,[4325557]=437,[4325558]=438,[4325559]=439,[4325560]=440,[4325561]=441,[4325562]=442,
  [4325563]=443,[4325564]=444,[4325565]=445,[4325566]=446,[4325567]=447,[4325568]=448,[4325569]=449,
  [4325570]=450,[4325571]=451,[4325572]=452,[4325573]=453,[4325574]=454,[4325575]=455,[4325576]=456,
  [4325577]=457,[4325578]=458,[4325579]=459,[4325580]=460,[4325581]=461,[4325582]=462,[4325583]=463,
  [4325584]=464,[4325585]=465,[4325586]=466,[4325587]=467,[4325588]=468,[4325589]=469,[4325590]=470,
  [4325591]=471,[4325592]=472,[4325593]=473,[4325594]=474,[4325595]=475,[4325599]=479,[4325600]=480,
  [4325601]=481,[4325602]=482,[4325604]=484,[4325605]=485,[4325606]=486,[4325607]=487,[4325608]=488,
  [4325609]=489,[4325610]=490,[4325611]=491,[4325612]=492,[4325613]=493,[4325614]=494,[4325615]=495,
  [4325616]=496,[4325622]=502,[4325623]=503,[4325624]=504,[4325625]=505,[4325626]=506,
}

-- NPC: 16 entries. ap_id -> { flag_index, expected_bool }. Checked when get_flag(flags, idx) == expected.
local LOC_NPC = {
  [4325676]={52,false},[4325677]={30,true},[4325678]={201,true},[4325680]={208,true},[4325681]={234,true},
  [4325682]={206,false},[4325683]={235,true},[4325684]={239,true},[4325685]={238,false},[4325686]={233,true},
  [4325687]={209,true},[4325688]={116,true},[4325689]={237,false},[4325690]={236,true},[4325691]={232,true},
  [4325692]={210,true},
}

-- BATTLEFIELD: 20 entries. ap_id -> index into BATTLEFIELD_DATA (20 bytes). Checked when battlefield_data[idx] == 0.
local LOC_BF = {
  [4325727]=0,[4325728]=1,[4325729]=2,[4325730]=3,[4325731]=4,[4325732]=5,[4325733]=6,[4325734]=7,
  [4325735]=8,[4325736]=9,[4325737]=10,[4325738]=11,[4325739]=12,[4325740]=13,[4325741]=14,[4325742]=15,
  [4325743]=16,[4325744]=17,[4325745]=18,[4325746]=19,
}

-- ── Internal state ────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local items_received   = {}     -- ordered stream (extended ITEM lines)
local slot_number      = 0
local remote_items     = false
local rom_ok           = nil    -- cached "MQ" signature result
local sig_domain       = nil    -- {domain, addr} that yielded the signature
local mem              = {}
local log_fn           = nil

-- Per-poll snapshots, refreshed each frame.
local game_flags  = {}    -- byte index 0..63
local battlefield = {}    -- byte index 0..19
local completed   = nil   -- COMPLETED_GAME byte

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[ffmq] " .. tostring(msg)) end
end

-- ── Memory API (resolved at init; 2-arg domain form + current-domain fallback) ─
local function resolve_memory_api()
  if not memory then return false end
  mem.read_u8  = memory.read_u8     or memory.readbyte
  mem.write_u8 = memory.write_u8    or memory.writebyte
  return mem.read_u8 ~= nil
end

local function rd(addr, domain)
  if not mem.read_u8 then return nil end
  local ok, v = pcall(mem.read_u8, addr, domain)
  if ok and type(v) == "number" then return v end
  ok, v = pcall(mem.read_u8, addr)              -- older API: current domain
  if ok and type(v) == "number" then return v end
  return nil
end

local function read_u8(addr, domain) return rd(addr, domain or WRAM) end

-- Read from CARTRAM, falling back to the "SRAM" domain name on cores that use it.
local function read_cartram_u8(off)
  local ok, v = pcall(mem.read_u8, off, CARTRAM)
  if ok and type(v) == "number" then return v end
  ok, v = pcall(mem.read_u8, off, "SRAM")
  if ok and type(v) == "number" then return v end
  return nil
end

-- Read one byte trying a list of {domain, addr} pairs in order (for the ROM sig).
local function read_first(pairs_list)
  for _, p in ipairs(pairs_list) do
    local ok, v = pcall(mem.read_u8, p[2], p[1])
    if ok and type(v) == "number" then return v, p end
  end
  return nil
end

local function bit_and(a, b)
  -- 16-bit safe AND without the bit library (cores vary).
  local res, bitval = 0, 1
  while a > 0 and b > 0 do
    if a % 2 == 1 and b % 2 == 1 then res = res + bitval end
    a = math.floor(a / 2); b = math.floor(b / 2); bitval = bitval * 2
  end
  return res
end

-- get_flag(data, flag): byte = flag//8; bit = 0x80 >> (flag%8)  (MSB-first).
-- Returns true when the bit is set.
local POW2 = { [0]=1, 2, 4, 8, 16, 32, 64, 128 }
local function flag_set_msb(byte_array, flag)
  local b = byte_array[math.floor(flag / 8)]
  if b == nil then return false end
  local mask = 0x80 / POW2[flag % 8]            -- 0x80 >> (flag%8)
  return bit_and(b, mask) ~= 0
end

-- ── ROM identity: the FFMQ header title begins "MQ" ───────────────────────────
-- Tries System Bus 0x00FFC0 (LoROM header on the bus), then CARTROM/ROM LoROM and
-- HiROM offsets. Whichever domain yields "MQ" is cached and reused.
local function rom_is_ap()
  if rom_ok ~= nil then return rom_ok end
  local first, picked = read_first({
    { "System Bus", SIG_BUS_ADDR },
    { "CARTROM",    SIG_CARTROM_LO },
    { "ROM",        SIG_CARTROM_LO },
    { "CARTROM",    SIG_CARTROM_HI },
    { "ROM",        SIG_CARTROM_HI },
  })
  if first == nil then return false end           -- ROM not readable yet; retry
  if first ~= string.byte(ROM_SIG, 1) then
    rom_ok = false
    log("non-FFMQ/non-AP ROM (no 'MQ' header) — detection idle, no writes")
    return false
  end
  for i = 2, #ROM_SIG do
    local ok, b = pcall(mem.read_u8, picked[2] + i - 1, picked[1])
    if not ok or type(b) ~= "number" then return false end
    if b ~= string.byte(ROM_SIG, i) then
      rom_ok = false
      log("non-FFMQ/non-AP ROM (no 'MQ' header) — detection idle, no writes")
      return false
    end
  end
  rom_ok = true
  sig_domain = picked
  log(("FFMQ ROM verified ('MQ' header via %s 0x%X)"):format(picked[1], picked[2]))
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

-- Item-stream filter, same policy as the ALttP/EarthBound modules: in the default
-- 0b001 mode the patched game grants its own found items, so own-world items are
-- dropped unless the slot is in full remote mode; server/starting entries dropped.
local function accept_item(it)
  if it.player == nil then return true end          -- legacy ITEM:<id> line
  if it.player == 0 and (it.location or 0) <= 0 then return false end
  if (not remote_items) and it.player == slot_number then return false end
  return true
end

local function delivery_list()
  local out = {}
  for _, it in ipairs(items_received) do
    if accept_item(it) then out[#out + 1] = it end
  end
  return out
end

-- ── Live-read consistency gate (Client.py validate_read_state) ────────────────
-- The client reads the 6-byte signature at WRAM 0x3749 BEFORE and AFTER the data
-- read; both must equal VALIDATE_SIG, otherwise the WRAM snapshot is mid-update
-- and the frame is skipped. We approximate the same guard by reading it twice
-- around our own flag refresh.
local function validate_probe()
  for i = 1, 6 do
    if read_u8(VALIDATE_OFF + i - 1, WRAM) ~= VALIDATE_SIG[i] then return false end
  end
  return true
end

local function refresh_snapshot()
  for i = 0, GAME_FLAGS_LEN - 1 do game_flags[i]  = read_u8(GAME_FLAGS_OFF + i, WRAM) end
  for i = 0, BATTLEFIELD_LEN - 1 do battlefield[i] = read_u8(BATTLEFIELD_OFF + i, WRAM) end
  completed = read_u8(COMPLETED_OFF, WRAM)
end

-- In-game gate: GAME_FLAGS in-game flag (index 34) set.
local function in_gameplay()
  return flag_set_msb(game_flags, IN_GAME_FLAG)
end

local function scan_into(new)
  -- Chests / boxes: GAME_FLAGS bit (0x20*8)+container (MSB-first).
  for ap_id, flag in pairs(LOC_CHEST) do
    if not reported[ap_id] and wanted(ap_id) and flag_set_msb(game_flags, flag) then
      reported[ap_id] = true; new[#new + 1] = ap_id
    end
  end
  -- NPCs: explicit flag index, compared to the expected boolean.
  for ap_id, e in pairs(LOC_NPC) do
    if not reported[ap_id] and wanted(ap_id) and flag_set_msb(game_flags, e[1]) == e[2] then
      reported[ap_id] = true; new[#new + 1] = ap_id
    end
  end
  -- Battlefields: BATTLEFIELD_DATA[idx] == 0.
  for ap_id, idx in pairs(LOC_BF) do
    if not reported[ap_id] and wanted(ap_id) then
      local v = battlefield[idx]
      if v ~= nil and v == 0 then reported[ap_id] = true; new[#new + 1] = ap_id end
    end
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
  slot_number = tonumber(cfg.slot_number) or 0
  local sd = cfg.slot_data
  if type(sd) == "table" then
    remote_items = (sd.remote_items == true) or (tonumber(sd.remote_items) == 1)
  end
  load_locations(cfg.locations)
  local n = 0
  for _ in pairs(LOC_CHEST) do n = n + 1 end
  for _ in pairs(LOC_NPC)   do n = n + 1 end
  for _ in pairs(LOC_BF)    do n = n + 1 end
  log(("ready: slot #%d, remote_items=%s, %d location flags")
      :format(slot_number, tostring(remote_items), n))
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_ap() then return new end
  -- Consistency gate: probe, refresh, probe again — skip on any mismatch.
  if not validate_probe() then return new end
  refresh_snapshot()
  if not validate_probe() then return new end
  if not in_gameplay() then return new end
  scan_into(new)
  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_ap() then return false end
  if not validate_probe() then return false end
  refresh_snapshot()
  if not validate_probe() then return false end
  -- COMPLETED_GAME[0] & 0x80 AND GAME_FLAGS[30] & 0x18 (Client.py finished_game).
  if completed == nil or bit_and(completed, GOAL_COMPLETED_MASK) == 0 then return false end
  local gf30 = game_flags[GOAL_FLAGS_BYTE]
  return gf30 ~= nil and bit_and(gf30, GOAL_FLAGS_MASK) ~= 0
end

-- Remote multiworld items: see the file header. items_handling = 0b001 means the
-- patched game grants its own found items, so solo play and check reporting work
-- fully. Applying REMOTE items is the client's CARTRAM hand-off (write {code,hi,lo}
-- to RECEIVED_DATA at $E0:1FF0 only when RECEIVED_DATA[0] == 0, i.e. the game has
-- consumed the previous one; code = (item - ITEM_CODE_START) + 1, minus 256 when
-- > 256). That single guarded write is the one piece deferred until it can be
-- confirmed in-emulator. We record the stream (so a future wiring has it) but make
-- no write — never a wrong CARTRAM write — until then.
function M.receive_item(item_id, meta)
  if type(meta) == "table" then
    items_received[#items_received + 1] = {
      item     = tonumber(item_id),
      index    = tonumber(meta.index),
      player   = tonumber(meta.player),
      flags    = tonumber(meta.flags),
      location = tonumber(meta.locId),
    }
  else
    items_received[#items_received + 1] = { item = tonumber(item_id) }
  end
  -- intentionally no memory write (documented; deferred until in-emulator verified)
end

return M
