using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// ZeldaPhantomHourglassPlugin — Archipelago integration for The Legend of
// Zelda: Phantom Hourglass (Nintendo DS, 2007).
//
// ── CONFIRMED AP WORLD ───────────────────────────────────────────────────────
//
// A community Archipelago world exists for Phantom Hourglass. The apworld
// randomizes dungeon items, sea chart pieces, spirit gems, and key items
// across the World of the Ocean King.
//
// AP game string: "Zelda: Phantom Hourglass" (verify against world __init__.py)
//
// HOW THE INTEGRATION WORKS:
//   The game runs on BizHawk (melonDS core) or melonDS standalone.
//   The player supplies their own legal NDS cartridge dump.
//   ChecksImplemented is false until a BizHawk Lua bridge is built and
//   verified in-emulator.
//
// GATE TO ENABLE FULL INTEGRATION: implement a BizHawk Lua module that reads
// the check flags from NDS Main RAM, wire up item delivery, and flip
// ChecksImplemented to true.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ZeldaPhantomHourglassPlugin : EmulatorPlugin
{
    public ZeldaPhantomHourglassPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "zelda_phantom_hourglass";
    public override string DisplayName => "Zelda: Phantom Hourglass";
    public override string Subtitle    => "DS · Emulated";
    public override string ApWorldName => "Zelda: Phantom Hourglass";

    public override string Description =>
        "The Legend of Zelda: Phantom Hourglass is the Nintendo DS sequel to " +
        "The Wind Waker. Link and Tetra search for the Ghost Ship across the " +
        "World of the Ocean King, navigating sea charts, conquering the Temple " +
        "of the Ocean King, and collecting Spirit Gems to power Leaf, Neri, and " +
        "Ciela.\n\n" +
        "In the Archipelago randomizer, dungeon items, sea chart pieces, spirit " +
        "gems, and key items across the ocean join the multiworld pool. Defeat " +
        "Bellum to complete your goal.\n\n" +
        "Requires: your own legal Phantom Hourglass NDS cartridge dump and " +
        "BizHawk. Full check integration will be enabled in a future update.";

    public override string ThemeAccentColor => "#2A7EB5";   // Ocean King blue

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "NDS";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "zelda_phantom_hourglass";

    public override bool ChecksImplemented => false;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        System.Array.Empty<RomIdentity>();

    // Hides the base class property that returns EmulatorDirectory.
    public new string GameDirectory => string.Empty;
}
