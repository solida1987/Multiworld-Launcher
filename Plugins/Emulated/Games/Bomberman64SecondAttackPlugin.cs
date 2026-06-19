using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// Bomberman64SecondAttackPlugin — Archipelago integration for
// Bomberman 64: The Second Attack! (Nintendo 64, 2000),
// on the proven BizHawk Lua-pipe bridge (Mupen64Plus / N64Hawk core).
//
// WORLD SOURCE: Community apworld by Happyhappyism.
//   Repository: github.com/Happyhappyism/Archipelago (community fork)
//   AP game string: "Bomberman 64: The Second Attack!"
//   System: N64. Emulator: BizHawk (Mupen64Plus or N64Hawk core).
//
// WHAT THE APWORLD RANDOMIZES:
//   Elemental Stones, elemental bomb partners, and items spread across the
//   cosmic stages join the multiworld location pool. Defeat the cosmic villains
//   to complete the goal.
//
// STATUS: STUB — ChecksImplemented = false. The BizHawk Lua module
//   ("bomberman_64_second_attack") is a no-op stub. A second-pass analysis of
//   the apworld's Client / Locations source is required before a real check
//   table and goal logic can be built.
//
// GATE TO ENABLE: parse the apworld Client.py + Locations.py to extract N64
//   RDRAM addresses for location flags and the goal condition, implement the
//   bomberman_64_second_attack Lua module with real flag reads, then flip
//   ChecksImplemented.
//
// ROM: Player supplies their own legal Bomberman 64: The Second Attack! N64
//   cartridge dump. No patch applied — ROM is launched directly.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Bomberman64SecondAttackPlugin : EmulatorPlugin
{
    public Bomberman64SecondAttackPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "bomberman_64_second_attack";
    public override string DisplayName => "Bomberman 64: The Second Attack!";
    public override string Subtitle    => "N64 · Emulated";
    public override string ApWorldName => "Bomberman 64: The Second Attack!";

    public override string Description =>
        "Bomberman 64: The Second Attack! is the 2000 N64 action RPG sequel in " +
        "which Bomberman fights cosmic villains with elemental bombs. Collecting " +
        "Elemental Stones and teaming up with elemental bomb partners are the " +
        "keys to stopping the evil Rukifellth. The Archipelago community world " +
        "randomizes Elemental Stones, partners, and items into the multiworld " +
        "pool. Runs on BizHawk's Mupen64Plus or N64Hawk core. You supply your " +
        "own legal Bomberman 64: The Second Attack! N64 cartridge dump.\n\n" +
        "Source: github.com/Happyhappyism/Archipelago (community fork).";

    public override string ThemeAccentColor => "#D04020";   // fire red, Second Attack theme

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "N64";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "bomberman_64_second_attack";

    /// STUB: location check detection not yet implemented. Second pass required.
    public override bool ChecksImplemented => false;

    /// No fixed MD5 constraint — the player supplies their own legal dump.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();

    // ── ROM safety net ────────────────────────────────────────────────────────

    public new string GameDirectory => string.Empty;

    public override RomRequirement? GetUnmetRomRequirement()
    {
        if (RomPath != null && File.Exists(RomPath)) return null;
        return new RomRequirement(
            DisplayName, "N64",
            "your own legal Bomberman 64: The Second Attack! N64 cartridge dump (.z64 / .n64 / .v64)",
            RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());
    }

    // ── Session ROM ───────────────────────────────────────────────────────────

    protected override Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        SessionRomNote =
            "[Bomberman 64: The Second Attack!] This AP world does not patch " +
            "the ROM — launching your cartridge dump directly. Make sure " +
            "BizHawk is using the Mupen64Plus or N64Hawk core.";
        return Task.FromResult<string?>(null);
    }
}
