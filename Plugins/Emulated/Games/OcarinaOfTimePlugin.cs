using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// OcarinaOfTimePlugin — Archipelago integration for The Legend of Zelda: Ocarina of
// Time (Nintendo 64), the OFFICIAL AP-main apworld "Ocarina of Time" (world id
// "oot"), on the proven BizHawk Lua-pipe bridge (N64 core).
//
// DISTINCT FROM the Ship of Harkinian native port already shipped here (GameId
// the_legend_of_zelda_ocarina_of_time_ship_of_harkinian). THIS is the N64/BizHawk
// world from ArchipelagoMW/Archipelago main (worlds/oot, world_version 7.0.0,
// archipelago.json game "Ocarina of Time", minimum_ap_version 0.6.4; the
// BizHawk-client world whose connector is data/lua/connector_oot.lua,
// script_version 3, author espeon65536). It uses its OWN world id "oot" as the
// GameId, so there is no registry collision with the SoH integration.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED. The 637-location
// table in oot.lua was GENERATED, not hand-copied: the AP location ids come from
// the world's own id algorithm in worlds/oot/Location.py (location_id_offset 67000,
// the loctypes_70 / locnames_pre_70 stable sort, non_indexed types dropped — i.e.
// exactly the network_data_package location_name_to_id the real OoTClient.py
// resolves NAMES against), and each location's flag-read recipe is taken verbatim
// from connector_oot.lua's read_*_checks helpers (scene/skulltula/event/
// item_get_info/info_table/shop/boss/big-poe/biggoron/fishing/membership). The goal
// (scene pointer == Triforce-Hunt-credits OR Ganon-defeated) is replicated exactly
// from connector_oot.lua is_game_complete. Mock-verified through MoonSharp against
// synthetic big-endian N64 RDRAM. items_handling = 0b001 (full LOCAL) — the
// AP-patched OoT ROM grants its OWN found items, so a SOLO seed plays fully and
// every check is reported in a multiworld. Delivering REMOTE multiworld items is
// the connector's guarded RDRAM write path (coop_context incoming-item handshake +
// the save's received-item counter, plus the deathlink HP write); it is deferred
// (oot.lua receive_item is a no-op) until it can be confirmed in-emulator, rather
// than shipped unverified — a wrong RDRAM write corrupts the live save. Checks +
// goal flow regardless.
//
// COVERAGE NOTE: the table covers the ~637 PERMANENT save-context checks the
// connector reads (chests, gold skulltulas, songs, shops, freestanding, Deku
// scrubs, cows, boss hearts, great-fairy rewards, fishing, big-poe bottle, Biggoron
// sword, Gerudo membership). It does NOT include the connector's INSTANT scratch-RAM
// path (0x40002C, which is read-then-ZEROED — a RAM write we don't do read-only) nor
// the slot-data-driven pot/crate/beehive "collectible" path (keyed by
// collectibleOffsets the launcher doesn't forward into the read-only module). Those
// are the documented coverage gap; every permanent check still lands the moment the
// game writes it to the save (a few frames later than the connector's scratch peek).
//
// N64 NOTE: N64 is BIG-ENDIAN. oot.lua reads BizHawk's "RDRAM" N64 domain (logical
// big-endian, un-byte-swapped — exactly as the shipped cv64 / dk64 / banjo_tooie N64
// modules read it), assembling u16/u32 big-endian from read_u8 so it never depends
// on a core's read_u16/u32 endianness, matching connector_oot.lua's
// mainmemory.read_u16_be / read_u32_be. N64 runs on BizHawk here only (the oot
// world's reference connector is a BizHawk Lua script).
//
// HOW OoT IS PATCHED — IMPORTANT: like DK64 / Banjo-Tooie / FF1, Ocarina of Time is
// NOT patched by the Archipelago generator into a launcher-applied container. The
// AP world emits an .apz5 patch; the player runs the OoT Archipelago client
// (OoTClient.py patch_and_run_game) which decompresses their own legally-obtained
// OoT 1.0 US ROM, applies the .apz5, and RE-COMPRESSES it with the world's bundled
// Compressor into "<seed>.z64". The player loads THAT patched .z64 here — the
// launcher applies no patch of its own; it loads the ROM directly (§11: the library
// copy, original untouched), same shape as the DK64 plugin. Because the output is
// re-compressed and every seed differs, the patched .z64 has no fixed size or MD5 —
// it is identified by CONTENT via the runtime OoT save-context signature oot.lua
// gates on, never by filename (§11). The vanilla OoT 1.0 US cartridge dump
// (33,554,432 bytes = 32 MiB, MD5 5bd1fe107bf8106b2ab6650abecd54d6; CRC EC7011B7…
// at ROM 0x10, per worlds/oot/Rom.py decompress_rom_file) is also accepted at import
// so a player pointing at the un-patched file is not blocked — but only the
// OoT-AP-patched .z64 actually carries the multiworld's checks/items.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class OcarinaOfTimePlugin : EmulatorPlugin
{
    public OcarinaOfTimePlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    // World id from worlds/oot — DISTINCT from the Ship of Harkinian integration.
    public override string GameId      => "oot";
    public override string DisplayName => "The Legend of Zelda: Ocarina of Time (N64)";
    public override string Subtitle    => "N64 · Emulated";
    public override string ApWorldName => "Ocarina of Time";

    public override string Description =>
        "The Legend of Zelda: Ocarina of Time is Nintendo's landmark 3D adventure " +
        "on the Nintendo 64 — guide Link across Hyrule, through nine dungeons and " +
        "the flow of time, to stop Ganondorf. In the Archipelago randomizer, the " +
        "items hidden in chests, songs, gold skulltulas, shops, Deku scrubs, cows " +
        "and dungeon rewards across Hyrule join the multiworld pool — defeat Ganon " +
        "(or fulfill your Triforce Hunt) to complete your run. Ocarina of Time is " +
        "patched with the Ocarina of Time Archipelago client, which decompresses, " +
        "patches and re-compresses your own OoT 1.0 US ROM into the seed's .z64.";

    public override string ThemeAccentColor => "#3FA34D";   // Ocarina of Time forest green

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "N64";

    // §14: N64 runs on BizHawk here (the oot world's reference client is a BizHawk
    // Lua connector, connector_oot.lua). snes9x/mGBA/Mesen are SNES/GB-GBA/NES
    // emulators — not N64 — so they are deliberately NOT offered for this game.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "oot";

    /// oot.lua reports checks + goal (637-flag table generated from the apworld's
    /// location_name_to_id x connector_oot.lua permanent-flag formulas; mock-
    /// verified). Remote-item delivery is the documented deferred piece
    /// (items_handling = 0b001, client-driven RDRAM coop_context writes).
    public override bool ChecksImplemented => true;

    /// The ROM the player loads is the OoT-AP-patched .z64 produced by the Ocarina
    /// of Time Archipelago client (OoTClient.py decompresses, applies the .apz5, and
    /// re-compresses a OoT 1.0 US ROM), identified by CONTENT — never by filename
    /// (§11). The patched ROM is re-compressed and every seed differs, so it has no
    /// fixed size/MD5; oot.lua's save-context signature confirms it is really an OoT
    /// ROM at launch. The vanilla "Legend of Zelda: Ocarina of Time (USA) v1.0"
    /// cartridge dump (33,554,432 bytes = 32 MiB, MD5
    /// 5bd1fe107bf8106b2ab6650abecd54d6, native big-endian compressed .z64; the only
    /// version the world accepts — Rom.py validCRC EC7011B7… at ROM 0x10) is also
    /// accepted at import so a player pointing at the un-patched file is not blocked —
    /// but only the OoT-AP-patched .z64 carries the multiworld's checks/items.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(33_554_432, "5bd1fe107bf8106b2ab6650abecd54d6",
            "The Legend of Zelda: Ocarina of Time (USA) v1.0 — vanilla .z64 cartridge dump " +
            "(patch it with the Ocarina of Time Archipelago client first)"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────
    //
    // OoT has no APProcedurePatch the launcher applies (see header) — the .apz5 is
    // applied externally by the OoT AP client, which also re-compresses the ROM. We
    // keep the standard hook shape for consistency, but the resolver is only an
    // optional override: if a player ever drops an explicit patch container in
    // Settings it would be honored; otherwise the OoT-AP-patched .z64 is loaded
    // directly with no patch step. The AP generator never emits a launcher-applied
    // container here.

    private const string PatchExt = ".apz5";

    /// Explicit patch override chosen by the user (Settings / drag-drop). Null =
    /// none — the normal OoT path, the patched .z64 is loaded as-is.
    public string? ApPatchPath { get; private set; }

    protected override void OnSavingSettings(IDictionary<string, object?> bag)
        => bag["ap_patch_path"] = ApPatchPath;

    protected override void OnLoadingSettings(JsonElement root)
    {
        if (root.TryGetProperty("ap_patch_path", out var el)) ApPatchPath = el.GetString();
    }

    /// Set an explicit patch override (rare for OoT) and persist it.
    public void SetExplicitPatch(string path) { ApPatchPath = path; SaveSettings(); }

    /// ROM safety net. OoT has no launcher-applied patch carrying an expected MD5
    /// (the .apz5 is applied + re-compressed externally by the OoT AP client), so the
    /// only requirement we can surface is "an OoT ROM is present" — the player
    /// supplies the OoT-AP-patched .z64 themselves. A future explicit .apz5 override
    /// (which the launcher does not apply for standard seeds) would still demand its
    /// base MD5 if one is set.
    public override RomRequirement? GetUnmetRomRequirement()
    {
        var patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out _);
        string? wantMd5 = patch != null
            ? SnesApPatchHelper.ReadManifestField(patch, "base_checksum") : null;
        bool haveRom = RomPath != null && File.Exists(RomPath);

        if (wantMd5 == null)
            return haveRom ? null
                 : new RomRequirement("The Legend of Zelda: Ocarina of Time", "N64",
                       "your OoT-AP-patched .z64 (make it with the Ocarina of Time " +
                       "Archipelago client from your own OoT 1.0 US ROM)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("The Legend of Zelda: Ocarina of Time", "N64",
            "Ocarina of Time (USA) v1.0 — the exact ROM this patch was generated against",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM ───────────────────────────────────────────────────────────
    //
    // The normal OoT path applies NO patch — the player's library ROM IS the
    // OoT-AP-patched .z64 they produced with the OoT AP client, so we return null and
    // the base class launches it directly. Only an explicit .apz5 override would
    // trigger SnesApPatchHelper; that branch is kept for parity with the other
    // emulated games but is inert for standard OoT seeds (the launcher cannot apply
    // an .apz5 itself — it needs the world's bundled Decompress/Compress tools and
    // the vanilla base ROM, which is why the OoT AP client builds the .z64 instead).

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            // Standard OoT: no launcher-applied AP patch. The library ROM is the
            // OoT-AP-patched .z64 the player generated with the OoT AP client.
            SessionRomNote =
                "[Ocarina of Time] Ocarina of Time is patched by the Ocarina of Time " +
                "Archipelago client (not by the launcher) — launching your ROM " +
                "directly. Make sure this is the OoT-AP-patched .z64 generated from " +
                "your own OoT 1.0 US ROM, not the vanilla cartridge dump.";
            return null;
        }

        // Optional explicit-override path (kept for parity; OoT seeds use the client).
        var res = await SnesApPatchHelper.ApplyAsync(
            "Ocarina of Time", patch, how, RomPath!,
            RomLibraryDirectory, ".z64", ct);
        SessionRomNote = res.Note;
        if (res.OutRom != null) RegisterSeed(res.OutRom, patch);
        return res.OutRom;
    }

    /// Record a produced/reused patched ROM in the seed library. Never throws.
    /// (Only reached on the optional explicit-override path — standard OoT seeds
    /// launch the OoT-AP-patched ROM directly and never get here.)
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
