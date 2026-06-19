using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// LandstalkerPlugin — Archipelago integration for Landstalker: The Treasures of
// King Nole (Sega Genesis / Mega Drive, 1992), on the proven BizHawk bridge.
//
// Source world: worlds/landstalker (ArchipelagoMW/Archipelago, main branch,
//   world_version 1.8.7, author Dinopony). game string:
//   "Landstalker - The Treasures of King Nole".
//   https://github.com/ArchipelagoMW/Archipelago/tree/main/worlds/landstalker
//
// HOW LANDSTALKER IS RANDOMIZED — IMPORTANT, AND DIFFERENT FROM MOST WORLDS:
// Landstalker is NOT patched by Archipelago, and it does NOT ship a Python
// BizHawkClient/SNIClient inside the apworld. There is no APProcedurePatch
// container, no Client.py, and no rom.py in the world directory. Instead the
// game is played through an EXTERNAL, closed-source connector —
// `randstalker_archipelago.exe` (the "Landstalker Archipelago Client", Windows
// only). That client:
//   • connects to the AP server ITSELF (it is the AP client for this game), and
//   • BUILDS the randomized ROM from the player's own Landstalker US dump
//     through its own UI ("input ROM path" + "output directory" + "Build ROM"),
//     then runs/feeds the chosen emulator (BizHawk 2.9.1 or RetroArch, Genesis
//     Plus GX core) and reads/writes the core's memory through its own private
//     interface.
// So, exactly like Final Fantasy 1 here (own external randomizer, FF1 pattern):
//   • the launcher applies NO patch of its own — PrepareSessionRomAsync returns
//     null and the base class launches the library ROM directly (§11: the copy,
//     the original is never touched). The ROM the player imports should be the
//     one BUILT by randstalker_archipelago.exe for their seed.
//   • there is no AP-published RAM map / signature / goal flag to replicate, so
//     the in-emulator Lua module (landstalker.lua) carries the exact, source-
//     derived 292-id LOCATION TABLE but GATES live reporting (no invented RAM
//     reads) — hence ChecksImplemented => false below. This is the honest split:
//     the launcher can host the game and surface its real check pool, but the
//     authoritative AP client for Landstalker remains the external exe.
//
// ROM IDENTITY (by CONTENT, never by filename — §11): the vanilla base is the
//   Landstalker (USA) Genesis cartridge dump, 2,097,152 bytes (2 MB), MD5
//   04AE2B65F3A11A7504339E60735BEED8 (No-Intro; the Steam LandStalker_USA.SGD is
//   the same US data). The Genesis Mini US dump is also accepted. The randomized
//   ROM the client builds is seed-unique (no fixed output MD5), so size + the
//   accepted base MD5s are the stable fingerprint at import time.
//
// GOAL: AP completion_condition (Rules.py) is
//   state.has("King Nole's Treasure") — a locked event item on the "End"
//   location; all three goal options (beat_gola / reach_kazalt / beat_dark_nole,
//   Options.py) route through that single End event. The external client raises
//   the AP goal; the in-RAM flag for it is not published, so landstalker.lua's
//   is_goal_complete() is gated off too.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class LandstalkerPlugin : EmulatorPlugin
{
    public LandstalkerPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "landstalker";
    public override string DisplayName => "Landstalker: The Treasures of King Nole";
    public override string Subtitle    => "Genesis · Emulated";

    /// EXACT AP world game string (worlds/landstalker archipelago.json / __init__).
    public override string ApWorldName => "Landstalker - The Treasures of King Nole";

    public override string Description =>
        "Landstalker: The Treasures of King Nole is the 1992 Sega Genesis " +
        "isometric action-RPG. You play Nigel, a treasure hunter exploring the " +
        "island of Mercator. In the Archipelago randomizer the chests, ground " +
        "items, shop slots and quest rewards across the whole island join the " +
        "multiworld pool, with key items handed out by the server. Reach the " +
        "hidden palace and claim King Nole's Treasure to win. Landstalker is " +
        "randomized and played through the external Landstalker Archipelago " +
        "client (randstalker_archipelago.exe), which builds your ROM and drives " +
        "the emulator.";

    public override string ThemeAccentColor => "#3FA34D";   // grass-green (web theme)

    // ── Emulator specifics ────────────────────────────────────────────────────

    // Genesis — same system identifier the shipped Sonic 1 Genesis plugin uses.
    protected override string RomSystem     => "GEN";

    // The Landstalker setup doc names BizHawk 2.9.1 (Genesis Plus GX) as the
    // supported emulator; BizHawk is the verified Genesis backend here.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "landstalker";

    /// FALSE on purpose. landstalker.lua carries the exact, source-derived 292-id
    /// location table, but Landstalker ships no in-repo RAM map / signature / goal
    /// flag (its memory client is the external randstalker_archipelago.exe), so
    /// live check + goal detection are GATED rather than invented. The launcher
    /// warns at launch so a player is never left wondering why no checks flow; the
    /// authoritative AP client for this game is the external exe. Flip to true only
    /// alongside a real, source-verified Genesis RAM map in landstalker.lua.
    public override bool ChecksImplemented => false;

    /// Accepted base ROMs by CONTENT (size first, then MD5) — never by filename
    /// (§11). The randomizer takes a Landstalker US dump; the No-Intro USA dump
    /// and the Genesis Mini USA dump are both valid 2 MB bases. The Steam
    /// LandStalker_USA.SGD is the same US data (2 MB) and matches by size/MD5.
    /// The randomized ROM the external client builds is seed-unique (no fixed
    /// MD5), so these base identities are what we validate at import time.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(2_097_152, "04ae2b65f3a11a7504339e60735beed8",
                        "Landstalker - The Treasures of King Nole (USA)"),
        new RomIdentity(2_097_152, "dbaa50ee8b061b573c33337e18329ecd",
                        "Landstalker - The Treasures of King Nole (USA) (Genesis Mini)"),
    };

    // ── AP patch (optional/inert — kept for parity, like FF1) ─────────────────
    //
    // Landstalker has no APProcedurePatch container (the external client builds
    // the ROM). We keep the standard hook shape so an explicit container dropped
    // in Settings would still be honoured, but the AP generator never emits one
    // and the normal path applies no patch.

    private const string PatchExt = ".aplandstalker";

    /// Explicit patch override chosen by the user (Settings / drag-drop). Null =
    /// none — the normal path, the (externally built) library ROM is loaded as-is.
    public string? ApPatchPath { get; private set; }

    protected override void OnSavingSettings(IDictionary<string, object?> bag)
        => bag["ap_patch_path"] = ApPatchPath;

    protected override void OnLoadingSettings(JsonElement root)
    {
        if (root.TryGetProperty("ap_patch_path", out var el)) ApPatchPath = el.GetString();
    }

    /// Set an explicit patch override (rare for Landstalker) and persist it.
    public void SetExplicitPatch(string path) { ApPatchPath = path; SaveSettings(); }

    /// ROM safety net. Landstalker has no patch carrying an expected MD5, so the
    /// only requirement we can surface is "a Landstalker US ROM is present" — the
    /// player supplies the ROM their external client built. A future explicit
    /// .aplandstalker override (which the AP generator does not emit) would still
    /// demand its base MD5 if one were set.
    public override RomRequirement? GetUnmetRomRequirement()
    {
        var patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out _);
        string? wantMd5 = patch != null
            ? SnesApPatchHelper.ReadManifestField(patch, "base_checksum") : null;
        bool haveRom = RomPath != null && File.Exists(RomPath);

        if (wantMd5 == null)
            return haveRom ? null
                 : new RomRequirement("Landstalker: The Treasures of King Nole", "GEN",
                       "your Landstalker (USA) ROM as built by the Landstalker " +
                       "Archipelago client (randstalker_archipelago.exe)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Landstalker: The Treasures of King Nole", "GEN",
            "Landstalker (USA) — the exact ROM this patch was generated against",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM ───────────────────────────────────────────────────────────
    //
    // The normal Landstalker path applies NO patch — the player's library ROM is
    // the one randstalker_archipelago.exe built for their seed, so we return null
    // and the base class launches it directly. Only an explicit .aplandstalker
    // override (not emitted by the AP generator) would trigger SnesApPatchHelper;
    // that branch is kept for parity with the other emulated games but is inert
    // for standard Landstalker seeds.

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            // Standard Landstalker: no AP patch container exists. The library ROM
            // is the randomized .md/.bin the player built with the external client.
            SessionRomNote =
                "[Landstalker] Landstalker is randomized and played through the " +
                "external Landstalker Archipelago client (randstalker_archipelago.exe), " +
                "not by this launcher — launching your ROM directly. Make sure this " +
                "is the ROM that client BUILT for your seed (it connects to the AP " +
                "server itself and delivers items; this launcher hosts the game and " +
                "knows its check pool, but check/goal detection runs in that client).";
            return null;
        }

        // Optional explicit-override path (kept for parity; Landstalker never
        // emits .aplandstalker). Produce a Genesis .md from the base dump.
        var res = await SnesApPatchHelper.ApplyAsync(
            "Landstalker", patch, how, RomPath!,
            RomLibraryDirectory, ".md", ct);
        SessionRomNote = res.Note;
        if (res.OutRom != null) RegisterSeed(res.OutRom, patch);
        return res.OutRom;
    }

    /// Record a produced/reused patched ROM in the seed library. Never throws.
    /// (Only reached on the optional explicit-override path — standard Landstalker
    /// seeds launch the externally-built ROM directly and never get here.)
    private void RegisterSeed(string outRom, string patch)
    {
        try
        {
            SeedLibraryStore.Instance.Register(new SeedEntry
            {
                GameId         = GameId,
                PatchedRomPath = outRom,
                PatchPath      = patch,
                SeedName       = SnesApPatchHelper.ParseSeedFromPatch(patch),
                SlotName       = CurrentSlotName ?? "",
                CreatedUtc     = DateTimeOffset.UtcNow,
            });
        }
        catch { /* registry is non-essential to launching */ }
    }
}
