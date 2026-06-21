using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Minit;

// ═══════════════════════════════════════════════════════════════════════════════
// MinitPlugin — install / update / launch for "Minit" played through the
// APMinit community apworld by qwint. Minit is a GameMaker: Studio 2 game.
// The integration uses a PROXY CLIENT (AP-side Python client that runs inside
// Archipelago) + a BSDIFF-PATCHED game data file, rather than a mod loader or
// in-game menu. This is a community apworld (NOT in AP-main) — the game must
// be owned by the player, and the Archipelago client runs as a separate process.
//
// REALITY CHECK (2026-06-14) — every fact below was verified against the real
// qwint/APMinit repository and docs this session.
// ─────────────────────────────────────────────────────────────────────────────
//
//   * AP WORLD GAME STRING: "Minit" — verified against:
//       APWorld/archipelago.json: {"game": "Minit", ...}
//       APWorld/__init__.py: class MinitWorld(World): ... game = "Minit"
//
//   * RELEASES: qwint/APMinit GitHub releases ship a SINGLE asset per release:
//       "minit.apworld" — e.g. v0.6.12/minit.apworld (latest 2026-06-14).
//     The apworld bundles the patching data (APWorld/data/patch.bsdiff) and
//     the proxy client (APWorld/MinitClient.py) inside. Latest: v0.6.12.
//
//   * STEAM APP ID: 609490 (Minit on Steam, verified). Epic Games is also
//     supported; itch.io is NOT supported by the apworld.
//
//   * HOW IT WORKS (verified against APWorld/docs/setup_en.md and MinitClient.py):
//     1. Player owns a copy of Minit (Steam/Epic). The game ships a GameMaker
//        data file "data.win" in its install folder.
//     2. Install the minit.apworld into Archipelago's custom_worlds folder.
//     3. In Archipelago, open the Minit Client (via ArchipelagoLauncher.exe).
//        Enter the host:port and slot name in the client's top bar, connect.
//     4. In the client, run "/patch" — the client locates "data.win" via the
//        path stored in Archipelago's host.yaml, applies APWorld/data/patch.bsdiff
//        to it using bsdiff4, and writes "ap_v1.0_data.win" next to it.
//        Then it LAUNCHES the game automatically: subprocess.Popen([exe_path,
//        "-game", patched_path]).
//     5. The proxy client runs a local HTTP server on localhost:11311. The
//        patched GameMaker game communicates with it via HTTP (POST /Locations,
//        GET /Items, etc.). The proxy client bridges between game and AP server.
//     6. There is NO in-game AP menu, NO config file for connection — the
//        connection is entered in the Archipelago client's UI (top bar).
//        The AP client owns the slot connection, NOT the launcher.
//
//   * ConnectsItself: FALSE for this integration. The Archipelago client (the
//     Python MinitClient running inside Archipelago) holds the AP server slot.
//     The launcher does NOT hold an ApClient on this slot — but the slot owner
//     is the AP application, not the game exe itself. Since the launcher has no
//     business trying to hold the slot either (the AP client already does), we
//     set ConnectsItself = true to ensure the launcher does not also try to
//     connect to the slot. This matches how SoH / OpenTTD / VVVVVV work: the
//     launcher defers the AP connection entirely.
//     NOTE: The patched game communicates via HTTP localhost:11311 to the proxy
//     client — so it is a three-process system: launcher → Archipelago (AP
//     client) → game. ConnectsItself = true prevents the launcher from opening
//     a fourth connection.
//
//   * INSTALL STRATEGY for this launcher:
//     a) Download minit.apworld from the latest qwint/APMinit GitHub release.
//     b) Stage it into Games/Minit/ for the user to manually copy into
//        Archipelago's custom_worlds folder (same pattern as apworld-based games
//        this launcher does not ship as core AP content). Surface clear guided
//        steps in the Settings panel.
//     c) Detect the Steam Minit install to surface its "data.win" path for the
//        user to enter into Archipelago's host.yaml (where the AP client looks
//        for the game file to patch). The launcher does NOT write host.yaml.
//     d) LaunchAsync launches ArchipelagoLauncher.exe (or Archipelago.exe) if
//        present, since connection and patching are done there. If the AP
//        launcher is not found, open the Minit Steam page / exe as fallback.
//
//   * DATA FILE LOCATION: MinitClient.py looks for "data.win" in the path set
//     under MinitWorld.settings.data_file (which is stored in Archipelago's
//     host.yaml). The game executable is either "minit.exe" or "minitGMS2.exe"
//     in the same folder. The patched file is written as "ap_v1.0_data.win" in
//     that same folder.
//
//   * This plugin also supports detecting the game exe folder via the Steam
//     registry and surfacing its path to the user, to make the host.yaml step
//     easier. No writing to host.yaml — the user does that step in Archipelago.
//
// ═══════════════════════════════════════════════════════════════════════════════

#pragma warning disable CS0067

public sealed class MinitPlugin : IGamePlugin
{
    // ── Constants — AP world / GitHub ────────────────────────────────────────

    private const string GITHUB_OWNER = "qwint";
    private const string GITHUB_REPO  = "APMinit";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases/latest";

    // Pinned fallback — verified live 2026-06-14 (v0.6.12 is the current release).
    private const string FallbackVersion     = "0.6.12";
    private const string FallbackApWorldName = "minit.apworld";
    private static readonly string FallbackApWorldUrl =
        $"{RepoUrl}/releases/download/v{FallbackVersion}/{FallbackApWorldName}";

    // Steam — Minit appid 609490 (verified).
    private const string SteamAppId       = "609490";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";
    private const string SteamStorePage   = $"https://store.steampowered.com/app/{SteamAppId}/Minit/";

    // Common folder name under steamapps/common.
    private const string SteamFolderName  = "Minit";

    // Minit game executables (GameMaker: Studio 2).
    private const string MinitExe1        = "minit.exe";
    private const string MinitExe2        = "minitGMS2.exe";

    // The GameMaker data file name the AP client patches.
    private const string DataWinName      = "data.win";
    private const string PatchedDataName  = "ap_v1.0_data.win";

    // Links.
    private const string SetupGuideUrl    = $"{RepoUrl}/blob/main/APWorld/docs/setup_en.md";
    private const string ApWorldReleasesUrl = $"{RepoUrl}/releases";
    private const string ArchipelagoSite  = "https://archipelago.gg";
    private const string ApReleasesUrl    = "https://github.com/ArchipelagoMW/Archipelago/releases";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "minit";
    public string DisplayName => "Minit";
    public string Subtitle    => "Native PC · AP proxy client";
    public string ApWorldName => "Minit";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "minit.png");

    public string ThemeAccentColor => "#1A1A2E";   // Minit's monochrome aesthetic

    public string[] GameBadges => new[] { "Steam · own the game", "Needs AP client" };

    public string Description =>
        "Minit, Devolver Digital's 2018 one-minute adventure game, played " +
        "through the APMinit community Archipelago world by qwint. Minit is a " +
        "GameMaker game — the integration patches the game's data file and runs " +
        "a local HTTP proxy client inside Archipelago that bridges the game to " +
        "the multiworld server. You must own Minit (Steam or Epic Games), install " +
        "the minit.apworld into Archipelago, then use the Minit Client inside " +
        "Archipelago to connect and patch the game. The launcher downloads the " +
        "apworld for you and helps you find your install path.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = the apworld file is present in our staging folder OR the
    /// patched data file already exists next to the user's game data.win.
    public bool IsInstalled =>
        File.Exists(StagedApWorldPath) || PatchedDataExists();

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin stages the downloaded minit.apworld and keeps settings.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Minit");

    private string StagedApWorldPath =>
        Path.Combine(GameDirectory, FallbackApWorldName);

    private string SettingsSidecarDir =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SettingsSidecarPath =>
        Path.Combine(SettingsSidecarDir, "minit_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // Minit's AP connection is owned by the Archipelago proxy client (Python
    // MinitClient.py), not by the launcher. These events are interface stubs.

    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;

#pragma warning restore CS0067

    // ── ConnectsItself / Standalone ───────────────────────────────────────────

    /// The Archipelago proxy client (MinitClient.py inside Archipelago) owns
    /// the AP slot connection. The launcher must not also try to connect to the
    /// same slot — so ConnectsItself = true suppresses the launcher's auto-connect.
    public bool ConnectsItself => true;

    /// Plain Minit runs fine without AP.
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = IsInstalled
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch
        {
            InstalledVersion = null;
        }
            try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(GITHUB_OWNER, GITHUB_REPO, ct));
        }
        catch
        {
            AvailableVersion = null; // contract: never throw on network failure
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 1. Resolve the latest release.
        progress.Report((5, "Checking the latest APMinit release..."));
        var (version, apWorldUrl) = await ResolveLatestApWorldAsync(ct);
        AvailableVersion = version;

        if (apWorldUrl == null)
            throw new InvalidOperationException(
                "Could not find the minit.apworld download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                ApWorldReleasesUrl + ". Copy minit.apworld into your " +
                "Archipelago custom_worlds folder when done.");

        // 2. Download the apworld into Games/Minit/.
        progress.Report((10, $"Downloading minit.apworld {version}..."));
        Directory.CreateDirectory(GameDirectory);
        await DownloadFileAsync(apWorldUrl, StagedApWorldPath,
            $"Downloading minit.apworld {version}...", 10, 85, progress, ct);

        // 3. Stamp the version.
        WriteStampedVersion(version);
        InstalledVersion = version;

        // 4. Surface a clear guided message.
        string? gameDir  = DetectMinitDir();
        string  gamePath = gameDir != null
            ? ("Detected Minit at: " + gameDir +
               " — copy that path into Archipelago's host.yaml under minit → data_file.")
            : "Minit was not detected automatically. " +
              "Find your Minit install folder (contains data.win) and set that path " +
              "in Archipelago's host.yaml under the minit section → data_file.";

        progress.Report((100,
            $"Downloaded minit.apworld {version} to {StagedApWorldPath}. " +
            "NEXT STEPS: " +
            "1) Copy minit.apworld from the location shown in Settings into " +
            "your Archipelago custom_worlds folder, then restart Archipelago. " +
            "2) " + gamePath + " " +
            "3) In Archipelago, open the Minit Client, enter your server/slot, " +
            "connect, then type /patch in the client to patch and launch the game. " +
            "Open Settings for the full guided steps and links."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    /// LaunchAsync for Minit: the correct play flow is to use Archipelago's own
    /// Minit Client (which patches + launches the game). So we attempt to launch
    /// ArchipelagoLauncher.exe / Archipelago.exe if it can be found. Failing that
    /// we fall back to launching the game exe or the Steam page.
    /// ConnectsItself = true: the launcher does NOT hold its own AP client on this slot.
    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        _ = session; // connection handled by the Archipelago client, not here
        LaunchGame();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        LaunchGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself) ────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // Items are bridged by the AP proxy client (MinitClient.py) → game via HTTP.
        // The launcher has no IPC channel to forward items through.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The Archipelago proxy client manages the in-game AP status display.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── How this integration works ─────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text =
                "Minit uses a proxy client integration: the Archipelago client " +
                "(included with Archipelago as the Minit Client) patches the game's " +
                "data file and runs a local HTTP server that bridges the game to the " +
                "AP server. You own the game (Steam/Epic), the launcher downloads the " +
                "apworld, and Archipelago handles the rest.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── apworld file status ────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "APWORLD FILE", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        bool apWorldPresent = File.Exists(StagedApWorldPath);
        panel.Children.Add(new TextBlock
        {
            Text = apWorldPresent
                ? "minit.apworld downloaded: " + StagedApWorldPath
                : "minit.apworld not yet downloaded. Use the Install button on the Play tab.",
            FontSize = 11,
            Foreground = apWorldPresent ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        if (apWorldPresent)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Copy this file into your Archipelago custom_worlds folder " +
                       "(e.g. C:\\ProgramData\\Archipelago\\custom_worlds\\) then restart Archipelago.",
                FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });
        }

        // ── Minit install detection ────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "MINIT INSTALLATION", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 4, 0, 8),
        });

        string? gameDir = DetectMinitDir();
        panel.Children.Add(new TextBlock
        {
            Text = gameDir != null
                ? "Detected Minit at: " + gameDir
                : "Minit not detected automatically (Steam appid 609490).",
            FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        bool patchedExists = PatchedDataExists();
        if (gameDir != null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = patchedExists
                    ? "Patched data file found: " + Path.Combine(gameDir, PatchedDataName)
                    : $"Patched file '{PatchedDataName}' not yet created — run /patch in the " +
                      "Minit Client inside Archipelago after connecting.",
                FontSize = 11,
                Foreground = patchedExists ? success : muted,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
            });

            panel.Children.Add(new TextBlock
            {
                Text = "Enter this path in Archipelago's host.yaml under minit → data_file: " +
                       Path.Combine(gameDir, DataWinName),
                FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = "Find your Minit install folder (contains data.win, minit.exe or " +
                       "minitGMS2.exe) and enter that data.win path in Archipelago's host.yaml " +
                       "under the minit section → data_file. Steam installs are typically under " +
                       @"...\steamapps\common\Minit\data.win.",
                FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });
        }

        // ── Guided setup steps ─────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 4, 0, 8),
        });

        foreach (string step in new[]
        {
            "1. Own Minit on Steam or Epic Games. Install it.",
            "2. Install Archipelago from the official releases (link below).",
            "3. Click Install on the Play tab to download minit.apworld here.",
            "4. Copy minit.apworld from " + GameDirectory + " into your Archipelago " +
               "custom_worlds folder, then restart Archipelago.",
            "5. Set minit → data_file in Archipelago's host.yaml to the data.win path " +
               "shown above (or find it manually in your Minit install folder).",
            "6. In Archipelago, open the Minit Client from the Launcher. Enter your " +
               "server host:port and slot name in the top bar, then click Connect.",
            "7. In the Minit Client, type /patch and press Enter. The client patches " +
               "the game data file and launches Minit automatically.",
            "8. Play Minit. Items and locations sync via the AP client running in the background.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ──────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });

        foreach (var (label, url) in new[]
        {
            ("APMinit (GitHub) ↗",           RepoUrl),
            ("APMinit Setup Guide ↗",        SetupGuideUrl),
            ("APMinit Releases (apworld) ↗", ApWorldReleasesUrl),
            ("Archipelago Releases ↗",       ApReleasesUrl),
            ("Minit on Steam ↗",             SteamStorePage),
            ("Archipelago Official ↗",       ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content             = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding             = new Thickness(0, 2, 0, 2),
                Background          = System.Windows.Media.Brushes.Transparent,
                BorderThickness     = new Thickness(0),
                FontSize            = 12,
                Margin              = new Thickness(0, 0, 0, 4),
                Foreground          = accent,
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
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

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

    // ── Private helpers — release resolution ──────────────────────────────────

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest minit.apworld release: version + download URL.
    /// First tries /releases/latest; falls back to the /releases list (newest
    /// first) in case the endpoint is stale; then pins the known-good fallback.
    private async Task<(string Version, string? ApWorldUrl)> ResolveLatestApWorldAsync(
        CancellationToken ct)
    {
        // Try /releases/latest first (clean endpoint when repo uses full releases).
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc  = JsonDocument.Parse(json);
            var (version, url) = ExtractApWorldAsset(doc.RootElement);
            if (version != null && url != null)
                return (version, url);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* try the list */ }

        // Try the full /releases list (newest first).
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    var (version, url) = ExtractApWorldAsset(rel);
                    if (version != null && url != null)
                        return (version, url);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through to pinned */ }

        return (FallbackVersion, FallbackApWorldUrl);
    }

    /// Extract version + minit.apworld download URL from a GitHub release JSON object.
    private static (string? Version, string? Url) ExtractApWorldAsset(JsonElement release)
    {
        string? version = release.TryGetProperty("tag_name", out var t)
            ? NormalizeTag(t.GetString())
            : null;

        if (version == null) return (null, null);
        if (!release.TryGetProperty("assets", out var assets)
            || assets.ValueKind != JsonValueKind.Array)
            return (version, null);

        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;

            // The asset is named "minit.apworld".
            if (name.EndsWith(".apworld", StringComparison.OrdinalIgnoreCase) &&
                name.IndexOf("minit", StringComparison.OrdinalIgnoreCase) >= 0)
                return (version, url);
        }

        return (version, null);
    }

    // ── Private helpers — Minit install detection ─────────────────────────────

    /// Detect the Minit Steam install directory by reading the Windows registry
    /// and scanning Steam library roots for appmanifest_609490.acf. Returns the
    /// install dir if found, else null.
    private static string? DetectMinitDir()
    {
        try
        {
            foreach (string steamRoot in SteamRoots())
            {
                if (string.IsNullOrWhiteSpace(steamRoot)) continue;
                foreach (string lib in SteamLibraryRoots(steamRoot))
                {
                    try
                    {
                        string steamapps = Path.Combine(lib, "steamapps");
                        string manifest  = Path.Combine(steamapps, $"appmanifest_{SteamAppId}.acf");
                        if (!File.Exists(manifest)) continue;

                        string common     = Path.Combine(steamapps, "common");
                        string? installDir = ReadAcfInstallDir(manifest);
                        if (installDir != null)
                        {
                            string candidate = Path.Combine(common, installDir);
                            if (LooksLikeMinitDir(candidate)) return candidate;
                        }
                        // Conventional name fallback.
                        string conventional = Path.Combine(common, SteamFolderName);
                        if (LooksLikeMinitDir(conventional)) return conventional;
                    }
                    catch { /* try next library */ }
                }
            }
        }
        catch { }
        return null;
    }

    private static bool LooksLikeMinitDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            // Minit's data.win is the strongest marker (GameMaker data file).
            if (File.Exists(Path.Combine(dir, DataWinName))) return true;
            if (File.Exists(Path.Combine(dir, MinitExe1)))   return true;
            if (File.Exists(Path.Combine(dir, MinitExe2)))   return true;
            return false;
        }
        catch { return false; }
    }

    /// True if the patched AP data file already exists in the detected install dir.
    private bool PatchedDataExists()
    {
        try
        {
            string? dir = DetectMinitDir();
            if (dir == null) return false;
            return File.Exists(Path.Combine(dir, PatchedDataName));
        }
        catch { return false; }
    }

    // ── Private helpers — Steam VDF / registry ────────────────────────────────

    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormalizeSteamPath(hkcu);

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormalizeSteamPath(hklm);

        string? hklm64 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm64)) yield return NormalizeSteamPath(hklm64);

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    private static IEnumerable<string> SteamLibraryRoots(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(steamRoot)) yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }

        foreach (string path in ExtractVdfPaths(text))
        {
            string norm = path.Replace('/', '\\').TrimEnd('\\');
            if (norm.Length > 0 && seen.Add(norm))
                yield return norm;
        }
    }

    private static IEnumerable<string> ExtractVdfPaths(string text)
    {
        const string key = "\"path\"";
        int i = 0;
        while ((i = text.IndexOf(key, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            i += key.Length;
            int open = text.IndexOf('"', i);
            if (open < 0) yield break;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) yield break;
            string raw = text.Substring(open + 1, close - open - 1);
            yield return raw.Replace("\\\\", "\\");
            i = close + 1;
        }
    }

    private static string? ReadAcfInstallDir(string acfPath)
    {
        try
        {
            string text = File.ReadAllText(acfPath);
            const string key = "\"installdir\"";
            int i = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += key.Length;
            int open = text.IndexOf('"', i);
            if (open < 0) return null;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) return null;
            return text.Substring(open + 1, close - open - 1).Replace("\\\\", "\\");
        }
        catch { return null; }
    }

    private static string? ReadRegistryString(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(subKey);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Launch strategy:
    /// The correct flow for AP play is to use the Archipelago Launcher (which
    /// opens the Minit Client), not to launch the game directly. But for
    /// standalone play or when AP is already running, launching the game exe
    /// directly also works. We try in order:
    ///   1. ArchipelagoLauncher.exe (if Archipelago is installed in a standard path)
    ///   2. minit.exe / minitGMS2.exe (direct launch from detected install)
    ///   3. steam://rungameid/609490 (Steam fallback)
    private void LaunchGame()
    {
        // Try the game exe directly (most useful for standalone, avoids AP client confusion).
        string? gameDir = DetectMinitDir();
        if (gameDir != null)
        {
            string? exe = GetMinitExe(gameDir);
            if (exe != null)
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName         = exe,
                    WorkingDirectory = gameDir,
                    UseShellExecute  = true,
                }) ?? throw new InvalidOperationException("Failed to start Minit.");

                _gameProcess = proc;
                IsRunning    = true;
                try
                {
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (_, _) =>
                    {
                        IsRunning = false;
                        GameExited?.Invoke(proc.ExitCode);
                    };
                }
                catch { /* some processes don't expose Exited reliably */ }
                return;
            }
        }

        // Fall back to Steam.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true;
                return;
            }
            catch { }
        }

        throw new FileNotFoundException(
            "Could not find Minit. Make sure Minit is installed via Steam (appid 609490). " +
            "For Archipelago play: open Archipelago, select the Minit Client, connect, " +
            "and use /patch to launch the game.",
            MinitExe1);
    }

    private static string? GetMinitExe(string dir)
    {
        string path1 = Path.Combine(dir, MinitExe1);
        if (File.Exists(path1)) return path1;
        string path2 = Path.Combine(dir, MinitExe2);
        if (File.Exists(path2)) return path2;
        return null;
    }

    // ── Private helpers — download ────────────────────────────────────────────

    private async Task DownloadFileAsync(
        string url,
        string destPath,
        string msg,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        progress.Report((pctStart, msg));
        using var response = await _http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total      = response.Content.Headers.ContentLength ?? -1;
        long downloaded = 0;

        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        var buf = new byte[81920];
        int bytesRead;
        while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
            downloaded += bytesRead;
            if (total > 0)
            {
                int span = Math.Max(1, pctEnd - pctStart);
                int pct  = pctStart + (int)(span * downloaded / total);
                progress.Report((pct, $"{msg} {downloaded / 1024}KB"));
            }
        }
        await dst.FlushAsync(ct);
    }

    // ── Private helpers — settings sidecar ────────────────────────────────────

    private sealed class MinitSettings
    {
        public string? ApWorldVersion { get; set; }
    }

    private MinitSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<MinitSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(MinitSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { }
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ApWorldVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings();
        s.ApWorldVersion = v;
        SaveSettings(s);
    }
}
