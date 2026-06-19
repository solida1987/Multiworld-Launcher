using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// SuperSmashBrosMeleePlugin — Archipelago integration for
// Super Smash Bros. Melee (GameCube, 2001), via Dolphin emulator.
//
// WORLD SOURCE: Community apworld by PinkSwitch.
//   Repository: github.com/PinkSwitch/Archipelago (community fork)
//   AP game string: "Super Smash Bros. Melee"
//   System: GCN (GameCube). Emulator: Dolphin (external AP client plugin).
//
// WHAT THE APWORLD RANDOMIZES:
//   Trophies, event matches, and character unlocks join the multiworld location
//   pool. Complete the required events and collect the necessary trophies to
//   reach your goal.
//
// STATUS: STUB — ChecksImplemented = false. Melee uses Dolphin's own AP
//   client plugin, NOT the BizHawk Lua-pipe bridge. LuaScriptName and
//   LuaModuleName are kept as documentation stubs only; they are not exercised
//   while ChecksImplemented is false. Dolphin is not yet in EmulatorBackends,
//   so SupportedEmulatorIds resolves to nothing — the UI shows "coming soon."
//
// GATE TO ENABLE: add Dolphin to EmulatorBackends (id "dolphin", system "GCN"),
//   integrate the Dolphin AP client plugin bridge, verify in-emulator, then
//   flip ChecksImplemented to true.
//
// ISO: Super Smash Bros. Melee (NTSC-U, game ID GALE01).
//   AcceptableBaseRoms is empty — any Melee GCN ISO the player provides is
//   accepted at import (no size or MD5 constraint enforced by the launcher).
//   The player must supply their own legal SSBM GameCube ISO.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SuperSmashBrosMeleePlugin : EmulatorPlugin
{
    public SuperSmashBrosMeleePlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "super_smash_bros_melee";
    public override string DisplayName => "Super Smash Bros. Melee";
    public override string Subtitle    => "GCN · Emulated";
    public override string ApWorldName => "Super Smash Bros. Melee";

    public override string Description =>
        "Super Smash Bros. Melee is Nintendo's beloved 2001 GameCube fighting " +
        "game, pitting iconic Nintendo characters against one another across " +
        "diverse stages with its deep, fast-paced combat engine. The game's " +
        "extensive trophy collection, event matches, and unlockable roster " +
        "made it one of the best-selling GameCube titles of all time.\n\n" +
        "The Archipelago community world (by PinkSwitch) randomizes trophies, " +
        "event matches, and character unlocks into the multiworld pool. Complete " +
        "the required events and collect the necessary trophies to reach your " +
        "goal.\n\n" +
        "NOTE: This game uses the Dolphin emulator with the AP integration " +
        "plugin. NTSC-U GameCube ISO only (game ID GALE01). You supply your " +
        "own legal SSBM GameCube ISO. The launcher will add integrated Dolphin " +
        "support in a future update.\n\n" +
        "Source: github.com/PinkSwitch/Archipelago (community fork).";

    public override string ThemeAccentColor => "#C0C000";   // Melee gold/trophy theme

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

    // Melee uses Dolphin's own AP client plugin, not the BizHawk Lua bridge.
    // These values are documentation stubs; LuaModuleName is not exercised
    // while ChecksImplemented is false.
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "super_smash_bros_melee";

    /// STUB: check detection not implemented. Dolphin AP plugin bridge required.
    public override bool ChecksImplemented => false;

    /// No fixed MD5 constraint — the player supplies their own legal ISO.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();

    // Hides the base class property that returns EmulatorDirectory; emulated
    // ROM games have no separate install directory.
    public new string GameDirectory => string.Empty;

    public override RomRequirement? GetUnmetRomRequirement()
    {
        if (RomPath != null && File.Exists(RomPath)) return null;
        return new RomRequirement(
            DisplayName, "GCN",
            "your own legal Super Smash Bros. Melee GameCube ISO (NTSC-U, game ID GALE01)",
            RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());
    }

    // ── Session ROM ───────────────────────────────────────────────────────────

    protected override Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        SessionRomNote =
            "[Super Smash Bros. Melee] Melee is not patched by this AP world — " +
            "launching your ISO directly via Dolphin. Make sure the Dolphin AP " +
            "integration plugin is installed and configured before starting.";
        return Task.FromResult<string?>(null);
    }
}
