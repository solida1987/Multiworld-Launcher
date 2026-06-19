using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// MetroidFusionPlugin — Archipelago integration for Metroid Fusion (GBA), on the
// proven BizHawk Lua-pipe bridge.
//
// COMMUNITY APWORLD — this game is NOT in the ArchipelagoMW/Archipelago main repo.
//   Source: https://github.com/Rosalie-A/Archipelago  (branch "metroidfusion",
//   worlds/metroidfusion). world id "metroidfusion", game string "Metroid Fusion",
//   patch ".apmetfus" → ".gba", APWorld version 17. Randomizer engine: MARS
//   (mars_patcher), the same family that powers Metroid: Zero Mission.
//
// STATUS: location DETECTION + goal are REAL and SOURCE-DERIVED. The 126-location
// table in metroidfusion.lua (103 minor + 23 major) was GENERATED from the
// apworld source — the ap_id assignment loop in Locations.py joined with the MARS
// bitfield orders in data/minor_locations.py + data/major_locations.py; the
// per-bitfield math (EWRAM 16-byte minor field at 0x037200, IWRAM 4-byte major
// field at 0x06B4), the game_mode==ingame gate, the game_mode==credits goal and
// the "MFU" ROM-name check are replicated exactly from worlds/metroidfusion/
// Client.py (MetroidFusionClient.game_watcher), with the addresses taken from
// data/memory.py. Mock-verified through MoonSharp against synthetic GBA per-domain
// memory (19/19 checks).
//
// items_handling = 0b011 — the AP SERVER drives item delivery, and the Fusion
// client itself injects every received item (locals AND remotes) through a guarded
// IWRAM inventory/tank/keycard write path plus an SRAM received-count handshake
// (Client.py received_items_check + sync_upgrades). That path is the documented
// deferred piece (metroidfusion.lua receive_item is a no-op) until it can be
// confirmed in-emulator, rather than shipped unverified — a wrong IWRAM write
// could mis-grant items or corrupt the save. Checks + goal flow regardless.
//
// HOW FUSION IS RANDOMIZED — IMPORTANT: the .apmetfus IS an APProcedurePatch
// container, but its procedure is a CUSTOM step  procedure = [("call_mars",
// ["patch_file.json"])]  implemented inside the apworld's own mars_patcher (BPS/
// IPS asm patches + JSON-driven layout/item/text writes). The launcher's shared
// apply_appatch.py only knows the standard apply_bsdiff4 / apply_ips / apply_tokens
// steps and would refuse call_mars — so the launcher does NOT try to apply
// .apmetfus itself. The patched .gba is produced by the Metroid Fusion Archipelago
// client (the AP "Open Patch" flow auto-patches the player's vanilla ROM on
// connect). The player loads that produced .gba here; if it sits next to the
// .apmetfus we reuse it, otherwise we launch the library ROM directly and explain.
// Base ROM: the player's own Metroid Fusion (USA) cartridge dump (8 MB), identified
// by CONTENT (size + MD5) — never by filename (§11). MD5 from
// worlds/metroidfusion/Rom.py (MetroidFusionProcedurePatch.hash).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MetroidFusionPlugin : EmulatorPlugin
{
    public MetroidFusionPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "metroidfusion";
    public override string DisplayName => "Metroid Fusion";
    public override string Subtitle    => "GBA · Emulated";

    // Exact AP world id — the "game" string in worlds/metroidfusion (MetroidFusionWorld.game
    // / MetroidFusionClient.game).
    public override string ApWorldName => "Metroid Fusion";

    public override string Description =>
        "Metroid Fusion is Samus Aran's GBA adventure aboard the research station " +
        "BSL, hunting the parasitic X and her own cloned SA-X. In the Archipelago " +
        "randomizer, every missile and energy tank, beam, suit and major upgrade " +
        "across the station's six sectors joins the multiworld pool. Reach the " +
        "ending to complete your goal. Community apworld built on the MARS " +
        "randomizer (worlds/metroidfusion).";

    public override string ThemeAccentColor => "#1E73C8";   // Fusion-suit blue

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "GBA";

    // §14: GBA emulators — BizHawk is the Fusion community's verified client target;
    // mGBA/Mesen are the GBA alternatives (shown per their bridge state in the
    // dropdown).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "mgba", "mesen" };
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "metroidfusion";

    /// metroidfusion.lua reports checks + goal (126-location table generated from
    /// the apworld; bitfield math + gate + MFU ROM-name check from Client.py;
    /// mock-verified 19/19). Remote-item delivery is the documented deferred piece
    /// (items_handling = 0b011, the client's own guarded IWRAM write path).
    public override bool ChecksImplemented => true;

    /// The vanilla cartridge dump the randomizer is built for, by CONTENT
    /// (8 MB + MD5) — never by filename. MD5 from worlds/metroidfusion/Rom.py
    /// (MetroidFusionProcedurePatch.hash = the Metroid Fusion (USA) cartridge).
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(8 * 1024 * 1024, "27d02a4f03e172e029c9b82ac3db79f7",
                        "Metroid Fusion (USA) cartridge"),
    };

    // ── AP patch ────────────────────────────────────────────────────────────────
    //
    // The .apmetfus is an APProcedurePatch container, but its custom call_mars step
    // is not applicable by the shared apply_appatch.py (see header). We keep the
    // standard hook shape for consistency, but PrepareSessionRomAsync never runs
    // the Python applier on a .apmetfus — the player loads the .gba the Metroid
    // Fusion AP client produced. The resolver below is used only to (a) detect that
    // an .apmetfus for this room exists, and (b) locate a pre-patched .gba sitting
    // next to it.

    private const string PatchExt = ".apmetfus";

    /// Explicit .apmetfus chosen by the user (Settings / drag-drop). Null = auto.
    public string? ApPatchPath { get; private set; }

    protected override void OnSavingSettings(IDictionary<string, object?> bag)
        => bag["ap_patch_path"] = ApPatchPath;

    protected override void OnLoadingSettings(JsonElement root)
    {
        if (root.TryGetProperty("ap_patch_path", out var el)) ApPatchPath = el.GetString();
    }

    /// Set the explicit AP patch (drag-and-drop / room-link import) and persist it.
    public void SetExplicitPatch(string path) { ApPatchPath = path; SaveSettings(); }

    /// ROM safety net. The .apmetfus container's manifest does not expose the base
    /// ROM MD5 in the field apply_appatch.py reads, so the requirement we surface
    /// is "the right Metroid Fusion (USA) ROM is present" — validated by CONTENT
    /// against AcceptableBaseRoms (the US cartridge MD5).
    public override RomRequirement? GetUnmetRomRequirement()
    {
        bool haveRom = RomPath != null && File.Exists(RomPath);
        if (haveRom && ValidateBaseRom(RomPath!) == null)
            return null;

        return new RomRequirement("Metroid Fusion", "GBA",
            "Metroid Fusion (USA) — your original cartridge dump " +
            "(or the patched .gba produced by the Metroid Fusion Archipelago client)",
            RequiredMd5: null, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM ───────────────────────────────────────────────────────────
    //
    // Fusion's .apmetfus uses a custom call_mars procedure the shared applier cannot
    // run, so the launcher applies NO patch of its own. If a .gba produced by the
    // Metroid Fusion AP client sits next to the resolved .apmetfus, we launch that
    // (it carries the seed's checks + slot/seed identity); otherwise we launch the
    // library ROM directly and explain. The original library ROM is never modified.

    protected override Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out string how);

        if (patch == null)
        {
            SessionRomNote =
                "[Metroid Fusion] No AP patch (.apmetfus) found. Generate the " +
                "multiworld, then open the .apmetfus with the Archipelago client once " +
                "to produce the patched .gba — load that here. Launching your current " +
                "ROM directly for now.";
            return Task.FromResult<string?>(null);
        }

        // The Fusion client writes the patched ROM next to the .apmetfus with the
        // same stem and a .gba extension (AP's standard "open patch" output). If it
        // is there, launch it — it is the seed-specific ROM with this slot's data.
        string sidecar = Path.ChangeExtension(patch, ".gba");
        if (File.Exists(sidecar))
        {
            SessionRomNote =
                $"[Metroid Fusion] Launching the patched ROM produced by the Metroid " +
                $"Fusion Archipelago client: {sidecar} (matched {how}).";
            RegisterSeed(sidecar, patch);
            return Task.FromResult<string?>(sidecar);
        }

        // Found the .apmetfus but no patched .gba yet — the launcher cannot apply the
        // custom call_mars procedure itself (apply_appatch.py supports only
        // bsdiff4/IPS/tokens). Tell the player exactly what to do.
        SessionRomNote =
            $"[Metroid Fusion] Found {Path.GetFileName(patch)}, but its patched .gba " +
            "is not next to it yet. Open the .apmetfus once with the Archipelago " +
            "client (it auto-patches your Metroid Fusion (USA) ROM and writes the " +
            ".gba beside the patch), then load that .gba here. Launching your current " +
            "ROM directly for now.";
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
