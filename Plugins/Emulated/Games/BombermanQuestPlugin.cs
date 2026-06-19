using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// BombermanQuestPlugin — Archipelago integration for Bomberman Quest (Game Boy
// Color, 1998, Hudson Soft). Community AP world from
// github.com/Happyhappyism/Archipelago.
//
// STATUS: STUB — ChecksImplemented = false. The BizHawk Lua module
// ("bomberman_quest") is a no-op stub. A second-pass deep dive of the apworld's
// Client / Locations source is required before a real check table and goal
// logic can be built.
//
// WHAT NEEDS A SECOND PASS:
//   1. Parse Happyhappyism's apworld Client.py + Locations.py to extract GBC
//      WRAM addresses for location flags and the goal condition.
//   2. Confirm expected Bomberman Quest (USA) ROM identifier (MD5 if any
//      base_checksum exists in the world).
//   3. Implement "bomberman_quest.lua" with real flag reads and goal detection;
//      flip ChecksImplemented to true.
//
// GBC NOTE: No patch — the player supplies their own legally-obtained
// Bomberman Quest GBC ROM. Runs on BizHawk's GambatteLink or GBHawk core.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class BombermanQuestPlugin : EmulatorPlugin
{
    public BombermanQuestPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "bomberman_quest";
    public override string DisplayName => "Bomberman Quest";
    public override string Subtitle    => "GBC · Emulated";
    public override string ApWorldName => "Bomberman Quest";

    public override string Description =>
        "Bomberman Quest is a 1998 Game Boy Color action RPG where Bomberman " +
        "collects monster parts and upgrades his bombs. The Archipelago community " +
        "world randomizes items and progression. Runs on BizHawk's GBC core. " +
        "Supply your own legal Bomberman Quest ROM.";

    public override string ThemeAccentColor => "#3090D0";   // Bomberman blue

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "GBC";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "bomberman_quest";

    /// STUB: location check detection not yet implemented. Second pass required.
    public override bool ChecksImplemented => false;

    /// No patch — the player's own ROM is used directly. No fixed MD5 to
    /// validate against (any legal dump of Bomberman Quest GBC is accepted).
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();

    // ── GameDirectory — hidden behind "new" per the EmulatorPlugin pattern ────

    public new string GameDirectory => string.Empty;

    // ── ROM safety net ────────────────────────────────────────────────────────

    public override RomRequirement? GetUnmetRomRequirement()
    {
        if (RomPath != null && File.Exists(RomPath)) return null;
        return new RomRequirement(
            DisplayName, "GBC",
            "your own legal Bomberman Quest (USA) GBC ROM",
            RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());
    }

    // ── Session ROM ───────────────────────────────────────────────────────────

    protected override Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        SessionRomNote =
            "[Bomberman Quest] No patch applied — launching your ROM directly. " +
            "Make sure BizHawk is using the GambatteLink or GBHawk GBC core.";
        return Task.FromResult<string?>(null);
    }
}
