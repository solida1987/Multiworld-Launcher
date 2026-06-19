using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Parkitect;

// ═══════════════════════════════════════════════════════════════════════════════
// ParkitectPlugin — install / launch for Parkitect (Texel Raptor).
//
// AP world: "Parkitect" — ConnectsItself = true (in-game AP client via mod).
// Distribution: Steam (appid 453090).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ParkitectPlugin : IGamePlugin
{
    private const int STEAM_APPID = 453090;

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "parkitect";
    public string DisplayName => "Parkitect";
    public string Subtitle    => "Native PC · built-in AP client";
    public string ApWorldName => "Parkitect";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "parkitect.png");

    public string ThemeAccentColor => "#E8A020";
    public string[] GameBadges     => new[] { "Requires Steam" };

    public string Description =>
        "Parkitect is Texel Raptor's theme park building and management simulation. " +
        "The Archipelago randomizer shuffles research items, blueprints, and park " +
        "scenario unlocks into the multiworld. The in-game AP client connects to " +
        "the server directly via the Parkitect mod loader. Requires a Steam copy " +
        "of Parkitect (appid 453090).";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled =>
        !string.IsNullOrEmpty(GameDirectory) && Directory.Exists(GameDirectory);

    public bool IsRunning { get; private set; }

    public bool ConnectsItself    => true;
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

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        InstalledVersion = IsInstalled ? "installed" : null;
        AvailableVersion = null;
        await Task.CompletedTask;
    }

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((100,
            "Parkitect requires manual mod installation. " +
            "See the AP setup guide and Settings panel for steps."));
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

    // ── AP bridge — inert ─────────────────────────────────────────────────────

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

        panel.Children.Add(Header("PARKITECT GAME DIRECTORY", muted));

        bool found = IsInstalled;
        panel.Children.Add(new TextBlock
        {
            Text = found ? "Parkitect detected: " + GameDirectory
                         : "Parkitect not found. Install via Steam (appid 453090) or browse below.",
            FontSize = 11, Foreground = found ? ok : warn,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var dirBox = new TextBox
        {
            Text = GameDirectory, IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg, BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var browseBtn = new Button
        {
            Content = "Browse...", Width = 90, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg, BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        browseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Parkitect install folder",
                InitialDirectory = found ? GameDirectory : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true) { GameDirectory = dlg.FolderName; dirBox.Text = dlg.FolderName; }
        };
        DockPanel.SetDock(browseBtn, Dock.Right);
        dirRow.Children.Add(browseBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(Header("SETUP GUIDE", muted));
        foreach (string step in new[]
        {
            "1. Own Parkitect on Steam (appid 453090). Install it if you have not.",
            "2. Download and install the Parkitect Archipelago mod following the AP setup guide.",
            "3. Drop the .apworld file into your Archipelago worlds folder, then generate a seed.",
            "4. Launch Parkitect and enter your AP connection details in-game.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5),
            });
        }

        panel.Children.Add(Header("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Parkitect AP Setup Guide ↗",  "https://archipelago.gg/tutorial/Parkitect/setup/en"),
            ("Parkitect on Steam ↗",        "https://store.steampowered.com/app/453090/Parkitect/"),
            ("Archipelago Official ↗",      "https://archipelago.gg"),
        })
        {
            var btn = new Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4), Foreground = linkClr,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
            };
            panel.Children.Add(btn);
        }

        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return Array.Empty<NewsItem>();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void StartGame()
    {
        IsRunning = true;

        string? exe = null;
        if (!string.IsNullOrEmpty(GameDirectory) && Directory.Exists(GameDirectory))
        {
            foreach (string name in new[] { "Parkitect.exe", "parkitect.exe" })
            {
                string p = Path.Combine(GameDirectory, name);
                if (File.Exists(p)) { exe = p; break; }
            }
        }

        ProcessStartInfo psi;
        if (exe != null)
        {
            psi = new ProcessStartInfo
            {
                FileName = exe, WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = true,
            };
        }
        else
        {
            psi = new ProcessStartInfo($"steam://rungameid/{STEAM_APPID}") { UseShellExecute = true };
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
        catch (Exception ex) { IsRunning = false; throw new InvalidOperationException("Could not launch Parkitect.", ex); }
    }

    private static TextBlock Header(string text, Brush fg) => new()
    {
        Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold,
        Foreground = fg, Margin = new Thickness(0, 8, 0, 8),
    };
}
