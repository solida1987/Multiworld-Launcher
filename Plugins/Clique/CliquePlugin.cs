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

namespace LauncherV2.Plugins.Clique;

// ═══════════════════════════════════════════════════════════════════════════════
// CliquePlugin — web-browser launch for "Clique", a web-based Archipelago game
// hosted on a Forgejo/Gitea instance at pharware.com (NOT on GitHub). Clicking
// Play opens the repository/releases page in the default browser so the player
// can download the latest HTML release and open it locally. There is nothing
// to auto-install; the launcher cannot download from a Forgejo instance
// without vendor-specific handling.
//
// RELEASE SOURCE: the game is hosted at
//     https://pharware.com/git/Phar/Clique
// with releases at
//     https://pharware.com/git/Phar/Clique/releases/latest
// The Forgejo/Gitea API endpoint for releases is:
//     https://pharware.com/git/api/v1/repos/Phar/Clique/releases
// This API is queried for version information; if it fails (e.g. the Forgejo
// API returns a different shape than expected), AvailableVersion is set to null
// and no exception is thrown — per the contract ("never throw on network failure").
//
// Because the game is an HTML file downloaded and opened by the user, IsInstalled
// is always true (no local install tracked by the launcher), GameDirectory is
// empty, and InstallOrUpdateAsync directs the player to the releases page.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CliquePlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GH_OWNER    = "Phar";
    private const string GH_REPO     = "Clique";
    private const string REPO_URL    = "https://pharware.com/git/Phar/Clique";
    private const string GH_RELEASES = "https://pharware.com/git/api/v1/repos/Phar/Clique/releases";
    private const string RELEASES_URL = "https://pharware.com/git/Phar/Clique/releases/latest";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId        => "clique";
    public string DisplayName   => "Clique";
    public string Subtitle      => "Web · built-in AP client";

    /// EXACT AP game string — matches the ApWorldName registered in the Clique
    /// world (pharware.com/git/Phar/Clique).
    public string ApWorldName   => "Clique";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "clique.png");

    public string ThemeAccentColor => "#C0A000";
    public string[] GameBadges     => new[] { "Free", "Web Browser" };

    public string Description =>
        "Clique is a web-based Archipelago game. Opens in your browser and connects " +
        "directly to your AP server.";

    public string? VideoPreviewUrl  => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    // Clique is a web/HTML game — there is no local install to version-stamp.
    // InstalledVersion is fixed; AvailableVersion is populated from the Forgejo
    // API when connectivity allows.
    public string? InstalledVersion { get; private set; } = "web";
    public string? AvailableVersion { get; private set; }

    /// Always true — the game is an HTML file; no launcher-managed install.
    public bool IsInstalled => true;

    /// Never running as a local launcher-tracked process.
    public bool IsRunning => false;

    // ── IGamePlugin — Properties ──────────────────────────────────────────────

    /// Not used for a web game (no local files).
    public string GameDirectory { get; set; } = string.Empty;

    public bool ConnectsItself     => true;
    public bool SupportsStandalone => false;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // Clique's built-in browser client speaks to the AP server directly.
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
            // Forgejo/Gitea API: GET /api/v1/repos/{owner}/{repo}/releases
            // Returns a JSON array of release objects; each has "tag_name".
            // If the API shape differs or the server is unreachable, catch and
            // set AvailableVersion = null per contract.
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
            // Forgejo API may differ or be unreachable — gracefully degrade.
            AvailableVersion = null; // contract: never throw on network failure
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    /// Directs the player to the Clique releases page to download the HTML file.
    /// The launcher cannot auto-install from this Forgejo instance.
    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((50, "Opening Clique releases page in your browser..."));
        OpenUrl(RELEASES_URL);
        progress.Report((100,
            "Clique releases page opened. Download the latest HTML file and open it in your browser."));
        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return true; // web/HTML game is always "installed"
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    /// Open the Clique repository page in the system default browser so the
    /// player can access the latest release and download the HTML file.
    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        OpenUrl(REPO_URL);
        return Task.CompletedTask;
    }

    /// Not supported — Clique requires an AP server connection in-browser.
    public Task LaunchStandaloneAsync(CancellationToken ct = default)
        => throw new NotSupportedException("Clique does not support standalone play.");

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
            "1. Download the latest release from the Clique repository (link below).",
            "2. Open the HTML file in your browser.",
            "3. Enter your AP server details to connect.",
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

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Note: Clique is hosted on a Forgejo instance (pharware.com), not GitHub. " +
                   "Click the repository link below to find the latest release.",
            FontSize        = 11,
            Foreground      = muted,
            TextWrapping    = System.Windows.TextWrapping.Wrap,
            Margin          = new Thickness(0, 8, 0, 0),
        });

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
            ("Clique Repository ↗",           REPO_URL),
            ("Clique Latest Release ↗",       RELEASES_URL),
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
            // Forgejo/Gitea API returns the same shape as GitHub for releases:
            // array of objects with name, body/description, tag_name, published_at,
            // and html_url. "body" may be named "note" on some Forgejo versions;
            // try "body" first, fall back to "note".
            string json = await _http.GetStringAsync(GH_RELEASES, ct);
            using var doc  = JsonDocument.Parse(json);
            var items = new List<NewsItem>();

            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                // Release body: Forgejo uses "body" (same as GitHub).
                string body = "";
                if (el.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String)
                    body = b.GetString() ?? "";
                else if (el.TryGetProperty("note", out var nb) && nb.ValueKind == JsonValueKind.String)
                    body = nb.GetString() ?? "";

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    body,
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
