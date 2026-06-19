using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// YuGiOhDungeonDiceMonstersPlugin — Archipelago integration for
// Yu-Gi-Oh! Dungeon Dice Monsters (Game Boy Advance, 2002).
//
// CONFIRMED AP WORLD:
//   Community apworld by JustinMarshall98:
//   https://github.com/JustinMarshall98/Archipelago---YuGiOh-Dungeon-Dice-Monsters-GBA-
//   (world folder: worlds/yugiohddm)
//   AP game string: "Yu-Gi-Oh Dungeon Dice Monsters"
//   System: GBA. Emulator: BizHawk.
//
// WHAT THE APWORLD RANDOMIZES:
//   Monster dice acquired across the game's Dungeon Dice Monsters campaign
//   form the multiworld location pool. The player must collect the required
//   dice and defeat the final opponent to complete the goal.
//
// STATUS: STUB — ChecksImplemented = false. No BizHawk Lua module
//   exists for this game yet. The plugin registers the game so it appears
//   in the UI catalog.
//
// GATE TO ENABLE: build the BizHawk Lua module ("yugioh_ddm.lua"), verify
//   in-emulator, and set ChecksImplemented = true.
//
// ROM: Yu-Gi-Oh! Dungeon Dice Monsters (USA) GBA cartridge dump.
//   AcceptableBaseRoms is empty — no constraint applied at this stub stage.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class YuGiOhDungeonDiceMonstersPlugin : EmulatorPlugin
{
    public YuGiOhDungeonDiceMonstersPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "yugioh_ddm";
    public override string DisplayName => "Yu-Gi-Oh! Dungeon Dice Monsters";
    public override string Subtitle    => "GBA · Emulated";
    public override string ApWorldName => "Yu-Gi-Oh Dungeon Dice Monsters";

    public override string Description =>
        "Yu-Gi-Oh! Dungeon Dice Monsters is the 2002 Game Boy Advance adaptation of " +
        "the board game-within-a-show from the Battle City arc of the Yu-Gi-Oh! " +
        "anime. Players roll custom dice to summon monsters, carve paths across a " +
        "grid dungeon, and destroy the opponent's Heart Points in a fast-paced " +
        "tactical duel. The campaign mode pits you against a roster of opponents " +
        "from the series as you build your dice collection.\n\n" +
        "In the Archipelago randomizer, monster dice earned across the campaign's " +
        "battles join the multiworld pool. Collect the required dice and defeat the " +
        "final opponent to complete your goal.\n\n" +
        "Requires: your own legal Yu-Gi-Oh! Dungeon Dice Monsters (USA) GBA " +
        "cartridge dump and BizHawk (or an alternative GBA emulator). " +
        "The launcher will add an integrated bridge module in a future update.";

    public override string ThemeAccentColor => "#8040C0";   // DDM purple / Dungeon dice

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "GBA";

    // BizHawk is the primary verified target; mGBA and Mesen work as alternatives.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "mgba", "mesen" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "yugioh_ddm";

    public override bool ChecksImplemented => false;

    public new string GameDirectory => string.Empty;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        System.Array.Empty<RomIdentity>();
}
