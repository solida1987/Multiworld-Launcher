using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// Pikmin2Plugin — Archipelago integration for
// Pikmin 2 (GameCube, 2004).
//
// CONFIRMED AP WORLD:
//   Community apworld for Pikmin 2 on GameCube.
//   AP game string: "Pikmin 2"
//   System: GameCube. Emulator: Dolphin.
//
// WHAT THE APWORLD RANDOMIZES:
//   Treasures, cave floors, captain rescues, and Pikmin type unlocks form
//   the multiworld location pool. The player must collect enough Pokos' worth
//   of treasures to pay off Hocotate Freight's debt and complete the goal.
//
// STATUS: STUB — ChecksImplemented = false. Dolphin is not yet in
//   EmulatorBackends and no launcher bridge module exists for this game.
//   The plugin registers the game so it appears in the UI.
//
// GATE TO ENABLE: add Dolphin to EmulatorBackends (id "dolphin", system "GC"),
//   build a bridge module, verify in-emulator, set ChecksImplemented = true.
//
// ISO: Pikmin 2 (NTSC-U, game ID G2ME01).
//   AcceptableBaseRoms is empty — no constraint — so any GCN ISO the player
//   provides is accepted.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Pikmin2Plugin : EmulatorPlugin
{
    public Pikmin2Plugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "pikmin_2";
    public override string DisplayName => "Pikmin 2";
    public override string Subtitle    => "GameCube · Emulated (Dolphin)";
    public override string ApWorldName => "Pikmin 2";

    public override string Description =>
        "Pikmin 2 is Nintendo's 2004 GameCube strategy game in which Olimar and " +
        "Louie return to the Pikmin Planet to collect treasures and pay off Hocotate " +
        "Freight's enormous debt. Two captains, a vast collection of real-world " +
        "artifact treasures, and brutal underground caves full of monsters and " +
        "puzzles expand the original game into a much deeper adventure.\n\n" +
        "In the Archipelago randomizer, treasures, cave floors, captain rescues, and " +
        "Pikmin type unlocks join the multiworld pool. Collect enough Pokos' worth of " +
        "treasures to settle the debt and complete your goal.\n\n" +
        "Requires: your own legal NTSC-U GameCube ISO (G2ME01) and Dolphin Emulator. " +
        "The launcher will add integrated Dolphin support in a future update.";

    public override string ThemeAccentColor => "#E84000";   // Pikmin 2 red / Bulborb

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "GC";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "dolphin" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "pikmin_2";

    public override bool ChecksImplemented => false;

    public new string GameDirectory => string.Empty;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();
}
