using System;
using System.Collections.Generic;
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

namespace LauncherV2.Plugins.Autopelago;

// ═══════════════════════════════════════════════════════════════════════════════
// AutopelagoPlugin — launcher integration for "Autopelago"
// (airbreather/Autopelago on GitHub, live at https://autopelago.app).
//
// Autopelago is a meta-game where an autonomous rat player navigates an
// Archipelago multiworld and automatically discovers and checks locations — it
// IS the Archipelago client itself (not a mod for another game). The current
// release series (v1.0.0+) is a pure HOSTED WEB APPLICATION (Angular SPA).
// There is no Windows native exe: the game runs in the browser at
// https://autopelago.app, and the local dist.zip is merely the static-file
// build of that site.
//
// REALITY CHECK (2026-06-14) — facts verified this session
// ─────────────────────────────────────────────────────────────────────────────
//   * REPO: airbreather/Autopelago. Language: TypeScript (Angular 21). All
//     releases from v1.0.0 onward ship only "dist.zip" (the static web build).
//     The native Windows exe series ended at v0.10.4 (released before v0.11.0).
//     Latest at time of writing: v1.1.4 ("Autopelago 1.1.4 (Web App)").
//
//   * HOW IT CONNECTS (verified against the Angular source code at
//     src/app/archipelago-client.ts and src/app/connect-screen/connect-screen-
//     state.ts):
//       - The app connects via the standard Archipelago WebSocket protocol.
//       - AP game string: "Autopelago" — verified in the client.login() call
//         in src/app/archipelago-client.ts, parameter 3 = 'Autopelago'.
//       - Connection parameters in the web UI: host (default "archipelago.gg"),
//         port (default 38281), slot name, optional password.
//       - The web app supports URL query parameters for pre-filling connection
//         details (verified: src/app/connect-screen/connect-screen-state.ts,
//         QUERY_PARAM_NAME_MAP). The short-form params are:
//             h = host
//             p = port  (numeric string)
//             s = slot  (slotname)
//             w = password (plaintext; the 'W' param is encrypted with the slot)
//         Example: https://autopelago.app/?h=archipelago.gg&p=38281&s=MySlot
//         All three (h, p, s) must be present for the auto-fill to fire;
//         password is optional (omitted when empty or null).
//       - The web app runs in the browser — the launcher's job is to open the
//         default browser to the prefilled URL. No exe to start, no process to
//         track. ConnectsItself = true (the web app owns the slot connection),
//         SupportsStandalone = false (requires an AP server to do anything).
//
//   * THE AP WORLD: game string "Autopelago" — verified verbatim in the source
//     (client.login(..., ..., 'Autopelago', ...)). World id "autopelago".
//     No .apworld is shipped on the v1.x releases (the web-app releases ship
//     only dist.zip). The v0.11.x series did ship autopelago.apworld alongside
//     dist.zip — users may still need that from the last pre-v1.0 release
//     (v0.11.17) if their AP generator does not bundle the world. This plugin
//     does NOT download or manage the apworld (it is the user's generator setup
//     concern); the settings panel surfaces a link to the relevant release.
//
//   * NO INSTALL REQUIRED: Autopelago runs at https://autopelago.app — a
//     permanent hosted deployment. "Install" is a no-op: IsInstalled = true
//     always (the web app is always available at the canonical URL). The
//     launcher's Install button is wired to "open the web app" semantics.
//     CheckForUpdateAsync fetches the latest GitHub release tag and surfaces it
//     as AvailableVersion so the user can see when the hosted app updates; there
//     is nothing to download or deploy locally.
//
//   * WHY NO OFFLINE/LOCAL SERVE: the dist.zip is a plain static Angular PWA
//     with an ngsw-config.json (service worker). It can self-host on any local
//     web server, but the hosted https://autopelago.app is always current and
//     requires no server setup. Serving locally adds a dependency (Python
//     SimpleHTTPServer or dotnet dev-certs or node http-server) without a clear
//     user benefit. This plugin therefore always navigates to the canonical URL.
//     If a future release ships a self-contained local-server exe, adapt to the
//     ChecksFinderPlugin / MeritousPlugin pattern.
//
// DEFENSIVE / UNVERIFIED DETAILS (flagged, "verify at build time"):
//   * URL query-param auto-fill requires ALL of h, p, s — the launcher always
//     sends all three so the game screen opens directly without a manual connect
//     step. Password 'w' is omitted when the session password is empty/null.
//   * The web app uses WebSockets (archipelago.js) — no firewall issue beyond
//     standard browser WebSocket, which is port-80/443-tunnelled when needed.
//   * "IsRunning" cannot be tracked (no process) — it is always false. The
//     launcher's "Stop" button is a no-op: the user closes the browser tab.
//   * The settings sidecar (Games/ROMs/autopelago/autopelago_launcher.json) is
//     kept for future launcher-side toggles (e.g. preferred browser path).
//     It is currently unused beyond the scaffolding boilerplate.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class AutopelagoPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER       = "airbreather";
    private const string GITHUB_REPO        = "Autopelago";
    private const string RepoUrl            = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL    =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"{GH_RELEASES_URL}/latest";

    /// The canonical hosted web app URL — no install required.
    private const string WebAppUrl          = "https://autopelago.app";

    /// AP setup guide (Archipelago official website).
    private const string SetupGuideUrl      = "https://archipelago.gg/tutorial/Autopelago/setup_en";

    /// Last release that shipped autopelago.apworld for users who still need it.
    private const string LastApWorldRelease =
        $"{RepoUrl}/releases/tag/v0.11.17";

    /// Pinned fallback version (used only for AvailableVersion display when API is
    /// unreachable — nothing is actually downloaded).
    private const string FallbackVersion    = "1.1.4";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ─────────────────────────────────────────────────

    public string GameId      => "autopelago";
    public string DisplayName => "Autopelago";
    public string Subtitle    => "Web App · autonomous AP player";

    /// EXACT AP game string — verified in src/app/archipelago-client.ts:
    ///     client.login(..., ..., 'Autopelago', ...)
    public string ApWorldName => "Autopelago";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "autopelago.png");

    public string ThemeAccentColor => "#7B4BB8";   // rat/purple theme
    public string[] GameBadges     => new[] { "Free · web app" };

    public string Description =>
        "Autopelago is an Archipelago meta-game where a rat player autonomously " +
        "explores a multiworld and checks locations on your behalf — it's a " +
        "game so easy, it plays itself. The rat navigates landmarks, passes " +
        "ability checks, receives items that buff or debuff its movement, and " +
        "keeps going until all locations are cleared. You can nudge it via chat " +
        "commands (@RatName go \"Location\") or just watch. Because Autopelago " +
        "IS the Archipelago client — there is nothing to install — the launcher " +
        "opens the web app at autopelago.app with your connection details " +
        "pre-filled so you can jump straight into your multiworld room.";

    public string? VideoPreviewUrl  => null;
    public string[] ScreenshotUrls  => Array.Empty<string>();

    // ── Version state ──────────────────────────────────────────────────────────
    //
    // There is no local install — IsInstalled is always true (the hosted web
    // app is always available). InstalledVersion surfaces the latest available
    // version for display; it matches AvailableVersion once CheckForUpdateAsync
    // has run successfully.

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// Always true — the web app at autopelago.app requires no local install.
    public bool IsInstalled => true;
    public bool IsWebBased => true;

    /// Always false — no process to track (the game runs in the browser).
    public bool IsRunning => false;

    // ── Game traits ────────────────────────────────────────────────────────────

    /// The web app IS the AP client — it connects directly; the launcher must
    /// not hold a competing ApClient session on the same slot while it runs.
    public bool ConnectsItself => true;

    /// The web app does nothing without an AP server room; no standalone mode.
    public bool SupportsStandalone => false;

    // ── Paths ──────────────────────────────────────────────────────────────────

    /// No local game install — returns the canonical web app URL as a sentinel.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Autopelago");

    private string SettingsSidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                        "autopelago_launcher.json");

    private string SettingsSidecarDir => Path.GetDirectoryName(SettingsSidecarPath)!;

    // ── AP bridge events ───────────────────────────────────────────────────────
    // The web app owns the slot — the launcher relays nothing. These exist for
    // interface compatibility (ConnectsItself = true). GameExited is also
    // suppressed: no process to track means no exit event to fire.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Lifecycle — CheckForUpdate ─────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // InstalledVersion mirrors AvailableVersion (the hosted web app is always
        // at the latest deployed version; the user cannot be "behind").
        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            string ver = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(GITHUB_OWNER, GITHUB_REPO, ct));
            AvailableVersion  = ver;
            InstalledVersion  = ver;   // web app is always current
        }
        catch
        {
            AvailableVersion = null;
            InstalledVersion = null;  // contract: never throw on network failure
        }
    }

    // ── Lifecycle — InstallOrUpdate ────────────────────────────────────────────
    // "Install" for Autopelago = nothing to download; the hosted web app is always
    // available. We check for the latest version, surface it, and report ready.

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((10, "Checking latest Autopelago release..."));

        try
        {
            var (version, _) = await ResolveLatestReleaseAsync(ct);
            AvailableVersion = version;
            InstalledVersion = version;
            progress.Report((100,
                $"Autopelago {version} is live at autopelago.app — press Play to " +
                "open the web app with your connection details pre-filled."));
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Offline or API-rate-limited: the web app is still accessible.
            InstalledVersion ??= FallbackVersion;
            progress.Report((100,
                "Autopelago is a hosted web app — no download needed. " +
                "Press Play to open autopelago.app in your browser."));
        }
    }

    // ── Lifecycle — Verify ─────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return true;   // web app, always available
    }

    // ── Lifecycle — Launch ─────────────────────────────────────────────────────

    /// Open autopelago.app in the default browser with AP connection parameters
    /// pre-filled via verified URL query params (h=host, p=port, s=slot, w=pw).
    ///
    /// VERIFIED: src/app/connect-screen/connect-screen-state.ts defines
    ///   QUERY_PARAM_NAME_MAP = { host:'h', port:'p', slot:'s', password:'w', ... }
    /// All three of h, p, s must be present for the auto-fill to activate.
    /// The 'w' (plaintext password) param is omitted when no password is set.
    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        var (host, port) = ParseServerHostPort(session.ServerUri);

        var url = BuildWebAppUrl(host, port, session.SlotName, session.Password);
        OpenBrowser(url);
        return Task.CompletedTask;
    }

    // StopAsync is a no-op — no process to kill (the game is a browser tab).
    public Task StopAsync()
    {
        // Nothing to do — the user closes the browser tab.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ──────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The web app receives items from the AP server directly;
        // the launcher has nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The web app renders its own AP status; no launcher HUD channel.
    }

    // ── Settings UI ────────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var accent  = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x7B, 0x4B, 0xB8));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        // ── Section: How it works ─────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "ABOUT AUTOPELAGO",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Autopelago is a hosted web application — there is nothing to " +
                   "install locally. The game runs in your browser at autopelago.app. " +
                   "Press Play to open the web app with your Archipelago server, port, " +
                   "slot name, and password automatically pre-filled in the URL.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: AP connection ─────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "ARCHIPELAGO CONNECTION",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "The launcher opens autopelago.app with your connection details " +
                   "pre-filled via URL query parameters (h=host, p=port, s=slot, " +
                   "w=password). The web app connects directly to the Archipelago " +
                   "server as the 'Autopelago' game client — the launcher does not " +
                   "hold a competing connection while the web app is open.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: apworld note ─────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "APWORLD / GAME GENERATION",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "The current v1.x releases do not ship a standalone autopelago" +
                   ".apworld. If your Archipelago installation does not bundle the " +
                   "Autopelago world, download autopelago.apworld from the last " +
                   "v0.11.17 release and place it in Archipelago's custom_worlds " +
                   "folder before generating a seed.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Open web app button ───────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "OPEN WEB APP",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });
        var openBtn = new System.Windows.Controls.Button
        {
            Content             = "Open autopelago.app in browser",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Padding             = new System.Windows.Thickness(12, 6, 12, 6),
            Background          = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground          = fg,
            BorderBrush         = accent,
            FontSize            = 12,
            Margin              = new System.Windows.Thickness(0, 0, 0, 12),
            Cursor              = System.Windows.Input.Cursors.Hand,
        };
        openBtn.Click += (_, _) => OpenBrowser(WebAppUrl);
        panel.Children.Add(openBtn);

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "LINKS",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("autopelago.app (live game) ↗",              WebAppUrl),
            ("Autopelago on GitHub ↗",                    RepoUrl),
            ("Latest release ↗",                          $"{RepoUrl}/releases/latest"),
            ("v0.11.17 (last release with .apworld) ↗",  LastApWorldRelease),
            ("Archipelago Official ↗",                    "https://archipelago.gg"),
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
                Foreground          = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor              = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => OpenBrowser(u);
            panel.Children.Add(btn);
        }

        return panel;
    }

    // ── News feed ──────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc  = JsonDocument.Parse(json);
            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) &&
                    d.ValueKind == JsonValueKind.String)
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

    // ── Private helpers — release resolution ───────────────────────────────────

    private async Task<(string Version, string? DistZipUrl)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null)
            {
                // Autopelago v1.x ships only "dist.zip" per release — pick it up
                // opportunistically (we don't use it, but surface the URL in case
                // a future extension wants to self-host locally).
                string? distZip = null;
                if (root.TryGetProperty("assets", out var assets) &&
                    assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        if (name != null && url != null &&
                            name.Equals("dist.zip", StringComparison.OrdinalIgnoreCase))
                        {
                            distZip = url;
                            break;
                        }
                    }
                }
                return (version, distZip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → fallback below */ }

        return (FallbackVersion, null);
    }

    // ── Private helpers — URL building ─────────────────────────────────────────

    /// Build the autopelago.app URL with AP connection params pre-filled.
    /// VERIFIED query params (src/app/connect-screen/connect-screen-state.ts):
    ///   h = host, p = port (numeric string), s = slot, w = password (plaintext)
    /// All three (h, p, s) must be present for auto-fill to activate.
    /// Password 'w' is omitted when the session has no password.
    private static string BuildWebAppUrl(string host, int port, string slot, string? password)
    {
        // Percent-encode the slot/password (they can contain special chars).
        string encodedHost = Uri.EscapeDataString(host);
        string encodedSlot = Uri.EscapeDataString(slot);

        string url = $"{WebAppUrl}/?h={encodedHost}&p={port}&s={encodedSlot}";

        if (!string.IsNullOrEmpty(password))
            url += $"&w={Uri.EscapeDataString(password)}";

        return url;
    }

    // ── Private helpers — server URI parsing ───────────────────────────────────

    private const int DefaultApPort = 38281;

    /// Accepts "archipelago.gg:38281", "ws://host:port", "wss://host:port", a
    /// bare hostname. Default AP port is 38281.
    private static (string Host, int Port) ParseServerHostPort(string serverUri)
    {
        string s = serverUri.Trim();
        foreach (string prefix in new[] { "wss://", "ws://", "archipelago://" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..];
                break;
            }
        }

        // Strip any trailing path.
        int slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];

        string host = s;
        int    port = DefaultApPort;

        // IPv6 bracketed literal: [::1]:38281
        if (s.StartsWith('['))
        {
            int close = s.IndexOf(']');
            if (close > 0)
            {
                host = s[1..close];
                string rest = s[(close + 1)..];
                if (rest.StartsWith(':') &&
                    int.TryParse(rest[1..], out int p6) && p6 > 0 && p6 <= 65535)
                    port = p6;
            }
        }
        else
        {
            // Count colons: >1 means bare IPv6, so no port stripping.
            int colonCount = 0;
            foreach (char c in s) if (c == ':') colonCount++;

            if (colonCount == 1)
            {
                int colon = s.LastIndexOf(':');
                if (colon > 0 &&
                    int.TryParse(s[(colon + 1)..], out int p) && p > 0 && p <= 65535)
                {
                    host = s[..colon];
                    port = p;
                }
            }
            // colonCount > 1 → bare IPv6 literal, keep host = s
        }

        if (string.IsNullOrWhiteSpace(host)) host = "archipelago.gg";
        return (host, port);
    }

    // ── Private helpers — misc ─────────────────────────────────────────────────

    /// "v1.1.4" → "1.1.4"; strips a leading 'v' before a digit. null/blank → null.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Open a URL in the system default browser. Best effort; never throws.
    private static void OpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true
            });
        }
        catch { /* non-fatal — user can always type the URL manually */ }
    }

    // ── Settings sidecar — scaffold for future launcher-side toggles ───────────

    private sealed class AutopelagoSettings
    {
        // Reserved for future launcher-side options (e.g. preferred browser,
        // local-serve toggle). Currently empty — the web app has no launcher-
        // side configuration that isn't derivable from the AP session.
    }

    private AutopelagoSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<AutopelagoSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(AutopelagoSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(
                SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }
}
