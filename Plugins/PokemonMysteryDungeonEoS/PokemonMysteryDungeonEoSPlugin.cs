using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;
using LauncherV2.Plugins.Emulated;
using LauncherV2.Plugins.Emulated.Games;

namespace LauncherV2.Plugins.PokemonMysteryDungeonEoS;

// ═══════════════════════════════════════════════════════════════════════════════
// PokemonMysteryDungeonEoSPlugin — Archipelago integration for Pokémon Mystery
// Dungeon: Explorers of Sky (Nintendo DS), on the proven BizHawk Lua-pipe
// bridge. BizHawk runs NDS through its bundled melonDS core.
//
// CONFIRMED AP WORLD (2026-06-15)
// ────────────────────────────────
// Source: https://github.com/CrypticMonkey33/ArchipelagoExplorersOfSky
// Latest release: v0.3.2 (2026-01-09).  AP world name (EOSWorld.game and
// EOSWeb.game in worlds/pmd_eos/__init__.py):
//     "Pokémon Mystery Dungeon: Explorers of Sky"
// Patch extension: .apeos → produces .nds (EOSProcedurePatch in rom.py).
// Base ROM: EU dump (POKEDUN_SORA_C2SP01_00.nds), MD5
//     6735749e060e002efd88e61560e45567  (EOSSettings.RomFile.md5s).
//
// STATUS: stub — ROM + patch wiring is mirrored from PokemonPlatinumPlugin.
// The Lua bridge module (pokemon_mystery_dungeon_eos.lua) does NOT exist yet;
// SupportsStandalone is false; ConnectsItself is true so the launcher never
// double-connects a slot while the community's bundled EoS AP client (client.py
// wraps BizHawk via SNIClient) is running.
//
// WHY ConnectsItself = true
// ─────────────────────────
// The EoS apworld ships a BizHawk client (client.py) that connects to the AP
// server natively from within BizHawk. Launching the launcher's own ApClient
// on the same slot simultaneously would cause the server to kick one of the
// two sessions and trigger an auto-reconnect loop. Until a named-pipe bridge
// module is built and confirmed in-emulator, ConnectsItself = true suppresses
// the launcher's auto-connect / "connection lost" toast while the game runs.
//
// LAUNCH CHAIN (current)
// ──────────────────────
// 1. BizHawk is auto-installed from GitHub releases on first install
//    (inherited from EmulatorPlugin.InstallOrUpdateAsync).
// 2. User supplies their EU NDS ROM in Settings (never shipped — §11).
// 3. PrepareSessionRomAsync applies the .apeos patch to a library COPY via
//    apply_appatch.py (bsdiff4 + token_data.bin); the original is untouched.
// 4. BizHawk starts with the patched .nds. The community EoS BizHawk client
//    (bundled in the apworld's /EoSClient tooling) must be run separately —
//    launcher pipe bridge is deferred to a future update.
//
// MINIMUM AP VERSION: 0.6.3 (archipelago.json).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PokemonMysteryDungeonEoSPlugin : EmulatorPlugin
{
    public PokemonMysteryDungeonEoSPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "pmd_eos";
    public override string DisplayName => "Pokémon Mystery Dungeon: Explorers of Sky";
    public override string Subtitle    => "NDS · Emulated";

    // Exact AP world id — the `game` string in worlds/pmd_eos/__init__.py
    // (EOSWorld.game and EOSWeb.game, v0.3.2).
    public override string ApWorldName => "Pokémon Mystery Dungeon: Explorers of Sky";

    public override string Description =>
        "Pokémon Mystery Dungeon: Explorers of Sky is the second entry in the " +
        "Nintendo DS Mystery Dungeon series. As a human-turned-Pokémon, you and " +
        "your partner must stop the collapse of Temporal Tower in an adventure " +
        "spanning time and darkness. " +
        "In the Archipelago randomizer by CrypticMonkey33, dungeon floors, story " +
        "chapters, and special episodes feed the multiworld item pool. " +
        "Requires BizHawk with the bundled melonDS NDS core.";

    // ── Visual identity ───────────────────────────────────────────────────────

    public override string ThemeAccentColor => "#3A7FCF";   // temporal-blue / Dialga

    // ── AP bridge — ConnectsItself = true ────────────────────────────────────
    //
    // The EoS apworld ships a BizHawk client (client.py via SNIClient) that
    // connects to the AP server from inside BizHawk. The launcher suppresses
    // its own ApClient slot-connection while the game is running to avoid a
    // dual-connection kick-war.
    //
    // LocationsChecked and GoalCompleted (inherited from EmulatorPlugin) are
    // never raised by this plugin — the community BizHawk client sends checks
    // and goal directly to the AP server. They will start firing once a
    // launcher-side Lua bridge module (pokemon_mystery_dungeon_eos.lua) is
    // built and verified in-emulator. GameExited is inherited and fires
    // normally through the base class process-exit path.

    /// The EoS apworld's native BizHawk client connects to the AP server
    /// itself. See header — the launcher must not hold a second session on
    /// the same slot while the game runs.
    public bool ConnectsItself => true;

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem => "NDS";

    // NDS is hosted by BizHawk's bundled melonDS core — the only NDS backend
    // wired in the launcher today (same as PokemonPlatinumPlugin).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk" };

    protected override string LuaScriptName => "bizhawk_ap_connector.lua";

    // The per-game Lua module does not exist yet; the base connector will show
    // the standard "no module" notice in BizHawk's Lua console.
    protected override string LuaModuleName => "pokemon_mystery_dungeon_eos";

    /// Checks are not yet self-implemented in the launcher bridge. The game's
    /// own BizHawk client (shipped with the apworld) handles check detection
    /// directly against the AP server.
    public override bool ChecksImplemented => false;

    /// The vanilla cartridge dump the randomizer is built for, by CONTENT
    /// (MD5) — never by filename (§11). Only the EU dump is accepted by the
    /// apworld's bsdiff4 base patch (EOSProcedurePatch.hash in rom.py,
    /// EOSSettings.RomFile.md5s). Size 0 = accept any size matching the MD5.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(
            SizeBytes: 0,   // NDS dump size varies; MD5 is the authoritative id
            Md5:  "6735749e060e002efd88e61560e45567",
            Label: "Pokémon Mystery Dungeon: Explorers of Sky (EU)"),
    };

    // ── AP patch ─────────────────────────────────────────────────────────────

    // EOSProcedurePatch.patch_file_ending (worlds/pmd_eos/rom.py, v0.3.2).
    private const string PatchExt = ".apeos";

    /// Explicit .apeos chosen by the user (Settings / drag-drop). Null = auto.
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

    /// The patch that would be used right now, in the shared helper's priority
    /// order (explicit pick → this room's seed+slot → slot name → newest).
    private string? ResolvePatch(out string how)
        => SnesApPatchHelper.ResolvePatch(
               PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out how);

    /// ROM safety net: demand the exact EU dump when our ROM is missing or the
    /// wrong one. The patch's base_checksum (MD5 of the EU ROM) is the
    /// canonical verification.
    public override RomRequirement? GetUnmetRomRequirement()
    {
        string? patch   = ResolvePatch(out _);
        string? wantMd5 = patch != null
            ? SnesApPatchHelper.ReadManifestField(patch, "base_checksum") : null;
        bool haveRom    = RomPath != null && File.Exists(RomPath);

        if (wantMd5 == null)
            return haveRom ? null
                 : new RomRequirement(
                       "Pokémon Mystery Dungeon: Explorers of Sky", "NDS",
                       "Pokémon Mystery Dungeon: Explorers of Sky (EU) — your original cartridge dump",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom &&
            ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement(
            "Pokémon Mystery Dungeon: Explorers of Sky", "NDS",
            "Pokémon Mystery Dungeon: Explorers of Sky (EU) — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

    // ── Session ROM: apply the .apeos patch to a library copy ────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = ResolvePatch(out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[PMD: Explorers of Sky] No AP patch (.apeos) found — launching " +
                "the vanilla ROM. Checks and item delivery CANNOT WORK on a " +
                "vanilla ROM: generate the multiworld with the pmd_eos apworld, " +
                "then pick the patch in Settings (or drop it under " +
                SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        var res = await SnesApPatchHelper.ApplyAsync(
            "Pokémon Mystery Dungeon: Explorers of Sky",
            patch, how, RomPath!,
            RomLibraryDirectory, ".nds", ct);
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
        catch { /* seed library is non-essential to launching */ }
    }

    // ── Settings panel ────────────────────────────────────────────────────────

    public override UIElement? CreateSettingsPanel()
    {
        var basePanel = base.CreateSettingsPanel();
        if (basePanel is not StackPanel panel) return basePanel;

        var muted = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg    = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));

        // ── AP patch section ──────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text       = "ARCHIPELAGO PATCH",
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new Thickness(0, 20, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "The multiworld generator produces a .apeos patch for your slot. " +
                   "The launcher applies it to a COPY of your EU ROM at launch " +
                   "(your file is never modified). Leave empty to auto-use the " +
                   "newest patch from the Archipelago output folder.",
            FontSize    = 11,
            Foreground  = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin      = new Thickness(0, 0, 0, 8),
        });

        var patchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var patchBox = new TextBox
        {
            Text        = ApPatchPath ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var clearBtn = new Button
        {
            Content     = "Auto",
            Width       = 50,
            Margin      = new Thickness(0, 0, 8, 0),
            Padding     = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
            ToolTip     = "Clear the explicit pick — auto-detect the newest generated patch.",
        };
        var pickBtn = new Button
        {
            Content     = "Select AP patch...",
            Width       = 130,
            Padding     = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };

        var autoNote = new TextBlock
        {
            FontSize    = 10,
            Foreground  = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin      = new Thickness(0, 4, 0, 0),
        };
        void RefreshAutoNote()
        {
            string? auto = ResolvePatch(out string how);
            autoNote.Text = ApPatchPath != null
                ? "Explicit patch selected — auto-detection off."
                : auto != null
                    ? $"Auto-detected ({how}): {auto}"
                    : $"No .apeos found under {SnesApPatchHelper.ApOutputDirectory} yet.";
        }
        RefreshAutoNote();

        pickBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select Archipelago patch for Pokémon Mystery Dungeon: Explorers of Sky",
                Filter = "AP EoS patch (*.apeos)|*.apeos|All files (*.*)|*.*",
            };
            if (Directory.Exists(SnesApPatchHelper.ApOutputDirectory))
                dlg.InitialDirectory = SnesApPatchHelper.ApOutputDirectory;
            if (dlg.ShowDialog() != true) return;
            ApPatchPath   = dlg.FileName;
            patchBox.Text = dlg.FileName;
            SaveSettings();
            RefreshAutoNote();
        };
        clearBtn.Click += (_, _) =>
        {
            ApPatchPath   = null;
            patchBox.Text = "";
            SaveSettings();
            RefreshAutoNote();
        };

        DockPanel.SetDock(pickBtn,  Dock.Right);
        DockPanel.SetDock(clearBtn, Dock.Right);
        patchRow.Children.Add(pickBtn);
        patchRow.Children.Add(clearBtn);
        patchRow.Children.Add(patchBox);
        panel.Children.Add(patchRow);
        panel.Children.Add(autoNote);

        // ── Note about ConnectsItself ────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "NOTE: This game uses the EoS AP client bundled with the apworld " +
                   "(BizHawk script). The launcher pre-fills the connection but does " +
                   "not hold its own AP slot-connection while the game runs, to avoid " +
                   "session conflicts.",
            FontSize    = 10,
            Foreground  = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin      = new Thickness(0, 12, 0, 4),
        });

        return panel;
    }
}
