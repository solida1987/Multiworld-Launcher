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

namespace LauncherV2.Plugins.OpenTTD;

// ═══════════════════════════════════════════════════════════════════════════════
// OpenTTDPlugin — install / update / launch for "OpenTTD Archipelago".
//
// REALITY CHECK (2026-06-10) — full details with sources in
// Research_V2/OPENTTD_AP_REALITY_2026-06-10.md
// ─────────────────────────────────────────────────────────────────────────────
// The integration is NOT a Game Script and NOT vanilla OpenTTD:
//
//   * OpenTTD Game Scripts (Squirrel, GS API) are sandboxed: the API has no
//     socket, HTTP, or file-IO classes (https://docs.openttd.org/gs-api/).
//     The only outbound channels are GSLog and GSAdmin — and GSAdmin only
//     reaches admin-port clients of a *server* instance (TCP 3977,
//     [network] server_admin_port). A GS can never open a TCP/pipe
//     connection to this launcher. The earlier design based on that idea
//     was discarded.
//
//   * The real, released integration is a patched fork with a NATIVE in-game
//     AP client: https://github.com/solida1987/openttd-archipelago
//     ("OpenTTD 15.2 with Archipelago multiworld randomizer integration",
//     latest release v1.4.1, 2026-03-19). The Windows release zip is fully
//     standalone — OpenGFX/OpenSFX/OpenMSX are bundled, no vanilla OpenTTD
//     install is needed. The game connects to the AP server itself (TLS
//     WebSocket client in src/archipelago.cpp); players connect via the
//     "Archipelago" button in the game's main menu.
//
// WHAT THIS PLUGIN DOES TODAY (V2.0)
// ──────────────────────────────────
//   1. Installs/updates the fork from its GitHub releases (latest tag, with
//      a pinned fallback when the API is unreachable).
//   2. Downloads the release's openttd.apworld next to the install so the
//      user can drop it into Archipelago's custom_worlds folder.
//   3. On AP launch, pre-fills <GameDirectory>\data\ap_connection.cfg with
//      the session's server/slot/password. Verified fork behavior: the
//      in-game Archipelago dialog loads that file (key=value: host, port,
//      slot, pass, ssl) every time it opens, and the fork pins its personal
//      dir to <exe>\data\ (self-contained build — never Documents\OpenTTD).
//   4. Launches openttd.exe. Plain non-AP play works identically
//      (SupportsStandalone = true).
//
// WHAT IT DOES NOT DO YET — V2.1 PLAN
// ───────────────────────────────────
//   The fork's native client talks to the AP server directly, so this plugin
//   performs NO item/check relaying: LocationsChecked / GoalCompleted /
//   ReceiveItemsAsync are intentionally inert. The launcher must also avoid
//   double-connecting its own ApClient to the same slot while the game runs.
//   V2.1 plan (we own the fork, so the channel is added fork-side):
//     a) "autoconnect=1" key in ap_connection.cfg → zero-click connect.
//     b) Fork writes data\ap_status.json on AP state changes → launcher
//        tails it for live status / goal UI (no sockets, no AV surface).
//   Estimated 2–3 days combined; see the research note for the breakdown.
//
// GITHUB RELEASE FORMAT (fork)
//   Tag "v1.4.1" → assets:
//     openttd-archipelago-v1.4.1-win64.zip   (standalone game, ~70 MB)
//     openttd.apworld                        (for Archipelago custom_worlds)
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class OpenTTDPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER = "solida1987";
    private const string GITHUB_REPO  = "openttd-archipelago";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"{GH_RELEASES_URL}/latest";

    /// Pinned fallback release used when the GitHub API is unreachable.
    /// Verified live on 2026-06-10.
    private const string Fork_FallbackVersion = "1.4.1";
    private const string ApWorldAssetName     = "openttd.apworld";

    private static string ForkZipName(string version) => $"openttd-archipelago-v{version}-win64.zip";
    private static string ForkZipUrl(string version)
        => $"{RepoUrl}/releases/download/v{version}/{ForkZipName(version)}";
    private static string ForkApWorldUrl(string version)
        => $"{RepoUrl}/releases/download/v{version}/{ApWorldAssetName}";

    /// Vanilla OpenTTD reference — NOT used for install (the fork bundle is
    /// self-contained and vanilla has no AP support). Kept as a verified
    /// reference: current stable and the CDN's real filename pattern, checked
    /// 2026-06-10. Note the pattern is "windows-win64" — the previously used
    /// "windows-x64" has never existed on the CDN (returns 404).
    private const string OpenTTD_Version  = "15.3";
    private const string OpenTTD_ZipName  = $"openttd-{OpenTTD_Version}-windows-win64.zip";
    private const string OpenTTD_DownloadUrl
        = $"https://cdn.openttd.org/openttd-releases/{OpenTTD_Version}/{OpenTTD_ZipName}";

    /// Installed fork version stamp, written after a successful install.
    private const string VersionFileName = "openttd_ap_version.dat";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30), // 70 MB download on slow lines
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId         => "openttd_archipelago";
    public string DisplayName    => "OpenTTD Archipelago";
    public string Subtitle       => "Transport Simulator";

    /// AP game name as registered by the RELEASED v1.4.1 apworld (verified by
    /// inspecting the openttd.apworld asset: game = "OpenTTD"). Watch out: the
    /// fork's main branch carries an unreleased rename to "OpenTTD-Exp" —
    /// re-verify this on every fork release bump.
    public string ApWorldName    => "OpenTTD";

    public string IconPath       => Path.Combine(AppContext.BaseDirectory, "Assets", "openttd_archipelago.png");
    public string ThemeAccentColor => "#0A4A8E";   // transport-blue
    public string[] GameBadges     => Array.Empty<string>();

    public string Description =>
        "OpenTTD is a free, open-source transport simulation game. " +
        "Build rail, road, air, and sea networks to connect cities and industries. " +
        "OpenTTD Archipelago is a custom build of the game with a built-in Archipelago " +
        "client — vehicles, infrastructure, missions, and more are randomized into the " +
        "multiworld. Connect from the Archipelago button in the game's main menu. " +
        "Deeper launcher integration (auto-connect and live status) is in development.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => File.Exists(GameExePath);
    public bool    IsRunning        { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the OpenTTD Archipelago build is installed.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "OpenTTD");

    private string GameExePath
        => Path.Combine(GameDirectory, "openttd.exe");

    /// The fork's personal dir — pinned to <exe>\data\ by the self-contained
    /// build (src/os/windows/win32.cpp). Saves, openttd.cfg, and
    /// ap_connection.cfg all live here.
    private string DataDirectory
        => Path.Combine(GameDirectory, "data");

    /// Connection prefill file read by the in-game Archipelago dialog.
    private string ApConnectionConfigPath
        => Path.Combine(DataDirectory, "ap_connection.cfg");

    /// Where the release's openttd.apworld is saved for the user to copy into
    /// Archipelago's custom_worlds folder.
    private string ApWorldLocalPath
        => Path.Combine(GameDirectory, ApWorldAssetName);

    private string VersionFilePath
        => Path.Combine(GameDirectory, VersionFileName);

    // ── Internal state ────────────────────────────────────────────────────────

    private Process?               _gameProcess;
    private CancellationTokenSource? _statusPollCts;
    private readonly HashSet<long> _reportedLocations = new();

    // ── AP bridge events ──────────────────────────────────────────────────────
    // V2.0: the fork's native AP client owns the slot; the launcher relays nothing.
    // V2.1+: once the fork writes data\ap_status.json, PollApStatusAsync fires
    // these to update the launcher's location tracker and goal UI.
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;

    public event Action<int>? GameExited;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Local side: version stamp is only meaningful while the exe exists.
        try
        {
            InstalledVersion = File.Exists(VersionFilePath) && File.Exists(GameExePath)
                ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim()
                : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        // Remote side: latest fork release tag ("v1.4.1" → "1.4.1").
        // CDN HEAD redirect — no REST API quota consumed.
        try
        {
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
        // 1. Resolve the latest fork release (pinned fallback when offline).
        progress.Report((2, "Checking latest OpenTTD Archipelago release..."));
        var (version, zipUrl, apworldUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        // 2. Already current? (idempotent fast path)
        if (File.Exists(GameExePath)
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"OpenTTD Archipelago {version} is up to date."));
            return;
        }

        // 3. Download + extract the standalone win64 build.
        await DownloadAndExtractGameAsync(zipUrl, version, progress, ct);

        // 4. Fetch the apworld next to the install (best effort — the game
        //    itself works without it; generation needs it in custom_worlds).
        if (apworldUrl != null)
        {
            try
            {
                progress.Report((85, $"Downloading {ApWorldAssetName}..."));
                byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                await File.WriteAllBytesAsync(ApWorldLocalPath, apworld, ct);
                progress.Report((92, $"{ApWorldAssetName} saved — copy it into Archipelago's custom_worlds folder."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((92, $"Could not download {ApWorldAssetName} — get it from the GitHub release page."));
            }
        }

        // 5. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100, $"OpenTTD Archipelago {version} ready."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return File.Exists(GameExePath);
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        if (!File.Exists(GameExePath))
            throw new FileNotFoundException(
                "OpenTTD Archipelago is not installed. Click Install Game first.",
                GameExePath);

        // Pre-fill the in-game Archipelago dialog. Verified fork behavior:
        // the dialog reads <exe>\data\ap_connection.cfg (host/port/slot/pass/
        // ssl, key=value) every time it opens, so the player only has to click
        // Connect. Best effort — the game runs fine without the prefill.
        try { WriteApConnectionConfig(session); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        StartGameProcess();
        return Task.CompletedTask;
    }

    /// The fork is a complete game — plain (non-AP) play is fully supported.
    public bool SupportsStandalone => true;

    /// The fork's NATIVE in-game AP client owns the slot connection (see the
    /// header). The launcher must not connect its own ApClient to the same
    /// slot while the game runs — the server would kick one of the two and
    /// auto-reconnect would turn that into a kick-war.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        if (!File.Exists(GameExePath))
            throw new FileNotFoundException(
                "OpenTTD Archipelago is not installed. Click Install Game first.",
                GameExePath);

        // No connection prefill — the player simply doesn't open the
        // Archipelago window (or connects manually later).
        StartGameProcess();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _statusPollCts?.Cancel();
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        _reportedLocations.Clear();
        ScrubApConnectionPassword();   // plaintext credentials die with the session (P3-20)
        return Task.CompletedTask;
    }

    /// §12 fullscreen toggle. OpenTTD has NO Windows command-line fullscreen
    /// switch (verified against the fork's src/openttd.cpp: '-f' is Unix-only
    /// dedicated-server forking and is not even registered on _WIN32). The
    /// real launch-time control is the "fullscreen" bool in the [misc]
    /// section of openttd.cfg (src/table/settings/misc_settings.ini, var
    /// _fullscreen) — and the fork pins its personal dir to the launcher-owned
    /// <GameDirectory>\data\, so writing it there is safe (§11: this is OUR
    /// install copy, never the user's own files). Best effort: a cfg write
    /// failure must never block a launch.
    private void ApplyFullscreenSetting()
    {
        try
        {
            bool   want = SettingsStore.Load().OpenTtdFullscreen;
            string cfg  = Path.Combine(DataDirectory, "openttd.cfg");
            string line = $"fullscreen = {(want ? "true" : "false")}";

            if (!File.Exists(cfg))
            {
                // No cfg yet (first launch) — windowed is OpenTTD's default,
                // so only seed a minimal cfg when fullscreen is wanted.
                if (!want) return;
                Directory.CreateDirectory(DataDirectory);
                File.WriteAllText(cfg, "[misc]\n" + line + "\n", new UTF8Encoding(false));
                return;
            }

            var  lines     = new List<string>(File.ReadAllLines(cfg));
            bool inMisc    = false;
            int  miscStart = -1;
            for (int i = 0; i < lines.Count; i++)
            {
                string t = lines[i].Trim();
                if (t.StartsWith('['))
                {
                    inMisc = t.Equals("[misc]", StringComparison.OrdinalIgnoreCase);
                    if (inMisc) miscStart = i;
                    continue;
                }
                int eq = t.IndexOf('=');
                if (inMisc && eq > 0 &&
                    t[..eq].Trim().Equals("fullscreen", StringComparison.OrdinalIgnoreCase))
                {
                    if (t.Equals(line, StringComparison.OrdinalIgnoreCase)) return;  // already right
                    lines[i] = line;
                    File.WriteAllText(cfg, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
                    return;
                }
            }

            // No fullscreen key yet — insert under [misc] (or append the section).
            if (miscStart >= 0) lines.Insert(miscStart + 1, line);
            else { lines.Add("[misc]"); lines.Add(line); }
            File.WriteAllText(cfg, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        }
        catch { /* cosmetic launch option — never block the launch */ }
    }

    /// Blank the pass= line in ap_connection.cfg once the session ends
    /// (P3-20) — the plaintext room password should not outlive the session
    /// on disk. Safe timing: every AP launch rewrites the file first, and the
    /// in-game dialog re-reads it on open (host/slot prefill survives).
    private void ScrubApConnectionPassword()
    {
        try
        {
            if (!File.Exists(ApConnectionConfigPath)) return;
            var lines = File.ReadAllLines(ApConnectionConfigPath);
            bool changed = false;
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].StartsWith("pass=", StringComparison.OrdinalIgnoreCase) &&
                    lines[i].Length > 5)
                {
                    lines[i] = "pass=";
                    changed  = true;
                }
            }
            if (changed)
                File.WriteAllText(ApConnectionConfigPath,
                    string.Join('\n', lines) + "\n", new UTF8Encoding(false));
        }
        catch { /* best effort — the next AP launch overwrites the file anyway */ }
    }

    // ── AP bridge — inert in V2.0 (see header) ───────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The fork's native client receives items from the AP server directly;
        // there is nothing to forward. Becomes meaningful only if a fork-side
        // launcher channel is added (V2.1 plan).
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // No in-game HUD channel exists yet — the fork renders its own AP
        // status overlay (top-right, archipelago_gui.cpp). Intentionally empty.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted  = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg     = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success= new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var panel  = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

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
            // Real folder picker (P3-16) — .NET 8 WPF ships OpenFolderDialog,
            // replacing the old "OpenFileDialog as folder picker" hack.
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select OpenTTD install folder",
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
            Text       = IsInstalled ? "✓ OpenTTD Archipelago is installed" : "Not installed (click Install in the Play tab)",
            FontSize   = 11, Foreground = IsInstalled ? success : muted,
            Margin     = new Thickness(0, 6, 0, IsInstalled && File.Exists(ApWorldLocalPath) ? 6 : 20),
        });

        if (IsInstalled && File.Exists(ApWorldLocalPath))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"{ApWorldAssetName} is saved in the install folder — copy it into your " +
                       @"Archipelago custom_worlds folder (default: C:\ProgramData\Archipelago\custom_worlds) " +
                       "to generate multiworlds.",
                FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 20),
            });
        }

        // ── Launch options (§12) ──────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LAUNCH OPTIONS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });
        var chkFullscreen = new CheckBox
        {
            Content    = "Fullscreen",
            IsChecked  = SettingsStore.Load().OpenTtdFullscreen,
            Foreground = fg,
            Margin     = new Thickness(0, 0, 0, 4),
            ToolTip    = "Start OpenTTD fullscreen (written to the install's own " +
                         "openttd.cfg before each launch).",
        };
        chkFullscreen.Checked   += (_, _) => { var s = SettingsStore.Load(); s.OpenTtdFullscreen = true;  SettingsStore.Save(s); };
        chkFullscreen.Unchecked += (_, _) => { var s = SettingsStore.Load(); s.OpenTtdFullscreen = false; SettingsStore.Save(s); };
        panel.Children.Add(chkFullscreen);
        panel.Children.Add(new TextBlock
        {
            Text = "Applied at launch — toggling it mid-game does nothing until the next start. " +
                   "Alt+Enter still works in-game as usual.",
            FontSize = 10, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 20),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("OpenTTD Official Site ↗",        "https://www.openttd.org"),
            ("OpenTTD Archipelago (GitHub) ↗", RepoUrl),
            ("Archipelago Official ↗",         "https://archipelago.gg"),
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
            // Releases of the real fork repo (verified live, v1.4.1+).
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

    // ── Private helpers ───────────────────────────────────────────────────────

    /// "v1.4.1" → "1.4.1". Returns null for null/blank tags.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        return tag.StartsWith('v') ? tag[1..] : tag;
    }

    /// Resolve the latest fork release: version + win64 zip asset URL +
    /// apworld asset URL. Falls back to the pinned release when offline.
    private async Task<(string Version, string ZipUrl, string? ApWorldUrl)>
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
                string? zip = null, apworld = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    if (name.EndsWith("-win64.zip", StringComparison.OrdinalIgnoreCase))
                        zip = url;
                    else if (name.Equals(ApWorldAssetName, StringComparison.OrdinalIgnoreCase))
                        apworld = url;
                }
                if (zip != null) return (version, zip, apworld);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback below */ }

        return (Fork_FallbackVersion,
                ForkZipUrl(Fork_FallbackVersion),
                ForkApWorldUrl(Fork_FallbackVersion));
    }

    /// Write the connection prefill file the fork's Archipelago dialog reads.
    /// Format verified from archipelago_manager.cpp (AP_LoadConnectionConfig):
    /// plain key=value lines; "ssl" is parsed but currently unused (the fork
    /// auto-detects TLS). Must be BOM-less — the game parses it with fgets().
    /// "autoconnect=1" is a V2.1 extension the fork will honour when added —
    /// current v1.4.1 silently ignores unknown keys, so it is safe to include.
    private void WriteApConnectionConfig(ApSession session)
    {
        var (host, port, ssl) = ParseServerUri(session.ServerUri);

        Directory.CreateDirectory(DataDirectory);
        var sb = new StringBuilder();
        sb.Append("host=").Append(host).Append('\n');
        sb.Append("port=").Append(port).Append('\n');
        sb.Append("slot=").Append(session.SlotName).Append('\n');
        sb.Append("pass=").Append(session.Password).Append('\n');
        sb.Append("ssl=").Append(ssl ? '1' : '0').Append('\n');
        sb.Append("autoconnect=1").Append('\n');
        File.WriteAllText(ApConnectionConfigPath, sb.ToString(), new UTF8Encoding(false));
    }

    /// Accepts "archipelago.gg:38281", "ws://host:port", "wss://host:port",
    /// a bare hostname, and IPv6 literals — bracketed "[::1]:38281" or bare
    /// "::1" (a blind LastIndexOf(':') used to split bare IPv6 mid-address,
    /// P3-19). Default AP port is 38281.
    private static (string Host, int Port, bool Ssl) ParseServerUri(string serverUri)
    {
        string s = serverUri.Trim();
        bool ssl = s.StartsWith("wss://", StringComparison.OrdinalIgnoreCase);

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
            // Bracketed IPv6: [::1] or [::1]:38281
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
            // Bare IPv6 literal (multiple colons, no brackets) — the whole
            // string is the host; there is no way to carry a port this way.
            host = s;
        }
        else
        {
            int colon = s.LastIndexOf(':');
            if (colon > 0 && int.TryParse(s[(colon + 1)..], out int p) && p > 0 && p <= 65535)
            {
                host = s[..colon];
                port = p;
            }
        }

        if (host.Length == 0) host = "archipelago.gg";
        return (host, port, ssl);
    }

    private void StartGameProcess()
    {
        // §12: apply the launcher-side fullscreen toggle before every launch
        // (AP and standalone both come through here).
        ApplyFullscreenSetting();

        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = GameExePath,
            WorkingDirectory = GameDirectory,
            UseShellExecute  = false,
        }) ?? throw new InvalidOperationException("Failed to start OpenTTD.");

        _gameProcess = proc;
        IsRunning    = true;

        // Start the ap_status.json poller (V2.1 channel — no-op until the fork
        // starts writing the file; the loop exits silently when the game exits).
        _statusPollCts?.Cancel();
        _statusPollCts = new CancellationTokenSource();
        _ = PollApStatusAsync(_statusPollCts.Token);

        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            _statusPollCts?.Cancel();
            _reportedLocations.Clear();
            ScrubApConnectionPassword();   // session over — blank pass= (P3-20)
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    /// Poll data\ap_status.json every 2 seconds while the game is running.
    ///
    /// Expected JSON the fork writes (V2.1 contract — currently v1.4.1 doesn't
    /// write this file; the loop exits harmlessly if the file never appears):
    ///   { "connected": true, "checked_locations": [12345, 67890], "goal_completed": false }
    ///
    /// "checked_locations" is a cumulative list — the launcher tracks which IDs
    /// it has already reported and only fires LocationsChecked for new ones.
    private async Task PollApStatusAsync(CancellationToken ct)
    {
        string statusPath = Path.Combine(DataDirectory, "ap_status.json");
        bool   goalFired  = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(2000, ct);

                if (!File.Exists(statusPath)) continue;

                string json;
                try { json = await File.ReadAllTextAsync(statusPath, ct); }
                catch { continue; }   // file locked mid-write — skip this tick

                try
                {
                    using var doc  = JsonDocument.Parse(json);
                    var root       = doc.RootElement;

                    // Checked locations — report only IDs not already fired.
                    if (root.TryGetProperty("checked_locations", out var locEl)
                        && locEl.ValueKind == JsonValueKind.Array)
                    {
                        var newIds = new List<long>();
                        foreach (var el in locEl.EnumerateArray())
                        {
                            if (!el.TryGetInt64(out long id)) continue;
                            if (_reportedLocations.Add(id))
                                newIds.Add(id);
                        }
                        if (newIds.Count > 0)
                            LocationsChecked?.Invoke(newIds.ToArray());
                    }

                    // Goal — fire once only.
                    if (!goalFired
                        && root.TryGetProperty("goal_completed", out var gc)
                        && gc.ValueKind == JsonValueKind.True)
                    {
                        goalFired = true;
                        GoalCompleted?.Invoke();
                    }
                }
                catch { /* malformed JSON — skip this tick */ }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task DownloadAndExtractGameAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(), ForkZipName(version));
        try
        {
            // Download (~70 MB)
            progress.Report((5, $"Downloading OpenTTD Archipelago {version}..."));
            using var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tempZip);
            var buf = new byte[81920];
            int bytesRead;

            while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                downloaded += bytesRead;
                if (total > 0)
                {
                    int pct = (int)(5 + 60 * downloaded / total);
                    progress.Report((pct, $"Downloading OpenTTD Archipelago... {downloaded / 1_000_000}MB"));
                }
            }
            await dst.FlushAsync(ct);
            dst.Close();

            // Extract
            progress.Report((70, "Extracting..."));
            Directory.CreateDirectory(GameDirectory);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, GameDirectory, overwriteFiles: true);

            // Release zips may contain a single top-level sub-folder
            // (e.g. openttd-archipelago-v1.4.1-win64/). Flatten it so
            // openttd.exe lands directly in GameDirectory.
            if (!File.Exists(GameExePath))
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

            progress.Report((80, "Game files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }
}
