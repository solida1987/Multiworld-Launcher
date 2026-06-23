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

namespace LauncherV2.Plugins.NeonWhite;

// ═══════════════════════════════════════════════════════════════════════════════
// NeonWhitePlugin — launch integration for "Neon White" (Angel Matrix / Annapurna
// Interactive, 2022, Steam) with the Archipelago BepInEx mod.
//
// ── FACTS ────────────────────────────────────────────────────────────────────
//   * Steam appid 1533950. GameDirectory is auto-detected via SteamLocator.
//   * AP mod repo: s5bug/NeonWhiteAP (BepInEx plugin).
//   * ConnectsItself = true: the BepInEx mod has a built-in AP client dialog
//     injected into the game's main menu — no external TextClient needed.
//   * SupportsStandalone = true: you can run the base Steam game without AP.
//   * The mod patches Neon White's main menu with an "Archipelago" connect button.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class NeonWhitePlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const int    STEAM_APPID  = 1533950;
    private const string GH_OWNER    = "s5bug";
    private const string GH_REPO     = "NeonWhiteAP";
    private const string GH_RELEASES =
        $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases";
    private const string REPO_URL    = $"https://github.com/{GH_OWNER}/{GH_REPO}";
    private const string STEAM_URL   = "https://store.steampowered.com/app/1533950/";
    private const string BEPINEX_URL = "https://github.com/BepInEx/BepInEx/releases";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "neon_white";
    public string DisplayName => "Neon White";
    public string Subtitle    => "Steam · BepInEx AP mod";
    public string ApWorldName => "Neon White";

    public string IconPath
        => Path.Combine(AppContext.BaseDirectory, "Assets", "neon_white.png");

    public string ThemeAccentColor => "#E040FB";

    public string[] GameBadges => new[] { "Requires Steam", "BepInEx Required" };

    public string Description =>
        "Neon White is a fast-paced card-action game about an assassin in Heaven. " +
        "The Archipelago BepInEx mod adds a connect button to the main menu, letting " +
        "you link to an AP multiworld. Install the mod, then launch Neon White and " +
        "use the in-game Archipelago button to connect. Requires Steam (appid 1533950) " +
        "and BepInEx 5 installed into the game folder.";

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
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(GH_OWNER, GH_REPO, ct));
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((100,
            "Install Neon White via Steam (appid 1533950), then:\n" +
            "1. Install BepInEx 5 into the Neon White game folder.\n" +
            "2. Download the NeonWhiteAP release from GitHub and place the " +
            "   plugin DLL in BepInEx/plugins/NeonWhiteAP/.\n" +
            "3. Use Browse below to confirm or set the game folder if not detected."));
        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        if (!IsInstalled) return false;
        // Check that BepInEx is present (basic indicator that the mod can load)
        string bepInExPath = Path.Combine(GameDirectory, "BepInEx");
        return Directory.Exists(bepInExPath);
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
            "1. Install Neon White via Steam (appid 1533950).",
            "2. Download and install BepInEx 5 into the Neon White game folder " +
                "(see BepInEx GitHub releases page).",
            "3. Download the NeonWhiteAP plugin from the GitHub releases page " +
                "and place the DLL in BepInEx/plugins/NeonWhiteAP/.",
            "4. Click Browse below to confirm or update the detected game folder.",
            "5. Launch Neon White. An 'Archipelago' button will appear on the " +
                "main menu — click it to enter your server address and connect.",
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
                : "Game not found. Install via Steam (appid 1533950) or browse manually.",
            FontSize = 11, Foreground = found ? ok : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        if (found)
        {
            bool bepInExFound = System.IO.Directory.Exists(
                System.IO.Path.Combine(GameDirectory, "BepInEx"));
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = bepInExFound
                    ? "BepInEx detected in game folder."
                    : "BepInEx not found. Install BepInEx 5 into the game folder before launching.",
                FontSize = 11, Foreground = bepInExFound ? ok : warn,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 6),
            });
        }

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
                Title = "Select Neon White game folder",
                InitialDirectory = System.IO.Directory.Exists(GameDirectory)
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
            ("NeonWhiteAP Mod GitHub ↗",   REPO_URL),
            ("NeonWhiteAP Releases ↗",     REPO_URL + "/releases"),
            ("BepInEx Releases ↗",         BEPINEX_URL),
            ("Steam Store ↗",              STEAM_URL),
            ("Archipelago Official ↗",     "https://archipelago.gg"),
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

        if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir))
        {
            // Neon White executable name
            string candidate = System.IO.Path.Combine(dir, "Neon White.exe");
            if (System.IO.File.Exists(candidate)) exe = candidate;

            // Fuzzy fallback: any exe containing "neon" in the name
            if (exe == null)
            {
                try
                {
                    foreach (string f in System.IO.Directory.EnumerateFiles(dir, "*.exe",
                                 System.IO.SearchOption.TopDirectoryOnly))
                    {
                        string stem = System.IO.Path.GetFileNameWithoutExtension(f);
                        if (stem.IndexOf("neon", StringComparison.OrdinalIgnoreCase) >= 0)
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
                  WorkingDirectory = System.IO.Path.GetDirectoryName(exe)!,
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
                "Could not launch Neon White. " +
                "Make sure the game is installed via Steam and BepInEx with the NeonWhiteAP mod is installed.", ex);
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