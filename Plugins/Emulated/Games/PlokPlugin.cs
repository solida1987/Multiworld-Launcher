using System;
using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// PlokPlugin — Archipelago integration for Plok
// (SNES, 1993), on the BizHawk Lua-pipe bridge.
//
// WORLD SOURCE: ArchipelagoMW/Archipelago (AP-main).
//   game string: "Plok"
//   system:      SNES.
//   emulator:    BizHawk (SNESHawk / BSNES core).
//
// STATUS: STUB — ChecksImplemented = false. No Lua memory module has been
// built and verified for this game through the launcher bridge. The plugin
// registers the game in the catalog so it appears in the UI.
//
// GATE TO ENABLE: build a plok.lua module mapping SNES RAM addresses to the
// apworld's location ids, verify in-emulator, then set ChecksImplemented = true.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PlokPlugin : EmulatorPlugin
{
    public PlokPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "plok";
    public override string DisplayName => "Plok";
    public override string Subtitle    => "SNES · Emulated";
    public override string ApWorldName => "Plok";

    public override string Description =>
        "Plok (1993, Software Creations / Tradewest) is a SNES platformer starring " +
        "Plok, a flag-collecting creature from Akrillic Island. Plok can fire his " +
        "own limbs as projectiles and must reclaim his ancestor's flags that were " +
        "stolen by fleas. The game is known for its unusual graphical style, " +
        "challenging flea boss encounters, and a memorable soundtrack by Tim and " +
        "Geoff Follin.\n\n" +
        "In the Archipelago randomizer, flag recoveries, power-up abilities, and " +
        "stage progression checks join the multiworld pool. Recover all flags and " +
        "complete Akrillic Island to finish your goal.\n\n" +
        "Source: github.com/ArchipelagoMW/Archipelago (AP-main).";

    public override string ThemeAccentColor => "#FF6600";   // Plok orange

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "SNES";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "plok";

    // No Lua module built or verified yet — stub only.
    public override bool ChecksImplemented => false;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();

    // Hides the base class property that returns EmulatorDirectory; emulated
    // ROM games have no separate install directory.
    public new string GameDirectory => string.Empty;
}
