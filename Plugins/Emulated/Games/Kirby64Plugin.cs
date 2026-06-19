using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// Kirby64Plugin — Archipelago integration for Kirby 64: The Crystal Shards
// (Nintendo 64), the apworld "Kirby 64 - The Crystal Shards", on the proven BizHawk
// Lua-pipe bridge (N64 core).
//
// WORLD SOURCE: Kirby 64 is NOT in ArchipelagoMW/Archipelago main — it ships on the
// k64cs branch of the maintainer's fork
// https://github.com/Silvris/Archipelago/tree/k64cs/worlds/k64
// (worlds/k64; archipelago.json game "Kirby 64 - The Crystal Shards",
// world_version 0.3.2, minimum_ap_version 0.6.4, author Silvris — an AP core dev).
// The reference client worlds/k64/client.py is a real worlds._bizhawk.BizHawkClient.
// kirby_64_-_the_crystal_shards.lua records the exact source URL.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED. The 959-location
// table in kirby_64_-_the_crystal_shards.lua was GENERATED from the apworld source
// (the location dicts in locations.py, default_levels in regions.py, the
// `consumables` dict in consumable_info.py — all parsed, not hand-copied), and the
// save-flag / crystal-bit / consumable-bit math + the "-HALKEN--KIRBY4-" save
// signature gate + the ROM identity gate + the goal are replicated exactly from
// client.py game_watcher; mock-verified through MoonSharp against synthetic
// big-endian N64 RDRAM + ROM (boss, stage-complete, crystal-shard low/high-bit, and
// the 64-bit-mask consumable edge case, plus the gating). items_handling = 0b111
// (FULL remote) — the AP client delivers EVERY item (including the slot's own) by
// writing into the live RDRAM save (recv-index handshake at save+0x174, then
// copy-ability/friend/crystal/life/health/star writes + the DeathLink kill path).
// That guarded RDRAM-write path is the documented deferred piece
// (kirby_64_-_the_crystal_shards.lua receive_item is a no-op) until it can be
// confirmed in-emulator, rather than shipped unverified — a wrong RDRAM write
// corrupts the live save. Checks + goal flow regardless.
//
// N64 NOTE: N64 is BIG-ENDIAN, and the K64 client reads two BizHawk N64 domains —
// "RDRAM" (console work RAM: the save struct holding every location flag + the goal
// byte + the signature, and the consumable bitfield) and "ROM" (the cartridge:
// "Kirby64"/"K64" AP identity strings AND the per-player stage layout table the
// basepatch writes at 0x1FFF230, read so stage shuffle remaps the right location id
// per slot). kirby_64_-_the_crystal_shards.lua replicates the client's exact byte
// order (int.from_bytes big — with the one deliberate little-endian crystal_array
// read the client itself uses). N64 runs on BizHawk here only — snes9x/mGBA/Mesen
// are other-system emulators and are NOT listed.
//
// AP PATCH (.apk64cs → .z64): a standard APProcedurePatch container (apply_basepatch
// bsdiff4 + apply_tokens + calc_6103_crc), applied via the shared SnesApPatchHelper
// (resolve the seed's patch, validate the base ROM MD5, run apply_appatch.py on a
// library COPY — the original is never modified). Base ROM: the user's own Kirby 64
// - The Crystal Shards (USA) cartridge dump (24 MiB), identified by CONTENT
// (size + MD5) — never by filename (§11). (K64UHASH from worlds/k64/rom.py.)
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Kirby64Plugin : EmulatorPlugin
{
    public Kirby64Plugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "kirby_64_crystal_shards";
    public override string DisplayName => "Kirby 64: The Crystal Shards";
    public override string Subtitle    => "N64 · Emulated";
    public override string ApWorldName => "Kirby 64 - The Crystal Shards";

    public override string Description =>
        "Kirby 64: The Crystal Shards is the pink puffball's first 3D adventure on " +
        "the Nintendo 64. After Dark Matter shatters Ripple Star's great crystal, " +
        "Kirby and friends travel six worlds to recover the scattered shards and " +
        "restore peace. Kirby's signature trick here is combining two copy " +
        "abilities into wild new powers. In the Archipelago randomizer, the crystal " +
        "shards, copy abilities, friends, and the many consumables across every " +
        "stage join the multiworld pool — gather enough crystals, clear the worlds, " +
        "and defeat Zero-Two to complete your run. Kirby 64 is randomized with its " +
        "Archipelago client, which patches your own USA ROM into the seed's .z64.";

    public override string ThemeAccentColor => "#E85C9A";   // Kirby pink

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "N64";

    // §14: N64 runs on BizHawk here (the K64 apworld's reference client is a
    // worlds._bizhawk BizHawkClient — its setup guide requires "BizHawk 2.7 or
    // later, 2.10 recommended"). snes9x/mGBA/Mesen are SNES/GBA/NES emulators —
    // not N64 — so they are deliberately NOT offered for this game.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "kirby_64_-_the_crystal_shards";

    /// kirby_64_-_the_crystal_shards.lua reports checks + goal (959-flag table
    /// generated from the apworld source; save/crystal/consumable bit math + the
    /// signature gates from the reference client; mock-verified). Remote-item
    /// delivery is the documented deferred piece (items_handling = 0b111,
    /// client-driven RDRAM save writes).
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dump the randomizer is built for, by CONTENT
    /// (24 MiB + MD5) — never by filename. Kirby 64 - The Crystal Shards (USA)
    /// only; the patch base_checksum validates the exact dump before anything is
    /// written. (K64UHASH from worlds/k64/rom.py; 25,165,824-byte .z64.)
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(24 * 1024 * 1024, "d33e4254336383a17ff4728360562ada",
                        "Kirby 64 - The Crystal Shards (USA)"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    private const string PatchExt = ".apk64cs";

    /// Explicit .apk64cs chosen by the user (Settings / drag-drop). Null = auto.
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
                 : new RomRequirement("Kirby 64: The Crystal Shards", "N64",
                       "Kirby 64 - The Crystal Shards (USA)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Kirby 64: The Crystal Shards", "N64",
            "Kirby 64 - The Crystal Shards (USA) — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apk64cs to a library copy ─────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Kirby 64] No AP patch (.apk64cs) found — launching the vanilla " +
                "ROM. Generate the multiworld, then pick the patch in Settings " +
                "(or drop it under " + SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Kirby 64", patch, how, RomPath!,
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
