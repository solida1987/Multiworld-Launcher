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
// SecretOfEvermorePlugin — Archipelago integration for Secret of Evermore (SNES).
//
// STATUS: ROM PATCH FLOW is implemented; IN-EMULATOR CHECK DETECTION IS NOT —
// and CANNOT be, faithfully, from the apworld. ChecksImplemented => false.
// ─────────────────────────────────────────────────────────────────────────
// Secret of Evermore is the lone Archipelago SNES world that has NO in-emulator
// memory-scanning client. Source reviewed (worlds/soe @ main): there is no
// per-game Client.py, and an org-wide search finds no SNIClient subclass for it.
// Instead the repo-root SNIClient.py special-cases the patch suffix and launches
// a CLOSED-SOURCE BROWSER CLIENT:
//
//     if args.diff_file.endswith(".apsoe"):
//         webbrowser.open("http://www.evermizer.com/apclient/#server=...")
//         ... sys.exit()                       (SNIClient.py ~line 700)
//
// All location-"checked" detection lives inside that evermizer.com web app, which
// talks to the AP server and to the emulator directly. The apworld itself carries
// ZERO memory addresses (no WRAM/SRAM map, no flag tables, no validate_rom
// signature, no game_watcher); the item/location lists come from the compiled
// `pyevermizer` binary wheel, not source. So there is no source-derived flag table
// to PARSE the way every other shipped game's table was — fabricating one would
// break the project's "source-derived, no hand-copy" rule and risk mis-reporting.
// soe.lua is therefore inert-by-design and fully documents this; this plugin
// reports ChecksImplemented = false so the launcher warns honestly at launch.
//
// WHAT THIS PLUGIN DOES DO
//   • Applies the seed's .apsoe (APDeltaPatch — same container family as LttP's
//     .aplttp / Emerald's .apemerald) to a library COPY of the user's vanilla US
//     ROM via the shared SnesApPatchHelper → a playable .sfc. The original ROM is
//     never modified. The player then runs SoE's own evermizer.com browser client
//     next to the emulator to send checks and receive items (SessionRomNote says
//     so explicitly).
//
// BASE ROM: the user's own Secret of Evermore (USA) cartridge dump, identified by
// CONTENT (3,145,728 bytes + MD5 6e9c9451…) — never by filename (§11). The patch
// container's own base_checksum is re-validated by apply_appatch.py before any
// bytes are written.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SecretOfEvermorePlugin : EmulatorPlugin
{
    public SecretOfEvermorePlugin() => LoadSettings();

    // ── Identity ──────────────────────────────────────────────────────────────

    public override string GameId      => "soe";
    public override string DisplayName => "Secret of Evermore";
    public override string Subtitle    => "SNES · Emulated";
    public override string ApWorldName => "Secret of Evermore";

    public override string Description =>
        "Secret of Evermore is Square's 1995 SNES action-RPG, where a boy and his " +
        "dog journey through Prehistoria, Antiqua, Gothica, and Omnitopia, mixing " +
        "ingredients into alchemy formulas. The Archipelago randomizer (Evermizer) " +
        "shuffles alchemy, gourds, bosses, NPC gifts, and more into the multiworld " +
        "pool. NOTE: Secret of Evermore's Archipelago client runs in your WEB " +
        "BROWSER (evermizer.com), not inside the emulator — the launcher applies " +
        "your seed patch and starts the ROM, and you drive checks/items from that " +
        "browser client alongside the game.";

    // ── Visual identity ───────────────────────────────────────────────────────

    public override string ThemeAccentColor => "#3E7A4E";   // Prehistoria jungle green

    // ── Emulator specifics ────────────────────────────────────────────────────

    protected override string RomSystem     => "SNES";
    protected override string LuaScriptName => "bizhawk_ap_connector.lua";
    protected override string LuaModuleName => "soe";

    // §14: the SNES emulators this world could be played on. BizHawk is the
    // launcher's verified backend; snes9x / Mesen are the alternatives shown per
    // their bridge state. (For SoE the in-emulator bridge is inert regardless —
    // detection happens in the browser client — but the choice is offered for
    // consistency with the other SNES titles.)
    protected override IReadOnlyList<string> SupportedEmulatorIds =>
        new[] { "bizhawk", "snes9x", "mesen" };

    private const string PatchExt  = ".apsoe";
    private const string ResultExt = ".sfc";

    /// The vanilla Secret of Evermore (USA) cartridge dump the randomizer is built
    /// for, identified by CONTENT (3,145,728 bytes + MD5), never by filename. This
    /// is the US hash the apworld pins (worlds/soe/patch.py USHASH); apply_appatch.py
    /// independently re-checks the patch container's own base_checksum before
    /// patching, so this only drives the launcher's "wrong version" hint.
    protected override IReadOnlyList<RomIdentity> AcceptableBaseRoms
        => new[]
        {
            new RomIdentity(3 * 1024 * 1024,                     // 3,145,728 bytes (24 Mbit)
                            "6e9c94511d04fac6e0a1e582c170be3a",
                            "Secret of Evermore (USA)"),
        };

    /// FALSE on purpose: Secret of Evermore has no in-emulator AP client — its
    /// checks/items are handled by the evermizer.com browser client, and the
    /// apworld carries no memory map to detect checks from (see the class header
    /// and soe.lua). The launcher's launch-time warning is the honest outcome:
    /// the ROM patch flow works, but this bridge will not report checks.
    public override bool ChecksImplemented => false;

    /// The Secret of Evermore datapackage checksum is announced by the server in
    /// RoomInfo, not embedded as a constant in the apworld; left null.
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
                 : new RomRequirement("Secret of Evermore", "SNES",
                       "Secret of Evermore (USA)",
                       RequiredMd5: null, WrongVersionPresent: false, BuildRomFilter());

        if (haveRom && ComputeMd5(RomPath!).Equals(wantMd5, StringComparison.OrdinalIgnoreCase))
            return null;

        return new RomRequirement(
            "Secret of Evermore", "SNES",
            "Secret of Evermore (USA) — the original vanilla cartridge dump",
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

    // ── Session ROM: apply the .apsoe to a library copy ───────────────────────

    protected override async Task<string?> PrepareSessionRomAsync(
        ApSession session, CancellationToken ct)
    {
        string? patch = ResolvePatchPath(out string how);
        if (patch == null)
        {
            SessionRomNote =
                "[Secret of Evermore] No AP patch (.apsoe) found — launching the " +
                "vanilla ROM. Generate the multiworld, then pick the patch in " +
                "Settings → Secret of Evermore (or drop it under " +
                SnesApPatchHelper.ApOutputDirectory + "). Reminder: Secret of " +
                "Evermore checks/items run through the evermizer.com BROWSER client, " +
                "not the in-emulator bridge.";
            return null;
        }

        if (RomPath == null || !File.Exists(RomPath))
        {
            SessionRomNote =
                "[Secret of Evermore] No base ROM set — import your vanilla " +
                "Secret of Evermore (USA) cartridge dump in Settings, then relaunch.";
            return null;
        }

        var result = await SnesApPatchHelper.ApplyAsync(
            "Secret of Evermore", patch, how, RomPath!, RomLibraryDirectory, ResultExt, ct);
        SessionRomNote = result.Note +
            " Note: drive checks/items through the Secret of Evermore browser client " +
            "at evermizer.com — the in-emulator bridge does not report checks for " +
            "this game.";
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
            Text = "The multiworld generator produces a .apsoe patch for your slot. " +
                   "The launcher applies it to a COPY of your ROM at launch (your file " +
                   "is never modified). Leave empty to auto-use the newest patch from " +
                   "the Archipelago output folder. Secret of Evermore's checks and " +
                   "items are handled by its browser client at evermizer.com, opened " +
                   "alongside the emulator — the in-emulator bridge stays idle for " +
                   "this game.",
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
                    : $"No .apsoe found under {SnesApPatchHelper.ApOutputDirectory} yet.";
        }
        RefreshAutoNote();

        pickBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select Archipelago patch for Secret of Evermore",
                Filter = "AP SoE patch (*.apsoe)|*.apsoe|All files (*.*)|*.*",
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
