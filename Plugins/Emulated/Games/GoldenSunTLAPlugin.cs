using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// GoldenSunTLAPlugin — Archipelago integration for Golden Sun: The Lost Age
// (GBA), on the proven BizHawk Lua-pipe bridge.
//
// COMMUNITY WORLD. Source: cjmang/Archipelago, branch `gstla` (game string
// "Golden Sun The Lost Age", world id `gstla`, world_version 0.2.3, minimum AP
// 0.6.7). apworld: github.com/cjmang/Archipelago/releases (gstla.apworld).
//
// STATUS: location DETECTION for the 319 standard flag-checked locations
// (Item / Psyenergy / Hidden / Trade / Character) and the default GOAL (Doom
// Dragon defeated) are REAL and SOURCE-DERIVED — the flag→ap_id table in
// gstla.lua was GENERATED with Python directly from gen/InternalLocationData.py
// (every location's `flag` + `ap_id`), the bit math + in-game gate + ROM
// identity verified against BizClient.py, and mock-verified through MoonSharp
// against synthetic memory. Djinn (72, ROM-remap possession) and the
// event/alternate-goal flags are faithful but GATED in the Lua until confirmed
// in-emulator. items_handling = 0b111, but the PATCHED GAME consumes the remote
// item stream itself via an EWRAM save-data handshake (AP item slot 0xA96 /
// received counter 0xA72 with co-op filtering); replicating those guarded writes
// is deferred (gstla.lua receive_item is a no-op) rather than shipped unverified.
// A SOLO seed plays fully and every standard check is reported in a multiworld.
//
// AP PATCH (.apgstla → .gba): a standard APProcedurePatch container, applied via
// the shared SnesApPatchHelper (resolve the seed's patch, validate the base ROM
// MD5, run apply_appatch.py on a library COPY — the original is never modified).
// NOTE on patch generation: the gstla generator's UPS patch is produced by the
// gs2randomiser.com website at SEED GENERATION time and embedded in the
// .apgstla token data, so applying the container locally needs no network — the
// same apply_appatch.py path every other ROM world uses.
// Base ROM: the user's own Golden Sun: The Lost Age (UE) cartridge dump (16 MB),
// identified by CONTENT (size + MD5) — never by filename (§11).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class GoldenSunTLAPlugin : EmulatorPlugin
{
    public GoldenSunTLAPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "gstla";
    public override string DisplayName => "Golden Sun: The Lost Age";
    public override string Subtitle    => "GBA · Emulated";
    public override string ApWorldName => "Golden Sun The Lost Age";

    public override string Description =>
        "Golden Sun: The Lost Age is the GBA sequel JRPG — Felix and crew cross " +
        "Weyard to light the Elemental Lighthouses. In the Archipelago randomizer, " +
        "treasures, Psynergy items, Djinn, and Summon tablets across the world join " +
        "the multiworld pool. Defeat the Doom Dragon to complete your goal (other " +
        "bosses and superbosses can be configured as alternate goals).";

    public override string ThemeAccentColor => "#C8A21E";   // Golden Sun gold

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "GBA";

    // §14: GBA emulators — BizHawk verified; mGBA/Mesen are the alternatives
    // (shown per their bridge state in the dropdown).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "mgba", "mesen" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "gstla";

    /// gstla.lua reports the 319 standard checks + the default Doom Dragon goal
    /// (table generated from the apworld; mock-verified). Djinn + remote-item
    /// delivery are the documented gated/deferred pieces.
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dump the randomizer is built for, by CONTENT
    /// (16 MB + MD5) — never by filename. The patch base_checksum validates the
    /// exact dump before anything is written; the MD5 here is the same one
    /// Rom.py (CHECKSUM_GSTLA) demands for the UE ROM.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(16 * 1024 * 1024, "8efe8b2aaed97149e897570cd123ff6e",
                        "Golden Sun: The Lost Age (USA, Europe)"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    private const string PatchExt = ".apgstla";

    /// Explicit .apgstla chosen by the user (Settings / drag-drop). Null = auto.
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
                 : new RomRequirement("Golden Sun: The Lost Age", "GBA",
                       "Golden Sun: The Lost Age (USA, Europe)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Golden Sun: The Lost Age", "GBA",
            "Golden Sun: The Lost Age (USA, Europe) — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apgstla to a library copy ─────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Golden Sun: The Lost Age] No AP patch (.apgstla) found — " +
                "launching the vanilla ROM. Generate the multiworld, then pick the " +
                "patch in Settings (or drop it under " +
                SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Golden Sun: The Lost Age", patch, how, RomPath!,
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
