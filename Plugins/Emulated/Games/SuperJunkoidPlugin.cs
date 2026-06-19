using System;
using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// SuperJunkoidPlugin — Archipelago integration for Super Junkoid
// (SNES ROM hack, 2022), on the BizHawk Lua-pipe bridge.
//
// WORLD SOURCE: ArchipelagoMW/Archipelago (AP-main).
//   game string: "Super Junkoid"
//   system:      SNES.
//   emulator:    BizHawk (SNESHawk / BSNES core).
//
// NOTE: Super Junkoid is a fan-made SNES ROM hack distributed with its own
// free base ROM — no original Nintendo cartridge is required. The apworld
// ships a pre-patched base ROM or uses a freely distributable base image.
//
// STATUS: STUB — ChecksImplemented = false. No Lua memory module has been
// built and verified for this game through the launcher bridge. The plugin
// registers the game in the catalog so it appears in the UI.
//
// GATE TO ENABLE: build a super_junkoid.lua module mapping SNES RAM addresses
// to the apworld's location ids, verify in-emulator, then set
// ChecksImplemented = true.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SuperJunkoidPlugin : EmulatorPlugin
{
    public SuperJunkoidPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "super_junkoid";
    public override string DisplayName => "Super Junkoid";
    public override string Subtitle    => "SNES · Emulated";
    public override string ApWorldName => "Super Junkoid";

    public override string Description =>
        "Super Junkoid is a 2022 fan-made SNES ROM hack built on a freely " +
        "distributable base, inspired by Super Metroid's engine and atmosphere. " +
        "You explore a decayed industrial world as a salvager, collecting upgrades " +
        "and powerups to unlock new areas and abilities. The game features a " +
        "distinct visual style, hand-crafted maps, and a moody electronic " +
        "soundtrack tailored for the randomizer scene.\n\n" +
        "In the Archipelago randomizer, upgrades, powerups, and exploration " +
        "milestones join the multiworld pool. Clear the final zone to complete " +
        "your goal. No original Nintendo cartridge is required.\n\n" +
        "Source: github.com/ArchipelagoMW/Archipelago (AP-main).";

    public override string ThemeAccentColor => "#4A7A6D";   // Super Junkoid industrial teal

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "SNES";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "super_junkoid";

    // No Lua module built or verified yet — stub only.
    public override bool ChecksImplemented => false;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();

    // Hides the base class property that returns EmulatorDirectory; emulated
    // ROM games have no separate install directory.
    public new string GameDirectory => string.Empty;
}
