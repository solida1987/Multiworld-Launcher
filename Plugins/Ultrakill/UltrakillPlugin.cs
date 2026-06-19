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

namespace LauncherV2.Plugins.Ultrakill;

// ═══════════════════════════════════════════════════════════════════════════════
// UltrakillPlugin — launch integration for "ULTRAKILL"
// (Arsi "Hakita" Patala / New Blood Interactive, 2020 PC) with the
// Archipelago community randomizer BepInEx mod.
//
// ── FACTS ────────────────────────────────────────────────────────────────────
//   * Steam appid 1229490. GameDirectory is auto-detected via SteamLocator.
//   * AP world repo: TRPG0/ArchipelagoULTRAKILL
//       ApWorldName: "ULTRAKILL"
//   * ConnectsItself = true: the BepInEx mod ships a built-in AP client that
//     connects via the in-game Plugin Config menu. No external TextClient needed.
//   * SupportsStandalone = true: ULTRAKILL can be launched without AP.
//   * Install: r2modman / Gale mod manager or manual BepInEx plugin drop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class UltrakillPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const int    STEAM_APPID  = 1229490;
    private const string GH_OWNER    = "TRPG0";
    private const string GH_REPO     = "ArchipelagoULTRAKILL";
    private const string GH_RELEASES =
        $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases";
    private const string REPO_URL    = $"https://github.com/{GH_OWNER}/{GH_REPO}";
    private const string STEAM_URL   = "https://store.steampowered.com/app/1229490/";
    private const string THUNDERSTORE = "https://thunderstore.io/c/ultrakill/p/TRPG/";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "ultrakill";
    public string DisplayName => "ULTRAKILL";
    public string Subtitle    => "Steam · BepInEx AP mod";
    public string ApWorldName => "ULTRAKILL";

    public string IconPath
        => Path.Combine(AppContext.BaseDirectory, "Assets", "ultrakill.png");

    public string ThemeAccentColor => "#C01010";

    public string[] GameBadges => new[] { "Requires Steam" };

    public string Description =>
        "ULTRAKILL is a fast-paced retro FPS where you feed on the blood of your " +
        "enemies to stay alive. The Archipelago randomizer shuffles all weapons, " +
        "arms, and movement abilities across the multiworld. Levels are unlocked " +
        "by finding access items, and secrets yield multiworld checks. Supports " +
        "traps, challenges, P-ranks, and more as location checks. Requires the " +
        "Steam version (appid 1229490) with the BepInEx AP mod installed via " +
        "r2modman, Gale, or manually. The mod's built-in client connects to the " +
        "AP server from the in-game Plugin Config menu.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled => !string.IsNullOrEmpty(GameDirectory) &&
                               Directory.Exists(GameDirectory);
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
            "Install ULTRAKILL via Steam (appid 1229490). Then install the " +
            "Archipelago mod using r2modman or Gale mod manager, or manually " +
            "extract the BepInEx plugin zip into the game's BepInEx/plugins " +
            "folder. Download the latest release from the GitHub releases page. " +
            "Use Browse to set the game folder if it was not detected automatically."));
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
        IsRunning    = false;
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

        panel.Children.Add(MakeHeader("SETUP GUIDE", muted));
        foreach (string step in new[]
        {
            "1. Install ULTRAKILL via Steam (appid 1229490).",
            "2. Install a mod manager: r2modman or Gale (links below).",
            "3. Download the Archipelago mod from the GitHub releases page and " +
                "install it via the mod manager. Alternatively, extract the zip " +
                "into BepInEx/plugins manually (also install PluginConfigurator).",
            "4. Click Browse below to confirm or update the detected game folder.",
            "5. Launch from the Play tab. In-game: open Options > PLUGIN CONFIG > " +
                "Archipelago > PLAYER SETTINGS, enter your AP server address, " +
                "slot name, and password, then click Connect.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        panel.Children.Add(MakeHeader("GAME DIRECTORY", muted));

        bool found = IsInstalled;
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = found
                ? "Game detected: " + GameDirectory
                : "Game not found. Install via Steam (appid 1229490) or browse manually.",
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
                Title = "Select ULTRAKILL game folder",
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
            ("AP Mod GitHub ↗",          REPO_URL),
            ("AP Mod Releases ↗",        REPO_URL + "/releases"),
            ("Thunderstore (Gale/r2) ↗", THUNDERSTORE + "Archipelago/"),
            ("r2modman ↗",               "https://github.com/ebkr/r2modmanPlus/releases"),
            ("Gale Mod Manager ↗",       "https://github.com/Kesomannen/gale/releases"),
            ("Steam Store ↗",            STEAM_URL),
            ("Archipelago Official ↗",   "https://archipelago.gg"),
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

        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            // Known ULTRAKILL executable names
            foreach (string name in new[]
            {
                "ULTRAKILL.exe",
                "Ultrakill.exe",
            })
            {
                string c = Path.Combine(dir, name);
                if (File.Exists(c)) { exe = c; break; }
            }

            // Fuzzy fallback: any exe containing "ultrakill"
            if (exe == null)
            {
                try
                {
                    foreach (string f in Directory.EnumerateFiles(dir, "*.exe",
                                 System.IO.SearchOption.TopDirectoryOnly))
                    {
                        string stem = Path.GetFileNameWithoutExtension(f);
                        if (stem.IndexOf("ultrakill", StringComparison.OrdinalIgnoreCase) >= 0)
                        { exe = f; break; }
                    }
                }
                catch { }
            }
        }

        // Fall back to Steam launch URI
        ProcessStartInfo psi = exe != null
            ? new ProcessStartInfo
              {
                  FileName         = exe,
                  WorkingDirectory = Path.GetDirectoryName(exe)!,
                  UseShellExecute  = false,
              }
            : new ProcessStartInfo($"steam://rungameid/{STEAM_APPID}")
              { UseShellExecute = true };

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
                "Could not launch ULTRAKILL. " +
                "Make sure the game is installed via Steam and the AP mod is installed.", ex);
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