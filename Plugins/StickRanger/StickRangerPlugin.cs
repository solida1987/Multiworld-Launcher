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

namespace LauncherV2.Plugins.StickRanger;

// ═══════════════════════════════════════════════════════════════════════════════
// StickRangerPlugin — launch integration for "Stick Ranger" (AP randomizer).
//
// ── FACTS ────────────────────────────────────────────────────────────────────
//   * What it is: Stick Ranger is a web-based action RPG by hat-san (Dan-Ball).
//     The Archipelago integration is a community port at Kryen112/AP_Stick_Ranger
//     (GitHub). The randomized game runs entirely in the browser at:
//       https://kryen112.github.io/
//     There is no native executable — "launching" opens the above URL. The web
//     client shows a connection dialog (host, port, slot, password) and connects
//     to the AP server directly via WebSocket. Save state is stored on
//     Archipelago's DataStorage so progress persists even if the port changes.
//   * AP game string: not in AP-main; community apworld at Kryen112/AP_Stick_Ranger.
//   * ConnectsItself = true: the in-browser client handles the AP connection.
//   * SupportsStandalone = false: requires an AP room.
//   * GameDirectory: not applicable (web game) — set to string.Empty.
//   * Installation: nothing to install. The user just needs a modern browser.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class StickRangerPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GH_OWNER    = "Kryen112";
    private const string GH_REPO     = "AP_Stick_Ranger";
    private const string REPO_URL    = $"https://github.com/{GH_OWNER}/{GH_REPO}";
    private const string GH_RELEASES = $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases";
    private const string GAME_URL    = "https://kryen112.github.io/";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId       => "stickranger";
    public string DisplayName  => "Stick Ranger";
    public string Subtitle     => "Browser · built-in AP client";
    public string ApWorldName  => "Stick Ranger";

    public string IconPath
        => Path.Combine(AppContext.BaseDirectory, "Assets", "stickranger.png");

    public string ThemeAccentColor => "#E05020";

    public string[] GameBadges => new[] { "Browser Game", "Free" };

    public string Description =>
        "Stick Ranger is an action RPG by hat-san (Dan-Ball) where you control a " +
        "party of stickmen through increasingly challenging stages. The Archipelago " +
        "randomizer by Kryen112 shuffles weapon drops and stage progression into the " +
        "multiworld. The game runs entirely in your browser — no download required. " +
        "Enter your AP server details in the in-game connection dialog and play. " +
        "Progress is saved on Archipelago's DataStorage so it persists between sessions.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    // Always "installed" — it is a browser game, nothing to install locally.
    public bool IsInstalled  => true;
    public bool IsWebBased => true;
    public bool IsRunning    { get; private set; }

    public bool ConnectsItself     => true;
    public bool SupportsStandalone => false;

    // ── Paths — not applicable for a browser game ─────────────────────────────

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
        InstalledVersion = "browser";
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var el in doc.RootElement.EnumerateArray())
                    if (el.TryGetProperty("tag_name", out var t))
                    { AvailableVersion = t.GetString()?.Trim(); break; }
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((100,
            "Stick Ranger runs in your browser — there is nothing to install. " +
            "Click Play to open the game."));
        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return true;   // browser game is always "installed"
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
        // We cannot meaningfully close the user's browser tab; best effort only.
        try { _browserProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        _browserProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (browser client speaks directly to AP server) ───────

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
        var ok      = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var linkClr = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
                    { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        panel.Children.Add(MakeHeader("HOW TO PLAY", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Stick Ranger runs entirely in your browser — no download or install needed.",
            FontSize = 11, Foreground = ok,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        foreach (string step in new[]
        {
            "1. Make sure you have a running Archipelago room with a Stick Ranger slot.",
            "2. Click Play — your browser will open the game at kryen112.github.io.",
            "3. Enter your AP server address, port, slot name, and password in the " +
               "connection dialog shown in the game.",
            "4. The game connects via WebSocket and saves your progress on AP's DataStorage.",
            "5. Install the apworld from the GitHub releases page into your Archipelago " +
               "\"worlds\" folder before generating a seed.",
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
            ("Play Stick Ranger (browser) ↗",    GAME_URL),
            ("AP_Stick_Ranger GitHub ↗",         REPO_URL),
            ("AP_Stick_Ranger releases ↗",       REPO_URL + "/releases"),
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
            var psi = new ProcessStartInfo(GAME_URL) { UseShellExecute = true };
            var proc = Process.Start(psi);
            _browserProcess = proc;
            // Browsers fork immediately and the launcher process exits at once.
            // We set IsRunning=false after a short delay to reflect that we have
            // no real handle on the browser tab's lifetime.
            if (proc != null)
            {
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) =>
                {
                    // The shell/browser launcher process exited — the tab may
                    // still be open, but we have no way to track it.
                    IsRunning = false;
                    GameExited?.Invoke(0);
                };
            }
            else
            {
                // UseShellExecute with a URL typically returns null (no child
                // process handle). Treat the launch as successful.
                IsRunning = false;
                GameExited?.Invoke(0);
            }
        }
        catch (Exception ex)
        {
            IsRunning = false;
            throw new InvalidOperationException(
                "Could not open Stick Ranger in the browser. " +
                "Try navigating to " + GAME_URL + " manually.", ex);
        }
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
