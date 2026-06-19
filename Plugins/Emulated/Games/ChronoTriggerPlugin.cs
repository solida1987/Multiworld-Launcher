using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// ChronoTriggerPlugin — Archipelago integration for Chrono Trigger: Jets of Time
// (SNES), on the proven BizHawk Lua-pipe bridge.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED. The 193-location
// table in ctjot.lua was GENERATED (Python ast) from the three flag dicts the
// client actually scans in worlds/ctjot/Client.py, JOINed by name with
// data/location_data.json (loc_id = LOCATION_ID_START + value); the event/treasure
// bit math, the "@ABC" gate-travel + map-id gate, the victory flag, the "AP" ROM
// signature, and the receive handshake are replicated 1:1 from CTJoTSNIClient, and
// mock-verified through MoonSharp against synthetic SNES memory. items_handling =
// 0b011 — the server streams this world's own found items too; the in-game event
// code grants every received item, and ctjot.lua mirrors the client's
// _deliver_next_item handshake (gated on the game's own received-item count + the
// event code zeroing the receive slot).
//   SOURCE: https://github.com/Anguirel86/Archipelago (branch "ctjot"),
//           worlds/ctjot (the ctjot.apworld source).
//
// HOW CTJoT IS RANDOMIZED — IMPORTANT: like Final Fantasy 1, Chrono Trigger: Jets
// of Time is NOT patched by Archipelago. The apworld has no APProcedurePatch
// container (.apctjot) and no generate_output — there is nothing for the launcher
// to apply. The player generates a patched ROM (.sfc) + matching .yaml TOGETHER on
// the CTJoT web generator (https://multiworld.ctjot.com), uploading their own US
// Chrono Trigger SNES ROM. That ALREADY-PATCHED .sfc is what the player loads here;
// the launcher applies no patch of its own and loads the ROM directly (§11: the
// library copy, original untouched). The patched ROM is the same 4 MB as the base
// US cartridge with a per-seed checksum, identified by CONTENT (size + the "AP"
// signature ctjot.lua verifies) — never by filename (§11). Each seed has a unique
// MD5, so no fixed MD5 can be demanded for the patched file; the vanilla US dump's
// MD5 is also accepted so a player who points at the un-patched ROM is not blocked
// at import — but only the web-generated .sfc carries the multiworld's checks/items.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ChronoTriggerPlugin : EmulatorPlugin
{
    public ChronoTriggerPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "ctjot";
    public override string DisplayName => "Chrono Trigger: Jets of Time";
    public override string Subtitle    => "SNES · Emulated";
    public override string ApWorldName => "Chrono Trigger Jets of Time";

    public override string Description =>
        "Chrono Trigger: Jets of Time is an open-world Archipelago randomizer for the " +
        "iconic SNES JRPG. You start with two characters and the winged Epoch and must " +
        "journey through time, finding the remaining characters and key items, with " +
        "chests, sealed chests, and key-item checks joining the multiworld pool. Defeat " +
        "Lavos to complete your goal. Generate your patched ROM and matching config at " +
        "multiworld.ctjot.com using your own Chrono Trigger (USA) ROM.";

    public override string ThemeAccentColor => "#3C6FB0";   // Chrono Trigger sky blue

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "SNES";

    // §14: the SNES emulators this world is played on. BizHawk is the verified Lua
    // bridge target; the CTJoT multiworld guide also names snes9x-rr and RetroArch
    // (both SNI clients) — snes9x is the headline alternative and Mesen the third,
    // shown per their bridge state in the dropdown.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "snes9x", "mesen" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "ctjot";

    /// ctjot.lua reports checks + goal (table generated from the apworld source; bit
    /// math from Client.py; mock-verified). The receive handshake is implemented and
    /// mirrors the client; like every emulated game it wants a real in-emulator
    /// playthrough to confirm live, but detection + goal are source-faithful.
    public override bool ChecksImplemented => true;

    // ── ROM ───────────────────────────────────────────────────────────────────
    //
    // CTJoT has no APProcedurePatch (see header) — the web generator produces the
    // patched .sfc. The player imports that already-patched ROM here. The vanilla US
    // cartridge dump is also accepted at import (so a player who picks the un-patched
    // file is not hard-blocked), but only the web-generated .sfc carries checks/items.

    /// The ROMs accepted at import, by CONTENT (size + MD5) — never by filename
    /// (§11). Both are 4 MB: the patched seed (per-seed MD5, so size + the "AP"
    /// signature ctjot.lua verifies are the stable fingerprint — no fixed MD5) and
    /// the vanilla US dump (canonical No-Intro MD5).
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(4 * 1024 * 1024, null,
                        "Chrono Trigger: Jets of Time — web-generated patched ROM from multiworld.ctjot.com"),
        new RomIdentity(4 * 1024 * 1024, "a2bc447961e52fd2227baed164f729dc",
                        "Chrono Trigger (USA) — vanilla cartridge dump (patch it on multiworld.ctjot.com first)"),
    };

    /// ROM safety net. CTJoT has no patch carrying an expected MD5 (the ROM is built
    /// by the web generator), so the only requirement we can surface is "a Chrono
    /// Trigger ROM is present" — the player supplies the patched .sfc themselves.
    public override RomRequirement? GetUnmetRomRequirement()
    {
        if (RomPath != null && File.Exists(RomPath)) return null;

        return new RomRequirement(
            "Chrono Trigger: Jets of Time", "SNES",
            "your web-generated patched Chrono Trigger: Jets of Time ROM " +
            "(create it at multiworld.ctjot.com with your own Chrono Trigger (USA) ROM)",
            RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());
    }

    // ── Session ROM ───────────────────────────────────────────────────────────
    //
    // CTJoT applies NO patch — the player's library ROM IS the already-patched .sfc
    // from multiworld.ctjot.com, so we return null and the base class launches it
    // directly. There is no .apctjot container for the AP generator to emit, so there
    // is no patch-apply branch here (unlike the SnesApPatchHelper games).

    protected override Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        SessionRomNote =
            "[Chrono Trigger: Jets of Time] Jets of Time is patched on " +
            "multiworld.ctjot.com (not by Archipelago) — launching your ROM directly. " +
            "Make sure this ROM is the .sfc you downloaded from the web generator " +
            "(paired with its .yaml), not a plain Chrono Trigger cartridge dump.";
        return Task.FromResult<string?>(null);
    }
}
