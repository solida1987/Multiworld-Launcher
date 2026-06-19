using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// MegaMan2Plugin — Archipelago integration for Mega Man 2 (NES, USA), on the
// proven BizHawk Lua-pipe bridge.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED (the 71-location
// set in mm2.lua was GENERATED from worlds/mm2/client.py's own check loops +
// MM2_CONSUMABLE_TABLE and cross-checked 1:1 against locations.py; the byte/bit
// math, the "MM2" PRG-ROM signature, the difficulty gate and the
// completed_stages[0xD] goal are replicated exactly from worlds/mm2/client.py, and
// mock-verified through MoonSharp against synthetic NES memory). items_handling =
// 0b111 — the AP SERVER drives ALL item delivery; the reference client writes
// received items into NES RAM (weapon/item unlock masks, stage access, E-tank/
// life/energy queues, SFX strobes, EnergyLink) gated on the game's own
// received-items counter at RAM 0x8E. That guarded multi-write is the documented
// deferred piece (mm2.lua receive_item is a no-op) until it can be confirmed
// in-emulator, rather than shipped unverified — a wrong RAM write mid-run corrupts
// it / desyncs the counter. Checks + goal flow regardless.
//
// AP PATCH (.apmm2 → .nes): a standard APProcedurePatch container
// (mm2_basepatch.bsdiff4 + token_patch.bin), applied via the shared
// SnesApPatchHelper (resolve the seed's patch, validate the base ROM via the
// manifest base_checksum, run apply_appatch.py on a library COPY — the original is
// never modified). Base ROM: the user's own Mega Man 2 (USA) NES cartridge dump,
// identified by CONTENT (size) — never by filename (§11).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MegaMan2Plugin : EmulatorPlugin
{
    public MegaMan2Plugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "mm2";
    public override string DisplayName => "Mega Man 2";
    public override string Subtitle    => "NES · Emulated";
    public override string ApWorldName => "Mega Man 2";

    public override string Description =>
        "Mega Man 2, the 1988 NES classic. In the Archipelago randomizer the eight " +
        "Robot Master weapons, the three Items, every stage clear and the scattered " +
        "E-tanks, 1-ups and energy pickups join the multiworld pool, with shuffled " +
        "stage access and boss weaknesses. Fight through Dr. Wily's castle and defeat " +
        "him to complete your goal. Bring your own Mega Man 2 (USA) NES ROM.";

    public override string ThemeAccentColor => "#0078D0";   // Mega Man cyan-blue

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "NES";

    // §14: NES emulators — BizHawk is the mm2 community's verified client target;
    // Mesen is the NES alternative (shown per its bridge state in the dropdown).
    // mGBA is NOT offered — it is a GB/GBA emulator and cannot host NES.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "mesen" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "mm2";

    /// mm2.lua reports checks + goal (id set generated from the apworld; byte/bit
    /// math from client.py; mock-verified). Remote-item delivery is the documented
    /// deferred piece (items_handling = 0b111, server-driven RAM writes).
    public override bool ChecksImplemented => true;

    /// The vanilla NES cartridge dump the randomizer is built for, by CONTENT —
    /// never by filename (§11). worlds/mm2/rom.py accepts the US NES, Legacy
    /// Collection and Virtual Console releases; its MD5s are of the HEADERLESS PRG
    /// (it strips the 16-byte iNES header before hashing). The launcher validates
    /// the user's whole FILE, so a headerless MD5 cannot be pinned here; instead we
    /// pin the standard US NES file SIZE — a 16-byte iNES header + 256 KB PRG =
    /// 262,160 bytes — as the stable, name-independent detector. The authoritative
    /// gate is the patch's own base_checksum (validated by apply_appatch.py before
    /// any byte is written), and mm2.lua's "MM2" PRG-ROM signature confirms a real
    /// MM2 ROM at launch. A wrong-but-right-sized file is therefore caught at patch
    /// time with a clear checksum-mismatch message rather than blocked at import.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(262_160, null,
                        "Mega Man 2 (USA) — NES cartridge dump (256 KB PRG + iNES header)"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    private const string PatchExt = ".apmm2";

    /// Explicit .apmm2 chosen by the user (Settings / drag-drop). Null = auto.
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
    /// missing or the wrong one. Note: mm2's manifest checksum is the HEADERLESS
    /// PRG MD5, so we cannot compare it against our whole-file MD5 — when the patch
    /// carries one we surface it as the required version but still let the Python
    /// patcher be the authority on the exact match.
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

        return new RomRequirement("Mega Man 2", "NES",
            "Mega Man 2 (USA) — your original NES cartridge dump",
            // The manifest MD5 is the headerless PRG hash; pass it through for
            // display, but WrongVersionPresent is false (no ROM is set at all).
            wantMd5, WrongVersionPresent: false, BuildRomFilter());
    }

    // ── Session ROM: apply the .apmm2 to a library copy ────────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Mega Man 2] No AP patch (.apmm2) found — launching the vanilla ROM. " +
                "Generate the multiworld, then pick the patch in Settings (or drop it " +
                "under " + SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Mega Man 2", patch, how, RomPath!,
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
