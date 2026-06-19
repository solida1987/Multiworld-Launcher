using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// GauntletLegendsPlugin — Archipelago integration for Gauntlet Legends
// (Nintendo 64, 2000), on the proven BizHawk Lua-pipe bridge (N64 core).
//
// COMMUNITY WORLD. The apworld is jamesbrq/GauntletLegendsAP, a standalone
// community apworld. game string: "Gauntlet Legends".
// SOURCE: https://github.com/jamesbrq/GauntletLegendsAP
// NOT in AP-main.
//
// STATUS: STUB — ChecksImplemented = false. A full parse of the
// jamesbrq/GauntletLegendsAP source (Locations.py, Client.py, Rom.py) is
// required to extract RDRAM addresses, the location flag table, goal condition,
// and the correct patch suffix before a real Lua module can be authored.
//
// WHAT NEEDS A SECOND PASS:
//   1. Parse worlds/gauntlet_legends/Locations.py (or equivalent) for the full
//      location table and RDRAM addresses used by the community BizHawk client.
//   2. Identify the patch container type and patch suffix from Rom.py.
//   3. Confirm the Gauntlet Legends (USA) vanilla ROM MD5 and byte size.
//   4. Implement "gauntlet_legends.lua" with the real flag table and goal
//      logic; flip ChecksImplemented to true.
//
// N64 NOTE: N64 is BIG-ENDIAN. Memory reads in the Lua module must use
// big-endian byte order from BizHawk's "RDRAM" and "ROM" domains (same
// pattern as dk64.lua / mk64.lua / paper_mario.lua — see their headers).
// N64 runs on BizHawk here only.
//
// AP PATCH: assumed APDeltaPatch or APProcedurePatch container, applied via
// the shared SnesApPatchHelper. Suffix ".apgl" is a placeholder — confirm
// from worlds/gauntlet_legends/Rom.py patch_file_ending.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class GauntletLegendsPlugin : EmulatorPlugin
{
    public GauntletLegendsPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "gauntlet_legends";
    public override string DisplayName => "Gauntlet Legends";
    public override string Subtitle    => "N64 · Emulated";
    public override string ApWorldName => "Gauntlet Legends";

    public override string Description =>
        "Gauntlet Legends is Atari's 2000 Nintendo 64 action RPG, a sequel to the " +
        "arcade original. Up to four players choose a warrior, valkyrie, wizard or " +
        "elf and battle through four kingdoms — Mountain, Castle, Desert and Ice — " +
        "collecting runestones and battling bosses to confront the demon Skorne at " +
        "the end of his lair. In the Archipelago randomizer the runestones, relics, " +
        "potions and boss keys are shuffled into the multiworld pool. Bring your own " +
        "Gauntlet Legends (USA) Nintendo 64 ROM.";

    public override string ThemeAccentColor => "#B8860B";   // Gauntlet gold

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "N64";

    // N64 runs on BizHawk here only. snes9x/mGBA/Mesen are SNES/GBA/NES
    // emulators — not N64 — so they are deliberately NOT offered.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "gauntlet_legends";

    /// STUB: location detection is not yet implemented. A second-pass deep
    /// dive of the jamesbrq/GauntletLegendsAP source is required to generate
    /// the real RDRAM flag table and goal logic. See header notes.
    public override bool ChecksImplemented => false;

    /// The vanilla Gauntlet Legends (USA) cartridge dump, identified by
    /// CONTENT (size + MD5) — never by filename (§11). MD5 and exact size must
    /// be confirmed from the jamesbrq apworld's Rom.py; the values below are
    /// placeholders and must be verified before a production build.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        // NOTE: size and MD5 are placeholder — confirm from Rom.py base hash.
        new RomIdentity(16 * 1024 * 1024,   // 16,777,216 bytes (16 MiB) — typical N64
                        null,
                        "Gauntlet Legends (USA) — vanilla .z64 cartridge dump"),
    };

    // ── AP patch ──────────────────────────────────────────────────────────────

    // Patch suffix placeholder ".apgl" — confirm from worlds/gauntlet_legends/Rom.py
    private const string PatchExt  = ".apgl";
    private const string ResultExt = ".z64";

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
                 : new RomRequirement("Gauntlet Legends", "N64",
                       "Gauntlet Legends (USA) — your original cartridge dump (.z64)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Gauntlet Legends", "N64",
            "Gauntlet Legends (USA) — the exact ROM this patch was generated against",
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
                "[Gauntlet Legends] No AP patch (" + PatchExt + ") found — launching " +
                "the vanilla ROM. Checks CANNOT WORK on a vanilla ROM: generate the " +
                "multiworld with the Gauntlet Legends apworld, then pick the patch in " +
                "Settings (or drop it under " +
                SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        if (RomPath == null || !File.Exists(RomPath))
        {
            SessionRomNote =
                "[Gauntlet Legends] No base ROM set — import your vanilla Gauntlet " +
                "Legends (USA) cartridge dump in Settings, then relaunch.";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Gauntlet Legends", patch, how, RomPath!,
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
