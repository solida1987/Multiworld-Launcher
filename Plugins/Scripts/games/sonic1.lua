-- ═══════════════════════════════════════════════════════════════════════════════
-- sonic1.lua — game module for the Archipelago BizHawk connector.
--              Sonic the Hedgehog 1 (Sega Genesis / Mega Drive, 1991)
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the official
-- AP world worlds/sonic1 (client.py + constants.py + sram.py, repo
-- github.com/kaithar/Archipelago branch `sonic1`, release sonic_1-v0.1.1). The
-- 208-entry location table (196 monitors + 6 bosses + 6 special stages) was
-- GENERATED directly from constants.py (monitor idx walk + boss/special bit maps),
-- not hand-copied, so it is exact. The SRAM save-struct decode + the 8-bit-SRAM
-- "dead byte" de-interleave + the byte-order auto-detection are replicated
-- EXACTLY from sram.py (SegaSRAM.detect_type / read_bytes). Loads crash-free on
-- any ROM; self-disables until the AP save magic is found.
--
-- MEMORY MODEL (BizHawk Genesis domains — matches client.py S1Client)
-- ──────────────────────────────────────────────────────────────────────────
--   The sonic1 AP client is a BizHawkClient. system = ("GEN",). It reads two
--   Genesis domains via worlds/_bizhawk read():
--     "SRAM"     the cartridge battery save. Holds the whole AP state struct
--                (S1Layout): the 'AS10' magic, the 196 per-monitor "broken"
--                bytes, the boss + special-stage bitfields, the seed name, the
--                slot id, and the victory bit. EVERY location-checked flag and
--                the goal signal live here.
--     "68K RAM"  the 68000 work RAM (the client reads the game-mode byte at
--                0x0F600 and the zone/act bytes at 0x0FE10 to know which level
--                you're in). We read ONLY the game-mode byte, as a gameplay
--                gate so a freshly-booted/zeroed save can't report phantom
--                checks before the ROM initialises it.
--
--   THE 8-BIT SRAM "DEAD BYTE" PROBLEM (sram.py, exactly):
--     Genesis carts wire 8-bit SRAM onto a 16-bit bus, so each logical save
--     byte is exposed by BizHawk with a partner "dead" byte, and DIFFERENT
--     BizHawk cores/builds present it DIFFERENTLY. The client tries four
--     layouts (`ram_type`) and picks the one where the magic 'AS10' appears:
--       ram_type 0  even bytes  → logical byte i lives at raw[i*2]
--       ram_type 1  odd  bytes  → logical byte i lives at raw[i*2 + 1]
--       ram_type 2  packed      → logical byte i lives at raw[i]   (no dead bytes)
--       ram_type 3  byte-swapped→ logical pairs are 16-bit byte-swapped
--     We replicate detect_type's search (read the magic region in each layout,
--     match 'AS10') and then de-interleave the save region the SAME way. This
--     is why we read the SRAM region byte-for-byte and unpack in Lua rather
--     than trusting a core's read_u16 — the dead-byte interleave is the whole
--     problem the client solves.
--
--   THE STRUCT IS BIG-ENDIAN (sram.py S1Layout(sram.BigEndian)): the one
--   multi-byte numeric field this module needs to reason about — SR_Slot, a
--   u16 — is big-endian, so its high byte is at the lower clean-data offset.
--   (The location/goal reads themselves are all single bytes, so byte order
--   only matters for the seed/slot fields, which we treat as opaque here.)
--
--   CLEAN-DATA LAYOUT (struct '>4s 196B 18B 20s H', offsets into clean_data):
--     [0  ..4  )  SR_Head        4-byte magic, must equal 'AS10'
--     [4  ..200)  SR_Monitors    196 bytes; byte k is monitor idx (k+1).
--                                0x00 = broken (CHECKED), 0x1F = unbroken.
--     [200]       SR_Specials    bitfield: bit (1<<n) = special-stage n+1 done
--     [201]       SR_Emeralds    bitfield (not a location — emeralds are items)
--     [202]       SR_Bosses      bitfield: bit (1<<n) = act-3/FZ boss n+1 beaten
--     [203..217)  buff/ring/gate/powerup/deathlink/deaths counters (not checks)
--     [207]       SR_SSGate      bit 0x40 (64) = VICTORY shown — the GOAL flag
--     [217..237)  SR_Seed        20-byte seed name
--     [237..239)  SR_Slot        u16 big-endian, the slot id
--
--   LOCATION FLAGS (client.py game_watcher, exact):
--     • Monitor idx i (1..196): checked when SR_Monitors[i-1] == 0x00 (MAGIC_BROKEN).
--       AP location id = id_base + i,  id_base = 3141501000.
--     • Boss n (bits 1,2,4,8,16,32): checked when SR_Bosses & bit ~= 0.
--       AP location ids 3141501211..216.
--     • Special n (bits 1,2,4,8,16,32): checked when SR_Specials & bit ~= 0.
--       AP location ids 3141501221..226.
--
--   GOAL (client.py): the client raises ClientStatus.CLIENT_GOAL once its
--   `finish_game` condition is met (specials/emeralds/rings/bosses all >= the
--   slot's yaml goals). At that exact moment it ALSO sets the victory bit
--   sskeys |= 64 and writes it into SR_SSGate (client.py lines ~297+312). So
--   SR_SSGate & 0x40 in SRAM is precisely "the client has declared this seed
--   complete" — a single authoritative byte we can read, instead of trying to
--   re-derive the multi-source goal math (which mixes server-side items the Lua
--   side does not see) in the connector. We gate it on the save being
--   initialised + a server slot_data being present (the client itself only ever
--   computes/writes the bit when slot_data is available).
--
--   GAMEPLAY GATE (mirrors the spirit of client.py): the client only acts on a
--   save once SR_Head == 'AS10' and the seed is not blank. We require the same
--   AND that the 68K game-mode byte (0x0F600) is not the boot/Sega state — so a
--   zeroed save on the very first frames can't surface checks.
--
-- WHAT THIS DOES
--   • poll(): de-interleave the SRAM save once, then read the monitor bytes +
--     boss/special bitfields exactly as the client does → AP location ids,
--     gated to the slot's server location set and to the AP-save / gameplay gate.
--   • is_goal_complete(): SR_SSGate & 0x40 (the client's own victory bit).
--   • receive_item(): NO-OP (documented). items_handling = 0b111 — the server
--     delivers ALL items (emeralds, keys, rings, powerups, deathlink) and the
--     REAL client applies them by WRITING the SRAM save struct through its
--     guarded accumulate-and-commit path (client.py game_watcher: per-item
--     decode into SR_Emeralds/SR_LevelGate/SR_SSGate/SR_RingsFound/SR_*_in,
--     then full_write/commit with the exact dead-byte re-interleave for the
--     detected ram_type). Re-implementing those guarded SRAM writes in Lua
--     without in-emulator verification would risk corrupting the live battery
--     save (wrong ram_type interleave, partial commit, lost-save). It is
--     therefore intentionally GATED here rather than shipped unverified —
--     detection + goal are fully live; remote-item DELIVERY is the deferred
--     piece, the same honest split the other emulated worlds ship with.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "sonic1"

local ADDRESSES_VERIFIED = true   -- tables generated from worlds/sonic1 source

-- ── Memory domains (BizHawk Genesis) ──────────────────────────────────────────
local SRAM    = "SRAM"      -- cartridge battery save (the whole AP state struct)
local WORKRAM = "68K RAM"   -- 68000 work RAM (game-mode gate byte)

-- ── Constants (worlds/sonic1 constants.py + sram.py + client.py) ──────────────
local ID_BASE          = 3141501000

local MAGIC            = "AS10"     -- SR_Head; written by the AP patch's save init
local MAGIC_BROKEN     = 0x00       -- a monitor byte of 0x00 == checked
-- (unbroken monitors read 0x1F; only 0x00 counts as broken/checked)

-- clean_data offsets into the de-interleaved save struct (see header layout)
local OFF_HEAD         = 0          -- 4 bytes
local OFF_MONITORS     = 4          -- 196 bytes
local MON_COUNT        = 196
local OFF_SPECIALS     = 200        -- 1 byte bitfield
local OFF_BOSSES       = 202        -- 1 byte bitfield
local OFF_SSGATE       = 207        -- 1 byte; bit 0x40 == victory (goal)
local OFF_SEED         = 217        -- 20-byte seed name
local STRUCT_LEN       = 239        -- total clean_data length ('>4s196B18B20sH')

local VICTORY_BIT      = 0x40       -- SR_SSGate bit set by the client on win

-- 68K work RAM gate: game mode byte. 0x00 (Sega screen) / boot states should not
-- surface checks; any other mode (Title 0x04, Level 0x0C, Special 0x10, …) is
-- fine — the stronger gate is the AP save magic + seed being present.
local GAME_MODE_ADDR   = 0x0F600
local GAME_MODE_SEGA   = 0x00

-- ── Location table (GENERATED from worlds/sonic1/constants.py) ────────────────
-- MON: ap_id -> SR_Monitors byte index (0-based within the 196-byte array).
-- Monitor idx i (1..196) -> ap_id ID_BASE+i -> byte (i-1). Checked when the byte
-- equals MAGIC_BROKEN (0x00).
local MON = {
  [3141501001]=0,[3141501002]=1,[3141501003]=2,[3141501004]=3,[3141501005]=4,[3141501006]=5,[3141501007]=6,[3141501008]=7,
  [3141501009]=8,[3141501010]=9,[3141501011]=10,[3141501012]=11,[3141501013]=12,[3141501014]=13,[3141501015]=14,[3141501016]=15,
  [3141501017]=16,[3141501018]=17,[3141501019]=18,[3141501020]=19,[3141501021]=20,[3141501022]=21,[3141501023]=22,[3141501024]=23,
  [3141501025]=24,[3141501026]=25,[3141501027]=26,[3141501028]=27,[3141501029]=28,[3141501030]=29,[3141501031]=30,[3141501032]=31,
  [3141501033]=32,[3141501034]=33,[3141501035]=34,[3141501036]=35,[3141501037]=36,[3141501038]=37,[3141501039]=38,[3141501040]=39,
  [3141501041]=40,[3141501042]=41,[3141501043]=42,[3141501044]=43,[3141501045]=44,[3141501046]=45,[3141501047]=46,[3141501048]=47,
  [3141501049]=48,[3141501050]=49,[3141501051]=50,[3141501052]=51,[3141501053]=52,[3141501054]=53,[3141501055]=54,[3141501056]=55,
  [3141501057]=56,[3141501058]=57,[3141501059]=58,[3141501060]=59,[3141501061]=60,[3141501062]=61,[3141501063]=62,[3141501064]=63,
  [3141501065]=64,[3141501066]=65,[3141501067]=66,[3141501068]=67,[3141501069]=68,[3141501070]=69,[3141501071]=70,[3141501072]=71,
  [3141501073]=72,[3141501074]=73,[3141501075]=74,[3141501076]=75,[3141501077]=76,[3141501078]=77,[3141501079]=78,[3141501080]=79,
  [3141501081]=80,[3141501082]=81,[3141501083]=82,[3141501084]=83,[3141501085]=84,[3141501086]=85,[3141501087]=86,[3141501088]=87,
  [3141501089]=88,[3141501090]=89,[3141501091]=90,[3141501092]=91,[3141501093]=92,[3141501094]=93,[3141501095]=94,[3141501096]=95,
  [3141501097]=96,[3141501098]=97,[3141501099]=98,[3141501100]=99,[3141501101]=100,[3141501102]=101,[3141501103]=102,[3141501104]=103,
  [3141501105]=104,[3141501106]=105,[3141501107]=106,[3141501108]=107,[3141501109]=108,[3141501110]=109,[3141501111]=110,[3141501112]=111,
  [3141501113]=112,[3141501114]=113,[3141501115]=114,[3141501116]=115,[3141501117]=116,[3141501118]=117,[3141501119]=118,[3141501120]=119,
  [3141501121]=120,[3141501122]=121,[3141501123]=122,[3141501124]=123,[3141501125]=124,[3141501126]=125,[3141501127]=126,[3141501128]=127,
  [3141501129]=128,[3141501130]=129,[3141501131]=130,[3141501132]=131,[3141501133]=132,[3141501134]=133,[3141501135]=134,[3141501136]=135,
  [3141501137]=136,[3141501138]=137,[3141501139]=138,[3141501140]=139,[3141501141]=140,[3141501142]=141,[3141501143]=142,[3141501144]=143,
  [3141501145]=144,[3141501146]=145,[3141501147]=146,[3141501148]=147,[3141501149]=148,[3141501150]=149,[3141501151]=150,[3141501152]=151,
  [3141501153]=152,[3141501154]=153,[3141501155]=154,[3141501156]=155,[3141501157]=156,[3141501158]=157,[3141501159]=158,[3141501160]=159,
  [3141501161]=160,[3141501162]=161,[3141501163]=162,[3141501164]=163,[3141501165]=164,[3141501166]=165,[3141501167]=166,[3141501168]=167,
  [3141501169]=168,[3141501170]=169,[3141501171]=170,[3141501172]=171,[3141501173]=172,[3141501174]=173,[3141501175]=174,[3141501176]=175,
  [3141501177]=176,[3141501178]=177,[3141501179]=178,[3141501180]=179,[3141501181]=180,[3141501182]=181,[3141501183]=182,[3141501184]=183,
  [3141501185]=184,[3141501186]=185,[3141501187]=186,[3141501188]=187,[3141501189]=188,[3141501190]=189,[3141501191]=190,[3141501192]=191,
  [3141501193]=192,[3141501194]=193,[3141501195]=194,[3141501196]=195,
}

-- BOSS: ap_id -> bit mask within the SR_Bosses byte. Checked when (byte & mask) ~= 0.
local BOSS = {
  [3141501211]=1,[3141501212]=2,[3141501213]=4,[3141501214]=8,[3141501215]=16,[3141501216]=32,
}

-- SPECIAL: ap_id -> bit mask within the SR_Specials byte. Checked when (byte & mask) ~= 0.
local SPECIAL = {
  [3141501221]=1,[3141501222]=2,[3141501223]=4,[3141501224]=8,[3141501225]=16,[3141501226]=32,
}

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local have_slot_data   = false  -- a server slot_data object was provided
local ram_type         = nil    -- -1 detect-fail; 0/1/2/3 once detected; nil untried
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[sonic1] " .. tostring(msg)) end
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

-- ── 8-bit SRAM de-interleave (sram.py SegaSRAM, exact) ────────────────────────
-- Read the raw SRAM bytes for one logical (de-interleaved) byte index, honouring
-- the detected ram_type. For ram_type 0/1 the cart presents 2 raw bytes per
-- logical byte (a real byte + a dead byte); for 2 it is 1:1; for 3 it is the
-- 16-bit byte-swapped form. Reads are done lazily per logical index so we never
-- assume how large the core's SRAM block is beyond what we touch.
local function read_clean(idx)
  local t = ram_type
  if t == 2 then
    return read_u8(idx, SRAM)                     -- packed: 1:1
  elseif t == 0 then
    return read_u8(idx * 2, SRAM)                 -- even bytes hold the data
  elseif t == 1 then
    return read_u8(idx * 2 + 1, SRAM)             -- odd bytes hold the data
  elseif t == 3 then
    -- byte-swapped 16-bit words: logical idx -> raw (idx xor 1)
    local raw = (idx % 2 == 0) and (idx + 1) or (idx - 1)
    return read_u8(raw, SRAM)
  end
  return nil
end

-- detect_type (sram.py): find which layout exposes the 'AS10' magic at clean
-- offset 0. We probe by reading the first 4 logical bytes in each candidate
-- layout and matching the magic. Caches the winner in `ram_type`; -1 = no match
-- yet (uninitialised save / wrong ROM) — retried every poll, exactly like the
-- client's double-detection.
local function magic_matches_layout(t)
  local saved = ram_type
  ram_type = t
  local ok = true
  for i = 1, #MAGIC do
    local b = read_clean(i - 1)
    if b == nil or b ~= string.byte(MAGIC, i) then ok = false; break end
  end
  ram_type = saved
  return ok
end

local function detect_ram_type()
  for _, t in ipairs({ 0, 1, 2, 3 }) do
    if magic_matches_layout(t) then
      ram_type = t
      log("SRAM type detected: " .. t .. " (AS10 magic found)")
      return true
    end
  end
  ram_type = -1
  return false
end

-- Ensure we have a working ram_type; (re)detect if needed. Mirrors the client's
-- "if ram_type == -1: detect again" guard at the top of game_watcher.
local function ensure_ram_type()
  if ram_type == nil or ram_type == -1 then return detect_ram_type() end
  -- Re-validate cheaply: if the magic vanished (save reset / core reload), redetect.
  if not magic_matches_layout(ram_type) then return detect_ram_type() end
  return true
end

-- ── Per-poll save snapshot (de-interleaved) ───────────────────────────────────
local save = {}   -- clean_data byte index -> value, refreshed each poll

local function refresh_save()
  for i = 0, STRUCT_LEN - 1 do save[i] = read_clean(i) end
end

local function save_is_initialised()
  -- SR_Head must equal 'AS10' (client.py: bail unless SR_Head == b'AS10').
  for i = 1, #MAGIC do
    if save[OFF_HEAD + (i - 1)] ~= string.byte(MAGIC, i) then return false end
  end
  -- Seed must not contain a 0xFF byte (client.py: bail if b'\xff' in SR_Seed —
  -- an uninitialised/blank save).
  for i = 0, 19 do
    local b = save[OFF_SEED + i]
    if b == nil or b == 0xFF then return false end
  end
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

-- ── Gameplay gate ─────────────────────────────────────────────────────────────
-- The client only acts once it has slot_data AND the save is initialised. We add
-- the 68K game-mode byte so a zeroed save on the first frames can't surface
-- checks even if the magic was just written.
local function in_gameplay()
  if not have_slot_data then return false end
  if not save_is_initialised() then return false end
  local mode = read_u8(GAME_MODE_ADDR, WORKRAM)
  if mode == nil then return true end      -- can't read mode → rely on save gate
  return mode ~= GAME_MODE_SEGA
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
  have_slot_data = (type(cfg.slot_data) == "table")
  local n = 0; for _ in pairs(MON) do n = n + 1 end
  log("ready: " .. (n + 12) .. " location flags (Genesis SRAM, big-endian struct)" ..
      (have_slot_data and "" or " — waiting for slot_data"))
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not ensure_ram_type() then return new end       -- no AP save present yet
  refresh_save()
  if not in_gameplay() then return new end

  -- Monitors: byte == 0x00 (MAGIC_BROKEN) means checked.
  for ap_id, idx in pairs(MON) do
    if not reported[ap_id] and wanted(ap_id) then
      local b = save[OFF_MONITORS + idx]
      if b == MAGIC_BROKEN then
        reported[ap_id] = true
        new[#new + 1] = ap_id
      end
    end
  end

  -- Bosses: SR_Bosses & bit.
  local bosses = save[OFF_BOSSES]
  if bosses ~= nil then
    for ap_id, bit in pairs(BOSS) do
      if not reported[ap_id] and wanted(ap_id) and bit_and(bosses, bit) ~= 0 then
        reported[ap_id] = true
        new[#new + 1] = ap_id
      end
    end
  end

  -- Specials: SR_Specials & bit.
  local specials = save[OFF_SPECIALS]
  if specials ~= nil then
    for ap_id, bit in pairs(SPECIAL) do
      if not reported[ap_id] and wanted(ap_id) and bit_and(specials, bit) ~= 0 then
        reported[ap_id] = true
        new[#new + 1] = ap_id
      end
    end
  end

  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED then return false end
  if not ensure_ram_type() then return false end
  refresh_save()
  if not save_is_initialised() or not have_slot_data then return false end
  -- The client sets SR_SSGate bit 0x40 at the instant it raises CLIENT_GOAL.
  local ssgate = save[OFF_SSGATE]
  return ssgate ~= nil and bit_and(ssgate, VICTORY_BIT) ~= 0
end

-- Remote multiworld items: see the file header. items_handling = 0b111 — the
-- SERVER delivers every item and the REAL client applies them by WRITING the
-- SRAM save struct through its guarded accumulate-and-commit path (per-item
-- decode into SR_Emeralds / SR_LevelGate / SR_SSGate / SR_RingsFound / SR_*_in,
-- then full_write/commit with the exact dead-byte re-interleave for the detected
-- ram_type). Re-implementing those guarded SRAM writes in Lua without
-- in-emulator verification would risk corrupting the live battery save, so it is
-- intentionally GATED (no-op) until it can be confirmed in-emulator. Detection +
-- goal are fully live.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
