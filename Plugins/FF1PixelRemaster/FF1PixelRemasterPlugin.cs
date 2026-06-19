using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LauncherV2.Core;

// NOTE on type qualification (BUILD GOTCHA — CS0104):
// This project sets BOTH <UseWPF>true</UseWPF> and <UseWindowsForms>true</UseWindowsForms>.
// WPF UI types are FULLY QUALIFIED below to avoid CS0104. Do NOT add
// `using System.Windows.Controls;` or `using System.Windows.Media;` here.

namespace LauncherV2.Plugins.FF1PixelRemaster;

// ═══════════════════════════════════════════════════════════════════════════════
// FF1PixelRemasterPlugin — launch integration for "FINAL FANTASY" (Final Fantasy I
// Pixel Remaster, Square Enix, 2021), played through the FF1PRAP Archipelago mod.
//
// ── FACTS (verified 2026-06-19) ─────────────────────────────────────────────
//   * Steam release: "FINAL FANTASY" appid 1173770. This is the Pixel Remaster
//     edition of Final Fantasy I (the bundle "FINAL FANTASY I-VI Bundle" is
//     appid 21478, but the standalone FF1 Pixel Remaster is 1173770).
//   * AP game string: "ff1pr"  (verified in wildham0/FF1PRAP apworld code).
//   * APWorld file: ff1pr.apworld  (41 releases as of 2026-03; latest v0.5.17).
//   * Mod repo: wildham0/FF1PRAP  (BepInEx 6 IL2CPP plugin for the Unity
//     IL2CPP build of the Pixel Remaster). Note: BepInEx 6 (IL2CPP) is
//     required — NOT BepInEx 5. The game is a 64-bit IL2CPP Unity build.
//   * ConnectsItself = true: once the mod is installed, selecting "Archipelago"
//     on the title screen and clicking "Edit AP Config" enters the server/slot/
//     password. No separate AP client exe is needed.
//   * SupportsStandalone = true: the base game is fully playable without AP.
//   * AP Wiki: archipelago.miraheze.org/wiki/FF1_Pixel_Remaster
//   * Install flow: own FF1 Pixel Remaster on Steam → install BepInEx 6 IL2CPP
//     → copy FF1PRAP.zip plugin folder into BepInEx/plugins → copy ff1pr.apworld
//     into AP worlds folder → generate seed → launch game, choose "Archipelago"
//     on title screen, configure connection.
//   * This plugin detects the Steam install, launches the game via Steam, and
//     guides the mod setup in Settings. It does NOT download or install the mod.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class FF1PixelRemasterPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const int    STEAM_APPID     = 1173770;
    private const string STEAM_STORE_URL = "https://store.steampowered.com/app/1173770/FINAL_FANTASY/";
    private const string STEAM_RUN_URL   = "steam://rungameid/1173770";
    private const string MOD_REPO_URL    = "https://github.com/wildham0/FF1PRAP";
    private const string AP_WIKI_URL     = "https://archipelago.miraheze.org/wiki/FF1_Pixel_Remaster";
    private const string GH_RELEASES     = "https://api.github.com/repos/wildham0/FF1PRAP/releases";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "ff1pr";
    public string DisplayName => "Final Fantasy I Pixel Remaster";
    public string Subtitle    => "Steam · built-in AP client (BepInEx 6 mod)";
    public string ApWorldName => "ff1pr";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "ff1pr.png");

    public string ThemeAccentColor => "#7B44A0";

    public string[] GameBadges => new[] { "Requires Steam" };

    public string Description =>
        "Final Fantasy I Pixel Remaster (Square Enix, 2021) is the HD-2D " +
        "re-release of the original 1987 RPG. The FF1PRAP BepInEx 6 mod adds a " +
        "full Archipelago integration — key items, spells, and progression unlocks " +
        "are shuffled into the multiworld. Select \"Archipelago\" on the title screen " +
        "to configure your server connection. Requires a Steam copy of FINAL FANTASY " +
        "(appid 1173770) and BepInEx 6 IL2CPP with the FF1PRAP plugin.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled =>
        !string.IsNullOrEmpty(GameDirectory) && Directory.Exists(GameDirectory);

    public bool IsRunning { get; private set; }

    public bool ConnectsItself     => true;
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; } =
        SteamLocator.FindGameDir(STEAM_APPID) ?? string.Empty;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────

#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        InstalledVersion = IsInstalled ? "installed" : null;
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var el in doc.RootElement.EnumerateArray())
                    if (el.TryGetProperty("tag_name", out var t))
                    { AvailableVersion = t.GetString()?.Trim(); break; }
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((100,
            "Install BepInEx 6 (IL2CPP build, NOT version 5) into the FF1 Pixel " +
            "Remaster Steam folder, then download FF1PRAP.zip from the GitHub " +
            "releases and extract it into BepInEx/plugins. Copy ff1pr.apworld into " +
            "your Archipelago worlds folder. See Settings for links."));
        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        _ = session;
        StartGame();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (game mod owns the slot connection) ────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var ok      = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0xFF, 0xA5, 0x00));
        var linkClr = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
                    { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // Game directory detection
        panel.Children.Add(MakeHeader("GAME DIRECTORY", muted));
        bool found = IsInstalled;
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = found ? "Final Fantasy I Pixel Remaster detected: " + GameDirectory
                         : "Game not found. Install FINAL FANTASY via Steam (appid 1173770) " +
                           "or browse to your install folder below.",
            FontSize = 11, Foreground = found ? ok : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        var dirRow = new System.Windows.Controls.DockPanel
                     { Margin = new System.Windows.Thickness(0, 0, 0, 14) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = GameDirectory, IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var browseBtn = new System.Windows.Controls.Button
        {
            Content = "Browse...", Width = 90,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        browseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Final Fantasy I Pixel Remaster install folder",
                InitialDirectory = found ? GameDirectory : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(browseBtn,
            System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(browseBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        // Setup guide
        panel.Children.Add(MakeHeader("MOD SETUP GUIDE", muted));
        foreach (string step in new[]
        {
            "1. Own \"FINAL FANTASY\" on Steam (appid 1173770) and install it.",
            "2. Download BepInEx 6 for IL2CPP (x64). Extract it into the game " +
                "folder so that BepInEx/ sits next to FINAL FANTASY.exe. " +
                "IMPORTANT: use BepInEx 6 — NOT version 5. FF1 Pixel Remaster " +
                "is an IL2CPP Unity build and requires IL2CPP BepInEx.",
            "3. Download FF1PRAP.zip from the GitHub releases page (wildham0/FF1PRAP). " +
                "Extract and copy the FF1PRAP folder into BepInEx/plugins/.",
            "4. Copy ff1pr.apworld from the same release into your Archipelago " +
                "lib/worlds or custom_worlds folder, then generate your seed via " +
                "the Archipelago Launcher.",
            "5. Launch the game. On the title screen, select \"Archipelago\", then " +
                "click \"Edit AP Config\" and enter your server address, slot name, " +
                "and password. Click \"Connect\" to start the session.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // Links
        panel.Children.Add(MakeHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("FINAL FANTASY on Steam ↗",         STEAM_STORE_URL),
            ("FF1PRAP on GitHub ↗",              MOD_REPO_URL),
            ("FF1 Pixel Remaster — AP Wiki ↗",   AP_WIKI_URL),
            ("Archipelago Official ↗",           "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0),
                FontSize = 12,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = linkClr,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); }
                catch { }
            };
            panel.Children.Add(btn);
        }

        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES, ct);
            using var doc  = JsonDocument.Parse(json);
            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);
                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t) ? t.GetString()  ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString()       : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void StartGame()
    {
        IsRunning = true;

        // Prefer launching via Steam to satisfy DRM and overlay correctly.
        try
        {
            var psi = new ProcessStartInfo(STEAM_RUN_URL) { UseShellExecute = true };
            _gameProcess = Process.Start(psi);
            // Steam protocol returns immediately; we cannot track the child process.
        }
        catch
        {
            // Fallback: direct exe launch from detected folder.
            string dir = GameDirectory;
            string? exe = null;
            if (Directory.Exists(dir))
            {
                foreach (string name in new[]
                {
                    "FINAL FANTASY.exe", "FinalFantasy.exe", "FFI.exe", "ff1.exe",
                })
                {
                    string c = Path.Combine(dir, name);
                    if (File.Exists(c)) { exe = c; break; }
                }
                if (exe == null)
                {
                    try
                    {
                        foreach (string f in Directory.EnumerateFiles(dir, "*.exe",
                                     System.IO.SearchOption.TopDirectoryOnly))
                        {
                            string stem = Path.GetFileNameWithoutExtension(f);
                            if (stem.IndexOf("final",   StringComparison.OrdinalIgnoreCase) >= 0 ||
                                stem.IndexOf("fantasy",  StringComparison.OrdinalIgnoreCase) >= 0 ||
                                stem.Equals("FFI",       StringComparison.OrdinalIgnoreCase))
                            { exe = f; break; }
                        }
                    }
                    catch { }
                }
            }

            if (exe == null)
            {
                IsRunning = false;
                GameExited?.Invoke(-1);
                return;
            }

            try
            {
                var psi2 = new ProcessStartInfo
                {
                    FileName         = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                    UseShellExecute  = false,
                };
                var proc = Process.Start(psi2);
                _gameProcess = proc;
                if (proc != null)
                {
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (_, _) => { IsRunning = false; GameExited?.Invoke(proc.ExitCode); };
                }
                else { IsRunning = false; GameExited?.Invoke(0); }
            }
            catch (Exception ex)
            {
                IsRunning = false;
                throw new InvalidOperationException(
                    "Could not launch Final Fantasy I Pixel Remaster. " +
                    "Make sure the game is installed via Steam.", ex);
            }
        }
    }

    private static System.Windows.Controls.TextBlock MakeHeader(string text,
        System.Windows.Media.Brush color) => new()
    {
        Text = text, FontSize = 10,
        FontWeight = System.Windows.FontWeights.SemiBold,
        Foreground = color,
        Margin = new System.Windows.Thickness(0, 8, 0, 8),
    };
}
