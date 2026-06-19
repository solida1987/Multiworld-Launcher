using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// RatchetAndClankUpYourArsenalPlugin — Archipelago integration for
// Ratchet & Clank: Up Your Arsenal (PlayStation 2, 2004).
//
// CONFIRMED AP WORLD:
//   Community apworld by Taoshix:
//   https://github.com/Taoshix/Archipelago-RaC3
//   AP game string: "Ratchet and Clank 3"
//   System: PS2. Emulator: PCSX2 v1.7+ (PINE protocol).
//   Supported discs: US and PAL versions.
//
// WHAT THE APWORLD RANDOMIZES:
//   Weapons, gadgets, armor, Titanium Bolts, skill points, and planet access
//   form the multiworld location pool. The player must gear up and defeat
//   Dr. Nefarious to complete the goal.
//
// STATUS: STUB — ChecksImplemented = false. No PCSX2 bridge module
//   exists for this game yet. The plugin registers the game so it appears
//   in the UI catalog.
//
// GATE TO ENABLE: build a PCSX2 PINE bridge module, verify in-emulator,
//   and set ChecksImplemented = true.
//
// DISC: Ratchet & Clank: Up Your Arsenal PS2 disc image (US or PAL).
//   AcceptableBaseRoms is empty — no constraint applied at this stub stage.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class RatchetAndClankUpYourArsenalPlugin : EmulatorPlugin
{
    public RatchetAndClankUpYourArsenalPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "ratchet_and_clank_3";
    public override string DisplayName => "Ratchet & Clank: Up Your Arsenal";
    public override string Subtitle    => "PS2 · Emulated (PCSX2)";
    public override string ApWorldName => "Ratchet and Clank 3";

    public override string Description =>
        "Ratchet & Clank: Up Your Arsenal (2004, PlayStation 2) brings the galactic " +
        "duo back together to stop the megalomaniacal Dr. Nefarious from eradicating " +
        "all organic life in the Solana Galaxy. The third mainline entry delivered " +
        "the series' most ambitious multiplayer, a fully online arena mode, and a " +
        "massive single-player campaign packed with over forty weapons, a six-player " +
        "Courtney Gears, and the iconic \"Annihilator\" missile launcher.\n\n" +
        "In the Archipelago randomizer, weapons, gadgets, armor, Titanium Bolts, skill " +
        "points, and planet access join the multiworld pool. Arm yourself and defeat " +
        "Dr. Nefarious to complete your goal.\n\n" +
        "Requires: your own legal Ratchet & Clank: Up Your Arsenal PS2 disc image " +
        "(US or PAL) and PCSX2 v1.7 or later. " +
        "The launcher will add integrated PCSX2 support in a future update.";

    public override string ThemeAccentColor => "#30A040";   // UYA green / Ranger armor

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "PS2";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "pcsx2" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "ratchet_and_clank_3";

    public override bool ChecksImplemented => false;

    public new string GameDirectory => string.Empty;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        System.Array.Empty<RomIdentity>();
}
