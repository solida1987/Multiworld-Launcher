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

namespace LauncherV2.Plugins.Wordipelago;

// ═══════════════════════════════════════════════════════════════════════════════
// WordipelagoPlugin — integration for "Wordipelago" (web game, browser-based)
// with built-in Archipelago support.
//
// ── FACTS ────────────────────────────────────────────────────────────────────
//   * Web game — no local installation required. Launches at itch.io URL.
//   * AP world repo: ProfDeCube/Archipelago
//   * ConnectsItself = true: the web app connects directly to the AP server.
//   * SupportsStandalone = false: requires an AP room.
//   * IsInstalled = true always (nothing to install locally).
//   * GameDirectory is always string.Empty (interface requirement, unused).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class WordipelagoPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GH_OWNER    = "ProfDeCube";
    private const string GH_REPO     = "Archipelago";
    private const string GH_RELEASES = "https://api.github.com/repos/ProfDeCube/Archipelago/releases";
    private const string REPO_URL    = "https://github.com/ProfDeCube/Archipelago";
    private const string GAME_URL    = "https://prof-de-cube.itch.io/wordipelago";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "wordipelago";
    public string DisplayName => "Wordipelago";
    public string Subtitle    => "Web · built-in AP client";
    public string ApWorldName => "Wordipelago";

    public string IconPath
        => Path.Combine(AppContext.BaseDirectory, "Assets", "wordipelago.png");

    public string ThemeAccentColor => "#20A060";

    public string[] GameBadges => new[] { "Free", "Web Browser" };

    public string Description =>
        "Wordipelago is a word-guessing web game with built-in Archipelago support. " +
        "Opens in your browser and connects directly to your AP server.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    // Always "installed" — it is a web game with no local files.
    public bool IsInstalled => true;
    public bool IsWebBased => true;
    public bool IsRunning { get; private set; }

    public bool ConnectsItself     => true;
    public bool SupportsStandalone => false;

    // ── Paths ─────────────────────────────────────────────────────────────────

    // Required by IGamePlugin but unused for a web game.
    public string GameDirectory { get; set; } = string.Empty;

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
            "Wordipelago is a web game. Click Play to open it in your browser. " +
            "No installation required."));
        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return true; // Always available — it is a web game.
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        _ = session;
        OpenBrowser();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        OpenBrowser();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // Nothing to stop for a web game.
        IsRunning = false;
        return Task.CompletedTask;
    }

    // ── AP bridge — built-in client in the web app ────────────────────────────

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
            "1. Click Play to open Wordipelago in your browser.",
            "2. The game will prompt for your AP server, slot name, and password.",
            "3. No local installation required.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        panel.Children.Add(MakeHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Open Wordipelago in Browser ↗", GAME_URL),
            ("AP World GitHub ↗",             REPO_URL),
            ("AP World Releases ↗",           REPO_URL + "/releases"),
            ("Archipelago Official ↗",        "https://archipelago.gg"),
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

    private void OpenBrowser()
    {
        IsRunning = true;
        try
        {
            Process.Start(new ProcessStartInfo(GAME_URL) { UseShellExecute = true });
        }
        catch { }
        // Browser games don't have a trackable process — mark not running immediately.
        IsRunning = false;
        GameExited?.Invoke(0);
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
