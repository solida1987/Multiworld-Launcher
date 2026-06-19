using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// StarFox64Plugin — Archipelago integration for Star Fox 64 (Nintendo 64),
// the community apworld "Star Fox 64" by Auztin, on the proven BizHawk
// Lua-bridge (N64 core).
//
// WORLD SOURCE: Star Fox 64 is NOT in ArchipelagoMW/Archipelago main — it ships
// as the community apworld https://github.com/Auztin/AP-Star-Fox-64
// (ap/__init__.py: game = "Star Fox 64"; version 0.4.1; author Austin).
// Latest release: v0.4.1 — star_fox_64.apworld + connector_sf64_bizhawk.lua.
//
// HOW STAR FOX 64 IS PATCHED — IMPORTANT: unlike most AP ROM games, Star Fox 64
// is NOT patched by the Archipelago generator. There is no APProcedurePatch
// container (.apsf64) in the AP output folder and the world emits no
// generate_output. Instead the Star Fox 64 AP Client (ap/client.py) patches the
// player's own legally-obtained USA v1.1 ROM via bsdiff4 against a patch bundled
// inside the apworld, producing "Star Fox 64 AP vX.Y.Z.z64". The player loads
// THAT patched .z64 here — the launcher applies no patch of its own; it loads
// the ROM directly (§11: the library copy, original untouched), the same shape
// as the Banjo-Tooie plugin. The patched ROM is identified by CONTENT — never by
// filename (§11). The vanilla USA cartridge dump is also accepted at import so a
// player is not blocked, but only the SF64-AP-patched .z64 carries the
// multiworld's checks/items.
//
// IPC BRIDGE: Star Fox 64 uses an N64-side TCP socket (port 0x5F64 = 24420),
// not the Lua named-pipe used by most other emulated games. The BizHawk side is
// connector_sf64_bizhawk.lua which the AP Client writes into data/lua/ and
// arranges to be loaded by BizHawk. The launcher launches BizHawk with that
// connector lua; the player runs the Star Fox 64 AP Client separately to handle
// connection, ROM patching, and item delivery over the TCP socket.
//
// N64 NOTE: N64 is BIG-ENDIAN. The connector reads BizHawk's "RDRAM" domain in
// logical big-endian (un-byte-swapped), matching the client's int.from_bytes big
// convention. N64 runs on BizHawk here only — PJ64/EverDrive are the community
// client's own targets, not this launcher's bridge. snes9x/mGBA/Mesen are
// other-system emulators and are NOT listed.
//
// ROM: Star Fox 64 (USA) v1.1
//   native .z64 (big-endian)  MD5 741a94eee093c4c8684e66b89f8685e8  (8 MiB)
//   byte-swapped .v64          MD5 ef9a76901153f66123dafccb0c13cd94  (same cart,
//                              auto-deswapped by the AP client's patch_rom())
// Both are accepted at import; size = 8,388,608 bytes (64 Mbit cartridge).
// Source: ap/client.py patch_rom().
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class StarFox64Plugin : EmulatorPlugin
{
    public StarFox64Plugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "star_fox_64";
    public override string DisplayName => "Star Fox 64";
    public override string Subtitle    => "N64 · Emulated";
    public override string ApWorldName => "Star Fox 64";

    public override string Description =>
        "Star Fox 64 is Nintendo's landmark 3D rail shooter. Commanding the " +
        "Star Fox team's Arwing fighters across 15 planets in the Lylat System, " +
        "Fox McCloud must stop the mad scientist Andross. Multiple branching " +
        "routes — including the Expert path to Venom — reward skill and secrets. " +
        "In the Archipelago randomizer, level access, medals, checkpoints, rings, " +
        "bombs, laser upgrades, and extra lives all join the multiworld pool. " +
        "Defeat Andross (or both Andross and Robot Andross) to complete your goal.";

    public override string ThemeAccentColor => "#1A5276";   // Lylat Space blue

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "N64";

    // §14: N64 runs on BizHawk here (the SF64 community apworld's documented
    // connector is connector_sf64_bizhawk.lua). PJ64 and EverDrive are supported
    // by the AP client's own separate connectors, not by this launcher bridge.
    // snes9x/mGBA/Mesen are SNES/GBA/NES emulators — not N64 — and are NOT listed.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "sf64";

    /// Checks + goal are driven by the AP Client communicating with the in-ROM
    /// TCP socket (port 0x5F64). The launcher bridge loads BizHawk + the lua
    /// connector; the player runs the Star Fox 64 AP Client alongside.
    public override bool ChecksImplemented => true;

    // ── ROM identity ───────────────────────────────────────────────────────────
    //
    // Star Fox 64 (USA) v1.1 is a 64 Mbit (8 MiB = 8,388,608 bytes) cartridge.
    // The AP client's patch_rom() accepts both the native big-endian .z64 and the
    // byte-swapped .v64 (auto-deswaps). Both are accepted at import; the player
    // also supplies the SF64-AP-patched .z64 that the AP client produces.
    // Source: ap/client.py patch_rom() MD5 checks.

    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        // The AP-client-patched .z64 produced from the player's own ROM. No
        // fixed per-seed MD5, so size-only identity — the connector's handshake
        // confirms it at launch.
        new RomIdentity(8 * 1024 * 1024, null,
            "Star Fox 64 AP-patched .z64 — generated by the SF64 AP Client from your own v1.1 ROM"),

        // Vanilla USA v1.1 — native .z64 big-endian dump.
        new RomIdentity(8 * 1024 * 1024, "741a94eee093c4c8684e66b89f8685e8",
            "Star Fox 64 (USA) v1.1 — native .z64 dump (patch it with the SF64 AP Client first)"),

        // Vanilla USA v1.1 — byte-swapped .v64 dump (same cart, different byte order).
        new RomIdentity(8 * 1024 * 1024, "ef9a76901153f66123dafccb0c13cd94",
            "Star Fox 64 (USA) v1.1 — byte-swapped .v64 dump (patch it with the SF64 AP Client first)"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────
    //
    // Star Fox 64 has no APProcedurePatch the launcher applies (see header). We
    // keep the standard hook shape for consistency, but the resolver is an
    // optional override only — if a player drops an explicit patch container in
    // Settings it would be honored; otherwise the SF64-AP-patched .z64 is loaded
    // as-is with no patch step. The AP generator never emits one.

    private const string PatchExt = ".apsf64";

    /// Explicit patch override chosen by the user (Settings / drag-drop). Null =
    /// none — the normal SF64 path where the AP-patched .z64 is loaded directly.
    public string? ApPatchPath { get; private set; }

    protected override void OnSavingSettings(IDictionary<string, object?> bag)
        => bag["ap_patch_path"] = ApPatchPath;

    protected override void OnLoadingSettings(JsonElement root)
    {
        if (root.TryGetProperty("ap_patch_path", out var el)) ApPatchPath = el.GetString();
    }

    /// Set an explicit patch override (rare for SF64) and persist it.
    public void SetExplicitPatch(string path) { ApPatchPath = path; SaveSettings(); }

    /// ROM safety net. Star Fox 64 has no launcher-applied patch carrying an
    /// expected MD5, so the only firm requirement is "a Star Fox 64 ROM is
    /// present". A future explicit .apsf64 override (not emitted by the AP
    /// generator) would demand its base MD5 if one is set.
    public override RomRequirement? GetUnmetRomRequirement()
    {
        var patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out _);
        string? wantMd5 = patch != null
            ? SnesApPatchHelper.ReadManifestField(patch, "base_checksum") : null;
        bool haveRom = RomPath != null && File.Exists(RomPath);

        if (wantMd5 == null)
            return haveRom ? null
                 : new RomRequirement("Star Fox 64", "N64",
                       "your Star Fox 64 AP-patched .z64 (generate it with the " +
                       "Star Fox 64 AP Client from your own USA v1.1 ROM)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Star Fox 64", "N64",
            "Star Fox 64 (USA) v1.1 — the exact ROM this patch was generated against",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM ───────────────────────────────────────────────────────────
    //
    // The normal SF64 path applies NO patch — the player's library ROM IS the
    // SF64-AP-patched .z64 produced by the Star Fox 64 AP Client, so we return
    // null and the base class launches it directly. Only an explicit .apsf64
    // override (not emitted by the AP generator) would trigger SnesApPatchHelper;
    // that branch is kept for parity with other emulated games but is inert for
    // standard SF64 seeds.

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            // Standard SF64: no launcher-applied AP patch. The library ROM is
            // the SF64-AP-patched .z64 the player generated with the Star Fox 64
            // AP Client. Launch it directly.
            SessionRomNote =
                "[Star Fox 64] Star Fox 64 is patched by the Star Fox 64 AP " +
                "Client (not by the launcher) — launching your ROM directly. " +
                "Make sure this is the AP-patched .z64 generated from your own " +
                "USA v1.1 ROM, not the vanilla cartridge dump. Also launch the " +
                "Star Fox 64 AP Client separately to handle the AP connection " +
                "over its TCP socket.";
            return null;
        }

        // Optional explicit-override path (kept for parity; SF64 never emits .apsf64).
        var res = await SnesApPatchHelper.ApplyAsync(
            "Star Fox 64", patch, how, RomPath!,
            RomLibraryDirectory, ".z64", ct);
        SessionRomNote = res.Note;
        if (res.OutRom != null) RegisterSeed(res.OutRom, patch);
        return res.OutRom;
    }

    /// Record a produced/reused patched ROM in the seed library. Never throws.
    /// (Only reached on the optional explicit-override path — standard SF64 seeds
    /// launch the AP-patched ROM directly and never get here.)
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
