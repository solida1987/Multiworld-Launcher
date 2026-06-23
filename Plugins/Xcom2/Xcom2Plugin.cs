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

// NOTE on type qualification (BUILD GOTCHA -- CS0104):
// This project sets BOTH <UseWPF>true</UseWPF> and <UseWindowsForms>true</UseWindowsForms>.
// WPF UI types are FULLY QUALIFIED below to avoid CS0104. Do NOT add
// `using System.Windows.Controls;` or `using System.Windows.Media;` here.

namespace LauncherV2.Plugins.Xcom2;

// ===============================================================================
// Xcom2Plugin -- launch integration for "XCOM 2: War of the Chosen"
// (Firaxis / 2K, 2016 base + 2017 WOTC expansion) with the Archipelago mod.
//
// -- FACTS ------------------------------------------------------------------
//   * Steam appid 593380 (War of the Chosen DLC, requires base 268500).
//   * AP world repo: Snyax/X2WOTCArchipelago (game = "XCOM 2 War of the Chosen")
//   * Mod repo: Snyax/WOTCArchipelago (Steam Workshop id 3281191663)
//   * ConnectsItself = true: the mod ships a built-in AP client (Client.py /
//     in-game TcpLink) that connects to the server directly.
//   * SupportsStandalone = false: requires an AP room.
//   * The AP mod is installed via Steam Workshop or manual download.
// ===============================================================================

public sealed class Xcom2Plugin : IGamePlugin
{
    // -- Constants ----------------------------------------------------------------

    private const int    STEAM_APPID_WOTC = 593380;   // War of the Chosen
    private const int    STEAM_APPID_BASE = 268500;   // XCOM 2 base game
    private const string GH_OWNER        = "Snyax";
    private const string GH_REPO         = "X2WOTCArchipelago";
    private const string MOD_REPO        = "WOTCArchipelago";
    private const string GH_RELEASES     =
        $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases";
    private const string REPO_URL        = $"https://github.com/{GH_OWNER}/{GH_REPO}";
    private const string MOD_REPO_URL    = $"https://github.com/{GH_OWNER}/{MOD_REPO}";
    private const string WORKSHOP_URL    =
        "https://steamcommunity.com/sharedfiles/filedetails/?id=3281191663";
    private const string STEAM_URL       =
        "https://store.steampowered.com/app/593380/XCOM_2_War_of_the_Chosen/";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // -- IGamePlugin -- Identity --------------------------------------------------

    public string GameId      => "xcom2";
    public string DisplayName => "XCOM 2: War of the Chosen";
    public string Subtitle    => "Steam - built-in AP client";
    public string ApWorldName => "XCOM 2 War of the Chosen";

    public string IconPath
        => Path.Combine(AppContext.BaseDirectory, "Assets", "xcom2.png");

    public string ThemeAccentColor => "#1A6B3C";

    public string[] GameBadges => new[] { "Requires Steam", "Requires WOTC DLC" };

    public string Description =>
        "XCOM 2: War of the Chosen is the acclaimed turn-based tactics sequel from " +
        "Firaxis Games. Lead the Resistance against the alien ADVENT occupation while " +
        "battling the deadly Chosen. The Archipelago randomizer mod shuffles research " +
        "projects, items, and strategic upgrades across your multiworld. Requires the " +
        "Steam version of the War of the Chosen expansion (appid 593380) with the " +
        "WOTCArchipelago mod installed via Steam Workshop or manually. The mod's built-in " +
        "client connects directly to the Archipelago server.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // -- Version state ------------------------------------------------------------

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled => !string.IsNullOrEmpty(GameDirectory) &&
                               Directory.Exists(GameDirectory);
    public bool IsRunning { get; private set; }

    public bool ConnectsItself     => true;
    public bool SupportsStandalone => false;

    // -- Paths --------------------------------------------------------------------

    public string GameDirectory { get; set; } =
        SteamLocator.FindGameDir(STEAM_APPID_WOTC) ??
        SteamLocator.FindGameDir(STEAM_APPID_BASE) ??
        string.Empty;

    // -- Internal state -----------------------------------------------------------

    private Process? _gameProcess;

    // -- AP bridge events ---------------------------------------------------------
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // -- Lifecycle -- CheckForUpdate ----------------------------------------------

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        InstalledVersion = IsInstalled ? "installed" : null;
        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(GH_OWNER, GH_REPO, ct));
        }
        catch { AvailableVersion = null; }
    }

    // -- Lifecycle -- InstallOrUpdate ---------------------------------------------

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((100,
            "Install XCOM 2: War of the Chosen via Steam (appid 593380), then " +
            "subscribe to the WOTCArchipelago mod on Steam Workshop or download it " +
            "manually from the GitHub releases page. " +
            "Use Browse to set the game folder if it was not detected automatically."));
        return Task.CompletedTask;
    }

    // -- Lifecycle -- Verify ------------------------------------------------------

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // -- Lifecycle -- Launch ------------------------------------------------------

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

    // -- AP bridge -- inert (mod speaks directly to AP server) --------------------

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // -- Settings UI --------------------------------------------------------------

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
            "1. Own XCOM 2: War of the Chosen on Steam (appid 593380).",
            "2. Subscribe to the WOTCArchipelago mod on Steam Workshop " +
                "(ID 3281191663), or download it manually from GitHub releases.",
            "3. Launch XCOM 2 once via Steam to ensure the mod is loaded.",
            "4. Click Browse below to confirm or update the detected game folder.",
            "5. In the AP multiworld room, note your server address, slot name, " +
                "and password. The mod's in-game menu will prompt for these on startup.",
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
                : "Game not found. Install XCOM 2: War of the Chosen via Steam " +
                  "(appid 593380) or browse manually.",
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
                Title = "Select XCOM 2: War of the Chosen game folder",
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
            ("AP World GitHub",      REPO_URL),
            ("AP World Releases",    REPO_URL + "/releases"),
            ("Mod GitHub",           MOD_REPO_URL),
            ("Steam Workshop Mod",   WORKSHOP_URL),
            ("Steam Store",          STEAM_URL),
            ("Archipelago Official", "https://archipelago.gg"),
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

    // -- News feed ----------------------------------------------------------------

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

    // -- Private helpers ----------------------------------------------------------

    private void StartGame()
    {
        IsRunning = true;
        string dir = GameDirectory;
        string? exe = null;

        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            // Common XCOM 2 WOTC executable names and locations
            foreach (string name in new[]
            {
                "XCom2-WarOfTheChosen-Win64-Shipping.exe",
                "XCOM2.exe",
            })
            {
                foreach (string candidate in new[]
                {
                    Path.Combine(dir, name),
                    Path.Combine(dir, "Binaries", "Win64", name),
                    Path.Combine(dir, "XCom2-WarOfTheChosen", "Binaries", "Win64", name),
                })
                {
                    if (File.Exists(candidate)) { exe = candidate; break; }
                }
                if (exe != null) break;
            }
        }

        // Fall back to Steam launch URI (WOTC DLC, requires base game)
        ProcessStartInfo psi = exe != null
            ? new ProcessStartInfo
              {
                  FileName         = exe,
                  WorkingDirectory = Path.GetDirectoryName(exe)!,
                  UseShellExecute  = false,
              }
            : new ProcessStartInfo($"steam://rungameid/{STEAM_APPID_WOTC}")
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
                "Could not launch XCOM 2: War of the Chosen. " +
                "Make sure the game is installed via Steam and the WOTCArchipelago mod is applied.", ex);
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