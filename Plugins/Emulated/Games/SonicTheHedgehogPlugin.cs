using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// SonicTheHedgehogPlugin — Archipelago integration for Sonic the Hedgehog 1
// (Sega Genesis / Mega Drive, 1991), on the proven BizHawk Lua-pipe bridge.
//
// Source world: github.com/kaithar/Archipelago, branch `sonic1`
//   (release sonic_1-v0.1.1). S1Client.system = ("GEN",), game = "Sonic the
//   Hedgehog 1", patch suffix ".aps1", items_handling = 0b111 (full remote).
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED (the 208-location
// table in sonic1.lua — 196 monitors + 6 bosses + 6 special stages — was
// GENERATED from worlds/sonic1/constants.py; the Genesis-SRAM "dead byte"
// de-interleave + byte-order auto-detection + the save-struct decode are
// replicated exactly from sram.py / client.py; mock-verified through MoonSharp
// against synthetic SRAM + 68K-RAM buffers in all four SRAM layouts). The goal
// reads the client's own victory bit (SR_SSGate & 0x40), which the client writes
// the instant it raises CLIENT_GOAL.
//
// REMOTE ITEMS: items_handling = 0b111, so the SERVER delivers every item and the
// real client applies them by WRITING the SRAM save struct (guarded
// accumulate-and-commit, with the dead-byte re-interleave for the detected SRAM
// layout). Re-implementing those guarded SRAM writes in Lua without in-emulator
// verification would risk corrupting the live battery save, so sonic1.lua's
// receive_item is a documented no-op — detection + goal are fully live; remote
// item DELIVERY is the deferred piece (the same honest split the other emulated
// worlds ship with).
//
// AP PATCH (.aps1 → .md): an APProcedurePatch container, applied via the shared
// SnesApPatchHelper (resolve the seed's patch, run apply_appatch.py on a library
// COPY — the original is never modified). NOTE: the Sonic 1 patch normalises THREE
// accepted base ROMs (Rev0 / Rev1 / GameCube Edition) down to Rev0 internally via
// its own bundled bsdiff4 procedure, so its manifest carries no single
// base_checksum (hash = "Multiple"). We therefore accept all three dumps by
// CONTENT (size + MD5) and let the patch procedure validate the exact dump.
// Base ROM: the user's own Sonic 1 (1991) cartridge dump (512 KB), identified by
// CONTENT — never by filename (§11).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SonicTheHedgehogPlugin : EmulatorPlugin
{
    public SonicTheHedgehogPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "sonic1";
    public override string DisplayName => "Sonic the Hedgehog";
    public override string Subtitle    => "Genesis · Emulated";
    public override string ApWorldName => "Sonic the Hedgehog 1";

    public override string Description =>
        "Sonic the Hedgehog is the 1991 Sega Genesis original. In the Archipelago " +
        "randomizer every item monitor across the six zones joins the multiworld " +
        "pool, with zone keys, Chaos Emeralds, rings and power-ups handed out by " +
        "the server. Beat the bosses, clear the Special Stages and meet your " +
        "configurable victory goal to win.";

    public override string ThemeAccentColor => "#1565C0";   // Sonic cobalt blue

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "GEN";

    // The sonic1 world is a BizHawkClient (system = "GEN"); BizHawk is the
    // verified backend for Genesis here.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "sonic1";

    /// sonic1.lua reports checks + goal (208-entry table generated from the
    /// apworld; mock-verified across all four SRAM layouts). Remote-item delivery
    /// is the documented deferred piece (guarded SRAM writes).
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dumps the randomizer accepts, by CONTENT (512 KB +
    /// MD5) — never by filename. The Sonic 1 patch's own procedure normalises any
    /// of these three down to Rev0 before applying, so all three are valid bases;
    /// the patch validates the exact dump before anything is written.
    ///   Rev0 (USA, Europe)  md5 1bc674be034e43c96b86487ac69d9293  — most common
    ///   Rev1 (Japan, Korea / Steam SONIC_W.68K)  md5 09dadb5071eb35050067a32462e39c5f
    ///   GameCube Edition     md5 c6c15aea60bda10ae11c6bc375296153
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(512 * 1024, "1bc674be034e43c96b86487ac69d9293",
                        "Sonic the Hedgehog (USA, Europe) Rev 0"),
        new RomIdentity(512 * 1024, "09dadb5071eb35050067a32462e39c5f",
                        "Sonic the Hedgehog (Japan, Korea) Rev 1 / Steam SONIC_W.68K"),
        new RomIdentity(512 * 1024, "c6c15aea60bda10ae11c6bc375296153",
                        "Sonic the Hedgehog (GameCube Edition)"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    private const string PatchExt = ".aps1";

    /// Explicit .aps1 chosen by the user (Settings / drag-drop). Null = auto.
    public string? ApPatchPath { get; private set; }

    protected override void OnSavingSettings(IDictionary<string, object?> bag)
        => bag["ap_patch_path"] = ApPatchPath;

    protected override void OnLoadingSettings(JsonElement root)
    {
        if (root.TryGetProperty("ap_patch_path", out var el)) ApPatchPath = el.GetString();
    }

    /// Set the explicit AP patch (drag-and-drop / room-link import) and persist it.
    public void SetExplicitPatch(string path) { ApPatchPath = path; SaveSettings(); }

    /// ROM safety net. The Sonic 1 patch manifest has no single base_checksum
    /// (hash = "Multiple" — its procedure accepts Rev0/Rev1/GCE and normalises
    /// internally), so when a patch is present we can't demand one specific MD5;
    /// any of the three accepted dumps (validated by AcceptableBaseRoms /
    /// ValidateBaseRom on import) is fine, and the patch procedure does the final
    /// exact check. We only surface a requirement when NO ROM is imported.
    public override RomRequirement? GetUnmetRomRequirement()
    {
        var patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out _);
        string? wantMd5 = patch != null
            ? SnesApPatchHelper.ReadManifestField(patch, "base_checksum") : null;
        bool haveRom = RomPath != null && File.Exists(RomPath);

        // No usable per-patch MD5 (the common case for Sonic 1): just require that
        // SOME accepted base ROM is imported.
        if (wantMd5 == null)
            return haveRom ? null
                 : new RomRequirement("Sonic the Hedgehog", "GEN",
                       "Sonic the Hedgehog (1991) — Rev 0, Rev 1, or GameCube Edition",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        // A patch that does carry an exact base_checksum is honoured strictly.
        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Sonic the Hedgehog", "GEN",
            "Sonic the Hedgehog (1991) — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .aps1 to a library copy ────────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Sonic the Hedgehog] No AP patch (.aps1) found — launching the " +
                "vanilla ROM. Generate the multiworld, then pick the patch in " +
                "Settings (or drop it under " +
                SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Sonic the Hedgehog", patch, how, RomPath!,
            RomLibraryDirectory, ".md", ct);
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
