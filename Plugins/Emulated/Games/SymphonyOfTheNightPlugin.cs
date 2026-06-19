using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// SymphonyOfTheNightPlugin — Archipelago integration for Castlevania: Symphony of
// the Night (PlayStation / PSX), on the proven BizHawk Lua-pipe bridge.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED. The boss /
// cutscene / relic / prologue detection tables in sotn.lua were GENERATED from
// the community AP world's in-emulator connector (data/lua/connector_sotn.lua in
// github.com/AdmiralTryhard/SOTNArchipelago, world id "sotn") and cross-checked
// against worlds/sotn/Locations.py — all 63 numeric AP location ids are covered,
// goal = the inverted-castle Dracula kill ("Black Marble Gallery: Patricide").
// Mock-verified through MoonSharp against synthetic PSX MainRAM. items_handling =
// 0b111 — the AP SERVER drives ALL item delivery; the reference connector grants
// items (and DENIES un-released vanilla pickups) through a tower of guarded
// MainRAM writes. That write/deny path is the documented deferred piece
// (sotn.lua receive_item is a no-op) until it can be confirmed in-emulator —
// a wrong RAM write would corrupt the live game. Checks + goal flow regardless.
//
// PSX MEMORY: the connector reads PSX main RAM by raw offset via BizHawk's
// `mainmemory` accessor, which on the PSX core (Nymashock) is the "MainRAM"
// memory domain — that is what sotn.lua reads (System Bus as a fallback name).
//
// NO PATCH, NO AP ROM SIGNATURE — IMPORTANT: unlike most AP ROM games, SotN is
// NOT patched by Archipelago. There is no APProcedurePatch container (no
// .apsotn), no ROM signature is written, and fill_slot_data carries no base
// checksum. The player supplies their OWN legally-obtained SotN PSX disc image
// (.bin/.cue or .chd) and starts a fresh game; the AP client + the bundled
// connector_sotn.lua do everything at runtime over live PSX RAM (checks read
// from RAM, items granted/denied by writing RAM). So there is nothing for the
// launcher to patch and nothing to validate by content hash — the disc image is
// the player's own, identified only as a PSX disc (§11: a library copy is used,
// the original is never modified). REQUIRES BizHawk's Nymashock PSX core +
// PSX BIOS firmware, which the player must supply (BizHawk ships neither).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SymphonyOfTheNightPlugin : EmulatorPlugin
{
    public SymphonyOfTheNightPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "sotn";
    public override string DisplayName => "Castlevania: Symphony of the Night";
    public override string Subtitle    => "PSX · Emulated";

    // The community apworld registers itself under the game string "sotn"
    // (worlds/sotn/__init__.py: game = "sotn"). The launcher's ApWorldName must
    // match that exact string so the seed's slot/game line resolves.
    public override string ApWorldName => "sotn";

    public override string Description =>
        "Castlevania: Symphony of the Night is the PlayStation classic that defined " +
        "the Metroidvania genre. In the Archipelago randomizer, Alucard's relics, " +
        "key items, and equipment across both the upright and inverted castles join " +
        "the multiworld pool. Bring Dracula down for real to complete your goal. " +
        "You supply your own legal SotN disc; it runs on BizHawk's Nymashock PSX " +
        "core (a PSX BIOS is also required).";

    public override string ThemeAccentColor => "#5A1421";   // inverted-castle crimson

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "PSX";

    // §14: PSX here runs on BizHawk only (its Nymashock core is what the apworld
    // targets). No snes9x / mGBA / Mesen — none host PSX.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "sotn";

    /// sotn.lua reports checks + goal (detection tables generated from the
    /// community connector; cross-checked against Locations.py; mock-verified).
    /// Remote-item delivery is the documented deferred piece (items_handling =
    /// 0b111, server-driven guarded MainRAM writes + vanilla-pickup denial).
    public override bool ChecksImplemented => true;

    /// The player supplies their OWN legal SotN PSX disc image — the apworld
    /// patches nothing and pins no checksum, so there is no fixed size or MD5 to
    /// validate against (a .bin/.cue or .chd varies by region, redump and
    /// container). Declaring no AcceptableBaseRoms means the picker accepts the
    /// disc the player points at (a PSX disc image) and the launcher copies it
    /// into its library untouched (§11). The connector self-disables on any disc
    /// that is not a fresh-game SotN, so a wrong file simply never reports checks.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms =>
        Array.Empty<RomIdentity>();

    // ── ROM safety net ──────────────────────────────────────────────────────────

    /// SotN has no patch carrying an expected checksum (it is never patched), so
    /// the only requirement we can surface is "a SotN disc image is present" —
    /// the player provides the disc and a fresh save themselves.
    public override RomRequirement? GetUnmetRomRequirement()
    {
        if (RomPath != null && File.Exists(RomPath)) return null;
        return new RomRequirement(
            DisplayName, "PSX",
            "your own legal Castlevania: Symphony of the Night PSX disc image " +
            "(.bin/.cue or .chd) — runs on BizHawk's Nymashock core, PSX BIOS required",
            RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());
    }

    // ── Session ROM ─────────────────────────────────────────────────────────────
    //
    // SotN applies NO patch (see header) — the player's library disc image IS what
    // is launched. Return null so the base class loads it directly. A note steers
    // the player to the required setup (fresh game + Nymashock + BIOS), mirroring
    // the FinalFantasy1 "randomize-it-yourself, no AP patch" pattern.

    protected override Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        SessionRomNote =
            "[Castlevania: Symphony of the Night] SotN is not patched by Archipelago — " +
            "launching your disc image directly. Start a NEW game (do not load an " +
            "existing save, or AP will think you already checked things), and make " +
            "sure BizHawk is using the Nymashock PSX core with a PSX BIOS installed.";
        return Task.FromResult<string?>(null);
    }
}
