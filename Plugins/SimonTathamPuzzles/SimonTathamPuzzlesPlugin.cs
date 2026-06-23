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

namespace LauncherV2.Plugins.SimonTathamPuzzles;

// ═══════════════════════════════════════════════════════════════════════════════
// SimonTathamPuzzlesPlugin — launch integration for Simon Tatham's Portable
// Puzzle Collection with Archipelago support (web version by ishanpm).
//
// ── FACTS ────────────────────────────────────────────────────────────────────
//   * Web game at ishanpm.github.io/ap-sgtpuzzles-web/
//   * AP world: github.com/ishanpm/ap-sgtpuzzles-web
//   * ConnectsItself = true: the web game has a built-in AP client.
//   * SupportsStandalone = false: requires an AP room.
//   * Launch() opens the game URL directly in the user's default browser.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SimonTathamPuzzlesPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GH_OWNER    = "ishanpm";
    private const string GH_REPO     = "ap-sgtpuzzles-web";
    private const string GH_RELEASES = "https://api.github.com/repos/ishanpm/ap-sgtpuzzles-web/releases";
    private const string REPO_URL    = "https://github.com/ishanpm/ap-sgtpuzzles-web";
    private const string GAME_URL    = "https://ishanpm.github.io/ap-sgtpuzzles-web/";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "simon_tatham_puzzles";
    public string DisplayName => "Simon Tatham's Portable Puzzle Collection";
    public string Subtitle    => "Web · built-in AP client";
    public string ApWorldName => "Simon Tatham's Portable Puzzle Collection";

    public string IconPath
        => Path.Combine(AppContext.BaseDirectory, "Assets", "simon_tatham_puzzles.png");

    public string ThemeAccentColor => "#607080";

    public string[] GameBadges => new[] { "Free", "Web Browser" };

    public string Description =>
        "Simon Tatham's Portable Puzzle Collection is a set of classic logic puzzles " +
        "with an Archipelago integration. Runs entirely in your web browser.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    // Web game — always considered installed (no local directory).
    public bool IsInstalled => true;
    public bool IsWebBased => true;
    public bool IsRunning   { get; private set; }

    public bool ConnectsItself     => true;
    public bool SupportsStandalone => false;

    // ── Paths ─────────────────────────────────────────────────────────────────

    // Web game — no local game directory.
    public string GameDirectory { get; set; } = string.Empty;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _browserProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        InstalledVersion = "web";
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
            "This is a web game. Click Play to open it in your browser."));
        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return true; // Web game — no local install to verify.
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
        // Browser tabs cannot be reliably killed; just reset state.
        IsRunning       = false;
        _browserProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (web game speaks directly to AP server) ────────────

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
        var linkClr = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
                    { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        panel.Children.Add(MakeHeader("SETUP GUIDE", muted));
        foreach (string step in new[]
        {
            "1. Click Play to open the puzzle collection in your browser.",
            "2. Enter your AP server, slot name, and password in the game's " +
                "connection dialog.",
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

        panel.Children.Add(MakeHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Play Puzzle Collection ↗",  GAME_URL),
            ("AP World GitHub ↗",          REPO_URL),
            ("Archipelago Official ↗",     "https://archipelago.gg"),
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
                Foreground          = linkClr,
                Cursor              = System.Windows.Input.Cursors.Hand,
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
        try
        {
            var proc = Process.Start(new ProcessStartInfo(GAME_URL)
                       { UseShellExecute = true });
            _browserProcess = proc;
            // Browser process tracking is best-effort only.
            IsRunning = false;
            GameExited?.Invoke(0);
        }
        catch (Exception ex)
        {
            IsRunning = false;
            throw new InvalidOperationException(
                "Could not open Simon Tatham's Portable Puzzle Collection in the browser. " +
                "Navigate manually to: " + GAME_URL, ex);
        }
    }

    private static System.Windows.Controls.TextBlock MakeHeader(string text,
        System.Windows.Media.Brush color) => new()
    {
        Text       = text,
        FontSize   = 10,
        FontWeight = System.Windows.FontWeights.SemiBold,
        Foreground = color,
        Margin     = new System.Windows.Thickness(0, 8, 0, 8),
    };
}
