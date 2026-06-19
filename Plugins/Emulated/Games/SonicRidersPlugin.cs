using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// SonicRidersPlugin — Archipelago integration for
// Sonic Riders (GameCube, 2006).
//
// CONFIRMED AP WORLD:
//   Community apworld for Sonic Riders.
//   AP game string: "Sonic Riders"
//   System: GameCube. Emulator: Dolphin.
//
// WHAT THE APWORLD RANDOMIZES:
//   Race courses, Extreme Gear unlocks, character unlocks, and story missions
//   join the multiworld pool. The player must complete the required races
//   and defeat Babylon Guardian to reach the final goal.
//
// STATUS: STUB — ChecksImplemented = false. Dolphin is not yet in
//   EmulatorBackends and no launcher bridge module exists for this game.
//   The plugin registers the game so it appears in the UI.
//
// GATE TO ENABLE: add Dolphin to EmulatorBackends (id "dolphin", system "GC"),
//   build a bridge module, verify in-emulator, set ChecksImplemented = true.
//
// ISO: Sonic Riders (NTSC-U, game ID GSNE8P / regional).
//   AcceptableBaseRoms is empty — no constraint — so any GCN ISO the player
//   provides is accepted.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SonicRidersPlugin : EmulatorPlugin
{
    public SonicRidersPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "sonic_riders";
    public override string DisplayName => "Sonic Riders";
    public override string Subtitle    => "GameCube · Emulated (Dolphin)";
    public override string ApWorldName => "Sonic Riders";

    public override string Description =>
        "Sonic Riders is Sonic Team's 2006 GameCube hoverboard racing game in which " +
        "Sonic, Tails, Knuckles, and the mysterious Babylon Rogues compete across " +
        "high-speed anti-gravity circuits. Riders are propelled by Air, a limited " +
        "fuel resource that determines both speed and the tricks players can perform " +
        "mid-race.\n\n" +
        "In the Archipelago randomizer, race courses, Extreme Gear, character " +
        "unlocks, and story missions join the multiworld pool. Complete the required " +
        "races and defeat the Babylon Guardian to reach your goal.\n\n" +
        "Requires: your own legal GameCube ISO and Dolphin Emulator. " +
        "The launcher will add integrated Dolphin support in a future update.";

    public override string ThemeAccentColor => "#0070C0";   // Sonic blue

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "GC";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "dolphin" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "sonic_riders";

    public override bool ChecksImplemented => false;

    public new string GameDirectory => string.Empty;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();
}
