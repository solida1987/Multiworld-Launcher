using System.Collections.Generic;
using System.Windows;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// KirbyAirRidePlugin — Archipelago integration for Kirby Air Ride (GameCube),
// the apworld "Kirby Air Ride" by DeDeDeK
// (github.com/DeDeDeK/KARchipelago, worlds/kirby_air_ride).
//
// STATUS: STUB — Dolphin is not yet in the EmulatorBackends catalog, and the
// KAR apworld ships its own standalone Python client (KARClient.py, launched as
// "Kirby Air Ride Client" via ArchipelagoLauncher) that reads Dolphin memory
// directly via Dolphin Memory Engine (DME). It does NOT use the BizHawk Lua-pipe
// bridge employed by every other emulated plugin here. ChecksImplemented stays
// false; ConnectsItself stays false. The plugin registers the game in the catalog
// so it appears in the UI and the description explains the current setup path.
//
// GATE TO ENABLE: add Dolphin to EmulatorBackends (id "dolphin", system "GCN",
// BridgeReady=false until a Lua or socket bridge is confirmed) and/or integrate
// the KARClient launcher. Then flip ChecksImplemented and update the description.
//
// AP WORLD: DeDeDeK/KARchipelago (worlds/kirby_air_ride).
//   game string:  "Kirby Air Ride"
//   client class: KARClient (extends BizHawkClient family via DME, not Lua pipe)
//   game modes:   Air Ride, City Trial, Top Ride — checklist-block locations.
//   goal:         collect N checklist blocks (customisable) or 100 blocks across
//                 enabled modes; City Trial goal includes beating King Dedede.
//
// ISO: NTSC-U only (game ID GKYE01).
//   CRC32: f1a3e7a2
//   MD5:   bd936616ba7f998d8d0a1eb3f553b634
//
// EMULATOR: Dolphin (latest release recommended). The client auto-detects the
// Dolphin process; the player enables a Riivolution mod before booting the game.
// PAL (GKYP01) and Japan (GKYJ01) ISOs are explicitly rejected by the client.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class KirbyAirRidePlugin : EmulatorPlugin
{
    public KirbyAirRidePlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "kirby_air_ride";
    public override string DisplayName => "Kirby Air Ride";
    public override string Subtitle    => "GameCube · Emulated (Dolphin)";

    // Exact AP world game string from worlds/kirby_air_ride/__init__.py.
    public override string ApWorldName => "Kirby Air Ride";

    public override string Description =>
        "Kirby Air Ride is Nintendo's 2003 GameCube racer with three distinct " +
        "modes: Air Ride (single-lap courses on copy-ability machines), City " +
        "Trial (a free-roaming sandbox where you build the best machine before " +
        "a final event), and Top Ride (compact top-down tracks). In the " +
        "Archipelago randomizer (by DeDeDeK), checklist blocks across all three " +
        "modes form the location pool — complete boxes, unlock machines, and " +
        "defeat King Dedede in City Trial to reach your goal.\n\n" +
        "NOTE: This game uses the Dolphin emulator and the standalone Kirby Air " +
        "Ride Archipelago client from github.com/DeDeDeK/KARchipelago. Enable " +
        "the Riivolution mod in Dolphin before booting, then launch the " +
        "\"Kirby Air Ride Client\" from ArchipelagoLauncher to connect your room. " +
        "NTSC-U ISO only (game ID GKYE01). The launcher will add integrated " +
        "Dolphin support in a future update.";

    public override string ThemeAccentColor => "#3DB8E8";   // sky blue (Air Ride)

    // ── Emulator specifics ────────────────────────────────────────────────────

    // Dolphin is the canonical GameCube emulator; it is not yet in
    // EmulatorBackends (BizHawk does not host GCN), so AvailableBackends()
    // returns an empty list and the UI shows the honest "coming soon" state.
    protected override string RomSystem => "GCN";

    // Declare Dolphin so the intent is recorded; the base class resolves this
    // against EmulatorBackends.BackendsForSystem("GCN") which currently returns
    // nothing — a graceful no-op rather than a silent wrong default.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "dolphin" };

    // KARClient uses direct Dolphin Memory Engine reads, not a BizHawk Lua
    // module. These values are kept as documentation; the Lua bridge is not
    // used and LuaModuleName is not exercised while ChecksImplemented is false.
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "kirby_air_ride";

    // Checks are NOT implemented through the launcher bridge: the reference
    // client is the standalone KARClient.py / Kirby Air Ride Client exe.
    public override bool ChecksImplemented => false;
}
