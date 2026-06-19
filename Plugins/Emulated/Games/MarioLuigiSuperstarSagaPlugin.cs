using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// MarioLuigiSuperstarSagaPlugin — Archipelago integration for Mario & Luigi:
// Superstar Saga (GBA), on the proven BizHawk Lua-pipe bridge.
//
// STATUS: location DETECTION + goal + item delivery are REAL and SOURCE-DERIVED.
// Every static table in mlss.lua (ROOM_COUNT / BEANSTONES / NONBLOCK /
// ROOM_EXCEPTION / EREWARD / SHOP·BADGE·PANTS / ITEMS_BY_ID) was GENERATED from
// worlds/mlss (Locations.py + Items.py, world_version 1.10.2) and the three
// check subsystems + the receive loop mirror MLSSClient.game_watcher byte-for-
// byte. Mock-verified through MoonSharp against synthetic EWRAM/IWRAM/ROM buffers
// (block-pointer-walk, nonBlock room-exception, shop buy, goal, item delivery).
//
// items_handling = 0b101 (remote items + starting inventory). MLSS is NOT a
// self-granting ROM: the client hands the patched game each received item via the
// EWRAM mailbox 0x3057 and advances the counter at 0x4808 — mlss.lua replicates
// that exact guarded-write handshake, GATED behind the AP-ROM identity AND the
// live "MLSSAP" logo so it can never write into a foreign or unpatched cartridge.
//
// AP PATCH (.apmlss → .gba): MLSSProcedurePatch is a standard APProcedurePatch
// container (base_patch.bsdiff4 + token_data.bin + procedure steps), applied via
// the shared SnesApPatchHelper — resolve the seed's patch, validate the base ROM
// MD5 (the manifest base_checksum), run apply_appatch.py on a library COPY (the
// original is never modified). Base ROM: the user's own Mario & Luigi: Superstar
// Saga (USA) cartridge dump (16 MB), identified by CONTENT (size + MD5) — never
// by filename (§11).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MarioLuigiSuperstarSagaPlugin : EmulatorPlugin
{
    public MarioLuigiSuperstarSagaPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "mlss";
    public override string DisplayName => "Mario & Luigi: Superstar Saga";
    public override string Subtitle    => "GBA · Emulated";

    // Exact AP world id ("game" string in worlds/mlss/archipelago.json,
    // Client.py and the MLSSWorld definition) — NO colon, "&" preserved.
    public override string ApWorldName => "Mario & Luigi Superstar Saga";

    public override string Description =>
        "Mario & Luigi: Superstar Saga is the GBA original of the bros. RPG " +
        "series. In the Archipelago randomizer, every block, dig spot, shop slot, " +
        "mole reward, Beanstar piece and key item across the Beanbean Kingdom " +
        "joins the multiworld pool. Defeat Cackletta's Soul to complete your goal.";

    public override string ThemeAccentColor => "#1Fa84C";   // Beanbean green

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "GBA";

    // §14: GBA emulators — BizHawk verified; mGBA/Mesen are the alternatives
    // (shown per their bridge state in the dropdown).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "mgba", "mesen" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "mlss";

    /// mlss.lua reports checks + goal and delivers items (tables generated from
    /// the apworld; mock-verified). Live in-emulator confirmation is the
    /// remaining gate, but every memory write is guarded by the AP-ROM identity
    /// and the runtime MLSSAP logo, so it is safe to enable.
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dump the randomizer is built for, by CONTENT
    /// (16 MB + MD5) — never by filename. The patch base_checksum validates the
    /// exact dump before anything is written.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(16 * 1024 * 1024, "4b1a5897d89d9e74ec7f630eefdfd435",
                        "Mario & Luigi: Superstar Saga (USA)"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    private const string PatchExt = ".apmlss";

    /// Explicit .apmlss chosen by the user (Settings / drag-drop). Null = auto.
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
                 : new RomRequirement("Mario & Luigi: Superstar Saga", "GBA",
                       "Mario & Luigi: Superstar Saga (USA)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Mario & Luigi: Superstar Saga", "GBA",
            "Mario & Luigi: Superstar Saga (USA) — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apmlss to a library copy ──────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Mario & Luigi: Superstar Saga] No AP patch (.apmlss) found — " +
                "launching the vanilla ROM. Generate the multiworld, then pick the " +
                "patch in Settings (or drop it under " +
                SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Mario & Luigi: Superstar Saga", patch, how, RomPath!,
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
