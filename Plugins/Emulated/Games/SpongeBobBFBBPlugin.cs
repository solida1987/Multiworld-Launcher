using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// SpongeBobBFBBPlugin — Archipelago integration for
// SpongeBob SquarePants: Battle for Bikini Bottom (GameCube, 2003),
// on the Dolphin bridge.
//
// WORLD SOURCE: Cyb3RGER/bfbb_ap_world (community apworld).
//   game string: "SpongeBob SquarePants: Battle for Bikini Bottom" — verify
//                against worlds/__init__.py.
//   system:      GC (GameCube).
//   emulator:    Dolphin (GameCube).
//
// WHAT THE APWORLD RANDOMIZES:
//   Shiny objects, golden spatulas, socks, and level access are shuffled into
//   the multiworld pool. Collect golden spatulas to unlock worlds and reach
//   the final confrontation with Robo-Plankton to complete the goal.
//
// STATUS: STUB — ChecksImplemented = false. Dolphin is not yet in the
// EmulatorBackends catalog and no bridge module exists for this game through
// the launcher. The plugin registers the game so it appears in the UI.
//
// GATE TO ENABLE: add Dolphin to EmulatorBackends (id "dolphin", system "GC"),
// build a bfbb bridge module, verify in-emulator, set ChecksImplemented = true.
//
// ISO: SpongeBob SquarePants: Battle for Bikini Bottom (GameCube).
//   AcceptableBaseRoms is left with a display label only (no size or MD5
//   constraint) so any GCN ISO the player provides is accepted at import.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SpongeBobBFBBPlugin : EmulatorPlugin
{
    public SpongeBobBFBBPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "spongebob_bfbb";
    public override string DisplayName => "SpongeBob: Battle for Bikini Bottom";
    public override string Subtitle    => "GameCube · Emulated (Dolphin)";
    public override string ApWorldName => "SpongeBob SquarePants: Battle for Bikini Bottom";

    public override string Description =>
        "SpongeBob SquarePants: Battle for Bikini Bottom is the beloved 2003 " +
        "GameCube 3D platformer. Plankton's robot army has invaded Bikini " +
        "Bottom and SpongeBob, Patrick, and Sandy must fight through eight " +
        "worlds — from the Krusty Krab to Rock Bottom to the Flying Dutchman's " +
        "Graveyard — collecting golden spatulas to unlock new areas and save " +
        "the day.\n\n" +
        "In the Archipelago randomizer (by Cyb3RGER), golden spatulas, socks, " +
        "shiny objects, and level gates join the multiworld pool. Collect " +
        "enough spatulas to reach and defeat Robo-Plankton to complete your " +
        "goal.\n\n" +
        "NOTE: This game uses the Dolphin emulator. The launcher will add " +
        "integrated Dolphin support in a future update.\n\n" +
        "Source: github.com/Cyb3RGER/bfbb_ap_world.";

    public override string ThemeAccentColor => "#F5C518";   // SpongeBob yellow

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
    protected override string LuaModuleName => "spongebob_bfbb";

    // No bridge module built or verified yet — stub only.
    public override bool ChecksImplemented => false;

    // Hides the base class property that returns EmulatorDirectory; emulated
    // ROM games have no separate install directory.
    public new string GameDirectory => string.Empty;
}
