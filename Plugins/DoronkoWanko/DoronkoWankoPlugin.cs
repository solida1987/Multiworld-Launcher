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

namespace LauncherV2.Plugins.DoronkoWanko;

// ═══════════════════════════════════════════════════════════════════════════════
// DoronkoWankoPlugin — launch integration for "DORONKO WANKO" (indie puzzle
// game, 2022) played through its Archipelago integration.
//
// ── FACTS ────────────────────────────────────────────────────────────────────
//   * Steam appid: 2168430 (verified: store.steampowered.com/app/2168430)
//   * AP game string: "DORONKO WANKO"
//   * ConnectsItself = true: the mod's built-in AP client handles connection.
//   * SupportsStandalone = true: the base game runs without AP.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DoronkoWankoPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const int    STEAM_APPID = 2168430;
    private const string STEAM_RUN   = "steam://rungameid/2168430";
    private const string STEAM_URL   =
        "https://store.steampowered.com/app/2168430/DORONKO_WANKO/";

    // Community AP world — repo placeholder; update if a specific repo is found.
    private const string REPO_URL    = "https://github.com/alwaysintreble/DoronkoWankoArchipelago";
    private const string GH_RELEASES = REPO_URL + "/releases";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId       => "doronko_wanko";
    public string DisplayName  => "DORONKO WANKO";
    public string Subtitle     => "Native PC · built-in AP client";
    public string ApWorldName  => "DORONKO WANKO";

    public string IconPath
        => Path.Combine(AppContext.BaseDirectory, "Assets", "doronko_wanko.png");

    public string ThemeAccentColor => "#F06090";

    public string[] GameBadges => new[] { "Requires Steam" };

    public string Description =>
        "DORONKO WANKO is a cozy puzzle game about a muddy dog exploring colorful " +
        "environments and making everything delightfully dirty. In the Archipelago " +
        "randomizer, level completions and collectibles join the multiworld item pool. " +
        "Requires a Steam copy of DORONKO WANKO (appid 2168430).";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled  => !string.IsNullOrEmpty(GameDirectory) &&
                                Directory.Exists(GameDirectory);
    public bool IsRunning    { get; private set; }

    public bool ConnectsItself    => true;
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory
    {
        get; set;
    } = SteamLocator.FindGameDir(STEAM_APPID) ?? string.Empty;

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
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync("alwaysintreble", "DoronkoWankoArchipelago", ct));
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((100,
            "DORONKO WANKO requires manual mod installation. Install the base game " +
            "via Steam, then follow the setup guide at the GitHub link in the " +
            "Settings panel."));
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

    // ── AP bridge — inert (mod speaks directly to AP server) ─────────────────

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

        panel.Children.Add(MakeHeader("DORONKO WANKO GAME DIRECTORY", muted));
        bool gameFound = IsInstalled;
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = gameFound
                ? "DORONKO WANKO detected: " + GameDirectory
                : "DORONKO WANKO not found. Install via Steam (appid 2168430).",
            FontSize = 11,
            Foreground = gameFound ? ok : warn,
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
                Title = "Select DORONKO WANKO game folder",
                InitialDirectory = Directory.Exists(GameDirectory)
                    ? GameDirectory : AppContext.BaseDirectory,
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

        panel.Children.Add(MakeHeader("SETUP GUIDE", muted));
        foreach (string step in new[]
        {
            "1. Own and install DORONKO WANKO on Steam (appid 2168430).",
            "2. Download the DORONKO WANKO AP mod from the GitHub link below.",
            "3. Follow the README to place mod files into your game folder.",
            "4. Drop the .apworld into your Archipelago worlds folder and generate a seed.",
            "5. Launch from the Play tab; enter connection details in-game.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        panel.Children.Add(MakeHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("DORONKO WANKO AP mod (GitHub) ↗", REPO_URL),
            ("DORONKO WANKO on Steam ↗",         STEAM_URL),
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
        string dir = GameDirectory;
        string? exe = null;

        if (Directory.Exists(dir))
        {
            foreach (string name in new[] { "DORONKO WANKO.exe", "DoronkoWanko.exe",
                                            "doronkowanko.exe" })
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
                        if (stem.IndexOf("doronko", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            stem.IndexOf("wanko",   StringComparison.OrdinalIgnoreCase) >= 0)
                        { exe = f; break; }
                    }
                }
                catch { }
            }
        }

        ProcessStartInfo psi;
        if (exe != null)
        {
            psi = new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute  = false,
            };
        }
        else
        {
            psi = new ProcessStartInfo(STEAM_RUN) { UseShellExecute = true };
        }

        try
        {
            var proc = Process.Start(psi);
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
                "Could not launch DORONKO WANKO. Make sure the game is installed via Steam.", ex);
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
