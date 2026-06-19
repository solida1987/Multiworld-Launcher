using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// FinalFantasyTacticsA2Plugin — Archipelago integration for Final Fantasy
// Tactics A2: Grimoire of the Rift (Nintendo DS, 2007/2008).
//
// ── CONFIRMED AP WORLD ───────────────────────────────────────────────────────
//
// A community Archipelago world exists for FF Tactics A2. The apworld
// randomizes mission rewards, clan privileges, loot, and job unlocks across
// Ivalice in the DS sequel to Final Fantasy Tactics Advance.
//
// NOTE: This is a SEPARATE game and apworld from FFTacticsAdvancePlugin (GBA).
// FFTA (GBA) lives in FFTacticsAdvancePlugin.cs. This plugin covers FFTA2 (DS).
//
// AP game string: "Final Fantasy Tactics A2: Grimoire of the Rift"
// (verify against world __init__.py)
//
// HOW THE INTEGRATION WORKS:
//   The game runs on BizHawk (melonDS core) or melonDS standalone.
//   The player supplies their own legal NDS cartridge dump.
//   ChecksImplemented is false until a BizHawk Lua bridge is built and
//   verified in-emulator.
//
// GATE TO ENABLE FULL INTEGRATION: implement a BizHawk Lua module that reads
// the mission completion flags from NDS Main RAM, wire up item delivery, and
// flip ChecksImplemented to true.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class FinalFantasyTacticsA2Plugin : EmulatorPlugin
{
    public FinalFantasyTacticsA2Plugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "ffta2";
    public override string DisplayName => "Final Fantasy Tactics A2: Grimoire of the Rift";
    public override string Subtitle    => "DS · Emulated";
    public override string ApWorldName => "Final Fantasy Tactics A2: Grimoire of the Rift";

    public override string Description =>
        "Final Fantasy Tactics A2: Grimoire of the Rift is the Nintendo DS " +
        "sequel to Final Fantasy Tactics Advance. Luso Clemens is transported " +
        "from his school library into the world of Ivalice via the mysterious " +
        "Grimoire of the Rift, and must complete missions, master jobs, and " +
        "recruit clan members to find his way home.\n\n" +
        "In the Archipelago randomizer, mission rewards, clan privileges, loot, " +
        "and job progression across Ivalice join the multiworld pool. Complete " +
        "your final mission to achieve your goal.\n\n" +
        "Requires: your own legal FF Tactics A2 NDS cartridge dump and BizHawk. " +
        "Full check integration will be enabled in a future update.";

    public override string ThemeAccentColor => "#5A3A8A";   // Ivalice violet

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "NDS";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "ffta2";

    public override bool ChecksImplemented => false;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        System.Array.Empty<RomIdentity>();

    // Hides the base class property that returns EmulatorDirectory.
    public new string GameDirectory => string.Empty;
}
