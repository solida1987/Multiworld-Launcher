using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// CrystalisPlugin — Archipelago integration for Crystalis (NES, 1990), on the
// proven BizHawk Lua-pipe bridge.
//
// COMMUNITY WORLD. The apworld is from Ars-Ignis/Archipelago, a fork of
// ArchipelagoMW/Archipelago. game string: "Crystalis".
// SOURCE: https://github.com/Ars-Ignis/Archipelago
// NOT in AP-main.
//
// STATUS: STUB — ChecksImplemented = false. A full parse of the Ars-Ignis
// fork's Crystalis world source (Locations.py, Client.py, Rom.py) is required
// to extract NES-RAM addresses, the location flag table, goal condition, and
// correct patch suffix before a real Lua module can be authored.
//
// WHAT NEEDS A SECOND PASS:
//   1. Parse worlds/crystalis/Locations.py for the full location table and
//      NES RAM addresses used by the community BizHawk client.
//   2. Identify the patch container type and patch suffix from Rom.py.
//   3. Confirm the Crystalis (USA) vanilla ROM MD5 and byte size.
//   4. Implement "crystalis.lua" with the real flag table and goal logic;
//      flip ChecksImplemented to true.
//
// NES NOTE: BizHawk uses its NesHawk / QuickNes cores for NES. Addresses live
// in the "RAM" memory domain. NES is 8-bit/little-endian; single-byte reads
// are sufficient for flag checks.
//
// AP PATCH: assumed APDeltaPatch or APProcedurePatch container, applied via
// the shared SnesApPatchHelper. Suffix ".apcrystalis" is a placeholder —
// confirm from worlds/crystalis/Rom.py patch_file_ending.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CrystalisPlugin : EmulatorPlugin
{
    public CrystalisPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "crystalis";
    public override string DisplayName => "Crystalis";
    public override string Subtitle    => "NES · Emulated";
    public override string ApWorldName => "Crystalis";

    public override string Description =>
        "Crystalis is SNK's 1990 NES action RPG. The world has been consumed by " +
        "nuclear war, and you awaken from a century of cryogenic sleep as a warrior " +
        "who must forge four elemental swords, challenge the four elemental sages, " +
        "and defeat the sorcerer Draygon in his tower in the sky. In the Archipelago " +
        "randomizer the swords, armors, key items and dungeon progression checks are " +
        "shuffled into the multiworld pool. Bring your own Crystalis (USA) NES ROM.";

    public override string ThemeAccentColor => "#5B3A8A";   // Crystalis sword violet

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "NES";

    // BizHawk is the verified target for NES AP community clients.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "crystalis";

    /// STUB: location detection is not yet implemented. A second-pass deep
    /// dive of the Ars-Ignis/Archipelago Crystalis world source is required.
    /// See header notes.
    public override bool ChecksImplemented => false;

    /// The vanilla Crystalis (USA) NES cartridge dump, identified by CONTENT
    /// (size + MD5) — never by filename (§11). MD5 and exact size must be
    /// confirmed from worlds/crystalis/Rom.py; the values below are
    /// placeholders and must be verified before a production build.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        // NOTE: size and MD5 are placeholder — confirm from Rom.py base hash.
        new RomIdentity(262_160, null,
                        "Crystalis (USA) — NES cartridge dump"),
    };

    // ── AP patch ──────────────────────────────────────────────────────────────

    // Patch suffix placeholder ".apcrystalis" — confirm from worlds/crystalis/Rom.py
    private const string PatchExt  = ".apcrystalis";
    private const string ResultExt = ".nes";

    /// Explicit patch chosen by the user (Settings / drag-drop). Null = auto.
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

    /// ROM safety net: demand the exact vanilla ROM when the patch's
    /// base_checksum is known, or any ROM when it is not.
    public override RomRequirement? GetUnmetRomRequirement()
    {
        var patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out _);
        string? wantMd5 = patch != null
            ? SnesApPatchHelper.ReadManifestField(patch, "base_checksum") : null;
        bool haveRom = RomPath != null && File.Exists(RomPath);

        if (wantMd5 == null)
            return haveRom ? null
                 : new RomRequirement("Crystalis", "NES",
                       "Crystalis (USA) — your original NES cartridge dump",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Crystalis", "NES",
            "Crystalis (USA) — the exact ROM this patch was generated against",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the patch to a library copy ────────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Crystalis] No AP patch (" + PatchExt + ") found — launching the " +
                "vanilla ROM. Checks CANNOT WORK on a vanilla ROM: generate the " +
                "multiworld with the Crystalis apworld, then pick the patch in " +
                "Settings (or drop it under " +
                SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        if (RomPath == null || !File.Exists(RomPath))
        {
            SessionRomNote =
                "[Crystalis] No base ROM set — import your vanilla Crystalis (USA) " +
                "NES cartridge dump in Settings, then relaunch.";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Crystalis", patch, how, RomPath!,
            RomLibraryDirectory, ResultExt, ct);
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
