using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.Plugins.KeepTalking;

public sealed class KeepTalkingPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const int STEAM_APPID = 341800;

    // ── Identity ──────────────────────────────────────────────────────────────

    public string GameId           => "keep_talking_and_nobody_explodes";
    public string DisplayName      => "Keep Talking and Nobody Explodes";
    public string Subtitle         => "Native PC · built-in AP client";
    public string ApWorldName      => "Keep Talking and Nobody Explodes";
    public string IconPath         => Path.Combine(AppContext.BaseDirectory, "Assets", "keep_talking_and_nobody_explodes.png");
    public string ThemeAccentColor => "#D94E1F";
    public string[] GameBadges     => new[] { "Requires Steam" };

    public string Description =>
        "Keep Talking and Nobody Explodes is a cooperative bomb-defusal party game. " +
        "One player defuses a bomb while others read from the manual. " +
        "In the Archipelago integration, module completions and bomb solutions " +
        "join the multiworld pool. Requires a Steam copy.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion => null;
    public string? AvailableVersion => null;
    public bool    IsInstalled      => !string.IsNullOrEmpty(GameDirectory) && Directory.Exists(GameDirectory);
    public bool    IsRunning        { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; } = SteamLocator.FindGameDir(STEAM_APPID) ?? string.Empty;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Connectivity ──────────────────────────────────────────────────────────

    public bool ConnectsItself     => true;
    public bool SupportsStandalone => true;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    public Task CheckForUpdateAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((100,
            "Keep Talking and Nobody Explodes requires manual mod installation. " +
            "Install the game via Steam (appid 341800), then follow the " +
            "Archipelago setup guide for this game."));
        return Task.CompletedTask;
    }

    public Task<bool> VerifyInstallAsync(CancellationToken ct = default)
        => Task.FromResult(IsInstalled);

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
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

        panel.Children.Add(new TextBlock
        {
            Text = "GAME DIRECTORY",
            FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        bool found = IsInstalled;
        panel.Children.Add(new TextBlock
        {
            Text = found ? "Keep Talking and Nobody Explodes detected: " + GameDirectory
                         : "Game not found. Install via Steam (appid 341800).",
            FontSize = 11, Foreground = found ? ok : warn,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var dirBox = new TextBox
        {
            Text = GameDirectory, IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var browseBtn = new Button
        {
            Content = "Browse...", Width = 90, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        browseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select Keep Talking and Nobody Explodes game folder",
                InitialDirectory = Directory.Exists(GameDirectory) ? GameDirectory
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
            }
        };
        DockPanel.SetDock(browseBtn, Dock.Right);
        dirRow.Children.Add(browseBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new TextBlock
        {
            Text = "LINKS",
            FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });

        foreach (var (label, url) in new[]
        {
            ("Keep Talking on Steam ↗",  $"https://store.steampowered.com/app/{STEAM_APPID}/"),
            ("Archipelago Official ↗",   "https://archipelago.gg"),
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

    public Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
        => Task.FromResult(Array.Empty<NewsItem>());

    // ── Private helpers ───────────────────────────────────────────────────────

    private void StartGame()
    {
        IsRunning = true;

        string gameDir = GameDirectory;
        string? exe    = null;

        if (!string.IsNullOrEmpty(gameDir) && Directory.Exists(gameDir))
        {
            foreach (string name in new[] { "ktane.exe", "KTaNE.exe", "Keep Talking and Nobody Explodes.exe" })
            {
                string candidate = Path.Combine(gameDir, name);
                if (File.Exists(candidate)) { exe = candidate; break; }
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
            psi = new ProcessStartInfo($"steam://rungameid/{STEAM_APPID}") { UseShellExecute = true };
        }

        try
        {
            var proc = Process.Start(psi);
            _gameProcess = proc;
            if (proc != null)
            {
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) =>
                {
                    IsRunning = false;
                    GameExited?.Invoke(proc.ExitCode);
                };
            }
            else
            {
                IsRunning = false;
                GameExited?.Invoke(0);
            }
        }
        catch (Exception ex)
        {
            IsRunning = false;
            throw new InvalidOperationException(
                "Could not launch Keep Talking and Nobody Explodes. " +
                "Make sure the game is installed via Steam.", ex);
        }
    }
}
