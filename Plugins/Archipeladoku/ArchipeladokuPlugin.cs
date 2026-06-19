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

namespace LauncherV2.Plugins.Archipeladoku;

// ═══════════════════════════════════════════════════════════════════════════════
// ArchipeladokuPlugin — integration for "Archipeladoku" (web-based Sudoku game)
// with built-in Archipelago support by galdiuz.
//
// ── FACTS ─────────────────────────────────────────────────────────────────────
//   * Web game hosted on GitHub Pages: https://galdiuz.github.io/archipeladoku/
//   * GitHub repo: https://github.com/galdiuz/archipeladoku
//     The repo does NOT publish GitHub Releases — it ships directly as a
//     GitHub Pages site. The releases API may return an empty array; this
//     is handled gracefully (AvailableVersion = null, no error surfaced).
//
//   * AP game string: "Archipeladoku"
//   * ConnectsItself = true: the web app connects directly to the AP server.
//   * SupportsStandalone = false: requires an Archipelago room.
//   * IsInstalled = true always — it is a web game with no local files.
//   * GameDirectory is always string.Empty (interface requirement, unused).
//
//   * Launch: opens https://galdiuz.github.io/archipeladoku/ in the default
//     browser. The player enters their AP server, slot name, and password
//     directly in the web app.
//
//   * InstallOrUpdate: nothing to install — reports instructions and returns.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ArchipeladokuPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GH_OWNER    = "galdiuz";
    private const string GH_REPO     = "archipeladoku";
    private const string GH_RELEASES = "https://api.github.com/repos/galdiuz/archipeladoku/releases";
    private const string REPO_URL    = "https://github.com/galdiuz/archipeladoku";
    private const string GAME_URL    = "https://galdiuz.github.io/archipeladoku/";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "archipeladoku";
    public string DisplayName => "Archipeladoku";
    public string Subtitle    => "Web · built-in AP client";
    public string ApWorldName => "Archipeladoku";

    public string IconPath
        => Path.Combine(AppContext.BaseDirectory, "Assets", "archipeladoku.png");

    public string ThemeAccentColor => "#404080";   // dark indigo

    public string[] GameBadges => new[] { "Free", "Web Browser" };

    public string Description =>
        "Archipeladoku is a web-based Sudoku puzzle game with Archipelago integration. " +
        "Opens in your browser and connects directly to your AP server.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    // Always "installed" — it is a web game with no local files.
    public bool IsInstalled => true;
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
        // This repo ships via GitHub Pages, not Releases. The releases endpoint
        // may return an empty array — handle gracefully, no error surfaced.
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (el.TryGetProperty("tag_name", out var t))
                    { AvailableVersion = t.GetString()?.Trim(); break; }
                }
                // Empty array is normal for this repo — leave AvailableVersion null.
            }
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((100,
            "Archipeladoku is a web game. Click Play to open it in your browser. " +
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

        // ── Setup steps ───────────────────────────────────────────────────────
        panel.Children.Add(MakeHeader("SETUP GUIDE", muted));
        foreach (string step in new[]
        {
            "1. Click Play to open Archipeladoku in your browser.",
            "2. Enter your AP server, slot name, and password.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── AP connection note ────────────────────────────────────────────────
        panel.Children.Add(MakeHeader("ARCHIPELAGO CONNECTION", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Archipeladoku has a built-in AP client. No separate Text Client " +
                   "is needed — just enter your server details in the web app.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────────
        panel.Children.Add(MakeHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Open Archipeladoku in Browser ↗", GAME_URL),
            ("GitHub Repository ↗",             REPO_URL),
            ("Archipelago Official ↗",           "https://archipelago.gg"),
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
        // This repo does not publish GitHub Releases — return empty gracefully.
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
