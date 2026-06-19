using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// ZeldaSpiritTracksPlugin — Archipelago integration for The Legend of Zelda:
// Spirit Tracks (Nintendo DS, 2009).
//
// ── CONFIRMED AP WORLD ───────────────────────────────────────────────────────
//
// A community Archipelago world exists for Spirit Tracks. The apworld
// randomizes dungeon items, rail maps, force gems, and key items across
// the land of New Hyrule.
//
// AP game string: "Zelda: Spirit Tracks" (verify against world __init__.py)
//
// HOW THE INTEGRATION WORKS:
//   The game runs on BizHawk (melonDS core) or melonDS standalone.
//   The player supplies their own legal NDS cartridge dump.
//   ChecksImplemented is false until a BizHawk Lua bridge is built and
//   verified in-emulator.
//
// GATE TO ENABLE FULL INTEGRATION: implement a BizHawk Lua module that reads
// the check flags from NDS Main RAM, wire up item delivery, and flip
// ChecksImplemented to true.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ZeldaSpiritTracksPlugin : EmulatorPlugin
{
    public ZeldaSpiritTracksPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "zelda_spirit_tracks";
    public override string DisplayName => "Zelda: Spirit Tracks";
    public override string Subtitle    => "DS · Emulated";
    public override string ApWorldName => "Zelda: Spirit Tracks";

    public override string Description =>
        "The Legend of Zelda: Spirit Tracks is the Nintendo DS successor to " +
        "Phantom Hourglass. A century after the founding of New Hyrule, Link " +
        "travels by Spirit Train with Princess Zelda's ghost to restore the " +
        "Spirit Tracks and stop Chancellor Cole from reviving the Demon King " +
        "Malladus.\n\n" +
        "In the Archipelago randomizer, dungeon items, rail maps, force gems, " +
        "and key items across New Hyrule join the multiworld pool. Defeat " +
        "Malladus to complete your goal.\n\n" +
        "Requires: your own legal Spirit Tracks NDS cartridge dump and BizHawk. " +
        "Full check integration will be enabled in a future update.";

    public override string ThemeAccentColor => "#4A7A2A";   // Hyrule rail green

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "NDS";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "zelda_spirit_tracks";

    public override bool ChecksImplemented => false;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        System.Array.Empty<RomIdentity>();

    // Hides the base class property that returns EmulatorDirectory.
    public new string GameDirectory => string.Empty;
}
