using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// Sly2BandOfThievesPlugin — Archipelago integration for
// Sly 2: Band of Thieves (PS2, 2004).
//
// ── CONFIRMED AP WORLD ───────────────────────────────────────────────────────
//
// A community Archipelago world exists for Sly 2: Band of Thieves
// (PlayStation 2, 2004). The apworld randomizes clue bottle collections,
// gadget unlocks, and episode progression across the game's eight episodes.
//
// AP game string: "Sly 2: Band of Thieves" (inferred from world naming
// convention; verify against worlds/__init__.py).
//
// HOW THE INTEGRATION WORKS:
//   Sly 2 runs on PCSX2. The launcher registers the game in the catalog.
//   The player supplies their own legal Sly 2 PS2 disc image.
//   ChecksImplemented is false until a PCSX2 AP bridge is built and verified.
//
// GATE TO ENABLE FULL INTEGRATION: build a PCSX2 bridge module for this game,
// verify in-emulator, and set ChecksImplemented = true.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Sly2BandOfThievesPlugin : EmulatorPlugin
{
    public Sly2BandOfThievesPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "sly2_band_of_thieves";
    public override string DisplayName => "Sly 2: Band of Thieves";
    public override string Subtitle    => "PS2 · Emulated (PCSX2)";
    public override string ApWorldName => "Sly 2: Band of Thieves";

    public override string Description =>
        "Sly 2: Band of Thieves (2004, PlayStation 2) follows master thief Sly " +
        "Cooper and his gang — Bentley and Murray — as they travel the globe to " +
        "recover pieces of the dreaded Clockwerk body before the villainous " +
        "Klaww Gang can reassemble and misuse them. Eight episodes across " +
        "Paris, India, Canada, Prague, and beyond unfold with stealth missions, " +
        "gadget heists, and team-based capers.\n\n" +
        "In the Archipelago randomizer, clue bottles, gadgets, and episode " +
        "access join the multiworld pool. Collect every clue, crack every safe, " +
        "and take down the Klaww Gang to complete the mission.\n\n" +
        "Requires: your own legal Sly 2: Band of Thieves PS2 disc image and " +
        "PCSX2. The launcher will add full integrated PCSX2 support in a future " +
        "update.";

    public override string ThemeAccentColor => "#3366AA";   // Sly Cooper blue

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "PS2";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "pcsx2" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "sly2_band_of_thieves";

    public override bool ChecksImplemented => false;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();

    // Hides the base class property that returns EmulatorDirectory.
    public new string GameDirectory => string.Empty;
}
