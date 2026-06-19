using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// PaperMarioPlugin — Archipelago integration for Paper Mario (Nintendo 64), the
// apworld "Paper Mario" (PMR — Paper Mario 64 Randomizer), on the proven BizHawk
// Lua-pipe bridge (N64 core).
//
// WORLD SOURCE: Paper Mario is NOT in ArchipelagoMW/Archipelago main — it ships in
// the community apworld https://github.com/JKBSunshine/PMR_APWorld (the PMR
// randomizer's AP world; client.py game "Paper Mario", system "N64", patch_suffix
// ".appm64"). paper_mario.lua records the exact source URLs.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED. The 711-location
// flag table in paper_mario.lua (176 ModFlag + 535 GameFlag) was GENERATED from
// the apworld source — data/data.py `checks_table` (name → (flag_type, flag_id))
// joined to Locations.py's location_name_to_id (location_id_prefix 8112000000 +
// index into data/LocationsList.py's OrderedDict) — and the get_flag_value bit
// math (re-derived to a static (byte,bit) per location and cross-checked against
// the reference function on 20,000 random flags) + the GOAL_FLAG goal logic are
// replicated exactly from client.py game_watcher; mock-verified through MoonSharp
// against synthetic big-endian N64 RDRAM/ROM. items_handling = 0b101 — the server
// does NOT send this slot's own locally-found items, so a SOLO seed plays fully
// and every check is reported in a multiworld. Delivering REMOTE multiworld items
// is the client's guarded RDRAM path (write the next item id into KEY_RECV_BUFFER
// under an empty-buffer + received-sequence handshake); it is deferred
// (paper_mario.lua receive_item is a no-op) until it can be confirmed in-emulator,
// rather than shipped unverified — a wrong RDRAM write corrupts the live game.
// Checks + goal flow regardless.
//
// N64 NOTE: N64 is BIG-ENDIAN, and the PMR client reads two BizHawk N64 memory
// domains — "RDRAM" (console work RAM: the game-mode byte + the Mod-Flag and
// Game-Flag bitmaps that hold every location-checked flag and the goal flag) and
// "ROM" (the "PAPER MARIO         " cartridge name + the b'PMDB' patch magic).
// Unlike Banjo-Tooie there is NO pointer-chase — the bitmaps live at fixed
// physical RDRAM offsets (MF 0x357000, GF 0x0DBC70). paper_mario.lua replicates
// the client's exact byte/bit mapping and LSB-first flag bits. N64 runs on BizHawk
// here only — snes9x/mGBA/Mesen are other-system emulators and are NOT listed.
//
// AP PATCH (.appm64 → .z64): a standard APDeltaPatch container (Rom.py
// PaperMarioDeltaPatch(APDeltaPatch), patch_file_ending ".appm64",
// result_file_ending ".z64"; the world's generate_output writes it via
// patch.write()), applied via the shared SnesApPatchHelper (resolve the seed's
// patch, validate the base ROM MD5, run apply_appatch.py on a library COPY — the
// original is never modified). Base ROM: the user's own Paper Mario (USA)
// cartridge dump (32 MiB), identified by CONTENT (size + MD5) — never by filename
// (§11). (PaperMarioDeltaPatch.hash from Rom.py; 33,554,432-byte .z64.)
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PaperMarioPlugin : EmulatorPlugin
{
    public PaperMarioPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "paper_mario";
    public override string DisplayName => "Paper Mario";
    public override string Subtitle    => "N64 · Emulated";
    public override string ApWorldName => "Paper Mario";

    public override string Description =>
        "Paper Mario for the Nintendo 64 is the paper-thin RPG adventure where " +
        "Mario sets out to rescue the seven Star Spirits, free Princess Peach, and " +
        "take back the Star Rod from Bowser. In the Archipelago randomizer, the " +
        "badges, items, partners, and the many secrets scattered across the " +
        "Mushroom Kingdom join the multiworld pool — defeat Bowser (or open Star " +
        "Way, per your seed's goal) to complete your run. Paper Mario is randomized " +
        "with the Paper Mario 64 Randomizer Archipelago world, which patches your " +
        "own USA ROM into the seed's .z64.";

    public override string ThemeAccentColor => "#E03030";   // Paper Mario red

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "N64";

    // §14: N64 runs on BizHawk here (the PMR apworld's reference client is a
    // BizHawkClient — the Generic BizHawk Client target). snes9x/mGBA/Mesen are
    // SNES/GBA/NES emulators — not N64 — so they are deliberately NOT offered.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "paper_mario";

    /// paper_mario.lua reports checks + goal (711-flag table generated from the
    /// apworld; get_flag_value bit math + GOAL_FLAG from client.py; mock-verified).
    /// Remote-item delivery is the documented deferred piece (items_handling =
    /// 0b101 — the server withholds this slot's own items, so solo play is full).
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dump the randomizer is built for, by CONTENT
    /// (32 MiB + MD5) — never by filename. Paper Mario (USA) only; the patch
    /// base_checksum validates the exact dump before anything is written.
    /// (PaperMarioDeltaPatch.hash from PMR_APWorld Rom.py; 33,554,432-byte .z64.)
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(32 * 1024 * 1024, "a722f8161ff489943191330bf8416496",
                        "Paper Mario (USA)"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    private const string PatchExt = ".appm64";

    /// Explicit .appm64 chosen by the user (Settings / drag-drop). Null = auto.
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
                 : new RomRequirement("Paper Mario", "N64",
                       "Paper Mario (USA)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Paper Mario", "N64",
            "Paper Mario (USA) — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .appm64 to a library copy ──────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Paper Mario] No AP patch (.appm64) found — launching the " +
                "vanilla ROM. Generate the multiworld, then pick the patch in " +
                "Settings (or drop it under " +
                SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Paper Mario", patch, how, RomPath!,
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
