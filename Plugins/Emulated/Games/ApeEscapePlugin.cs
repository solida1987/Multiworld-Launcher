using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// ApeEscapePlugin — Archipelago integration for Ape Escape (PlayStation / PSX),
// on the proven BizHawk Lua-pipe bridge (Nymashock PSX core).
//
// COMMUNITY WORLD. The apworld is from Thedragon005/Archipelago-Ape-Escape
// (a standalone AP community fork). AP game string: "Ape Escape" (inferred).
//
// STATUS: STUB — ChecksImplemented = false. The BizHawk Lua module
// ("ape_escape.lua") is a no-op stub that identifies the disc by header/title
// signature and reports nothing. A second-pass deep dive of the apworld's
// Client / Locations source is required before a real check table and goal
// logic can be built.
//
// WHAT NEEDS A SECOND PASS:
//   1. Parse Thedragon005's apworld Client.py + Locations.py to extract PSX
//      MainRAM addresses for location flags and the goal condition.
//   2. Identify whether the world patches the disc image (most PSX AP worlds do
//      NOT patch — they read live PSX RAM). Adjust PrepareSessionRomAsync if so.
//   3. Confirm the Ape Escape (USA) disc image expected identifiers (title, disc
//      size, MD5 if any base_checksum exists in the world).
//   4. Implement "ape_escape.lua" with real flag reads and goal detection; flip
//      ChecksImplemented to true.
//
// PSX NOTE: No patch, no AP ROM signature — same pattern as SymphonyOfTheNight.
// The player supplies their own legally-obtained Ape Escape PSX disc image.
// Requires BizHawk's Nymashock PSX core + a PSX BIOS (player must supply both).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ApeEscapePlugin : EmulatorPlugin
{
    public ApeEscapePlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "ape_escape";
    public override string DisplayName => "Ape Escape";
    public override string Subtitle    => "PSX · Emulated";
    public override string ApWorldName => "Ape Escape";

    public override string Description =>
        "Ape Escape is Sony's 1999 PlayStation launch title that introduced the " +
        "dual analog stick as a gameplay necessity. Kakeru races through time to " +
        "recapture hundreds of mischievous monkeys that the villain Specter has " +
        "freed and armed with Peak Point Helmets. In the Archipelago randomizer " +
        "the gadgets, time stations, and monkey catches across every era join the " +
        "multiworld pool. You supply your own legal Ape Escape PSX disc image; " +
        "it runs on BizHawk's Nymashock core (PSX BIOS required).";

    public override string ThemeAccentColor => "#E8A020";   // Ape Escape banana yellow

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "PSX";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "ape_escape";

    /// STUB: location check detection not yet implemented. Second pass required.
    public override bool ChecksImplemented => false;

    /// Ape Escape has no patch (same no-patch pattern as SotN) — the player's own
    /// disc image is used directly. No fixed MD5 to validate against.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();

    // ── ROM safety net ────────────────────────────────────────────────────────

    public override RomRequirement? GetUnmetRomRequirement()
    {
        if (RomPath != null && File.Exists(RomPath)) return null;
        return new RomRequirement(
            DisplayName, "PSX",
            "your own legal Ape Escape PSX disc image (.bin/.cue or .chd) — " +
            "runs on BizHawk's Nymashock core (PSX BIOS required)",
            RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());
    }

    // ── Session ROM ───────────────────────────────────────────────────────────

    protected override Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        SessionRomNote =
            "[Ape Escape] Ape Escape is not patched by this AP world — launching " +
            "your disc image directly. Start a NEW game so AP can track your " +
            "progress from the beginning, and make sure BizHawk is using the " +
            "Nymashock PSX core with a PSX BIOS installed.";
        return Task.FromResult<string?>(null);
    }
}
