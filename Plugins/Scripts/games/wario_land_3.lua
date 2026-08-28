-- ═══════════════════════════════════════════════════════════════════════════════
-- wario_land_3.lua — game module for the Archipelago BizHawk connector.
--                    Wario Land 3 (Game Boy Color)
--
-- STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED from the AP world
-- RvPA-Roopa/Wario-Land-3-Archipelago, commit
-- 5c4d5411f9d3571740f1a95e186b5ddaf152c123 (wl3/client.py + wl3/locations.py).
-- The 410-entry location space is GENERATED from locations.py's four id
-- formulas (not hand-copied) and the per-type byte/bit math is replicated
-- EXACTLY from client.py game_watcher(). Loads crash-free on any ROM;
-- self-disables on a cartridge whose header title lacks "WARIO" (the world's
-- own — deliberately loose — identity check; see ROM IDENTITY).
--
-- LOCATION IDS (wl3/locations.py, all four bases restated in client.py 27/177-179):
--   CHESTS  100 ids: 7770300 + (owlevel-1)*4 + color   (locations.py line 3)
--   KEYS    100 ids: 7770400 + (owlevel-1)*4 + color   (locations.py line 86)
--   COINS   200 ids: 7770500 + (owlevel-1)*8 + coin    (locations.py line 124)
--   BOSSES   10 ids: 7770700 + boss_index (0..9)       (locations.py line 168)
--   owlevel is 1..25 (LEVEL_LIST), color is 0=Grey 1=Red 2=Green 3=Blue.
--
-- MEMORY MODEL (BizHawk GBC domains — matches wl3/client.py WL3Client)
-- ───────────────────────────────────────────────────────────────────────
--   The WL3 AP client is a BizHawkClient (client.py line 256 `system = "GBC"`)
--   reading three domains; this module mirrors its domain choices exactly:
--     "System Bus" — WRAM0 live state (client.py line 90: "WRAM0 addresses —
--                    always accessible via System Bus"):
--                      wLevel          0xC458  (line 91: "(owlevel-1)*8 + state")
--                      wLevelEndScreen 0xCED4  (line 94: "0=idle, 0x81–0x84=
--                                               chest collecting")
--                      wGameModeFlags  0xC491  (line 95: "bit 0 =
--                                               MODE_GAME_CLEARED")
--     "WRAM"       — the full 32 KB across banks (client.py lines 98-100:
--                    "bank 0 at offset 0x0000, bank 1 at 0x1000, bank 2 at
--                    0x2000"). Banked flag arrays:
--                      wLevelKeys          0x117C (25 B, bank 1 0xD17C, line 105)
--                      wOpenedChests       0x11AE (13 B, bank 1 0xD1AE, line 107)
--                      wCoinFlags          0x14A2 (25 B, bank 1 0xD4A2, line 145)
--                      wBossDefeatedFlags  0x14D3 ( 2 B, bank 1 0xD4D3, line 138)
--     "ROM"        — cartridge header title at 0x0134 (validate_rom, line 460).
--
--   client.py game_watcher() decodes locations five ways:
--
--     1. CHEST OPEN (edge) — wLevelEndScreen rising edge 0 → {1..4} after
--        masking bit 7 (lines 616-634: "the live value is 0x81–0x84 ... Mask
--        it off"), with owlevel = (wLevel >> 3) + 1 (line 623) →
--        chest id 7770300 + (owlevel-1)*4 + (color-1).
--     2. CHEST OPEN (state fallback) — new bits in wOpenedChests, bit
--        loc_index = (owlevel-1)*4+color, only loc_index < 100 (lines
--        705-737). Two corruption gates the client itself applies, mirrored
--        here: SKIP while in the Temple ("THE_TEMPLE = $C8, wLevel>>3 = 25 —
--        text decompression overflows into wOpenedChests, writing garbage",
--        lines 708-710) and SKIP a read that turns on more than 4 new bits at
--        once ("at most 4 chests per level", line 719).
--     3. KEYS — wLevelKeys, per level byte, LOW NIBBLE only (line 640:
--        `& 0x0F`), bit b → key id 7770400 + byte_idx*4 + b. The client only
--        ever READS wLevelKeys (its restore writes go to wKeyInventory at
--        0x1195, a different array — _update_level_keys lines 2417-2429), so
--        set bits mean "this key location was picked up here".
--     4. COINS — wCoinFlags, 25 bytes × 8 bits, bit set by the ROM's
--        SetCoinFlagFromCurObj on pickup (lines 654-672) → coin id
--        7770500 + byte_idx*8 + bit.
--     5. BOSSES — wBossDefeatedFlags, 2 bytes, bits 0..9 (boss_idx >= 10 is
--        skipped, line 692) → boss id 7770700 + boss_idx. Upstream gates this
--        on slot_data["boss_defeats"] "so old seeds without the option don't
--        send unknown-location checks" (line 681); here the slot's server
--        location set does the same job — ids the seed doesn't contain are
--        never wanted().
--
--   GOAL: client.py lines 976-983 — wGameModeFlags (System Bus 0xC491) bit 0
--         (MODE_GAME_CLEARED, "final boss defeated") → CLIENT_GOAL.
--
--   INIT GUARD — the reference client has none beyond the ROM title, so
--         game_ready() below rebuilds one from the client's own documented
--         value ranges (the false-GOAL-at-boot rule: the goal byte must never
--         be judged against uninitialised WRAM):
--           a) wLevelEndScreen ∈ {0, 0x81, 0x82, 0x83, 0x84} — the client
--              documents exactly these live values (lines 94 and 616-618).
--           b) wLevel ≤ 0xCF — wLevel = (owlevel-1)*8 + state (line 91) with
--              owlevel at most 26 (the Temple, wLevel ≥ 0xC8, line 710), so
--              (26-1)*8 + 7 = 0xCF is the highest encodable value.
--         Both hold trivially on zeroed WRAM (reports nothing) and both
--         reject 0xFF-initialised WRAM. RESIDUAL RISK (documented, not
--         hidden): garbage that satisfies both while the flag arrays hold
--         set bits would still be decoded; no stronger invariant exists in
--         the published source. The one-shot wOpenedChests baseline seed
--         additionally requires the 4 bits past location 99 (byte 12 high
--         nibble) to be clear, since only garbage can set them.
--
-- ROM IDENTITY — deliberately loose, and why. validate_rom() (client.py lines
--   457-466) reads 10 bytes at ROM 0x0134, strips zero bytes and accepts any
--   title containing "WARIO". wl3/rom.py line 373 sets `hash = None   # no
--   hash check — any WL3 ROM version accepted`, and the patch never writes a
--   title of its own, so no tighter seed signature EXISTS in this world. The
--   substring also matches other Wario cartridges and even an unpatched WL3;
--   the WRAM guard and the all-zero flag arrays of a fresh game are what keep
--   a wrong cartridge silent. This mirrors the world's own intent and its own
--   risk profile — not a shortcut taken here.
--
-- ONE DELIBERATE DIVERGENCE from client.py, stated openly: when a
--   wOpenedChests read fails the >4-new-bits corruption gate, upstream still
--   advances its baseline to the corrupted read (line 735 runs regardless),
--   which permanently hides any real bits that were inside the garbage;
--   upstream can afford that because it re-seeds from WRAM on connect and
--   writes server state back into the array. This module does neither, so it
--   KEEPS the last good baseline instead and simply retries next poll — a
--   transient garbage read then heals instead of masking chests forever.
--
-- WHAT THIS DOES (mirrors wl3/client.py game_watcher → detection paths)
--   • poll(): guard, then decode paths 1-5 above → AP ids. Gated to the
--     slot's server location set and to game_ready(). READ-ONLY — this module
--     writes nothing (the client's heartbeat/restore/trap writes are item- and
--     UI-machinery, not detection).
--   • is_goal_complete(): System Bus 0xC491 bit 0, behind the SAME
--     game_ready() guard poll() uses.
--   • receive_item(): NO-OP (documented). items_handling = 0b111 (client.py
--     line 469) — the AP SERVER drives ALL item delivery. The reference
--     client applies received items through a large guarded write machinery
--     (treasure bits via _grant_item, wKeyInventory 0x1195, transform unlocks
--     0x122A/0x122B, trap queue 0x1227 with lock byte 0x14A0, level unlocks,
--     popup/message renderer, client heartbeat 0x149F). That machinery
--     depends on live ROM state and is intentionally DEFERRED until it can be
--     confirmed in-emulator; a wrong WRAM write corrupts the run. Checks +
--     goal flow regardless — and with no heartbeat writes the ROM keeps
--     showing its own pickup messages, which is the correct standalone look.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "wario_land_3"

local ADDRESSES_VERIFIED = true   -- id space generated from wl3 world source

-- ── Memory domains (BizHawk GBC) — the client's own domain choices ────────────
local SYSBUS = "System Bus"  -- WRAM0 live state (client "System Bus")
local WRAM   = "WRAM"        -- all 32 KB, bank n at offset n*0x1000 (client "WRAM")
local ROM    = "ROM"         -- cartridge header title

-- ── Addresses (wl3/client.py, commit 5c4d5411) ────────────────────────────────
local ADDR_LEVEL        = 0xC458   -- System Bus: wLevel = (owlevel-1)*8 + state (line 91)
local ADDR_END_SCREEN   = 0xCED4   -- System Bus: wLevelEndScreen (line 94)
local ADDR_GAME_MODE    = 0xC491   -- System Bus: wGameModeFlags, bit 0 = cleared (line 95)
local ADDR_LEVEL_KEYS   = 0x117C   -- WRAM: wLevelKeys, 25 bytes (line 105)
local ADDR_OPENED_CHESTS= 0x11AE   -- WRAM: wOpenedChests, 13 bytes (line 107)
local ADDR_COIN_FLAGS   = 0x14A2   -- WRAM: wCoinFlags, 25 bytes (line 145)
local ADDR_BOSS_FLAGS   = 0x14D3   -- WRAM: wBossDefeatedFlags, 2 bytes (line 138)

local ROM_TITLE_ADDR    = 0x0134   -- ROM: header title, 10 bytes (line 460)
local ROM_TITLE_SUBSTR  = "WARIO"  -- validate_rom: `"WARIO" not in rom_title` → reject

-- ── Id bases (wl3/locations.py; restated in client.py 27/177-179) ─────────────
local CHEST_BASE  = 7770300
local KEY_BASE    = 7770400
local COIN_BASE   = 7770500
local BOSS_BASE   = 7770700
local NUM_LEVELS  = 25             -- locations.py LEVEL_LIST (owlevel 1..25)
local NUM_BOSSES  = 10             -- client.py line 180, matches the 10-bit flag field
local WLEVEL_MAX  = 0xCF           -- (26-1)*8 + 7 — see INIT GUARD note
local TEMPLE_MIN  = 0xC8           -- client.py line 710: is_temple = w_level >= 0xC8
local MAX_NEW_CHEST_BITS = 4       -- client.py line 719: "at most 4 chests per level"

-- ── State ─────────────────────────────────────────────────────────────────────
local reported         = {}     -- ap_id -> true once returned from poll()
local server_locations = nil    -- set of ap_ids the server expects (nil = all)
local rom_ok           = nil    -- cached "WARIO" title result
local mem              = {}
local log_fn           = nil
local prev_end_screen  = 0      -- chest edge state (client.py line 262 starts at 0)
local oc_baseline      = nil    -- last ACCEPTED wOpenedChests read (13 bytes)
local oc_seed_warned   = false

-- ── Logging ───────────────────────────────────────────────────────────────────
local function log(msg)
  if log_fn then pcall(log_fn, "[wario_land_3] " .. tostring(msg)) end
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

-- bit i (0..7) as a power-of-two mask, matching the client's (1 << i).
local POW2 = { [0]=1, 2, 4, 8, 16, 32, 64, 128 }

-- ── ROM identity — the world's own check, see the ROM IDENTITY header note ────
-- validate_rom() (client.py lines 457-466): read 10 bytes at ROM 0x0134, drop
-- zero bytes, accept when the remaining ASCII contains "WARIO". No tighter
-- signature exists: rom.py line 373 sets hash = None and the patch writes no
-- title. Cached like every other map: a NO on a static ROM domain is final.
local function rom_is_wl3()
  if rom_ok ~= nil then return rom_ok end
  local title = ""
  for i = 0, 9 do
    local b = read_u8(ROM_TITLE_ADDR + i, ROM)
    if b == nil then return false end          -- not readable yet; retry next poll
    if b ~= 0 and b >= 0x20 and b <= 0x7E then -- printable ASCII, zeros stripped
      title = title .. string.char(b)
    end
  end
  if string.find(title, ROM_TITLE_SUBSTR, 1, true) == nil then
    rom_ok = false
    log("cartridge title '" .. title .. "' lacks 'WARIO' — detection idle")
    return false
  end
  rom_ok = true
  log("WL3 cartridge accepted (title '" .. title .. "' contains 'WARIO')")
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
-- Rebuilt from the client's own documented value ranges (the reference client
-- has no init guard) — see the INIT GUARD note in the header. Any clause
-- failing (or unreadable) means "not initialised": report nothing, goal false.
local function game_ready()
  local es = read_u8(ADDR_END_SCREEN, SYSBUS)
  if es == nil or (es ~= 0 and (es < 0x81 or es > 0x84)) then return false end
  local lv = read_u8(ADDR_LEVEL, SYSBUS)
  if lv == nil or lv > WLEVEL_MAX then return false end
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
  -- 100 chests + 100 keys + 200 coins + 10 bosses = 410.
  log("ready: " .. (100 + 100 + 200 + NUM_BOSSES) .. " location ids")
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED then return new end
  if not rom_is_wl3() then return new end
  if not game_ready() then return new end

  local w_level = read_u8(ADDR_LEVEL, SYSBUS)
  if w_level == nil then return new end
  local is_temple = w_level >= TEMPLE_MIN     -- client.py line 710

  -- 1. Chest open, edge path — wLevelEndScreen 0 → 0x81..0x84 (client 616-634).
  local es = read_u8(ADDR_END_SCREEN, SYSBUS)
  if es ~= nil then
    local color_byte = bit_and(es, 0x7F)      -- mask bit 7 (client line 618)
    if prev_end_screen == 0 and color_byte >= 1 and color_byte <= 4 then
      local owlevel = math.floor(w_level / 8) + 1   -- (wLevel >> 3) + 1 (line 623)
      if owlevel >= 1 and owlevel <= NUM_LEVELS then -- 100 chest ids exist (locations.py)
        emit(new, CHEST_BASE + (owlevel - 1) * 4 + (color_byte - 1))
      end
    end
    prev_end_screen = es                      -- client line 634
  end

  -- 2. Chest open, state fallback — wOpenedChests bits, with the client's own
  --    Temple skip + >4-new-bits corruption gate (client 705-737). One-time
  --    baseline seed mirrors upstream's seed-on-connect (client 782-800) with
  --    the extra past-location-99 garbage clause from the header.
  if not is_temple then
    local oc = {}
    local ok_read = true
    for i = 0, 12 do
      oc[i] = read_u8(ADDR_OPENED_CHESTS + i, WRAM)
      if oc[i] == nil then ok_read = false break end
    end
    if ok_read then
      if oc_baseline == nil then
        -- Baseline seed: bits past loc_index 99 can only be garbage (the id
        -- space has exactly 100 chests; upstream ignores them at line 726).
        if bit_and(oc[12], 0xF0) == 0 then
          for idx = 0, 99 do
            if bit_and(oc[math.floor(idx / 8)], POW2[idx % 8]) ~= 0 then
              emit(new, CHEST_BASE + idx)
            end
          end
          oc_baseline = oc
        elseif not oc_seed_warned then
          oc_seed_warned = true
          log("wOpenedChests holds bits past location 99 — baseline seed deferred")
        end
      else
        local total_new = 0
        for i = 0, 12 do
          local nb = bit_and(255 - oc_baseline[i], oc[i])
          for b = 0, 7 do
            if bit_and(nb, POW2[b]) ~= 0 then total_new = total_new + 1 end
          end
        end
        if total_new <= MAX_NEW_CHEST_BITS then   -- client line 719
          for idx = 0, 99 do
            local i, b = math.floor(idx / 8), idx % 8
            if bit_and(oc[i], POW2[b]) ~= 0 and bit_and(oc_baseline[i], POW2[b]) == 0 then
              emit(new, CHEST_BASE + idx)
            end
          end
          oc_baseline = oc
        end
        -- >4 new bits: skip AND keep the old baseline (documented divergence).
      end
    end
  end

  -- 3. Keys — wLevelKeys low nibbles (client 636-650, `& 0x0F` at line 640).
  for byte_idx = 0, NUM_LEVELS - 1 do
    local kb = read_u8(ADDR_LEVEL_KEYS + byte_idx, WRAM)
    if kb ~= nil then
      for b = 0, 3 do
        if bit_and(kb, POW2[b]) ~= 0 then
          emit(new, KEY_BASE + byte_idx * 4 + b)
        end
      end
    end
  end

  -- 4. Coins — wCoinFlags, 8 bits per level byte (client 654-672).
  for byte_idx = 0, NUM_LEVELS - 1 do
    local cb = read_u8(ADDR_COIN_FLAGS + byte_idx, WRAM)
    if cb ~= nil and cb ~= 0 then
      for b = 0, 7 do
        if bit_and(cb, POW2[b]) ~= 0 then
          emit(new, COIN_BASE + byte_idx * 8 + b)
        end
      end
    end
  end

  -- 5. Bosses — wBossDefeatedFlags bits 0..9; bits >= 10 skipped exactly as
  --    upstream's `if boss_idx >= NUM_BOSSES: continue` (client 676-703).
  for byte_idx = 0, 1 do
    local bb = read_u8(ADDR_BOSS_FLAGS + byte_idx, WRAM)
    if bb ~= nil and bb ~= 0 then
      for b = 0, 7 do
        local boss_idx = byte_idx * 8 + b
        if boss_idx < NUM_BOSSES and bit_and(bb, POW2[b]) ~= 0 then
          emit(new, BOSS_BASE + boss_idx)
        end
      end
    end
  end

  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_wl3() then return false end
  if not game_ready() then return false end
  -- wGameModeFlags bit 0 = MODE_GAME_CLEARED, "final boss defeated"
  -- (client.py lines 95 and 976-983).
  local b = read_u8(ADDR_GAME_MODE, SYSBUS)
  return b ~= nil and bit_and(b, 0x01) ~= 0
end

-- Remote items: see the file header. items_handling = 0b111 (client.py line 469)
-- — the AP server drives ALL item delivery. The reference client applies
-- received items through guarded WRAM write machinery (treasure bits, key
-- inventory 0x1195, transform unlocks, trap queue + lock, level unlocks, popup
-- renderer, heartbeat). That path depends on live ROM state and is deferred
-- here until it can be confirmed in-emulator; a wrong WRAM write corrupts the
-- run, so this is a no-op (never a wrong write) rather than shipped unverified.
function M.receive_item(item_id, meta)
  -- intentionally empty (documented)
end

return M
