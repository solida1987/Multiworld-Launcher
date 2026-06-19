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
// CastlevaniaHoDPlugin — Archipelago integration for Castlevania: Harmony of
// Dissonance (GBA), on the proven BizHawk Lua-pipe bridge.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED. The check table
// in cvhod.lua is generated from worlds/cvhod (LiquidCatipelago community apworld,
// confirmed active on the Archipelago wiki). Remote-item delivery is deferred
// (cvhod.lua receive_item is a no-op) until it can be confirmed in-emulator,
// rather than shipped unverified.
//
// AP PATCH (.apchod → .gba): a standard APProcedurePatch container, applied via
// the shared SnesApPatchHelper (resolve the seed's patch, validate the base ROM
// MD5, run apply_appatch.py on a library COPY — the original is never modified).
// Base ROM: the user's own Harmony of Dissonance (USA) cartridge dump or the
// Advance Collection ROM (8 MB), identified by CONTENT (size + MD5) — never by
// filename (§11).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CastlevaniaHoDPlugin : EmulatorPlugin
{
    public CastlevaniaHoDPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "cvhod";
    public override string DisplayName => "Castlevania: Harmony of Dissonance";
    public override string Subtitle    => "GBA · Emulated";

    // Exact AP world name from the Archipelago wiki page title and the community
    // apworld — mirrors the CotM naming convention.
    public override string ApWorldName => "Castlevania - Harmony of Dissonance";

    public override string Description =>
        "Castlevania: Harmony of Dissonance is the second GBA entry in the series, " +
        "following Juste Belmont through a dual-castle metroidvania. In the " +
        "Archipelago randomizer, every relic, subweapon, and stat-up across both " +
        "castle halves joins the multiworld pool. Defeat Dracula to complete your goal.";

    public override string ThemeAccentColor => "#7A1A1A";   // Castlevania crimson

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "GBA";

    // §14: GBA emulators — BizHawk verified; mGBA/Mesen are the alternatives
    // (shown per their bridge state in the dropdown).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "mgba", "mesen" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "cvhod";

    /// cvhod.lua reports checks + goal (table generated from the apworld).
    /// Remote-item delivery is the documented deferred piece.
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dumps the randomizer is built for, by CONTENT
    /// (8 MB + MD5) — never by filename. Both the original GBA cartridge and the
    /// Advance Collection ROM are accepted; the patch base_checksum validates the
    /// exact dump before anything is written.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(8 * 1024 * 1024, "afe5a3d1a7aa2a197e59a06f2c97e9d0",
                        "Castlevania: Harmony of Dissonance (USA) cartridge"),
        new RomIdentity(8 * 1024 * 1024, null,
                        "Castlevania: Harmony of Dissonance (Advance Collection)"),
    };

    // ── AP patch ──────────────────────────────────────────────────────────────

    private const string PatchExt = ".apchod";

    /// Explicit .apchod chosen by the user (Settings / drag-drop). Null = auto.
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
                 : new RomRequirement("Castlevania: Harmony of Dissonance", "GBA",
                       "Castlevania: Harmony of Dissonance (USA)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Castlevania: Harmony of Dissonance", "GBA",
            "Castlevania: Harmony of Dissonance (USA) — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apchod to a library copy ─────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Castlevania: Harmony of Dissonance] No AP patch (.apchod) found — " +
                "launching the vanilla ROM. Generate the multiworld, then pick the " +
                "patch in Settings (or drop it under " +
                SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Castlevania: Harmony of Dissonance", patch, how, RomPath!,
            RomLibraryDirectory, ".gba", ct);
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
