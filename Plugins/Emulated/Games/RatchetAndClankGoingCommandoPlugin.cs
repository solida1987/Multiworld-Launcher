using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// RatchetAndClankGoingCommandoPlugin — Archipelago integration for
// Ratchet & Clank: Going Commando (PlayStation 2, 2003).
//
// CONFIRMED AP WORLD:
//   Community apworld by evilwb:
//   https://github.com/evilwb/APRac2
//   AP game string: "Ratchet and Clank 2"
//   System: PS2. Emulator: PCSX2 (PINE protocol).
//   Supported disc: NTSC-U SCUS-97268.
//
// WHAT THE APWORLD RANDOMIZES:
//   Weapons, gadgets, armor pieces, Platinum Bolts, ship parts, and planet
//   access form the multiworld location pool. The player must gather the
//   required items and defeat the Thugs-4-Less leader to complete the goal.
//
// STATUS: STUB — ChecksImplemented = false. No PCSX2 bridge module
//   exists for this game yet. The plugin registers the game so it appears
//   in the UI catalog.
//
// GATE TO ENABLE: build a PCSX2 PINE bridge module, verify in-emulator,
//   and set ChecksImplemented = true.
//
// DISC: Ratchet & Clank: Going Commando PS2 disc image (NTSC-U SCUS-97268).
//   AcceptableBaseRoms is empty — no constraint applied at this stub stage.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class RatchetAndClankGoingCommandoPlugin : EmulatorPlugin
{
    public RatchetAndClankGoingCommandoPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "ratchet_and_clank_2";
    public override string DisplayName => "Ratchet & Clank: Going Commando";
    public override string Subtitle    => "PS2 · Emulated (PCSX2)";
    public override string ApWorldName => "Ratchet and Clank 2";

    public override string Description =>
        "Ratchet & Clank: Going Commando (2003, PlayStation 2) sends the intrepid " +
        "Lombax mechanic and his robot companion on an all-new adventure across the " +
        "Bogon Galaxy at the request of mega-corporation MegaCorp. With an expanded " +
        "arsenal of wildly inventive weapons and gadgets, upgraded armor, and a " +
        "thruster-pack that lets Ratchet strafe in combat, Going Commando deepened " +
        "every system from the original.\n\n" +
        "In the Archipelago randomizer, weapons, gadgets, armor pieces, Platinum " +
        "Bolts, ship parts, and planet access join the multiworld pool. Gear up and " +
        "defeat the Thugs-4-Less leader to complete your goal.\n\n" +
        "Requires: your own legal Ratchet & Clank: Going Commando PS2 disc image " +
        "(NTSC-U SCUS-97268) and PCSX2. " +
        "The launcher will add integrated PCSX2 support in a future update.";

    public override string ThemeAccentColor => "#E87020";   // Ratchet orange / Megacorp

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "PS2";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "pcsx2" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "ratchet_and_clank_2";

    public override bool ChecksImplemented => false;

    public new string GameDirectory => string.Empty;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        System.Array.Empty<RomIdentity>();
}
