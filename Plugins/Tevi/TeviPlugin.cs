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

namespace LauncherV2.Plugins.Tevi;

// ═══════════════════════════════════════════════════════════════════════════════
// TeviPlugin — native PC, ConnectsItself.
// AP world: "TEVI"
// Steam appid: 1845730
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class TeviPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────
    private const int STEAM_APPID = 1845730;

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────
    public string GameId           => "tevi";
    public string DisplayName      => "TEVI";
    public string Subtitle         => "Native PC · built-in AP client";
    public string ApWorldName      => "TEVI";
    public string IconPath         => Path.Combine(AppContext.BaseDirectory, "Assets", "tevi.png");
    public string ThemeAccentColor => "#C040A0";
    public string[] GameBadges     => new[] { "Requires Steam" };
    public string Description      =>
        "TEVI is a fast-paced bullet-hell action game with deep skill combos and " +
        "an interconnected world to explore. Follow Tevi as she unravels the " +
        "mysteries of the land while fighting through hordes of enemies with " +
        "acrobatic attacks and magic. The Archipelago randomizer shuffles abilities, " +
        "items, and progression into the multiworld. Requires a Steam copy.";
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
        await Task.CompletedTask;
        AvailableVersion = null;
    }

    public Task InstallOrUpdateAsync(IProgress<(int Pct, string Msg)> progress,
                                     CancellationToken ct = default)
    {
        progress.Report((100, "TEVI requires manual mod installation. " +
            "See the Settings panel for setup instructions."));
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

        panel.Children.Add(SectionHeader("TEVI GAME DIRECTORY", muted));
        bool found = IsInstalled;
        panel.Children.Add(new TextBlock
        {
            Text = found
                ? "TEVI detected: " + GameDirectory
                : "TEVI not found. Install via Steam (appid 1845730).",
            FontSize = 11, Foreground = found ? ok : warn,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(SectionHeader("SETUP GUIDE", muted));
        foreach (string step in new[]
        {
            "1. Own and install TEVI on Steam (appid 1845730).",
            "2. Download the TEVI Archipelago mod from the community repository.",
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
            ("TEVI on Steam ↗",        "https://store.steampowered.com/app/1845730/TEVI/"),
            ("Archipelago Official ↗",  "https://archipelago.gg"),
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
            foreach (string name in new[] { "TEVI.exe", "Tevi.exe" })
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
            throw new InvalidOperationException("Could not launch TEVI.", ex);
        }
    }

    private static TextBlock SectionHeader(string text, Brush muted) => new()
    {
        Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold,
        Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
    };
}
