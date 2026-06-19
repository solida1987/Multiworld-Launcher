using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// RatchetAndClankPlugin — Archipelago integration for
// Ratchet & Clank (PlayStation 2, 2002).
//
// CONFIRMED AP WORLD:
//   Community apworld for Ratchet & Clank (PS2 original, 2002).
//   AP game string: "Ratchet & Clank"
//   System: PS2. Emulator: PCSX2.
//
// WHAT THE APWORLD RANDOMIZES:
//   Gadget and weapon unlocks, bolt progression, planet access, and gold bolt
//   collectibles across every planet join the multiworld pool. The player
//   must gather the required items and defeat Chairman Drek to reach the goal.
//
// STATUS: STUB — ChecksImplemented = false. No PCSX2 bridge module
//   exists for this game yet. The plugin registers the game so it appears
//   in the UI catalog.
//
// GATE TO ENABLE: build a PCSX2 bridge module, verify in-emulator,
//   and set ChecksImplemented = true.
//
// DISC: Ratchet & Clank PS2 disc image (NTSC-U or PAL).
//   AcceptableBaseRoms is empty — no constraint applied at this stub stage.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class RatchetAndClankPlugin : EmulatorPlugin
{
    public RatchetAndClankPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "ratchet_and_clank";
    public override string DisplayName => "Ratchet & Clank";
    public override string Subtitle    => "PS2 · Emulated (PCSX2)";
    public override string ApWorldName => "Ratchet & Clank";

    public override string Description =>
        "Ratchet & Clank (2002, PlayStation 2) introduces the lovable Lombax mechanic " +
        "Ratchet and his robot companion Clank as they traverse the galaxy to stop " +
        "the villainous Chairman Drek from building a new homeworld by tearing apart " +
        "other planets. Armed with an ever-expanding arsenal of inventive weapons and " +
        "gadgets, the duo visits a dozen planets packed with platforming challenges, " +
        "aerial dogfights, and secret collectibles.\n\n" +
        "In the Archipelago randomizer, weapons, gadgets, planet access, bolt upgrades, " +
        "and gold bolts join the multiworld pool. Gather the required items and defeat " +
        "Chairman Drek to reach your goal.\n\n" +
        "Requires: your own legal Ratchet & Clank PS2 disc image and PCSX2. " +
        "The launcher will add integrated PCSX2 support in a future update.";

    public override string ThemeAccentColor => "#F0A000";   // Ratchet golden wrench

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "PS2";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "pcsx2" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "ratchet_and_clank";

    public override bool ChecksImplemented => false;

    public new string GameDirectory => string.Empty;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();
}
