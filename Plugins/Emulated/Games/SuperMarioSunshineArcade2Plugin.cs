using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// SuperMarioSunshineArcade2Plugin — Archipelago integration for
// Super Mario Sunshine Arcade 2 (GameCube fan-mod), via Dolphin emulator.
//
// WORLD SOURCE: Community apworld by Jorbori.
//   Repository: github.com/Jorbori/SMSA2AP
//   GH_OWNER:   "Jorbori"
//   GH_REPO:    "SMSA2AP"
//   GH_RELEASES: "https://api.github.com/repos/Jorbori/SMSA2AP/releases"
//   REPO_URL:   "https://github.com/Jorbori/SMSA2AP"
//   AP game string: "Super Mario Sunshine Arcade 2"
//   System: GameCube. Emulator: Dolphin (external, player-managed).
//
// WHAT THE APWORLD RANDOMIZES:
//   Shines, Blue Coins, unlockable stages, and progression items from the
//   fan-made SMSA2 mod join the multiworld location pool. Collect the required
//   Shines and complete the stages to reach your goal.
//
// STATUS: STUB — ChecksImplemented = false. Dolphin is not yet in
//   EmulatorBackends and no launcher bridge module exists for this game.
//   The plugin registers the game so it appears in the UI and the description
//   explains the current setup path.
//
// GATE TO ENABLE: add Dolphin to EmulatorBackends (id "dolphin", system "GCN"),
//   build a bridge module, verify in-emulator, set ChecksImplemented = true.
//
// SETUP STEPS (player-side):
//   1. Install Dolphin emulator (https://dolphin-emu.org/).
//   2. Download the SMSA2AP patch from github.com/Jorbori/SMSA2AP/releases.
//   3. Apply the patch to your Super Mario Sunshine GameCube ISO.
//   4. Load the patched ISO in Dolphin.
//   5. Follow the AP world readme for connection.
//
// ISO: Super Mario Sunshine (NTSC-U, game ID GMSE01) as the base. The SMSA2
//   patch transforms it into the fan-made Arcade 2 mod.
//   AcceptableBaseRoms is empty — no constraint — so any GCN ISO the player
//   provides is accepted.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SuperMarioSunshineArcade2Plugin : EmulatorPlugin
{
    public SuperMarioSunshineArcade2Plugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "super_mario_sunshine_arcade_2";
    public override string DisplayName => "Super Mario Sunshine Arcade 2";
    public override string Subtitle    => "GameCube · Dolphin required";
    public override string ApWorldName => "Super Mario Sunshine Arcade 2";

    public override string Description =>
        "Super Mario Sunshine Arcade 2 is a fan-made GameCube mod and hack of " +
        "Super Mario Sunshine that redesigns levels and gameplay around an arcade " +
        "challenge structure, offering a fresh take on Isle Delfino's platforming " +
        "with new layouts and objectives.\n\n" +
        "The Archipelago integration (by Jorbori) randomizes Shines, Blue Coins, " +
        "unlockable stages, and progression items from the mod into the multiworld " +
        "pool. Collect the required Shines and complete the stages to reach your " +
        "goal.\n\n" +
        "NOTE: This game requires the Dolphin emulator and your own legal Super " +
        "Mario Sunshine GameCube ISO as the base for patching. Download the SMSA2AP " +
        "patch from github.com/Jorbori/SMSA2AP, apply it to your SMS ISO, and load " +
        "the patched ISO in Dolphin. Follow the AP world readme for connection. " +
        "The launcher will add integrated Dolphin support in a future update.\n\n" +
        "Source: github.com/Jorbori/SMSA2AP.";

    public override string ThemeAccentColor => "#0078D4";   // F.L.U.D.D. nozzle blue

    // ── Badges ────────────────────────────────────────────────────────────────

    // Override the base class badge (which shows "ROM needed" when no ROM is
    // imported) to instead always show the platform/requirement badges, since
    // this game's patched ISO is always player-supplied and never tracked by
    // the launcher's ROM library.
    public override string[] GameBadges => new[] { "GameCube", "Dolphin Required", "ISO Required" };

    // ── Emulator specifics ────────────────────────────────────────────────────

    // SMSA2 is a GameCube title (SMS base, GMSE01); RomSystem = "GCN".
    protected override string RomSystem => "GCN";

    // Declare Dolphin so the intent is recorded; the base class resolves this
    // against EmulatorBackends.BackendsForSystem("GCN") which currently returns
    // nothing — a graceful no-op rather than a silent wrong default.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "dolphin" };

    // Dolphin-based GCN games use Dolphin's own AP integration, not the BizHawk
    // Lua-pipe bridge. These values are kept as documentation stubs; they are
    // not exercised while ChecksImplemented is false.
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "super_mario_sunshine_arcade_2";

    // Checks are NOT implemented through the launcher bridge. The SMSA2AP world
    // and Dolphin client handle AP connection when launched manually.
    public override bool ChecksImplemented => false;

    // Emulated ROM games have no separate install directory.
    public new string GameDirectory => string.Empty;

    // No ROM constraint — the player supplies their own patched GCN ISO.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        System.Array.Empty<RomIdentity>();
}
