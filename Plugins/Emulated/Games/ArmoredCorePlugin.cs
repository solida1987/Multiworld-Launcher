using System.Collections.Generic;
using LauncherV2.Plugins.Emulated;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// ArmoredCorePlugin — Archipelago integration for Armored Core (PSX, 1997).
//
// ── CONFIRMED AP WORLD (github.com/JustinMarshall98/Armored-Core-PSX-Archipelago)
//
// A community Archipelago world exists for Armored Core (PlayStation, 1997).
// Repository: github.com/JustinMarshall98/Armored-Core-PSX-Archipelago
// (maintained by JustinMarshall98).
//
// The apworld randomizes AC parts, mission access, and arena progression.
//
// AP game string: "Armored Core" (inferred from repository; verify against
// worlds/__init__.py).
//
// HOW THE INTEGRATION WORKS:
//   Armored Core runs on PCSX2. The launcher registers the game in the catalog.
//   The player supplies their own legal Armored Core PSX disc image.
//   ChecksImplemented is false until a PCSX2 AP bridge is built and verified.
//
// GATE TO ENABLE FULL INTEGRATION: build a PCSX2 bridge module for this game
// using JustinMarshall98's world source, verify in-emulator, set
// ChecksImplemented = true.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ArmoredCorePlugin : EmulatorPlugin
{
    public ArmoredCorePlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "armored_core_psx";
    public override string DisplayName => "Armored Core";
    public override string Subtitle    => "PSX · Emulated (PCSX2)";
    public override string ApWorldName => "Armored Core";

    public override string Description =>
        "Armored Core (1997, PlayStation) is FromSoftware's original mech action " +
        "game. As a mercenary Raven piloting a customizable Armored Core, you " +
        "take contracts from corporations and fight through the underground city " +
        "of Camber while uncovering a conspiracy threatening to tear the surface " +
        "world apart.\n\n" +
        "In the Archipelago randomizer (by JustinMarshall98), AC parts, mission " +
        "unlocks, and arena access join the multiworld pool. Build your ultimate " +
        "mech from scattered components and defeat the Chrome and Murakumo " +
        "leadership to complete your run.\n\n" +
        "Requires: your own legal Armored Core PSX disc image and PCSX2. The " +
        "launcher will add full integrated PCSX2 support in a future update.\n\n" +
        "Source: github.com/JustinMarshall98/Armored-Core-PSX-Archipelago.";

    public override string ThemeAccentColor => "#5A7A9A";   // AC steel blue

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "PSX";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "pcsx2" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "armored_core_psx";

    public override bool ChecksImplemented => false;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();

    // Hides the base class property that returns EmulatorDirectory.
    public new string GameDirectory => string.Empty;
}
