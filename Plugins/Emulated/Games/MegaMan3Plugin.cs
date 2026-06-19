using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// MegaMan3Plugin — Archipelago integration for Mega Man 3 (NES, USA), on the
// proven BizHawk Lua-pipe bridge. Sibling to MegaMan2Plugin.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED (the 170-location
// set in mm3.lua was GENERATED from worlds/mm3/client.py's own game_watcher check
// loops + MM3_CONSUMABLE_TABLE / MM3_DOC_REMAP and cross-checked 1:1 against
// locations.py — 0 extra, 0 missing; the byte/bit math, the "MM3" PRG-ROM
// signature, the bar-state init/in-stage guard and the completed_stages & 0x20
// goal are replicated exactly from worlds/mm3/client.py, and mock-verified through
// MoonSharp against synthetic NES memory). items_handling = 0b111 — the AP SERVER
// drives ALL item delivery; the reference client writes received items into NES
// RAM (weapon-energy flags, robot-master / Doc-Robo stage-access unlock masks,
// lives / E-tanks / Rush, SFX strobes, EnergyLink) gated on the game's own
// received-items counter at RAM 0x688. That guarded multi-write is the documented
// deferred piece (mm3.lua receive_item is a no-op) until it can be confirmed
// in-emulator, rather than shipped unverified — a wrong RAM write mid-run corrupts
// it / desyncs the counter. Checks + goal flow regardless.
//
// AP PATCH (.apmm3 → .nes): a standard APProcedurePatch container
// (mm3_basepatch.bsdiff4 + token_patch.bin), applied via the shared
// SnesApPatchHelper (resolve the seed's patch, validate the base ROM via the
// manifest base_checksum, run apply_appatch.py on a library COPY — the original is
// never modified). Base ROM: the user's own Mega Man 3 (USA) NES cartridge dump,
// identified by CONTENT (size + MD5) — never by filename (§11).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MegaMan3Plugin : EmulatorPlugin
{
    public MegaMan3Plugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "mm3";
    public override string DisplayName => "Mega Man 3";
    public override string Subtitle    => "NES · Emulated";
    public override string ApWorldName => "Mega Man 3";

    public override string Description =>
        "Mega Man 3, the 1990 NES sequel. In the Archipelago randomizer the eight " +
        "Robot Master weapons, the Rush upgrades, every stage clear (Robot Masters, " +
        "Doc Robots, Break Man and the Wily fortress bosses) and the scattered " +
        "E-tanks, 1-ups and energy pickups join the multiworld pool, with shuffled " +
        "stage access and boss weaknesses. Fight through Dr. Wily's castle and defeat " +
        "Gamma to complete your goal. Bring your own Mega Man 3 (USA) NES ROM.";

    public override string ThemeAccentColor => "#3CA0E0";   // Mega Man 3 sky-blue

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "NES";

    // §14: NES emulators — BizHawk is the mm3 community's verified client target;
    // Mesen is the NES alternative (shown per its bridge state in the dropdown).
    // mGBA is NOT offered — it is a GB/GBA emulator and cannot host NES.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "mesen" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "mm3";

    /// mm3.lua reports checks + goal (id set generated from the apworld; byte/bit
    /// math from client.py; mock-verified). Remote-item delivery is the documented
    /// deferred piece (items_handling = 0b111, server-driven RAM writes).
    public override bool ChecksImplemented => true;

    /// The vanilla NES cartridge dump the randomizer is built for, by CONTENT —
    /// never by filename (§11). worlds/mm3/rom.py accepts the US NES, Legacy
    /// Collection and US Virtual Console releases; its MD5s are of the HEADERLESS
    /// PRG+CHR (it strips the 16-byte iNES header before hashing — see
    /// read_headerless_nes_rom). The launcher validates the user's whole FILE, so
    /// those headerless MD5s cannot be pinned directly; instead we pin the standard
    /// US NES file SIZE — a 16-byte iNES header + 256 KB PRG + 128 KB CHR =
    /// 393,232 bytes — as the stable, name-independent detector, and carry the
    /// known headerless MD5s for display/reference. The authoritative gate is the
    /// patch's own base_checksum (validated by apply_appatch.py before any byte is
    /// written), and mm3.lua's "MM3" PRG-ROM signature confirms a real MM3 ROM at
    /// launch. A wrong-but-right-sized file is therefore caught at patch time with a
    /// clear checksum-mismatch message rather than blocked at import.
    ///
    /// NOTE: the MD5s below are the HEADERLESS hashes (matching rom.py's MM3NESHASH /
    /// MM3LCHASH / MM3VCHASH). Because AcceptableBaseRoms compares against the WHOLE
    /// file, the MD5 fields are left null (size is the real gate) — a headered-file
    /// MD5 would never equal a headerless hash. The values are documented inline so
    /// the accepted dumps are unambiguous.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        // 4a53b6f58067d62c9a43404fe835dd5c — Mega Man 3 (USA) NES, headerless PRG+CHR
        // 5266687de215e790b2008284402f3917 — Mega Man Legacy Collection MM3, headerless
        // c50008f1ac86fae8d083232cdd3001a5 — Mega Man 3 (USA) Virtual Console, headerless
        new RomIdentity(393_232, null,
                        "Mega Man 3 (USA) — NES cartridge dump (256 KB PRG + 128 KB CHR + iNES header)"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    private const string PatchExt = ".apmm3";

    /// Explicit .apmm3 chosen by the user (Settings / drag-drop). Null = auto.
    public string? ApPatchPath { get; private set; }

    protected override void OnSavingSettings(IDictionary<string, object?> bag)
        => bag["ap_patch_path"] = ApPatchPath;

    protected override void OnLoadingSettings(JsonElement root)
    {
        if (root.TryGetProperty("ap_patch_path", out var el)) ApPatchPath = el.GetString();
    }

    /// Set the explicit AP patch (drag-and-drop / room-link import) and persist it.
    public void SetExplicitPatch(string path) { ApPatchPath = path; SaveSettings(); }

    /// ROM safety net: the resolved patch tells us the EXACT base ROM the manifest
    /// was generated against (base_checksum). Demand that specific dump when ours is
    /// missing or the wrong one. Note: mm3's manifest checksum is the HEADERLESS
    /// PRG+CHR MD5, so we cannot compare it against our whole-file MD5 — when the
    /// patch carries one we surface it as the required version but still let the
    /// Python patcher be the authority on the exact match.
    public override RomRequirement? GetUnmetRomRequirement()
    {
        var patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out _);
        bool haveRom = RomPath != null && File.Exists(RomPath);

        // A right-sized ROM is in place — the patch's base_checksum (headerless)
        // is verified Python-side at apply time, so don't second-guess it here.
        if (haveRom) return null;

        string? wantMd5 = patch != null
            ? SnesApPatchHelper.ReadManifestField(patch, "base_checksum") : null;

        return new RomRequirement("Mega Man 3", "NES",
            "Mega Man 3 (USA) — your original NES cartridge dump",
            // The manifest MD5 is the headerless PRG+CHR hash; pass it through for
            // display, but WrongVersionPresent is false (no ROM is set at all).
            wantMd5, WrongVersionPresent: false, BuildRomFilter());
    }

    // ── Session ROM: apply the .apmm3 to a library copy ────────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Mega Man 3] No AP patch (.apmm3) found — launching the vanilla ROM. " +
                "Generate the multiworld, then pick the patch in Settings (or drop it " +
                "under " + SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Mega Man 3", patch, how, RomPath!,
            RomLibraryDirectory, ".nes", ct);
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
