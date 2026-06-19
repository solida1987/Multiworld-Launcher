using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// PokeParkWiiPlugin — Archipelago integration for
// PokéPark Wii: Pikachu's Adventure (Wii, 2009).
//
// CONFIRMED AP WORLD:
//   Community apworld for PokéPark Wii: Pikachu's Adventure.
//   AP game string: "PokePark Wii: Pikachu's Adventure"
//   System: Wii. Emulator: Dolphin.
//
// WHAT THE APWORLD RANDOMIZES:
//   Attractions (minigames), friendship encounters, Berry pickups, and zone
//   unlocks form the multiworld location pool. The player must befriend the
//   required Pokemon and complete the required attractions to reach the goal.
//
// STATUS: STUB — ChecksImplemented = false. Dolphin is not yet in
//   EmulatorBackends and no launcher bridge module exists for this game.
//   The plugin registers the game so it appears in the UI.
//
// GATE TO ENABLE: add Dolphin to EmulatorBackends (id "dolphin", system "Wii"),
//   build a bridge module, verify in-emulator, set ChecksImplemented = true.
//
// ISO/WBFS: PokéPark Wii: Pikachu's Adventure (NTSC-U, game ID SPPE01).
//   AcceptableBaseRoms is empty — no constraint — so any Wii disc image the
//   player provides is accepted.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PokeParkWiiPlugin : EmulatorPlugin
{
    public PokeParkWiiPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "pokepark_wii";
    public override string DisplayName => "PokePark Wii: Pikachu's Adventure";
    public override string Subtitle    => "Wii · Emulated (Dolphin)";
    public override string ApWorldName => "PokePark Wii: Pikachu's Adventure";

    public override string Description =>
        "PokePark Wii: Pikachu's Adventure is Nintendo's 2009 Wii game in which " +
        "Pikachu explores the magical PokePark, befriending Pokemon by challenging " +
        "them to minigame Attractions and freeing the Sky Prism Pieces scattered " +
        "across its zones. The game blends light platforming, exploration, and " +
        "simple action sequences across seven vibrant zones.\n\n" +
        "In the Archipelago randomizer, Attractions, friendship encounters, Berry " +
        "pickups, and zone unlocks join the multiworld pool. Befriend the required " +
        "Pokemon and complete the Attractions to reach your goal.\n\n" +
        "Requires: your own legal Wii disc image (SPPE01) and Dolphin Emulator. " +
        "The launcher will add integrated Dolphin support in a future update.";

    public override string ThemeAccentColor => "#F6C800";   // Pikachu yellow

    // ── Emulator specifics ────────────────────────────────────────────────────

    // PokePark Wii is a Wii title; RomSystem = "Wii" (not "GC").
    protected override string RomSystem => "Wii";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "dolphin" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "pokepark_wii";

    public override bool ChecksImplemented => false;

    public new string GameDirectory => string.Empty;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();
}
