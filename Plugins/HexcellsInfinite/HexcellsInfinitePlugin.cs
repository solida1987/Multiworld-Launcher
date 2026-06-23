using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.Plugins.HexcellsInfinite;

// ═══════════════════════════════════════════════════════════════════════════════
// HexcellsInfinitePlugin — native PC, ConnectsItself.
// AP world: "Hexcells Infinite"
// Steam appid: 304410
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class HexcellsInfinitePlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────
    private const int    STEAM_APPID = 304410;
    private const string MOD_OWNER   = "Heaxeus";
    private const string MOD_REPO    = "Archipelago";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────
    public string GameId           => "hexcells_infinite";
    public string DisplayName      => "Hexcells Infinite";
    public string Subtitle         => "Native PC · built-in AP client";
    public string ApWorldName      => "Hexcells Infinite";
    public string IconPath         => Path.Combine(AppContext.BaseDirectory, "Assets", "hexcells_infinite.png");
    public string ThemeAccentColor => "#1E6B9A";
    public string[] GameBadges     => new[] { "Requires Steam" };
    public string Description      =>
        "Hexcells Infinite is the third entry in the Hexcells series of hexagonal " +
        "Minesweeper-inspired logic puzzles. Build and share an infinite number of " +
        "puzzle levels with the included level editor. The Archipelago randomizer " +
        "turns puzzle completion into a multiworld check system. Requires a Steam copy.";
    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version / state ───────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool IsInstalled        => !string.IsNullOrEmpty(GameDirectory) && Directory.Exists(GameDirectory);
    public bool IsRunning          { get; private set; }
    public bool ConnectsItself     => true;
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────
    public string GameDirectory
    {
        get => SteamLocator.FindGameDir(STEAM_APPID) ?? string.Empty;
        set { }
    }

    // ── Process ───────────────────────────────────────────────────────────────
    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Lifecycle ─────────────────────────────────────────────────────────────
    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        InstalledVersion = IsInstalled ? "installed" : null;
        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(MOD_OWNER, MOD_REPO, ct));
        }
        catch { AvailableVersion = null; }
    }

    public Task InstallOrUpdateAsync(IProgress<(int Pct, string Msg)> progress,
                                     CancellationToken ct = default)
    {
        progress.Report((50, "Opening Hexcells Infinite AP mod releases page..."));
        OpenUrl($"https://github.com/{MOD_OWNER}/{MOD_REPO}/releases/latest");
        progress.Report((100,
            "Download the latest Hexcells Infinite Archipelago release " +
            "and install it following the setup guide."));
        return Task.CompletedTask;
    }

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

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

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────
    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var ok      = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xFF, 0xA5, 0x00));
        var linkClr = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        panel.Children.Add(SectionHeader("HEXCELLS INFINITE GAME DIRECTORY", muted));
        bool found = IsInstalled;
        panel.Children.Add(new TextBlock
        {
            Text = found
                ? "Hexcells Infinite detected: " + GameDirectory
                : "Hexcells Infinite not found. Install via Steam (appid 304410).",
            FontSize = 11, Foreground = found ? ok : warn,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(SectionHeader("SETUP GUIDE", muted));
        foreach (string step in new[]
        {
            "1. Own and install Hexcells Infinite on Steam (appid 304410).",
            "2. Download the Hexcells Infinite Archipelago mod from the community repository.",
            "3. Follow the mod README to install the mod files into your game directory.",
            "4. Drop the .apworld file into your Archipelago worlds folder and generate a seed.",
            "5. Launch from the Play tab. The mod handles the AP connection in-game.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5),
            });
        }

        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Hexcells Infinite AP Releases ↗", $"https://github.com/{MOD_OWNER}/{MOD_REPO}/releases/latest"),
            ("Hexcells Infinite on Steam ↗",   "https://store.steampowered.com/app/304410/Hexcells_Infinite/"),
            ("Archipelago Official ↗",          "https://archipelago.gg"),
        })
        {
            var btn = new Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2),
                Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
                Foreground = linkClr, Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            { try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
            panel.Children.Add(btn);
        }
        return panel;
    }

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return Array.Empty<NewsItem>();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────
    private void StartGame()
    {
        IsRunning = true;
        string gameDir = GameDirectory;
        string? exe = null;

        if (!string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir))
            foreach (string name in new[] { "HexcellsInfinite.exe", "Hexcells Infinite.exe" })
            {
                string c = Path.Combine(gameDir, name);
                if (File.Exists(c)) { exe = c; break; }
            }

        ProcessStartInfo psi = exe != null
            ? new ProcessStartInfo
              { FileName = exe, WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false }
            : new ProcessStartInfo($"steam://rungameid/{STEAM_APPID}") { UseShellExecute = true };

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
            throw new InvalidOperationException("Could not launch Hexcells Infinite.", ex);
        }
    }

    private static TextBlock SectionHeader(string text, Brush muted) => new()
    {
        Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold,
        Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
    };

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }
}
