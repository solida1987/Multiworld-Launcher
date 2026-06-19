using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// ZeldaALinkBetweenWorldsPlugin — Archipelago integration for
// The Legend of Zelda: A Link Between Worlds (3DS), community apworld by
// randomsalience (randomsalience/albw-archipelago).
//
// ── HONEST REALITY CHECK (2026-06-16) ────────────────────────────────────────
//
//   * AP WORLD — community apworld from randomsalience/albw-archipelago (GitHub).
//     AP game string: "A Link Between Worlds" (inferred from project name; verify
//     against worlds/__init__.py / archipelago.json when integrating).
//     This is a COMMUNITY apworld — NOT in AP main. Install the .apworld into
//     your Archipelago worlds/ folder.
//
//   * PLATFORM — Nintendo 3DS (the original release). Emulation is via Citra
//     (now continued as Lime3DS). The launcher registers "citra" as the emulator
//     id; until Citra/Lime3DS is added to EmulatorBackends, AvailableBackends()
//     returns an empty list and the UI shows an honest "no emulator configured"
//     state (same graceful-degradation pattern as SuperMarioSunshinePlugin and
//     KirbyAirRidePlugin for Dolphin/GCN).
//
//   * CONNECTOR — No BizHawk Lua connector exists for 3DS (BizHawk does not host
//     a 3DS core). ChecksImplemented = false. A future Citra/Lime3DS memory-read
//     bridge would enable checks; this stub records the intent and allows the
//     game to appear in the catalog.
//
//   * ROM — The player provides their own legally-obtained 3DS ROM. AcceptableBaseRoms
//     is left empty (no ROM constraint imposed) so any file the player points at
//     is accepted — the apworld's own patcher/tool handles the patch step.
//
// ── GATE TO ENABLE ─────────────────────────────────────────────────────────────
//   1. Add Citra/Lime3DS to EmulatorBackends (id "citra", system "3DS").
//   2. Build a 3DS memory-read bridge and implement the native module.
//   3. Confirm the exact game string and ROM constraint from the apworld.
//   4. Set ChecksImplemented = true and fill AcceptableBaseRoms.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ZeldaALinkBetweenWorldsPlugin : EmulatorPlugin
{
    public ZeldaALinkBetweenWorldsPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "albw";
    public override string DisplayName => "Zelda: A Link Between Worlds";
    public override string Subtitle    => "3DS · Emulated (Citra/Lime3DS)";

    // Inferred AP game string from project name — verify against worlds/__init__.py.
    public override string ApWorldName => "A Link Between Worlds";

    public override string Description =>
        "The Legend of Zelda: A Link Between Worlds is Nintendo's 2013 Nintendo 3DS " +
        "sequel to A Link to the Past — Link can merge into walls as a 2D painting to " +
        "traverse Hyrule and the shadowed realm of Lorule. In the community Archipelago " +
        "integration (randomsalience/albw-archipelago), dungeon items, upgrades, and " +
        "collectibles across Hyrule and Lorule join the multiworld pool.\n\n" +
        "Play via Citra or Lime3DS (3DS emulators for PC). Integrated emulator support " +
        "is planned for a future update — for now, generate your seed, load your patched " +
        "ROM in Citra/Lime3DS, and connect via the standard Archipelago launcher.\n\n" +
        "Source: github.com/randomsalience/albw-archipelago.";

    public override string ThemeAccentColor => "#CC5500";   // A Link Between Worlds burnt orange

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "3DS";

    // Citra/Lime3DS — not yet in EmulatorBackends; AvailableBackends() returns
    // empty → honest "no emulator configured" state, same as KirbyAirRidePlugin.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "citra" };

    // No Lua connector for 3DS (BizHawk has no 3DS core; Citra has its own API).
    // Placeholder values so the EmulatorPlugin base compiles cleanly.
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "albw";

    // Checks not yet implemented through the launcher bridge.
    public override bool ChecksImplemented => false;

    // No ROM constraint — player provides a ROM per the apworld's own instructions.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        System.Array.Empty<RomIdentity>();
}
