using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// CastlevaniaLegacyOfDarknessPlugin — Archipelago integration for
// Castlevania: Legacy of Darkness (Nintendo 64, 1999, Konami).
// Community AP world from github.com/LiquidCat64/LiquidCatipelago.
//
// STATUS: STUB — ChecksImplemented = false. The BizHawk Lua module
// ("castlevania_legacy_of_darkness") is a no-op stub. A second-pass review
// of LiquidCat64's apworld Client + Locations source is required before a
// real check table and goal logic can be built.
//
// WHAT NEEDS A SECOND PASS:
//   1. Parse LiquidCat64/LiquidCatipelago Client.py + Locations.py to extract
//      N64 RDRAM addresses for location-checked flags and the goal condition.
//   2. Confirm the expected base ROM identity (Castlevania: Legacy of Darkness
//      (USA) dump — MD5/size from the apworld's patch manifest if any patch
//      format ships).
//   3. Implement "castlevania_legacy_of_darkness.lua" with real flag reads
//      and goal detection (N64 is big-endian — mirror the cv64.lua pattern);
//      flip ChecksImplemented to true.
//
// N64 NOTE: No patch — the player supplies their own legally-obtained
// Castlevania: Legacy of Darkness N64 ROM. Runs on BizHawk's Mupen64Plus
// or Nymashock N64 core. The Lua connector script must be loaded manually
// inside BizHawk (Tools → Lua Console → Open Script).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CastlevaniaLegacyOfDarknessPlugin : EmulatorPlugin
{
    public CastlevaniaLegacyOfDarknessPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "castlevania_legacy_of_darkness";
    public override string DisplayName => "Castlevania: Legacy of Darkness";
    public override string Subtitle    => "N64 · BizHawk required";
    public override string ApWorldName => "Castlevania Legacy of Darkness";

    public override string Description =>
        "Castlevania: Legacy of Darkness for N64 with an Archipelago randomizer " +
        "via BizHawk emulator. Requires BizHawk and a Castlevania: Legacy of " +
        "Darkness N64 ROM.";

    public override string ThemeAccentColor => "#800000";

    public override string[] GameBadges => new[] { "N64", "BizHawk Required", "ROM Required" };

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "N64";

    // N64 runs on BizHawk here only — snes9x/mGBA/Mesen are other-system
    // emulators and are NOT offered for N64 games.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "castlevania_legacy_of_darkness";

    /// STUB: location check detection not yet implemented. Second pass required
    /// (LiquidCat64/LiquidCatipelago Client.py + Locations.py).
    public override bool ChecksImplemented => false;

    /// No patch — the player's own ROM is used directly. No ROM identity
    /// constraint declared yet; any legal dump of the N64 game is accepted.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();

    // ── GameDirectory — hidden behind "new" per the EmulatorPlugin pattern ────

    public new string GameDirectory => string.Empty;

    // ── ROM safety net ────────────────────────────────────────────────────────

    public override RomRequirement? GetUnmetRomRequirement()
    {
        if (RomPath != null && File.Exists(RomPath)) return null;
        return new RomRequirement(
            DisplayName, "N64",
            "your own legal Castlevania: Legacy of Darkness N64 ROM",
            RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());
    }

    // ── Session ROM ───────────────────────────────────────────────────────────

    protected override Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        SessionRomNote =
            "[Castlevania: Legacy of Darkness] No patch applied — launching your " +
            "ROM directly. Make sure BizHawk is using an N64 core (Mupen64Plus " +
            "or Nymashock) and load the Lua connector script manually via Tools " +
            "→ Lua Console → Open Script.";
        return Task.FromResult<string?>(null);
    }
}
