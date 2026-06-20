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

namespace LauncherV2.Plugins.AirDelivery;

// ═══════════════════════════════════════════════════════════════════════════════
// AirDeliveryPlugin — launch integration for "Air Delivery", a PICO-8 game
// with Archipelago integration by qwint.
//
// ── FACTS ─────────────────────────────────────────────────────────────────────
//   * PICO-8 game. Releases at: https://github.com/qwint/ap-air-delivery
//     Each release ships an HTML file (browser play) and/or a .p8 cart file
//     (PICO-8 app). The HTML file is the most accessible path — any modern
//     browser can run it without installing PICO-8.
//
//   * AP game string: "Air Delivery"
//   * ConnectsItself = false: the game does NOT connect to the AP server
//     on its own — the player uses the AP Text Client in a separate window.
//   * SupportsStandalone = false: requires an Archipelago room to be useful.
//   * IsInstalled = true (permissive): the game is browser/HTML-based and
//     there is no reliable install-detection heuristic. The player points
//     the launcher at the folder where they extracted the release.
//
//   * Launch strategy (in priority order):
//       1. air_delivery.html  — open in default browser
//       2. *.html (any HTML file in GameDirectory) — open first one found
//       3. air_delivery.p8   — launch PICO-8 with it (UseShellExecute)
//       4. *.p8 (any cart in GameDirectory)
//       5. Fallback: open GitHub releases page in browser
//
//   * InstallOrUpdate: user-directed — reports download instructions and
//     opens the GitHub releases page. No automated download.
//
//   * GameDirectory = string.Empty by default. The user browses to the
//     folder where they extracted the release in the Settings panel.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class AirDeliveryPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GH_OWNER    = "qwint";
    private const string GH_REPO     = "ap-air-delivery";
    private const string GH_RELEASES = "https://api.github.com/repos/qwint/ap-air-delivery/releases";
    private const string REPO_URL    = "https://github.com/qwint/ap-air-delivery";
    private const string RELEASES_URL = "https://github.com/qwint/ap-air-delivery/releases";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "air_delivery";
    public string DisplayName => "Air Delivery";
    public string Subtitle    => "PICO-8 · AP client required";
    public string ApWorldName => "Air Delivery";

    public string IconPath
        => Path.Combine(AppContext.BaseDirectory, "Assets", "air_delivery.png");

    public string ThemeAccentColor => "#60C0FF";   // sky blue

    public string[] GameBadges => new[] { "PICO-8", "Free Download" };

    public string Description =>
        "Air Delivery is a PICO-8 game with Archipelago integration. " +
        "Download from GitHub and run the HTML file in your browser or load the " +
        ".p8 cart in PICO-8.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    // Permissive — PICO-8 game; hard to detect without a specific installer.
    // If the user has set a directory, we consider it "installed".
    public bool IsInstalled => true;
    public bool IsWebBased => true;

    public bool IsRunning { get; private set; }

    public bool ConnectsItself    => false;
    public bool SupportsStandalone => false;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Path to the folder where the user extracted the release. Empty until
    /// the user browses to it in the Settings panel.
    public string GameDirectory { get; set; } = string.Empty;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // Air Delivery does not connect to the AP server itself — the player
    // uses the AP Text Client. These events are interface stubs only.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        InstalledVersion = !string.IsNullOrEmpty(GameDirectory) &&
                           Directory.Exists(GameDirectory) ? "installed" : null;
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
        progress.Report((50, "Opening Air Delivery releases page..."));
        try
        {
            Process.Start(new ProcessStartInfo(RELEASES_URL) { UseShellExecute = true });
        }
        catch { /* non-fatal */ }

        progress.Report((100,
            "Download Air Delivery from the GitHub releases page. You can play " +
            "it via the included HTML file in a browser, or load the .p8 cart in PICO-8."));
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
        LaunchGame();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        LaunchGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert ────────────────────────────────────────────────────

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
        var linkClr = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
                    { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Setup steps ───────────────────────────────────────────────────────
        panel.Children.Add(MakeHeader("SETUP GUIDE", muted));
        foreach (string step in new[]
        {
            "1. Download the release from GitHub (link below).",
            "2. Extract the archive.",
            "3. Open the HTML file in your browser (or load the .p8 cart in PICO-8).",
            "4. Set Browse to the folder where you extracted the release.",
            "5. Use the AP Text Client to connect to your server separately.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Game directory browse row ─────────────────────────────────────────
        panel.Children.Add(MakeHeader("GAME DIRECTORY", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Point this at the folder where you extracted the release " +
                   "(the folder that contains the HTML file or .p8 cart).",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
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
                Title = "Select Air Delivery folder",
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

        // ── AP connection note ────────────────────────────────────────────────
        panel.Children.Add(MakeHeader("ARCHIPELAGO CONNECTION", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Air Delivery does not have a built-in AP client. Use the " +
                   "Archipelago Text Client (or any AP client) to connect to your " +
                   "server in a separate window while the game is open in your browser.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────────
        panel.Children.Add(MakeHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Air Delivery on GitHub ↗",     REPO_URL),
            ("Releases (download here) ↗",   RELEASES_URL),
            ("Archipelago Official ↗",        "https://archipelago.gg"),
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

    /// Launch the game: look for HTML or .p8 files in GameDirectory; fall
    /// back to opening the GitHub releases page in the browser.
    private void LaunchGame()
    {
        IsRunning = true;

        string? target = ResolveGameFile();
        if (target != null)
        {
            try
            {
                _gameProcess = Process.Start(
                    new ProcessStartInfo(target) { UseShellExecute = true });
                return;
            }
            catch { /* fall through to releases page */ }
        }

        // No local file found — open the GitHub releases page so the user can
        // download the release. This is the graceful fallback when GameDirectory
        // has not been set yet.
        try
        {
            Process.Start(new ProcessStartInfo(RELEASES_URL) { UseShellExecute = true });
        }
        catch { /* non-fatal */ }
    }

    /// Find the best launchable file in GameDirectory:
    ///   Priority 1: air_delivery.html
    ///   Priority 2: any *.html in the directory
    ///   Priority 3: air_delivery.p8
    ///   Priority 4: any *.p8 in the directory
    ///   Returns null if nothing found or directory is empty/unset.
    private string? ResolveGameFile()
    {
        string dir = GameDirectory;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

        // Try preferred HTML name first.
        string preferred = Path.Combine(dir, "air_delivery.html");
        if (File.Exists(preferred)) return preferred;

        // Any HTML file in the directory.
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*.html"))
                return f;
        }
        catch { /* directory access failure — non-fatal */ }

        // Try preferred .p8 cart name.
        string preferredP8 = Path.Combine(dir, "air_delivery.p8");
        if (File.Exists(preferredP8)) return preferredP8;

        // Any .p8 cart in the directory.
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*.p8"))
                return f;
        }
        catch { /* non-fatal */ }

        return null;
    }

    private static System.Windows.Controls.TextBlock MakeHeader(
        string text,
        System.Windows.Media.Brush foreground) =>
        new()
        {
            Text       = text,
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = foreground,
            Margin     = new System.Windows.Thickness(0, 8, 0, 6),
        };
}
