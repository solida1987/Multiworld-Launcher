using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// PokemonCrystalPlugin — Archipelago integration for Pokémon Crystal (Game Boy
// Color), on the proven BizHawk Lua-pipe bridge. COMMUNITY world (gerbiljames,
// "Archipelago-Crystal", release 5.4.6).
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED. pokemon_crystal.lua
// was built by PARSING the released pokemon_crystal.apworld (client.py + rom.py +
// world.py + data/data.json) with Python — the WRAM flag-array bases/lengths, the
// three location families (event flags = raw bit index, dexsanity = id+10000 gated on
// has_pokedex, grasssanity = bit+30000 remapped via slot_data, dexcountsanity =
// caught-count thresholds + 20000), the wArchipelagoSafeWrite overworld gate, and the
// slot_data-driven goal-flag list are taken byte-for-byte from client.game_watcher.
// Mock-verified through MoonSharp against synthetic GB memory. items_handling = 0b001
// (or 0b011 when the slot's remote_items option is on) — in BOTH modes the PATCHED
// GAME grants its own locally-found items, so a SOLO seed plays fully and every check
// is reported in a multiworld. Delivering REMOTE items is the client's guarded
// wArchipelagoItemReceived WRAM write; it is deferred (pokemon_crystal.lua
// receive_item is a no-op) until it can be confirmed in-emulator, rather than shipped
// unverified where a wrong write could corrupt the save.
//
// AP PATCH (.apcrystal → .gbc): a standard APProcedurePatch container
// (PokemonCrystalProcedurePatch: apply_bsdiff4 + token_data + overrides), applied via
// the shared SnesApPatchHelper (resolve the seed's patch, validate the base ROM MD5
// from the manifest base_checksum, run apply_appatch.py on a library COPY — the
// original is never modified). Base ROM: the user's own Pokémon Crystal (UE)
// cartridge dump, V1.0 OR V1.1 (2 MB), identified by CONTENT (size + MD5) — never by
// filename (§11). The apworld accepts both revisions (PokemonCrystalProcedurePatch
// .hash lists both), so both are accepted here.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PokemonCrystalPlugin : EmulatorPlugin
{
    public PokemonCrystalPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "pokemon_crystal";
    public override string DisplayName => "Pokémon Crystal";
    public override string Subtitle    => "GBC · Emulated";
    public override string ApWorldName => "Pokemon Crystal";

    public override string Description =>
        "Pokémon Crystal is the culmination of the Generation I and II Pokémon games. " +
        "In the Archipelago randomizer, badges, key items, TMs, HMs and overworld " +
        "items across Johto and Kanto join the multiworld pool, with optional dex-, " +
        "grass- and shopsanity. Explore both regions, become the Pokémon League " +
        "Champion, and defeat the elusive Red at the peak of Mt. Silver to complete " +
        "your goal. Bring your own Crystal (UE) V1.0 or V1.1 dump.";

    public override string ThemeAccentColor => "#3B6BB5";   // Crystal blue

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "GBC";

    // §14: GB/GBC emulators — BizHawk verified (the apworld ships a BizHawk Lua AP
    // client for Crystal, system ("GB","GBC")); mGBA/Mesen are the alternatives
    // (shown per their bridge state in the dropdown).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "mgba", "mesen" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "pokemon_crystal";

    /// pokemon_crystal.lua reports checks + goal (tables parsed from the apworld;
    /// mock-verified). Remote-item delivery is the documented deferred piece.
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dumps the randomizer is built for, by CONTENT
    /// (2 MB + MD5) — never by filename. Both Crystal (UE) V1.0 and V1.1 are accepted
    /// (PokemonCrystalProcedurePatch.hash lists both); the patch base_checksum
    /// re-validates the exact dump before anything is written.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(2 * 1024 * 1024, "9f2922b235a5eeb78d65594e82ef5dde",
                        "Pokémon Crystal (UE) (V1.0) cartridge"),
        new RomIdentity(2 * 1024 * 1024, "301899b8087289a6436b0a241fbbb474",
                        "Pokémon Crystal (UE) (V1.1) cartridge"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    private const string PatchExt = ".apcrystal";

    /// Explicit .apcrystal chosen by the user (Settings / drag-drop). Null = auto.
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
    /// needs (V1.0's or V1.1's) — demand that specific dump when ours is missing or
    /// the wrong one.
    public override RomRequirement? GetUnmetRomRequirement()
    {
        var patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out _);
        string? wantMd5 = patch != null
            ? SnesApPatchHelper.ReadManifestField(patch, "base_checksum") : null;
        bool haveRom = RomPath != null && File.Exists(RomPath);

        if (wantMd5 == null)
            return haveRom ? null
                 : new RomRequirement("Pokémon Crystal", "GBC",
                       "Pokémon Crystal (UE) — V1.0 or V1.1",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        // Name the exact revision the patch wants so the prompt is unambiguous.
        string versionLabel = wantMd5.Equals("301899b8087289a6436b0a241fbbb474",
                                  StringComparison.OrdinalIgnoreCase)
            ? "Pokémon Crystal (UE) (V1.1) — your original cartridge dump"
            : "Pokémon Crystal (UE) (V1.0) — your original cartridge dump";

        return new RomRequirement("Pokémon Crystal", "GBC",
            versionLabel, wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apcrystal to a library copy ───────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Pokémon Crystal] No AP patch (.apcrystal) found — launching the " +
                "vanilla ROM. Generate the multiworld, then pick the patch in Settings " +
                "(or drop it under " + SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Pokémon Crystal", patch, how, RomPath!,
            RomLibraryDirectory, ".gbc", ct);
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
