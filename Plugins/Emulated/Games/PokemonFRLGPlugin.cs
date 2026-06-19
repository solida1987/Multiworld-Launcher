using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// PokemonFRLGPlugin — Archipelago integration for Pokémon FireRed & LeafGreen
// (GBA), on the proven BizHawk Lua-pipe bridge. The pokemon_frlg apworld is ONE
// world that covers BOTH cartridges, so this plugin accepts either base ROM
// (FireRed OR LeafGreen, incl. their rev1 dumps) and either patch extension
// (.apfirered / .apleafgreen) — exactly the way PokemonRBPlugin handles Red/Blue.
//
// COMMUNITY APWORLD — this game is NOT in ArchipelagoMW/Archipelago main (the
// worlds/pokemon_frlg path 404s on the main branch as of 2026-06).
//   Source : https://github.com/vyneras/Archipelago/tree/frlg-stable/worlds/pokemon_frlg
//   World id ("game" in pokemon_frlg/archipelago.json + PokemonFRLGClient.game):
//            "Pokemon FireRed and LeafGreen"
//   apworld version 1.0.4 (minimum_ap_version 0.6.3). Author: Vyneras.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED. pokemon_frlg.lua
// replicates PokemonFRLGClient.game_watcher byte-for-byte: the four flag-scan
// regions (BASE event/item/trainer flags at SaveBlock1+0x1130, dexsanity at
// SaveBlock1+0x0848, shopsanity at SaveBlock2+0xB24, famesanity at
// SaveBlock1+0x3B14), the AP location id == in-game flag/computed offset model
// (FRLG adds NO base offset — locations.py name_to_id = location_data.flag, ids
// 340..24671), and the FLAG_DEFEATED_CHAMP / FLAG_DEFEATED_CHAMP_REMATCH goal.
// The address map (gMain / gSaveBlock1Ptr / gSaveBlock2Ptr) is taken verbatim from
// data/extracted_data.json and is IDENTICAL across all four base ROM revisions.
// Mock-verified through MoonSharp against synthetic GBA System Bus / ROM buffers
// (the IN-OVERWORLD gate, base + dexsanity + shopsanity + famesanity detection,
// the AP-ROM identity gate, and goal trigger). In-emulator confirmation on a real
// FRLG save is the remaining gate.
//
// items_handling = 0b011 if remote_items else 0b001 (client.py validate_rom). The
// DEFAULT (0b001) means the server sends only THIS slot's own items and the
// PATCHED GAME grants its locally-found items itself, so a SOLO seed plays fully
// and every check is reported in a multiworld. Delivering REMOTE items is the
// client's gArchipelagoReceivedItem buffer handshake (byte-identical to Pokémon
// Emerald's 4-write protocol); it is deferred (pokemon_frlg.lua receive_item is a
// documented no-op) until it can be confirmed in-emulator, rather than shipped
// unverified.
//
// AP PATCH (.apfirered / .apleafgreen → .gba): a standard APProcedurePatch
// container (base_patch_*.bsdiff4 + token_data), applied via the shared
// SnesApPatchHelper (resolve the seed's patch, validate the base ROM MD5 from the
// manifest base_checksum, run apply_appatch.py on a library COPY — the original is
// never modified). Base ROM: the user's own FireRed OR LeafGreen (USA) cartridge
// dump (16 MB), identified by CONTENT (size + MD5) — never by filename (§11).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PokemonFRLGPlugin : EmulatorPlugin
{
    public PokemonFRLGPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "pokemon_frlg";
    public override string DisplayName => "Pokémon FireRed & LeafGreen";
    public override string Subtitle    => "GBA · Emulated";

    // Exact AP world id ("game" string in pokemon_frlg/archipelago.json + the
    // PokemonFRLGClient.game / PokemonFRLGWorld definition).
    public override string ApWorldName => "Pokemon FireRed and LeafGreen";

    public override string Description =>
        "Pokémon FireRed & LeafGreen are the Game Boy Advance remakes of the Kanto " +
        "originals. In the Archipelago randomizer, badges, HMs, key items, TMs, " +
        "overworld and hidden items, and optional Pokédex/shop checks across Kanto " +
        "and the Sevii Islands join the multiworld pool. Beat the Champion to " +
        "complete your goal. One world covers both versions — bring your FireRed or " +
        "LeafGreen dump.";

    public override string ThemeAccentColor => "#E03C2C";   // FireRed flame red

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "GBA";

    // §14: GBA emulators — BizHawk verified; mGBA/Mesen are the alternatives
    // (shown per their bridge state in the dropdown).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "mgba", "mesen" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "pokemon_frlg";

    /// pokemon_frlg.lua reports checks + goal (four flag-scan regions mirrored
    /// from the apworld client; mock-verified). Remote-item delivery is the
    /// documented deferred piece.
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dumps the randomizer is built for, by CONTENT
    /// (16 MB + MD5) — never by filename. ALL FOUR accepted base ROMs (FireRed
    /// rev0/rev1 and LeafGreen rev0/rev1; one apworld covers both versions). These
    /// MD5s are the patch `hash` lists (PokemonFireRedProcedurePatch.hash /
    /// PokemonLeafGreenProcedurePatch.hash in rom.py); the patch base_checksum
    /// validates the exact dump before anything is written.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(16 * 1024 * 1024, "e26ee0d44e809351c8ce2d73c7400cdd",
                        "Pokémon FireRed (USA)"),
        new RomIdentity(16 * 1024 * 1024, "51901a6e40661b3914aa333c802e24e8",
                        "Pokémon FireRed (USA) Rev 1"),
        new RomIdentity(16 * 1024 * 1024, "612ca9473451fa42b51d1711031ed5f6",
                        "Pokémon LeafGreen (USA)"),
        new RomIdentity(16 * 1024 * 1024, "9d33a02159e018d09073e700e1fd10fd",
                        "Pokémon LeafGreen (USA) Rev 1"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    // pokemon_frlg ships one world but two cartridges → two patch extensions.
    private const string PatchExtFireRed   = ".apfirered";
    private const string PatchExtLeafGreen = ".apleafgreen";

    // The two known LeafGreen base-ROM MD5s — used only to phrase the ROM prompt
    // (which version the resolved patch wants); FireRed is the fallback label.
    private static readonly string[] LeafGreenMd5 =
        { "612ca9473451fa42b51d1711031ed5f6", "9d33a02159e018d09073e700e1fd10fd" };

    /// Explicit .apfirered/.apleafgreen chosen by the user (Settings / drag-drop).
    /// Null = auto.
    public string? ApPatchPath { get; private set; }

    protected override void OnSavingSettings(IDictionary<string, object?> bag)
        => bag["ap_patch_path"] = ApPatchPath;

    protected override void OnLoadingSettings(JsonElement root)
    {
        if (root.TryGetProperty("ap_patch_path", out var el)) ApPatchPath = el.GetString();
    }

    /// Set the explicit AP patch (drag-and-drop / room-link import) and persist it.
    public void SetExplicitPatch(string path) { ApPatchPath = path; SaveSettings(); }

    /// Resolve the best patch across BOTH FireRed and LeafGreen extensions, in the
    /// helper's normal priority order (explicit pick → this room's seed+slot →
    /// slot name → newest). When the user pinned an explicit patch it always wins
    /// and its own extension is irrelevant. Otherwise we ask the helper for each
    /// extension and keep the newer-modified of the two matches (so a LeafGreen
    /// seed resolves to the .apleafgreen even when a stale .apfirered is lying
    /// around).
    private string? ResolveBestPatch(out string how)
    {
        if (!string.IsNullOrEmpty(ApPatchPath) && File.Exists(ApPatchPath))
        {
            how = "picked in Settings";
            return ApPatchPath;
        }

        string? seed = GetSeedName?.Invoke();
        string? fr   = SnesApPatchHelper.ResolvePatch(PatchExtFireRed,   null, seed, CurrentSlotName, out string howFr);
        string? lg   = SnesApPatchHelper.ResolvePatch(PatchExtLeafGreen, null, seed, CurrentSlotName, out string howLg);

        if (fr != null && lg != null)
        {
            // Both versions have a candidate — keep the more recently written one.
            bool frNewer = File.GetLastWriteTimeUtc(fr) >= File.GetLastWriteTimeUtc(lg);
            how = frNewer ? howFr : howLg;
            return frNewer ? fr : lg;
        }
        if (fr != null) { how = howFr; return fr; }
        if (lg != null) { how = howLg; return lg; }
        how = "no patch found";
        return null;
    }

    /// ROM safety net: the resolved patch tells us the EXACT vanilla ROM MD5 it
    /// needs (FireRed's or LeafGreen's) — demand that specific dump when ours is
    /// missing or the wrong one.
    public override RomRequirement? GetUnmetRomRequirement()
    {
        string? patch = ResolveBestPatch(out _);
        string? wantMd5 = patch != null
            ? SnesApPatchHelper.ReadManifestField(patch, "base_checksum") : null;
        bool haveRom = RomPath != null && File.Exists(RomPath);

        if (wantMd5 == null)
            return haveRom ? null
                 : new RomRequirement("Pokémon FireRed & LeafGreen", "GBA",
                       "Pokémon FireRed or LeafGreen (USA)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        // Name the exact version the patch wants so the prompt is unambiguous.
        bool wantsLeafGreen = Array.Exists(LeafGreenMd5,
            m => m.Equals(wantMd5, StringComparison.OrdinalIgnoreCase));
        string versionLabel = wantsLeafGreen
            ? "Pokémon LeafGreen (USA) — your original cartridge dump"
            : "Pokémon FireRed (USA) — your original cartridge dump";

        return new RomRequirement("Pokémon FireRed & LeafGreen", "GBA",
            versionLabel, wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apfirered/.apleafgreen to a library copy ───────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = ResolveBestPatch(out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Pokémon FireRed & LeafGreen] No AP patch (.apfirered / .apleafgreen) " +
                "found — launching the vanilla ROM. Generate the multiworld, then pick " +
                "the patch in Settings (or drop it under " +
                SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Pokémon FireRed & LeafGreen", patch, how, RomPath!,
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
