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

namespace LauncherV2.Plugins.OpenRCT2;

// ═══════════════════════════════════════════════════════════════════════════════
// OpenRCT2Plugin — install / launch for "OpenRCT2" Archipelago integration.
//
// ── VERIFIED FACTS (2026-06-19, sources: Crazycolbster/rollercoaster-tycoon-
//    randomizer, archipelago.miraheze.org/wiki/OpenRCT2) ─────────────────────
//
//   * GAME: OpenRCT2 — an open-source re-implementation of RollerCoaster Tycoon
//     1 and 2. Free download. Requires the original RCT1 / RCT2 game data files
//     (the player must own them, e.g. via GOG / Steam). The launcher does NOT
//     download OpenRCT2 itself — the player installs it from openrct2.io.
//
//   * AP WORLD: apworld file is "openrct2.apworld" (verified from Crazycolbster
//     release assets). AP game string: "OpenRCT2" (verified directly from the
//     Archipelago connection code:
//       {cmd: "Connect", game: "OpenRCT2", ...}
//     in src/modules/archipelagoConnection.ts).
//     Repository: Crazycolbster/rollercoaster-tycoon-randomizer (community fork
//     of Die4Ever/rollercoaster-tycoon-randomizer, adds full AP integration).
//     Latest verified release: v0.1.21-beta.
//     Release assets: openrct2.apworld + rctrando.js + 4 × .park scenario files.
//
//   * HOW IT CONNECTS: The integration is an OpenRCT2 PLUGIN (a JavaScript .js
//     file — "rctrando.js") placed in OpenRCT2's plugin/ folder. The plugin
//     provides a built-in AP connection window; the player enters the AP server,
//     slot, and password in-game. ConnectsItself = true (the in-game plugin owns
//     the AP slot connection; the launcher must NOT hold a competing ApClient).
//
//   * SCENARIOS: four "Archipelago Madness" .park scenarios ship with each
//     release — two vanilla (synchronous / asynchronous) and two with Expansions.
//     These go in the OpenRCT2 scenarios/ folder.
//
//   * INSTALLATION FLOW (plugin-managed):
//     1. Download rctrando.js -> OpenRCT2 plugin/ folder.
//     2. Download the four Archipelago Madness .park files -> scenarios/ folder.
//     3. Download openrct2.apworld for reference.
//     The player must have OpenRCT2 installed and its plugin/ and scenario/
//     directories located. The Settings panel provides a Browse button for the
//     OpenRCT2 install directory.
//
//   * FREE GAME: OpenRCT2 is free and open-source (openrct2.io). However, it
//     REQUIRES original RCT2 (or RCT1) data files which the player must own.
//
//   * SupportsStandalone = true: OpenRCT2 can run without AP.
//
//   * BUILD NOTE: UseWindowsForms=true alongside UseWPF=true; all WPF UI types
//     are fully qualified to avoid CS0104 ambiguity.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class OpenRCT2Plugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GH_OWNER    = "Crazycolbster";
    private const string GH_REPO     = "rollercoaster-tycoon-randomizer";
    private const string GH_RELEASES = $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases";
    private const string REPO_URL    = $"https://github.com/{GH_OWNER}/{GH_REPO}";
    private const string OPENRCT2_URL = "https://openrct2.io/";

    /// Pinned fallback asset URLs (verified 2026-06-19 from v0.1.21-beta release).
    private const string FALLBACK_TAG         = "v0.1.21-beta";
    private const string FALLBACK_PLUGIN_URL  =
        "https://github.com/Crazycolbster/rollercoaster-tycoon-randomizer/releases/download/" +
        "v0.1.21-beta/rctrando.js";
    private const string FALLBACK_APWORLD_URL =
        "https://github.com/Crazycolbster/rollercoaster-tycoon-randomizer/releases/download/" +
        "v0.1.21-beta/openrct2.apworld";

    private const string PLUGIN_JS_FILENAME = "rctrando.js";
    private const string VERSION_STAMP_FILE = "openrct2_ap_version.dat";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(15),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "openrct2";
    public string DisplayName => "OpenRCT2";
    public string Subtitle    => "Native PC · built-in AP plugin";

    /// EXACT AP game string — verified from archipelagoConnection.ts source:
    /// {cmd: "Connect", game: "OpenRCT2", ...}
    public string ApWorldName => "OpenRCT2";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "openrct2.png");

    public string ThemeAccentColor => "#E8A020";   // RCT golden yellow

    public string[] GameBadges => new[] { "Free · Own RCT2 Data" };

    public string Description =>
        "OpenRCT2 is an open-source remake of RollerCoaster Tycoon 2, playable " +
        "with Archipelago via a community JavaScript plugin. The integration adds " +
        "a built-in connection window inside OpenRCT2 — enter your AP server, slot, " +
        "and password in-game. Ride research, unlocks, and park milestones become " +
        "location checks shuffled across the multiworld. Four special \"Archipelago " +
        "Madness\" scenarios are included (vanilla and Expansions, synchronous and " +
        "asynchronous). OpenRCT2 is free from openrct2.io, but requires original " +
        "RCT2 (or RCT1) data files which you must own (GOG/Steam). The launcher " +
        "installs the AP plugin and scenarios into your OpenRCT2 directory. " +
        "Community integration by Crazycolbster.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version / install state ───────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled =>
        !string.IsNullOrEmpty(GameDirectory) &&
        Directory.Exists(PluginDir) &&
        File.Exists(Path.Combine(PluginDir, PLUGIN_JS_FILENAME));

    public bool IsRunning { get; private set; }

    public bool ConnectsItself     => true;
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// The player's OpenRCT2 install directory (set via Browse in Settings).
    public string GameDirectory { get; set; } = string.Empty;

    private string PluginDir   => Directory.Exists(GameDirectory)
        ? Path.Combine(GameDirectory, "plugin")    : string.Empty;
    private string ScenarioDir => Directory.Exists(GameDirectory)
        ? Path.Combine(GameDirectory, "scenario")  : string.Empty;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        string stampPath = Path.Combine(AppContext.BaseDirectory, "Games", "OpenRCT2", VERSION_STAMP_FILE);
        InstalledVersion = File.Exists(stampPath)
            ? (await File.ReadAllTextAsync(stampPath, ct)).Trim()
            : null;

        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(GH_OWNER, GH_REPO, ct));
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(GameDirectory) || !Directory.Exists(GameDirectory))
            throw new InvalidOperationException(
                "OpenRCT2 install directory not set. Use Browse in Settings to locate it.");

        Directory.CreateDirectory(PluginDir);
        Directory.CreateDirectory(ScenarioDir);

        string cacheDir = Path.Combine(AppContext.BaseDirectory, "Games", "OpenRCT2");
        Directory.CreateDirectory(cacheDir);

        // Resolve latest release
        progress.Report((5, "Fetching latest OpenRCT2 AP release..."));
        string pluginUrl  = FALLBACK_PLUGIN_URL;
        string apworldUrl = FALLBACK_APWORLD_URL;
        string releaseTag = FALLBACK_TAG;
        var    parkUrls   = new List<string>();

        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    if (!el.TryGetProperty("tag_name", out var tagProp)) continue;
                    releaseTag = tagProp.GetString()?.Trim() ?? FALLBACK_TAG;
                    if (el.TryGetProperty("assets", out var assets))
                    {
                        foreach (var asset in assets.EnumerateArray())
                        {
                            string? name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                            string? url  = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                            if (name == null || url == null) continue;
                            if (name.Equals("rctrando.js", StringComparison.OrdinalIgnoreCase))
                                pluginUrl = url;
                            else if (name.Equals("openrct2.apworld", StringComparison.OrdinalIgnoreCase))
                                apworldUrl = url;
                            else if (name.EndsWith(".park", StringComparison.OrdinalIgnoreCase))
                                parkUrls.Add(url);
                        }
                    }
                    break;
                }
            }
        }
        catch { /* use pinned fallbacks */ }

        // Download plugin JS
        progress.Report((10, $"Downloading rctrando.js ({releaseTag})..."));
        await DownloadFileAsync(pluginUrl, Path.Combine(PluginDir, PLUGIN_JS_FILENAME),
            progress, 10, 40, ct);

        // Download .park scenarios
        if (parkUrls.Count > 0)
        {
            progress.Report((40, "Downloading Archipelago Madness scenarios..."));
            int i = 0;
            foreach (string parkUrl in parkUrls)
            {
                string parkName = Uri.UnescapeDataString(parkUrl.Split('/')[^1]);
                // Decode %20 etc. that appear in "Archipelago Madness (Expansions).park"
                parkName = parkName.Replace("%20", " ").Replace("%28", "(").Replace("%29", ")");
                int startPct = 40 + i * 15;
                int endPct   = startPct + 15;
                await DownloadFileAsync(parkUrl, Path.Combine(ScenarioDir, parkName),
                    progress, startPct, Math.Min(endPct, 80), ct);
                i++;
            }
        }
        else
        {
            // Fallback: no scenario URLs resolved; note it
            progress.Report((80, "Scenarios not found in release — check GitHub releases page."));
        }

        // Download apworld for reference
        progress.Report((80, "Downloading openrct2.apworld..."));
        await DownloadFileAsync(apworldUrl, Path.Combine(cacheDir, "openrct2.apworld"),
            progress, 80, 92, ct);

        // Write version stamp
        progress.Report((92, "Finalizing..."));
        await File.WriteAllTextAsync(Path.Combine(cacheDir, VERSION_STAMP_FILE), releaseTag, ct);
        InstalledVersion = releaseTag;

        progress.Report((100,
            $"OpenRCT2 AP plugin {releaseTag} installed to {PluginDir}. " +
            "Load an Archipelago Madness scenario from the Scenarios menu in-game."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
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
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (OpenRCT2 plugin handles the AP connection) ─────────

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
            "1. Download and install OpenRCT2 from openrct2.io (free).",
            "2. Ensure you have RCT2 data files (owned via GOG/Steam).",
            "3. Click Browse to point the launcher at your OpenRCT2 install folder.",
            "4. Click Install/Update to deploy the AP plugin (rctrando.js) and scenarios.",
            "5. Open OpenRCT2, load an Archipelago Madness scenario from the Scenarios menu.",
            "6. Use the in-game Archipelago connection window to enter your AP server details.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
            });
        }

        // Browse row
        panel.Children.Add(MakeHeader("OPENRCT2 INSTALL DIRECTORY", muted));
        var dirRow = new System.Windows.Controls.DockPanel
                     { Margin = new System.Windows.Thickness(0, 0, 0, 14) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text       = GameDirectory,
            IsReadOnly = true,
            FontSize   = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var browseBtn = new System.Windows.Controls.Button
        {
            Content = "Browse...", Width = 90,
            Padding    = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        browseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select OpenRCT2 install folder (containing openrct2.exe)",
                InitialDirectory = Directory.Exists(GameDirectory)
                    ? GameDirectory : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(browseBtn,
            System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(browseBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(MakeHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("OpenRCT2 Download ↗",        OPENRCT2_URL),
            ("OpenRCT2 AP Plugin GitHub ↗", REPO_URL),
            ("Plugin Releases ↗",           REPO_URL + "/releases"),
            ("Archipelago Wiki: OpenRCT2 ↗","https://archipelago.miraheze.org/wiki/OpenRCT2"),
            ("Archipelago Official ↗",      "https://archipelago.gg"),
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
            using var doc = JsonDocument.Parse(json);
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
        string? exe = null;

        if (Directory.Exists(GameDirectory))
        {
            foreach (string name in new[] { "openrct2.exe", "OpenRCT2.exe" })
            {
                string c = Path.Combine(GameDirectory, name);
                if (File.Exists(c)) { exe = c; break; }
            }
        }

        if (exe == null)
        {
            IsRunning = false;
            GameExited?.Invoke(-1);
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute  = false,
            };
            var proc = Process.Start(psi);
            _gameProcess = proc;
            if (proc != null)
            {
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) => { IsRunning = false; GameExited?.Invoke(proc.ExitCode); };
            }
            else { IsRunning = false; GameExited?.Invoke(0); }
        }
        catch (Exception ex)
        {
            IsRunning = false;
            throw new InvalidOperationException(
                "Could not launch OpenRCT2. Make sure it is installed and the " +
                "directory is set correctly in Settings.", ex);
        }
    }

    private async Task DownloadFileAsync(
        string url, string destPath,
        IProgress<(int Pct, string Msg)> progress,
        int startPct, int endPct,
        CancellationToken ct)
    {
        using var response = await _http.GetAsync(url,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? -1;
        using var src  = await response.Content.ReadAsStreamAsync(ct);
        using var dest = File.Create(destPath);
        var buf  = new byte[81920];
        long done = 0;
        int  read;
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            await dest.WriteAsync(buf.AsMemory(0, read), ct);
            done += read;
            if (total > 0)
            {
                int pct = startPct + (int)((endPct - startPct) * done / total);
                progress.Report((pct, $"Downloading... {done / 1024:N0} KB"));
            }
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
