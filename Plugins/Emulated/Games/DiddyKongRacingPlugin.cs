using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// DiddyKongRacingPlugin — Archipelago integration for Diddy Kong Racing
// (Nintendo 64), on the proven BizHawk Lua-pipe bridge (N64 core).
//
// COMMUNITY WORLD. The apworld is zakwiz's "Diddy Kong Racing"
// (zakwiz/DiddyKongRacingAP, a fork of ArchipelagoMW/Archipelago — 22 releases,
// latest v1.1.4 on 2026-03-17, world_version not confirmed in this pass).
// game string "Diddy Kong Racing". SOURCE:
// https://github.com/zakwiz/DiddyKongRacingAP/releases
// NOT in AP-main.
//
// STATUS: STUB — ChecksImplemented = false. The world source has NOT been
// fully parsed in this pass (a second-pass deep dive of the DKR apworld's
// Client.py / Locations.py is needed before a real check table can be built).
// The BizHawk Lua module ("Diddy Kong Racing.lua") is a no-op stub that
// identifies the ROM by header signature, reports nothing, and never completes
// the goal. AP connection, ROM library management, and patch application all
// work exactly the same way as the other N64 plugins (Mario Kart 64, Paper
// Mario, Donkey Kong 64).
//
// WHAT NEEDS A SECOND PASS:
//   1. Parse worlds/dkr/Client.py (or equivalent) to extract the RDRAM
//      addresses for location flags, the goal/win condition, and the ROM
//      signature expected by the client.
//   2. Identify the patch container type and exact patch suffix the world
//      emits (recorded as ".apdkr" here — confirm against Rom.py).
//   3. Confirm the Diddy Kong Racing (USA) vanilla ROM MD5 and size.
//   4. Implement "Diddy Kong Racing.lua" with the real flag table, bit math,
//      and goal logic; flip ChecksImplemented to true.
//
// N64 NOTE: N64 is BIG-ENDIAN. Memory reads in the Lua module must use
// big-endian byte order from BizHawk's "RDRAM" and "ROM" domains (same as
// dk64.lua / mk64.lua / paper_mario.lua — see their headers for the pattern).
// N64 runs on BizHawk here only.
//
// AP PATCH (.apdkr → .z64): assumed APDeltaPatch or APProcedurePatch container
// (standard for AP N64 worlds), applied via the shared SnesApPatchHelper
// (resolve the seed's patch, validate the base ROM MD5, run apply_appatch.py
// on a library COPY — the original is never modified). Patch suffix and base
// ROM MD5 must be confirmed from the zakwiz/DiddyKongRacingAP source.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DiddyKongRacingPlugin : EmulatorPlugin
{
    public DiddyKongRacingPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "diddy_kong_racing";
    public override string DisplayName => "Diddy Kong Racing";
    public override string Subtitle    => "N64 · Emulated";
    public override string ApWorldName => "Diddy Kong Racing";

    public override string Description =>
        "Diddy Kong Racing is Rare's 1997 Nintendo 64 kart racer and adventure " +
        "game. Timber the Tiger's island has been invaded by the evil space pig " +
        "Wizpig, and Diddy Kong leads a team of racers across five worlds — " +
        "Dino Domain, Snowflake Mountain, Sherbet Island, Everfrost Peak and the " +
        "final Wizpig confrontations — in cars, hovercrafts and planes. In the " +
        "Archipelago randomizer the balloon trophies, boss keys, speed boosts, " +
        "vehicle unlocks and the countless golden balloons scattered across the " +
        "courses join the multiworld pool. Bring your own Diddy Kong Racing (USA) " +
        "Nintendo 64 ROM.";

    // ── Visual identity ───────────────────────────────────────────────────────

    public override string ThemeAccentColor => "#D4281C";   // Diddy's cap red

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "N64";

    // §14: N64 runs on BizHawk here. snes9x/mGBA/Mesen are SNES/GBA/NES
    // emulators — not N64 — so they are deliberately NOT offered for this game.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "Diddy Kong Racing";

    /// STUB: location check detection is not yet implemented. A second-pass
    /// deep dive of the zakwiz/DiddyKongRacingAP source is required to generate
    /// the real RDRAM flag table and goal logic. See header notes.
    public override bool ChecksImplemented => false;

    /// The vanilla Diddy Kong Racing (USA) cartridge dump, identified by
    /// CONTENT (size + MD5) — never by filename (§11). MD5 and exact size must
    /// be confirmed from worlds/dkr/Rom.py in the zakwiz fork; the values
    /// below are placeholders derived from the standard DKR USA .z64 dump and
    /// must be verified against the apworld's own base_checksum before shipping
    /// a production build.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        // NOTE: size and MD5 are placeholder — confirm from Rom.py base hash.
        new RomIdentity(16 * 1024 * 1024,          // 16,777,216 bytes (16 MiB)
                        null,
                        "Diddy Kong Racing (USA) — vanilla .z64 cartridge dump"),
    };

    // ── AP patch ──────────────────────────────────────────────────────────────

    // Patch suffix assumed ".apdkr" — confirm from worlds/dkr/Rom.py
    // patch_file_ending in the zakwiz/DiddyKongRacingAP source.
    private const string PatchExt  = ".apdkr";
    private const string ResultExt = ".z64";

    /// Explicit .apdkr chosen by the user (Settings / drag-drop). Null = auto.
    public string? ApPatchPath { get; private set; }

    protected override void OnSavingSettings(IDictionary<string, object?> bag)
        => bag["ap_patch_path"] = ApPatchPath;

    protected override void OnLoadingSettings(JsonElement root)
    {
        if (root.TryGetProperty("ap_patch_path", out var el))
            ApPatchPath = el.GetString();
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
                 : new RomRequirement("Diddy Kong Racing", "N64",
                       "Diddy Kong Racing (USA) — .z64 dump",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Diddy Kong Racing", "N64",
            "Diddy Kong Racing (USA) — your original cartridge dump (.z64)",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apdkr to a library copy ──────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Diddy Kong Racing] No AP patch (.apdkr) found — launching the " +
                "vanilla ROM. Checks CANNOT WORK on a vanilla ROM: generate the " +
                "multiworld with the Diddy Kong Racing apworld, then pick the patch " +
                "in Settings (or drop it under " +
                SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        if (RomPath == null || !File.Exists(RomPath))
        {
            SessionRomNote =
                "[Diddy Kong Racing] No base ROM set — import your vanilla USA " +
                "cartridge dump in Settings, then relaunch.";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Diddy Kong Racing", patch, how, RomPath!,
            RomLibraryDirectory, ResultExt, ct);
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
