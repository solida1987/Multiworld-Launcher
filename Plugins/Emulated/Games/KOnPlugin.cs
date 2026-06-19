using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// KOnPlugin — Archipelago integration for
// K-On! Houkago Live!! (PSP, 2010).
//
// CONFIRMED AP WORLD:
//   Community apworld for K-On! Houkago Live!!
//   AP game string: "K-On! Houkago Live!!"
//   System: PSP. Emulator: PPSSPP.
//
// WHAT THE APWORLD RANDOMIZES:
//   Songs, outfits, practice sessions, band member unlocks, and Live events
//   join the multiworld pool. The player must complete the required
//   performances to reach the final goal.
//
// STATUS: STUB — ChecksImplemented = false. No PPSSPP bridge module
//   exists for this game yet. The plugin registers the game so it appears
//   in the UI catalog.
//
// GATE TO ENABLE: build a PPSSPP bridge module, verify in-emulator,
//   and set ChecksImplemented = true.
//
// ISO/CSO: K-On! Houkago Live!! PSP image (JPN).
//   AcceptableBaseRoms is empty — no constraint applied at this stub stage.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class KOnPlugin : EmulatorPlugin
{
    public KOnPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "kon_houkago_live";
    public override string DisplayName => "K-On! Houkago Live!!";
    public override string Subtitle    => "PSP · Emulated (PPSSPP)";
    public override string ApWorldName => "K-On! Houkago Live!!";

    public override string Description =>
        "K-On! Houkago Live!! (2010, PSP) is the rhythm game adaptation of the " +
        "beloved anime series following the Light Music Club at Sakuragaoka High " +
        "School. Players perform as Yui, Mio, Ritsu, Tsumugi, and Azusa through " +
        "memorable songs from the show, unlocking new outfits, practice events, " +
        "and Live performances as the club strives to put on a perfect concert.\n\n" +
        "In the Archipelago randomizer, songs, outfits, practice sessions, and " +
        "Live events join the multiworld pool. Complete the required performances " +
        "to reach your goal.\n\n" +
        "Requires: your own legal K-On! Houkago Live!! PSP image and PPSSPP. " +
        "The launcher will add integrated PPSSPP support in a future update.";

    public override string ThemeAccentColor => "#E86090";   // K-On sakura pink

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "PSP";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "ppsspp" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "kon_houkago_live";

    public override bool ChecksImplemented => false;

    public new string GameDirectory => string.Empty;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();
}
