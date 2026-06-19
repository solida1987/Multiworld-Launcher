using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// PaperMarioTTYDPlugin — Archipelago integration for
// Paper Mario: The Thousand-Year Door (GameCube, 2004).
//
// CONFIRMED AP WORLD:
//   Community apworld by JKBSunshine and contributors.
//   Repository: github.com/JKBSunshine/PMTTYD_APWorld
//   AP game string: "Paper Mario The Thousand Year Door"
//   System: GameCube. Emulator: Dolphin.
//
// WHAT THE APWORLD RANDOMIZES:
//   Crystal Stars, partners, key items, badges, and chapter-clearing goals
//   form the multiworld location pool. The player must collect enough Crystal
//   Stars and reach the final boss to complete the goal.
//
// STATUS: STUB — ChecksImplemented = false. Dolphin is not yet in
//   EmulatorBackends and no launcher bridge module exists for this game.
//   The plugin registers the game so it appears in the UI.
//
// GATE TO ENABLE: add Dolphin to EmulatorBackends (id "dolphin", system "GC"),
//   build a bridge module, verify in-emulator, set ChecksImplemented = true.
//
// ISO: Paper Mario: The Thousand-Year Door (NTSC-U, game ID G8ME01).
//   AcceptableBaseRoms is empty — no constraint — so any GCN ISO the player
//   provides is accepted.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PaperMarioTTYDPlugin : EmulatorPlugin
{
    public PaperMarioTTYDPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "paper_mario_ttyd";
    public override string DisplayName => "Paper Mario: The Thousand-Year Door";
    public override string Subtitle    => "GameCube · Emulated (Dolphin)";
    public override string ApWorldName => "Paper Mario The Thousand Year Door";

    public override string Description =>
        "Paper Mario: The Thousand-Year Door is Nintendo's beloved 2004 GameCube " +
        "RPG in which Mario travels to Rogueport to collect the seven Crystal Stars " +
        "and uncover the mystery of the Thousand-Year Door. With a colorful cast of " +
        "partners, a theatrical battle system, and clever chapter-based storytelling, " +
        "it is widely regarded as one of the greatest RPGs of its era.\n\n" +
        "In the Archipelago randomizer (by JKBSunshine), Crystal Stars, partners, " +
        "key items, and badges join the multiworld pool. Collect the required Crystal " +
        "Stars and reach the final chapter to complete your goal.\n\n" +
        "Requires: your own legal NTSC-U GameCube ISO (G8ME01) and Dolphin Emulator. " +
        "The launcher will add integrated Dolphin support in a future update.\n\n" +
        "Source: github.com/JKBSunshine/PMTTYD_APWorld.";

    public override string ThemeAccentColor => "#8B2FC9";   // Thousand-Year Door purple

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "GC";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "dolphin" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "paper_mario_ttyd";

    public override bool ChecksImplemented => false;

    public new string GameDirectory => string.Empty;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();
}
