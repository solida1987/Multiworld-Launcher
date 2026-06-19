using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// MinishCapPlugin — Archipelago integration for The Legend of Zelda: The Minish
// Cap (GBA), on the proven BizHawk Lua-pipe bridge.
//
// COMMUNITY APWORLD — this game is NOT in ArchipelagoMW/Archipelago main.
//   Source : https://github.com/eternalcode0/TMC-APWorld   (world_version 0.3.1)
//   World id ("game" in tmc/archipelago.json + MinishCapClient.game): "The Minish Cap"
//   Authors: EternalCode, Myth197
//
// STATUS: location DETECTION + goal + item delivery are REAL and SOURCE-DERIVED.
// The 511-entry LOC table in "The Minish Cap.lua" was GENERATED from
// tmc/locations.py (`all_locations`, every entry with a readable EWRAM ram_addr)
// and the check / goal / receive-loop math mirrors MinishCapClient.game_watcher
// byte-for-byte. Mock-verified through MoonSharp against synthetic EWRAM/IWRAM/ROM
// buffers (flag-mask detection, in-gameplay gate, goal trigger, item delivery).
//
// items_handling = 0b101 (remote items + starting inventory). The Minish Cap is
// NOT a self-granting ROM for remote items: the client hands the patched game each
// received item via the EWRAM mailbox 0x3FF10 (guarded on the mailbox being [0,0]
// AND a "player safe" byte at 0x2A4A) and advances the counter at 0x2A44 — the Lua
// module replicates that exact guarded-write handshake, GATED behind the AP-ROM
// identity AND the in-gameplay state so it can never write into a foreign or
// unpatched cartridge. (15 Goron Merchant + 100 Kinstone Fusion locations use the
// client's bit-counting / data-store paths instead of a readable flag; their ids
// are documented as deferred and filtered out so they are never falsely reported.)
//
// AP PATCH (.aptmc → .gba): MinishCapProcedurePatch is a standard APProcedurePatch
// container (base_patch.bsdiff4 + token_data.bin), applied via the shared
// SnesApPatchHelper — resolve the seed's patch, validate the base ROM MD5 (the
// manifest base_checksum), run apply_appatch.py on a library COPY (the original is
// never modified). Base ROM: the user's own Minish Cap (USA) cartridge dump
// (16 MB, MD5 2af78edbe244b5de44471368ae2b6f0b — the patch `hash`), identified by
// CONTENT (size + MD5), never by filename (§11).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MinishCapPlugin : EmulatorPlugin
{
    public MinishCapPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "the_minish_cap";
    public override string DisplayName => "The Legend of Zelda: The Minish Cap";
    public override string Subtitle    => "GBA · Emulated";

    // Exact AP world id ("game" string in tmc/archipelago.json + the
    // MinishCapClient.game / MinishCapWorld definition).
    public override string ApWorldName => "The Minish Cap";

    public override string Description =>
        "The Legend of Zelda: The Minish Cap is the GBA action-adventure where " +
        "Link shrinks to Minish size to explore Hyrule. In the Archipelago " +
        "randomizer, chests, heart pieces, scrolls, dig spots, dungeon prizes and " +
        "key items across the overworld and dungeons join the multiworld pool. " +
        "Defeat Vaati to complete your goal.";

    public override string ThemeAccentColor => "#2E9B57";   // Minish forest green

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "GBA";

    // §14: GBA emulators — BizHawk verified; mGBA/Mesen are the alternatives
    // (shown per their bridge state in the dropdown).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "mgba", "mesen" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";

    // The per-game Lua module is loaded as games/<LuaModuleName>.lua — the world
    // id carries spaces, and the connector's dofile() resolves a spaced path fine.
    protected override string LuaModuleName => "The Minish Cap";

    /// "The Minish Cap.lua" reports checks + goal and delivers items (table
    /// generated from the apworld; mock-verified). Live in-emulator confirmation
    /// is the remaining gate, but every memory write is guarded by the AP-ROM
    /// identity and the in-gameplay state, so it is safe to enable.
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dump the randomizer is built for, by CONTENT
    /// (16 MB + MD5) — never by filename. The MD5 is the patch `hash`
    /// (MinishCapProcedurePatch.hash); the patch base_checksum validates the exact
    /// dump before anything is written.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(16 * 1024 * 1024, "2af78edbe244b5de44471368ae2b6f0b",
                        "The Legend of Zelda: The Minish Cap (USA)"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    private const string PatchExt = ".aptmc";

    /// Explicit .aptmc chosen by the user (Settings / drag-drop). Null = auto.
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
                 : new RomRequirement("The Legend of Zelda: The Minish Cap", "GBA",
                       "The Legend of Zelda: The Minish Cap (USA)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("The Legend of Zelda: The Minish Cap", "GBA",
            "The Legend of Zelda: The Minish Cap (USA) — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .aptmc to a library copy ───────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[The Legend of Zelda: The Minish Cap] No AP patch (.aptmc) found — " +
                "launching the vanilla ROM. Generate the multiworld, then pick the " +
                "patch in Settings (or drop it under " +
                SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "The Legend of Zelda: The Minish Cap", patch, how, RomPath!,
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
