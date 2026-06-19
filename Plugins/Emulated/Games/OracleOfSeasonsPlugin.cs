using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// OracleOfSeasonsPlugin — Archipelago integration for The Legend of Zelda: Oracle
// of Seasons (Game Boy Color), on the proven BizHawk Lua-pipe bridge.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED. The 255-flag +
// 16-Gasha location table in tloz_oos.lua was GENERATED (not hand-copied) by a
// script that imports the real worlds/tloz_oos LOCATIONS_DATA (the `oos` branch of
// github.com/Dinopony/ArchipelagoOoS, world_version 21.0.0) and replicates, byte-
// for-byte: common/Util.py build_location_name_to_id_dict() (the AP id, which for
// every flag location is literally flag_byte*0x100 + bit_mask), the room→flag_byte
// fixup at the bottom of locations.py, and Client.py process_checked_locations()
// (flag detection + the deterministic Gasha-Nut counter). The in-game gate
// (game_state == 2) + BOTH goal options (beat_onox / beat_ganon) are taken verbatim
// from Client.py game_watcher / process_game_completion. Mock-verified through
// MoonSharp against synthetic Game Boy memory.
//
// items_handling = 0b001 — the PATCHED GAME grants its own locally-found items, so
// a SOLO seed plays fully and every check is reported in a multiworld. Delivering
// REMOTE multiworld items is the client's guarded write handshake (it polls a
// received-item index at 0xC6A0 and writes the next item's id/subid into the
// 2-byte struct at 0xCBFB only when that struct is empty AND the player is not
// inside Blaino's Gym). That write path is the one piece deferred (tloz_oos.lua
// receive_item is a documented no-op) until it can be confirmed in-emulator, rather
// than shipped unverified. Check reporting + goal are fully live.
//
// AP PATCH (.apoos → .gbc): a standard APProcedurePatch container (OoSProcedurePatch
// builds the ROM from the user's vanilla cart + the seed's data), applied via the
// shared SnesApPatchHelper (resolve the seed's patch, validate the base ROM MD5
// from the manifest's base_checksum, run apply_appatch.py on a library COPY — the
// original is never modified). Base ROM: the user's own Oracle of Seasons (USA)
// cartridge dump (1 MB), identified by CONTENT (size + MD5) — never by filename
// (§11). The world pins this exact MD5 (ROM_HASH) and OoSProcedurePatch.hash, and
// the patch's base_checksum re-checks it before anything is written.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class OracleOfSeasonsPlugin : EmulatorPlugin
{
    public OracleOfSeasonsPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "tloz_oos";
    public override string DisplayName => "Oracle of Seasons";
    public override string Subtitle    => "GBC · Emulated";
    public override string ApWorldName => "The Legend of Zelda - Oracle of Seasons";

    public override string Description =>
        "The Legend of Zelda: Oracle of Seasons is the action-adventure half of the " +
        "Game Boy Color Oracle duology. In the Archipelago randomizer, the items, " +
        "rings, and mystical seeds scattered across Holodrum — plus each region's " +
        "default season — join the multiworld pool. Defeat Onox (or, in a linked " +
        "game, Ganon) to complete your goal.";

    public override string ThemeAccentColor => "#C8541E";   // Din's autumn orange

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "GBC";

    // §14: GBC emulators — BizHawk verified (the AP world ships a BizHawk Lua
    // client for OoS, and multiworld REQUIRES BizHawk per its setup guide);
    // mGBA/Mesen are the alternatives (shown per their bridge state in the
    // dropdown).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "mgba", "mesen" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "tloz_oos";

    /// tloz_oos.lua reports checks + goal (table generated from the apworld; mock-
    /// verified). Remote-item delivery is the documented deferred piece.
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dump the randomizer is built for, by CONTENT
    /// (1 MB + MD5) — never by filename. The world pins this exact MD5 (ROM_HASH)
    /// and OoSProcedurePatch validates it before building; the patch's
    /// base_checksum re-checks it before anything is written. This is the only
    /// known-good base ROM (Oracle of Seasons USA).
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(1024 * 1024, "f2dc6c4e093e4f8c6cbea80e8dbd62cb",
                        "Zelda: Oracle of Seasons (USA) cartridge"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    private const string PatchExt = ".apoos";

    /// Explicit .apoos chosen by the user (Settings / drag-drop). Null = auto.
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
                 : new RomRequirement("Oracle of Seasons", "GBC",
                       "Zelda: Oracle of Seasons (USA)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Oracle of Seasons", "GBC",
            "Zelda: Oracle of Seasons (USA) — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apoos to a library copy ───────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Oracle of Seasons] No AP patch (.apoos) found — launching the " +
                "vanilla ROM. Generate the multiworld, then pick the patch in Settings " +
                "(or drop it under " + SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Oracle of Seasons", patch, how, RomPath!,
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
