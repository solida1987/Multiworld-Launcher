-- ═══════════════════════════════════════════════════════════════════════════════
-- alttp.lua — game module for the Archipelago BizHawk connector.
--           A Link to the Past (SNES)
--
-- STATUS: REAL implementation, SOURCE-DERIVED from the official AP 0.6.7
-- worlds/alttp/Client.py (an SNIClient subclass). Addresses + masks + the four
-- location tables were read directly out of the compiled client; the full
-- derivation + citations live in Research_V2/SNES_BRIDGE_2026-06-12.md.
-- Loads and runs crash-free on any ROM; self-disables on a non-AP cartridge.
--
-- MEMORY MODEL (SNI → BizHawk)
-- ────────────────────────────
--   The alttp client is an SNI game. SNI addresses translate to BizHawk SNES
--   memory domains:
--     SNI WRAM_START(0xF50000)+off  → domain "WRAM",    offset off (= $7E0000+off)
--     SNI SRAM_START(0xE00000)+off  → domain "CARTRAM", offset off (fallback "SRAM")
--   SAVEDATA mirror is in WRAM at offset 0xF000 ($7EF000). The AP "AP" signature
--   the client validates is in CARTRAM at 0x2000.
--
-- WHAT THIS DOES (mirrors worlds/alttp/Client.py)
--   • poll(): the client's track_locations scan — underworld room bits,
--     overworld event bits, NPC flag bits, misc bits → AP location ids, gated
--     to the slot's server location set (only those are reported).
--   • receive_item(): the RECV_ITEM/RECV_ITEM_PLAYER write the client uses; the
--     patched game owns RECV_PROGRESS and bumps it after consuming, so we only
--     write the next item when the game's counter has caught up.
--   • is_goal_complete(): game mode ∈ ENDGAME_MODES {0x19,0x1A} (credits).
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "alttp"

-- Everything below is verified against worlds/alttp/Client.py (see the research
-- doc). The tables are absolute AP location ids — no per-slot base offset is
-- needed — so detection is fully self-contained and this stays true.
local ADDRESSES_VERIFIED = true

-- ── Memory domains ────────────────────────────────────────────────────────────
local WRAM    = "WRAM"
local CARTRAM = "CARTRAM"     -- SNES battery SRAM in BizHawk; fallback "SRAM"

-- ── SNI-space constants (Client.py module top) ───────────────────────────────
-- Expressed as BizHawk-domain offsets (already net of WRAM_START/SRAM_START).
local GAME_MODE_OFF   = 0x0010            -- WRAM: main module byte ($7E0010)
local SAVEDATA_OFF    = 0xF000            -- WRAM: SRAM mirror base ($7EF000)
local ROMNAME_OFF     = 0x2000            -- CARTRAM: "AP" signature (SRAM 0x2000)

local RECV_PROGRESS   = SAVEDATA_OFF + 0x4D0   -- u16: items the game has taken
local RECV_ITEM       = SAVEDATA_OFF + 0x4D2   -- u8 : item id to grant
local RECV_ITEM_PLAYER= SAVEDATA_OFF + 0x4D3   -- u8 : sending player (0 = self)

-- Sub-table region bases inside SAVEDATA (Client.py track_locations).
local OW_BASE  = SAVEDATA_OFF + 0x280     -- overworld event bytes; idx=screen_id
local OW_MASK  = 0x40
local NPC_BASE = SAVEDATA_OFF + 0x410     -- NPC flags, 16-bit value

-- Game modes.
local INGAME_MODES  = { [0x07]=true, [0x09]=true, [0x0B]=true }
local ENDGAME_MODES = { [0x19]=true, [0x1A]=true }

-- ── Location tables (dumped from Client.py; see research doc §2c) ──────────────
-- Underworld / dungeon rooms: ap_id -> { room_id, mask16 }. 220 entries.
local LOC_UW = {
  [60175]={285,16},[60178]={285,32},[60181]={285,64},[60184]={285,128},[60187]={285,256},[59761]={85,16},[59824]={276,16},[59857]={276,32},[59770]={275,16},[59788]={267,16},[59836]={260,16},[59854]={259,16},[59881]={264,16},[59890]={266,16},[60034]={261,16},[60037]={261,32},[60040]={261,64},[60046]={47,16},[60049]={47,32},[60052]={47,64},[60055]={47,128},[60058]={47,256},[1572864]={225,512},[1572865]={226,512},[1572867]={283,1024},[1572868]={283,512},[1572869]={294,512},[60226]={291,16},[60229]={291,32},[60232]={291,64},[60235]={291,128},[1572880]={291,1024},[60238]={288,16},[60223]={292,16},[59791]={115,16},[1573216]={115,1024},[59830]={116,16},[59851]={133,16},[59842]={117,16},[1310769]={99,1024},[1310763]={83,1024},[1310760]={67,1024},[1573201]={51,2048},[59767]={168,16},[59773]={169,16},[1310811]={186,1024},[1310793]={153,1024},[59827]={185,16},[59833]={184,16},[59893]={170,16},[1573200]={200,2048},[59764]={113,16},[1310772]={113,1024},[60172]={114,16},[1310775]={114,1024},[60169]={128,16},[1310781]={128,1024},[59758]={50,16},[1310733]={33,1024},[60253]={17,16},[60256]={17,32},[60259]={17,64},[60025]={18,16},[60085]={224,16},[60082]={208,16},[1310817]={192,1024},[1310802]={176,1024},[1572866]={234,1024},[60202]={239,16},[60205]={239,32},[60208]={239,64},[60211]={239,128},[60214]={239,256},[60217]={255,16},[60220]={255,32},[59839]={254,16},[1573218]={135,1024},[59821]={119,16},[59878]={135,16},[59899]={39,32},[59896]={39,16},[1573202]={7,2048},[60190]={286,16},[60193]={286,32},[60196]={286,64},[60199]={286,128},[1572881]={286,1024},[1572870]={295,1024},[59776]={278,16},[59779]={278,32},[59884]={262,16},[59887]={284,16},[60840]={262,1024},[60019]={269,16},[60022]={269,32},[60028]={248,16},[60031]={248,32},[60043]={279,16},[60241]={60,16},[60244]={60,32},[60250]={60,128},[60247]={60,64},[59845]={268,16},[60061]={40,16},[59782]={55,16},[1310745]={56,1024},[1310742]={55,1024},[1310739]={54,1024},[59785]={54,16},[60064]={70,16},[1310736]={53,1024},[60070]={53,16},[60067]={52,16},[60073]={118,16},[60076]={118,32},[60079]={102,16},[1310730]={22,1024},[1573204]={6,2048},[59908]={219,32},[59905]={219,16},[59911]={220,16},[59914]={203,16},[1310814]={188,1024},[1310799]={171,1024},[59917]={101,16},[59920]={68,16},[59923]={69,16},[1573206]={172,2048},[59794]={103,16},[59803]={88,32},[59800]={88,16},[59809]={87,32},[59848]={104,16},[59806]={87,16},[1310766]={86,1024},[59902]={89,16},[1310748]={57,1024},[1573205]={41,2048},[1310724]={14,1024},[59860]={46,16},[1310754]={62,1024},[59797]={126,16},[59818]={158,16},[59875]={174,16},[1310790]={159,1024},[59872]={95,16},[59812]={31,16},[1310757]={63,1024},[59869]={63,16},[1573207]={222,2048},[60007]={195,16},[60010]={195,32},[59998]={194,16},[60001]={162,16},[1310805]={179,1024},[59866]={179,16},[1310796]={161,1024},[1310820]={193,1024},[60004]={193,16},[60013]={209,16},[1573208]={144,2048},[59938]={214,16},[59932]={183,16},[59935]={183,32},[1310808]={182,1024},[59926]={182,16},[1310727]={19,1024},[59941]={20,16},[59929]={36,16},[59956]={4,16},[59953]={213,128},[59950]={213,64},[59947]={213,32},[59944]={213,16},[1573209]={164,2048},[59995]={9,16},[59965]={42,32},[59977]={10,16},[59959]={58,16},[59962]={42,16},[59986]={43,16},[59971]={26,32},[59980]={106,16},[59983]={106,32},[59989]={25,16},[59992]={25,32},[59968]={26,16},[59974]={26,64},[1573203]={90,2048},[1310784]={139,1024},[1573217]={140,1024},[60121]={140,32},[60124]={140,64},[60130]={141,16},[60133]={157,16},[60136]={157,32},[60139]={157,64},[60142]={157,128},[1310778]={123,1024},[60088]={123,16},[60091]={123,32},[60094]={123,64},[60097]={123,128},[60115]={139,16},[1310787]={155,1024},[60112]={125,16},[60100]={124,16},[60103]={124,32},[60106]={124,64},[60109]={124,128},[60127]={140,128},[60118]={140,16},[60148]={28,32},[60151]={28,64},[60145]={28,16},[60157]={61,16},[60160]={61,32},[1310751]={61,1024},[60163]={61,64},[60166]={77,16},
}
-- Overworld: ap_id -> screen_id. Byte at OW_BASE+screen, check & 0x40.
local LOC_OW = {
  [1573194]=42,[1573189]=59,[1573193]=129,[1573188]=53,[1573186]=40,[1573187]=48,[166320]=128,[1573184]=3,[1573191]=91,[1573192]=104,[1573190]=74,[1573185]=5,
}
-- NPC flags: ap_id -> mask16. 16-bit value at NPC_BASE, check & mask.
local LOC_NPC = {
  [1572883]=4096,[975299]=2,[193020]=16,[1572906]=1024,[1572885]=32768,[211407]=4,[1572882]=128,[1572884]=8192,[1010170]=1,[1572886]=256,[975237]=32,[209095]=8,[1572887]=512,
}
-- Misc: ap_id -> { savedata_byte_off, mask8 }. Byte at SAVEDATA+off, check & mask.
local LOC_MISC = {
  [191256]={969,2},[212328]={969,16},[188229]={966,1},[212605]={969,1},
}

-- ── Internal state ────────────────────────────────────────────────────────────
local reported        = {}     -- ap_id -> true once returned from poll()
local server_locations= nil     -- set of ap_ids the server expects (nil = all)
local items_received  = {}     -- ordered stream (extended ITEM lines)
local slot_number     = 0
local remote_items    = false
local rom_ok          = nil     -- cached "AP" signature result (nil until checked)
local mem             = {}
local log_fn          = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[alttp] " .. tostring(msg)) end
end

-- ── Memory API (resolved at init; 2-arg domain form + current-domain fallback) ─
local function resolve_memory_api()
  if not memory then return false end
  mem.read_u8   = memory.read_u8     or memory.readbyte
  mem.read_u16  = memory.read_u16_le or memory.readword
  mem.write_u8  = memory.write_u8    or memory.writebyte
  mem.write_u16 = memory.write_u16_le or memory.writeword
  return mem.read_u8 ~= nil
end

local function rd(fn, addr, domain)
  if not fn then return nil end
  local ok, v = pcall(fn, addr, domain)
  if ok and type(v) == "number" then return v end
  ok, v = pcall(fn, addr)                       -- older API: current domain
  if ok and type(v) == "number" then return v end
  return nil
end

local function read_u8(addr, domain)  return rd(mem.read_u8,  addr, domain or WRAM) end
local function read_u16(addr, domain) return rd(mem.read_u16, addr, domain or WRAM) end

local function write_u8(addr, value, domain)
  if not mem.write_u8 then return end
  if pcall(mem.write_u8, addr, value, domain or WRAM) then return end
  pcall(mem.write_u8, addr, value)
end

-- Read from CARTRAM, falling back to the "SRAM" domain name on cores that use it.
local function read_cartram_u8(off)
  local ok, v = pcall(mem.read_u8, off, CARTRAM)
  if ok and type(v) == "number" then return v end
  ok, v = pcall(mem.read_u8, off, "SRAM")
  if ok and type(v) == "number" then return v end
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

-- ── ROM identity: the AP basepatch writes "AP" at CARTRAM 0x2000 ──────────────
local function rom_is_ap()
  if rom_ok ~= nil then return rom_ok end
  local a = read_cartram_u8(ROMNAME_OFF)
  local p = read_cartram_u8(ROMNAME_OFF + 1)
  if a == nil or p == nil then return false end          -- not readable yet; retry
  rom_ok = (a == string.byte("A") and p == string.byte("P"))
  if rom_ok then log("AP ROM verified ('AP' signature present)")
  else log("non-AP ROM (no 'AP' signature) — detection idle, no writes") end
  return rom_ok
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

-- Item-stream filter, identical policy to the Emerald module: the patched game
-- grants its own found items (items_handling=1), so own-world items are dropped
-- unless remote_items, and server/starting-inventory entries are dropped.
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

-- ── Detection helpers ─────────────────────────────────────────────────────────
local function game_mode() return read_u8(GAME_MODE_OFF) end

local function in_gameplay()
  local m = game_mode()
  return m ~= nil and INGAME_MODES[m] == true
end

local function scan_into(new)
  -- Underworld rooms: 2 bytes at SAVEDATA + room*2, check 16-bit & mask.
  for ap_id, rm in pairs(LOC_UW) do
    if not reported[ap_id] and wanted(ap_id) then
      local v = read_u16(SAVEDATA_OFF + rm[1] * 2)
      if v and bit_and(v, rm[2]) ~= 0 then reported[ap_id] = true; new[#new+1] = ap_id end
    end
  end
  -- Overworld: 1 byte at OW_BASE + screen, check & 0x40.
  for ap_id, screen in pairs(LOC_OW) do
    if not reported[ap_id] and wanted(ap_id) then
      local b = read_u8(OW_BASE + screen)
      if b and bit_and(b, OW_MASK) ~= 0 then reported[ap_id] = true; new[#new+1] = ap_id end
    end
  end
  -- NPC flags: 16-bit value at NPC_BASE, check & mask.
  local npc = read_u16(NPC_BASE)
  if npc then
    for ap_id, mask in pairs(LOC_NPC) do
      if not reported[ap_id] and wanted(ap_id) and bit_and(npc, mask) ~= 0 then
        reported[ap_id] = true; new[#new+1] = ap_id
      end
    end
  end
  -- Misc: 1 byte at SAVEDATA + off, check & mask.
  for ap_id, am in pairs(LOC_MISC) do
    if not reported[ap_id] and wanted(ap_id) then
      local b = read_u8(SAVEDATA_OFF + am[1])
      if b and bit_and(b, am[2]) ~= 0 then reported[ap_id] = true; new[#new+1] = ap_id end
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
  slot_number  = tonumber(cfg.slot_number) or 0
  local sd = cfg.slot_data
  if type(sd) == "table" then
    remote_items = (sd.remote_items == true) or (tonumber(sd.remote_items) == 1)
  end
  load_locations(cfg.locations)
  log(("ready: slot #%d, remote_items=%s, %d location flags")
      :format(slot_number, tostring(remote_items),
              -- count of embedded location entries
              (function() local n=0 for _ in pairs(LOC_UW) do n=n+1 end
                 for _ in pairs(LOC_OW) do n=n+1 end
                 for _ in pairs(LOC_NPC) do n=n+1 end
                 for _ in pairs(LOC_MISC) do n=n+1 end return n end)()))
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_ap() then return new end
  if not in_gameplay() then return new end
  scan_into(new)
  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_ap() then return false end
  local m = game_mode()
  return m ~= nil and ENDGAME_MODES[m] == true
end

-- Record the grant, then push the next pending item to the game. The game owns
-- RECV_PROGRESS and bumps it once it consumes an item; we only write when its
-- counter has caught up to what we've already delivered, mirroring the client.
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

  if not ADDRESSES_VERIFIED or not rom_is_ap() or not in_gameplay() then return end

  local list = delivery_list()
  local count = read_u16(RECV_PROGRESS)
  if count == nil then return end
  if count < #list then
    local nxt = list[count + 1]
    if nxt and nxt.item then
      -- ALttP item ids are a single byte in the receive slot.
      write_u8(RECV_ITEM_PLAYER, (nxt.player and nxt.player ~= slot_number) and 1 or 0)
      write_u8(RECV_ITEM, bit_and(nxt.item, 0xFF))
      -- The game advances RECV_PROGRESS itself after pickup; the next poll/grant
      -- feeds list[count+2], and so on.
    end
  end
end

return M
