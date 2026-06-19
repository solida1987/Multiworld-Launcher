using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// EarthBoundPlugin — Archipelago integration for EarthBound (SNES), on the proven
// BizHawk Lua-pipe bridge (snes9x/mesen are the experimental SNES alternatives).
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED. The 526-location
// table in earthbound.lua was GENERATED from worlds/earthbound's
// game_data/local_data.py `check_table` (world_version 4.3.1) — two flag regions
// (main world WRAM 0x9C00, shop slots WRAM 0xB721), bit math + the "MOM2AP" ROM
// signature + the GAME_CLEAR goal all verified against Client.py; mock-verified
// through MoonSharp against synthetic memory. REMOTE multiworld item delivery is
// ALSO implemented faithfully (the client's queue-driven path: ITEMQUEUE_HIGH
// counter + standard/money/special receive slots, with the same game-busy gating)
// because EarthBound's money/special remaps are tiny and fully known — needs a
// live playthrough to confirm in-emulator like every other SNES module here.
//
// THE PATCH (.apeb → .sfc) is an APProcedurePatch container (bsdiff4 + tokens),
// the same family as A Link to the Past's .aplttp, applied via the shared
// SnesApPatchHelper (resolve the seed's patch, validate the base ROM MD5, run
// apply_appatch.py on a library COPY — the original is never modified).
// Base ROM: the user's own EarthBound (US 1.0) cartridge dump or the Wii U Virtual
// Console image (3 MB), identified by CONTENT (size + one of two MD5s) — never by
// filename (§11). apply_appatch.py re-checks the patch's own base_checksum too.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class EarthBoundPlugin : EmulatorPlugin
{
    public EarthBoundPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "earthbound";
    public override string DisplayName => "EarthBound";
    public override string Subtitle    => "SNES · Emulated";
    public override string ApWorldName => "EarthBound";

    public override string Description =>
        "EarthBound is the cult-classic SNES RPG following Ness and friends against " +
        "the cosmic horror Giygas. In the Archipelago randomizer, character unlocks, " +
        "PSI, equipment, key items, and shop stock across the world of Eagleland join " +
        "the multiworld pool. Defeat Giygas to complete your goal.";

    public override string ThemeAccentColor => "#E03C31";   // EarthBound red

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "SNES";

    // §14: the SNES emulators this world is played on. BizHawk is verified; snes9x
    // is the headline alternative (the §14 Discord ask was "SNES: BizHawk or
    // snes9x"), with Mesen as a third option — both shown per their bridge state.
    // NOTE: the snes9x NWA bridge maps only WRAM + SRAM, so EarthBound's ROM-header
    // signature gate can't run there yet; BizHawk is the confirmed path.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "snes9x", "mesen" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "earthbound";

    /// earthbound.lua reports checks + goal + delivers remote items (tables
    /// generated from the apworld; mock-verified). Needs a live playthrough to
    /// confirm in-emulator, like the other SNES modules.
    public override bool ChecksImplemented => true;

    /// The vanilla EarthBound dumps the randomizer is built for, by CONTENT
    /// (3 MB + MD5) — never by filename. Both the US 1.0 cartridge dump and the
    /// Wii U Virtual Console image are accepted (the two hashes EarthBound's own
    /// Rom.py `valid_hashes` validates); the patch base_checksum re-checks the
    /// exact dump before anything is written.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(3 * 1024 * 1024, "a864b2e5c141d2dec1c4cbed75a42a85",
                        "EarthBound (USA) cartridge"),
        new RomIdentity(3 * 1024 * 1024, "6d71ccc8e2afda15d011348291afdf4f",
                        "EarthBound (Wii U Virtual Console)"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    private const string PatchExt  = ".apeb";
    private const string ResultExt = ".sfc";

    /// Explicit .apeb chosen by the user (Settings / drag-drop). Null = auto.
    public string? ApPatchPath { get; private set; }

    protected override void OnSavingSettings(IDictionary<string, object?> bag)
        => bag["ap_patch_path"] = ApPatchPath;

    protected override void OnLoadingSettings(JsonElement root)
    {
        if (root.TryGetProperty("ap_patch_path", out var el)) ApPatchPath = el.GetString();
    }

    /// Set the explicit AP patch (drag-and-drop / room-link import) and persist it.
    public void SetExplicitPatch(string path) { ApPatchPath = path; SaveSettings(); }

    /// ROM safety net: the resolved patch tells us the EXACT vanilla ROM MD5 it
    /// needs — demand that specific dump when ours is missing or the wrong one.
    public override RomRequirement? GetUnmetRomRequirement()
    {
        var patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out _);
        string? wantMd5 = patch != null
            ? SnesApPatchHelper.ReadManifestField(patch, "base_checksum") : null;
        bool haveRom = RomPath != null && File.Exists(RomPath);

        if (wantMd5 == null)
            return haveRom ? null
                 : new RomRequirement("EarthBound", "SNES",
                       "EarthBound (USA)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("EarthBound", "SNES",
            "EarthBound (USA) — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apeb to a library copy ────────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[EarthBound] No AP patch (.apeb) found — launching the vanilla ROM. " +
                "Checks and ITEM DELIVERY CANNOT WORK on a vanilla ROM: generate the " +
                "multiworld, then pick the patch in Settings → EarthBound (or drop it " +
                "under " + SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        if (RomPath == null || !File.Exists(RomPath))
        {
            SessionRomNote =
                "[EarthBound] No base ROM set — import your EarthBound (US 1.0) " +
                "cartridge dump in Settings, then relaunch.";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "EarthBound", patch, how, RomPath!, RomLibraryDirectory, ResultExt, ct);
        SessionRomNote = res.Note;
        if (res.OutRom != null) RegisterSeed(res.OutRom, patch);
        return res.OutRom;
    }

    /// Record a produced/reused patched ROM in the seed library. Never throws.
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
