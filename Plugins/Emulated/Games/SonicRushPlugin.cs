using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// SonicRushPlugin — Archipelago integration for
// Sonic Rush (Nintendo DS, 2005).
//
// CONFIRMED AP WORLD:
//   Community apworld for Sonic Rush.
//   AP game string: "Sonic Rush"
//   System: DS. Emulator: BizHawk (melonDS core).
//
// WHAT THE APWORLD RANDOMIZES:
//   Acts, boss access, Sol Emeralds, Chaos Emeralds, and special stage
//   unlocks across both Sonic and Blaze's storylines join the multiworld
//   pool. The player must complete the required acts and collect the
//   Emeralds to reach the final goal.
//
// STATUS: STUB — ChecksImplemented = false. No BizHawk DS bridge module
//   exists for this game yet. The plugin registers the game so it appears
//   in the UI catalog.
//
// GATE TO ENABLE: build a BizHawk (melonDS core) Lua bridge module for this
//   game, verify in-emulator, and set ChecksImplemented = true.
//
// ROM: Sonic Rush NDS cartridge dump (NTSC-U or PAL).
//   AcceptableBaseRoms is empty — no constraint applied at this stub stage.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SonicRushPlugin : EmulatorPlugin
{
    public SonicRushPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "sonic_rush";
    public override string DisplayName => "Sonic Rush";
    public override string Subtitle    => "DS · Emulated (BizHawk)";
    public override string ApWorldName => "Sonic Rush";

    public override string Description =>
        "Sonic Rush (2005, Nintendo DS) is Dimps and Sega's high-speed dual-screen " +
        "platformer featuring two playable characters: Sonic the Hedgehog and Blaze " +
        "the Cat, a princess from another dimension. The two must cooperate to " +
        "recover both the Chaos Emeralds and Blaze's Sol Emeralds from the villainous " +
        "Dr. Eggman and his inter-dimensional counterpart Eggman Nega.\n\n" +
        "In the Archipelago randomizer, acts, boss access, Sol and Chaos Emeralds, " +
        "and special stage unlocks across both storylines join the multiworld pool. " +
        "Complete the required acts and collect the Emeralds to reach your goal.\n\n" +
        "Requires: your own legal Sonic Rush NDS cartridge dump and BizHawk " +
        "(melonDS core). The launcher will add integrated BizHawk DS support in a " +
        "future update.";

    public override string ThemeAccentColor => "#0070C0";   // Sonic blue

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "DS";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "sonic_rush";

    public override bool ChecksImplemented => false;

    public new string GameDirectory => string.Empty;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();
}
