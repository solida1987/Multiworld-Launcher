using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Tyrian;

// ═══════════════════════════════════════════════════════════════════════════════
// TyrianPlugin — install / launch for Tyrian 2000 (freeware, Epic MegaGames).
//
// AP world: "Tyrian" — ConnectsItself = true (in-game AP client).
// Distribution: freeware standalone — GameDirectory = string.Empty.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class TyrianPlugin : IGamePlugin
{
    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "tyrian";
    public string DisplayName => "Tyrian 2000";
    public string Subtitle    => "Native PC · built-in AP client";
    public string ApWorldName => "Tyrian";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "tyrian.png");

    public string ThemeAccentColor => "#6A4C9C";
    public string[] GameBadges     => new[] { "Freeware" };

    public string Description =>
        "Tyrian 2000 — Epic MegaGames' acclaimed vertical-scrolling shoot-em-up, " +
        "free to play since 2004. The Archipelago randomizer shuffles ships, " +
        "weapons, armor, and shield upgrades into the multiworld. The in-game AP " +
        "client connects to the server directly. Tyrian 2000 is freeware — no " +
        "purchase required.";

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

    public string GameDirectory { get; set; } = string.Empty;

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
            "Download Tyrian 2000 (freeware) and the Archipelago client mod, " +
            "then set the install folder in Settings. See the AP setup guide for details."));
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

        panel.Children.Add(Header("TYRIAN 2000 GAME DIRECTORY", muted));

        bool found = IsInstalled;
        panel.Children.Add(new TextBlock
        {
            Text = found ? "Tyrian 2000 detected: " + GameDirectory
                         : "Tyrian 2000 not found. Browse to the install folder.",
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
                Title = "Select Tyrian 2000 install folder",
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
            "1. Download Tyrian 2000 (freeware) from the official source.",
            "2. Download and install the Tyrian Archipelago client from the AP setup guide.",
            "3. Set your install folder above so the Play button can launch the game.",
            "4. Launch Tyrian and enter your AP server details in-game.",
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
            ("Tyrian AP Setup Guide ↗", "https://archipelago.gg/tutorial/Tyrian/setup/en"),
            ("Archipelago Official ↗",  "https://archipelago.gg"),
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
            foreach (string name in new[] { "Tyrian.exe", "tyrian.exe", "Tyrian2000.exe" })
            {
                string p = Path.Combine(GameDirectory, name);
                if (File.Exists(p)) { exe = p; break; }
            }
            if (exe == null)
            {
                foreach (string f in Directory.EnumerateFiles(GameDirectory, "*.exe",
                    System.IO.SearchOption.TopDirectoryOnly))
                {
                    if (Path.GetFileNameWithoutExtension(f)
                            .IndexOf("tyrian", StringComparison.OrdinalIgnoreCase) >= 0)
                    { exe = f; break; }
                }
            }
        }

        if (exe == null)
        {
            IsRunning = false;
            throw new FileNotFoundException(
                "Could not find Tyrian.exe. Set the install folder in Settings.");
        }

        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName = exe, WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = true,
            });
            _gameProcess = proc;
            if (proc != null)
            {
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) => { IsRunning = false; GameExited?.Invoke(proc.ExitCode); };
            }
            else { IsRunning = false; GameExited?.Invoke(0); }
        }
        catch (Exception ex) { IsRunning = false; throw new InvalidOperationException("Could not launch Tyrian 2000.", ex); }
    }

    private static TextBlock Header(string text, Brush fg) => new()
    {
        Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold,
        Foreground = fg, Margin = new Thickness(0, 8, 0, 8),
    };
}
