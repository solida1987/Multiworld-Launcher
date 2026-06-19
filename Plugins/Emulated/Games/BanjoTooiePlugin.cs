using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// BanjoTooiePlugin — Archipelago integration for Banjo-Tooie (Nintendo 64), on the
// proven BizHawk Lua-pipe bridge (N64 core).
//
// WORLD SOURCE: Banjo-Tooie is NOT in ArchipelagoMW/Archipelago main — it ships in
// the community apworld https://github.com/jjjj12212/Archipelago-BanjoTooie
// (worlds/banjo_tooie; world_version 4.12.0, archipelago.json game "Banjo-Tooie",
// minimum_ap_version 0.6.7; authors jjjj12212 / g0goTBC / Austin, emu-loader by
// Umed). banjo_tooie.lua records the exact source URLs.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED. The 1069-location
// flag table in banjo_tooie.lua was GENERATED from
// worlds/banjo_tooie/client/_flag_data.py (every (category, flag_type, addr, bit)
// tuple), and the BTHACK pointer-chase + LSB-first flag bit math + the
// victory_condition goal logic are replicated exactly from the reference client
// (client/state.py BTHReader, client/game.py check_victory, BTClient.py
// validate_bt_signature); mock-verified through MoonSharp against synthetic
// big-endian N64 RDRAM. items_handling = 0b111 (FULL remote) — the AP client
// delivers EVERY item (including the slot's own) by writing per-item counts into
// the game's pc_items buffer; that guarded RDRAM-write path is the documented
// deferred piece (banjo_tooie.lua receive_item is a no-op) until it can be
// confirmed in-emulator, rather than shipped unverified — a wrong RDRAM write
// corrupts the live game state. Checks + goal flow regardless.
//
// N64 NOTE: N64 is BIG-ENDIAN. The Lua module reads BizHawk's "RDRAM" N64 domain
// (logical big-endian, un-byte-swapped — exactly as the shipped cv64/mk64 N64
// modules read it), chasing the BTHACK anchor pointer (physical RDRAM 0x400000 →
// struct → real/fake/nest/signpost flag buffers). The reference client is a
// direct-process-memory EmuLoaderClient (PJ64/Mupen/BizHawk/ares/gopher); its
// host-side byte-swap-within-word address rotation is a Mupen-LE storage artifact
// that BizHawk's logical Lua domain already accounts for, so the module reads
// straight big-endian (see banjo_tooie.lua MEMORY MODEL). N64 runs on BizHawk here
// only (the BT community's reference + tested target; PJ64/RMG/etc. are the
// external native client's own targets, not this launcher's bridge).
//
// HOW BANJO-TOOIE IS PATCHED — IMPORTANT: unlike most AP ROM games, Banjo-Tooie is
// NOT patched by the Archipelago generator. There is no APProcedurePatch container
// (.apbt) in the AP output folder and the world emits no generate_output. The
// Banjo-Tooie AP client (worlds/banjo_tooie/BTClient.py) bsdiff4-patches the
// player's own legally-obtained USA ROM against a patch bundled inside the apworld,
// producing "Banjo-Tooie AP <version>.z64". The player loads THAT patched .z64 here
// — the launcher applies no patch of its own; it loads the ROM directly (§11: the
// library copy, original untouched), same shape as the FF1 plugin. The patched ROM
// carries the BTHACK signature banjo_tooie.lua validates; it is identified by
// CONTENT (32 MiB + the runtime signature) — never by filename (§11). The vanilla
// USA cartridge dump is also accepted at import so a player is not blocked, but
// only the BT-AP-patched .z64 actually carries the multiworld's checks/items.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class BanjoTooiePlugin : EmulatorPlugin
{
    public BanjoTooiePlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "banjo_tooie";
    public override string DisplayName => "Banjo-Tooie";
    public override string Subtitle    => "N64 · Emulated";
    public override string ApWorldName => "Banjo-Tooie";

    public override string Description =>
        "Banjo-Tooie is the Nintendo 64 sequel to Banjo-Kazooie — a sprawling " +
        "3D platform-adventure where the bear-and-bird duo explore eight huge, " +
        "interconnected worlds to stop Gruntilda and her sisters. In the " +
        "Archipelago randomizer, Jiggies, Jinjos, moves, notes, and the many " +
        "collectibles spread across the Isle o' Hags join the multiworld pool — " +
        "defeat HAG-1 (or meet your chosen token goal) to complete your run. " +
        "Banjo-Tooie is randomized with the Banjo-Tooie Archipelago client, " +
        "which patches your own USA ROM into the seed's .z64.";

    public override string ThemeAccentColor => "#1FA539";   // Banjo-Tooie collectible green

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "N64";

    // §14: N64 runs on BizHawk here (BizHawk 2.10+ is the BT community's tested,
    // recommended client target). snes9x/mGBA/Mesen are SNES/GB-GBA/NES emulators
    // — not N64 — so they are deliberately NOT offered for this game.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "banjo_tooie";

    /// banjo_tooie.lua reports checks + goal (1069-flag table generated from the
    /// apworld; pointer-chase + bit math from the reference client; mock-verified).
    /// Remote-item delivery is the documented deferred piece (items_handling =
    /// 0b111, client-driven pc_items writes).
    public override bool ChecksImplemented => true;

    /// The ROM the player loads is the Banjo-Tooie-AP-patched .z64 produced by the
    /// Banjo-Tooie client (BTClient.py bsdiff4-patches a USA ROM), identified by
    /// CONTENT — never by filename (§11). Banjo-Tooie (USA) is a 256 Mbit cart =
    /// 33,554,432 bytes (32 MiB); the patch keeps that size, and every seed differs
    /// (no fixed patched MD5), so size is the stable detector and banjo_tooie.lua's
    /// BTHACK signature confirms it is really an AP-patched BT ROM at launch. The
    /// vanilla USA dumps are also accepted at import so a player pointing at the
    /// un-patched file is not blocked — but only the BT-AP-patched .z64 carries the
    /// multiworld's checks/items. (Vanilla USA MD5s from BTClient.patch_rom:
    /// 40e98faa…e4 = native .z64 big-endian; ca0df738…d9 = byte-swapped .v64.)
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(32 * 1024 * 1024, null,
            "Banjo-Tooie (USA) — your Banjo-Tooie-AP-patched .z64 (make it with the Banjo-Tooie Archipelago client)"),
        new RomIdentity(32 * 1024 * 1024, "40e98faa24ac3ebe1d25cb5e5ddf49e4",
            "Banjo-Tooie (USA) — vanilla .z64 cartridge dump (patch it with the Banjo-Tooie Archipelago client first)"),
        new RomIdentity(32 * 1024 * 1024, "ca0df738ae6a16bfb4b46d3860c159d9",
            "Banjo-Tooie (USA) — vanilla byte-swapped .v64 dump (patch it with the Banjo-Tooie Archipelago client first)"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────
    //
    // Banjo-Tooie has no APProcedurePatch the launcher applies (see header). We keep
    // the standard hook shape for consistency, but the resolver is only an optional
    // override: if a player ever drops an explicit patch container in Settings it
    // would be honored; otherwise the BT-AP-patched .z64 is loaded directly with no
    // patch step. The AP generator never emits one.

    private const string PatchExt = ".apbt";

    /// Explicit patch override chosen by the user (Settings / drag-drop). Null =
    /// none — the normal BT path, the patched .z64 is loaded as-is.
    public string? ApPatchPath { get; private set; }

    protected override void OnSavingSettings(IDictionary<string, object?> bag)
        => bag["ap_patch_path"] = ApPatchPath;

    protected override void OnLoadingSettings(JsonElement root)
    {
        if (root.TryGetProperty("ap_patch_path", out var el)) ApPatchPath = el.GetString();
    }

    /// Set an explicit patch override (rare for BT) and persist it.
    public void SetExplicitPatch(string path) { ApPatchPath = path; SaveSettings(); }

    /// ROM safety net. Banjo-Tooie has no launcher-applied patch carrying an
    /// expected MD5, so the only requirement we can surface is "a Banjo-Tooie ROM is
    /// present" — the player supplies the BT-AP-patched .z64 themselves. A future
    /// explicit .apbt override (which the AP generator does not emit) would still
    /// demand its base MD5 if one is set.
    public override RomRequirement? GetUnmetRomRequirement()
    {
        var patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out _);
        string? wantMd5 = patch != null
            ? SnesApPatchHelper.ReadManifestField(patch, "base_checksum") : null;
        bool haveRom = RomPath != null && File.Exists(RomPath);

        if (wantMd5 == null)
            return haveRom ? null
                 : new RomRequirement("Banjo-Tooie", "N64",
                       "your Banjo-Tooie-AP-patched .z64 (make it with the Banjo-Tooie " +
                       "Archipelago client from your own USA ROM)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Banjo-Tooie", "N64",
            "Banjo-Tooie (USA) — the exact ROM this patch was generated against",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM ───────────────────────────────────────────────────────────
    //
    // The normal BT path applies NO patch — the player's library ROM IS the
    // BT-AP-patched .z64 they produced with the Banjo-Tooie client, so we return
    // null and the base class launches it directly. Only an explicit .apbt override
    // (not emitted by the AP generator) would trigger SnesApPatchHelper; that branch
    // is kept for parity with the other emulated games but is inert for standard
    // BT seeds.

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            // Standard BT: no launcher-applied AP patch. The library ROM is the
            // BT-AP-patched .z64 the player generated with the Banjo-Tooie client.
            SessionRomNote =
                "[Banjo-Tooie] Banjo-Tooie is patched by the Banjo-Tooie Archipelago " +
                "client (not by the launcher) — launching your ROM directly. Make " +
                "sure this is the BT-AP-patched .z64 generated from your own USA ROM, " +
                "not the vanilla cartridge dump.";
            return null;
        }

        // Optional explicit-override path (kept for parity; BT never emits .apbt).
        var res = await SnesApPatchHelper.ApplyAsync(
            "Banjo-Tooie", patch, how, RomPath!,
            RomLibraryDirectory, ".z64", ct);
        SessionRomNote = res.Note;
        if (res.OutRom != null) RegisterSeed(res.OutRom, patch);
        return res.OutRom;
    }

    /// Record a produced/reused patched ROM in the seed library. Never throws.
    /// (Only reached on the optional explicit-override path — standard BT seeds
    /// launch the BT-AP-patched ROM directly and never get here.)
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
