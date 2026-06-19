-- ═══════════════════════════════════════════════════════════════════════════════
-- soe.lua — game module for the Archipelago BizHawk connector.
--          Secret of Evermore (SNES)
--
-- STATUS: DETECTION NOT IMPLEMENTABLE FROM SOURCE — Secret of Evermore is the one
-- Archipelago SNES world that does NOT use an in-emulator memory-scanning client.
-- This module therefore loads crash-free and stays IDLE (reports nothing, never
-- writes), and the SecretOfEvermorePlugin advertises ChecksImplemented = false.
-- This is the only HONEST state; the reason is architectural, not effort:
--
--   • Source reviewed (worlds/soe @ main, AP 0.6.x): __init__.py, patch.py,
--     options.py, logic.py, requirements.txt + the repo-root SNIClient.py.
--   • SoE has NO per-game SNI/BizHawk client. `worlds/soe/` contains no Client.py,
--     and an org-wide code search ("Secret of Evermore" + SNIClient / SoESNIClient)
--     finds NO handler class anywhere in the repo.
--   • Instead, the repo-root SNIClient.py special-cases the patch suffix:
--         if args.diff_file.endswith(".apsoe"):
--             webbrowser.open("http://www.evermizer.com/apclient/#server=...")
--             ... sys.exit()
--     i.e. SoE launches a CLOSED-SOURCE BROWSER CLIENT at evermizer.com that talks
--     to the AP server and to the emulator itself. The location "checked"
--     detection lives entirely inside that web app — it is NOT in the apworld.
--   • The apworld carries ZERO memory addresses: no WRAM/SRAM map, no flag tables,
--     no validate_rom signature, no game_watcher loop. The only "addresses" in the
--     source are AP-id math (_id_offset[type] + index, base 64000), and the item /
--     location lists come from the COMPILED `pyevermizer==0.50.1` binary wheel
--     (no Python source), so no check-bit table can be PARSED from the source the
--     way every other shipped module's table was.
--
-- Fabricating a flag table here would violate the project's "source-derived, no
-- hand-copy" rule (there is no source table to derive), and could mis-report
-- checks. So detection is intentionally left unimplemented and clearly documented,
-- exactly as cvcotm.lua documents its deferred remote-item path — but here it is
-- the DETECTION itself that has no faithful in-emulator implementation.
--
-- WHAT STILL WORKS (the plugin side)
--   • The SecretOfEvermorePlugin applies the seed's .apsoe APDeltaPatch to a
--     library COPY of the user's vanilla US ROM (MD5 6e9c9451…, 3,145,728 bytes)
--     → a playable .sfc, via the shared SnesApPatchHelper. The patched ROM plays;
--     a player drives checks/items through SoE's own evermizer.com browser client
--     alongside the emulator (the launcher surfaces this as a note).
--
-- MEMORY MODEL (for reference, were a client ever to exist)
--   SoE is an SNES title; SNI WRAM_START(0xF50000)+off → BizHawk "WRAM", SNI
--   SRAM_START(0xE00000)+off → "CARTRAM" (fallback "SRAM"). Left here so a future
--   implementer has the same domain routing the other SNES modules use.
--
-- MODULE CONTRACT (called by bizhawk_ap_connector.lua)
--   M.init(ctx) / M.poll() -> {ids} / M.is_goal_complete() -> bool /
--   M.receive_item(item_id, meta)
--
-- SOURCE: https://github.com/ArchipelagoMW/Archipelago/tree/main/worlds/soe
--         https://github.com/ArchipelagoMW/Archipelago/blob/main/SNIClient.py (L700)
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}
M.name = "soe"

-- No source-derived RAM map exists for this world (see header) — the module is
-- inert by design: it reports no checks and never reports goal completion.
local DETECTION_IMPLEMENTED = false

-- ── Memory domains (kept for a future implementer; unused while inert) ─────────
local WRAM    = "WRAM"
local CARTRAM = "CARTRAM"      -- SNES battery SRAM in BizHawk; fallback "SRAM"

-- ── AP id math (worlds/soe/__init__.py — FYI only; not used for detection) ─────
-- ap_id = _id_offset[check_type] + index, with _id_base = 64000:
--   ALCHEMY 64000 | BOSS 64050 | GOURD 64100 | NPC 64400 | EXTRA 64800 |
--   TRAP 64900 | SNIFF 65000. The per-type index lists live in the compiled
--   pyevermizer wheel, so they cannot be enumerated from source here.
local ID_BASE = 64000

-- ── State ─────────────────────────────────────────────────────────────────────
local mem    = {}
local log_fn = nil

local function log(msg)
  if log_fn then pcall(log_fn, "[soe] " .. tostring(msg)) end
end

-- ── Memory API (resolved at init; harmless even though unused) ────────────────
local function resolve_memory_api()
  if not memory then return false end
  mem.read_u8  = memory.read_u8     or memory.readbyte
  mem.read_u16 = memory.read_u16_le or memory.readword
  return mem.read_u8 ~= nil
end

-- ── Module contract ───────────────────────────────────────────────────────────

function M.init(ctx)
  if ctx and type(ctx.log) == "function" then log_fn = ctx.log end
  resolve_memory_api()   -- best effort; module stays inert regardless
  log("Secret of Evermore uses the evermizer.com BROWSER client for checks/items "
      .. "— there is no in-emulator memory map in the apworld, so this module is "
      .. "inert (no checks, no goal). The patched ROM plays normally; drive AP "
      .. "through the SoE browser client. See module header for the full rationale.")
end

-- No faithful, source-derived detection exists (see header). Always empty.
function M.poll()
  return {}
end

-- Goal is the in-game final boss, tracked by the evermizer.com browser client,
-- not by any apworld memory flag — nothing here can read it. Never true.
function M.is_goal_complete()
  return false
end

-- SoE delivers remote items through its own browser client; there is no AP
-- receive-buffer protocol in the apworld to write to. No-op (documented).
function M.receive_item(item_id, meta)
  -- intentionally empty: see module header
end

return M
