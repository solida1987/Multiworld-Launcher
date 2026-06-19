using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// MediEvilPlugin — Archipelago integration for MediEvil (1998, PSX).
//
// ── CONFIRMED AP WORLD ───────────────────────────────────────────────────────
//
// A community Archipelago world exists for MediEvil (original 1998 PlayStation
// release). The apworld randomizes chalice collection, weapon unlocks, and level
// progression across the game's 24 levels.
//
// AP game string: "MediEvil" (inferred from world naming convention; verify
// against worlds/__init__.py).
//
// HOW THE INTEGRATION WORKS:
//   MediEvil runs on PCSX2. The launcher registers the game in the catalog.
//   The player supplies their own legal MediEvil PSX disc image.
//   ChecksImplemented is false until a PCSX2 AP bridge is built and verified.
//
// GATE TO ENABLE FULL INTEGRATION: build a PCSX2 bridge module for this game,
// verify in-emulator, and set ChecksImplemented = true.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MediEvilPlugin : EmulatorPlugin
{
    public MediEvilPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "medievil";
    public override string DisplayName => "MediEvil";
    public override string Subtitle    => "PSX · Emulated (BizHawk)";
    public override string ApWorldName => "MediEvil";

    public override string Description =>
        "MediEvil (1998, PlayStation) is a gothic action-adventure in which the " +
        "bumbling skeleton knight Sir Daniel Fortesque rises from the dead to " +
        "defend Gallowmere from the evil sorcerer Zarok. Armed with shields, " +
        "swords, and magical weapons collected from the Hall of Heroes, Dan must " +
        "fight through 24 levels of monsters, puzzles, and ancient ruins.\n\n" +
        "In the Archipelago randomizer, chalices, weapons, and level access join " +
        "the multiworld pool. Fill the Chalice of Souls in each level to collect " +
        "rewards and prove Dan worthy of his place in the Hall of Heroes.\n\n" +
        "Requires: your own legal MediEvil PSX disc image. BizHawk (PSX core) is " +
        "supported today; native PCSX2 support is coming in a future update.";

    public override string ThemeAccentColor => "#4A7A3A";   // Gallowmere forest green

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "PSX";

    // BizHawk (PSX core) works today; PCSX2 is the "native" choice and listed
    // second so the dropdown shows it as "(coming soon)" when it ships.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "pcsx2" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "medievil";

    public override bool ChecksImplemented => false;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();

    // Hides the base class property that returns EmulatorDirectory.
    public new string GameDirectory => string.Empty;
}
