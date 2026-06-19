using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// ApeEscape3Plugin — Archipelago integration for
// Ape Escape 3 (PlayStation 2, 2005).
//
// CONFIRMED AP WORLD:
//   Community apworld for Ape Escape 3.
//   AP game string: "Ape Escape 3"
//   System: PS2. Emulator: PCSX2.
//
// WHAT THE APWORLD RANDOMIZES:
//   Monkey captures, Morphing Costume unlocks, time station access, and
//   gadget progression across every stage join the multiworld pool.
//   The player must capture the required monkeys and defeat Specter to
//   reach the final goal.
//
// STATUS: STUB — ChecksImplemented = false. No PCSX2 bridge module
//   exists for this game yet. The plugin registers the game so it appears
//   in the UI catalog.
//
// GATE TO ENABLE: build a PCSX2 bridge module, verify in-emulator,
//   and set ChecksImplemented = true.
//
// DISC: Ape Escape 3 PS2 disc image (NTSC-U or PAL).
//   AcceptableBaseRoms is empty — no constraint applied at this stub stage.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ApeEscape3Plugin : EmulatorPlugin
{
    public ApeEscape3Plugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "ape_escape_3";
    public override string DisplayName => "Ape Escape 3";
    public override string Subtitle    => "PS2 · Emulated (PCSX2)";
    public override string ApWorldName => "Ape Escape 3";

    public override string Description =>
        "Ape Escape 3 (2005, PlayStation 2) is the third mainline entry in Sony's " +
        "beloved monkey-catching franchise. This time the mischievous Specter has " +
        "taken over television broadcasts, and twins Satoru and Sayaka must travel " +
        "through a variety of TV-themed worlds — westerns, ninja epics, kung-fu " +
        "flicks, and more — using the Morpher to transform into powerful costumes " +
        "and recapture the broadcast-watching monkeys.\n\n" +
        "In the Archipelago randomizer, monkey captures, Morphing Costume unlocks, " +
        "gadgets, and stage access join the multiworld pool. Capture the required " +
        "monkeys and defeat Specter to reach your goal.\n\n" +
        "Requires: your own legal Ape Escape 3 PS2 disc image and PCSX2. " +
        "The launcher will add integrated PCSX2 support in a future update.";

    public override string ThemeAccentColor => "#E8A020";   // Ape Escape banana yellow

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "PS2";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "pcsx2" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "ape_escape_3";

    public override bool ChecksImplemented => false;

    public new string GameDirectory => string.Empty;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();
}
