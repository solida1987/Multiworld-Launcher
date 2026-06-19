using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// CastlevaniaCotMPlugin — Archipelago integration for Castlevania: Circle of the
// Moon (GBA), on the proven BizHawk Lua-pipe bridge.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED (the 125-location
// table in cvcotm.lua was GENERATED from worlds/cvcotm/locations.py, bit math +
// gate + AP signature verified against client.py; mock-verified through MoonSharp
// against synthetic memory). items_handling = 0b001 — the PATCHED GAME grants its
// own locally-found items, so a SOLO seed plays fully and every check is reported
// in a multiworld. Delivering REMOTE multiworld items is the client's intricate
// text-box-injection path; it is deferred (cvcotm.lua receive_item is a no-op)
// until it can be confirmed in-emulator, rather than shipped unverified.
//
// AP PATCH (.apcvcotm → .gba): a standard APProcedurePatch container, applied via
// the shared SnesApPatchHelper (resolve the seed's patch, validate the base ROM
// MD5, run apply_appatch.py on a library COPY — the original is never modified).
// Base ROM: the user's own CotM (US) cartridge dump or Advance Collection ROM
// (4 MB), identified by CONTENT (size + MD5) — never by filename (§11).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CastlevaniaCotMPlugin : EmulatorPlugin
{
    public CastlevaniaCotMPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "cvcotm";
    public override string DisplayName => "Castlevania: Circle of the Moon";
    public override string Subtitle    => "GBA · Emulated";
    public override string ApWorldName => "Castlevania - Circle of the Moon";

    public override string Description =>
        "Castlevania: Circle of the Moon is the GBA launch-title metroidvania. In " +
        "the Archipelago randomizer, every relic, DSS card, and stat-up across the " +
        "castle joins the multiworld pool. Defeat Dracula to complete your goal.";

    public override string ThemeAccentColor => "#7A1A1A";   // Castlevania crimson

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "GBA";

    // §14: GBA emulators — BizHawk verified; mGBA/Mesen are the alternatives
    // (shown per their bridge state in the dropdown).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "mgba", "mesen" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "cvcotm";

    /// cvcotm.lua reports checks + goal (table generated from the apworld; mock-
    /// verified). Remote-item delivery is the documented deferred piece.
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dumps the randomizer is built for, by CONTENT
    /// (4 MB + MD5) — never by filename. Both the original GBA cartridge and the
    /// Advance Collection ROM are accepted; the patch base_checksum validates the
    /// exact dump before anything is written.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(4 * 1024 * 1024, "50a1089600603a94e15ecf287f8d5a1f",
                        "Castlevania: Circle of the Moon (USA) cartridge"),
        new RomIdentity(4 * 1024 * 1024, "87a1bd6577b6702f97a60fc55772ad74",
                        "Castlevania: Circle of the Moon (Advance Collection)"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    private const string PatchExt = ".apcvcotm";

    /// Explicit .apcvcotm chosen by the user (Settings / drag-drop). Null = auto.
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
                 : new RomRequirement("Castlevania: Circle of the Moon", "GBA",
                       "Castlevania: Circle of the Moon (USA)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Castlevania: Circle of the Moon", "GBA",
            "Castlevania: Circle of the Moon (USA) — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apcvcotm to a library copy ────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Castlevania: Circle of the Moon] No AP patch (.apcvcotm) found — " +
                "launching the vanilla ROM. Generate the multiworld, then pick the " +
                "patch in Settings (or drop it under " +
                SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Castlevania: Circle of the Moon", patch, how, RomPath!,
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
