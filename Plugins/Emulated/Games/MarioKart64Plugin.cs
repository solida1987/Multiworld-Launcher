using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// MarioKart64Plugin — Archipelago integration for Mario Kart 64 (Nintendo 64),
// the apworld "Mario Kart 64", on the proven BizHawk Lua-pipe bridge (N64 core).
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED (the 448-location
// table in mk64.lua was GENERATED from worlds/mk64/Locations.py, the flag diff
// math + the valid-connection gate + the AP signature verified against Client.py;
// mock-verified through MoonSharp against synthetic big-endian N64 memory — 12/12
// checks green). items_handling = 0b001 — the PATCHED GAME grants its own
// locally-found items, so a SOLO seed plays fully and every check is reported in
// a multiworld. Delivering REMOTE multiworld items is the client's guarded RDRAM
// staging path; it is deferred (mk64.lua receive_item is a no-op) until it can be
// confirmed in-emulator, rather than shipped unverified.
//
// N64 NOTE: N64 is BIG-ENDIAN, and the mk64 client reads two BizHawk N64 memory
// domains — "RDRAM" (console work RAM: the live game-status byte, the received-
// item counter, and the 56-byte "locations unchecked" bit array) and "ROM" (the
// "MK64 ARCHIPELAGO" patch signature + the patched-in player name/seed). mk64.lua
// replicates the client's exact byte order (int.from_bytes big) and the inverted,
// LSB-first flag bits (a SET bit means UNCHECKED; the game clears a bit when a
// location is checked, and the client diffs against an all-0xFF baseline). N64
// runs on BizHawk here only — snes9x/mGBA/Mesen are other-system emulators and
// are NOT listed.
//
// AP PATCH (.apmk64 → .z64): a standard APProcedurePatch container (bsdiff4 +
// token data + CRC fixup), applied via the shared SnesApPatchHelper (resolve the
// seed's patch, validate the base ROM MD5, run apply_appatch.py on a library
// COPY — the original is never modified). Base ROM: the user's own Mario Kart 64
// (USA) cartridge dump (12 MB .z64), identified by CONTENT (size + MD5) — never
// by filename (§11).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MarioKart64Plugin : EmulatorPlugin
{
    public MarioKart64Plugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "mk64";
    public override string DisplayName => "Mario Kart 64";
    public override string Subtitle    => "N64 · Emulated";
    public override string ApWorldName => "Mario Kart 64";

    public override string Description =>
        "Mario Kart 64 is the original 3D kart racer. As one of eight Mushroom " +
        "Kingdom drivers, fire off shells and bananas, drift through hairpins for " +
        "mini-turbos, and risk the shortcuts on every course and cup. In the " +
        "Archipelago randomizer the karts, abilities, course unlocks and the " +
        "hundreds of item-box pickups scattered across the tracks all join the " +
        "multiworld pool — race to first place across the cups to complete your " +
        "goal.";

    public override string ThemeAccentColor => "#E52521";   // Mario red

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "N64";

    // §14: N64 runs on BizHawk here (the mk64 apworld's reference client is a
    // BizHawkClient). snes9x/mGBA/Mesen are SNES/GBA/NES emulators — not N64 —
    // so they are deliberately NOT offered for this game.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "mk64";

    /// mk64.lua reports checks + goal (table generated from the apworld; mock-
    /// verified). Remote-item delivery is the documented deferred piece.
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dump the randomizer is built for, by CONTENT
    /// (12 MB + MD5) — never by filename. Mario Kart 64 (USA) only; the patch
    /// base_checksum validates the exact dump before anything is written.
    /// (MK64ProcedurePatch.hash from worlds/mk64/Rom.py = the native big-endian
    /// .z64 dump, 12,582,912 bytes, SHA1 579C48E2…D5DD215E.)
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(12 * 1024 * 1024, "3a67d9986f54eb282924fca4cd5f6dff",
                        "Mario Kart 64 (USA) — .z64 (big-endian) dump"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    private const string PatchExt = ".apmk64";

    /// Explicit .apmk64 chosen by the user (Settings / drag-drop). Null = auto.
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
                 : new RomRequirement("Mario Kart 64", "N64",
                       "Mario Kart 64 (USA) — .z64 dump",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Mario Kart 64", "N64",
            "Mario Kart 64 (USA) — your original cartridge dump (.z64)",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apmk64 to a library copy ──────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Mario Kart 64] No AP patch (.apmk64) found — launching the " +
                "vanilla ROM. Generate the multiworld, then pick the patch in " +
                "Settings (or drop it under " +
                SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Mario Kart 64", patch, how, RomPath!,
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
