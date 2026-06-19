using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// Shadowgate64Plugin — Archipelago integration for Shadowgate 64: Trials of
// the Four Towers (Nintendo 64, 1999).
//
// ── CONFIRMED AP WORLD ───────────────────────────────────────────────────────
//
// A community Archipelago world exists for Shadowgate 64. The apworld
// randomizes key items, spells, and puzzle solutions across the four towers
// of the dark castle Shadowgate.
//
// AP game string: "Shadowgate 64" (verify against world __init__.py)
//
// HOW THE INTEGRATION WORKS:
//   The game runs on BizHawk (Mupen64Plus or Ares N64 core).
//   The player supplies their own legal N64 ROM dump.
//   ChecksImplemented is false until a BizHawk Lua bridge is built and
//   verified in-emulator.
//
// GATE TO ENABLE FULL INTEGRATION: implement a BizHawk Lua module that reads
// the check flags from N64 RDRAM, wire up item delivery, and flip
// ChecksImplemented to true.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Shadowgate64Plugin : EmulatorPlugin
{
    public Shadowgate64Plugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "shadowgate64";
    public override string DisplayName => "Shadowgate 64: Trials of the Four Towers";
    public override string Subtitle    => "N64 · Emulated";
    public override string ApWorldName => "Shadowgate 64";

    public override string Description =>
        "Shadowgate 64: Trials of the Four Towers is the 1999 Nintendo 64 " +
        "reimagining of the classic MacVenture point-and-click adventure. Del " +
        "Cottonwood must survive four towers of increasingly deadly traps and " +
        "puzzles inside the dark castle of Shadowgate, collecting keys, spells, " +
        "and forbidden relics to escape the Warlock Lord's clutches.\n\n" +
        "In the Archipelago randomizer, key items, spells, and puzzle progression " +
        "across the four towers join the multiworld pool. Escape Shadowgate to " +
        "complete your goal.\n\n" +
        "Requires: your own legal Shadowgate 64 N64 ROM dump and BizHawk. Full " +
        "check integration will be enabled in a future update.";

    public override string ThemeAccentColor => "#4A3A2A";   // Shadowgate stone brown

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "N64";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "shadowgate64";

    public override bool ChecksImplemented => false;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        System.Array.Empty<RomIdentity>();

    // Hides the base class property that returns EmulatorDirectory.
    public new string GameDirectory => string.Empty;
}
