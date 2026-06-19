using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// SpicyMycenaWafflesPlugin — Archipelago integration for SMW: Spicy Mycena
// Waffles, a custom Super Mario World SNES ROM hack with its own dedicated
// Archipelago world by TheLX5 (github.com/TheLX5/Archipelago).
//
// NOTE: This is a SEPARATE game from the standard Super Mario World AP world.
// The ROM hack carries its own apworld, its own patch format, and its own
// location/item pool — it must not be confused with the vanilla SMW world.
//
// STATUS: STUB — ChecksImplemented = false. The BizHawk Lua module
// ("smw_spicy_mycena_waffles") is a no-op stub. A second-pass deep dive of
// TheLX5's apworld Client + Locations source is required before a real check
// table and goal logic can be built.
//
// WHAT NEEDS A SECOND PASS:
//   1. Parse TheLX5/Archipelago Client.py + Locations.py to extract SNES
//      WRAM addresses for location-checked flags and the goal condition.
//   2. Confirm the expected base ROM (Super Mario World (USA) dump, and any
//      additional patch-to-hack step that produces the target image).
//   3. Implement "smw_spicy_mycena_waffles.lua" with real flag reads and
//      goal detection; flip ChecksImplemented to true.
//
// SNES NOTE: The player must apply the Spicy Mycena Waffles AP patch to a
// Super Mario World SNES ROM using Lunar IPS (or similar). No ROM is
// shipped by the launcher. Runs on BizHawk's BSNES or Faust SNES core.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SpicyMycenaWafflesPlugin : EmulatorPlugin
{
    public SpicyMycenaWafflesPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "smw_spicy_mycena_waffles";
    public override string DisplayName => "SMW: Spicy Mycena Waffles";
    public override string Subtitle    => "SNES ROM hack · BizHawk required";
    public override string ApWorldName => "SMW: Spicy Mycena Waffles";

    public override string Description =>
        "SMW: Spicy Mycena Waffles is a custom Super Mario World ROM hack with " +
        "its own dedicated Archipelago world by TheLX5. Requires BizHawk " +
        "emulator and the specific patch applied to a Super Mario World SNES ROM.";

    public override string ThemeAccentColor => "#FF6A00";

    public override string[] GameBadges => new[] { "SNES", "ROM Hack", "BizHawk Required" };

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "SNES";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "smw_spicy_mycena_waffles";

    /// STUB: location check detection not yet implemented. Second pass required
    /// (TheLX5/Archipelago Client.py + Locations.py).
    public override bool ChecksImplemented => false;

    /// No ROM identity constraint declared yet — the player applies the patch
    /// themselves and provides the resulting hacked ROM image.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();

    // ── GameDirectory — hidden behind "new" per the EmulatorPlugin pattern ────

    public new string GameDirectory => string.Empty;

    // ── ROM safety net ────────────────────────────────────────────────────────

    public override RomRequirement? GetUnmetRomRequirement()
    {
        if (RomPath != null && File.Exists(RomPath)) return null;
        return new RomRequirement(
            DisplayName, "SNES",
            "your patched Spicy Mycena Waffles SNES ROM (.sfc or .smc)",
            RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());
    }

    // ── Session ROM ───────────────────────────────────────────────────────────

    protected override Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        SessionRomNote =
            "[SMW: Spicy Mycena Waffles] No automatic patch applied — launching " +
            "your pre-patched ROM directly. Apply the Spicy Mycena Waffles AP " +
            "patch to a Super Mario World SNES ROM using Lunar IPS before " +
            "launching. Search the TheLX5/Archipelago releases for 'Spicy'.";
        return Task.FromResult<string?>(null);
    }
}
