using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.Plugins.GuildWars2;

public sealed class GuildWars2Plugin : IGamePlugin
{
    // ── Identity ──────────────────────────────────────────────────────────────

    public string GameId           => "guild_wars_2";
    public string DisplayName      => "Guild Wars 2";
    public string Subtitle         => "Native PC · built-in AP client";
    public string ApWorldName      => "Guild Wars 2";
    public string IconPath         => Path.Combine(AppContext.BaseDirectory, "Assets", "guild_wars_2.png");
    public string ThemeAccentColor => "#C8202A";
    public string[] GameBadges     => Array.Empty<string>();

    public string Description =>
        "Guild Wars 2 is ArenaNet's massively multiplayer online role-playing game. " +
        "In the Archipelago integration the game's world bosses, story completions, " +
        "and achievements join the multiworld pool. The standalone ArenaNet launcher " +
        "is required — no Steam version needed.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion => null;
    public string? AvailableVersion => null;
    public bool    IsInstalled      => !string.IsNullOrEmpty(GameDirectory) && Directory.Exists(GameDirectory);
    public bool    IsRunning        { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    // Guild Wars 2 uses the standalone ArenaNet launcher — no Steam appid.
    public string GameDirectory { get; set; } = string.Empty;

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
            "Guild Wars 2 is distributed via the ArenaNet standalone launcher. " +
            "Install or update through the official Guild Wars 2 website."));
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
        var linkClr = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        panel.Children.Add(new TextBlock
        {
            Text = "GAME DIRECTORY",
            FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
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
                Title            = "Select Guild Wars 2 game folder (contains Gw2-64.exe)",
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
            Text = "SETUP GUIDE",
            FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });

        foreach (string step in new[]
        {
            "1. Install Guild Wars 2 from the official website (guildwars2.com).",
            "2. Install the Guild Wars 2 Archipelago mod per the setup guide.",
            "3. Set the game directory above to your Guild Wars 2 install folder.",
            "4. Launch Guild Wars 2 from the Play tab. The mod connects to the AP server in-game.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        panel.Children.Add(new TextBlock
        {
            Text = "LINKS",
            FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 12, 0, 8),
        });

        foreach (var (label, url) in new[]
        {
            ("Guild Wars 2 Official ↗", "https://www.guildwars2.com"),
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
            foreach (string name in new[] { "Gw2-64.exe", "Gw2.exe", "Guild Wars 2.exe" })
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
            psi = new ProcessStartInfo("https://www.guildwars2.com") { UseShellExecute = true };
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
                "Could not launch Guild Wars 2. Set the game directory in Settings.", ex);
        }
    }
}
