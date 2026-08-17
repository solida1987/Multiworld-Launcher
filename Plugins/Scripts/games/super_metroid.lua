-- ═══════════════════════════════════════════════════════════════════════════════
-- super_metroid.lua — game module for the Archipelago BizHawk connector.
--                   Super Metroid (SNES)
--
-- STATUS: REAL protocol, SOURCE-DERIVED from the official AP 0.6.7
-- worlds/sm/Client.py (an SNIClient subclass), but SHIPPED GATED. The full
-- send/recv ring-queue protocol below is implemented and structurally correct;
-- it is held behind ADDRESSES_VERIFIED=false because it needs two per-slot base
-- ids (locations_start_id / items_start_id) that the launcher does not yet pass
-- in ap_config.json (they come from the live server's data package, not the
-- apworld). Until those arrive the module LOADS and RUNS as a safe no-op.
-- Derivation + citations: Research_V2/SNES_BRIDGE_2026-06-12.md §3.
--
-- MEMORY MODEL (SNI → BizHawk)
-- ────────────────────────────
--   SNI WRAM_START(0xF50000)+off → domain "WRAM"    (= $7E0000+off)
--   SNI SRAM_START(0xE00000)+off → domain "CARTRAM" (fallback "SRAM")
--   The multiworld ring queues live in SRAM; the game-state byte is WRAM $7E0998.
--
-- HOW SM CHECKS WORK (unlike ALttP — no flag scan)
--   The SMBasepatch ASM maintains two ring queues in SRAM:
--     • SEND queue  (game → client): the game appends an 8-byte record per
--       location collected; the client advances a read cursor and turns each
--       record's encoded index into  locations_start_id + index.
--     • RECV queue  (client → game): the client appends a 4-byte record per
--       granted item (player, item_id, location) and bumps a write cursor; the
--       game consumes them.
--   Both halves are implemented in poll()/receive_item() exactly as the client
--   does them; see the research doc for the byte layout.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "super_metroid"

-- GATE. The addresses/masks/protocol are verified against Client.py, but turning
-- a SEND-queue index into a real AP location id (and a received AP item id back
-- into the game's id) requires locations_start_id / items_start_id from the
-- connected room. Those are NOT in ap_config.json today, so this stays false and
-- poll()/receive_item() are inert. Flip to true once init() can read both bases.
local ADDRESSES_VERIFIED = false

-- ── Memory domains ────────────────────────────────────────────────────────────
local WRAM    = "WRAM"
local CARTRAM = "CARTRAM"

-- ── SNI-space constants, as BizHawk-domain offsets (Client.py module top) ─────
local GAMEMODE_OFF   = 0x0998             -- WRAM: game-state byte ($7E0998)
local ROMNAME_OFF    = 0x7FC0             -- CARTRAM: ROM-header name; first 2 = "SM"

-- SRAM ring queues (offsets within CARTRAM):
local SEND_START     = 0x2700             -- game→client records, 8 bytes each
local SEND_RCOUNT    = 0x2680             -- u16 read cursor (client owns) + ...
                                          --   the 4 bytes here = [read_u16, write_u16]
local RECV_START     = 0x2000             -- client→game records, 4 bytes each
local RECV_WCOUNT    = 0x2602             -- u16 write cursor (client owns)

local INGAME_MODES   = { [0x07]=true, [0x09]=true, [0x0B]=true }
local ENDGAME_MODES  = { [0x26]=true, [0x27]=true }   -- {38,39}: MB dead / ending

-- Foreign player ids above this are clamped to 0 in the RECV record (the ROM's
-- player-name table is bounded). The real value comes from the client; 255 is a
-- safe upper bound for the single-byte player lane used here.
local ROM_MAX_PLAYERID = 255

-- ── Internal state ────────────────────────────────────────────────────────────
local server_locations = nil
local items_received   = {}
local slot_number      = 0
local locations_start  = nil    -- locations_start_id (per-slot AP base) — REQUIRED
local items_start      = nil    -- items_start_id     (per-slot AP base) — REQUIRED
local rom_ok           = nil
local mem              = {}
local log_fn           = nil

local function log(msg)
  if log_fn then pcall(log_fn, "[super_metroid] " .. tostring(msg)) end
end

-- ── Memory API ────────────────────────────────────────────────────────────────
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
  ok, v = pcall(fn, addr)
  if ok and type(v) == "number" then return v end
  return nil
end

local function read_wram_u8(off) return rd(mem.read_u8, off, WRAM) end

-- CARTRAM reads/writes with a "SRAM" domain-name fallback.
local function cartram_read_u8(off)
  local ok, v = pcall(mem.read_u8, off, CARTRAM)
  if ok and type(v) == "number" then return v end
  ok, v = pcall(mem.read_u8, off, "SRAM")
  if ok and type(v) == "number" then return v end
  return nil
end
local function cartram_read_u16(off)
  local lo = cartram_read_u8(off); local hi = cartram_read_u8(off + 1)
  if lo == nil or hi == nil then return nil end
  return lo + hi * 256
end
local function cartram_write_u8(off, value)
  if pcall(mem.write_u8, off, value, CARTRAM) then return true end
  return pcall(mem.write_u8, off, value, "SRAM")
end
local function cartram_write_u16(off, value)
  cartram_write_u8(off, value % 256)
  cartram_write_u8(off + 1, math.floor(value / 256) % 256)
end

local function band(a, b)
  local res, bitval = 0, 1
  while a > 0 and b > 0 do
    if a % 2 == 1 and b % 2 == 1 then res = res + bitval end
    a = math.floor(a / 2); b = math.floor(b / 2); bitval = bitval * 2
  end
  return res
end

-- ── ROM identity: AP basepatch leaves "SM" at the start of the ROM-name field ─
local function rom_is_ap()
  if rom_ok ~= nil then return rom_ok end
  local s = cartram_read_u8(ROMNAME_OFF)
  local m = cartram_read_u8(ROMNAME_OFF + 1)
  if s == nil or m == nil then return false end
  rom_ok = (s == string.byte("S") and m == string.byte("M"))
  if rom_ok then log("AP ROM verified ('SM' signature present)")
  else log("non-AP ROM (no 'SM' signature) — idle, no writes") end
  return rom_ok
end

local function load_locations(ids)
  if type(ids) ~= "table" then return end
  server_locations = {}
  local n = 0
  for _, id in ipairs(ids) do
    local v = tonumber(id); if v then server_locations[v] = true; n = n + 1 end
  end
  log("server location set: " .. n .. " ids")
end

local function wanted(ap_id)
  if server_locations == nil then return true end
  return server_locations[ap_id] == true
end

local function accept_item(it)
  if it.player == nil then return true end
  if it.player == 0 and (it.location or 0) <= 0 then return false end
  return true
end

local function delivery_list()
  local out = {}
  for _, it in ipairs(items_received) do
    if accept_item(it) then out[#out + 1] = it end
  end
  return out
end

local function gamemode() return read_wram_u8(GAMEMODE_OFF) end
local function in_gameplay()
  local m = gamemode(); return m ~= nil and INGAME_MODES[m] == true
end

-- ── Module contract ───────────────────────────────────────────────────────────

function M.init(ctx)
  if ctx and type(ctx.log) == "function" then log_fn = ctx.log end
  if not resolve_memory_api() then
    log("BizHawk memory API unavailable — module idle"); return
  end
  local cfg = (ctx and ctx.config) or {}
  slot_number = tonumber(cfg.slot_number) or 0
  load_locations(cfg.locations)

  -- The two per-slot base ids. Read them from wherever the launcher might put
  -- them (top-level or inside slot_data); both are needed to translate ids.
  local sd = cfg.slot_data
  locations_start = tonumber(cfg.locations_start_id)
                    or (type(sd) == "table" and tonumber(sd.locations_start_id)) or nil
  items_start     = tonumber(cfg.items_start_id)
                    or (type(sd) == "table" and tonumber(sd.items_start_id)) or nil

  if ADDRESSES_VERIFIED and locations_start and items_start then
    log(("ready: slot #%d, locations_start=%d, items_start=%d")
        :format(slot_number, locations_start, items_start))
  else
    log("loaded as NO-OP: SM needs locations_start_id + items_start_id from the "
        .. "connected room (not in ap_config.json yet). Reporting nothing; "
        .. "applying nothing. See Research_V2/SNES_BRIDGE_2026-06-12.md §3e.")
  end
end

function M.poll()
  local new = {}
  if not ADDRESSES_VERIFIED or locations_start == nil then return new end
  if not rom_is_ap() or not in_gameplay() then return new end

  -- SEND queue: read [read_u16, write_u16] at SEND_RCOUNT.
  local read_idx  = cartram_read_u16(SEND_RCOUNT)
  local write_cnt = cartram_read_u16(SEND_RCOUNT + 2)
  if read_idx == nil or write_cnt == nil then return new end

  local guard = 0
  while read_idx < write_cnt and guard < 256 do
    guard = guard + 1
    local base = SEND_START + read_idx * 8
    local b4 = cartram_read_u8(base + 4)
    local b5 = cartram_read_u8(base + 5)
    if b4 == nil or b5 == nil then break end
    local item_index  = math.floor((b4 + b5 * 256) / 8)         -- (value)>>3
    local location_id = locations_start + item_index
    if wanted(location_id) then new[#new + 1] = location_id end
    read_idx = read_idx + 1
    cartram_write_u16(SEND_RCOUNT, read_idx)                    -- advance our cursor
  end
  return new
end

function M.is_goal_complete()
  if not ADDRESSES_VERIFIED or not rom_is_ap() then return false end
  local m = gamemode()
  return m ~= nil and ENDGAME_MODES[m] == true
end

-- Append granted items to the RECV ring and bump the write cursor (the client
-- owns this cursor in SM). The game drains the queue on its side.
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

  if not ADDRESSES_VERIFIED or items_start == nil then return end
  if not rom_is_ap() or not in_gameplay() then return end

  local list = delivery_list()
  local out_ptr = cartram_read_u16(RECV_WCOUNT)
  if out_ptr == nil then return end

  local guard = 0
  while out_ptr < #list and guard < 256 do
    guard = guard + 1
    local it = list[out_ptr + 1]
    if not it or not it.item then break end
    local g_item_id = band(it.item - items_start, 0xFF)
    local g_loc     = 0
    if locations_start and it.player and it.player == slot_number
       and (it.location or 0) > 0 then
      g_loc = band(it.location - locations_start, 0xFF)
    end
    local pid = it.player or 0
    if pid > ROM_MAX_PLAYERID then pid = 0 end
    local rec = RECV_START + out_ptr * 4
    cartram_write_u8(rec + 0, pid % 256)
    cartram_write_u8(rec + 1, math.floor(pid / 256) % 256)
    cartram_write_u8(rec + 2, g_item_id)
    cartram_write_u8(rec + 3, g_loc)
    out_ptr = out_ptr + 1
    cartram_write_u16(RECV_WCOUNT, out_ptr)
  end
end

return M
