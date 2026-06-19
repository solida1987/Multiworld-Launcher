-- ═══════════════════════════════════════════════════════════════════════════════
-- Diddy Kong Racing.lua — game module for the Archipelago BizHawk connector.
--                         Diddy Kong Racing (Nintendo 64) — "Diddy Kong Racing"
--
-- WORLD SOURCE (community): zakwiz/DiddyKongRacingAP (fork of ArchipelagoMW/
-- Archipelago, latest release v1.1.4 on 2026-03-17, 22 releases total).
--   https://github.com/zakwiz/DiddyKongRacingAP/releases
--
-- STATUS: STUB — detection is NOT implemented. This module verifies the ROM
-- cartridge header to confirm a Diddy Kong Racing ROM is loaded, then idles.
-- No checks are reported and the goal is never signalled. The C# plugin
-- advertises ChecksImplemented = false accordingly.
--
-- WHAT A SECOND PASS MUST DO:
--   1. Clone zakwiz/DiddyKongRacingAP and read worlds/dkr/Client.py (or
--      worlds/diddy_kong_racing/Client.py) to extract:
--        • ROM signature offset + expected bytes (the AP basepatch identity mark)
--        • RDRAM addresses for every location flag (or the formula used to
--          compute them from a compact table)
--        • The goal / victory condition address and bit
--      Compare to the mk64.lua and dk64.lua patterns — DKR is also N64 and
--      almost certainly uses the same BizHawk "RDRAM"/"ROM" domain pattern.
--   2. Generate the location table from worlds/dkr/Locations.py (or equivalent).
--   3. Replicate the flag bit math and any readiness gates from Client.py.
--   4. Replace the stub poll() / goal() below with the real implementation,
--      flip ConnectsItself to true if needed, and set ChecksImplemented = true
--      in DiddyKongRacingPlugin.cs.
--
-- N64 NOTES (applies to the full implementation):
--   • N64 IS BIG-ENDIAN. Multi-byte reads must assemble bytes MSB-first:
--       local hi = memory.read_u8(addr,   "RDRAM")
--       local lo = memory.read_u8(addr+1, "RDRAM")
--       local u16 = hi * 256 + lo
--   • Use BizHawk domains "RDRAM" (console work RAM) and "ROM" (cartridge).
--   • Gate all detection on a confirmed ROM signature so a vanilla / unpatched
--     cartridge on the title screen cannot report phantom checks.
--   See mk64.lua and dk64.lua for complete worked examples of this pattern.
-- ═══════════════════════════════════════════════════════════════════════════════

local M = {}

-- ── Module metadata (read by bizhawk_ap_connector.lua) ───────────────────────

M.GAME      = "Diddy Kong Racing"   -- must match AP game string exactly
M.SYSTEM    = "N64"
M.STUB      = true                  -- tells the connector: no real detection yet

-- ── ROM identity ──────────────────────────────────────────────────────────────
-- Diddy Kong Racing internal cartridge header is at ROM 0x3B (ID4 field).
-- Standard N64 cartridge name for the USA .z64 dump starts at ROM 0x20.
-- Confirm the exact patch signature offset from worlds/dkr/Client.py on second
-- pass and replace ROM_SIG_OFFSET / ROM_SIG_BYTES accordingly.

local ROM_NAME_OFFSET = 0x20        -- N64 internal header: game name (20 bytes)
local ROM_NAME_EXPECT = "DIDDY KONG RACING"  -- first 17 chars; confirm exact string

local rom_confirmed   = false       -- true once the ROM signature is validated
local warned_once     = false

local function check_rom()
    if rom_confirmed then return true end
    -- Read the first 17 bytes of the internal header name field.
    local name = ""
    for i = 0, 16 do
        local b = memory.read_u8(ROM_NAME_OFFSET + i, "ROM")
        if b == 0 then break end
        name = name .. string.char(b)
    end
    if name:find("DIDDY KONG") then
        rom_confirmed = true
        log("[DKR stub] ROM confirmed: " .. name)
        return true
    end
    if not warned_once then
        warned_once = true
        log("[DKR stub] ROM name mismatch — not a Diddy Kong Racing ROM (got: " ..
            name .. "). Detection disabled.")
    end
    return false
end

-- ── Connector API ─────────────────────────────────────────────────────────────

--- Called once when the connector first connects this module.
function M.init(ctx)
    log("[DKR stub] Diddy Kong Racing module loaded (STUB — no checks).")
    log("[DKR stub] A second-pass implementation is required. See module header.")
end

--- Called every poll cycle (typically every 500 ms).
--- Returns a list of newly-checked AP location IDs (empty until implemented).
function M.poll(ctx)
    if not check_rom() then return {} end
    -- Stub: nothing to report yet.
    return {}
end

--- Returns true when the player has completed their goal (never until implemented).
function M.goal(ctx)
    if not check_rom() then return false end
    -- Stub: goal is never reached.
    return false
end

--- Called when the server sends an item to deliver to the player.
--- No-op until item delivery addresses are confirmed from the source.
function M.receive_item(ctx, item_id, item_name, sender)
    -- TODO: write item_id into the appropriate RDRAM delivery slot.
    -- See mk64.lua receive_item and dk64.lua receive_item for the pattern.
end

return M
