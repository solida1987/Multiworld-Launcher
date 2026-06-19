using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// YuGiOhForbiddenMemoriesPlugin — Archipelago integration for
// Yu-Gi-Oh! Forbidden Memories (PlayStation/PSX, 2002).
//
// CONFIRMED AP WORLD:
//   Community apworld for Yu-Gi-Oh! Forbidden Memories.
//   AP game string: "Yu-Gi-Oh! Forbidden Memories"
//   System: PSX. Emulator: PCSX2 (via PSX backward-compatibility mode).
//
// WHAT THE APWORLD RANDOMIZES:
//   Card drops, opponent unlocks, star chip progression, deck capacity,
//   and campaign duelist access across the ancient Egyptian storyline join
//   the multiworld pool. The player must duel through the required opponents
//   and defeat the final boss to reach the goal.
//
// STATUS: STUB — ChecksImplemented = false. No PCSX2/PSX bridge module
//   exists for this game yet. The plugin registers the game so it appears
//   in the UI catalog.
//
// GATE TO ENABLE: build a PCSX2 (PSX mode) bridge module, verify in-emulator,
//   and set ChecksImplemented = true.
//
// DISC: Yu-Gi-Oh! Forbidden Memories PSX disc image (NTSC-U or PAL).
//   AcceptableBaseRoms is empty — no constraint applied at this stub stage.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class YuGiOhForbiddenMemoriesPlugin : EmulatorPlugin
{
    public YuGiOhForbiddenMemoriesPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "yugioh_forbidden_memories";
    public override string DisplayName => "Yu-Gi-Oh! Forbidden Memories";
    public override string Subtitle    => "PSX · Emulated (PCSX2)";
    public override string ApWorldName => "Yu-Gi-Oh! Forbidden Memories";

    public override string Description =>
        "Yu-Gi-Oh! Forbidden Memories (2002, PlayStation) is a legendary card " +
        "dueling RPG set in ancient Egypt. Prince Atem is swept back in time to " +
        "confront six powerful Mages who seek to harness the power of the forbidden " +
        "magic sealed in the Millennium Items. Players duel a vast roster of " +
        "opponents to earn rare card drops, star chips, and deck capacity upgrades " +
        "in the quest to restore balance to the kingdom.\n\n" +
        "In the Archipelago randomizer, card drops, star chips, deck capacity, " +
        "opponent access, and campaign progression join the multiworld pool. " +
        "Duel through the required opponents and defeat the final boss to reach " +
        "your goal.\n\n" +
        "Requires: your own legal Yu-Gi-Oh! Forbidden Memories PSX disc image and " +
        "PCSX2. The launcher will add integrated PCSX2 support in a future update.";

    public override string ThemeAccentColor => "#6A2C8F";   // Millennium purple

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "PSX";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "pcsx2" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "yugioh_forbidden_memories";

    public override bool ChecksImplemented => false;

    public new string GameDirectory => string.Empty;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();
}
