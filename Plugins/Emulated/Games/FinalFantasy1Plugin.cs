using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// FinalFantasy1Plugin — Archipelago integration for Final Fantasy (NES, USA), on
// the proven BizHawk Lua-pipe bridge.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED (the 267-location
// id list in ff1.lua was GENERATED from worlds/ff1/data/locations.json; the
// chest/NPC bit math + goal flag + "FINAL FANTASY" ROM signature are replicated
// exactly from worlds/ff1/Client.py, and mock-verified through MoonSharp against
// synthetic NES memory). items_handling = 0b111 — the AP SERVER drives ALL item
// delivery; the reference client writes received items into the cartridge save RAM
// (WRAM) gated on the game's own items-obtained counter. That guarded-write path is
// the documented deferred piece (ff1.lua receive_item is a no-op) until it can be
// confirmed in-emulator, rather than shipped unverified — a wrong WRAM write would
// corrupt the save. Checks + goal flow regardless.
//
// HOW FF1 IS RANDOMIZED — IMPORTANT: unlike most AP ROM games, Final Fantasy 1 is
// NOT patched by Archipelago. There is no APProcedurePatch container (.apff1) and
// fill_slot_data() is empty. The player randomizes their own legally-obtained
// vanilla USA ROM on https://finalfantasyrandomizer.com/ ("enable the Archipelago
// flag"), which downloads BOTH a pre-randomized *.nes AND the matching *.yaml. The
// ALREADY-RANDOMIZED .nes is what the player loads here — the launcher applies no
// patch of its own; it loads the ROM directly (§11: the library copy, original
// untouched). The randomizer expands the ROM MMC1→MMC3 (256 KB → 512 KB PRG, CHR
// RAM, no CHR ROM), so the randomized file is 524,304 bytes (512 KB + 16-byte iNES
// header). It is identified by CONTENT (size + the "FINAL FANTASY" PRG-ROM
// signature ff1.lua verifies) — never by filename (§11). Each seed has a unique
// MD5, so no fixed MD5 can be demanded; the size + signature are the stable
// fingerprint.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class FinalFantasy1Plugin : EmulatorPlugin
{
    public FinalFantasy1Plugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "ff1";
    public override string DisplayName => "Final Fantasy";
    public override string Subtitle    => "NES · Emulated";
    public override string ApWorldName => "Final Fantasy";

    public override string Description =>
        "Final Fantasy, the 1987 NES original that launched the series. In the " +
        "Archipelago randomizer, key items, equipment, gold, and consumables across " +
        "the whole adventure join the multiworld pool, with shuffled towns, dungeons " +
        "and fiends. Reach and terminate Chaos to complete your goal. Randomize your " +
        "own ROM at finalfantasyrandomizer.com with the Archipelago flag enabled.";

    public override string ThemeAccentColor => "#2A4DA8";   // Final Fantasy crystal blue

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "NES";

    // §14: NES emulators — BizHawk is the FF1 community's verified client target;
    // Mesen is the NES alternative (shown per its bridge state in the dropdown).
    // mGBA is NOT offered — it is a GB/GBA emulator and cannot host NES.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "mesen" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "ff1";

    /// ff1.lua reports checks + goal (id list generated from the apworld; bit math
    /// from Client.py; mock-verified). Remote-item delivery is the documented
    /// deferred piece (items_handling = 0b111, server-driven WRAM writes).
    public override bool ChecksImplemented => true;

    /// The ROM the player loads is the ALREADY-RANDOMIZED FF1 .nes from
    /// finalfantasyrandomizer.com, identified by CONTENT — never by filename (§11).
    /// FFR expands MMC1→MMC3, so the randomized ROM is 524,304 bytes (512 KB PRG +
    /// 16-byte iNES header). No MD5 is pinned: every seed differs, so size is the
    /// stable detector and ff1.lua's "FINAL FANTASY" PRG-ROM signature confirms it
    /// is really an FF1 ROM at launch. The vanilla 256 KB USA cartridge dump
    /// (262,160 bytes, MD5 24ae5edf8375162f91a6846d3202e3d6) is also accepted so a
    /// player who points at the un-randomized file is not blocked at import — but
    /// only the randomized ROM carries the multiworld's checks and items.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(524_304, null,
                        "Final Fantasy (USA) — Archipelago-randomized ROM from finalfantasyrandomizer.com"),
        new RomIdentity(262_160, "24ae5edf8375162f91a6846d3202e3d6",
                        "Final Fantasy (USA) — vanilla cartridge dump (randomize it on finalfantasyrandomizer.com first)"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────
    //
    // FF1 has no APProcedurePatch (see header). We keep the standard hook shape for
    // consistency, but the resolver is only an optional override: if a player ever
    // drops an explicit patch container in Settings it would be honored, otherwise
    // the randomized ROM is loaded directly with no patch step. The extension below
    // is a placeholder for that optional path — the AP generator never emits one.

    private const string PatchExt = ".apff1";

    /// Explicit patch override chosen by the user (Settings / drag-drop). Null =
    /// none — the normal FF1 path, the randomized ROM is loaded as-is.
    public string? ApPatchPath { get; private set; }

    protected override void OnSavingSettings(IDictionary<string, object?> bag)
        => bag["ap_patch_path"] = ApPatchPath;

    protected override void OnLoadingSettings(JsonElement root)
    {
        if (root.TryGetProperty("ap_patch_path", out var el)) ApPatchPath = el.GetString();
    }

    /// Set an explicit patch override (rare for FF1) and persist it.
    public void SetExplicitPatch(string path) { ApPatchPath = path; SaveSettings(); }

    /// ROM safety net. FF1 has no patch carrying an expected MD5, so the only
    /// requirement we can surface is "an FF1 ROM is present" — the player supplies
    /// the randomized .nes themselves. A future explicit .apff1 override (which the
    /// AP generator does not emit) would still demand its base MD5 if one is set.
    public override RomRequirement? GetUnmetRomRequirement()
    {
        var patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out _);
        string? wantMd5 = patch != null
            ? SnesApPatchHelper.ReadManifestField(patch, "base_checksum") : null;
        bool haveRom = RomPath != null && File.Exists(RomPath);

        if (wantMd5 == null)
            return haveRom ? null
                 : new RomRequirement("Final Fantasy", "NES",
                       "your Archipelago-randomized Final Fantasy (USA) ROM " +
                       "(generate it at finalfantasyrandomizer.com with the AP flag)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Final Fantasy", "NES",
            "Final Fantasy (USA) — the exact ROM this patch was generated against",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM ───────────────────────────────────────────────────────────
    //
    // The normal FF1 path applies NO patch — the player's library ROM IS the
    // already-randomized .nes, so we return null and the base class launches it
    // directly. Only an explicit .apff1 override (not emitted by the AP generator)
    // would trigger SnesApPatchHelper; that branch is kept for parity with the
    // other emulated games but is inert for standard FF1 seeds.

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            // Standard FF1: no AP patch container exists. The library ROM is the
            // randomized .nes the player downloaded from finalfantasyrandomizer.com.
            SessionRomNote =
                "[Final Fantasy] Final Fantasy is randomized on " +
                "finalfantasyrandomizer.com (not by Archipelago) — launching your ROM " +
                "directly. Make sure this ROM is the randomized .nes generated with " +
                "the Archipelago flag enabled, not the vanilla cartridge dump.";
            return null;
        }

        // Optional explicit-override path (kept for parity; FF1 never emits .apff1).
        var res = await SnesApPatchHelper.ApplyAsync(
            "Final Fantasy", patch, how, RomPath!,
            RomLibraryDirectory, ".nes", ct);
        SessionRomNote = res.Note;
        if (res.OutRom != null) RegisterSeed(res.OutRom, patch);
        return res.OutRom;
    }

    /// Record a produced/reused patched ROM in the seed library. Never throws.
    /// (Only reached on the optional explicit-override path — standard FF1 seeds
    /// launch the randomized ROM directly and never get here.)
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
