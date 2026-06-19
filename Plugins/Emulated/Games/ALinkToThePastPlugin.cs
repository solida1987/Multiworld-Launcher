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
// ALinkToThePastPlugin — Archipelago integration for A Link to the Past (SNES).
//
// STATUS: Bridge implemented end-to-end (patch apply + checks + items).
// ─────────────────────────────────────────────────────────────────────
// Launch chain:
//   1. BizHawk is auto-installed from GitHub releases on first install.
//   2. The user supplies their own VANILLA US 1.0 ROM (never shipped, picked in
//      Settings; copied into the launcher library).
//   3. PrepareSessionRomAsync applies the multiworld seed's .aplttp patch to a
//      library COPY (<rom>_<patch>.sfc) via SnesApPatchHelper. The original ROM
//      is never modified. Vanilla fallback launches with a loud note.
//   4. EmuHawk starts with Plugins/Scripts/bizhawk_ap_connector.lua, which loads
//      Plugins/Scripts/games/alttp.lua (LuaModuleName).
//   5. Connector ↔ launcher named pipe (byte mode, newline-framed):
//      CHECK:/GOAL/SYNC up, ITEM:(extended)/SYNCEND down.
//
// THE PATCH (.aplttp) is an APProcedurePatch ZIP (bsdiff4 + tokens) — the same
// family as Emerald's .apemerald, handled by the shared SnesApPatchHelper +
// Plugins/Scripts/apply_appatch.py. alttp.lua carries the full address map and
// the four location tables mirrored from worlds/alttp/Client.py (see
// Research_V2/SNES_BRIDGE_2026-06-12.md), and self-disables on a non-AP ROM.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ALinkToThePastPlugin : EmulatorPlugin
{
    public ALinkToThePastPlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "alttp";
    public override string DisplayName => "A Link to the Past";
    public override string Subtitle    => "SNES · Emulated";
    public override string ApWorldName => "A Link to the Past";

    public override string Description =>
        "A Link to the Past is the SNES classic that defined the top-down Zelda " +
        "formula. The Archipelago randomizer shuffles every chest, dungeon prize, " +
        "and overworld item into the multiworld pool. Rescue the maidens and " +
        "defeat Ganon to complete your goal.";

    // ── Visual identity ───────────────────────────────────────────────────────

    public override string ThemeAccentColor => "#C9A227";   // Triforce gold

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "SNES";
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "alttp";

    // §14: the SNES emulators this world is played on. BizHawk is verified; the
    // §14 Discord ask was "SNES: BizHawk or snes9x", so snes9x is the headline
    // alternative, with Mesen as a third option (both "coming soon" until their
    // bridge is verified in-emulator).
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "snes9x", "mesen" };

    private const string PatchExt  = ".aplttp";
    private const string ResultExt = ".sfc";

    /// The vanilla LttP cartridge dump the randomizer is built for, identified by
    /// CONTENT (1 MB + MD5), never by filename. This is the long-standing
    /// community-known US 1.0 hash every AP LttP generator validates against;
    /// apply_appatch.py independently re-checks the patch container's own
    /// base_checksum before patching, so this only drives the launcher's
    /// "wrong version" hint.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms
        => new[]
        {
            new RomIdentity(1024 * 1024,                          // 1,048,576 bytes
                            "03a63945398191337e896e5771f77173",
                            "The Legend of Zelda: A Link to the Past (USA)"),
        };

    /// alttp.lua mirrors the official SNIClient: underworld/overworld/NPC/misc
    /// flag scan → AP location ids, the RECV_ITEM receive protocol, and the
    /// credits-mode goal. All addresses are SOURCE-DERIVED from the apworld
    /// client; needs a real playthrough to confirm live (see research doc §6).
    public override bool ChecksImplemented => true;

    /// The A Link to the Past datapackage checksum is announced by the server in
    /// RoomInfo, not embedded as a constant in the apworld; left null. The
    /// launcher's "apworld updated" warning is simply not raised for this game
    /// until a live RoomInfo checksum is captured and pinned here.
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

    /// ROM safety net: when a patch is known, demand exactly its base ROM
    /// (base_checksum) so the launcher can ask the player by fingerprint instead
    /// of silently patching the wrong dump.
    public override RomRequirement? GetUnmetRomRequirement()
    {
        string? wantMd5 = null;
        var patch = ResolvePatchPath(out _);
        if (patch != null) wantMd5 = SnesApPatchHelper.ReadManifestField(patch, "base_checksum");

        bool haveRom = RomPath != null && File.Exists(RomPath);

        if (wantMd5 == null)
            return haveRom ? null
                 : new RomRequirement("A Link to the Past", "SNES",
                       "The Legend of Zelda: A Link to the Past (USA)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement(
            "A Link to the Past", "SNES",
            "The Legend of Zelda: A Link to the Past (USA) — the original vanilla cartridge dump",
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

    // ── Session ROM: apply the .aplttp to a library copy ──────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = ResolvePatchPath(out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[A Link to the Past] No AP patch (.aplttp) found — launching the " +
                "vanilla ROM. Checks and ITEM DELIVERY CANNOT WORK on a vanilla ROM: " +
                "generate the multiworld, then pick the patch in Settings → A Link to " +
                "the Past (or drop it under " + SnesApPatchHelper.ApOutputDirectory + ").";
            return null;
        }

        if (RomPath == null || !File.Exists(RomPath))
        {
            SessionRomNote =
                "[A Link to the Past] No base ROM set — import your vanilla US 1.0 " +
                "cartridge dump in Settings, then relaunch.";
            return null;
        }

        var result = await SnesApPatchHelper.ApplyAsync(
            "A Link to the Past", patch, how, RomPath!, RomLibraryDirectory, ResultExt, ct);
        SessionRomNote = result.Note;
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
            Text = "The multiworld generator produces a .aplttp patch for your slot. " +
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
                    : $"No .aplttp found under {SnesApPatchHelper.ApOutputDirectory} yet.";
        }
        RefreshAutoNote();

        pickBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select Archipelago patch for A Link to the Past",
                Filter = "AP LttP patch (*.aplttp)|*.aplttp|All files (*.*)|*.*",
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
