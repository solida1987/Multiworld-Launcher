using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// Castlevania64Plugin — Archipelago integration for Castlevania (Nintendo 64),
// the apworld "Castlevania 64", on the proven BizHawk Lua-pipe bridge (N64 core).
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED (the 352-location
// table in cv64.lua was GENERATED from worlds/cv64/locations.py, save-flag bit
// math + the Gameplay/Credits gate + the AP signature verified against client.py;
// mock-verified through MoonSharp against synthetic big-endian N64 memory).
// items_handling = 0b001 — the PATCHED GAME grants its own locally-found items,
// so a SOLO seed plays fully and every check is reported in a multiworld.
// Delivering REMOTE multiworld items is the client's guarded RDRAM text-box-
// injection path; it is deferred (cv64.lua receive_item is a no-op) until it can
// be confirmed in-emulator, rather than shipped unverified.
//
// N64 NOTE: N64 is BIG-ENDIAN, and the cv64 client reads two BizHawk N64 memory
// domains — "RDRAM" (console work RAM: game state + the save struct holding the
// location-checked flags + the goal cutscene byte) and "ROM" (the cartridge name
// + the "ARCHIPELAGO1" patch signature). cv64.lua replicates the client's exact
// byte order (int.from_bytes big) and MSB-first flag bits. N64 runs on BizHawk
// here only — snes9x/mGBA/Mesen are other-system emulators and are NOT listed.
//
// AP PATCH (.apcv64 → .z64): a standard APProcedurePatch container, applied via
// the shared SnesApPatchHelper (resolve the seed's patch, validate the base ROM
// MD5, run apply_appatch.py on a library COPY — the original is never modified).
// Base ROM: the user's own Castlevania (USA) v1.0 cartridge dump (12 MB),
// identified by CONTENT (size + MD5) — never by filename (§11).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Castlevania64Plugin : EmulatorPlugin
{
    public Castlevania64Plugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "cv64";
    public override string DisplayName => "Castlevania 64";
    public override string Subtitle    => "N64 · Emulated";
    public override string ApWorldName => "Castlevania 64";

    public override string Description =>
        "Castlevania for the Nintendo 64 is the franchise's first 3D entry. As " +
        "whip-wielding Reinhardt Schneider or sorceress Carrie Fernandez, brave " +
        "the traps and monsters of Dracula's castle. In the Archipelago " +
        "randomizer, the items scattered across every stage join the multiworld " +
        "pool — reach Dracula's chamber and end his rule to complete your goal.";

    public override string ThemeAccentColor => "#8B1A2B";   // Castlevania 64 crimson

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "N64";

    // §14: N64 runs on BizHawk here (the cv64 apworld's reference client is a
    // BizHawkClient). snes9x/mGBA/Mesen are SNES/GBA/NES emulators — not N64 —
    // so they are deliberately NOT offered for this game.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "cv64";

    /// cv64.lua reports checks + goal (table generated from the apworld; mock-
    /// verified). Remote-item delivery is the documented deferred piece.
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dump the randomizer is built for, by CONTENT
    /// (12 MB + MD5) — never by filename. Castlevania (USA) v1.0 only; the
    /// patch base_checksum validates the exact dump before anything is written.
    /// (CV64_US_10_HASH from worlds/cv64/rom.py; 12,582,912-byte .z64.)
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(12 * 1024 * 1024, "1cc5cf3b4d29d8c3ade957648b529dc1",
                        "Castlevania (USA) v1.0"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    private const string PatchExt = ".apcv64";

    /// Explicit .apcv64 chosen by the user (Settings / drag-drop). Null = auto.
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
                 : new RomRequirement("Castlevania 64", "N64",
                       "Castlevania (USA) v1.0",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Castlevania 64", "N64",
            "Castlevania (USA) v1.0 — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apcv64 to a library copy ──────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Castlevania 64] No AP patch (.apcv64) found — launching the " +
                "vanilla ROM. Generate the multiworld, then pick the patch in " +
                "Settings (or drop it under " +
                SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Castlevania 64", patch, how, RomPath!,
            RomLibraryDirectory, ".z64", ct);
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
