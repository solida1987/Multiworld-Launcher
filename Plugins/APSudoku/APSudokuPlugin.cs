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

namespace LauncherV2.Plugins.APSudoku;

// =============================================================================
// APSudokuPlugin -- Archipelago Sudoku puzzle game
//
// APSudoku is a Sudoku puzzle game built for Archipelago multiworld. Solving
// sudoku puzzles sends checks to the multiworld; items from other worlds
// arrive as hints or puzzle assists. The game client connects directly to the
// AP server (ConnectsItself = true).
//
// GitHub: https://github.com/APSudoku/APSudoku
// =============================================================================

public sealed class APSudokuPlugin : IGamePlugin
{
    // -- Constants -------------------------------------------------------------

    private const string GH_OWNER = "APSudoku";
    private const string GH_REPO  = "APSudoku";

    private static readonly string InstallRoot =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "MultiworldLauncher", "APSudoku");

    // -- IGamePlugin -- Identity -----------------------------------------------

    public string GameId      => "apsudoku";
    public string DisplayName => "APSudoku";
    public string Subtitle    => "PC - Sudoku puzzle for Archipelago";
    public string ApWorldName => "APSudoku";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "apsudoku.png");

    public string ThemeAccentColor => "#4466AA";
    public string[] GameBadges     => new[] { "Free", "Puzzle", "Standalone App" };

    public string Description =>
        "APSudoku is a Sudoku puzzle game built for Archipelago multiworld. " +
        "Solve Sudoku grids to send location checks to your multiworld; items " +
        "received from other players can unlock new grids or provide hints. " +
        "The app connects directly to the Archipelago server.";

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
        progress.Report((5, "Fetching latest APSudoku release..."));
        OpenUrl($"https://github.com/{GH_OWNER}/{GH_REPO}/releases/latest");
        progress.Report((100,
            "Opened APSudoku releases page in your browser. " +
            "Download and extract the zip, then click Play to launch."));
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
            System.Windows.Media.Color.FromRgb(0x44, 0x66, 0xAA));

        var panel = new System.Windows.Controls.StackPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        panel.Children.Add(MakeLabel("HOW TO PLAY", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "APSudoku is a Sudoku puzzle game for Archipelago multiworld. " +
                   "Solving puzzles sends checks; items from other games may assist or unlock new content. " +
                   "Download the latest release, extract it, and click Play to launch.",
            FontSize     = 11,
            Foreground   = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(MakeLabel("LINKS", muted));

        foreach (var (label, url) in new[]
        {
            ("Download APSudoku ↗", $"https://github.com/{GH_OWNER}/{GH_REPO}/releases/latest"),
            ("GitHub Repository ↗", $"https://github.com/{GH_OWNER}/{GH_REPO}"),
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
            Path.Combine(GameDirectory, "APSudoku.exe"),
            Path.Combine(InstallRoot,   "APSudoku.exe"),
        })
            if (File.Exists(candidate)) return candidate;
        return Path.Combine(InstallRoot, "APSudoku.exe");
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
