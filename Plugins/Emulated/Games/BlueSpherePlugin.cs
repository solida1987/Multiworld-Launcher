using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// BlueSpherePlugin — Archipelago integration for Blue Sphere
// (Sega Genesis / Mega Drive), on the BizHawk Lua-pipe bridge.
//
// WORLD SOURCE: FlitPix/ap-bluesphere (community apworld).
//   game string: "Blue Sphere" — verify against worlds/__init__.py.
//   system:      GEN (Sega Genesis / Mega Drive).
//   emulator:    BizHawk (Genesis core).
//   releases:    https://api.github.com/repos/FlitPix/ap-bluesphere/releases
//   repo:        https://github.com/FlitPix/ap-bluesphere
//
// WHAT THE APWORLD RANDOMIZES:
//   Blue Sphere is the special-stage bonus game from Sonic & Knuckles /
//   Sonic 3 & Knuckles, adapted as a standalone Archipelago game. Players
//   navigate sphere-covered checkerboard arenas, turning blue spheres into
//   red ones and collecting rings. Stage completion and ring milestones
//   are shuffled into the multiworld pool.
//
// STATUS: STUB — ChecksImplemented = false. No Lua memory module has been
// built and verified for this game through the launcher bridge. The plugin
// registers the game in the catalog so it appears in the UI.
//
// GATE TO ENABLE: build a blue_sphere.lua module mapping Genesis RAM
// addresses to the apworld's location ids, verify in-emulator, then set
// ChecksImplemented = true.
//
// ROM: Blue Sphere (Sega Genesis / Mega Drive).
//   AcceptableBaseRoms is left with a display label only (no size or MD5
//   constraint) so any Genesis ROM the player provides is accepted at import.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class BlueSpherePlugin : EmulatorPlugin
{
    public BlueSpherePlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "blue_sphere";
    public override string DisplayName => "Blue Sphere";
    public override string Subtitle    => "Genesis · BizHawk required";
    public override string ApWorldName => "Blue Sphere";

    public override string Description =>
        "Blue Sphere is the special-stage bonus game from Sonic & Knuckles / " +
        "Sonic 3 & Knuckles, adapted as a standalone Archipelago game. Players " +
        "navigate colourful checkerboard arenas filled with spheres, turning " +
        "every blue sphere red and collecting rings to clear each stage. With " +
        "over four billion unique stage layouts generated from a simple code, " +
        "no two runs ever feel the same.\n\n" +
        "In the Archipelago randomizer (by FlitPix), stage completions and key " +
        "milestones join the multiworld pool. Clear your assigned stages and " +
        "meet your goal to complete the game.\n\n" +
        "Requires BizHawk emulator and the Blue Sphere ROM.\n\n" +
        "Source: github.com/FlitPix/ap-bluesphere.";

    public override string ThemeAccentColor => "#0040FF";   // Blue Sphere cobalt

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "GEN";

    // BizHawk (Genesis core) is the documented emulator for the GEN system.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "blue_sphere";

    // No Lua module built or verified yet — stub only.
    public override bool ChecksImplemented => false;

    // Hides the base class property that returns EmulatorDirectory; emulated
    // ROM games have no separate install directory.
    public new string GameDirectory => string.Empty;
}
