using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// SimpsonsHitAndRunPlugin — Archipelago integration for
// The Simpsons: Hit & Run (GameCube, 2003).
//
// CONFIRMED AP WORLD:
//   Community apworld for The Simpsons: Hit & Run.
//   AP game string: "The Simpsons: Hit & Run"
//   System: GameCube. Emulator: Dolphin.
//
// WHAT THE APWORLD RANDOMIZES:
//   Missions, collector cards, wasp cameras, and level unlocks join the
//   multiworld pool. The player must complete the required missions across
//   Springfield's seven levels to reach the final goal.
//
// STATUS: STUB — ChecksImplemented = false. Dolphin is not yet in
//   EmulatorBackends and no launcher bridge module exists for this game.
//   The plugin registers the game so it appears in the UI.
//
// GATE TO ENABLE: add Dolphin to EmulatorBackends (id "dolphin", system "GC"),
//   build a bridge module, verify in-emulator, set ChecksImplemented = true.
//
// ISO: The Simpsons: Hit & Run (NTSC-U, game ID GSZE5D / GSZP5D / regional).
//   AcceptableBaseRoms is empty — no constraint — so any GCN ISO the player
//   provides is accepted.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SimpsonsHitAndRunPlugin : EmulatorPlugin
{
    public SimpsonsHitAndRunPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "simpsons_hit_and_run";
    public override string DisplayName => "The Simpsons: Hit & Run";
    public override string Subtitle    => "GameCube · Emulated (Dolphin)";
    public override string ApWorldName => "The Simpsons: Hit & Run";

    public override string Description =>
        "The Simpsons: Hit & Run is Radical Entertainment's 2003 GameCube open-world " +
        "action game set across seven levels of Springfield. Playing as Homer, Bart, " +
        "Lisa, Marge, and Apu, players drive, run, and cause chaos to uncover a " +
        "sinister conspiracy involving mysterious black vans and wasp cameras.\n\n" +
        "In the Archipelago randomizer, missions, collector cards, wasp cameras, and " +
        "level unlocks join the multiworld pool. Complete the required missions across " +
        "Springfield to reach your goal.\n\n" +
        "Requires: your own legal GameCube ISO and Dolphin Emulator. " +
        "The launcher will add integrated Dolphin support in a future update.";

    public override string ThemeAccentColor => "#F5BD00";   // Simpsons yellow

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "GC";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "dolphin" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "simpsons_hit_and_run";

    public override bool ChecksImplemented => false;

    public new string GameDirectory => string.Empty;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();
}
