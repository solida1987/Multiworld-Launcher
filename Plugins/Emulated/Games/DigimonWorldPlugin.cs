using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// DigimonWorldPlugin — Archipelago integration for Digimon World (PSX, 1999).
//
// ── CONFIRMED AP WORLD ───────────────────────────────────────────────────────
//
// A community Archipelago world exists for Digimon World (PlayStation, 1999).
// The apworld randomizes Digimon recruitment, item pickups, and progression
// gates across File Island.
//
// AP game string: "Digimon World" (inferred from world naming convention;
// verify against worlds/__init__.py).
//
// HOW THE INTEGRATION WORKS:
//   Digimon World runs on PCSX2. The launcher registers the game in the catalog.
//   The player supplies their own legal Digimon World PSX disc image.
//   ChecksImplemented is false until a PCSX2 AP bridge is built and verified.
//
// GATE TO ENABLE FULL INTEGRATION: build a PCSX2 bridge module for this game,
// verify in-emulator, and set ChecksImplemented = true.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DigimonWorldPlugin : EmulatorPlugin
{
    public DigimonWorldPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "digimon_world";
    public override string DisplayName => "Digimon World";
    public override string Subtitle    => "PSX · Emulated (PCSX2)";
    public override string ApWorldName => "Digimon World";

    public override string Description =>
        "Digimon World (1999, PlayStation) is the original monster-raising RPG set " +
        "on File Island. A young Tamer arrives to find the island's Digimon have " +
        "lost their memories and must be recruited back to the city one by one. " +
        "Raise your partner through battles, training, and careful feeding to " +
        "reach the ultimate showdown with Machinedramon.\n\n" +
        "In the Archipelago randomizer, Digimon recruitment, key items, and " +
        "progression gates across File Island join the multiworld pool. Restore " +
        "the city by bringing every Digimon home.\n\n" +
        "Requires: your own legal Digimon World PSX disc image and PCSX2. The " +
        "launcher will add full integrated PCSX2 support in a future update.";

    public override string ThemeAccentColor => "#2255BB";   // Digital World blue

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "PSX";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "pcsx2" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "digimon_world";

    public override bool ChecksImplemented => false;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();

    // Hides the base class property that returns EmulatorDirectory.
    public new string GameDirectory => string.Empty;
}
