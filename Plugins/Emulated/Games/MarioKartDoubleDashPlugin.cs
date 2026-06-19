using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// MarioKartDoubleDashPlugin — Archipelago integration for
// Mario Kart: Double Dash!! (GameCube, 2003), on the Dolphin bridge.
//
// WORLD SOURCE: aXu-AP/archipelago-double-dash (community apworld).
//   game string: "Mario Kart: Double Dash!!" — verify against worlds/__init__.py.
//   system:      GC (GameCube).
//   emulator:    Dolphin (GameCube).
//
// WHAT THE APWORLD RANDOMIZES:
//   Cups, characters, karts, and items are shuffled into the multiworld pool.
//   Players race through Grand Prix cups and unlock content from the multiworld.
//   Completing the required cups at the required trophy levels finishes the goal.
//
// STATUS: STUB — ChecksImplemented = false. Dolphin is not yet in the
// EmulatorBackends catalog and no bridge module exists for this game through
// the launcher. The plugin registers the game so it appears in the UI.
//
// GATE TO ENABLE: add Dolphin to EmulatorBackends (id "dolphin", system "GC"),
// build a double_dash bridge module, verify in-emulator, set
// ChecksImplemented = true.
//
// ISO: Mario Kart: Double Dash!! (NTSC-U, game ID GM4E01).
//   AcceptableBaseRoms is left with a display label only (no size or MD5
//   constraint) so any GCN ISO the player provides is accepted at import.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MarioKartDoubleDashPlugin : EmulatorPlugin
{
    public MarioKartDoubleDashPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "mario_kart_double_dash";
    public override string DisplayName => "Mario Kart: Double Dash!!";
    public override string Subtitle    => "GameCube · Emulated (Dolphin)";
    public override string ApWorldName => "Mario Kart: Double Dash!!";

    public override string Description =>
        "Mario Kart: Double Dash!! is Nintendo's 2003 GameCube racer famous for " +
        "its unique two-character kart system, where two racers ride together and " +
        "can swap positions mid-race. Sixteen tracks across four cups and a " +
        "diverse roster of Nintendo characters made it one of the best-selling " +
        "GameCube titles.\n\n" +
        "In the Archipelago randomizer (by aXu-AP), cups, characters, karts, and " +
        "special items join the multiworld pool. Race through the Grand Prix cups " +
        "and unlock content from the multiworld to reach your goal.\n\n" +
        "NOTE: This game uses the Dolphin emulator. NTSC-U ISO only " +
        "(game ID GM4E01). The launcher will add integrated Dolphin support in a " +
        "future update.\n\n" +
        "Source: github.com/aXu-AP/archipelago-double-dash.";

    public override string ThemeAccentColor => "#E82020";   // Double Dash red

    // ── Emulator specifics ────────────────────────────────────────────────────

    // Dolphin is the canonical GameCube emulator; it is not yet in
    // EmulatorBackends (BizHawk does not host GCN), so AvailableBackends()
    // returns an empty list and the UI shows the honest "coming soon" state.
    protected override string RomSystem => "GC";

    // Declare Dolphin so the intent is recorded; the base class resolves this
    // against EmulatorBackends.BackendsForSystem("GC") which currently returns
    // nothing — a graceful no-op rather than a silent wrong default.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "dolphin" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "mario_kart_double_dash";

    // No bridge module built or verified yet — stub only.
    public override bool ChecksImplemented => false;

    // Hides the base class property that returns EmulatorDirectory; emulated
    // ROM games have no separate install directory.
    public new string GameDirectory => string.Empty;
}
