using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// Spyro2RiptosRagePlugin — Archipelago integration for
// Spyro 2: Ripto's Rage! (PS1/PSX, 1999, SCUS-94425 NTSC-U).
//
// ── CONFIRMED AP WORLD (github.com/Uroogla/S2AP) ─────────────────────────────
//
// A LIVE community Archipelago world exists for Spyro 2: Ripto's Rage (PS1).
// Repository: github.com/Uroogla/S2AP  (maintained by Uroogla, same author as
// github.com/Uroogla/S3AP which already ships in this launcher).
//
//   • Ships an S2AP zip containing a custom DuckStation client (S2AP.exe) that
//     connects to the AP server directly via embedded scripting.
//   • Ships spyro2.apworld — the world file installed into the AP server.
//   • Only supports NTSC-U (SCUS-94425). Interpreter mode required in DuckStation.
//   • AP game string inferred from repo naming convention (same as S3AP "Spyro 3"):
//     "Spyro 2" — verify against __init__.py when upstream confirms.
//
// HOW THE INTEGRATION WORKS:
//   S2AP uses a custom DuckStation fork with the AP client scripted in. This is
//   the ConnectsItself=false + PSX/PCSX2 stub pattern — the launcher cannot bridge
//   the S2AP DuckStation client through the standard BizHawk Lua pipe. The plugin
//   registers the game in the catalog, links to the S2AP releases page, and shows
//   the correct setup instructions in the Settings panel.
//
//   PCSX2 is declared as the emulator ID (the catalog's PSX emulator slot) so the
//   game appears correctly grouped. In practice the player runs S2AP.exe (the
//   custom DuckStation fork), not PCSX2 itself — see setup instructions below.
//
// ISO: Spyro 2: Ripto's Rage! NTSC-U (SCUS-94425).
//   The player supplies their own legal PS1 disc image.
//
// GATE TO ENABLE FULL INTEGRATION: implement an S2AP bridge or wait for upstream
//   to publish a BizHawk/PCSX2 connector. For now ChecksImplemented = false.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Spyro2RiptosRagePlugin : EmulatorPlugin
{
    public Spyro2RiptosRagePlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "spyro2_psx";
    public override string DisplayName => "Spyro 2: Ripto's Rage!";
    public override string Subtitle    => "PSX · Emulated (S2AP / DuckStation)";

    // AP world game string — inferred from Uroogla/S2AP naming convention.
    // Verify against worlds/__init__.py if upstream changes the string.
    public override string ApWorldName => "Spyro 2";

    public override string Description =>
        "Spyro 2: Ripto's Rage! (1999, PS1) is the second Insomniac-developed Spyro " +
        "platformer. Spyro must collect Talismans and Orbs across 16 worlds and 3 " +
        "homeworlds, defeat Ripto, and free the inhabitants of Avalar.\n\n" +
        "The S2AP Archipelago integration (by Uroogla) randomizes Orbs, Gems, and " +
        "key items across the multiworld. It uses a custom DuckStation client " +
        "(S2AP.exe) that connects to the AP server — download S2AP from " +
        "github.com/Uroogla/S2AP/releases, extract it, and run S2AP.exe with your " +
        "NTSC-U disc image (SCUS-94425). Enable Interpreter mode in DuckStation and " +
        "install spyro2.apworld into your Archipelago server per the setup guide.\n\n" +
        "Requires: your own legal Spyro 2 NTSC-U PS1 disc image and the S2AP client " +
        "from github.com/Uroogla/S2AP. The launcher will add full integrated S2AP " +
        "support in a future update.";

    public override string ThemeAccentColor => "#2255BB";   // Ripto's electric blue

    // ── Emulator specifics ────────────────────────────────────────────────────

    // PSX system. S2AP uses a custom DuckStation client (not PCSX2 or BizHawk).
    // "pcsx2" is the catalog id for PS1/PS2 emulation in this launcher; the
    // EmulatorBackends resolver returns an empty list for PSX/pcsx2 until a backend
    // is registered, which gives an honest "not yet configured" UI state.
    protected override string RomSystem => "PSX";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "pcsx2" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "spyro2_psx";

    // S2AP connects via its embedded DuckStation client — not through the BizHawk
    // Lua bridge. ChecksImplemented stays false until a standard bridge exists.
    public override bool ChecksImplemented => false;
}
