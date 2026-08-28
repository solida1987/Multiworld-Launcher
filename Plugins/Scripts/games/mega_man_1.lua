-- ═══════════════════════════════════════════════════════════════════════════════
-- mega_man_1.lua — game module for the Archipelago BizHawk connector.
--                  Mega Man (NES) — the world calls the game "Mega Man"
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the AP world
-- Silvris/Archipelago, branch mm1, commit 759131cc184eae3071729d20b37be728c90fdb75
-- (worlds/mm1: client.py + locations.py + rom.py + src/mm1_basepatch.asm). The
-- 76-entry location set is GENERATED from the client's own game_watcher() check
-- loops + MM1_CONSUMABLES / MM1_STAGE_CHECKS / MM1_REFIGHTS / MM1_RBM_REMAP
-- (parsed from client.py, not hand-copied) and cross-checked 1:1 against
-- locations.py's declared numeric ids (0 extra, 0 missing). The flag/byte math
-- is replicated EXACTLY from client.py game_watcher(). Loads crash-free on any
-- ROM; self-disables on a non-MM1 cartridge (no "MM1" PRG-ROM signature).
-- Sibling module to mm2.lua / mm3.lua (same world author, same client shape).
--
-- LOCATION IDS HAVE NO BASE OFFSET. worlds/mm1/__init__.py line 78 sets
-- `location_name_to_id = location_lookup`, and locations.py line 132 builds
-- location_lookup straight from the raw LocationData ids (0x1..0xF, 0x11..0x17,
-- 0x101..0x136). client.py tests those raw values verbatim against
-- ctx.checked_locations (`wep_id = 0x10 + i`, `MM1_REFIGHTS[i-1]`, the raw
-- consumable ids). So the ids this module emits are exactly the server's ids.
--
-- MEMORY MODEL (BizHawk NES domains — matches client.py MegaMan1Client)
-- ───────────────────────────────────────────────────────────────────────
--   The MM1 AP client is a BizHawkClient (client.py line 221 `system = "NES"`)
--   that reads two NES domains:
--     "RAM"      — the NES 2 KB internal work RAM. Every location flag, the
--                  goal bits, the sanity-guard bytes and the consumable queue
--                  live here (all addresses ≤ 0x7FF).
--     "PRG ROM"  — the cartridge program ROM. Carries the "MM1" identifier the
--                  client's validate_rom() checks at offset 0x3FFE0.
--
--   client.py game_watcher() decodes locations five ways (all "RAM"):
--
--     1. RBM WEAPONS — robot_masters_defeated (RAM 0xC1), for i in 1..6:
--          defeated & MM1_RBM_REMAP[i] → id 0x10 + i           (0x11..0x16)
--          (client.py lines 547-552; MM1_RBM_REMAP lines 81-89:
--           {1:0x20, 2:0x10, 3:0x02, 4:0x40, 5:0x04, 6:0x08, 7:0x80})
--     2. BOSS REFIGHTS — boss_refights (RAM 0xCB), for i in 1..6:
--          refights & (1<<(i-1)) → id MM1_REFIGHTS[i-1]
--          (client.py lines 553-556; MM1_REFIGHTS lines 72-79:
--           {0:8, 1:0xE, 2:0xC, 3:0xD, 4:0x9, 5:0xF})
--     3. MAGNET BEAM — magnet_beam_get (RAM 0xC5) & 0x80 → id 0x17
--          (client.py lines 558-559)
--     4. STAGE CLEARS — completed_stages, 16-bit little-endian word at RAM
--          0xC7/0xC8 (client.py line 53 "0xC7  # and C8", read as 2 bytes line
--          345, `int.from_bytes(completed_stages, "little")` line 561). For
--          each (bit, id) in MM1_STAGE_CHECKS (lines 60-70): word & (1<<bit) →
--          id. Bits 0..8 → ids {1,2,3,4,5,6,7,0xA,0xB}.
--     5. CONSUMABLES — a 10-slot × 3-byte EVENT QUEUE at RAM 0x7E0
--          (MM1_CONSUMABLE_CHECK, line 58). The patched game APPENDS a
--          (screen, x, stage) triple on each pickup; the CLIENT decodes each
--          non-(0,0,0) group through MM1_CONSUMABLES (lines 91-147, 54
--          entries → ids 0x101..0x136) and then CLEARS the group (lines
--          535-541). Consuming is part of the protocol — see the QUEUE note.
--
--   GOAL: client.py line 353 — `completed_stages[1] & 0x2` → CLIENT_GOAL.
--         That is RAM 0xC8 bit 1 = bit 9 of the stage word = Wily Stage 4
--         cleared (Wily Machine defeated). mm1_basepatch.asm SetStageClear
--         confirms the layout: it sets bit `stage` of the word for the
--         current stage, stages run 0..9, and 9 is the last Wily stage.
--
--   INIT GUARD — the reference client HAS NONE at this commit: its guard is
--         commented out (client.py lines 350-351, `#if difficulty[0] not in
--         (0, 1): return  # Game is not initialized`). Shipping that absence
--         would re-create the false-GOAL-at-boot bug class, so ram_ready()
--         below rebuilds a guard from invariants the same sources prove:
--           a) stage word bits 10..15 must be CLEAR — the ASM's SetStageClear
--              only ever sets bit `current_stage` (0..9) and nothing else
--              writes $C7/$C8 (asm defines line 26 `!cleared_stages = $C7 ;
--              $C8`), so any higher bit means uninitialised RAM.
--           b) health (RAM 0x6A, asm line 18 `!megaman_hp = $6A`) ≤ 0x1C —
--              0x1C is the full-bar constant everywhere in client.py (heal
--              path lines 465-480, refill cap line 507, Yashichi line 531).
--           c) last_wily (RAM 0xC6) ∈ {0, 7, 8, 9, 10} — the only writers are
--              the ASM's SetStageClear (line 363: stage+1 for stage ≥ 6 →
--              7..10) and the client (its own last_wily, default 8, lines
--              373-387); 0 is the cleared/boot value the client tests for.
--         All three hold trivially on zeroed RAM (reports nothing) and all
--         three reject 0xFF-initialised RAM. RESIDUAL RISK (documented, not
--         hidden): a garbage pattern that happens to satisfy all three could
--         still expose stage bits; no stronger invariant exists in the
--         published source, and the vanilla game clears RAM at reset.
--
-- QUEUE CONSUMPTION — THE ONE WRITE THIS MODULE DOES
--   The consumable queue must be drained by the client or checks are LOST:
--   mm1_basepatch.asm ConsumableCheck scans $07E0+Y for the first FREE slot
--   ("LDA $07E0, Y / BEQ .Set", Y += 3 up to 0x1E) and, when all 10 slots are
--   occupied, OVERWRITES the oldest ("LDY #$00 ; if we've managed to run 5
--   consumables in a row and the client hasn't cleared any of them, there's
--   nothing we can do but overwrite the oldest."). A reader that never clears
--   therefore ends up with a permanently full queue where every new pickup
--   lands in slot 0 — two pickups between two polls and the first is gone.
--   So after decoding a non-empty group this module zeroes THAT group's three
--   bytes, mirroring the client's consume step (client.py line 541). One
--   deliberate divergence, stated openly: client.py writes its zeros at the
--   queue BASE for every group (`writes.append((MM1_CONSUMABLE_CHECK,
--   bytes([0]*3), "RAM"))` — the offset `i` is dropped), which only ever
--   frees slot 0. The ASM above proves a slot is free exactly when its first
--   byte is 0, so zeroing the group AT ITS OWN OFFSET is what actually keeps
--   the queue draining; writing the base address for a non-zero group would
--   leave slots 1..9 stuck forever. Zeroed slots are the state the ASM
--   expects for "free" — this write cannot corrupt game state.
--
-- WHAT THIS DOES (mirrors worlds/mm1/client.py game_watcher → location loops)
--   • poll(): read the guard + flag bytes once, decode every wanted id with
--     the exact math above → AP ids; drain decoded consumable queue slots.
--     Gated to the slot's server location set and to ram_ready().
--   • is_goal_complete(): stage word bit 9 (RAM 0xC8 & 0x02), behind the SAME
--     ram_ready() guard poll() uses.
--   • receive_item(): NO-OP (documented). items_handling = 0b111 (client.py
--     line 274 sets ctx.items_handling = 0b111) — the AP SERVER drives ALL
--     item delivery. The reference client applies received items by writing
--     RAM gated on the game's received-items counter at RAM 0xCC (weapon
--     unlock mask 0x5D, stage-access mask 0xC2 + strobe 0xCA, SFX queue 0xC9,
--     lives 0xA6, health/weapon-energy spreading at 0x6A.., EnergyLink packet
--     0xC4, deathlink 0xC3). That stateful multi-write path depends on live
--     in-game state and is intentionally DEFERRED until it can be confirmed
--     in-emulator; a wrong RAM write mid-stage corrupts the run / desyncs the
--     counter. Checks + goal flow regardless.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "mega_man_1"

local ADDRESSES_VERIFIED = true   -- id set generated from worlds/mm1 source

-- ── Memory domains (BizHawk NES) ──────────────────────────────────────────────
local RAM    = "RAM"        -- NES 2 KB internal work RAM (client reads "RAM")
local PRGROM = "PRG ROM"    -- cartridge program ROM — "MM1" identifier

-- ── Addresses / constants (worlds/mm1/client.py, commit 759131cc) ─────────────
local ROM_NAME_LOCATION      = 0x3FFE0  -- PRG ROM: 16-byte name field; first 3 = "MM1"
local ROM_NAME               = "MM1"
local MM1_HEALTH             = 0x6A     -- RAM: Mega Man HP (client line 45; asm !megaman_hp = $6A)
local MM1_CLEARED_RBM        = 0xC1     -- RAM: robot-masters-defeated bit mask (line 47)
local MM1_MAGNET_BEAM        = 0xC5     -- RAM: magnet beam flag, bit 0x80 (line 51)
local MM1_LAST_WILY          = 0xC6     -- RAM: last wily stage marker (line 52)
local MM1_COMPLETED_STAGES   = 0xC7     -- RAM: 16-bit LE stage-clear word, 0xC7/0xC8 (line 53)
local MM1_BOSS_REFIGHTS      = 0xCB     -- RAM: boss refight bit mask (line 56)
local MM1_CONSUMABLE_CHECK   = 0x7E0    -- RAM: 10-slot × 3-byte pickup event queue (line 58)

local GOAL_HI_MASK           = 0x02     -- completed_stages[1] & 0x2 (client line 353)
local MAGNET_BEAM_MASK       = 0x80     -- magnet_beam_get & 0x80 (client line 558)
local MAGNET_BEAM_ID         = 0x17
local RBM_WEAPON_ID_BASE     = 0x10     -- wep_id = 0x10 + i (client line 550)
local HEALTH_FULL            = 0x1C     -- full-bar constant (client lines 465-480, 507, 531)
local QUEUE_SLOTS            = 10       -- client iterates range(0, 30, 3) (line 535)

-- RBM bit remap (GENERATED from client.py MM1_RBM_REMAP, lines 81-89).
-- Key 7 (0x80) exists in the source but the weapon loop only runs i = 1..6.
local RBM_REMAP = { [1]=0x20, [2]=0x10, [3]=0x02, [4]=0x40, [5]=0x04, [6]=0x08, [7]=0x80 }

-- Refight ids (GENERATED from client.py MM1_REFIGHTS, lines 72-79).
-- boss_refights bit (i-1), i = 1..6 → REFIGHTS[i-1].
local REFIGHTS = { [0]=0x8, [1]=0xE, [2]=0xC, [3]=0xD, [4]=0x9, [5]=0xF }

-- Stage-clear checks (GENERATED from client.py MM1_STAGE_CHECKS, lines 60-70).
-- word bit → id. Bit 9 is the GOAL, deliberately absent here.
local STAGE_CHECKS = {
  [0]=0x1, [1]=0x2, [2]=0x3, [3]=0x4, [4]=0x5, [5]=0x6, [6]=0x7, [7]=0xA, [8]=0xB,
}

-- Consumable queue decode table (GENERATED from client.py MM1_CONSUMABLES,
-- lines 91-147; 54 entries). Key = screen*0x10000 + x*0x100 + stage → ap id.
-- The (screen, x, stage) triple is exactly what the ASM writes into a slot
-- (ConsumableCheck: "LDA $0460, X / STA $07E0, Y" screen, "$0480, X" x,
-- "!current_stage" stage).
local CONSUMABLES = {}
do
  local t = {
    {0x0F,0x28,0x00,0x101},{0x09,0x08,0x01,0x102},{0x0E,0x90,0x01,0x103},
    {0x10,0x48,0x01,0x104},{0x11,0xA8,0x01,0x105},{0x11,0x90,0x01,0x106},
    {0x11,0x78,0x01,0x107},{0x11,0x60,0x01,0x108},{0x11,0x48,0x01,0x109},
    {0x11,0x30,0x01,0x10A},{0x06,0x28,0x02,0x10B},{0x06,0x38,0x02,0x10C},
    {0x06,0xC8,0x02,0x10D},{0x07,0xC8,0x02,0x10E},{0x11,0xF8,0x02,0x10F},
    {0x02,0x48,0x03,0x110},{0x03,0x48,0x03,0x111},{0x03,0x28,0x03,0x112},
    {0x04,0xE0,0x03,0x113},{0x04,0xF0,0x03,0x114},{0x06,0xA8,0x03,0x115},
    {0x06,0x98,0x03,0x116},{0x06,0x88,0x03,0x117},{0x0E,0x28,0x03,0x118},
    {0x01,0xD8,0x04,0x119},{0x06,0x50,0x04,0x11A},{0x06,0x60,0x04,0x11B},
    {0x06,0x70,0x04,0x11C},{0x07,0xF0,0x04,0x11D},{0x06,0xB0,0x05,0x11E},
    {0x0B,0x38,0x05,0x11F},{0x0B,0x28,0x05,0x120},{0x0B,0x70,0x05,0x121},
    {0x0B,0xB0,0x05,0x122},{0x0C,0x30,0x05,0x123},{0x0C,0xA0,0x05,0x124},
    {0x1F,0x90,0x06,0x125},{0x21,0x08,0x06,0x126},{0x24,0xB8,0x06,0x127},
    {0x24,0xC8,0x06,0x128},{0x19,0xC8,0x07,0x129},{0x1B,0xC8,0x07,0x12A},
    {0x1B,0xD8,0x07,0x12B},{0x1D,0xC8,0x07,0x12C},{0x1F,0xC8,0x07,0x12D},
    {0x1F,0xD8,0x07,0x12E},{0x21,0xC8,0x07,0x12F},{0x22,0xC8,0x07,0x130},
    {0x24,0x28,0x07,0x131},{0x27,0x28,0x07,0x132},{0x16,0x90,0x09,0x133},
    {0x1C,0x68,0x09,0x134},{0x1C,0xB8,0x09,0x135},{0x22,0xC8,0x09,0x136},
  }
  for _, e in ipairs(t) do
    CONSUMABLES[e[1] * 0x10000 + e[2] * 0x100 + e[3]] = e[4]
  end
end
local CONSUMABLE_COUNT = 54

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil    -- cached MM1 identifier result
local mem              = {}
local log_fn           = nil

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[mega_man_1] " .. tostring(msg)) end
end

-- ── Domain guard. A mistyped domain name must fail LOUDLY, never quietly fall
--    back to whichever domain the core happens to have current: that would turn
--    one wrong name into plausible-looking reads of the wrong memory. The real
--    domain list is resolved once, on the first guarded access; `_ap_two_arg`
--    records whether this core's read_u8 accepts a domain argument at all.
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
  mem.read_u8  = memory.read_u8  or memory.readbyte
  mem.write_u8 = memory.write_u8 or memory.writebyte
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

-- Write path — used ONLY to drain decoded consumable-queue slots (see the
-- QUEUE note in the header). Same domain guard as reads: a wrong domain name
-- must refuse, never land the write in whatever domain happens to be current.
local function write_u8(addr, value, domain)
  if not mem.write_u8 then return false end
  if not ap_domain_ok(domain) then return false end
  if (pcall(mem.write_u8, addr, value, domain)) then return true end
  if _ap_two_arg == false then  -- old API: select the domain, then write
    if domain ~= nil and memory.usememorydomain
        and not pcall(memory.usememorydomain, domain) then return false end
    return (pcall(mem.write_u8, addr, value))
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

-- bit i (0..7) as a power-of-two mask, matching the client's (1 << i).
local POW2 = { [0]=1, 2, 4, 8, 16, 32, 64, 128 }

-- ── ROM identity: the MM1 AP ROM carries "MM1" at PRG ROM 0x3FFE0 ─────────────
-- The client's validate_rom() reads 16 bytes at PRG ROM 0x3FFE0 and checks
-- game_name[:3] == b"MM1" (client.py lines 250-252). rom.py writes that name
-- field at HEADERED offset 0x3FFF0 (line 137, `patch.name = bytearray(f'MM1
-- {version}_{player}_{seed}\0' ...)`); BizHawk's "PRG ROM" domain is HEADERLESS
-- (excludes the 16-byte iNES header), so 0x3FFF0 - 0x10 = 0x3FFE0 — the read
-- base coincides with the name start and "MM1" lands at game_name[:3].
-- (Cross-checked against rom.py's version write at headered 0x3FFED and flag
-- byte at 0x3FFEC, which the client reads at 0x3FFDD / 0x3FFDC = those offsets
-- minus 0x10.) The trailing bytes encode the apworld version, which varies per
-- release; the client's exact version match is a compatibility feature, not
-- identity, so we match ONLY the version-independent "MM1" prefix — the
-- detector for any MM1 AP seed.
local function rom_is_mm1()
  if rom_ok ~= nil then return rom_ok end
  for i = 1, #ROM_NAME do
    local b = read_u8(ROM_NAME_LOCATION + i - 1, PRGROM)
    if b == nil then return false end          -- not readable yet; retry next poll
    if b ~= string.byte(ROM_NAME, i) then
      rom_ok = false
      log("non-MM1 ROM (no 'MM1' PRG-ROM signature) — detection idle")
      return false
    end
  end
  rom_ok = true
  log("MM1 ROM verified ('MM1' signature present)")
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
-- The reference client's init guard is commented out at this commit (client.py
-- lines 350-351), so this rebuilds one from source-proven invariants — see the
-- INIT GUARD note in the header for the derivation of each clause. Any clause
-- failing (or unreadable) means "not initialised": report nothing, goal false.
local function ram_ready()
  local hi = read_u8(MM1_COMPLETED_STAGES + 1, RAM)
  if hi == nil or bit_and(hi, 0xFC) ~= 0 then return false end  -- word bits 10..15
  local hp = read_u8(MM1_HEALTH, RAM)
  if hp == nil or hp > HEALTH_FULL then return false end        -- 0x1C = full bar
  local lw = read_u8(MM1_LAST_WILY, RAM)
  if lw == nil or (lw ~= 0 and (lw < 7 or lw > 10)) then return false end
  return true
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
  -- 9 stage + 6 refight + 6 weapon + 1 magnet beam + 54 consumables = 76.
  log("ready: " .. (9 + 6 + 6 + 1 + CONSUMABLE_COUNT) .. " location ids")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_mm1() then return new end
  if not ram_ready() then return new end

  -- 1. RBM weapons — robot_masters_defeated & RBM_REMAP[i] → 0x10 + i (i 1..6).
  local rbm = read_u8(MM1_CLEARED_RBM, RAM)
  if rbm then
    for i = 1, 6 do
      if bit_and(rbm, RBM_REMAP[i]) ~= 0 then emit(new, RBM_WEAPON_ID_BASE + i) end
    end
  end

  -- 2. Boss refights — boss_refights bit (i-1) → REFIGHTS[i-1] (i 1..6).
  local ref = read_u8(MM1_BOSS_REFIGHTS, RAM)
  if ref then
    for i = 1, 6 do
      if bit_and(ref, POW2[i - 1]) ~= 0 then emit(new, REFIGHTS[i - 1]) end
    end
  end

  -- 3. Magnet Beam — magnet_beam_get & 0x80 → 0x17.
  local mb = read_u8(MM1_MAGNET_BEAM, RAM)
  if mb and bit_and(mb, MAGNET_BEAM_MASK) ~= 0 then emit(new, MAGNET_BEAM_ID) end

  -- 4. Stage clears — 16-bit LE word at 0xC7/0xC8, STAGE_CHECKS bits 0..8.
  --    (ram_ready() already read and validated the high byte this poll.)
  local lo = read_u8(MM1_COMPLETED_STAGES, RAM)
  local hi = read_u8(MM1_COMPLETED_STAGES + 1, RAM)
  if lo and hi then
    local word = lo + hi * 256
    for bit, id in pairs(STAGE_CHECKS) do
      if bit_and(word, POW2[bit] or (256 * POW2[bit - 8])) ~= 0 then emit(new, id) end
    end
  end

  -- 5. Consumables — decode each non-(0,0,0) queue group, then DRAIN it at its
  --    own offset (see the QUEUE note: the ASM frees a slot exactly when its
  --    first byte is 0, and overwrites the oldest slot once all 10 are full).
  for slot = 0, QUEUE_SLOTS - 1 do
    local base = MM1_CONSUMABLE_CHECK + slot * 3
    local s = read_u8(base,     RAM)
    local x = read_u8(base + 1, RAM)
    local g = read_u8(base + 2, RAM)
    if s and x and g and not (s == 0 and x == 0 and g == 0) then
      local id = CONSUMABLES[s * 0x10000 + x * 0x100 + g]
      if id then
        emit(new, id)
      else
        -- Mirrors client.py: unknown tuples are still cleared (line 541 clears
        -- every non-empty group), otherwise they occupy a slot forever.
        log(string.format("unknown consumable tuple (%02X,%02X,%02X) — cleared", s, x, g))
      end
      write_u8(base,     0, RAM)
      write_u8(base + 1, 0, RAM)
      write_u8(base + 2, 0, RAM)
    end
  end

  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_mm1() then return false end
  if not ram_ready() then return false end
  -- completed_stages[1] & 0x2 (client.py line 353) — RAM 0xC8 bit 1, i.e. bit 9
  -- of the stage word: Wily Stage 4 cleared / Wily Machine defeated.
  local hi = read_u8(MM1_COMPLETED_STAGES + 1, RAM)
  return hi ~= nil and bit_and(hi, GOAL_HI_MASK) ~= 0
end

-- Remote items: see the file header. items_handling = 0b111 (client.py line 274)
-- — the AP server drives ALL item delivery. The reference client writes received
-- items into RAM gated on the game's received-items counter at RAM 0xCC (weapon
-- unlock mask 0x5D, stage-access mask 0xC2 + strobe 0xCA, SFX 0xC9, lives 0xA6,
-- health/weapon-energy spreading, EnergyLink, deathlink). That guarded
-- multi-write path is deferred here until it can be confirmed in-emulator; a
-- wrong RAM write would corrupt the run / desync the counter, so this is a
-- no-op (never a wrong write) rather than shipped unverified.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
