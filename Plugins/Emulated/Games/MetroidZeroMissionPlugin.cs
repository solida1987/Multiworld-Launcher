using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// MetroidZeroMissionPlugin — Archipelago integration for Metroid: Zero Mission
// (GBA), on the proven BizHawk Lua-pipe bridge.
//
// COMMUNITY APWORLD — this game is NOT in the ArchipelagoMW/Archipelago main repo.
//   Source: https://github.com/lilDavid/Archipelago-Metroid-Zero-Mission
//   world id "mzm", patch ".apmzm" → ".gba", world_version 0.5.3, min AP 0.6.4.
//   Base patch source: https://github.com/lilDavid/MZM-Archipelago
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED. The 101-location
// table in mzm.lua was GENERATED from worlds/mzm/locations.py (the seven per-area
// location_tables); the per-area bitfield math, the GM_INGAME gate, the
// ESCAPED_CHOZODIA goal flag and the "ZEROMISSIONE" ROM-title check are replicated
// exactly from worlds/mzm/client.py (MZMClient.send_game_state), with the symbol
// addresses taken from the world's patcher/data/extracted_symbols.json. Mock-
// verified through MoonSharp against synthetic GBA System-Bus memory.
//
// items_handling = 0b111 — the AP SERVER drives ALL item delivery. The MZM client
// itself injects every received item (locals AND remotes) through a guarded
// gIncomingItem / gIncomingMessage text-box write path, counting each against the
// game's own equipment/tank state. That path is the documented deferred piece
// (mzm.lua receive_item is a no-op) until it can be confirmed in-emulator, rather
// than shipped unverified — a wrong write to gIncomingItem could mis-grant items
// or soft-lock. Checks + goal flow regardless.
//
// HOW MZM IS RANDOMIZED — IMPORTANT: the .apmzm IS an APProcedurePatch container,
// but its procedure is a CUSTOM step  procedure = [("apply_json", ["patch.json"])]
// implemented inside the apworld's own patcher (LZ10/RLE decompression, layout
// patches, text writes). The launcher's shared apply_appatch.py only knows the
// standard apply_bsdiff4 / apply_ips / apply_tokens steps and would refuse
// apply_json — so the launcher does NOT try to apply .apmzm itself. The patched
// .gba is produced by the MZM Archipelago client (the AP "Open Patch" flow auto-
// patches the player's vanilla ROM on connect). The player loads that produced
// .gba here; if it sits next to the .apmzm we reuse it, otherwise we launch the
// library ROM directly and explain. Base ROM: the player's own Metroid: Zero
// Mission (USA) cartridge dump (8 MB), identified by CONTENT (size + MD5) — never
// by filename (§11). The vanilla and US-Virtual-Console MD5s are both accepted
// (the apworld supports both, with a VC warning).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MetroidZeroMissionPlugin : EmulatorPlugin
{
    public MetroidZeroMissionPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "mzm";
    public override string DisplayName => "Metroid: Zero Mission";
    public override string Subtitle    => "GBA · Emulated";

    // Exact AP world id — the "game" string in worlds/mzm/archipelago.json and
    // MZMWorld.game / MZMClient.game.
    public override string ApWorldName => "Metroid: Zero Mission";

    public override string Description =>
        "Metroid: Zero Mission is the GBA retelling of the original NES Metroid — " +
        "Samus' first mission to planet Zebes, rebuilt with new areas, items and a " +
        "Chozodia finale. In the Archipelago randomizer, every missile tank, energy " +
        "tank, beam and major upgrade across Zebes joins the multiworld pool. Escape " +
        "Chozodia to complete your goal. Community apworld by lil David & NoiseCrush.";

    public override string ThemeAccentColor => "#C81E2E";   // Samus Varia-suit red

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "GBA";

    // §14: GBA emulators — BizHawk is the MZM community's verified client target;
    // mGBA/Mesen are the GBA alternatives (shown per their bridge state in the
    // dropdown).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "mgba", "mesen" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "mzm";

    /// mzm.lua reports checks + goal (table generated from the apworld; bitfield
    /// math + gate + ROM-title check from client.py; mock-verified). Remote-item
    /// delivery is the documented deferred piece (items_handling = 0b111, the
    /// client's own guarded gIncomingItem write path).
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dumps the randomizer is built for, by CONTENT
    /// (8 MB + MD5) — never by filename. Both the standard US cartridge and the
    /// US Virtual Console dump are accepted (the apworld supports both; MD5s from
    /// worlds/mzm/patcher: MD5_US / MD5_US_VC).
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(8 * 1024 * 1024, "ebbce58109988b6da61ebb06c7a432d5",
                        "Metroid: Zero Mission (USA) cartridge"),
        new RomIdentity(8 * 1024 * 1024, "e23c14997c2ea4f11e5996908e577125",
                        "Metroid: Zero Mission (USA) Virtual Console"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────
    //
    // The .apmzm is an APProcedurePatch container, but its custom apply_json step
    // is not applicable by the shared apply_appatch.py (see header). We keep the
    // standard hook shape for consistency, but PrepareSessionRomAsync never runs
    // the Python applier on a .apmzm — the player loads the .gba the MZM AP client
    // produced. The resolver below is used only to (a) detect that an .apmzm for
    // this room exists, and (b) locate a pre-patched .gba sitting next to it.

    private const string PatchExt = ".apmzm";

    /// Explicit .apmzm chosen by the user (Settings / drag-drop). Null = auto.
    public string? ApPatchPath { get; private set; }

    protected override void OnSavingSettings(IDictionary<string, object?> bag)
        => bag["ap_patch_path"] = ApPatchPath;

    protected override void OnLoadingSettings(JsonElement root)
    {
        if (root.TryGetProperty("ap_patch_path", out var el)) ApPatchPath = el.GetString();
    }

    /// Set the explicit AP patch (drag-and-drop / room-link import) and persist it.
    public void SetExplicitPatch(string path) { ApPatchPath = path; SaveSettings(); }

    /// ROM safety net. The .apmzm container's manifest does not expose the base
    /// ROM MD5 in the field apply_appatch.py reads, so the requirement we surface
    /// is "the right MZM (USA) ROM is present" — validated by CONTENT against
    /// AcceptableBaseRoms (the US cartridge / VC MD5s).
    public override RomRequirement? GetUnmetRomRequirement()
    {
        bool haveRom = RomPath != null && File.Exists(RomPath);
        if (haveRom && ValidateBaseRom(RomPath!) == null)
            return null;

        return new RomRequirement("Metroid: Zero Mission", "GBA",
            "Metroid: Zero Mission (USA) — your original cartridge dump " +
            "(or the patched .gba produced by the MZM Archipelago client)",
            RequiredMd5: null, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM ───────────────────────────────────────────────────────────
    //
    // MZM's .apmzm uses a custom apply_json procedure the shared applier cannot
    // run, so the launcher applies NO patch of its own. If a .gba produced by the
    // MZM AP client sits next to the resolved .apmzm, we launch that (it carries
    // the seed's checks + slot/seed identity); otherwise we launch the library
    // ROM directly and explain. The original library ROM is never modified.

    protected override Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);

        if (patch == null)
        {
            SessionRomNote =
                "[Metroid: Zero Mission] No AP patch (.apmzm) found. Generate the " +
                "multiworld, then open the .apmzm with the Archipelago client once to " +
                "produce the patched .gba — load that here. Launching your current " +
                "ROM directly for now.";
            return Task.FromResult<string?>(null);
        }

        // The MZM client writes the patched ROM next to the .apmzm with the same
        // stem and a .gba extension (AP's standard "open patch" output). If it is
        // there, launch it — it is the seed-specific ROM with this slot's data.
        string sidecar = Path.ChangeExtension(patch, ".gba");
        if (File.Exists(sidecar))
        {
            SessionRomNote =
                $"[Metroid: Zero Mission] Launching the patched ROM produced by the " +
                $"MZM Archipelago client: {sidecar} (matched {how}).";
            RegisterSeed(sidecar, patch);
            return Task.FromResult<string?>(sidecar);
        }

        // Found the .apmzm but no patched .gba yet — the launcher cannot apply the
        // custom apply_json procedure itself (apply_appatch.py supports only
        // bsdiff4/IPS/tokens). Tell the player exactly what to do.
        SessionRomNote =
            $"[Metroid: Zero Mission] Found {Path.GetFileName(patch)}, but its patched " +
            ".gba is not next to it yet. Open the .apmzm once with the Archipelago " +
            "client (it auto-patches your Metroid: Zero Mission (USA) ROM and writes " +
            "the .gba beside the patch), then load that .gba here. Launching your " +
            "current ROM directly for now.";
        return Task.FromResult<string?>(null);
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
