using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// SuperMarioLand2Plugin — Archipelago integration for Super Mario Land 2: 6 Golden
// Coins (Game Boy), on the proven BizHawk Lua-pipe bridge.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED (the 60-entry
// exit/secret/bell location table in marioland2.lua was GENERATED from
// worlds/marioland2/locations.py with the client's exact
// enumerate(locations.items(), START_IDS) ordering; the per-type bit/condition
// math + the loaded/music guard + the "MARIOLAND2" ROM title are replicated
// exactly from worlds/marioland2/client.py, and mock-verified through MoonSharp
// against synthetic Game Boy memory). The ~2597 individual coin locations
// (coinsanity) are detected by the client over the LIVE level tilemap (System
// Bus), a different runtime detection class than the static cartridge flag array;
// that path is the documented deferred piece (marioland2.lua reports exits,
// secrets and bells — the on-cartridge flags — and a coinsanity seed reports its
// exit/secret/bell subset until the live coin scan is confirmed in-emulator).
//
// items_handling = 0b111 — the AP SERVER drives ALL item delivery; the reference
// client applies received items by writing PATCH-GENERATED ROM addresses (unique
// per seed) plus guarded CartRAM writes (lives / coins / level-data / difficulty).
// Those targets are not knowable without the per-seed patch's address map, so
// remote-item delivery is deferred (marioland2.lua receive_item is a no-op) until
// it can be confirmed in-emulator, rather than shipped unverified — a wrong ROM/
// CartRAM write corrupts the run. Checks + goal flow regardless.
//
// AP PATCH (.apsml2 → .gb): a standard APProcedurePatch container
// (apply_bsdiff4 + apply_tokens), applied via the shared SnesApPatchHelper
// (resolve the seed's patch, validate the base ROM MD5, run apply_appatch.py on a
// library COPY — the original is never modified). Base ROM: the user's own Super
// Mario Land 2: 6 Golden Coins (World) v1.0 cartridge dump (512 KB, MD5
// a8413347d5df8c9d14f97f0330d67bce — the exact hash rom.py demands), identified by
// CONTENT (size + MD5) — never by filename (§11).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SuperMarioLand2Plugin : EmulatorPlugin
{
    public SuperMarioLand2Plugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "marioland2";
    public override string DisplayName => "Super Mario Land 2: 6 Golden Coins";
    public override string Subtitle    => "GB · Emulated";
    public override string ApWorldName => "Super Mario Land 2";

    public override string Description =>
        "Super Mario Land 2: 6 Golden Coins is the 1992 Game Boy platformer where " +
        "Mario storms his own castle. In the Archipelago randomizer, level exits, " +
        "secret exits, midway bells, power-ups and abilities join the multiworld " +
        "pool, with optional coinsanity and golden-coin or coin-fragment hunt goals. " +
        "Reclaim Mario's Castle to complete your goal.";

    public override string ThemeAccentColor => "#E8A018";   // Mario gold

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "GB";

    // §14: Game Boy emulators — BizHawk is the SML2 community's verified client
    // target; mGBA + Mesen are the GB alternatives (shown per their bridge state
    // in the dropdown).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "mgba", "mesen" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "marioland2";

    /// marioland2.lua reports exit/secret/bell checks + goal (table generated from
    /// the apworld; bit math from client.py; mock-verified). Coin (coinsanity)
    /// detection and remote-item delivery are the documented deferred pieces.
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dump the randomizer is built for, by CONTENT
    /// (512 KB + MD5) — never by filename. rom.py pins exactly one MD5
    /// (a8413347d5df8c9d14f97f0330d67bce, SML2 "1.0"); the patch base_checksum
    /// validates that exact dump before anything is written.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(512 * 1024, "a8413347d5df8c9d14f97f0330d67bce",
                        "Super Mario Land 2: 6 Golden Coins (World) v1.0 cartridge"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    private const string PatchExt = ".apsml2";

    /// Explicit .apsml2 chosen by the user (Settings / drag-drop). Null = auto.
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
                 : new RomRequirement("Super Mario Land 2: 6 Golden Coins", "GB",
                       "Super Mario Land 2: 6 Golden Coins (World) v1.0",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Super Mario Land 2: 6 Golden Coins", "GB",
            "Super Mario Land 2: 6 Golden Coins (World) v1.0 — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apsml2 to a library copy ──────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Super Mario Land 2: 6 Golden Coins] No AP patch (.apsml2) found — " +
                "launching the vanilla ROM. Generate the multiworld, then pick the " +
                "patch in Settings (or drop it under " +
                SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Super Mario Land 2: 6 Golden Coins", patch, how, RomPath!,
            RomLibraryDirectory, ".gb", ct);
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
