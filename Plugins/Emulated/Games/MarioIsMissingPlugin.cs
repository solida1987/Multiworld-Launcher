using System;
using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// MarioIsMissingPlugin — Archipelago integration for Mario is Missing!
// (SNES, 1993), on the BizHawk Lua-pipe bridge.
//
// WORLD SOURCE: ArchipelagoMW/Archipelago (AP-main).
//   game string: "Mario is Missing!"
//   system:      SNES.
//   emulator:    BizHawk (SNESHawk / BSNES core).
//
// STATUS: STUB — ChecksImplemented = false. No Lua memory module has been
// built and verified for this game through the launcher bridge. The plugin
// registers the game in the catalog so it appears in the UI.
//
// GATE TO ENABLE: build a mario_is_missing.lua module mapping SNES RAM
// addresses to the apworld's location ids, verify in-emulator, then set
// ChecksImplemented = true.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MarioIsMissingPlugin : EmulatorPlugin
{
    public MarioIsMissingPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "mario_is_missing";
    public override string DisplayName => "Mario is Missing!";
    public override string Subtitle    => "SNES · Emulated";
    public override string ApWorldName => "Mario is Missing!";

    public override string Description =>
        "Mario is Missing! (1993, Software Toolworks / Nintendo) is an educational " +
        "SNES platformer where Luigi must rescue Mario from Bowser's castle. Bowser " +
        "and his Koopa minions have stolen artifacts from famous real-world landmarks " +
        "around the globe, and Luigi must recover and return each artifact to its " +
        "rightful location by answering geography questions.\n\n" +
        "In the Archipelago randomizer, artifact returns and world progression checks " +
        "join the multiworld pool. Return all stolen artifacts and defeat Bowser to " +
        "complete your goal.\n\n" +
        "Source: github.com/ArchipelagoMW/Archipelago (AP-main).";

    public override string ThemeAccentColor => "#228B22";   // Luigi green

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "SNES";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "mario_is_missing";

    // No Lua module built or verified yet — stub only.
    public override bool ChecksImplemented => false;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();

    // Hides the base class property that returns EmulatorDirectory; emulated
    // ROM games have no separate install directory.
    public new string GameDirectory => string.Empty;
}
