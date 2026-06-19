using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// FireEmblem8Plugin — Archipelago integration for Fire Emblem: The Sacred Stones
// (GBA, "FE8"), on the proven BizHawk Lua-pipe bridge.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED (the 83-location
// table in fe8.lua was GENERATED from worlds/fe8/connector_config.py; the flag
// bit math, the AP-ROM "FE8AP" signature, and the slot_data-driven goal flag are
// all mirrored from worlds/fe8/client.py and the in-game connector_fe8.lua;
// mock-verified through MoonSharp against synthetic memory). items_handling =
// 0b001 — the PATCHED GAME grants its own locally-found items, so a SOLO seed
// plays fully and every check is reported in a multiworld. Delivering REMOTE
// multiworld items is the client's guarded EWRAM-struct write (APReceivedItem +
// a "filled"/received-count handshake, only while in a safe proc state); it is
// deferred (fe8.lua receive_item is a no-op) until it can be confirmed
// in-emulator, rather than shipped unverified.
//
// SOURCE: the FE8 apworld is CT075/Archipelago @ branch fe8/stable
// (worlds/fe8 — "Is the actual randomizer in this repo? No." per the basepatch
// README). The base patch + in-game connector live at CT075/fe8-archipelago.
//
// AP PATCH (.apfe8 → .gba): FE8ProcedurePatch is a standard APProcedurePatch
// container (base_patch.bsdiff4 + token_data.bin + gameplay-changes step),
// applied via the shared SnesApPatchHelper (resolve the seed's patch, validate
// the base ROM MD5, run apply_appatch.py on a library COPY — the original is
// never modified). Base ROM: the user's own Fire Emblem: The Sacred Stones (U)
// cartridge dump (16 MB, MD5 005531fef9efbb642095fb8f64645236 — the patch
// `hash` / the apworld's settings md5s), identified by CONTENT (size + MD5),
// never by filename (§11).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class FireEmblem8Plugin : EmulatorPlugin
{
    public FireEmblem8Plugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "fe8";
    public override string DisplayName => "Fire Emblem: The Sacred Stones";
    public override string Subtitle    => "GBA · Emulated";

    // Exact AP world id ("game" string in worlds/fe8: FE8_NAME / FE8Client.game /
    // FE8World.game / GBAContext.game).
    public override string ApWorldName => "Fire Emblem Sacred Stones";

    public override string Description =>
        "Fire Emblem: The Sacred Stones is the GBA tactical RPG where the twins " +
        "Eirika and Ephraim raise an army against the demon king Fomortiis. In the " +
        "Archipelago randomizer, chapter clears, Sacred Twin regalia, and unit " +
        "recruitments become checks, while progressive level caps, weapon ranks, " +
        "and unit deployments join the multiworld pool. Reach your chosen goal — " +
        "by default, defeat Fomortiis — to complete the seed.";

    public override string ThemeAccentColor => "#1C6FB0";   // Sacred Stones azure

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "GBA";

    // §14: GBA emulators — BizHawk verified; mGBA/Mesen are the alternatives
    // (shown per their bridge state in the dropdown).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "mgba", "mesen" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "fe8";

    /// fe8.lua reports checks + goal (83-location table generated from the
    /// apworld; mock-verified). Remote-item delivery is the documented deferred
    /// piece (items_handling = 0b001, so solo play and check reporting are full).
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dump the randomizer is built for, by CONTENT
    /// (16 MB + MD5) — never by filename. The MD5 is the patch `hash`
    /// (FE8ProcedurePatch.hash, also the apworld's FE8RomFile.md5s); the patch
    /// base_checksum validates the exact dump before anything is written.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(16 * 1024 * 1024, "005531fef9efbb642095fb8f64645236",
                        "Fire Emblem: The Sacred Stones (USA, Australia)"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    private const string PatchExt = ".apfe8";

    /// Explicit .apfe8 chosen by the user (Settings / drag-drop). Null = auto.
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
                 : new RomRequirement("Fire Emblem: The Sacred Stones", "GBA",
                       "Fire Emblem: The Sacred Stones (U)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Fire Emblem: The Sacred Stones", "GBA",
            "Fire Emblem: The Sacred Stones (U) — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apfe8 to a library copy ───────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Fire Emblem: The Sacred Stones] No AP patch (.apfe8) found — " +
                "launching the vanilla ROM. Generate the multiworld, then pick the " +
                "patch in Settings (or drop it under " +
                SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Fire Emblem: The Sacred Stones", patch, how, RomPath!,
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
