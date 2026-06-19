using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// ToeJamAndEarlPlugin — Archipelago integration for ToeJam & Earl
// (Sega Genesis / Mega Drive, 1991), on the BizHawk Lua-pipe bridge.
//
// WORLD SOURCE: IgnisUmbrae/TJE-Archipelago (community apworld).
//   game string: "ToeJam and Earl" — verify against worlds/__init__.py.
//   system:      GEN (Sega Genesis / Mega Drive).
//   emulator:    BizHawk (Genesis core).
//
// WHAT THE APWORLD RANDOMIZES:
//   The 25 levels of ToeJam & Earl's procedurally generated world are explored
//   to find ship pieces and other items shuffled into the multiworld pool.
//   Collecting all ship pieces and reaching the top level completes the goal.
//
// STATUS: STUB — ChecksImplemented = false. No Lua memory module has been
// built and verified for this game through the launcher bridge. The plugin
// registers the game in the catalog so it appears in the UI.
//
// GATE TO ENABLE: build a tje.lua module mapping Genesis RAM addresses to
// the apworld's location ids, verify in-emulator, set ChecksImplemented = true.
//
// ROM: ToeJam & Earl (Sega Genesis / Mega Drive).
//   AcceptableBaseRoms is left with a display label only (no size or MD5
//   constraint) so any Genesis ROM the player provides is accepted at import.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ToeJamAndEarlPlugin : EmulatorPlugin
{
    public ToeJamAndEarlPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "toejam_and_earl";
    public override string DisplayName => "ToeJam & Earl";
    public override string Subtitle    => "GEN · Emulated";
    public override string ApWorldName => "ToeJam and Earl";

    public override string Description =>
        "ToeJam & Earl is the 1991 Sega Genesis cult classic where two funky " +
        "alien rappers crash-land on Earth and must collect the scattered pieces " +
        "of their spaceship across 25 randomly generated levels. The game blends " +
        "top-down exploration with a unique roguelike structure, letting you " +
        "discover presents, hitch rides with Santas, and dodge the bizarre " +
        "denizens of Earth.\n\n" +
        "In the Archipelago randomizer (by IgnisUmbrae), ship pieces and key " +
        "items join the multiworld pool. Collect all ship pieces and reach the " +
        "top to complete your goal.\n\n" +
        "Source: github.com/IgnisUmbrae/TJE-Archipelago.";

    public override string ThemeAccentColor => "#E8A020";   // ToeJam & Earl gold

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "GEN";

    // BizHawk (Genesis core) is the documented emulator for the GEN system.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "toejam_and_earl";

    // No Lua module built or verified yet — stub only.
    public override bool ChecksImplemented => false;

    // Hides the base class property that returns EmulatorDirectory; emulated
    // ROM games have no separate install directory.
    public new string GameDirectory => string.Empty;
}
