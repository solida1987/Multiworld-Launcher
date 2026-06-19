using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.Plugins.ChecksFinder;

// ═══════════════════════════════════════════════════════════════════════════════
// ChecksFinderPlugin — install / update / launch for "ChecksFinder", a free,
// standalone Minesweeper-style game whose downloadable build IS its own
// Archipelago client. This is a NATIVE "ConnectsItself" integration (NOT a
// BizHawk / Lua emulator game): the game speaks to the AP server itself, exactly
// like Celeste 64, Ship of Harkinian, the OpenTTD Archipelago fork, and APDOOM.
//
// Like Celeste 64, this is the CLEANEST kind of native plugin — there is NO
// bring-your-own-asset gate. ChecksFinder is free/freeware, so the download is a
// complete, self-contained game: install = a single GitHub-release download +
// launch. It is therefore modelled directly on Plugins/Celeste64/Celeste64Plugin
// .cs, with the AP.json connection-file machinery REMOVED — ChecksFinder has no
// config file or command line for the connection (see below); the player types
// the connection into the game's own "Play Online" screen.
//
// REALITY CHECK (2026-06-14) — facts verified this session
// ─────────────────────────────────────────────────────────────────────────────
//   * REPO (verified against the OFFICIAL Archipelago "ChecksFinder" setup guide
//       — https://archipelago.gg/tutorial/ChecksFinder/setup_en): the guide's
//       download step points players at
//         "the Github releases Page for the game (latest version)"  →
//         https://github.com/jonloveslegos/ChecksFinder/releases
//       and, alternatively, the itch.io page (https://suncat0.itch.io/checksfinder,
//       which also hosts a web build). The ORIGINAL repo is SunCatMC/ChecksFinder,
//       but the setup guide's "latest version" link — and the actively-updated
//       releases — are jonloveslegos/ChecksFinder, so that is the primary source
//       here, with SunCatMC pinned only as a last-ditch offline fallback.
//     Releases are NORMAL (non-prerelease) GitHub releases, so /releases/latest
//     works. Latest at time of writing: tag "v2.0.9" (2026-05-07). Windows asset
//     name, verified against that release's asset list:
//         "ChecksFinder_win.zip"
//     (the release also ships _linux/_mac/_android/_web zips — only the Windows
//     one is picked). v2.0.9 is pinned as the offline fallback so a fresh install
//     still works when the GitHub API is unreachable/rate-limited.
//
//   * HOW IT CONNECTS (VERIFIED against the official setup guide): the AP
//     connection is entered IN-GAME, not via a config file or command line. The
//     guide's verbatim steps are: "Start ChecksFinder and press `Play Online`",
//     then "Enter the following information: Server url, Server port, The name of
//     the slot you wish to connect to, The room password (optional)". There is NO
//     documented AP.json / settings file and NO command-line interface for the
//     connection — so, unlike the Celeste 64 plugin, this plugin does NOT (and
//     cannot reliably) prefill credentials; it simply launches the game and the
//     player types the four fields into the "Play Online" screen. AP servers
//     allow one connection per slot, so — like Celeste 64 / SoH / OpenTTD / APDOOM
//     — the launcher must NOT hold its own ApClient on the same slot while the
//     game runs: ConnectsItself = true.
//
//   * THE AP WORLD: game string "ChecksFinder" — verified verbatim against
//     AP-main worlds/checksfinder/__init__.py (the World subclass's
//     game = "ChecksFinder"). World id / folder "checksfinder". ChecksFinder is
//     core-verified in Archipelago (since 0.4.0), so the stable world ships WITH
//     Archipelago — there is no per-build .apworld to copy (the releases do not
//     ship one), which is why this plugin has no apworld-download step.
//
//   * NO ASSET GATE: ChecksFinder is free, so the build ships a COMPLETE, playable
//     game. There is no ROM/IWAD to supply, no first-run extractor, no asset-
//     library copy. Plain non-AP play: the game is a self-contained Minesweeper-
//     style game, but the guide notes "ChecksFinder currently only works when
//     integrated with archipelago.gg" — there is no documented offline/practice
//     mode that does anything without an AP room, so SupportsStandalone is left
//     FALSE (honest: launching it does nothing useful without a room to join).
//
// DEFENSIVE / UNVERIFIED DETAILS (flagged, "verify at build time"):
//   * The exact zip CONTENTS / exe name were NOT inspected offline (the itch page
//     reports the game is made with Godot; the exe name is undocumented).
//     ResolveGameExe() therefore prefers "ChecksFinder.exe", then any
//     "*checksfinder*"/"*checks*" exe in the install (fuzzy), skipping obvious
//     helper/uninstaller/crash-handler exes. Single-subdir extracts are flattened
//     so the exe lands at the install root.
//   * Because connection is in-game only, there is NOTHING to prefill and nothing
//     sensitive (no password) written to disk by this plugin — StopAsync still
//     defensively scrubs any stray credential file this plugin might ever write
//     (currently none), to match the Celeste 64 contract.
//   * One launcher-side setting (the install dir is the only one that matters) is
//     enough; this plugin keeps any optional setting in its OWN JSON sidecar
//     (Games/ROMs/checksfinder/checksfinder_launcher.json) rather than modifying
//     the shared Core/SettingsStore — it is added as a single self-contained file.
//     (The Celeste 64 / Doom plugins took the same self-contained-sidecar approach.)
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ChecksFinderPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    // Primary source: the repo the OFFICIAL AP setup guide links as "latest".
    private const string GITHUB_OWNER = "jonloveslegos";
    private const string GITHUB_REPO  = "ChecksFinder";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"{GH_RELEASES_URL}/latest";

    // Original repo + itch page — surfaced as informational links only.
    private const string OriginalRepoUrl = "https://github.com/SunCatMC/ChecksFinder";
    private const string ItchUrl         = "https://suncat0.itch.io/checksfinder";

    /// Official Archipelago "ChecksFinder" setup guide.
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/ChecksFinder/setup_en";

    // Pinned fallback — the latest non-prerelease at time of writing, with the
    // verified asset name. Used ONLY when the GitHub API is unreachable so a fresh
    // install still works offline-of-the-API.
    private const string FallbackVersion = "2.0.9";
    private const string FallbackZipName = "ChecksFinder_win.zip";
    private static readonly string FallbackZipUrl =
        $"{RepoUrl}/releases/download/v{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp, written after a successful install.
    private const string VersionFileName = "checksfinder_ap_version.dat";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "checksfinder";
    public string DisplayName => "ChecksFinder";
    public string Subtitle    => "Native PC · built-in Archipelago";

    /// EXACT AP game string — verified against worlds/checksfinder/__init__.py
    /// (the World subclass's game = "ChecksFinder").
    public string ApWorldName => "ChecksFinder";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "checksfinder.png");

    public string ThemeAccentColor => "#4A90D9";   // Minesweeper blue
    public string[] GameBadges     => new[] { "Free" };

    public string Description =>
        "ChecksFinder is a free, Minesweeper-style game built from the ground up " +
        "as its own Archipelago client. You avoid mines and collect your checks by " +
        "clearing boards, unlocking more of the grid as items arrive, and you win " +
        "when you have all your items and beat the final board. Because the game " +
        "speaks to the Archipelago server itself — no emulator, no Lua bridge — the " +
        "download is the complete, self-contained client: nothing to bring, nothing " +
        "to extract. The launcher installs the latest Windows build and launches " +
        "you straight to its Play Online screen, where you enter your room details.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => ResolveGameExe() != null;
    public bool    IsRunning        { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the ChecksFinder build is installed.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "ChecksFinder");

    /// Preferred exe (best-effort name). Resolution falls back to a fuzzy match.
    private string PreferredExePath => Path.Combine(GameDirectory, "ChecksFinder.exe");

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained source file). Lives under the
    /// launcher's ROM-library tree for consistency with the other native plugins,
    /// even though this game brings no ROM.
    private string SettingsSidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                        "checksfinder_launcher.json");

    private string SettingsSidecarDir => Path.GetDirectoryName(SettingsSidecarPath)!;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // ChecksFinder's native AP client reports checks/items/goal to the AP server
    // itself — the launcher relays nothing. These exist for interface
    // compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = File.Exists(VersionFilePath) && IsInstalled
                ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim()
                : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        try
        {
            var (version, _) = await ResolveLatestReleaseAsync(ct);
            AvailableVersion = version;
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
        // 1. Resolve the latest release (pinned fallback when offline).
        progress.Report((2, "Checking latest ChecksFinder release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        // 2. Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"ChecksFinder {version} is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a Windows download for ChecksFinder on the GitHub " +
                "release page. Check your internet connection, or download the " +
                "build manually from " + RepoUrl + "/releases.");

        // 3. Download + extract the build.
        await DownloadAndExtractGameAsync(zipUrl, version, progress, ct);

        // 4. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"ChecksFinder {version} ready. Press Play, then choose \"Play Online\" " +
            "and enter your server URL, port, slot name and (optional) password."));
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
        string exe = ResolveGameExe()
            ?? throw new FileNotFoundException(
                "ChecksFinder is not installed. Click Install Game first.",
                PreferredExePath);

        // ChecksFinder takes its AP connection from the in-game "Play Online"
        // screen (server URL / port / slot / optional password) — there is no
        // documented config file or command line to prefill, so we simply launch
        // the game and let the player enter the four fields. (session is intact
        // here for symmetry with other plugins and in case a future build adds a
        // documented prefill mechanism.)
        StartGameProcess(exe);
        return Task.CompletedTask;
    }

    /// ChecksFinder "currently only works when integrated with archipelago.gg"
    /// (per the official guide) — there is no documented standalone/practice mode
    /// that does anything without an AP room, so non-AP play is not offered.
    public bool SupportsStandalone => false;

    /// ChecksFinder's native in-game AP client owns the slot connection (see
    /// header). The launcher must not connect its own ApClient to the same slot
    /// while the game runs.
    public bool ConnectsItself => true;

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // This plugin writes no plaintext credential file (connection is in-game
        // only), but scrub defensively to match the native-plugin contract in
        // case a future build leaves one behind.
        ScrubAnyCredentialFile();
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // ChecksFinder's native client receives items from the AP server directly;
        // there is nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // ChecksFinder renders its own AP status in-game; no launcher HUD channel.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Section: Install directory ────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "INSTALL DIRECTORY", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = GameDirectory, IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var dirBtn = new Button
        {
            Content = "Browse...", Width = 90, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select ChecksFinder install folder",
                InitialDirectory = Directory.Exists(GameDirectory) ? GameDirectory
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
            }
        };
        DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new TextBlock
        {
            Text       = IsInstalled ? "✓ ChecksFinder is installed"
                                     : "Not installed (click Install in the Play tab)",
            FontSize   = 11, Foreground = IsInstalled ? success : muted,
            Margin     = new Thickness(0, 6, 0, 12),
        });

        // ── Section: How it connects ──────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "ARCHIPELAGO CONNECTION", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "ChecksFinder connects from inside the game. Press Play, choose " +
                   "\"Play Online\" on the title screen, then enter your server URL, " +
                   "server port, slot name, and the room password (optional). The " +
                   "launcher does not need to fill anything in — the game has its own " +
                   "built-in Archipelago client and only works when connected to an " +
                   "Archipelago room (archipelago.gg).",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(new TextBlock
        {
            Text = "This game is free — there is nothing to bring. The download is the " +
                   "complete client, so install is just a download. (Status: built " +
                   "from the official setup guide and the GitHub release; the exact " +
                   "in-game exe name and the zip contents are resolved at install " +
                   "time and have not been verified offline.)",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("ChecksFinder (GitHub releases) ↗", RepoUrl + "/releases"),
            ("ChecksFinder Setup Guide ↗",       SetupGuideUrl),
            ("ChecksFinder on itch.io ↗",        ItchUrl),
            ("Original repo (SunCatMC) ↗",       OriginalRepoUrl),
            ("Archipelago Official ↗",           "https://archipelago.gg"),
        })
        {
            var btn = new Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => { try { System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
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

    // ── Private helpers — release resolution ──────────────────────────────────

    /// "v2.0.9" → "2.0.9" when a leading 'v' decorates a digit; otherwise the tag
    /// is returned as-is (trimmed). Returns null for null/blank tags.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest release: version + Windows zip asset URL. ChecksFinder
    /// publishes normal (non-prerelease) releases, so /releases/latest is the
    /// right endpoint. Falls back to the pinned v2.0.9 direct URL when the API is
    /// unreachable.
    private async Task<(string Version, string? ZipUrl)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? zip = PickWindowsZip(assets);
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback below */ }

        // Offline fallback: v2.0.9, known asset URL.
        return (FallbackVersion, FallbackZipUrl);
    }

    /// From a release's assets array, pick the Windows .zip. ChecksFinder names
    /// its Windows asset "ChecksFinder_win.zip"; match broadly on win/x64,
    /// explicitly excluding the linux/mac/android/web/source variants the release
    /// also ships.
    private static string? PickWindowsZip(JsonElement assets)
    {
        string? zip = null;
        string? anyZip = null;

        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();
            if (!lower.EndsWith(".zip")) continue;

            // Skip the non-Windows platform builds shipped alongside.
            if (lower.Contains("source")  ||
                lower.Contains("linux")   ||
                lower.Contains("ubuntu")  ||
                lower.Contains("mac")     ||
                lower.Contains("osx")     ||
                lower.Contains("darwin")  ||
                lower.Contains("android") ||
                lower.Contains("web")     ||
                lower.Contains("html"))
                continue;

            anyZip ??= url;   // remember any plausible non-excluded game zip
            if (zip == null &&
                (lower.Contains("win") || lower.Contains("x64") || lower.Contains("x86_64")))
                zip = url;
        }

        // If nothing matched the Windows heuristics but a single non-platform game
        // zip exists (e.g. a future release named just "ChecksFinder.zip"), use it.
        return zip ?? anyZip;
    }

    // ── Private helpers — exe resolution ──────────────────────────────────────

    /// Resolve the installed exe: prefer "ChecksFinder.exe", then any
    /// "*checksfinder*"/"*checks*" exe in the install (fuzzy), skipping
    /// helper/uninstaller/crash-handler exes. Defensive — the exact zip contents
    /// were not inspected offline.
    private string? ResolveGameExe()
    {
        if (File.Exists(PreferredExePath)) return PreferredExePath;
        if (!Directory.Exists(GameDirectory)) return null;
        try
        {
            string? firstExe = null;
            foreach (string exe in Directory.EnumerateFiles(GameDirectory, "*.exe", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (name.Contains("unins") || name.Contains("setup") || name.Contains("crash"))
                    continue;
                firstExe ??= exe;               // remember any plausible exe
                if (name.Contains("check"))     // matches "checksfinder", "checks"
                    return exe;
            }
            // No name match but a lone plausible exe exists — Godot builds usually
            // ship exactly one. Use it as a last resort.
            return firstExe;
        }
        catch { /* directory vanished mid-scan */ }
        return null;
    }

    // ── Private helpers — credential scrub (defensive) ────────────────────────

    /// ChecksFinder takes its connection in-game, so this plugin writes no
    /// credential file. This is a defensive no-op-by-default hook: if a future
    /// build ever drops a plaintext connection/password file into the install,
    /// blank it once the session ends so it does not outlive the game. Best
    /// effort; never throws.
    private void ScrubAnyCredentialFile()
    {
        // No documented credential file exists today — intentionally nothing to
        // scrub. Kept as the contractual counterpart to Celeste 64's
        // ScrubApConfigPassword so the surface is consistent across native plugins.
    }

    // ── Private helpers — process ─────────────────────────────────────────────

    private void StartGameProcess(string exePath)
    {
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = GameDirectory,
            UseShellExecute  = false,
        }) ?? throw new InvalidOperationException("Failed to start ChecksFinder.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            ScrubAnyCredentialFile();   // session over (defensive — see header)
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // Kept in this plugin's OWN JSON file so it stays a single self-contained
    // source file and does not modify Core/SettingsStore. Reserved for any future
    // launcher-side toggle; the install dir is the only setting that matters today,
    // so the sidecar is currently unused beyond this scaffolding.

    private sealed class ChecksFinderSettings
    {
        // Intentionally empty for now — a placeholder for future launcher-side
        // options without touching the shared store.
    }

    private ChecksFinderSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<ChecksFinderSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(ChecksFinderSettings s)
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

    // ── Private helpers — download/extract ────────────────────────────────────

    private async Task DownloadAndExtractGameAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"checksfinder-ap-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading ChecksFinder {version}..."));
            using var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempZip))
            {
                var buf = new byte[81920];
                int bytesRead;
                while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                    downloaded += bytesRead;
                    if (total > 0)
                    {
                        int pct = (int)(5 + 75 * downloaded / total);
                        progress.Report((pct, $"Downloading ChecksFinder... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((85, "Extracting..."));
            Directory.CreateDirectory(GameDirectory);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, GameDirectory, overwriteFiles: true);

            // Release zips often contain a single top-level sub-folder — flatten
            // it so the exe lands directly in GameDirectory. (ResolveGameExe scans
            // subdirectories too, so only flatten when the extract is a lone
            // wrapper folder with nothing at the root.)
            if (Directory.GetFiles(GameDirectory).Length == 0)
            {
                string[] subdirs = Directory.GetDirectories(GameDirectory);
                if (subdirs.Length == 1)
                {
                    string sub = subdirs[0];
                    foreach (string fileSrc in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
                    {
                        string rel     = Path.GetRelativePath(sub, fileSrc);
                        string fileDst = Path.Combine(GameDirectory, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                        File.Move(fileSrc, fileDst, overwrite: true);
                    }
                    Directory.Delete(sub, recursive: true);
                }
            }

            progress.Report((95, "Game files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }
}
