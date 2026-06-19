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
// SuperMetroidPlugin — Archipelago integration for Super Metroid (SNES).
//
// STATUS: Patch apply implemented; in-game CHECK/ITEM bridge SHIPPED GATED.
// ─────────────────────────────────────────────────────────────────────
//   • Patch apply WORKS: the seed's .apsm container (an APProcedurePatch ZIP —
//     IPS basepatch + tokens) is applied to a library COPY via SnesApPatchHelper
//     + Plugins/Scripts/apply_appatch.py (which now understands the apply_ips
//     procedure step). The player gets a fully playable AP ROM, original never
//     touched.
//   • super_metroid.lua carries the REAL send/recv ring-queue protocol mirrored
//     from worlds/sm/Client.py, but it is held behind ADDRESSES_VERIFIED=false:
//     SM location/item ids are RELATIVE to per-slot base ids
//     (locations_start_id / items_start_id) that come from the live server's
//     data package and are not yet carried in ap_config.json. Until that
//     handshake exists the module loads and runs as a safe no-op, so
//     ChecksImplemented stays false (honest: the ROM plays, but our bridge does
//     not yet read its checks). See Research_V2/SNES_BRIDGE_2026-06-12.md §3e.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SuperMetroidPlugin : EmulatorPlugin
{
    public SuperMetroidPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "super_metroid";
    public override string DisplayName => "Super Metroid";
    public override string Subtitle    => "SNES · Emulated";
    public override string ApWorldName => "Super Metroid";

    public override string Description =>
        "Super Metroid is the SNES exploration masterpiece set on planet Zebes. " +
        "In the Archipelago randomizer, every missile pack, energy tank, and major " +
        "upgrade joins the multiworld pool. Defeat Mother Brain and escape Zebes " +
        "to complete your goal.";

    // ── Visual identity ───────────────────────────────────────────────────────

    public override string ThemeAccentColor => "#C75B12";   // Varia Suit orange

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "SNES";
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "super_metroid";

    // §14: SNES emulators for this world — BizHawk verified, snes9x the §14
    // headline alternative, Mesen third ("coming soon" until bridge-verified).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "snes9x", "mesen" };

    private const string PatchExt  = ".apsm";
    private const string ResultExt = ".sfc";

    /// The vanilla Super Metroid cartridge dump the randomizer is built for,
    /// identified by CONTENT (3 MB + MD5), never by filename. Long-standing
    /// community-known US/JU hash; apply_appatch.py re-validates the patch
    /// container's own base_checksum before patching, so this only drives the
    /// launcher's "wrong version" hint.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms
        => new[]
        {
            new RomIdentity(3 * 1024 * 1024,                     // 3,145,728 bytes
                            "21f3e98df4780ee1c667b84e57d88675",
                            "Super Metroid (Japan, USA)"),
        };

    /// FALSE on purpose: super_metroid.lua implements the full protocol but is
    /// gated until the launcher passes locations_start_id / items_start_id from
    /// the connected room (SM ids are slot-relative). The patched ROM plays;
    /// our bridge does not yet read its checks. Flip to true with the module's
    /// ADDRESSES_VERIFIED once that handshake lands.
    public override bool ChecksImplemented => false;

    /// Announced by the server in RoomInfo, not embedded in the apworld; null.
    public string? BuiltAgainstDataPackageChecksum => null;

    // ── AP patch settings ─────────────────────────────────────────────────────

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

    public void SetExplicitPatch(string path) { ApPatchPath = path; SaveSettings(); }

    public override RomRequirement? GetUnmetRomRequirement()
    {
        string? wantMd5 = null;
        var patch = ResolvePatchPath(out _);
        if (patch != null) wantMd5 = SnesApPatchHelper.ReadManifestField(patch, "base_checksum");

        bool haveRom = RomPath != null && File.Exists(RomPath);

        if (wantMd5 == null)
            return haveRom ? null
                 : new RomRequirement("Super Metroid", "SNES",
                       "Super Metroid (Japan, USA)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement(
            "Super Metroid", "SNES",
            "Super Metroid (Japan, USA) — the original vanilla cartridge dump",
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

    // ── Session ROM: apply the .apsm to a library copy ────────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = ResolvePatchPath(out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Super Metroid] No AP patch (.apsm) found — launching the vanilla " +
                "ROM. Generate the multiworld, then pick the patch in Settings → " +
                "Super Metroid (or drop it under " +
                SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        if (RomPath == null || !File.Exists(RomPath))
        {
            SessionRomNote =
                "[Super Metroid] No base ROM set — import your vanilla Super Metroid " +
                "cartridge dump in Settings, then relaunch.";
            return null;
        }

        var result = await SnesApPatchHelper.ApplyAsync(
            "Super Metroid", patch, how, RomPath!, RomLibraryDirectory, ResultExt, ct);
        // Append the in-game-bridge caveat so the note is honest about checks.
        string caveat = result.OutRom != null
            ? " NOTE: the ROM is playable, but in-game check/item detection is not " +
              "yet active for Super Metroid in this launcher (pending the slot " +
              "base-id handshake — see the SNES bridge research doc)."
            : "";
        SessionRomNote = result.Note + caveat;
        if (result.OutRom != null) RegisterSeed(result.OutRom, patch);
        return result.OutRom;
    }

    // ── Settings panel: base panel + AP patch section ─────────────────────────

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
            Text = "The multiworld generator produces a .apsm patch for your slot. " +
                   "The launcher applies it to a COPY of your ROM at launch (your file " +
                   "is never modified). Leave empty to auto-use the newest patch from " +
                   "the Archipelago output folder. (In-game check/item detection is " +
                   "not yet active for Super Metroid — the ROM still plays normally.)",
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
                    : $"No .apsm found under {SnesApPatchHelper.ApOutputDirectory} yet.";
        }
        RefreshAutoNote();

        pickBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select Archipelago patch for Super Metroid",
                Filter = "AP Super Metroid patch (*.apsm)|*.apsm|All files (*.*)|*.*",
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
