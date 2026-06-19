using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// SoulBlazerPlugin — Archipelago integration for Soul Blazer
// (SNES, 1992), on the BizHawk Lua-pipe bridge.
//
// WORLD SOURCE: Tranquilite0/Archipelago-SoulBlazer (community apworld).
//   game string: "Soul Blazer" — verify against worlds/__init__.py.
//   system:      SNES.
//   emulator:    BizHawk (SNESHawk / BSNES core).
//
// WHAT THE APWORLD RANDOMIZES:
//   Soul Blazer tasks the player with freeing imprisoned souls across six
//   worlds to restore a fallen kingdom. Items, boss rewards, and soul-release
//   checks join the multiworld pool. Defeat Deathtoll to complete the goal.
//
// STATUS: STUB — ChecksImplemented = false. No Lua memory module has been
// built and verified for this game through the launcher bridge. The plugin
// registers the game in the catalog so it appears in the UI.
//
// GATE TO ENABLE: build a soul_blazer.lua module mapping SNES RAM addresses
// to the apworld's location ids, verify in-emulator, then set
// ChecksImplemented = true.
//
// ROM: Soul Blazer (SNES).
//   AcceptableBaseRoms is left with a display label only (no size or MD5
//   constraint) so any SNES ROM the player provides is accepted at import.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SoulBlazerPlugin : EmulatorPlugin
{
    public SoulBlazerPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "soul_blazer";
    public override string DisplayName => "Soul Blazer";
    public override string Subtitle    => "SNES · Emulated";
    public override string ApWorldName => "Soul Blazer";

    public override string Description =>
        "Soul Blazer is the 1992 SNES action RPG by Quintet (the studio behind " +
        "Actraiser and Illusion of Gaia). You play as a divine emissary sent to " +
        "free the souls of people, animals, and plants imprisoned by the evil " +
        "Deathtoll across six distinct worlds — a village, a mountain, an " +
        "underwater kingdom, a laboratory, a haunted mansion, and the sky garden. " +
        "Freeing souls rebuilds the world and unlocks new areas and items.\n\n" +
        "In the Archipelago randomizer (by Tranquilite0), item rewards and soul-" +
        "release checks join the multiworld pool. Free all imprisoned souls and " +
        "defeat Deathtoll to complete your goal.\n\n" +
        "Source: github.com/Tranquilite0/Archipelago-SoulBlazer.";

    public override string ThemeAccentColor => "#4B9CD3";   // Soul Blazer sky blue

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "SNES";

    // BizHawk (SNESHawk/BSNES core) is the documented emulator for SNES here.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "soul_blazer";

    // No Lua module built or verified yet — stub only.
    public override bool ChecksImplemented => false;

    // Hides the base class property that returns EmulatorDirectory; emulated
    // ROM games have no separate install directory.
    public new string GameDirectory => string.Empty;
}
