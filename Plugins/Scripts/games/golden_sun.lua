-- ═══════════════════════════════════════════════════════════════════════════════
-- golden_sun.lua — game module for the Archipelago BizHawk connector.
--                  Golden Sun (GBA, 2001)
--
-- STATUS: NO-OP SKELETON. No Archipelago apworld for the original Golden Sun
-- (GBA, 2001) exists as of 2026-06-15. This module loads cleanly, verifies the
-- ROM cartridge identity, and performs no-op SYNC / GOAL handling. It will be
-- upgraded in-place when a community apworld is released and a RAM address map
-- is verified in-emulator.
--
-- Ref: standalone randomizer at github.com/Valyssa/GS-Randomizer (not an AP
-- world). Sister game Golden Sun: The Lost Age uses cjmang/Archipelago@gstla
-- (world id "gstla") — see gstla.lua.
-- ═══════════════════════════════════════════════════════════════════════════════

local GS = {}

-- ── ROM identity ─────────────────────────────────────────────────────────────
-- The cartridge title string for Golden Sun (USA/Europe) is "GOLDEN SUN" at
-- ROM offset 0x000000A0 (12 bytes, space-padded). Japanese releases use the
-- same offset with a different title. We check the first 6 bytes ("GOLDEN")
-- as a lightweight gate so the module self-disables on any other GBA title.

local EXPECTED_TITLE_PREFIX = "GOLDEN"

local function read_rom_title_prefix()
    local ok, domain = pcall(function() return memory.getmemorydomainlist() end)
    if not ok then return nil end
    for _, d in ipairs(domain) do
        if d == "ROM" then
            local ok2, val = pcall(function()
                local bytes = memory.read_bytes_as_array(0x000000A0, 6, "ROM")
                local s = ""
                for i = 1, #bytes do
                    local c = bytes[i]
                    if c == 0 then break end
                    s = s .. string.char(c)
                end
                return s
            end)
            if ok2 then return val end
        end
    end
    return nil
end

-- ── Module entry point called by bizhawk_ap_connector.lua ────────────────────

function GS.init(ctx)
    -- ctx carries: server, port, slot, password, pipe_base, locations, slot_data
    local prefix = read_rom_title_prefix()
    if prefix ~= EXPECTED_TITLE_PREFIX then
        print("[golden_sun] ROM title prefix '" .. tostring(prefix) ..
              "' does not match 'GOLDEN' — is this the correct GBA ROM?")
    else
        print("[golden_sun] Golden Sun (GBA) ROM confirmed. " ..
              "No AP world exists yet — checks and item delivery are inactive.")
    end
end

-- ── Per-frame update: no-op until RAM map is verified ─────────────────────────

function GS.update(ctx)
    -- Nothing to do: no location table, no RAM address map.
    -- Returns an empty check list (nil) and no goal signal.
    return nil, false
end

-- ── Item delivery: no-op (no item injection address map yet) ─────────────────

function GS.receive_item(ctx, item_id, item_index, sender, flags, location_id)
    -- Deferred: no IWRAM item-grant path has been verified.
end

return GS
