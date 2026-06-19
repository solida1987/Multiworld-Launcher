using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// LuigisMansionPlugin — Archipelago integration for
// Luigi's Mansion (GameCube, 2001, GLME01 NTSC-U).
//
// ── CONFIRMED AP WORLD (github.com/BootsinSoots/Archipelago) ─────────────────
//
// A LIVE community Archipelago world exists for Luigi's Mansion (GameCube).
// Repository: github.com/BootsinSoots/Archipelago  (fork of ArchipelagoMW/Archipelago,
// by BootsinSoots).
//
//   • The apworld is distributed from the BootsinSoots/Archipelago fork.
//   • AP game string: "Luigi's Mansion" (inferred from repo/world name convention;
//     verify against worlds/__init__.py).
//   • The integration uses Dolphin and communicates via the Archipelago client
//     bundled in the fork, which reads GameCube RAM via Dolphin.
//
// HOW THE INTEGRATION WORKS:
//   Luigi's Mansion is a GameCube title; it runs in Dolphin. The AP client in
//   BootsinSoots' Archipelago fork connects to the AP server directly while
//   Dolphin runs. The launcher registers the game and links to the setup guide.
//   Dolphin is declared as the emulator ID (the catalog's GCN emulator slot).
//   BizHawk does not host a GCN core, so the Lua bridge is unavailable here.
//
// ISO: Luigi's Mansion NTSC-U (GLME01).
//   The player supplies their own legal GameCube disc image.
//
// GATE TO ENABLE FULL INTEGRATION: add Dolphin to EmulatorBackends (id "dolphin",
//   system "GCN") and implement a memory-read bridge. For now ChecksImplemented = false.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class LuigisMansionPlugin : EmulatorPlugin
{
    public LuigisMansionPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "luigis_mansion";
    public override string DisplayName => "Luigi's Mansion";
    public override string Subtitle    => "GameCube · Emulated (Dolphin)";

    // AP world game string — inferred from BootsinSoots/Archipelago.
    // Verify against worlds/__init__.py if the string differs.
    public override string ApWorldName => "Luigi's Mansion";

    public override string Description =>
        "Luigi's Mansion (2001, GameCube) is Nintendo's launch title in which Luigi " +
        "wins a mysterious mansion in a contest he never entered. Armed with the " +
        "Poltergust 3000, he must vacuum up ghosts, rescue Mario, and confront the " +
        "ghost king King Boo.\n\n" +
        "In the Archipelago randomizer (by BootsinSoots), Boos, portrait ghosts, " +
        "keys, and mansion treasures join the multiworld item pool. The integration " +
        "uses Dolphin — load your NTSC-U ISO (GLME01) and connect via the " +
        "Archipelago launcher client from github.com/BootsinSoots/Archipelago.\n\n" +
        "Requires: your own legal Luigi's Mansion NTSC-U GameCube ISO and Dolphin " +
        "Emulator. The launcher will add full integrated Dolphin support in a " +
        "future update.";

    public override string ThemeAccentColor => "#6AAF2E";   // Luigi green

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
    protected override string LuaModuleName => "luigis_mansion";

    // Checks are not yet implemented through the launcher bridge.
    public override bool ChecksImplemented => false;
}
