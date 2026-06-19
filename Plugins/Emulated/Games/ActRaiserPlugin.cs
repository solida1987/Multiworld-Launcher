using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// ActRaiserPlugin — Archipelago integration for ActRaiser (SNES), on the
// proven BizHawk Lua-pipe bridge.
//
// COMMUNITY WORLD. The apworld is from Happyhappyism/Archipelago (a fork of
// ArchipelagoMW/Archipelago). AP game string: "ActRaiser" (inferred from repo).
//
// STATUS: STUB — ChecksImplemented = false. The BizHawk Lua module
// ("actraiser.lua") is a no-op stub that identifies the ROM by header signature
// and reports nothing. A second-pass deep dive of the apworld's Client / Locations
// source is required before a real check table and goal logic can be built.
//
// WHAT NEEDS A SECOND PASS:
//   1. Parse the Happyhappyism fork's worlds/actraiser/Client.py + Locations.py
//      to extract SNES memory addresses for location flags and goal condition.
//   2. Identify the patch container type and exact suffix (recorded as ".apactraiser").
//   3. Confirm the ActRaiser (USA) vanilla ROM MD5 and size.
//   4. Implement "actraiser.lua" with real flag reads and goal detection; flip
//      ChecksImplemented to true.
//
// AP PATCH (.apactraiser → .sfc): APProcedurePatch container, applied via the
// shared SnesApPatchHelper. Suffix must be confirmed from the apworld source.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ActRaiserPlugin : EmulatorPlugin
{
    public ActRaiserPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "actraiser";
    public override string DisplayName => "ActRaiser";
    public override string Subtitle    => "SNES · Emulated";
    public override string ApWorldName => "ActRaiser";

    public override string Description =>
        "ActRaiser is Quintet's 1990 SNES classic that blends side-scrolling action " +
        "platformer stages with overhead city-building simulation. As the Realm's " +
        "Master you battle the evil Tanzra's six lieutenants, clearing monster lairs " +
        "to free humanity and raise civilization across six regions. In the " +
        "Archipelago randomizer the game's magic, statues, acts and realm rewards " +
        "join the multiworld pool. Bring your own ActRaiser (USA) SNES ROM.";

    public override string ThemeAccentColor => "#3A5FA0";   // ActRaiser sky blue

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "SNES";

    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "actraiser";

    /// STUB: location check detection not yet implemented. Second pass required.
    public override bool ChecksImplemented => false;

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        // NOTE: MD5 is a placeholder — confirm from worlds/actraiser/Rom.py base hash.
        new RomIdentity(512 * 1024,   // 524,288 bytes (512 KiB — typical SNES ROM)
                        null,
                        "ActRaiser (USA) — vanilla .sfc/.smc cartridge dump"),
    };

    // ── AP patch ──────────────────────────────────────────────────────────────

    private const string PatchExt  = ".apactraiser";
    private const string ResultExt = ".sfc";

    public string? ApPatchPath { get; private set; }

    protected override void OnSavingSettings(IDictionary<string, object?> bag)
        => bag["ap_patch_path"] = ApPatchPath;

    protected override void OnLoadingSettings(JsonElement root)
    {
        if (root.TryGetProperty("ap_patch_path", out var el))
            ApPatchPath = el.GetString();
    }

    public void SetExplicitPatch(string path) { ApPatchPath = path; SaveSettings(); }

    public override RomRequirement? GetUnmetRomRequirement()
    {
        var patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out _);
        string? wantMd5 = patch != null
            ? SnesApPatchHelper.ReadManifestField(patch, "base_checksum") : null;
        bool haveRom = RomPath != null && File.Exists(RomPath);

        if (wantMd5 == null)
            return haveRom ? null
                 : new RomRequirement("ActRaiser", "SNES",
                       "ActRaiser (USA) — .sfc/.smc dump",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("ActRaiser", "SNES",
            "ActRaiser (USA) — your original cartridge dump (.sfc/.smc)",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apactraiser to a library copy ─────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[ActRaiser] No AP patch (.apactraiser) found — launching the vanilla " +
                "ROM. Checks CANNOT WORK on a vanilla ROM: generate the multiworld " +
                "with the ActRaiser apworld, then pick the patch in Settings (or drop " +
                "it under " + SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        if (RomPath == null || !File.Exists(RomPath))
        {
            SessionRomNote =
                "[ActRaiser] No base ROM set — import your vanilla USA cartridge dump " +
                "in Settings, then relaunch.";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "ActRaiser", patch, how, RomPath!,
            RomLibraryDirectory, ResultExt, ct);
        SessionRomNote = res.Note;
        if (res.OutRom != null) RegisterSeed(res.OutRom, patch);
        return res.OutRom;
    }

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
