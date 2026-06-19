using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// LinksAwakeningDXPlugin — Archipelago integration for The Legend of Zelda:
// Link's Awakening DX (Game Boy Color), on the proven BizHawk Lua-pipe bridge.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED. The 254-location
// table in ladx.lua was GENERATED (not hand-copied) by a script that imports the
// real worlds/ladx LADXR/checkMetadata.py table and replicates, byte-for-byte,
// BOTH source functions: Locations.py get_locations_to_id() (ap id math) AND
// Tracker.py LocationTracker.__init__ (flag address + mask + alternate byte). The
// safe-gameplay gate + victory goal are taken verbatim from
// LinksAwakeningClient.py LAClientConstants. Mock-verified through MoonSharp
// against synthetic Game Boy memory.
//
// items_handling = 0b101 — LADX uses REMOTE items: the game does NOT self-grant
// remote multiworld items. The upstream client delivers them with a guarded write
// handshake (wLinkGiveItem / wLinkStatusBits / wRecvIndex, spinning until the game
// is in a safe state, only after the player's first check). That write path is the
// one piece deferred (ladx.lua receive_item is a documented no-op) until it can be
// confirmed in-emulator, rather than shipped unverified where a wrong write could
// corrupt the save. Check reporting + goal are fully live.
//
// AP PATCH (.apladx → .gbc): a standard APProcedurePatch container (LADXR builds
// the ROM from data.json + the user's vanilla cart), applied via the shared
// SnesApPatchHelper (resolve the seed's patch, validate the base ROM MD5 from the
// manifest's base_checksum, run apply_appatch.py on a library COPY — the original
// is never modified). Base ROM: the user's own Link's Awakening DX (USA) cartridge
// dump (1 MB), identified by CONTENT (size + MD5) — never by filename (§11).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class LinksAwakeningDXPlugin : EmulatorPlugin
{
    public LinksAwakeningDXPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "ladx";
    public override string DisplayName => "Link's Awakening DX";
    public override string Subtitle    => "GBC · Emulated";
    public override string ApWorldName => "Links Awakening DX";

    public override string Description =>
        "The Legend of Zelda: Link's Awakening DX is the Game Boy Color remaster " +
        "of Link's island adventure. In the Archipelago randomizer, every chest, " +
        "instrument, owl statue, trade item, and seashell across Koholint joins the " +
        "multiworld pool. Wake the Wind Fish to complete your goal.";

    public override string ThemeAccentColor => "#1F7A3D";   // Koholint green

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "GBC";

    // §14: GBC emulators — BizHawk verified (the AP world ships a BizHawk Lua
    // connector for LADX); mGBA/Mesen are the alternatives (shown per their bridge
    // state in the dropdown).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "mgba", "mesen" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "ladx";

    /// ladx.lua reports checks + goal (table generated from the apworld; mock-
    /// verified). Remote-item delivery is the documented deferred piece.
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dump the randomizer is built for, by CONTENT
    /// (1 MB + MD5) — never by filename. LADXR validates this exact MD5
    /// (LADX_HASH) before building, and the patch's base_checksum re-checks it
    /// before anything is written. This is the only known-good base ROM.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(1024 * 1024, "07c211479386825042efb4ad31bb525f",
                        "Zelda: Link's Awakening DX (USA, Europe) cartridge"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────

    private const string PatchExt = ".apladx";

    /// Explicit .apladx chosen by the user (Settings / drag-drop). Null = auto.
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
                 : new RomRequirement("Link's Awakening DX", "GBC",
                       "Zelda: Link's Awakening DX (USA, Europe)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement("Link's Awakening DX", "GBC",
            "Zelda: Link's Awakening DX (USA, Europe) — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apladx to a library copy ──────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Link's Awakening DX] No AP patch (.apladx) found — launching the " +
                "vanilla ROM. Generate the multiworld, then pick the patch in Settings " +
                "(or drop it under " + SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Link's Awakening DX", patch, how, RomPath!,
            RomLibraryDirectory, ".gbc", ct);
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
