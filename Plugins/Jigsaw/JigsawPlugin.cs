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

// IMPORTANT (real project has <UseWindowsForms>true</UseWindowsForms>):
// WPF UI types that collide with WinForms are FULLY QUALIFIED below
// (System.Windows.Controls.*, System.Windows.Media.*, System.Windows.Thickness,
// System.Windows.FontWeights, System.Windows.HorizontalAlignment,
// System.Windows.TextWrapping, …) to avoid CS0104 ambiguities. Do NOT add
// `using System.Windows.Controls;` / `using System.Windows.Media;` here.

namespace LauncherV2.Plugins.Jigsaw;

// ═══════════════════════════════════════════════════════════════════════════════
// JigsawPlugin — web-browser launch for "Jigsaw", a web-based jigsaw puzzle
// game by spineraks-org with built-in Archipelago support. This is a BROWSER
// "ConnectsItself" integration: clicking Play opens the game page in the default
// browser; the player enters their AP server, slot name, and password in the
// game's own connection dialog. There is nothing to install, nothing to download,
// and no credential file written by the launcher.
//
// Because the game lives entirely at https://spineraks-org.github.io/ArchipelagoJigsaw/
// there is no install step — IsInstalled is always true, GameDirectory is empty,
// and InstallOrUpdateAsync is a no-op. The releases feed from the GitHub repo
// (spineraks-org/ArchipelagoJigsaw) is fetched for version display and news
// only; failure is silently swallowed per the contract ("never throw on network
// failure").
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class JigsawPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GH_OWNER       = "spineraks-org";
    private const string GH_REPO        = "ArchipelagoJigsaw";
    private const string REPO_URL       = "https://github.com/spineraks-org/ArchipelagoJigsaw";
    private const string GH_RELEASES    = "https://api.github.com/repos/spineraks-org/ArchipelagoJigsaw/releases";
    private const string GAME_URL       = "https://spineraks-org.github.io/ArchipelagoJigsaw/";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId        => "jigsaw";
    public string DisplayName   => "Jigsaw";
    public string Subtitle      => "Web · built-in AP client";

    /// EXACT AP game string — matches the ApWorldName registered in the Jigsaw
    /// world (spineraks-org/ArchipelagoJigsaw).
    public string ApWorldName   => "Jigsaw";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "jigsaw.png");

    public string ThemeAccentColor => "#4080C0";
    public string[] GameBadges     => new[] { "Free", "Web Browser" };

    public string Description =>
        "Jigsaw is a web-based jigsaw puzzle game with built-in Archipelago support. " +
        "Runs entirely in your browser and connects directly to your AP server.";

    public string? VideoPreviewUrl  => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    // Jigsaw is a web game — there is no local install to version-stamp.
    // InstalledVersion is fixed; AvailableVersion is populated from the GitHub
    // releases API when connectivity allows.
    public string? InstalledVersion { get; private set; } = "web";
    public string? AvailableVersion { get; private set; }

    /// Always true — the game is a web page, so no install step is needed.
    public bool IsInstalled => true;

    /// Never running as a local process — the browser tab is untracked.
    public bool IsRunning => false;

    // ── IGamePlugin — Properties ──────────────────────────────────────────────

    /// Not used for a web game (no local files).
    public string GameDirectory { get; set; } = string.Empty;

    public bool ConnectsItself     => true;
    public bool SupportsStandalone => false;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // Jigsaw's built-in browser client speaks to the AP server directly.
    // These events exist for interface compatibility; they are never raised.
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
            string json = await _http.GetStringAsync(GH_RELEASES, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    if (rel.TryGetProperty("tag_name", out var t))
                    {
                        AvailableVersion = NormalizeTag(t.GetString());
                        break;
                    }
                }
            }
        }
        catch
        {
            AvailableVersion = null; // contract: never throw on network failure
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    /// No-op: Jigsaw is a web game — there is nothing to download or install.
    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((100,
            "Jigsaw runs in your browser — nothing to install. Click Play to open it."));
        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return true; // web game is always "installed"
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    /// Open the Jigsaw web page in the system default browser.
    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        OpenUrl(GAME_URL);
        return Task.CompletedTask;
    }

    /// Not supported — Jigsaw requires an AP server connection in-browser.
    public Task LaunchStandaloneAsync(CancellationToken ct = default)
        => throw new NotSupportedException("Jigsaw does not support standalone play.");

    /// Nothing to stop — the launcher cannot close a browser tab.
    public Task StopAsync()
        => Task.CompletedTask;

    // ── AP bridge — inert ─────────────────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var link = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
            { Margin = new Thickness(0, 0, 0, 20) };

        // ── Section: How to play ──────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "HOW TO PLAY",
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new Thickness(0, 0, 0, 8),
        });

        foreach (string step in new[]
        {
            "1. Click Play to open Jigsaw in your browser.",
            "2. Enter your AP server, slot name, and password in the game's connection dialog.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text            = step,
                FontSize        = 11,
                Foreground      = muted,
                TextWrapping    = System.Windows.TextWrapping.Wrap,
                Margin          = new Thickness(0, 0, 0, 4),
            });
        }

        // ── Section: Links ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "LINKS",
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new Thickness(0, 16, 0, 8),
        });

        foreach (var (label, url) in new[]
        {
            ("Play Jigsaw in Browser ↗",      GAME_URL),
            ("Jigsaw on GitHub ↗",            REPO_URL),
            ("Archipelago Official ↗",        "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content              = label,
                HorizontalAlignment  = HorizontalAlignment.Left,
                Padding              = new Thickness(0, 2, 0, 2),
                Background           = System.Windows.Media.Brushes.Transparent,
                BorderThickness      = new Thickness(0),
                FontSize             = 12,
                Margin               = new Thickness(0, 0, 0, 4),
                Foreground           = link,
                Cursor               = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => OpenUrl(u);
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
                    Version: el.TryGetProperty("tag_name", out var t) ? NormalizeTag(t.GetString()) ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* non-fatal — browser launch failure is best-effort */ }
    }
}
