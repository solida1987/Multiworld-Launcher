using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// ScoobyDooNightOf100FrightsPlugin — Archipelago integration for
// Scooby-Doo! Night of 100 Frights (GameCube, 2002, GSDE7D NTSC-U).
//
// ── CONFIRMED AP WORLD (github.com/vgm5/Night_Of_100_Frights_ap_world) ───────
//
// A LIVE community Archipelago world exists for Scooby-Doo! Night of 100 Frights
// (GameCube). Repository: github.com/vgm5/Night_Of_100_Frights_ap_world
// (maintained by vgm5).
//
//   • The apworld is distributed from vgm5's repository as a standalone
//     night_of_100_frights.apworld (or similar name) installed into the AP server.
//   • AP game string: "Scooby-Doo! Night of 100 Frights" (inferred from repository
//     and world naming convention; verify against __init__.py).
//   • The integration runs on Dolphin; the AP client connects while Dolphin runs
//     with the player's GameCube ISO.
//
// HOW THE INTEGRATION WORKS:
//   This is a GameCube title running in Dolphin. The AP client from vgm5's repo
//   communicates with the AP server while Dolphin is running. The launcher
//   registers the game in the catalog and links to the setup resources. Dolphin
//   is declared as the emulator ID (the catalog's GCN emulator slot). BizHawk
//   does not host a GCN core, so the Lua bridge is unavailable here.
//
// ISO: Scooby-Doo! Night of 100 Frights NTSC-U (GSDE7D).
//   The player supplies their own legal GameCube disc image.
//
// GATE TO ENABLE FULL INTEGRATION: add Dolphin to EmulatorBackends (id "dolphin",
//   system "GCN") and implement a memory-read bridge. For now ChecksImplemented = false.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ScoobyDooNightOf100FrightsPlugin : EmulatorPlugin
{
    public ScoobyDooNightOf100FrightsPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "scooby_doo_night_of_100_frights";
    public override string DisplayName => "Scooby-Doo! Night of 100 Frights";
    public override string Subtitle    => "GameCube · Emulated (Dolphin)";

    // AP world game string — inferred from vgm5/Night_Of_100_Frights_ap_world.
    // Verify against worlds/__init__.py if the string differs.
    public override string ApWorldName => "Scooby-Doo! Night of 100 Frights";

    public override string Description =>
        "Scooby-Doo! Night of 100 Frights (2002, GameCube) is a 3D platformer in " +
        "which Scooby-Doo must rescue the Mystery Inc. gang from the Mastermind's " +
        "haunted mansion. Armed with Scooby Snacks and power-ups from Professor " +
        "Pericles, Scooby explores the mansion grounds, underground caves, and " +
        "spooky secret passages.\n\n" +
        "In the Archipelago randomizer (by vgm5), power-ups, keys, and collectibles " +
        "across the mansion grounds join the multiworld item pool. The integration " +
        "uses Dolphin — load your NTSC-U ISO (GSDE7D) and connect via the " +
        "Archipelago launcher client from " +
        "github.com/vgm5/Night_Of_100_Frights_ap_world.\n\n" +
        "Requires: your own legal Scooby-Doo! Night of 100 Frights NTSC-U " +
        "GameCube ISO and Dolphin Emulator. The launcher will add full integrated " +
        "Dolphin support in a future update.";

    public override string ThemeAccentColor => "#B05A00";   // Scooby-Doo mystery orange-brown

    // ── Emulator specifics ────────────────────────────────────────────────────

    // Dolphin is the canonical GameCube emulator. It is not yet in EmulatorBackends
    // (BizHawk does not host a GCN core), so AvailableBackends() returns an empty
    // list and the UI shows the honest "not yet configured" state.
    protected override string RomSystem => "GC";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "dolphin" };

    // No Lua module exists for this game through the BizHawk bridge.
    // These values are kept as documentation; LuaModuleName is not exercised
    // while ChecksImplemented is false.
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "scooby_doo_night_of_100_frights";

    // Checks are not yet implemented through the launcher bridge.
    public override bool ChecksImplemented => false;
}
