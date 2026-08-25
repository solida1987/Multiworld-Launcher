-- A Link Between Worlds -- launcher-side AP logic module (3DS over Azahar).
--
-- Runs in London's Snes9xLuaBridge in PASSTHROUGH mode: every read is a live
-- UDP exchange with Azahar's scripting server, addresses are the 3DS
-- process's own virtual addresses, and there is no WRAM snapshot.
--
-- PROVENANCE. The address map and watcher logic are ported from the ALBW
-- apworld's own client (albw/Client.py + Citra.py, GPL-2.0 -- the licence is
-- the permission); the location/item tables below are extracted from the
-- world's Locations.py and its native albwrandomizer library, not typed by
-- hand. Data version 2. Base id 6242624000.
--
-- THE CONTRACT WITH THE GAME. The seed patch plants an "ARCH" header block
-- in the game at 0x6fe5f8:
--   +0x00 "ARCH"      +0x04 data version (== 2)   +0x08 seed
--   +0x0c item mailbox (0xffffffff = empty)       +0x10 slot name (0x40)
--   +0x50 items-received count (game-owned)       +0x54 framework ptr
-- Checks are event/course/minigame flags, each read from BOTH the live
-- buffers and the save block -- a flag counts when either copy has it.
-- Delivery is one item at a time: when the game's own received count is
-- behind the server stream and the mailbox reads empty, the next item's
-- in-game id is written into the mailbox; the game consumes it and advances
-- its count itself. When no save is loaded the mailbox is parked at
-- 0xffffffff so a menu never eats an item.
--
-- HONEST GAP (v1): the world's client also fires LocationScouts for Ravio's
-- shop so the shop shows what its slots hold before purchase. That is a hint
-- QoL, not sync -- deliberately not ported yet.

local M = {}

local AP_HEADER   = 0x6fe5f8
local SAVES       = 0x711de8
local EVENTS      = 0x70b728
local COURSES     = 0x70c8e0
local MINIGAME    = 0x70d858
local MAIN_GAME_VTABLE = 0x6d1db4   -- the in-game task's vtable address
local DATA_VERSION = 2
local BASE_ID      = 6242624000
local GOAL_FLAG    = 685            -- event flag set by the credits trigger
local EMPTY        = 0xffffffff

-- Locations: c = AP code (AP id = BASE_ID + c), co = course (absent = event
-- flag), f = flag. "Hyrule Hotfoot 75s" is minigame bit 0, not a flag.
local locations = {
  {c=0,f=85},{c=1,co=2,f=249},{c=2,co=2,f=251},{c=3,co=2,f=250},
  {c=4,co=2,f=242},{c=5,co=2,f=244},{c=6,co=2,f=247},{c=7,co=2,f=246},
  {c=8,co=2,f=243},{c=9,co=2,f=248},{c=10,f=57},{c=11,f=181},{c=12,co=0,f=63},
  {c=13,f=971},{c=14,f=992},{c=15,f=913},{c=16,co=0,f=189},{c=17,co=0,f=159},
  {c=18,f=916},{c=19,hot=true},{c=20,f=969},{c=21,f=946},{c=22,f=945},
  {c=23,f=209},{c=24,f=963},{c=25,f=972},{c=26,f=980},{c=27,co=0,f=306},
  {c=28,co=0,f=267},{c=29,co=0,f=268},{c=30,co=0,f=275},{c=31,f=970},
  {c=32,co=0,f=259},{c=33,co=0,f=269},{c=34,co=0,f=270},{c=35,co=0,f=277},
  {c=36,co=0,f=305},{c=37,co=0,f=278},{c=38,co=0,f=279},{c=39,co=0,f=280},
  {c=40,co=0,f=281},{c=41,co=0,f=282},{c=42,co=0,f=291},{c=43,co=0,f=292},
  {c=44,co=0,f=283},{c=45,co=0,f=284},{c=46,co=0,f=285},{c=47,co=0,f=286},
  {c=48,co=0,f=287},{c=49,co=0,f=288},{c=50,co=0,f=289},{c=51,co=0,f=290},
  {c=52,co=0,f=297},{c=53,co=0,f=296},{c=54,co=0,f=298},{c=55,co=0,f=304},
  {c=56,co=0,f=300},{c=57,co=0,f=301},{c=58,co=0,f=302},{c=59},{c=60},{c=61},
  {c=62},{c=63},{c=64},{c=65},{c=66},{c=67},{c=68},{c=69,co=2,f=5},
  {c=70,co=0,f=276},{c=71,co=0,f=272},{c=72,co=0,f=295},{c=73,co=22,f=2},
  {c=74,co=0,f=273},{c=75,co=0,f=2},{c=76,co=0,f=5},{c=77,co=0,f=3},
  {c=78,f=213},{c=79,co=0,f=145},{c=80,co=0,f=266},{c=81,co=0,f=274},
  {c=82,f=978},{c=83,co=22,f=1},{c=84,f=979},{c=85,co=0,f=299},{c=86,f=261},
  {c=87,co=2,f=142},{c=88,co=4,f=134},{c=89,co=22,f=132},{c=90,co=0,f=271},
  {c=91,f=990},{c=92,f=37},{c=93,f=885},{c=94,f=981},{c=95,f=101},
  {c=96,co=2,f=192},{c=97,co=0,f=51},{c=98,f=976},{c=99,co=0,f=133},
  {c=100,co=0,f=258},{c=101,co=0,f=257},{c=102,co=0,f=150},{c=103,f=889},
  {c=104,co=2,f=2},{c=105,f=977},{c=106,co=4,f=138},{c=107,f=974},
  {c=108,co=2,f=48},{c=109,f=912},{c=110,co=4,f=168},{c=111,co=22,f=128},
  {c=112,co=0,f=303},{c=113,f=983},{c=114,co=4,f=143},{c=115,co=4,f=141},
  {c=116,co=0,f=265},{c=117,co=0,f=261},{c=118,co=4,f=131},{c=119,co=4,f=130},
  {c=120,co=4,f=133},{c=121,co=4,f=128},{c=122,co=0,f=128},{c=123,co=0,f=260},
  {c=124,co=4,f=129},{c=125,f=975},{c=126,co=0,f=264},{c=127,co=22,f=130},
  {c=128,f=995},{c=129,co=0,f=262},{c=130,f=947},{c=131,f=947},
  {c=132,co=0,f=263},{c=133,f=982},{c=134,co=4,f=10},{c=135,co=4,f=12},
  {c=136,co=4,f=11},{c=137,f=973},{c=138,f=987},{c=139,co=1,f=255},
  {c=140,co=1,f=279},{c=141,co=1,f=278},{c=142,co=1,f=276},{c=143,co=1,f=277},
  {c=144,co=1,f=283},{c=145,co=1,f=285},{c=146,co=1,f=287},{c=147,co=1,f=288},
  {c=148,co=1,f=289},{c=149,co=1,f=296},{c=150,co=1,f=297},{c=151,co=1,f=303},
  {c=152,co=1,f=306},{c=153,co=1,f=286},{c=154,co=1,f=205},{c=155,co=1,f=270},
  {c=156,co=1,f=271},{c=157,co=1,f=280},{c=158,co=5,f=198},{c=159,f=948},
  {c=160,co=3,f=206},{c=161,co=23,f=90},{c=162,f=949},{c=163,co=5,f=188},
  {c=164,co=5,f=129},{c=165,f=985},{c=166,co=5,f=130},{c=167,co=5,f=128},
  {c=168,co=1,f=64},{c=169,co=1,f=295},{c=170,co=0,f=294},{c=171,co=0,f=293},
  {c=172,co=1,f=294},{c=173,co=1,f=293},{c=174,co=1,f=301},
  {c=175,co=23,f=171},{c=176,f=968},{c=177,co=1,f=304},{c=178,co=1,f=300},
  {c=179,co=1,f=236},{c=180,co=1,f=298},{c=181,co=1,f=302},{c=182,co=1,f=299},
  {c=183,co=1,f=209},{c=184,co=1,f=272},{c=185,co=1,f=281},{c=186,co=1,f=284},
  {c=187,co=1,f=290},{c=188,co=1,f=291},{c=189,co=1,f=292},{c=190,co=1,f=177},
  {c=191,f=986},{c=192,co=1,f=282},{c=193,co=1,f=274},{c=194,co=1,f=237},
  {c=195,co=1,f=266},{c=196,co=1,f=273},{c=197,f=861},{c=198,f=861},
  {c=199,f=861},{c=200,f=861},{c=201,f=861},{c=202,f=861},{c=203,f=989},
  {c=204,f=988},{c=205,co=1,f=257},{c=206,co=1,f=258},{c=207,co=1,f=259},
  {c=208,co=1,f=265},{c=209,co=1,f=267},{c=210,co=1,f=268},{c=211,co=1,f=269},
  {c=212,co=1,f=275},{c=213,co=1,f=8},{c=214,co=1,f=6},{c=215,co=1,f=5},
  {c=216,co=1,f=260},{c=217,co=1,f=261},{c=218,co=1,f=305},{c=219,co=1,f=263},
  {c=220,co=1,f=12},{c=221,co=1,f=262},{c=222,co=1,f=11},{c=223,co=1,f=264},
  {c=224,co=23,f=10},{c=225,co=23,f=12},{c=226,co=23,f=11},{c=227,co=23,f=16},
  {c=228,co=9,f=26},{c=229,co=9,f=5},{c=230,co=9,f=2},{c=231,co=9,f=7},
  {c=232,co=9,f=3},{c=233,co=9,f=75},{c=234,co=9,f=73},{c=235,co=9,f=71},
  {c=236,co=9,f=69},{c=237,co=9,f=22},{c=238,co=9,f=27},{c=239,co=9,f=24},
  {c=240,co=9,f=25},{c=241,co=10,f=1},{c=242,co=10,f=2},{c=243,co=10,f=5},
  {c=244,co=10,f=3},{c=245,co=10,f=9},{c=246,co=10,f=15},{c=247,co=10,f=16},
  {c=248,co=10,f=17},{c=249,co=10,f=34},{c=250,co=10,f=26},{c=251,co=10,f=30},
  {c=252,co=11,f=2},{c=253,co=11,f=1},{c=254,co=11,f=9},{c=255,co=11,f=3},
  {c=256,co=11,f=4},{c=257,co=11,f=11},{c=258,co=11,f=8},{c=259,co=11,f=14},
  {c=260,co=11,f=5},{c=261,co=11,f=6},{c=262,co=13,f=34},{c=263,co=13,f=36},
  {c=264,co=13,f=43},{c=265,co=13,f=46},{c=266,co=13,f=54},{c=267,co=13,f=1},
  {c=268,co=13,f=24},{c=269,co=13,f=2},{c=270,co=13,f=3},{c=271,co=13,f=39},
  {c=272,co=13,f=67},{c=273,co=13,f=68},{c=274,co=13,f=9},{c=275,co=13,f=22},
  {c=276,co=14,f=64},{c=277,co=14,f=58},{c=278,co=14,f=57},{c=279,co=14,f=56},
  {c=280,co=14,f=55},{c=281,co=14,f=14},{c=282,co=14,f=59},{c=283,co=14,f=16},
  {c=284,co=14,f=13},{c=285,co=14,f=15},{c=286,co=14,f=28},{c=287,co=14,f=31},
  {c=288,co=15,f=4},{c=289,co=15,f=3},{c=290,co=15,f=7},{c=291,co=15,f=5},
  {c=292,co=15,f=26},{c=293,co=15,f=31},{c=294,co=15,f=8},{c=295,co=15,f=64},
  {c=296,co=1,f=238},{c=297,co=16,f=2},{c=298,co=16,f=13},{c=299,co=16,f=84},
  {c=300,co=16,f=106},{c=301,co=16,f=78},{c=302,co=16,f=64},
  {c=303,co=16,f=132},{c=304,co=16,f=130},{c=305,co=16,f=16},
  {c=306,co=16,f=5},{c=307,co=3,f=124},{c=308,co=17,f=8},{c=309,co=17,f=11},
  {c=310,co=17,f=37},{c=311,co=17,f=39},{c=312,co=17,f=50},{c=313,co=17,f=16},
  {c=314,co=17,f=17},{c=315,co=17,f=10},{c=316,co=17,f=36},{c=317,co=17,f=38},
  {c=318,co=17,f=40},{c=319,co=17,f=51},{c=320,co=17,f=47},{c=321,co=17,f=49},
  {c=322,co=17,f=61},{c=323,co=18,f=4},{c=324,co=18,f=1},{c=325,co=18,f=12},
  {c=326,co=18,f=157},{c=327,co=18,f=16},{c=328,co=18,f=6},{c=329,co=18,f=76},
  {c=330,co=18,f=73},{c=331,co=18,f=71},{c=332,co=18,f=22},
  {c=333,co=18,f=161},{c=334,co=18,f=26},{c=335,co=18,f=24},
  {c=336,co=18,f=70},{c=337,co=18,f=30},{c=338,co=1,f=251},{c=339,co=19,f=5},
  {c=340,co=19,f=17},{c=341,co=19,f=8},{c=342,co=19,f=12},{c=343,co=19,f=7},
  {c=344,co=19,f=1},{c=345,co=19,f=69},{c=346,co=19,f=66},{c=347,co=19,f=65},
  {c=348,co=19,f=81},{c=349,co=19,f=82},{c=350,f=984},{c=351,co=19,f=129},
  {c=352,co=20,f=169},{c=353,co=20,f=171},{c=354,co=20,f=212},
  {c=355,co=20,f=213},{c=356,co=20,f=170},{c=357,co=20,f=224},
  {c=358,co=20,f=222},{c=359,co=20,f=200},{c=360,co=20,f=202},
  {c=361,co=20,f=231},{c=362,co=20,f=80},{c=363,co=20,f=239},
  {c=364,co=20,f=215},{c=365,co=20,f=172},{c=366,f=862},
}

-- AP item code -> the in-game item id the mailbox takes.
local item_ids = {
  [0]=17,[1]=15,[2]=14,[3]=12,[4]=13,[5]=9,[6]=16,[7]=10,[8]=11,[9]=44,
  [10]=69,[11]=92,[12]=71,[13]=31,[14]=42,[15]=65,[16]=22,[18]=74,[19]=70,
  [20]=51,[21]=53,[22]=62,[23]=86,[24]=0,[25]=6,[26]=7,[27]=5,[28]=91,[29]=46,
  [30]=45,[31]=50,[32]=59,[33]=60,[34]=58,[35]=8,[36]=4,[37]=19,[38]=26,
  [39]=28,[40]=47,[41]=48,[42]=63,[43]=66,[44]=1,[45]=1,[46]=3,[47]=2,[48]=1,
  [49]=3,[50]=2,[51]=1,[52]=3,[53]=2,[54]=1,[55]=3,[56]=2,[57]=1,[58]=3,
  [59]=2,[60]=1,[61]=3,[62]=2,[63]=1,[64]=3,[65]=2,[66]=1,[67]=3,[68]=2,
  [69]=1,[70]=3,[71]=2,[72]=1,[73]=3,[74]=2,[75]=1,[76]=3,[77]=1,
}

local cfg = {}
local log = function(_) end
local wanted, wanted_any = {}, false
local stream = {}          -- [server index + 1] = AP item id
local reported = {}        -- AP location ids already returned from poll()
local goal = false
local save_ptr = 0

local function u8(a)  return memory.read_u8(a) end
local function u32(a) return memory.read_u32_le(a) end
local function blk(a, n) return memory.read_bytes(a, n) end

-- Test one bit in a byte buffer (1-based table) with plain arithmetic --
-- MoonSharp modules avoid bit32 by house convention.
local function flagset(buf, flag)
  local v = buf[math.floor(flag / 8) + 1]
  if v == nil then return false end
  local mask = 2 ^ (flag % 8)
  return v % (mask * 2) >= mask
end

local function magic_arch(a)
  local m = blk(a, 4)   -- "ARCH"
  return m[1] == 0x41 and m[2] == 0x52 and m[3] == 0x43 and m[4] == 0x48
end

function M.init(ctx)
  cfg = (ctx and ctx.config) or {}
  if ctx and ctx.log then log = ctx.log end
  local locs = cfg.locations or {}
  for i = 1, #locs do wanted[locs[i]] = true; wanted_any = true end
  log("albw: module ready (" .. tostring(#locs) .. " slot locations)")
end

function M.receive_item(id, meta)
  stream[((meta and meta.index) or 0) + 1] = id
end

local warned = nil
local function warn_once(msg)
  if warned ~= msg then warned = msg; log("albw: " .. msg) end
end

local function rom_valid()
  if not magic_arch(AP_HEADER) then
    warn_once("running game is not an AP-patched ALBW (no ARCH header)")
    return false
  end
  local v = u32(AP_HEADER + 0x4)
  if v ~= DATA_VERSION then
    warn_once("patch data version " .. v .. ", this module speaks " .. DATA_VERSION)
    return false
  end
  local sd = cfg.slot_data
  if sd and sd.seed and u32(AP_HEADER + 0x8) ~= sd.seed then
    warn_once("the running patch was built for a different multiworld seed")
    return false
  end
  return true
end

local function is_in_game()
  local fw = u32(AP_HEADER + 0x54)
  if fw == 0 then return false end
  local task_mgr = u32(fw + 0x1c)
  local start_node = task_mgr + 0x44
  local node = u32(start_node + 4)
  local n = 0
  while node ~= start_node and n < 100 do
    local task = u32(node + 8)
    if u32(task) == MAIN_GAME_VTABLE then return true end
    node = u32(node + 4)
    n = n + 1
  end
  return false
end

local function save_valid()
  save_ptr = 0
  local all_saves = u32(SAVES)
  if all_saves == 0 then return false end
  save_ptr = u32(all_saves + 0x14)
  if save_ptr == 0 then return false end
  if u32(save_ptr + 0x1600) ~= 0 then return false end
  if not magic_arch(save_ptr + 0xde0) then
    warn_once("loaded save file is not this multiworld's AP save")
    return false
  end
  if u32(save_ptr + 0xde8) ~= u32(AP_HEADER + 0x8) then
    warn_once("loaded save belongs to a different multiworld")
    return false
  end
  return true
end

function M.poll()
  local out = {}

  if not rom_valid() then goal = false; return out end

  if not is_in_game() then
    -- Park the mailbox so a title screen never consumes an item.
    memory.write_u32_le(AP_HEADER + 0xc, EMPTY)
    goal = false
    return out
  end

  if not save_valid() then goal = false; return out end

  local ev_ptr = u32(EVENTS)
  local co_ptr = u32(COURSES)
  local mg_ptr = u32(MINIGAME)
  if ev_ptr == 0 or co_ptr == 0 or mg_ptr == 0 then return out end

  -- One poll = one read of each buffer actually touched.
  local ev_live = blk(ev_ptr + 0x48, 0x80)
  local ev_save = blk(save_ptr + 0x40, 0x80)
  local hotfoot = (u8(mg_ptr + 0x35) % 2 == 1) or (u8(save_ptr + 0xda5) % 2 == 1)

  local ccache = {}
  local function course_bufs(i)
    local c = ccache[i]
    if c == nil then
      local live = blk(co_ptr + i * 0x16c + 0x160, 0x20)
      local tail = blk(co_ptr + i * 0x16c + 0x1a0, 0x10)
      for k = 1, #tail do live[0x20 + k] = tail[k] end
      c = { live, blk(save_ptr + 0x560 + i * 0x40, 0x40) }
      ccache[i] = c
    end
    return c
  end
  local function cflag(i, f)
    local c = course_bufs(i)
    return flagset(c[1], f) or flagset(c[2], f)
  end
  local function eflag(f)
    return flagset(ev_live, f) or flagset(ev_save, f)
  end

  local function loc_checked(L)
    if L.f ~= nil then
      if L.co == nil then
        if eflag(L.f) then return true end
      else
        if cflag(L.co, L.f) then return true end
        -- Overworld courses share high flags with their mirrored variants;
        -- the world's client checks 2/4 (course 0) and 3/5 (course 1) too.
        if L.co == 0 and L.f >= 0x100 and (cflag(2, L.f) or cflag(4, L.f)) then return true end
        if L.co == 1 and L.f >= 0x100 and (cflag(3, L.f) or cflag(5, L.f)) then return true end
      end
    end
    if L.hot and hotfoot then return true end
    return false
  end

  for i = 1, #locations do
    local L = locations[i]
    local id = BASE_ID + L.c
    if reported[id] == nil and (not wanted_any or wanted[id]) then
      if loc_checked(L) then
        reported[id] = true
        out[#out + 1] = id
      end
    end
  end

  goal = eflag(GOAL_FLAG)

  -- Deliver at most one item per poll, and only into an empty mailbox: the
  -- game advances its own received count when it consumes the item.
  local count = u32(AP_HEADER + 0x50)
  local next_id = stream[count + 1]
  if next_id ~= nil and u32(AP_HEADER + 0xc) == EMPTY then
    local game_item = item_ids[next_id - BASE_ID]
    if game_item ~= nil then
      memory.write_u32_le(AP_HEADER + 0xc, game_item)
    else
      warn_once("received AP item " .. tostring(next_id) .. " has no in-game id")
    end
  end

  return out
end

function M.is_goal_complete()
  return goal
end

return M
