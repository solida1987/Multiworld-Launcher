using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// StreetsOfRagePlugin — Archipelago integration for Streets of Rage
// (Sega Genesis / Mega Drive, 1991), on the BizHawk Lua-pipe bridge.
//
// WORLD SOURCE: UltiNaruto/SOR_AP_Randomizer (community apworld).
//   game string: "Streets of Rage" — verify against worlds/__init__.py.
//   system:      GEN (Sega Genesis / Mega Drive).
//   emulator:    BizHawk (Genesis core).
//
// WHAT THE APWORLD RANDOMIZES:
//   The eight stages of Streets of Rage are beaten to unlock items and
//   progression shuffled into the multiworld pool. Character abilities,
//   special moves, and stage access may be randomized. Reaching and
//   defeating Mr. X completes the goal.
//
// STATUS: STUB — ChecksImplemented = false. No Lua memory module has been
// built and verified for this game through the launcher bridge. The plugin
// registers the game in the catalog so it appears in the UI.
//
// GATE TO ENABLE: build a streets_of_rage.lua module mapping Genesis RAM
// addresses to the apworld's location ids, verify in-emulator, then set
// ChecksImplemented = true.
//
// ROM: Streets of Rage (Sega Genesis / Mega Drive).
//   AcceptableBaseRoms is left with a display label only (no size or MD5
//   constraint) so any Genesis ROM the player provides is accepted at import.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class StreetsOfRagePlugin : EmulatorPlugin
{
    public StreetsOfRagePlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "streets_of_rage";
    public override string DisplayName => "Streets of Rage";
    public override string Subtitle    => "GEN · Emulated";
    public override string ApWorldName => "Streets of Rage";

    public override string Description =>
        "Streets of Rage is the classic 1991 Sega Genesis beat 'em up. Playing " +
        "as Axel Stone, Blaze Fielding, or Adam Hunter, you fight through eight " +
        "stages of criminals to reach and defeat the crime lord Mr. X. The game " +
        "is celebrated for its fluid combat, branching final stage, and the " +
        "iconic soundtrack by Yuzo Koshiro.\n\n" +
        "In the Archipelago randomizer (by UltiNaruto), stage access, special " +
        "moves, and key items join the multiworld pool. Beat every stage and " +
        "take down Mr. X to complete your goal.\n\n" +
        "Source: github.com/UltiNaruto/SOR_AP_Randomizer.";

    public override string ThemeAccentColor => "#C03020";   // Streets of Rage red

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "GEN";

    // BizHawk (Genesis core) is the documented emulator for the GEN system.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "streets_of_rage";

    // No Lua module built or verified yet — stub only.
    public override bool ChecksImplemented => false;

    // Hides the base class property that returns EmulatorDirectory; emulated
    // ROM games have no separate install directory.
    public new string GameDirectory => string.Empty;
}
