using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// CastlevaniaDoSPlugin — Archipelago integration for Castlevania: Dawn of
// Sorrow (NDS), on the proven BizHawk Lua-pipe bridge (NDS core).
//
// SOURCE: community apworld by PinkSwitch (github.com/PinkSwitch/Archipelago,
// branch "dawn-of-sorrow", latest tag cvdos2.0.4). Confirmed active on the
// Archipelago wiki (archipelago.miraheze.org/wiki/Castlevania:_Dawn_of_Sorrow).
//
// STATUS: location DETECTION + goal are SOURCE-DERIVED from worlds/cv_dos
// (DoSClient.py, Locations.py, static_location_data.py). Remote-item delivery
// is deferred (cv_dos.lua receive_item is a no-op) until it can be confirmed
// in-emulator, rather than shipped unverified.
//
// NDS NOTE: NDS is LITTLE-ENDIAN. The DoSClient reads two BizHawk NDS memory
// domains — "ROM" (header identity: game-id bytes 0x00–0x11, patch-version
// string at 0x02F6DD7C) and "Main RAM" (save struct + soul/location flags).
// cv_dos.lua replicates the client's read layout exactly.
//
// AP PATCH (.apcvdos → .nds): a standard APProcedurePatch container, applied
// via the shared SnesApPatchHelper (resolve the seed's patch, validate the
// base ROM MD5, run apply_appatch.py on a library COPY — the original is never
// modified). Base ROM: the user's own Castlevania: Dawn of Sorrow (USA) NDS
// cartridge dump, identified by CONTENT (size + MD5 cc0f25b8…) — never by
// filename (§11). The header reads "CASTLEVANIA1ACVEA4" to distinguish DoS
// from Portrait of Ruin ("CASTLEVANIA2ACBEA4") unambiguously.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CastlevaniaDoSPlugin : EmulatorPlugin
{
    public CastlevaniaDoSPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "cv_dos";
    public override string DisplayName => "Castlevania: Dawn of Sorrow";
    public override string Subtitle    => "NDS · Emulated";

    // Exact AP world game string from worlds/cv_dos/__init__.py ("game") and
    // DoSClient.game — used for DataPackage lookup and slot matching.
    public override string ApWorldName => "Castlevania: Dawn of Sorrow";

    public override string Description =>
        "Castlevania: Dawn of Sorrow is the direct sequel to Aria of Sorrow on " +
        "the Nintendo DS. As Soma Cruz, master the souls of defeated monsters and " +
        "stop a cult from creating a new Dark Lord. In the Archipelago randomizer, " +
        "items and souls scattered across the castle join the multiworld pool — " +
        "defeat the Dark Lord Candidates to complete your goal.";

    public override string ThemeAccentColor => "#6B1A2A";   // DoS crimson-dark

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "NDS";

    // §14: DoSClient is a BizHawkClient — BizHawk is the only bridge-ready
    // NDS backend today. MelonDS / DeSmuME are not yet bridge-ready for AP.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "cv_dos";

    /// cv_dos.lua reports checks + goal (tables generated from the apworld).
    /// Remote-item delivery is the documented deferred piece.
    public override bool ChecksImplemented => true;

    /// The vanilla NDS cartridge dump the randomizer is built for, by CONTENT
    /// (size + MD5) — never by filename. MD5 sourced from DoSSettings.RomFile
    /// in worlds/cv_dos/__init__.py (CASTLEVANIA1_ACVEA4_00.nds).
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        // 64 MB — standard NDS card size for DoS. MD5 sourced from
        // worlds/cv_dos/__init__.py (DoSSettings.RomFile.md5).
        new RomIdentity(67108864L, "cc0f25b8783fb83cb4588d1c111bdc18",
                        "Castlevania: Dawn of Sorrow (USA)"),
    };

    // ── AP patch ──────────────────────────────────────────────────────────────

    private const string PatchExt = ".apcvdos";

    /// Explicit .apcvdos chosen by the user (Settings / drag-drop). Null = auto.
    public string? ApPatchPath { get; private set; }

    protected override void OnSavingSettings(IDictionary<string, object?> bag)
        => bag["ap_patch_path"] = ApPatchPath;

    protected override void OnLoadingSettings(JsonElement root)
    {
        if (root.TryGetProperty("ap_patch_path", out var el))
            ApPatchPath = el.GetString();
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
                 : new RomRequirement(
                       "Castlevania: Dawn of Sorrow", "NDS",
                       "Castlevania: Dawn of Sorrow (USA)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement(
            "Castlevania: Dawn of Sorrow", "NDS",
            "Castlevania: Dawn of Sorrow (USA) — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apcvdos to a library copy ────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Castlevania: Dawn of Sorrow] No AP patch (.apcvdos) found — " +
                "launching the vanilla ROM. Generate the multiworld, then pick the " +
                "patch in Settings (or drop it under " +
                SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Castlevania: Dawn of Sorrow", patch, how, RomPath!,
            RomLibraryDirectory, ".nds", ct);
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
