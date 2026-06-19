using System;
using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// DragonWarriorPlugin — Archipelago integration for Dragon Warrior
// (NES, 1986/1989), on the BizHawk Lua-pipe bridge.
//
// WORLD SOURCE: ArchipelagoMW/Archipelago (AP-main).
//   game string: "Dragon Warrior"
//   system:      NES.
//   emulator:    BizHawk (NesHawk / QuickNes core).
//
// STATUS: STUB — ChecksImplemented = false. No Lua memory module has been
// built and verified for this game through the launcher bridge. The plugin
// registers the game in the catalog so it appears in the UI.
//
// GATE TO ENABLE: build a dragon_warrior.lua module mapping NES RAM addresses
// to the apworld's location ids, verify in-emulator, then set
// ChecksImplemented = true.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DragonWarriorPlugin : EmulatorPlugin
{
    public DragonWarriorPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "dragon_warrior";
    public override string DisplayName => "Dragon Warrior";
    public override string Subtitle    => "NES · Emulated";
    public override string ApWorldName => "Dragon Warrior";

    public override string Description =>
        "Dragon Warrior (1986, Enix) is the NES role-playing game that established " +
        "the template for the genre in the West. You are a descendant of the hero " +
        "Erdrick, tasked by the King of Tantegel to recover the stolen Ball of Light " +
        "from the Dragonlord's castle at Charlock. Progress through towns, dungeons, " +
        "and the overworld, gathering spells, armor, and legendary weapons along the way.\n\n" +
        "In the Archipelago randomizer, town rewards, chest items, and quest " +
        "progression items join the multiworld pool. Defeat the Dragonlord to " +
        "complete your goal.\n\n" +
        "Source: github.com/ArchipelagoMW/Archipelago (AP-main).";

    public override string ThemeAccentColor => "#8B2500";   // Dragon Warrior red shield

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "NES";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "dragon_warrior";

    // No Lua module built or verified yet — stub only.
    public override bool ChecksImplemented => false;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();

    // Hides the base class property that returns EmulatorDirectory; emulated
    // ROM games have no separate install directory.
    public new string GameDirectory => string.Empty;
}
