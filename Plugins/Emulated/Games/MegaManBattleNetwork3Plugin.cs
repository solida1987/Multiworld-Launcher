using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// MegaManBattleNetwork3Plugin — Archipelago integration for Mega Man Battle
// Network 3 — Blue Version (GBA), on the proven BizHawk Lua-pipe bridge.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED (the 263-location
// table in mmbn3.lua was GENERATED from worlds/mmbn3/Locations.py; the check +
// goal math mirror the shipped connector_mmbn3.lua + MMBN3Client.py byte-for-byte;
// mock-verified through MoonSharp against synthetic GBA memory). items_handling =
// 0b101 — remote items ARE part of this game, but the connector's SendItemToGame
// delivery path is intricate and save-mutating (per-item-type RAM array writes
// gated behind an "is itemable?" state machine + a WRAM received-index handshake);
// it is deferred (mmbn3.lua receive_item is a no-op) until it can be confirmed
// in-emulator, rather than shipped unverified. Checks + goal work fully.
//
// VERSION SUPPORT: the AP world (worlds/mmbn3/Rom.py) accepts ONLY the US Blue
// Version (single MD5 6fe31df0…; the bn3-ap-patch bsdiff is built against Blue).
// White Version is NOT supported by this apworld, so it is not offered here.
//
// AP PATCH (.apbn3 → .gba): an APDeltaPatch container (ZIP holding a bsdiff4
// delta), applied via the shared SnesApPatchHelper (resolve the seed's patch,
// validate the base ROM MD5, run apply_appatch.py on a library COPY — the
// original is never modified). Base ROM: the user's own MMBN3 Blue (US) cartridge
// dump (8 MB), identified by CONTENT (size + MD5) — never by filename (§11).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MegaManBattleNetwork3Plugin : EmulatorPlugin
{
    public MegaManBattleNetwork3Plugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "mmbn3";
    public override string DisplayName => "Mega Man Battle Network 3";
    public override string Subtitle    => "GBA · Emulated";
    public override string ApWorldName => "MegaMan Battle Network 3";

    public override string Description =>
        "Play as Lan and MegaMan to stop the evil organization WWW and Dr. Wily " +
        "from taking over the Net. In the Archipelago randomizer, BattleChips, Navi " +
        "Customizer programs, Sub Chips, key items and Undernet ranks across ACDC, " +
        "SciLab, Yoka, Beach and the Undernet join the multiworld pool. Defeat Alpha " +
        "to complete your goal. (US Blue Version.)";

    public override string ThemeAccentColor => "#1E63C8";   // MegaMan / Blue Version blue

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "GBA";

    // §14: GBA emulators — BizHawk verified (the connector targets BizHawk 2.7+);
    // mGBA/Mesen are the alternatives (shown per their bridge state in the dropdown).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "mgba", "mesen" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "mmbn3";

    /// mmbn3.lua reports checks + goal (table generated from the apworld; mock-
    /// verified). Remote-item delivery is the documented deferred piece.
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dump the randomizer is built for, by CONTENT
    /// (8 MB + MD5) — never by filename. Only the US Blue Version is accepted
    /// (the apworld's sole CHECKSUM_BLUE); the patch base_checksum validates the
    /// exact dump before anything is written.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(8 * 1024 * 1024, "6fe31df0144759b34ad666badaacc442",
                        "Mega Man Battle Network 3 - Blue Version (USA)"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    private const string PatchExt = ".apbn3";

    /// Explicit .apbn3 chosen by the user (Settings / drag-drop). Null = auto.
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
                 : new RomRequirement("Mega Man Battle Network 3", "GBA",
                       "Mega Man Battle Network 3 - Blue Version (USA)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Mega Man Battle Network 3", "GBA",
            "Mega Man Battle Network 3 - Blue Version (USA) — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apbn3 to a library copy ───────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Mega Man Battle Network 3] No AP patch (.apbn3) found — launching the " +
                "vanilla ROM. Generate the multiworld, then pick the patch in Settings " +
                "(or drop it under " + SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Mega Man Battle Network 3", patch, how, RomPath!,
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
