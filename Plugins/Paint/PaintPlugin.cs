using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LauncherV2.Core;

// IMPORTANT (real project has <UseWindowsForms>true</UseWindowsForms>):
// WPF UI types that collide with WinForms are FULLY QUALIFIED below
// (System.Windows.Controls.*, System.Windows.Media.*, System.Windows.Thickness,
// System.Windows.FontWeights, System.Windows.HorizontalAlignment,
// System.Windows.TextWrapping, …) to avoid CS0104 ambiguities.
// Do NOT add `using System.Windows.Controls;` / `using System.Windows.Media;` here.

namespace LauncherV2.Plugins.Paint;

// ═══════════════════════════════════════════════════════════════════════════════
// PaintPlugin — launcher integration for "Paint", the Archipelago randomizer
// built on jsPaint (an open-source browser remake of Microsoft Paint).
//
// Paint is a BROWSER GAME, not a downloadable native exe. The game is played at
//     https://mariomantaw.github.io/jspaint/
// in the player's default web browser. There is no game binary to download, no
// install directory, no version to track. "Install" is a no-op (the game lives
// on the web), and "launch" means opening the URL in the default browser.
//
// REALITY CHECK (2026-06-14) — facts verified this session
// ─────────────────────────────────────────────────────────────────────────────
//   * GAME: jsPaint, an open-source browser remake of Microsoft Paint, maintained
//     by MarioManTAW and hosted at https://mariomantaw.github.io/jspaint/. The
//     Archipelago integration is built directly into this hosted version.
//
//   * HOW IT CONNECTS (verified against the official Archipelago Paint setup
//     guide — https://archipelago.gg/tutorial/Paint/guide_en): all steps take
//     place inside the browser tab. The guide's documented steps are:
//         1. Generate a seed in Archipelago.
//         2. Visit https://mariomantaw.github.io/jspaint/
//         3. Enter your server details, slot name, and (optional) room password
//            in the in-page connection panel, then click "Connect".
//         4. Optionally load a custom goal image via File → Open Goal Image.
//         5. Async-play note: progress is saved in the browser URL hash, so
//            keep the tab open or save the URL with the hash to resume.
//     There is NO documented command-line, config file, or URL query-parameter
//     mechanism for auto-filling the server/slot/password — the in-page fields
//     ARE the documented interface. This plugin therefore launches the browser
//     to the plain game URL and copies the server address to the clipboard as a
//     best-effort convenience (so the player can paste instead of retyping).
//     We do NOT invent undocumented URL parameters.
//
//   * AP WORLD: game string "Paint" — verified against AP-main
//     worlds/paint/__init__.py (PaintWorld.game = "Paint", version "0.5.2").
//     The world ships with Archipelago itself, so there is NO standalone apworld
//     file to download here. Slot data keys: logic_percent, goal_percent,
//     goal_image, death_link, canvas_size_increment, version.
//
//   * ConnectsItself = true: the browser tab connects directly to the AP server
//     using the player's credentials; the launcher must NOT hold its own AP
//     client on the same slot while the tab is open.
//
//   * SupportsStandalone = true: jsPaint can be used as a plain browser paint
//     application without an Archipelago room (the AP connection panel is
//     optional).
//
//   * IsInstalled always returns true: no local files are needed. The "game" is
//     the hosted web URL, always reachable as long as the player has a browser
//     and an internet connection.
//
//   * This plugin has NO download/extract machinery — there is nothing to fetch.
//     InstallOrUpdateAsync immediately returns success, and VerifyInstallAsync
//     always returns true.
//
//   * Settings sidecar at Games/ROMs/paint/paint_launcher.json — reserved for
//     any future launcher-side options (currently the sidecar is scaffolded but
//     holds no settings). This is consistent with the other native plugins.
//
// DEFENSIVE / UNVERIFIED DETAILS (flagged):
//   * The jsPaint URL has been stable since the AP world shipped. If it ever
//     moves, update GameUrl below. The AP-world home_page key in archipelago.json
//     is the canonical reference.
//   * The clipboard convenience copies "host:port" as typed by the player into
//     the launcher's server field. The in-page field label and whether the game
//     accepts a "host:port" combined string vs separate fields is not documented
//     in the guide — the player pastes and adjusts as needed.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PaintPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    /// The hosted jsPaint Archipelago game URL (official, verified 2026-06-14).
    private const string GameUrl = "https://mariomantaw.github.io/jspaint/";

    /// GitHub repo for jsPaint (used in the settings panel links only).
    private const string RepoUrl = "https://github.com/MarioManTAW/jspaint";

    /// Official Archipelago "Paint" setup guide.
    private const string SetupGuideUrl = "https://archipelago.gg/tutorial/Paint/guide_en";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "paint";
    public string DisplayName => "Paint";
    public string Subtitle    => "Browser · built-in Archipelago";

    /// EXACT AP game string — verified against worlds/paint/__init__.py
    /// (PaintWorld.game = "Paint").
    public string ApWorldName => "Paint";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "paint.png");

    public string ThemeAccentColor => "#E44C2E";   // paint-bucket red

    public string[] GameBadges => new[] { "Free", "Browser" };

    public string Description =>
        "Paint is an Archipelago randomizer built on jsPaint, an open-source " +
        "browser remake of Microsoft Paint by MarioManTAW. Draw on a canvas to " +
        "match a target image — you start with only a magnifier and one drawing " +
        "tool, and the canvas size is locked until you receive Progressive Canvas " +
        "Width and Height items from the multiworld. More tools, palette slots, " +
        "and colour depth arrive as Archipelago items from your friends. There " +
        "is nothing to install: press Play and the game opens in your browser. " +
        "Connect from the in-page panel — just enter your server, slot name, and " +
        "(optional) password, then click Connect.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    // Paint lives on the web — there is no installed version to track. The
    // "available version" is sourced from the AP world's own __init__.py version
    // string ("0.5.2"), kept as a constant since there is no GitHub releases API
    // to poll. This is honest: the game itself has no versioned downloads.

    /// AP world version at time of writing (worlds/paint/__init__.py line).
    private const string ApWorldVersion = "0.5.2";

    public string? InstalledVersion => null;      // browser game — no local install
    public string? AvailableVersion => ApWorldVersion;

    /// Always true — the game is hosted on the web and requires no local files.
    public bool IsInstalled => true;
    public bool IsWebBased => true;

    public bool IsRunning { get; private set; }

    /// Browser game — no meaningful install directory; return the ROM library slot.
    public string GameDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore
    /// so the plugin stays a single self-contained source file). The sidecar
    /// holds no settings today but exists as the standard scaffold for future
    /// launcher-side options.
    private string SettingsSidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                        "paint_launcher.json");

    private string SettingsSidecarDir => Path.GetDirectoryName(SettingsSidecarPath)!;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The browser tab connects to AP directly — the launcher relays nothing.
    // GameExited is also suppressed: we never receive an exit notification from
    // a browser tab (there is no process to watch), so the event is never fired.
    // These exist for interface compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Clipboard convenience ─────────────────────────────────────────────────
    // Paint has no documented config file or URL-parameter connection interface.
    // We copy the server address to the clipboard when the player presses Play
    // so they can paste it into the in-page connection panel.

    private string? _clipboardServer;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Browser game — no local install and no releases API to poll.
        // AvailableVersion is the static AP world version constant.
        return Task.CompletedTask;
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // Nothing to install — Paint is a hosted browser game with no downloads.
        progress.Report((100,
            "Paint is a browser game — no download needed. Press Play to open " +
            "the game in your browser, then enter your Archipelago connection " +
            "details in the in-page panel."));
        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        // Browser game — always verified (no local files to check).
        return Task.FromResult(true);
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // Copy the server address to the clipboard as a convenience — there is
        // no documented URL parameter or config file for Paint. The player pastes
        // into the in-page "Server" field in the browser.
        try { CopyServerToClipboard(session); }
        catch { /* clipboard is optional — non-fatal */ }

        // Open the game URL in the system default browser.
        OpenBrowser(GameUrl);

        // Paint is a browser tab — we have no process to track, but fire
        // IsRunning = true briefly as a UI hint; the browser manages its own
        // lifecycle. We never receive an exit event from a browser tab, so
        // IsRunning stays true until StopAsync is called (or the launcher closes).
        IsRunning = true;
        return Task.CompletedTask;
    }

    /// jsPaint works as a plain paint application without an AP room —
    /// standalone (non-AP) play is supported.
    public bool SupportsStandalone => true;

    /// The browser tab connects to the AP server directly; the launcher must not
    /// hold its own AP client on the same slot while the tab is open.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // Open jsPaint without any AP connection prefill — the player uses it as
        // a plain paint application (no AP room required).
        OpenBrowser(GameUrl);
        IsRunning = true;
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // There is no process to kill (the browser manages its own lifecycle).
        IsRunning = false;
        // Clear the clipboard if it still holds the server address we copied, so
        // it does not linger after the session.
        ClearServerFromClipboard();
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(
        ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The browser tab receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The game renders its own AP connection status in the browser;
        // there is no launcher HUD channel.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var accent  = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xE4, 0x4C, 0x2E));
        var panel   = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        // ── Section: About ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "ABOUT PAINT", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Paint is a browser-based Archipelago randomizer built on jsPaint, " +
                   "an open-source remake of Microsoft Paint. There is no download: the " +
                   "game runs entirely in your web browser at mariomantaw.github.io/jspaint/. " +
                   "Pressing Play opens the game in your default browser — no install " +
                   "required.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = $"AP world version: {ApWorldVersion}",
            FontSize = 11, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: How it connects ──────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "ARCHIPELAGO CONNECTION", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Paint connects from inside the browser tab. When you press Play, " +
                   "jsPaint opens in your default browser and your server address is " +
                   "copied to the clipboard. In the browser, enter your server details, " +
                   "slot name, and (optional) room password in the Archipelago connection " +
                   "panel, then click Connect. There is no config file to edit — the " +
                   "in-page panel is the only documented connection interface.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Async play tip: progress is saved in the URL hash. If you play " +
                   "asynchronously, leave the tab open or save the full URL (with the " +
                   "hash) so you can resume without losing progress.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Play Paint in browser ↗",     GameUrl),
            ("jsPaint GitHub ↗",            RepoUrl),
            ("Paint Setup Guide ↗",         SetupGuideUrl),
            ("Archipelago Official ↗",      "https://archipelago.gg"),
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
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(u)
                        { UseShellExecute = true });
                }
                catch { }
            };
            panel.Children.Add(btn);
        }
        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────
    // Paint has no GitHub releases to poll. Return an empty list rather than
    // polling a URL that has no release feed (the AP world lives in the monorepo
    // and jsPaint commits are not versioned releases for our purposes).

    public Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
        => Task.FromResult(Array.Empty<NewsItem>());

    // ── Private helpers — clipboard convenience (best effort) ─────────────────
    // Paint has no config file / CLI / URL parameter for the AP connection — it
    // is entered in-page. As a courtesy we put the "host:port" on the clipboard
    // at launch so the player can paste it into the in-page server field, and we
    // clear it again at StopAsync if it is still ours. The password is NOT put
    // on the clipboard.

    private void CopyServerToClipboard(ApSession session)
    {
        string server = FormatServerUrl(session.ServerUri);
        if (string.IsNullOrEmpty(server)) return;

        void Set()
        {
            try
            {
                System.Windows.Clipboard.SetText(server);
                _clipboardServer = server;
            }
            catch { /* clipboard busy/unavailable — non-fatal */ }
        }

        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess()) disp.Invoke(Set);
        else Set();
    }

    private void ClearServerFromClipboard()
    {
        if (_clipboardServer == null) return;
        string mine = _clipboardServer;
        _clipboardServer = null;

        void Clear()
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText() &&
                    System.Windows.Clipboard.GetText() == mine)
                    System.Windows.Clipboard.Clear();
            }
            catch { /* non-fatal */ }
        }

        var disp = System.Windows.Application.Current?.Dispatcher;
        if (disp != null && !disp.CheckAccess()) disp.Invoke(Clear);
        else Clear();
    }

    /// Normalise the launcher's server URI into "host:port" for the clipboard.
    /// Handles ws://wss:// prefixes, bare hostnames, and IPv6 literals.
    private static string FormatServerUrl(string serverUri)
    {
        var (host, port) = ParseServerHostPort(serverUri);
        return host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
    }

    /// Parse a server URI of the form "host:port", "ws://host:port",
    /// "wss://host:port", a bare hostname, or an IPv6 literal.
    /// Default AP port is 38281.
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

        int slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];

        string host = s;
        int    port = 38281;

        int colonCount = 0;
        foreach (char c in s) if (c == ':') colonCount++;

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
        else if (colonCount > 1)
        {
            host = s;  // bare IPv6 literal — no port carried this way
        }
        else
        {
            int colon = s.LastIndexOf(':');
            if (colon > 0 &&
                int.TryParse(s[(colon + 1)..], out int p) && p > 0 && p <= 65535)
            {
                host = s[..colon];
                port = p;
            }
        }

        if (host.Length == 0) host = "archipelago.gg";
        return (host, port);
    }

    // ── Private helpers — browser launch ─────────────────────────────────────

    /// Open a URL in the system default browser (ShellExecute).
    private static void OpenBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url)
                { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not open the browser for Paint. Try visiting {url} " +
                "manually. Error: " + ex.Message, ex);
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // Kept in this plugin's OWN JSON file so it stays a single self-contained
    // source file and does not modify Core/SettingsStore. The sidecar holds no
    // settings today but exists as the standard scaffold for future options.

    private sealed class PaintSettings
    {
        // Intentionally empty — a placeholder for future launcher-side options.
    }

    private PaintSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<PaintSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(PaintSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — settings just won't persist this time */ }
    }
}
