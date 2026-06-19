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

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// KirbySuperStarPlugin — Archipelago integration for Kirby Super Star (SNES),
// on the proven BizHawk Lua-pipe bridge and the snes9x/Mesen NWA backends.
//
// WORLD SOURCE: Kirby Super Star is NOT in ArchipelagoMW/Archipelago main — it
// ships on Silvris's fork (kss_0.1.9, the latest release as of 2026-06-15):
// https://github.com/Silvris/Archipelago/releases/tag/kss_0.1.9
// (worlds/kss; archipelago.json game "Kirby Super Star", world_version 0.1.9,
// minimum_ap_version 0.6.4, author Silvris — an AP core dev).
// The reference client worlds/kss/client.py is KSSSNIClient extending SNIClient
// (SNES via SNI — compatible with BizHawk's NWA connector, snes9x, and Mesen).
//
// STATUS: Check detection is SOURCE-DERIVED in principle (SRAM reads at
// SRAM_1_START + known offsets from client.py), but the full location table
// and SRAM-bit logic have not been mock-verified in MoonSharp against the Lua
// bridge. ChecksImplemented is therefore false; the stub launches the game and
// connects to AP but does not report checks. items_handling = 0b111 (FULL
// remote — the KSS client owns all item delivery via guarded SNES-write paths).
//
// AP PATCH (.apkss → .sfc): a standard APProcedurePatch container
// (apply_basepatch bsdiff4 + apply_tokens + calc_snes_crc), applied via the
// shared SnesApPatchHelper. Base ROM: the user's own Kirby Super Star (USA)
// cartridge dump (4 MiB), identified by CONTENT (size + MD5) — never by
// filename (§11). Two MD5s accepted: the standard US dump (KSS_UHASH) and the
// US Virtual Console dump (KSS_VCHASH), matching kss/rom.py's hash list.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class KirbySuperStarPlugin : EmulatorPlugin
{
    public KirbySuperStarPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "kss";
    public override string DisplayName => "Kirby Super Star";
    public override string Subtitle    => "SNES · Emulated";
    public override string ApWorldName => "Kirby Super Star";

    public override string Description =>
        "Kirby Super Star is the beloved SNES action platformer packed with eight " +
        "distinct sub-games — Spring Breeze, Dyna Blade, Gourmet Race, The Great " +
        "Cave Offensive, Milky Way Wishes, The Arena, Revenge of Meta Knight, and " +
        "Samurai Kirby. Kirby's signature copy-ability system lets him inhale " +
        "enemies to steal over 20 powers. In the Archipelago randomizer, treasures " +
        "from The Great Cave Offensive, Milky Way Wishes essences, boss clears, " +
        "consumables, and sub-game completions all join the multiworld pool — " +
        "clear the required sub-games to reach your goal. The apworld is by " +
        "Silvris. Bring your own Kirby Super Star (USA) SNES ROM.";

    public override string ThemeAccentColor => "#E85C9A";   // Kirby pink

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "SNES";
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "kss";

    // §14: KSS client is KSSSNIClient (SNIClient family) — works with BizHawk's
    // NWA connector, snes9x, and Mesen. All three are offered per their bridge
    // state in the dropdown.
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "snes9x", "mesen" };

    /// Check detection is not yet mock-verified in the Lua bridge; stub only.
    public override bool ChecksImplemented => false;

    // US cartridge dump and US Virtual Console dump, matching kss/rom.py's
    // KSS_UHASH and KSS_VCHASH. Both are 4 MiB headerless .sfc files.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms => new[]
    {
        new RomIdentity(4 * 1024 * 1024, "cb76ea8ac989e71210c89102d91c6c57",
                        "Kirby Super Star (USA)"),
        new RomIdentity(4 * 1024 * 1024, "5e0be1a462ffaca1351d446b96b25b74",
                        "Kirby Super Star (USA, Virtual Console)"),
    };

    // ── AP patch settings ─────────────────────────────────────────────────────

    private const string PatchExt  = ".apkss";
    private const string ResultExt = ".sfc";

    /// Explicit .apkss chosen by the user (Settings / drag-drop). Null = auto.
    public string? ApPatchPath { get; private set; }

    protected override void OnSavingSettings(IDictionary<string, object?> bag)
        => bag["ap_patch_path"] = ApPatchPath;

    protected override void OnLoadingSettings(JsonElement root)
    {
        if (root.TryGetProperty("ap_patch_path", out var el))
            ApPatchPath = el.GetString();
    }

    private string? ResolvePatchPath(out string how)
        => SnesApPatchHelper.ResolvePatch(
            PatchExt, ApPatchPath, GetSeedName?.Invoke(), CurrentSlotName, out how);

    /// Set the explicit AP patch (drag-and-drop / room-link import) and persist it.
    public void SetExplicitPatch(string path) { ApPatchPath = path; SaveSettings(); }

    /// ROM safety net: when a patch is known, demand exactly its base ROM
    /// (base_checksum) so the launcher can ask the player by fingerprint.
    public override RomRequirement? GetUnmetRomRequirement()
    {
        string? wantMd5 = null;
        var patch = ResolvePatchPath(out _);
        if (patch != null) wantMd5 = SnesApPatchHelper.ReadManifestField(patch, "base_checksum");

        bool haveRom = RomPath != null && File.Exists(RomPath);

        if (wantMd5 == null)
            return haveRom ? null
                 : new RomRequirement("Kirby Super Star", "SNES",
                       "Kirby Super Star (USA)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement(
            "Kirby Super Star", "SNES",
            "Kirby Super Star (USA) — your original cartridge dump",
            wantMd5, WrongVersionPresent: haveRom, BuildRomFilter());
    }

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

    // ── Session ROM: apply the .apkss to a library copy ───────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = ResolvePatchPath(out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Kirby Super Star] No AP patch (.apkss) found — launching the " +
                "vanilla ROM. Checks cannot work on a vanilla ROM: generate the " +
                "multiworld, then pick the patch in Settings (or drop it under " +
                SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        if (RomPath == null || !File.Exists(RomPath))
        {
            SessionRomNote =
                "[Kirby Super Star] No base ROM set — import your Kirby Super Star " +
                "(USA) cartridge dump in Settings, then relaunch.";
            return null;
        }

        var result = await SnesApPatchHelper.ApplyAsync(
            "Kirby Super Star", patch, how, RomPath!, RomLibraryDirectory, ResultExt, ct);
        SessionRomNote = result.Note;
        if (result.OutRom != null) RegisterSeed(result.OutRom, patch);
        return result.OutRom;
    }

    // ── Settings panel ────────────────────────────────────────────────────────

    public override UIElement? CreateSettingsPanel()
    {
        var basePanel = base.CreateSettingsPanel();
        if (basePanel is not StackPanel panel) return basePanel;

        var muted = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg    = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));

        panel.Children.Add(new TextBlock
        {
            Text = "ARCHIPELAGO PATCH", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 20, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "The multiworld generator produces a .apkss patch for your slot. " +
                   "The launcher applies it to a COPY of your ROM at launch (your file " +
                   "is never modified). Leave empty to auto-use the newest patch from " +
                   "the Archipelago output folder.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var patchRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var patchBox = new TextBox
        {
            Text = ApPatchPath ?? "", Margin = new Thickness(0, 0, 8, 0),
            IsReadOnly = true, FontSize = 12,
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var clearBtn = new Button
        {
            Content = "Auto", Width = 50, Margin = new Thickness(0, 0, 8, 0),
            Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
            ToolTip = "Clear the explicit pick — auto-detect the newest generated patch.",
        };
        var pickBtn = new Button
        {
            Content = "Select AP patch...", Width = 130, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };

        var autoNote = new TextBlock
        {
            FontSize = 10, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0),
        };
        void RefreshAutoNote()
        {
            string? auto = ResolvePatchPath(out string how);
            autoNote.Text = ApPatchPath != null
                ? "Explicit patch selected — auto-detection off."
                : auto != null
                    ? $"Auto-detected ({how}): {auto}"
                    : $"No .apkss found under {SnesApPatchHelper.ApOutputDirectory} yet.";
        }
        RefreshAutoNote();

        pickBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select Archipelago patch for Kirby Super Star",
                Filter = "AP KSS patch (*.apkss)|*.apkss|All files (*.*)|*.*",
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

        return panel;
    }
}
