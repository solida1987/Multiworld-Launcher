using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LauncherV2.Core;

// IMPORTANT (real project has <UseWindowsForms>true</UseWindowsForms>):
// All WPF types are FULLY QUALIFIED to avoid CS0104 ambiguities.
// Do NOT add `using System.Windows.Controls;` / `using System.Windows.Media;` here.

namespace LauncherV2.Plugins.HintMachine;

// =============================================================================
// HintMachinePlugin -- Archipelago hint-point farming tool
//
// HintMachine is a Windows application that connects to an Archipelago server
// and lets players earn hint points by playing mini-games (Minesweeper, Snake,
// Tetris clones, etc.) without having to ask for hints. The app manages its own
// AP connection (ConnectsItself = true).
//
// GitHub: https://github.com/HintMachine/hintMachine
// =============================================================================

public sealed class HintMachinePlugin : IGamePlugin
{
    // -- Constants -------------------------------------------------------------

    private const string GH_OWNER = "HintMachine";
    private const string GH_REPO  = "hintMachine";

    private static readonly string InstallRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "MultiworldLauncher", "HintMachine");

    // -- IGamePlugin -- Identity -----------------------------------------------

    public string GameId      => "hintmachine";
    public string DisplayName => "HintMachine";
    public string Subtitle    => "PC - Hint point farming tool";
    public string ApWorldName => "HintMachine";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "hintmachine.png");

    public string ThemeAccentColor => "#8B5CF6";
    public string[] GameBadges     => new[] { "Free", "Utility", "Hint Tool" };

    public string Description =>
        "HintMachine is a Windows tool that connects to an Archipelago server and " +
        "lets you earn hint points by playing built-in mini-games like Minesweeper, " +
        "Snake, Tetris clones, and more. Earn hints without spending them on other slots. " +
        "The app manages its own connection to the AP server.";

    public string? VideoPreviewUrl  => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // -- Version state ---------------------------------------------------------

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled => File.Exists(FindExe());
    public bool IsRunning   => false;

    // -- IGamePlugin -- Properties ---------------------------------------------

    public string GameDirectory { get; set; } = InstallRoot;
    public bool ConnectsItself   => true;
    public bool SupportsStandalone => false;

    // -- AP bridge events (inert — ConnectsItself = true) ----------------------
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // -- Lifecycle -- CheckForUpdate -------------------------------------------

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try { InstalledVersion = ReadInstalledVersion(); }
        catch { InstalledVersion = null; }

        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(GH_OWNER, GH_REPO, ct));
        }
        catch { AvailableVersion = null; }
    }

    // -- Lifecycle -- InstallOrUpdate ------------------------------------------

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((50, "Opening HintMachine releases page..."));
        OpenUrl($"https://github.com/{GH_OWNER}/{GH_REPO}/releases/latest");
        progress.Report((100,
            "Download the latest HintMachine release, extract the zip, " +
            "then point the launcher at the HintMachine.exe in Settings."));
        return Task.CompletedTask;
    }

    // -- Lifecycle -- Verify ---------------------------------------------------

    public Task<bool> VerifyInstallAsync(CancellationToken ct = default)
        => Task.FromResult(IsInstalled);

    // -- Lifecycle -- Launch ---------------------------------------------------

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        string exe = FindExe();
        if (!File.Exists(exe))
        {
            OpenUrl($"https://github.com/{GH_OWNER}/{GH_REPO}/releases/latest");
            return Task.CompletedTask;
        }
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public Task StopAsync() => Task.CompletedTask;

    // -- AP bridge -- inert (ConnectsItself = true) ----------------------------

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // -- Settings UI -----------------------------------------------------------

    public UIElement? CreateSettingsPanel()
    {
        var muted  = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg     = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var accent = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x8B, 0x5C, 0xF6));

        var panel = new System.Windows.Controls.StackPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        panel.Children.Add(MakeLabel("ABOUT", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "HintMachine connects to an Archipelago server and lets you earn hint points " +
                   "by playing mini-games (Minesweeper, Snake, Tetris clones, and more). " +
                   "Use hint points to reveal item locations without spending them from other slots. " +
                   "Download and extract the latest release, then click Play.",
            FontSize     = 11,
            Foreground   = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(MakeLabel("HOW TO SET UP", muted));

        foreach (string step in new[]
        {
            "1. Click 'Download HintMachine' below and extract the zip.",
            "2. Run HintMachine.exe.",
            "3. Enter your Archipelago server address and slot name.",
            "4. Play mini-games to earn hint points for your multiworld!",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text         = step,
                FontSize     = 11,
                Foreground   = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin       = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        panel.Children.Add(MakeLabel("LINKS", muted));

        foreach (var (label, url) in new[]
        {
            ("Download HintMachine ↗",  $"https://github.com/{GH_OWNER}/{GH_REPO}/releases/latest"),
            ("GitHub Repository ↗",     $"https://github.com/{GH_OWNER}/{GH_REPO}"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content             = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding             = new System.Windows.Thickness(0, 2, 0, 2),
                Background          = System.Windows.Media.Brushes.Transparent,
                BorderThickness     = new System.Windows.Thickness(0),
                FontSize            = 12,
                Margin              = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground          = accent,
                Cursor              = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => OpenUrl(u);
            panel.Children.Add(btn);
        }

        return panel;
    }

    // -- News feed -------------------------------------------------------------

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Add("User-Agent", "MultiworldLauncher");
            var url  = $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases";
            var json = await http.GetStringAsync(url, ct);
            using var doc = JsonDocument.Parse(json);
            var results = new System.Collections.Generic.List<NewsItem>();
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                var tag  = rel.GetProperty("tag_name").GetString() ?? "";
                var body = rel.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                var relUrl = rel.TryGetProperty("html_url", out var hu) ? hu.GetString() : null;
                if (rel.TryGetProperty("published_at", out var pub)
                    && DateTimeOffset.TryParse(pub.GetString(), out var dt))
                    results.Add(new NewsItem(tag, body, tag, dt, relUrl));
            }
            return results.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // -- Private helpers -------------------------------------------------------

    private string? ReadInstalledVersion()
    {
        var vf = Path.Combine(InstallRoot, "version.txt");
        return File.Exists(vf) ? File.ReadAllText(vf).Trim() : null;
    }

    private string FindExe()
    {
        foreach (var candidate in new[]
        {
            Path.Combine(GameDirectory, "HintMachine.exe"),
            Path.Combine(InstallRoot,   "HintMachine.exe"),
            Path.Combine(GameDirectory, "hintMachine.exe"),
            Path.Combine(InstallRoot,   "hintMachine.exe"),
        })
            if (File.Exists(candidate)) return candidate;
        return Path.Combine(InstallRoot, "HintMachine.exe");
    }

    private static System.Windows.Controls.TextBlock MakeLabel(
        string text, System.Windows.Media.SolidColorBrush color) => new()
    {
        Text       = text,
        FontSize   = 10,
        FontWeight = System.Windows.FontWeights.SemiBold,
        Foreground = color,
        Margin     = new System.Windows.Thickness(0, 0, 0, 8),
    };

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
