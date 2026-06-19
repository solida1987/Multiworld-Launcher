using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// XenobladeChroniclesXPlugin — Archipelago integration for
// Xenoblade Chronicles X (Wii U, 2015).
//
// CONFIRMED AP WORLD:
//   Community apworld by MaragonMH:
//   https://github.com/MaragonMH/Archipelago  (worlds/xcx)
//   AP game string: "Xenoblade Chronicles X"
//   System: Wii U. Emulator: Cemu.
//
// WHAT THE APWORLD RANDOMIZES:
//   Field Skills, Arts, Classes, Tyrants, Segments, and key story progression
//   items form the multiworld location pool. The player must gather the required
//   items to unlock the final story segment and complete the goal.
//
// STATUS: STUB — ChecksImplemented = false. Cemu is not yet in
//   EmulatorBackends and no launcher bridge module exists for this game.
//   The plugin registers the game so it appears in the UI catalog.
//
// GATE TO ENABLE: add Cemu to EmulatorBackends (id "cemu", system "WiiU"),
//   build a bridge module, verify in-emulator, set ChecksImplemented = true.
//
// DISC: Xenoblade Chronicles X Wii U dump (WUD/WUX, game ID AAHE01 NTSC-U
//   or AAHP01 PAL).
//   AcceptableBaseRoms is empty — no constraint applied at this stub stage.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class XenobladeChroniclesXPlugin : EmulatorPlugin
{
    public XenobladeChroniclesXPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "xenoblade_chronicles_x";
    public override string DisplayName => "Xenoblade Chronicles X";
    public override string Subtitle    => "Wii U · Emulated (Cemu)";
    public override string ApWorldName => "Xenoblade Chronicles X";

    public override string Description =>
        "Xenoblade Chronicles X is Monolith Soft's 2015 Wii U open-world JRPG in " +
        "which the remnants of humanity crash-land on the alien planet Mira and must " +
        "survive among colossal creatures while uncovering the mysteries of their " +
        "new home. As a BLADE operative, the player explores five vast continents, " +
        "masters a deep combat system of Arts and Classes, pilots giant Skell mechs, " +
        "and hunts legendary Tyrant monsters across an enormous open world.\n\n" +
        "In the Archipelago randomizer, Field Skills, Arts, Classes, Tyrants, Segments, " +
        "and key story items join the multiworld pool. Unlock the required items and " +
        "reach the final story segment to complete your goal.\n\n" +
        "Requires: your own legal Xenoblade Chronicles X Wii U disc dump (WUD/WUX) " +
        "and Cemu Emulator. The launcher will add integrated Cemu support in a future " +
        "update.";

    public override string ThemeAccentColor => "#1A7AC8";   // XCX blue / Mira sky

    // ── Emulator specifics ────────────────────────────────────────────────────

    // Xenoblade Chronicles X is a Wii U exclusive; RomSystem = "WiiU".
    protected override string RomSystem => "WiiU";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "cemu" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "xenoblade_chronicles_x";

    public override bool ChecksImplemented => false;

    public new string GameDirectory => string.Empty;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        System.Array.Empty<RomIdentity>();
}
