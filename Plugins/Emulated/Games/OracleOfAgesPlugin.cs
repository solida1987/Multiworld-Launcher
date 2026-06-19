using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// OracleOfAgesPlugin — Archipelago integration for The Legend of Zelda: Oracle
// of Ages (Game Boy Color), on the proven BizHawk Lua-pipe bridge. Sibling of
// OracleOfSeasonsPlugin; the two share everything but their identity constants.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED. The 226-flag
// location table in tloz_ooa.lua was GENERATED (not hand-copied) by a script that
// imports the real worlds/tloz_ooa LOCATIONS_DATA (the `oos` branch of
// github.com/SenPierre/ArchipelagoOoA, apworld VERSION 0.4.3) and replicates,
// byte-for-byte: Data.py build_location_name_to_id_dict() — OoA assigns ids
// SEQUENTIALLY from BASE_LOCATION_ID = 27022002000 in LOCATIONS_DATA dict order
// (NOT flag_byte*0x100+mask like Seasons), so the table was produced by importing
// the real module to preserve that order — and Client.py
// process_checked_locations() (flag detection: flag_bytes[flag_byte-0xC600] & mask
// == mask, default mask 0x20, with the multi-flag_byte "break on first match"
// alternatives modeled exactly). The in-game gate (game_state == 2) + BOTH goal
// options are taken verbatim from Client.py game_watcher / process_game_completion:
// beat_veran (default) is a pure flag test (0xC6D8 & 0x80, no room), beat_ganon is
// room 0x05F1 + flag 0xCAF1 & 0x80. Mock-verified through MoonSharp against
// synthetic Game Boy memory.
//
// items_handling = 0b101 — remote items + starting inventory. The patched game
// grants its own locally-found and STARTING items, and every check is reported in
// a multiworld, but unlike a fully-local seed the multiworld DOES rely on the
// client to deliver REMOTE items. That delivery is the client's guarded write
// handshake (it polls a received-item count at 0xC6A8 and writes the next item's
// id/subid into the 2-byte struct at 0xCBFB only while that struct is empty — the
// "empty" guard doubles as the Blaino's-Gym safety). That write path is the one
// piece deferred (tloz_ooa.lua receive_item is a documented no-op) until it can be
// confirmed in-emulator, rather than shipped unverified. Check reporting + goal are
// fully live.
//
// AP PATCH (.apooa → .gbc): a standard APProcedurePatch container (the world's
// OoAProcedurePatch builds the ROM from the user's vanilla cart + the seed's data),
// applied via the shared SnesApPatchHelper (resolve the seed's patch, validate the
// base ROM MD5 from the manifest's base_checksum, run apply_appatch.py on a library
// COPY — the original is never modified). Base ROM: the user's own Oracle of Ages
// (USA) cartridge dump (1 MB), identified by CONTENT (size + MD5) — never by
// filename (§11). The world pins this exact MD5 (ROM_HASH) and OoAProcedurePatch.hash,
// and the patch's base_checksum re-checks it before anything is written.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class OracleOfAgesPlugin : EmulatorPlugin
{
    public OracleOfAgesPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "tloz_ooa";
    public override string DisplayName => "Oracle of Ages";
    public override string Subtitle    => "GBC · Emulated";
    public override string ApWorldName => "The Legend of Zelda - Oracle of Ages";

    public override string Description =>
        "The Legend of Zelda: Oracle of Ages is the puzzle-focused half of the " +
        "Game Boy Color Oracle duology. In the Archipelago randomizer, the items, " +
        "rings, and tunes scattered across Labrynna — past and present — join the " +
        "multiworld pool. Defeat Veran (or, in a linked game, Ganon) to complete " +
        "your goal.";

    public override string ThemeAccentColor => "#1E7A8C";   // Nayru's tidal teal

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "GBC";

    // §14: GBC emulators — BizHawk verified (the AP world ships a BizHawk Lua
    // client for OoA, and multiworld REQUIRES BizHawk per its setup guide);
    // mGBA/Mesen are the alternatives (shown per their bridge state in the
    // dropdown).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "mgba", "mesen" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "tloz_ooa";

    /// tloz_ooa.lua reports checks + goal (table generated from the apworld; mock-
    /// verified). Remote-item delivery is the documented deferred piece.
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dump the randomizer is built for, by CONTENT
    /// (1 MB + MD5) — never by filename. The world pins this exact MD5 (ROM_HASH)
    /// and OoAProcedurePatch validates it before building; the patch's
    /// base_checksum re-checks it before anything is written. This is the only
    /// known-good base ROM (Oracle of Ages USA).
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(1024 * 1024, "c4639cc61c049e5a085526bb6cac03bb",
                        "Zelda: Oracle of Ages (USA) cartridge"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    private const string PatchExt = ".apooa";

    /// Explicit .apooa chosen by the user (Settings / drag-drop). Null = auto.
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
                 : new RomRequirement("Oracle of Ages", "GBC",
                       "Zelda: Oracle of Ages (USA)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Oracle of Ages", "GBC",
            "Zelda: Oracle of Ages (USA) — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apooa to a library copy ───────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Oracle of Ages] No AP patch (.apooa) found — launching the " +
                "vanilla ROM. Generate the multiworld, then pick the patch in Settings " +
                "(or drop it under " + SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Oracle of Ages", patch, how, RomPath!,
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
