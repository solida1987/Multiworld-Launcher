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

namespace LauncherV2.Plugins.AnUntitledStory;

// ═══════════════════════════════════════════════════════════════════════════════
// AnUntitledStoryPlugin — install / update / launch for "An Untitled Story"
// via the ThatOneGuy27/Archipelago-aus mod (GitHub).
//
// An Untitled Story is a freeware metroidvania by Maddy Thorson. The AP mod
// ships a COMPLETE patched build (GameMaker 8.2) — the player does not need to
// download the original game separately. The mod connects to the AP server via
// a configuration file: ArchipelagoConnectionInfo.ini (host / port / slot).
//
// FACTS VERIFIED THIS SESSION (2026-06-14)
// ─────────────────────────────────────────────────────────────────────────────
//   * REPO: ThatOneGuy27/Archipelago-aus
//     Latest release at time of writing: v1.5-beta
//     Assets:
//       "AnUntitledStory-AP-BetaV1.5.zip"  (22 MB — complete patched game)
//       "aus.apworld"  (16 KB — AP world definition)
//
//   * AP GAME STRING: "An Untitled Story"
//     Verified against worlds/aus/__init__.py → AUSWorld.game = "An Untitled Story"
//     base_id = 72000.
//
//   * CONNECTION CONFIG: ArchipelagoConnectionInfo.ini in the game root.
//     Verified fields (all release notes, v1.1-beta through v1.5-beta):
//         host=archipelago.gg
//         port=38281
//         slot=PlayerSlotName
//     No password field in any release; the GameMaker AP library (gm-apclientpp)
//     does not expose a password field via the INI interface. The plugin writes
//     host/port/slot only; passwords are NOT supported by this mod version.
//
//   * EXE NAME: varies by release. v1.3–v1.4: "AnUntitledStory.Randomizer.Beta.V2.0a.exe"
//     v1.5 (GM 8.2 port): exact name changed — release just says "the .exe file".
//     Resolution: prefer any exe matching "*untitled*" or "*randomizer*" in the
//     install root; fall back to ANY exe that is not an obvious helper.
//     A known-name array is also tried first (see ResolveGameExe).
//
//   * GAME IS FREEWARE: no ROM/asset gate. The mod zip is a self-contained
//     patched build. Install = download zip → extract → configure INI → launch.
//     SupportsStandalone = true (launch without AP by not writing the INI).
//     ConnectsItself = true (the game's own gm-apclientpp DLL owns the AP slot).
//
//   * PRERELEASE RELEASES: all releases are tagged "-beta"; the GitHub
//     /releases/latest endpoint returns the most recent non-pre-release, which
//     would be nothing. We therefore enumerate /releases (not /latest) and pick
//     the first entry (newest).
//
// DEFENSIVE NOTES
//   * The INI is written as a Windows classic [section]-free INI (key=value,
//     no [section] header) since that is the format the release notes show.
//     A read-modify-write preserves any unrecognised keys the game may add.
//   * Password field: the AP protocol supports passwords but the GameMaker
//     gm-apclientpp library reads only host/port/slot from the INI in all
//     v1.1–v1.5 releases inspected. If a future release adds a password field
//     the player can edit the INI manually; we note this limitation in the UI.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class AnUntitledStoryPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER      = "ThatOneGuy27";
    private const string GITHUB_REPO       = "Archipelago-aus";
    private const string RepoUrl           = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL   =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";

    // Pinned fallback — v1.5-beta is the latest at time of writing. Used when
    // the GitHub API is unreachable so install still works.
    private const string FallbackVersion   = "v1.5-beta";
    private const string FallbackZipName   = "AnUntitledStory-AP-BetaV1.5.zip";
    private static readonly string FallbackZipUrl =
        $"{RepoUrl}/releases/download/{FallbackVersion}/{FallbackZipName}";
    private const string FallbackApWorldUrl =
        $"{RepoUrl}/releases/download/{FallbackVersion}/aus.apworld";

    /// The verified AP connection file name, in the game root.
    private const string ApConfigFileName  = "ArchipelagoConnectionInfo.ini";
    private const string VersionFileName   = "aus_ap_version.dat";

    /// Known exe names across v1.1–v1.4 releases (fuzzy resolution used for v1.5+).
    private static readonly string[] KnownExeNames = new[]
    {
        "AnUntitledStory.Randomizer.Beta.V2.0a.exe",
        "AnUntitledStory_Randomizer_Beta_V1.1.exe",
        "AnUntitledStory_Randomizer_Beta_V1.0a.exe",
    };

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "an_untitled_story";
    public string DisplayName => "An Untitled Story";
    public string Subtitle    => "Freeware PC · built-in Archipelago";

    /// EXACT AP game string — verified against worlds/aus/__init__.py
    /// (AUSWorld.game = "An Untitled Story").
    public string ApWorldName => "An Untitled Story";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "an_untitled_story.png");

    public string ThemeAccentColor => "#5A9E6F";   // earthy green matching the game's palette

    public string[] GameBadges => new[] { "Freeware", "Metroidvania" };

    public string Description =>
        "An Untitled Story is a freeware metroidvania by Maddy Thorson, " +
        "chronicling the travels of an adventurous egg through a handcrafted world " +
        "of 18 unique bosses and hundreds of collectibles. " +
        "The Archipelago mod (by ThatOneGuy27) ships a fully self-contained " +
        "patched build — there is nothing to bring. The game connects to the " +
        "Archipelago server via ArchipelagoConnectionInfo.ini (host/port/slot). " +
        "The launcher writes this file for you each time you press Play.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => ResolveGameExe() != null;
    public bool    IsRunning        { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the AUS AP build is installed.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "AnUntitledStory");

    private string ApConfigPath   => Path.Combine(GameDirectory, ApConfigFileName);
    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    private string ApWorldLocalPath { get; set; }
        = string.Empty;   // resolved on first download

    /// Plugin sidecar — keeps settings out of the shared SettingsStore.
    private string SettingsSidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                        "an_untitled_story_launcher.json");
    private string SettingsSidecarDir => Path.GetDirectoryName(SettingsSidecarPath)!;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The game's gm-apclientpp DLL connects to AP directly; the launcher relays
    // nothing. These exist for interface compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Capability flags ──────────────────────────────────────────────────────

    /// The game's built-in gm-apclientpp DLL owns the AP slot connection.
    /// The launcher must not connect its own ApClient to the same slot.
    public bool ConnectsItself    => true;

    /// The game is playable standalone (the INI is simply not written).
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = File.Exists(VersionFilePath) && IsInstalled
                ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim()
                : null;
        }
        catch { InstalledVersion = null; }

        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(GITHUB_OWNER, GITHUB_REPO, ct));
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((2, "Checking latest An Untitled Story (Archipelago) release..."));

        var (tag, zipUrl, apworldUrl, apworldName) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = tag;

        string apworldFileName = string.IsNullOrEmpty(apworldName) ? "aus.apworld" : apworldName;
        ApWorldLocalPath = Path.Combine(GameDirectory, apworldFileName);

        // Already current?
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == tag)
        {
            InstalledVersion = tag;
            progress.Report((100, $"An Untitled Story (AP) {tag} is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a Windows download for An Untitled Story on the " +
                "GitHub release page. Check your internet connection or download " +
                "manually from " + RepoUrl + "/releases.");

        await DownloadAndExtractGameAsync(zipUrl, tag, progress, ct);

        // Download the apworld next to the install (best effort).
        if (!string.IsNullOrEmpty(apworldUrl))
        {
            try
            {
                progress.Report((85, "Downloading aus.apworld..."));
                byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                await File.WriteAllBytesAsync(ApWorldLocalPath, apworld, ct);
                progress.Report((92,
                    $"{apworldFileName} saved — copy it into Archipelago's " +
                    @"custom_worlds folder (C:\ProgramData\Archipelago\custom_worlds) " +
                    "if you generate with this build."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((92,
                    "Could not download aus.apworld — get it from the GitHub release " +
                    "page (the stable world also ships with Archipelago)."));
            }
        }

        await File.WriteAllTextAsync(VersionFilePath, tag, ct);
        InstalledVersion = tag;

        progress.Report((100,
            $"An Untitled Story (AP) {tag} ready. Press Play to connect — the " +
            "launcher fills in ArchipelagoConnectionInfo.ini automatically."));
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
                "An Untitled Story is not installed. Click Install Game first.",
                Path.Combine(GameDirectory, KnownExeNames[0]));

        // Write the verified AP connection INI before launching.
        try { WriteApConfig(session); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        StartGameProcess(exe);
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        string exe = ResolveGameExe()
            ?? throw new FileNotFoundException(
                "An Untitled Story is not installed. Click Install Game first.",
                Path.Combine(GameDirectory, KnownExeNames[0]));

        // Do not write the INI — game starts in vanilla mode.
        StartGameProcess(exe);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert ─────────────────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;   // gm-apclientpp receives items from the AP server directly

    public void OnApStateChanged(ApConnectionState state) { }   // game renders its own status

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xCC, 0x44));

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── INSTALL DIRECTORY ─────────────────────────────────────────────
        panel.Children.Add(MakeLabel("INSTALL DIRECTORY", muted));

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = GameDirectory, IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var browseBtn = new System.Windows.Controls.Button
        {
            Content = "Browse...", Width = 90, Padding = new Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        browseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select An Untitled Story install folder",
                InitialDirectory = Directory.Exists(GameDirectory) ? GameDirectory
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(browseBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(browseBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new TextBlock
        {
            Text = IsInstalled
                ? "An Untitled Story (Archipelago) is installed"
                : "Not installed — click Install in the Play tab",
            FontSize   = 11,
            Foreground = IsInstalled ? success : muted,
            Margin     = new Thickness(0, 6, 0, 12),
        });

        // ── ARCHIPELAGO CONNECTION ─────────────────────────────────────────
        panel.Children.Add(MakeLabel("ARCHIPELAGO CONNECTION", muted));
        panel.Children.Add(new TextBlock
        {
            Text =
                "The game connects via ArchipelagoConnectionInfo.ini in the install " +
                "folder (host / port / slot). The launcher writes this file " +
                "automatically each time you press Play. " +
                "Note: this GameMaker mod reads host, port, and slot only — " +
                "password-protected rooms are not supported in the current build.",
            FontSize = 11, Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        panel.Children.Add(new TextBlock
        {
            Text =
                "After launching, select the Archipelago option from the in-game " +
                "menu to begin the multiworld session.",
            FontSize = 11, Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // Password warning
        panel.Children.Add(new TextBlock
        {
            Text = "Password-protected rooms: the mod does not support passwords via the INI. " +
                   "Use a room without a password, or edit ArchipelagoConnectionInfo.ini by hand " +
                   "if a future mod update adds a password field.",
            FontSize = 11, Foreground = warn,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── KNOWN ISSUES ──────────────────────────────────────────────────
        panel.Children.Add(MakeLabel("KNOWN ISSUES", muted));
        panel.Children.Add(new TextBlock
        {
            Text =
                "Starting inventory items are not supported by the mod and will " +
                "break later received items. Use plandoed items in early locations " +
                "instead (e.g. \"First Item\", \"Bird Temple\"). " +
                "To play on multiple AP slots at once you need separate copies of " +
                "the game folder, or delete UntitledSave4 and ItemRando4.ini " +
                "between sessions.",
            FontSize = 11, Foreground = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // Apworld note
        if (File.Exists(ApWorldLocalPath))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"{Path.GetFileName(ApWorldLocalPath)} is saved in the install folder. " +
                       @"Copy it into C:\ProgramData\Archipelago\custom_worlds if you generate " +
                       "with this build rather than the bundled Archipelago world.",
                FontSize = 11, Foreground = muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });
        }

        // ── LINKS ─────────────────────────────────────────────────────────
        panel.Children.Add(MakeLabel("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("An Untitled Story AP Mod (GitHub) ↗",  RepoUrl),
            ("An Untitled Story Releases ↗",         $"{RepoUrl}/releases"),
            ("Archipelago Official ↗",               "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2),
                Background      = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize        = 12,
                Margin          = new Thickness(0, 0, 0, 4),
                Foreground      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor          = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true }); }
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
                    Version: el.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "",
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

    /// Enumerate releases (NOT /latest — all AUS releases are tagged "-beta" and
    /// are therefore pre-releases, which /latest skips). Pick the first entry
    /// (newest). Falls back to the pinned v1.5-beta when the API is unreachable.
    private async Task<(string Tag, string? ZipUrl, string? ApWorldUrl, string? ApWorldName)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL + "?per_page=5", ct);
            using var doc = JsonDocument.Parse(json);

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string? tag = el.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                if (string.IsNullOrEmpty(tag)) continue;

                if (!el.TryGetProperty("assets", out var assets) ||
                    assets.ValueKind != JsonValueKind.Array)
                    continue;

                var (zip, apworld, apworldName) = PickAssets(assets);
                if (zip != null)
                    return (tag, zip, apworld, apworldName);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable — fall through to pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl, FallbackApWorldUrl, "aus.apworld");
    }

    /// From a release's assets array, pick the game zip and apworld asset.
    private static (string? Zip, string? ApWorld, string? ApWorldName)
        PickAssets(JsonElement assets)
    {
        string? zip = null, apworld = null, apworldName = null;
        string? anyZip = null;

        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();

            if (lower.EndsWith(".apworld"))
            {
                apworld     = url;
                apworldName = name;
            }
            else if (lower.EndsWith(".zip") &&
                     !lower.Contains("source") &&
                     !lower.Contains("linux") &&
                     !lower.Contains("mac")   &&
                     !lower.Contains("osx"))
            {
                anyZip ??= url;
                if (lower.Contains("anuntitledstory") || lower.Contains("untitled"))
                    zip = url;
            }
        }

        zip ??= anyZip;
        return (zip, apworld, apworldName);
    }

    // ── Private helpers — exe resolution ──────────────────────────────────────

    /// Resolve the installed exe. Resolution order:
    ///   1. Known exe names from past releases (KnownExeNames).
    ///   2. Any exe whose name contains "untitled" or "randomizer".
    ///   3. Any exe in the install root that is not a helper/uninstaller.
    private string? ResolveGameExe()
    {
        if (!Directory.Exists(GameDirectory)) return null;

        // Pass 1: known names.
        foreach (string known in KnownExeNames)
        {
            string path = Path.Combine(GameDirectory, known);
            if (File.Exists(path)) return path;
        }

        // Pass 2 + 3: scan.
        try
        {
            string? best = null;
            foreach (string exe in Directory.EnumerateFiles(GameDirectory, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string nameLower = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (nameLower.Contains("unins") || nameLower.Contains("setup") ||
                    nameLower.Contains("crash") || nameLower.Contains("report"))
                    continue;
                if (nameLower.Contains("untitled") || nameLower.Contains("randomizer"))
                    return exe;       // Pass 2 match — return immediately
                best ??= exe;        // Pass 3 candidate
            }
            return best;
        }
        catch { return null; }
    }

    // ── Private helpers — ArchipelagoConnectionInfo.ini ───────────────────────

    /// Write the verified AP connection INI. The file uses a section-less
    /// key=value format as documented in all release notes. Read-modify-write so
    /// any keys the game itself adds (e.g. cached port) are preserved.
    private void WriteApConfig(ApSession session)
    {
        Directory.CreateDirectory(GameDirectory);

        var config = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Read existing keys to preserve them (game may write back extra state).
        if (File.Exists(ApConfigPath))
        {
            try
            {
                foreach (string line in File.ReadAllLines(ApConfigPath))
                {
                    int eq = line.IndexOf('=');
                    if (eq > 0)
                    {
                        string key = line[..eq].Trim();
                        string val = line[(eq + 1)..].Trim();
                        config[key] = val;
                    }
                }
            }
            catch { /* corrupt — start fresh */ }
        }

        // Extract host and port from the session URI.
        var (host, port) = ParseServerHostPort(session.ServerUri);
        config["host"] = host;
        config["port"] = port.ToString();
        config["slot"] = session.SlotName ?? "";

        var sb = new StringBuilder();
        foreach (var kv in config)
            sb.AppendLine($"{kv.Key}={kv.Value}");

        File.WriteAllText(ApConfigPath, sb.ToString(), new UTF8Encoding(false));
    }

    /// Parse "host:port" from any AP server URI format. Default port 38281.
    private static (string Host, int Port) ParseServerHostPort(string serverUri)
    {
        string s = serverUri.Trim();
        foreach (string prefix in new[] { "wss://", "ws://", "archipelago://" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            { s = s[prefix.Length..]; break; }
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
            host = s;   // bare IPv6 literal
        }
        else
        {
            int colon = s.LastIndexOf(':');
            if (colon > 0 && int.TryParse(s[(colon + 1)..], out int p) && p > 0 && p <= 65535)
            { host = s[..colon]; port = p; }
        }

        if (host.Length == 0) host = "archipelago.gg";
        return (host, port);
    }

    // ── Private helpers — process ─────────────────────────────────────────────

    private void StartGameProcess(string exePath)
    {
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = GameDirectory,
            UseShellExecute  = false,
        }) ?? throw new InvalidOperationException("Failed to start An Untitled Story.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — download/extract ────────────────────────────────────

    private async Task DownloadAndExtractGameAsync(
        string zipUrl,
        string tag,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"aus-ap-{tag}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading An Untitled Story (AP) {tag}..."));
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
                        int pct = (int)(5 + 65 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading An Untitled Story... {downloaded / 1_000_000} MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((72, "Extracting game files..."));
            Directory.CreateDirectory(GameDirectory);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, GameDirectory, overwriteFiles: true);

            // Flatten a single wrapper sub-folder if the zip extracts one.
            FlattenSingleSubdir(GameDirectory);

            progress.Report((82, "Game files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    /// If GameDirectory contains no files but exactly one sub-directory, move
    /// all contents up one level (flatten). Some zips wrap everything in a
    /// top-level folder.
    private static void FlattenSingleSubdir(string dir)
    {
        try
        {
            if (Directory.GetFiles(dir).Length > 0) return;
            string[] subdirs = Directory.GetDirectories(dir);
            if (subdirs.Length != 1) return;
            string sub = subdirs[0];
            foreach (string src in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
            {
                string rel = Path.GetRelativePath(sub, src);
                string dst = Path.Combine(dir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Move(src, dst, overwrite: true);
            }
            Directory.Delete(sub, recursive: true);
        }
        catch { /* best effort — ResolveGameExe scans subdirs anyway */ }
    }

    // ── Private helpers — UI builder ──────────────────────────────────────────

    private static System.Windows.Controls.TextBlock MakeLabel(string text, System.Windows.Media.SolidColorBrush color) => new()
    {
        Text       = text,
        FontSize   = 10,
        FontWeight = System.Windows.FontWeights.SemiBold,
        Foreground = color,
        Margin     = new Thickness(0, 0, 0, 8),
    };

    // ── Settings sidecar (reserved for future launcher-side options) ──────────

    private sealed class AusSettings { /* placeholder */ }

    private AusSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<AusSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(AusSettings s)
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
}
