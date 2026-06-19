using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// MarioSuperSluggersPlugin — Archipelago integration for
// Mario Super Sluggers (Wii, 2008), via Dolphin emulator.
//
// WORLD SOURCE: Community apworld by MarioManTAW.
//   Repository: github.com/MarioManTAW/Archipelago (community fork)
//   GH_OWNER:   "MarioManTAW"
//   GH_REPO:    "Archipelago"
//   GH_RELEASES: "https://api.github.com/repos/MarioManTAW/Archipelago/releases"
//   REPO_URL:   "https://github.com/MarioManTAW/Archipelago"
//   AP game string: "Mario Super Sluggers"
//   System: Wii. Emulator: Dolphin (external, player-managed).
//
// WHAT THE APWORLD RANDOMIZES:
//   Characters, stadiums, and challenge-mode objectives join the multiworld
//   location pool. Unlock the required roster and complete the challenge
//   objectives to reach your goal.
//
// STATUS: STUB — ChecksImplemented = false. Dolphin is not yet in
//   EmulatorBackends and no launcher bridge module exists for this game.
//   The plugin registers the game so it appears in the UI and the description
//   explains the current setup path.
//
// GATE TO ENABLE: add Dolphin to EmulatorBackends (id "dolphin", system "Wii"),
//   build a bridge module, verify in-emulator, set ChecksImplemented = true.
//
// SETUP STEPS (player-side):
//   1. Install Dolphin emulator (https://dolphin-emu.org/).
//   2. Download the AP patch from github.com/MarioManTAW/Archipelago/releases.
//   3. Apply the patch to your Mario Super Sluggers Wii ISO.
//   4. Open Dolphin and load the patched ISO.
//   5. The AP world will handle connection — follow its readme.
//
// ISO: Mario Super Sluggers (NTSC-U, game ID RSVP01).
//   AcceptableBaseRoms is empty — no constraint — so any Wii disc image the
//   player provides is accepted.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MarioSuperSluggersPlugin : EmulatorPlugin
{
    public MarioSuperSluggersPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "mario_super_sluggers";
    public override string DisplayName => "Mario Super Sluggers";
    public override string Subtitle    => "Wii · Dolphin required";
    public override string ApWorldName => "Mario Super Sluggers";

    public override string Description =>
        "Mario Super Sluggers is Nintendo's 2008 Wii baseball game featuring an " +
        "all-star roster of Mushroom Kingdom characters across colorful themed " +
        "stadiums. The game's Challenge Mode tasks players with recruiting players, " +
        "unlocking stadiums, and defeating rivals to assemble the ultimate team.\n\n" +
        "The Archipelago community world (by MarioManTAW) randomizes characters, " +
        "stadiums, and challenge-mode objectives into the multiworld pool. Unlock " +
        "the required roster and complete the challenge objectives to reach your " +
        "goal.\n\n" +
        "NOTE: This game uses the Dolphin emulator and requires your own legal " +
        "Mario Super Sluggers Wii ISO. Download the AP patch from " +
        "github.com/MarioManTAW/Archipelago, apply it to your ISO, and load the " +
        "patched ISO in Dolphin. The launcher will add integrated Dolphin support " +
        "in a future update.\n\n" +
        "Source: github.com/MarioManTAW/Archipelago (community fork).";

    public override string ThemeAccentColor => "#CC2020";   // Mushroom Kingdom red

    // ── Badges ────────────────────────────────────────────────────────────────

    // Override the base class badge (which shows "ROM needed" when no ROM is
    // imported) to instead always show the platform/requirement badges, since
    // this game's ISO is always player-supplied and never tracked by the
    // launcher's ROM library.
    public override string[] GameBadges => new[] { "Wii", "Dolphin Required", "ISO Required" };

    // ── Emulator specifics ────────────────────────────────────────────────────

    // Mario Super Sluggers is a Wii title; RomSystem = "Wii".
    protected override string RomSystem => "Wii";

    // Declare Dolphin so the intent is recorded; the base class resolves this
    // against EmulatorBackends.BackendsForSystem("Wii") which currently returns
    // nothing — a graceful no-op rather than a silent wrong default.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "dolphin" };

    // Dolphin-based Wii games use Dolphin's own AP integration, not the BizHawk
    // Lua-pipe bridge. These values are kept as documentation stubs; they are
    // not exercised while ChecksImplemented is false.
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "mario_super_sluggers";

    // Checks are NOT implemented through the launcher bridge. The AP world and
    // Dolphin client handle AP connection when launched manually.
    public override bool ChecksImplemented => false;

    // Emulated ROM games have no separate install directory.
    public new string GameDirectory => string.Empty;

    // No ROM constraint — the player supplies their own legal Wii ISO.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        System.Array.Empty<RomIdentity>();
}
