using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// VoltorbFlipPlugin — Archipelago integration for Voltorb Flip (the Game
// Corner minigame from Pokemon HeartGold/SoulSilver, NDS, 2009/2010).
//
// ── CONFIRMED AP WORLD ───────────────────────────────────────────────────────
//
// A community Archipelago world exists for Voltorb Flip as a standalone
// randomizer goal built around the HG/SS Game Corner minigame. The apworld
// randomizes coin prizes and level progression across the flip boards.
//
// AP game string: "Voltorb Flip" (verify against world __init__.py)
//
// HOW THE INTEGRATION WORKS:
//   The game runs on BizHawk (melonDS core) using the HG/SS NDS ROM.
//   The player supplies their own legal Pokemon HG or SS cartridge dump.
//   ChecksImplemented is false until a BizHawk Lua bridge is built and
//   verified in-emulator against the HG/SS minigame memory layout.
//
// GATE TO ENABLE FULL INTEGRATION: implement a BizHawk Lua module that reads
// the Voltorb Flip board state and coin/level progress from NDS Main RAM,
// wire up item delivery, and flip ChecksImplemented to true.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class VoltorbFlipPlugin : EmulatorPlugin
{
    public VoltorbFlipPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "voltorb_flip";
    public override string DisplayName => "Voltorb Flip";
    public override string Subtitle    => "DS · Emulated (HG/SS)";
    public override string ApWorldName => "Voltorb Flip";

    public override string Description =>
        "Voltorb Flip is the Game Corner minigame from Pokemon HeartGold and " +
        "SoulSilver on the Nintendo DS. Players flip tiles on a numbered grid " +
        "to collect coins while avoiding hidden Voltorb — clear boards to " +
        "advance through ten levels and accumulate enough coins for rare prizes.\n\n" +
        "In the Archipelago randomizer, coin prizes and board-level milestones " +
        "join the multiworld pool. Reach the highest level and claim your coins " +
        "to complete your goal.\n\n" +
        "Requires: your own legal Pokemon HeartGold or SoulSilver NDS cartridge " +
        "dump and BizHawk. Full check integration will be enabled in a future update.";

    public override string ThemeAccentColor => "#D44A1A";   // Voltorb red

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "NDS";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "voltorb_flip";

    public override bool ChecksImplemented => false;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        System.Array.Empty<RomIdentity>();

    // Hides the base class property that returns EmulatorDirectory.
    public new string GameDirectory => string.Empty;
}
