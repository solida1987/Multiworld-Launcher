using System.Collections.Generic;
using System.Windows;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// WarioLand3Plugin — Archipelago integration for Wario Land 3 (Game Boy Color,
// 2000, Nintendo).
//
// STATUS: STUB — no confirmed AP world exists for this game (searched
// github.com/ArchipelagoMW/Archipelago, MultiworldGG, community GitHub, and
// community Discord as of 2026-06-16). ChecksImplemented = false; the bridge
// does not report locations until a real apworld is found and a matching Lua
// module (games/wario_land_3.lua) is verified in-emulator.
//
// WHEN AN APWORLD LANDS
// ──────────────────────
// 1. Drop the verified worlds/wario_land_3/ directory into the Archipelago
//    install.
// 2. Build/obtain the BizHawk Lua module (games/wario_land_3.lua) that reads
//    the correct GBC RAM addresses for music box collection, treasure chests,
//    boss clears, or whatever the apworld uses as location checks.
// 3. Set ChecksImplemented = true.
// 4. Fill in AcceptableBaseRoms with the exact dump the apworld's rom.py or
//    patch container demands (size in bytes + lowercase MD5 hex).
//    Known retail dumps:
//      Wario Land 3 (World) (En,Ja)  1 MB  MD5: unknown — fill in from apworld
//      Wario Land 3 (USA)             1 MB  MD5: unknown — fill in from apworld
// 5. If the apworld ships a patch container (e.g. .apwl3), follow the
//    SnesApPatchHelper pattern from OracleOfAgesPlugin / LinksAwakeningDXPlugin
//    and add PrepareSessionRomAsync + GetUnmetRomRequirement overrides.
//
// GAME NOTES
// ──────────
// Wario Land 3 (Game Boy Color, Nintendo, 2000). A sandbox-style GBC platformer
// — Wario explores an overworld map, uncovering four music boxes per level across
// eight worlds. There is no lives system and no game-over screen, making it
// especially friendly to multiworld randomization: progress is non-linear and
// the game tracks completion per-item, not per-run.
//
// A standalone (non-AP) randomizer exists: github.com/AaronDobbe/wl3-randomizer
// — it shuffles music-box treasures across chests. A future AP apworld would
// most naturally model each chest/music-box as a check (roughly 72+ locations)
// with bosses and key items as the progression layer.
//
// The standard GBC emulators for BizHawk AP work are:
//   bizhawk   — Lua scripting via named pipe bridge (primary, bridged today)
//   gambatte   — standalone emulator, no Lua bridge; listed for completeness
//   bgb        — Windows standalone, no Lua bridge; listed for completeness
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class WarioLand3Plugin : EmulatorPlugin
{
    public WarioLand3Plugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "wario_land_3";
    public override string DisplayName => "Wario Land 3";
    public override string Subtitle    => "GBC · Emulated";
    public override string ApWorldName => "Wario Land 3";

    public override string Description =>
        "Wario Land 3 (Game Boy Color, 2000) is a non-linear platformer where " +
        "Wario explores an overworld map, unlocking powers and uncovering music " +
        "boxes hidden across eight worlds. Its chest-based treasure progression " +
        "and lack of a lives system make it a natural fit for Archipelago " +
        "randomization. No confirmed Archipelago world exists for it yet — this " +
        "entry is a placeholder that will connect once the community releases one. " +
        "Check the Archipelago Discord for updates.";

    public override string ThemeAccentColor => "#C8A000";   // Wario gold

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "GBC";

    // BizHawk is the only emulator with a working Lua/pipe AP bridge today.
    // gambatte and bgb are listed as the community's other GBC options but have
    // no bridge — shown as "coming soon" in the emulator dropdown.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "gambatte", "bgb" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "wario_land_3";

    // No apworld exists yet — ChecksImplemented stays false so the launcher
    // warns at launch rather than silently producing zero check traffic.
    public override bool ChecksImplemented => false;

    // AcceptableBaseRoms left empty: without a confirmed apworld we cannot
    // know which exact dump the randomizer will require. The base-class
    // GetUnmetRomRequirement() falls back to "any GBC ROM", which is correct
    // for a stub — it just asks the user to supply some ROM without checking
    // an MD5. Fill this in with the real MD5(s) once an apworld ships.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        System.Array.Empty<RomIdentity>();

    // No AP patch container defined yet. PrepareSessionRomAsync is not
    // overridden — the base implementation launches the library ROM as-is.
}
