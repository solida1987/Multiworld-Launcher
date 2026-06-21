using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LauncherV2.Core;

// NOTE on type qualification (BUILD GOTCHA):
// This project sets BOTH <UseWPF>true</UseWPF> and <UseWindowsForms>true</UseWindowsForms>.
// That makes a long list of simple type names ambiguous between WPF and WinForms
// (Clipboard, MessageBox, Application, Color, Brush(es), Button, TextBox, CheckBox,
// Orientation, FontWeights, HorizontalAlignment, Cursors, …). To avoid CS0104 this
// file deliberately does NOT do `using System.Windows.Controls;` /
// `using System.Windows.Media;` — every WPF UI type below is written fully qualified
// (System.Windows.Controls.*, System.Windows.Media.*, System.Windows.MessageBox, …).

namespace LauncherV2.Plugins.Deltarune;

// ═══════════════════════════════════════════════════════════════════════════════
// DeltarunePlugin — install-guide / detect / launch for "DELTARUNE" (Toby Fox)
// played through the DELTARUNEAP third-party Archipelago mod.
//
// WHAT KIND OF INTEGRATION IS THIS? (verified 2026-06-14 — see REALITY CHECK)
// ─────────────────────────────────────────────────────────────────────────────
// Deltarune's Archipelago support is a THIRD-PARTY mod by theemeraldsword85,
// distributed as a standalone .apworld file from GitHub Releases. Unlike
// Undertale (which ships inside AP-main), the DELTARUNEAP repo is a fork of
// the Archipelago framework with Deltarune worlds added.
//
// The pieces are:
//   * deltarune.apworld — the custom world file placed in AP's custom_worlds
//     directory. Source: https://github.com/theemeraldsword85/DELTARUNEAP/releases
//   * DeltaruneClient — a Python client shipped with the .apworld fork. The
//     player runs it from their Archipelago folder (Component "DELTARUNE Client").
//     It owns the AP slot connection.
//   * A bsdiff4 patch (ch1.bsdiff, ch2.bsdiff, ch3.bsdiff, ch4.bsdiff,
//     deltarune.bsdiff) applied to the player's own DELTARUNE 1.04 data.win files.
//   * The player's DELTARUNE 1.04 installation — available free from deltarune.com
//     OR on Steam (appid 1671210). The mod REQUIRES version 1.04 (last public
//     Steam branch); Beta and Ch1+2 Demo will not work.
//
// WHY ConnectsItself = true:
//   The patched GameMaker game communicates through save-data files under
//   %localappdata%\DELTARUNEAP; the DeltaruneClient watches those files and
//   talks to the AP server. Connection info (host:port, slot, password) is
//   entered IN-GAME via "Change connection info" in the DELTARUNE title screen.
//   The launcher's own ApClient must NOT also sit on the slot.
//
// WHAT THIS PLUGIN HONESTLY DOES:
//   * DETECT the player's DELTARUNE installation (Steam appid 1671210 or a
//     user-set folder) and the PATCHED copy at the Archipelago DELTARUNE dir.
//   * GUIDE the irreducible steps: get DELTARUNE 1.04, install the .apworld,
//     run /auto_patch once from the DeltaruneClient.
//   * LAUNCH the patched DELTARUNE.exe from the detected/override patched folder.
//   * TRACK available version from GitHub Releases API.
//
// REALITY CHECK (2026-06-14) — facts verified against theemeraldsword85/DELTARUNEAP:
//   * AP game string: "DELTARUNE" — VERIFIED in worlds/deltarune/__init__.py
//     (game = "DELTARUNE") AND DeltaruneClient.py (self.game = "DELTARUNE").
//   * Patching: DeltaruneClient.py copies the vanilla DELTARUNE folder into
//     Utils.user_path("DELTARUNE", ...) and patches these data.win files via bsdiff4:
//       chapter1_windows/data.win (ch1.bsdiff)
//       chapter2_windows/data.win (ch2.bsdiff)
//       chapter3_windows/data.win (ch3.bsdiff)
//       chapter4_windows/data.win (ch4.bsdiff)
//       data.win                  (deltarune.bsdiff)
//   * Patched game lives at: <AP user dir>\DELTARUNE\
//   * Save data: %localappdata%\DELTARUNEAP  (DeltaruneContext.save_game_folder)
//   * Connection: in-game "Change connection info" dialog on the title screen.
//     Host:port can be pasted directly into the host field. Slot + password set there.
//   * /auto_patch command: run from DeltaruneClient in your Archipelago folder.
//     `/auto_patch <your DELTARUNE Install Directory>` or `/auto_patch steaminstall`
//     or `/auto_patch steamdepot` for Steam installs.
//   * DELTARUNE 1.04 required (last public branch on Steam). Beta/Demo will not work.
//   * DELTARUNE Steam appid: 1671210 (verified from DeltaruneClient.py: depotid 1671212,
//     path app_1671210).
//   * Latest release at time of writing: v2.0.2 (deltarune.apworld).
//
// DEFENSIVE / UNVERIFIED DETAILS:
//   * Patched game exe name: The setup guide says to run the DELTARUNE application
//     inside the <Archipelago>\DELTARUNE folder. GameMaker Studio games are compiled
//     as "DELTARUNE.exe" by convention. ResolvePatchedExe() tries that name first.
//   * IsInstalled is true ONLY when a runnable game exe is found in the patched folder.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DeltarunePlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    /// DELTARUNE Steam application id.
    private const string SteamAppId = "1671210";

    /// Standard Steam install sub-folder name (steamapps\common\DELTARUNE).
    private const string SteamCommonFolderName = "DELTARUNE";

    /// The GameMaker runtime exe name for DELTARUNE (preferred patched exe).
    private const string PreferredExeName = "DELTARUNE.exe";

    /// Official setup guide.
    private const string SetupGuideUrl =
        "https://github.com/theemeraldsword85/DELTARUNEAP/blob/main/worlds/deltarune/docs/setup_en.md";

    /// DELTARUNEAP GitHub Releases page.
    private const string ApWorldReleasesUrl =
        "https://github.com/theemeraldsword85/DELTARUNEAP/releases";

    /// DELTARUNEAP GitHub Releases API endpoint (for news + version check).
    private const string ApWorldReleasesApiUrl =
        "https://api.github.com/repos/theemeraldsword85/DELTARUNEAP/releases";

    /// DELTARUNE free download page (official — Toby Fox).
    private const string DeltaruneFreeUrl = "https://deltarune.com/";

    /// DELTARUNE on Steam (free, appid 1671210).
    private const string SteamStoreUrl =
        "https://store.steampowered.com/app/1671210/";

    /// Archipelago releases page (the player needs AP installed to run the client).
    private const string ApReleasesUrl =
        "https://github.com/ArchipelagoMW/Archipelago/releases";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "deltarune";
    public string DisplayName => "Deltarune";
    public string Subtitle    => "Native PC · Third-party APWorld";

    /// EXACT AP game string — VERIFIED against worlds/deltarune/__init__.py
    /// (game = "DELTARUNE") and DeltaruneClient.py (self.game = "DELTARUNE").
    public string ApWorldName => "DELTARUNE";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "deltarune.png");

    public string ThemeAccentColor => "#8040C0";   // purple (Deltarune title theme)
    public string[] GameBadges     => new[] { "Free · 3rd-party APWorld" };

    public string Description =>
        "DELTARUNE is Toby Fox's ongoing episodic RPG set in the world below Undertale. " +
        "You play as Kris, navigating a school, a magical Dark World, and the mysteries " +
        "between chapters. This Archipelago integration (by theemeraldsword85) shuffles " +
        "items, weapons, armor, and chapter progression across your multiworld. " +
        "DELTARUNE is free to download from deltarune.com and also on Steam (free). " +
        "Requires DELTARUNE version 1.04 (last public Steam branch). " +
        "The DELTARUNEAP client patches your game files once and handles all " +
        "multiworld communication; connection details are entered in-game.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// Installed == a runnable patched DELTARUNE exe is present in the resolved
    /// patched folder. Owning only the unpatched base game does NOT flip this true.
    public bool IsInstalled => ResolvePatchedExe() != null;
    public bool IsRunning   { get; private set; }

    /// ConnectsItself: the in-game connection dialog + DeltaruneClient own the AP slot.
    /// The launcher must NOT connect its own ApClient to the same slot.
    public bool ConnectsItself => true;

    /// DELTARUNE is a complete standalone game — launching without the client simply
    /// means nothing is connected to the multiworld.
    public bool SupportsStandalone => true;

    /// Reports the resolved patched folder when known, else "".
    public string GameDirectory => ResolvePatchedDir() ?? "";

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// This plugin's own settings sidecar. BOM-less UTF-8.
    private string SettingsSidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                        "deltarune_launcher.json");

    private string SettingsSidecarDir => Path.GetDirectoryName(SettingsSidecarPath)!;

    /// User-set override pointing at the PATCHED Archipelago DELTARUNE folder (the
    /// one created by /auto_patch, e.g. <Archipelago user dir>\DELTARUNE). Optional.
    private string? _overridePatchedDir;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The DeltaruneClient owns the AP slot. These exist only for interface
    // compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Constructor — restore previously chosen patched folder ────────────────

    public DeltarunePlugin()
    {
        try
        {
            string? saved = LoadSettings().PatchedDir;
            if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
                _overridePatchedDir = saved;
        }
        catch { /* fall back to detection only */ }
    }

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────
    // Queries the DELTARUNEAP GitHub Releases API to find the latest tag.
    // Never throws on network failure.

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            // CDN HEAD redirect — no REST API quota consumed. GitHub's
            // /releases/latest already resolves to the newest non-prerelease.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync("theemeraldsword85", "DELTARUNEAP", ct));
        }
        catch { /* non-fatal */ }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────
    // Honest guided setup: nothing for the launcher to download autonomously
    // (the bsdiff patches live inside the .apworld; the base game is the player's
    // own copy). Reports detection state and the exact remaining manual steps.

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((10, "Checking for your DELTARUNE installation..."));

        string? steamDir   = DetectSteamInstallDir();
        string? patchedDir = ResolvePatchedDir();
        bool    patched    = ResolvePatchedExe() != null;

        if (patched)
        {
            progress.Report((100,
                $"Patched DELTARUNE found at \"{patchedDir}\". Press Play to launch it. " +
                "Connection details are entered in-game via \"Change connection info\" " +
                "on the title screen. Also start the DELTARUNE Client from Archipelago."));
            return Task.CompletedTask;
        }

        if (steamDir != null)
        {
            progress.Report((100,
                "DELTARUNE detected at \"" + steamDir + "\", but the Archipelago " +
                "(patched) copy was not found yet. To finish setup: " +
                "1) Place deltarune.apworld in your Archipelago custom_worlds folder. " +
                "2) Open Archipelago, start the \"DELTARUNE Client\", and run  " +
                "/auto_patch \"" + steamDir + "\"  once. " +
                "3) Point this plugin at the \\DELTARUNE folder it created (Settings). " +
                "Requires DELTARUNE 1.04 (last public Steam branch). " +
                "See the Setup Guide link in Settings."));
        }
        else
        {
            progress.Report((100,
                "DELTARUNE was not detected. " +
                "DELTARUNE is FREE — download it from deltarune.com or Steam (free, appid 1671210). " +
                "Requires version 1.04 (last public Steam branch — Beta and Demo will not work). " +
                "After installing: " +
                "1) Place deltarune.apworld in your Archipelago custom_worlds folder. " +
                "2) Start the \"DELTARUNE Client\" from Archipelago and run  " +
                "/auto_patch <your DELTARUNE folder>  or /auto_patch steaminstall. " +
                "3) Point this plugin at the \\DELTARUNE folder it creates (Settings). " +
                "See the Setup Guide link in Settings."));
        }
        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public Task<bool> VerifyInstallAsync(CancellationToken ct = default)
        => Task.FromResult(IsInstalled);

    // ── Lifecycle — Launch ────────────────────────────────────────────────────
    // Opens the patched game. Connection is entered in-game via "Change connection
    // info" on the title screen — we do NOT pass args and do NOT hold an ApClient.

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        _ = session; // connection is made in-game, not via command-line args
        LaunchPatchedGame();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        LaunchPatchedGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The DeltaruneClient receives items from the AP server and relays them into
        // the game via %localappdata%\DELTARUNEAP save files. Nothing to forward here.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // Connection state is managed in-game and by the DeltaruneClient.
    }

    // ── Validation of a user-picked patched folder ────────────────────────────

    /// Accept the folder only when it contains a runnable DELTARUNE exe. Returns
    /// null when acceptable, else a short reason so the user can pick again.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick the \\DELTARUNE folder that " +
                   "the DELTARUNE Client created with /auto_patch.";
        try
        {
            if (FindPatchedExeIn(folder) != null) return null;
        }
        catch
        {
            return "Could not read that folder. Pick a different one and try again.";
        }
        return "That folder does not contain a runnable DELTARUNE game (\"DELTARUNE.exe\"). " +
               "In Archipelago, run the \"DELTARUNE Client\" and use /auto_patch first, " +
               "then pick the \\DELTARUNE folder it created.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD0, 0xA0, 0x40));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x80, 0x40, 0xC0));

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "DELTARUNE's Archipelago support is a third-party mod (DELTARUNEAP by " +
                "theemeraldsword85). DELTARUNE is FREE (deltarune.com or Steam). " +
                "The DELTARUNEAP Client patches your game files once; connection details " +
                "(server, slot, password) are entered IN-GAME via \"Change connection info\" " +
                "on the title screen — not in the launcher. Requires DELTARUNE version 1.04 " +
                "(last public Steam branch). Beta versions and the Ch1+2 Demo will not work.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: DELTARUNE base game (detected) ───────────────────────
        string? steamDir = DetectSteamInstallDir();
        panel.Children.Add(SectionHeader("DELTARUNE BASE GAME", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = steamDir != null
                ? "Detected via Steam (appid " + SteamAppId + "):\n" + steamDir
                : "DELTARUNE not detected via Steam. " +
                  "DELTARUNE is FREE — get it from deltarune.com or Steam (free, appid " +
                  SteamAppId + "). Requires version 1.04 (last public Steam branch).",
            FontSize = 11, Foreground = steamDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Patched Archipelago copy ────────────────────────────
        panel.Children.Add(SectionHeader("ARCHIPELAGO (PATCHED) DELTARUNE FOLDER", muted));

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = _overridePatchedDir ?? ResolvePatchedDir() ?? "",
            IsReadOnly = true, FontSize = 12, Margin = new Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Browse...", Width = 90, Padding = new Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };

        var statusText = new System.Windows.Controls.TextBlock
        {
            FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        };
        void RefreshStatus()
        {
            bool ok = ResolvePatchedExe() != null;
            statusText.Text = ok
                ? "Patched DELTARUNE found here — ready to launch."
                : "Patched game not found here yet. Run Archipelago's \"DELTARUNE " +
                  "Client\" and use /auto_patch, then point this at the \\DELTARUNE " +
                  "folder it created.";
            statusText.Foreground = ok ? success : muted;
        }
        RefreshStatus();

        dirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select your Archipelago (patched) DELTARUNE folder",
                InitialDirectory = Directory.Exists(_overridePatchedDir ?? "")
                    ? _overridePatchedDir!
                    : (steamDir != null && Directory.Exists(steamDir)
                        ? steamDir
                        : AppContext.BaseDirectory),
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    var go = System.Windows.MessageBox.Show(
                        bad + "\n\nUse this folder anyway?",
                        "No patched game found here",
                        System.Windows.MessageBoxButton.YesNo,
                        System.Windows.MessageBoxImage.Warning);
                    if (go != System.Windows.MessageBoxResult.Yes) return;
                }
                _overridePatchedDir = picked;
                dirBox.Text = picked;
                SavePatchedDir(picked);
                RefreshStatus();
            }
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);
        panel.Children.Add(statusText);

        // ── Section: Version info ─────────────────────────────────────────
        panel.Children.Add(SectionHeader("VERSION", muted));
        string verText = "Available: " + (AvailableVersion ?? "unknown (check for updates)");
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = verText, FontSize = 11, Foreground = muted,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Setup & connection steps ─────────────────────────────
        panel.Children.Add(SectionHeader("SETUP & CONNECTION", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "1) Get DELTARUNE 1.04 (free from deltarune.com or Steam, free, appid " + SteamAppId + ").\n" +
                "   IMPORTANT: Must be version 1.04 (last public branch). Beta and Demo will not work.\n\n" +
                "2) Download deltarune.apworld from the DELTARUNEAP Releases page and " +
                "place it in your Archipelago custom_worlds folder " +
                "(or double-click the .apworld to install).\n\n" +
                "3) Open Archipelago, start the \"DELTARUNE Client\", and run:\n" +
                "      /auto_patch steaminstall\n" +
                "   or  /auto_patch \"<your DELTARUNE folder>\"\n" +
                "   This copies and patches your DELTARUNE installation into your " +
                "Archipelago folder under \\DELTARUNE.\n\n" +
                "4) Point the \"Archipelago (patched) DELTARUNE folder\" above at that \\DELTARUNE folder.\n\n" +
                "To play: press Play here (or launch the patched game directly). " +
                "On the DELTARUNE title screen, choose \"Change connection info\" " +
                "and enter your host:port (e.g. archipelago.gg:38281), slot name, " +
                "and password. You can paste host:port directly into the host field. " +
                "Connection is handled in-game — the launcher does not connect to your slot.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("DELTARUNE (Free Download) ↗",    DeltaruneFreeUrl),
            ("DELTARUNE on Steam (Free) ↗",    SteamStoreUrl),
            ("DELTARUNEAP Releases ↗",         ApWorldReleasesUrl),
            ("DELTARUNEAP Setup Guide ↗",      SetupGuideUrl),
            ("Archipelago Releases ↗",         ApReleasesUrl),
            ("Archipelago Official ↗",         "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => { try { Process.Start(
                new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
            panel.Children.Add(btn);
        }
        return panel;
    }

    private static System.Windows.Controls.TextBlock SectionHeader(
        string text, System.Windows.Media.Brush muted)
        => new System.Windows.Controls.TextBlock
        {
            Text = text, FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        };

    // ── News feed ─────────────────────────────────────────────────────────────
    // Pull from the DELTARUNEAP GitHub Releases API. Never throws — empty on failure.

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(ApWorldReleasesApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var news = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                news.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (news.Count >= 10) break;
            }
            return news.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — patched-folder / exe resolution ─────────────────────

    /// The resolved PATCHED DELTARUNE folder: the user override if it still exists,
    /// else null. The Steam base folder is NOT the Archipelago patched copy.
    private string? ResolvePatchedDir()
    {
        if (!string.IsNullOrWhiteSpace(_overridePatchedDir) &&
            Directory.Exists(_overridePatchedDir))
            return _overridePatchedDir;
        return null;
    }

    /// The runnable PATCHED DELTARUNE exe inside the resolved patched folder, or null.
    private string? ResolvePatchedExe()
    {
        string? dir = ResolvePatchedDir();
        if (dir == null) return null;
        try { return FindPatchedExeIn(dir); }
        catch { return null; }
    }

    /// Find a runnable DELTARUNE exe in `dir`: prefer DELTARUNE.exe, else a fuzzy
    /// "*deltarune*.exe", else (last resort) a lone non-helper .exe.
    private static string? FindPatchedExeIn(string dir)
    {
        if (!Directory.Exists(dir)) return null;

        string preferred = Path.Combine(dir, PreferredExeName);
        if (File.Exists(preferred)) return preferred;

        string[] exes;
        try { exes = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly); }
        catch { return null; }

        // Fuzzy "deltarune" exe.
        foreach (string exe in exes)
        {
            string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
            if (IsHelperExe(name)) continue;
            if (name.Contains("deltarune")) return exe;
        }

        // Last resort: a single non-helper exe in the folder.
        string? only = null;
        int count = 0;
        foreach (string exe in exes)
        {
            string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
            if (IsHelperExe(name)) continue;
            only = exe; count++;
        }
        return count == 1 ? only : null;
    }

    /// Names that are NOT the runnable game (uninstaller, helpers, the Steam shim).
    private static bool IsHelperExe(string nameLowerNoExt)
        => nameLowerNoExt.Contains("unins")    ||
           nameLowerNoExt.Contains("setup")    ||
           nameLowerNoExt.Contains("crash")    ||
           nameLowerNoExt.Contains("steam")    ||
           nameLowerNoExt.Contains("vcredist") ||
           nameLowerNoExt.Contains("dxsetup");

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void LaunchPatchedGame()
    {
        string? exe = ResolvePatchedExe();
        if (exe == null)
            throw new FileNotFoundException(
                "Patched DELTARUNE not found. In Archipelago, start the \"DELTARUNE " +
                "Client\" and run /auto_patch once, then point this plugin at the " +
                "\\DELTARUNE folder it creates (Settings).",
                Path.Combine(ResolvePatchedDir() ?? "", PreferredExeName));

        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exe,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            UseShellExecute  = false,
        }) ?? throw new InvalidOperationException("Failed to start DELTARUNE.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — Steam detection ────────────────────────────────────

    /// Best-effort Steam auto-detection of the DELTARUNE installation:
    ///   1. Steam root from registry (HKCU SteamPath, then HKLM InstallPath).
    ///   2. Library roots from steamapps\libraryfolders.vdf.
    ///   3. appmanifest_1671210.acf → "installdir" → steamapps\common\<installdir>.
    ///   4. Fall back to steamapps\common\DELTARUNE.
    /// Returns the dir (validated to contain DELTARUNE.exe or data.win) or null.
    private static string? DetectSteamInstallDir()
    {
        try
        {
            foreach (string steamRoot in EnumerateSteamRoots())
            {
                foreach (string lib in EnumerateSteamLibraries(steamRoot))
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    if (!Directory.Exists(steamapps)) continue;

                    string acf = Path.Combine(steamapps, $"appmanifest_{SteamAppId}.acf");
                    if (File.Exists(acf))
                    {
                        string? installDirName = ReadVdfValue(acf, "installdir");
                        if (!string.IsNullOrWhiteSpace(installDirName))
                        {
                            string candidate = Path.Combine(steamapps, "common", installDirName!);
                            if (LooksLikeDeltarune(candidate)) return candidate;
                        }
                    }

                    string std = Path.Combine(steamapps, "common", SteamCommonFolderName);
                    if (LooksLikeDeltarune(std)) return std;
                }
            }
        }
        catch { /* registry/file access failed — null */ }
        return null;
    }

    private static IEnumerable<string> EnumerateSteamRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string? root in new[]
        {
            ReadRegistryString(Microsoft.Win32.Registry.CurrentUser,
                               @"Software\Valve\Steam", "SteamPath"),
            ReadRegistryString(Microsoft.Win32.Registry.LocalMachine,
                               @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            ReadRegistryString(Microsoft.Win32.Registry.LocalMachine,
                               @"SOFTWARE\Valve\Steam", "InstallPath"),
        })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string norm = root!.Replace('/', '\\');
            if (Directory.Exists(norm) && seen.Add(norm))
                yield return norm;
        }
    }

    private static IEnumerable<string> EnumerateSteamLibraries(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (seen.Add(steamRoot))
            yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string[] lines;
        try { lines = File.ReadAllLines(vdf); }
        catch { yield break; }

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            string? val = TryReadQuotedKeyValue(line, "path");
            if (val == null)
                val = TryReadLastQuotedAbsolutePath(line);
            if (string.IsNullOrWhiteSpace(val)) continue;

            string norm = val!.Replace(@"\\", @"\").Replace('/', '\\');
            if (Directory.Exists(norm) && seen.Add(norm))
                yield return norm;
        }
    }

    /// True when a folder looks like a DELTARUNE install (data.win or DELTARUNE.exe
    /// present in the root, or chapter1_windows subdirectory present).
    private static bool LooksLikeDeltarune(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, PreferredExeName)) ||
                   File.Exists(Path.Combine(dir, "data.win")) ||
                   Directory.Exists(Path.Combine(dir, "chapter1_windows"));
        }
        catch { return false; }
    }

    private static string? ReadRegistryString(
        Microsoft.Win32.RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    private static string? ReadVdfValue(string path, string key)
    {
        try
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string? val = TryReadQuotedKeyValue(raw.Trim(), key);
                if (val != null) return val;
            }
        }
        catch { /* unreadable — null */ }
        return null;
    }

    private static string? TryReadQuotedKeyValue(string line, string key)
    {
        int k0 = line.IndexOf('"');
        if (k0 < 0) return null;
        int k1 = line.IndexOf('"', k0 + 1);
        if (k1 < 0) return null;
        string foundKey = line.Substring(k0 + 1, k1 - k0 - 1);
        if (!string.Equals(foundKey, key, StringComparison.OrdinalIgnoreCase))
            return null;

        int v0 = line.IndexOf('"', k1 + 1);
        if (v0 < 0) return null;
        int v1 = line.IndexOf('"', v0 + 1);
        if (v1 < 0) return null;
        return line.Substring(v0 + 1, v1 - v0 - 1);
    }

    private static string? TryReadLastQuotedAbsolutePath(string line)
    {
        int end = line.LastIndexOf('"');
        if (end <= 0) return null;
        int start = line.LastIndexOf('"', end - 1);
        if (start < 0) return null;
        string token = line.Substring(start + 1, end - start - 1);
        bool looksAbsolute =
            (token.Length >= 3 && token[1] == ':' && (token[2] == '\\' || token[2] == '/')) ||
            token.StartsWith('/') || token.Contains(@":\\");
        return looksAbsolute ? token : null;
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class DeltaruneSettings
    {
        /// The patched Archipelago DELTARUNE folder, persisted across launcher restarts.
        public string? PatchedDir { get; set; }
    }

    private DeltaruneSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<DeltaruneSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(DeltaruneSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    private void SavePatchedDir(string dir)
    {
        var s = LoadSettings();
        s.PatchedDir = dir;
        SaveSettings(s);
    }
}
