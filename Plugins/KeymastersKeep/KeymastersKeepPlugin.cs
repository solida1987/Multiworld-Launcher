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

namespace LauncherV2.Plugins.KeymastersKeep;

// ═══════════════════════════════════════════════════════════════════════════════
// KeymastersKeepPlugin — launch integration for "Keymaster's Keep".
//
// ── FACTS ────────────────────────────────────────────────────────────────────
//   * What it is: a meta-challenge generator that integrates with Archipelago.
//     It generates unique game challenges ("trials") associated with AP locations.
//     Trials are unlocked via items called "keys". It is NOT a traditional video
//     game — it acts as an AP client/framework that drives challenges across
//     many other games.
//   * AP world: maintained in the SerpentAI/Archipelago fork, not AP-main.
//     AP game string: "Keymaster's Keep"
//   * ConnectsItself = true: the KmK application connects directly to the AP
//     server and manages its own slot.
//   * SupportsStandalone = false: requires an AP room to function.
//   * Game directory: user-managed (no Steam, no standard install path).
//     The user downloads KmK from SerpentAI/Archipelago releases and sets the
//     folder in Settings.
//   * GitHub sources:
//       https://github.com/SerpentAI/Archipelago  (the apworld + KmK client)
//       https://github.com/SerpentAI/KeymastersKeepGames  (game challenge plugins)
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class KeymastersKeepPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GH_OWNER    = "SerpentAI";
    private const string GH_REPO     = "Archipelago";
    private const string REPO_URL    = $"https://github.com/{GH_OWNER}/{GH_REPO}";
    private const string GAMES_REPO  = "https://github.com/SerpentAI/KeymastersKeepGames";
    private const string GH_RELEASES = $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId       => "keymasterskeep";
    public string DisplayName  => "Keymaster's Keep";
    public string Subtitle     => "Native PC · built-in AP client";
    public string ApWorldName  => "Keymaster's Keep";

    public string IconPath
        => Path.Combine(AppContext.BaseDirectory, "Assets", "keymasterskeep.png");

    public string ThemeAccentColor => "#8040C0";

    public string[] GameBadges => new[] { "Meta Game" };

    public string Description =>
        "Keymaster's Keep is a dynamic, multi-game objective and challenge generator " +
        "that integrates seamlessly with Archipelago. Instead of playing one game, it " +
        "generates unique in-game challenges (\"trials\") from your existing game " +
        "library — completing a trial sends a check back to the multiworld. Trials are " +
        "locked behind \"keys\" received as items from other players. The KmK client " +
        "connects directly to the Archipelago server. Maintained by Serpent.AI; " +
        "download from the SerpentAI/Archipelago GitHub releases.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled  => !string.IsNullOrEmpty(GameDirectory) &&
                                Directory.Exists(GameDirectory);
    public bool IsRunning    { get; private set; }

    public bool ConnectsItself     => true;
    public bool SupportsStandalone => false;

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; } = string.Empty;

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
            "Download Keymaster's Keep from the SerpentAI/Archipelago GitHub releases " +
            "page, extract it to your preferred folder, and set the path in Settings."));
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

    // ── AP bridge — inert (KmK speaks directly to AP server) ─────────────────

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

        panel.Children.Add(MakeHeader("SETUP GUIDE", muted));
        foreach (string step in new[]
        {
            "1. Download Keymaster's Keep from the SerpentAI/Archipelago GitHub releases page (link below).",
            "2. Extract the ZIP to your preferred folder.",
            "3. Optionally download game challenge plugins from the KeymastersKeepGames repository.",
            "4. Click Browse below to point the launcher at the extracted Keymaster's Keep folder.",
            "5. Launch from the Play tab and enter your AP server, slot, and password in the KmK client.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        panel.Children.Add(MakeHeader("WHAT IT IS", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Keymaster's Keep is not a game itself — it is a meta-challenge " +
                   "generator. It picks challenges from your existing game library and " +
                   "ties them to Archipelago locations. Complete a challenge to check " +
                   "a location; receive keys as items to unlock the next set of challenges.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // Browse row
        panel.Children.Add(MakeHeader("GAME DIRECTORY", muted));
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
                Title = "Select Keymaster's Keep folder",
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

        panel.Children.Add(MakeHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Keymaster's Keep (SerpentAI/Archipelago) ↗", REPO_URL),
            ("KmK releases ↗",                             REPO_URL + "/releases"),
            ("KeymastersKeepGames (challenge plugins) ↗",  GAMES_REPO),
            ("Archipelago Official ↗",                     "https://archipelago.gg"),
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
            // Look for the KmK / Archipelago launcher exe by common names.
            foreach (string name in new[]
            {
                "KeymastersKeep.exe",
                "Keymaster's Keep.exe",
                "Archipelago.exe",
                "ArchipelagoLauncher.exe",
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
                        string stem = Path.GetFileNameWithoutExtension(f)
                                          .ToLowerInvariant();
                        if (stem.Contains("keymaster") || stem.Contains("archipelago"))
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
            var psi = new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute  = false,
            };
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
                "Could not launch Keymaster's Keep. Make sure the application is installed " +
                "and the folder is set correctly in Settings.", ex);
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
