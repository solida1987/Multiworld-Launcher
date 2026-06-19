using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// TheGrinchPlugin — Archipelago integration for The Grinch (PSX, 2000).
//
// ── CONFIRMED AP WORLD ───────────────────────────────────────────────────────
//
// A community Archipelago world exists for The Grinch (PlayStation, 2000),
// the 3D platformer tie-in to the 2000 Universal film. The apworld randomizes
// collectibles, presents, and level access across Whoville and Mount Crumpit.
//
// AP game string: "The Grinch" (inferred from world naming convention; verify
// against worlds/__init__.py).
//
// HOW THE INTEGRATION WORKS:
//   The Grinch runs on PCSX2. The launcher registers the game in the catalog.
//   The player supplies their own legal The Grinch PSX disc image.
//   ChecksImplemented is false until a PCSX2 AP bridge is built and verified.
//
// GATE TO ENABLE FULL INTEGRATION: build a PCSX2 bridge module for this game,
// verify in-emulator, and set ChecksImplemented = true.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class TheGrinchPlugin : EmulatorPlugin
{
    public TheGrinchPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "the_grinch_psx";
    public override string DisplayName => "The Grinch";
    public override string Subtitle    => "PSX · Emulated (PCSX2)";
    public override string ApWorldName => "The Grinch";

    public override string Description =>
        "The Grinch (2000, PlayStation) is the 3D platformer tie-in to the " +
        "Universal film. The Grinch descends from Mount Crumpit into Whoville " +
        "to ruin Christmas — stealing presents, sabotaging decorations, and " +
        "evading the suspicious townsfolk — before the Whos' spirit of the " +
        "season inspires a change of heart.\n\n" +
        "In the Archipelago randomizer, collectibles, stolen presents, and level " +
        "access join the multiworld pool. Ruin Christmas across every corner of " +
        "Whoville to complete your goal.\n\n" +
        "Requires: your own legal The Grinch PSX disc image and PCSX2. The " +
        "launcher will add full integrated PCSX2 support in a future update.";

    public override string ThemeAccentColor => "#3A7A3A";   // Grinch green

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "PSX";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "pcsx2" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "the_grinch_psx";

    public override bool ChecksImplemented => false;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();

    // Hides the base class property that returns EmulatorDirectory.
    public new string GameDirectory => string.Empty;
}
