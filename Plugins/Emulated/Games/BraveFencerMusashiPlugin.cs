using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// BraveFencerMusashiPlugin — Archipelago integration for Brave Fencer Musashi
// (PlayStation / PSX), on the proven BizHawk Lua-pipe bridge (Nymashock PSX core).
//
// COMMUNITY WORLD. The apworld is from AegeusEvander/Brave-Fencer-Musashi-AP-World
// (a standalone community AP world). AP game string: "Brave Fencer Musashi"
// (inferred from repo name).
//
// STATUS: STUB — ChecksImplemented = false. The BizHawk Lua module
// ("brave_fencer_musashi.lua") is a no-op stub that identifies the disc by
// header/title signature and reports nothing. A second-pass deep dive of the
// apworld's Client / Locations source is required before a real check table and
// goal logic can be built.
//
// WHAT NEEDS A SECOND PASS:
//   1. Parse AegeusEvander's apworld Client.py + Locations.py to extract PSX
//      MainRAM addresses for location flags and the goal condition.
//   2. Identify whether the world patches the disc image (most PSX AP worlds do
//      NOT patch — they read live PSX RAM). Adjust PrepareSessionRomAsync if so.
//   3. Confirm the Brave Fencer Musashi (USA/NTSC) disc image expected identifiers.
//   4. Implement "brave_fencer_musashi.lua" with real flag reads and goal detection;
//      flip ChecksImplemented to true.
//
// PSX NOTE: No patch assumed — same pattern as SymphonyOfTheNight and ApeEscape.
// The player supplies their own legally-obtained Brave Fencer Musashi disc image.
// Requires BizHawk's Nymashock PSX core + a PSX BIOS (player must supply both).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class BraveFencerMusashiPlugin : EmulatorPlugin
{
    public BraveFencerMusashiPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "brave_fencer_musashi";
    public override string DisplayName => "Brave Fencer Musashi";
    public override string Subtitle    => "PSX · Emulated";
    public override string ApWorldName => "Brave Fencer Musashi";

    public override string Description =>
        "Brave Fencer Musashi is Square's 1998 PlayStation action RPG in which the " +
        "young hero Musashi is summoned to the kingdom of Allucaneet to stop the " +
        "Thirstquencher Empire. Armed with the twin swords Lumina and Fusion, he " +
        "battles through five towers to rescue the Princess and defeat the Dark " +
        "Lord Flatski. In the Archipelago randomizer the game's binchotan scrolls, " +
        "Fusion essences, weapon upgrades and dungeon items join the multiworld " +
        "pool. You supply your own legal Brave Fencer Musashi PSX disc image; " +
        "it runs on BizHawk's Nymashock core (PSX BIOS required).";

    public override string ThemeAccentColor => "#7B3F9E";   // Musashi royal purple

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "PSX";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "brave_fencer_musashi";

    /// STUB: location check detection not yet implemented. Second pass required.
    public override bool ChecksImplemented => false;

    /// No patch assumed (same no-patch pattern as SotN/ApeEscape) — the player's
    /// own disc image is used directly. No fixed MD5 to validate against.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();

    // ── ROM safety net ────────────────────────────────────────────────────────

    public override RomRequirement? GetUnmetRomRequirement()
    {
        if (RomPath != null && File.Exists(RomPath)) return null;
        return new RomRequirement(
            DisplayName, "PSX",
            "your own legal Brave Fencer Musashi PSX disc image (.bin/.cue or .chd) — " +
            "runs on BizHawk's Nymashock core (PSX BIOS required)",
            RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());
    }

    // ── Session ROM ───────────────────────────────────────────────────────────

    protected override Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        SessionRomNote =
            "[Brave Fencer Musashi] Brave Fencer Musashi is not patched by this AP " +
            "world — launching your disc image directly. Start a NEW game so AP can " +
            "track your progress from the beginning, and make sure BizHawk is using " +
            "the Nymashock PSX core with a PSX BIOS installed.";
        return Task.FromResult<string?>(null);
    }
}
