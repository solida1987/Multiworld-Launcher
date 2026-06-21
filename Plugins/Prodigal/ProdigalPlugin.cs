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
using LauncherV2.Core;

// IMPORTANT (real project has <UseWindowsForms>true</UseWindowsForms>):
// WPF UI types that collide with WinForms are FULLY QUALIFIED below
// (System.Windows.Controls.*, System.Windows.Media.*, System.Windows.Thickness,
// System.Windows.FontWeights, System.Windows.HorizontalAlignment,
// System.Windows.TextWrapping, …) to avoid CS0104 ambiguities. Do NOT add
// `using System.Windows.Controls;` / `using System.Windows.Media;` here.

namespace LauncherV2.Plugins.Prodigal;

// ═══════════════════════════════════════════════════════════════════════════════
// ProdigalPlugin — install / update / launch for "Prodigal" by Chase Bethea,
// played through the ProdigalArchipelago mod by randomsalience. This is a
// NATIVE "ConnectsItself" integration: the patched game has an Archipelago
// connection UI built in and connects to the AP server itself — no separate
// client, no Lua bridge.
//
// GAME FACTS (2026-06-14)
// ─────────────────────────────────────────────────────────────────────────────
//   * GAME: "Prodigal" — a charming action RPG / Zelda-like by Chase Bethea
//     where you return to your hometown of Goldenport to explore dungeons and
//     uncover a mystery. NOT on Steam. Available on itch.io:
//     https://coaguco.itch.io/prodigal
//
//   * MOD REPO: https://github.com/randomsalience/ProdigalArchipelago
//     GitHub releases ship the patched game build (GameMaker Studio or similar)
//     with the AP client integrated. The release zip wraps or patches the base
//     game so all items, keys, and progression are randomized into the multiworld.
//
//   * AP WORLD STRING: "Prodigal" (verified from randomsalience/ProdigalArchipelago).
//
//   * CONNECTION: FULLY IN-GAME (ConnectsItself = true). The patched game has
//     its own built-in Archipelago connection UI — the player opens the in-game
//     AP menu, enters server address / slot name / password, and connects from
//     there. The launcher does NOT hold its own ApClient while the game runs;
//     it only launches the exe.
//
//   * ITCH.IO GATE: The base game requires a purchase on itch.io. The launcher
//     CANNOT auto-download the base game (itch.io requires login/purchase).
//     The install flow is:
//       1. Download the mod zip from GitHub releases into GameDirectory.
//       2. Extract it there.
//       3. Show the user a clear note: they must own the base game from
//          itch.io and follow the setup instructions in the mod repo to
//          combine their purchased copy with the downloaded mod files.
//     This is the "guided install" pattern — we handle the mod download but
//     the user must supply the base game.
//
//   * LAUNCH: Find *.exe in GameDirectory matching "prodigal" (case-insensitive);
//     if not found, open GameDirectory in Explorer so the user can launch manually.
//
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ProdigalPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER = "randomsalience";
    private const string GITHUB_REPO  = "ProdigalArchipelago";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"{GH_RELEASES_URL}/latest";

    private const string ItchUrl      = "https://coaguco.itch.io/prodigal";
    private const string SetupGuideUrl = $"{RepoUrl}#installation";

    private const int DefaultApPort = 38281;

    private const string VersionFileName = "prodigal_ap_version.dat";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "prodigal";
    public string DisplayName => "Prodigal";
    public string Subtitle    => "Native PC · built-in Archipelago";

    /// EXACT AP game string — verified from randomsalience/ProdigalArchipelago.
    public string ApWorldName => "Prodigal";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "prodigal.png");

    public string ThemeAccentColor => "#C8721A";   // warm amber / adventure RPG

    public string[] GameBadges => new[] { "Requires Prodigal (itch.io)" };

    public string Description =>
        "Prodigal is a charming action RPG by Chase Bethea — a Zelda-like adventure " +
        "where you return to your hometown of Goldenport and explore dungeons, towns, " +
        "and secrets in a hand-crafted world. The ProdigalArchipelago mod by " +
        "randomsalience fully randomizes the game for Archipelago Multiworld: items, " +
        "keys, equipment, and progression are shuffled into the multiworld, and the " +
        "patched game has a built-in Archipelago connection UI so it connects to the " +
        "server itself — no separate client needed. Prodigal is available on itch.io " +
        "(purchase required); the launcher downloads the mod files from GitHub and " +
        "guides you through combining them with your copy of the game.";

    public string? VideoPreviewUrl  => null;
    public string[] ScreenshotUrls  => Array.Empty<string>();

    // ── ConnectsItself / SupportsStandalone ───────────────────────────────────

    /// The patched game has a built-in AP client — the launcher never holds the
    /// slot while the game runs.
    public bool ConnectsItself    => true;

    /// Standalone play requires the user's own base game to be set up.
    public bool SupportsStandalone => true;

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => ResolveGameExe() != null;
    public bool    IsRunning        { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the Prodigal Archipelago mod files are installed.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Prodigal");

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    private string SettingsSidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                        "prodigal_launcher.json");

    private string SettingsSidecarDir => Path.GetDirectoryName(SettingsSidecarPath)!;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;
    private string?  _apWorldFileName;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The patched game's built-in AP client reports checks/items/goal to the AP
    // server itself — the launcher relays nothing.
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
        progress.Report((2, "Checking latest ProdigalArchipelago release..."));
        var (version, zipUrl, apworldUrl, apworldName) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;
        _apWorldFileName = apworldName;

        // 2. Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"ProdigalArchipelago {version} is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a download for ProdigalArchipelago on the GitHub " +
                "release page. Check your internet connection, or download the " +
                "mod manually from " + RepoUrl + "/releases.");

        // 3. Download + extract the mod build.
        await DownloadAndExtractAsync(zipUrl, version, progress, ct);

        // 4. Fetch the apworld next to the install IF the release ships one
        //    (opportunistic — the AP-main world may already be bundled).
        if (apworldUrl != null)
        {
            try
            {
                progress.Report((85, "Downloading the Prodigal apworld..."));
                byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                string apworldDst = Path.Combine(GameDirectory,
                    string.IsNullOrEmpty(apworldName) ? "prodigal.apworld" : apworldName);
                await File.WriteAllBytesAsync(apworldDst, apworld, ct);
                progress.Report((92,
                    $"{Path.GetFileName(apworldDst)} saved — copy it into Archipelago's " +
                    "custom_worlds folder if you generate with this build."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((92,
                    "Could not download the apworld — check the mod repo for the latest version."));
            }
        }

        // 5. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"ProdigalArchipelago {version} mod files are in: {GameDirectory}\n\n" +
            "IMPORTANT: You must own Prodigal (purchased on itch.io). Follow the " +
            "setup instructions at the mod repo to combine your purchased copy with " +
            "these mod files, then press Play to launch. Connection is done from the " +
            "in-game Archipelago menu."));
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
        string? exe = ResolveGameExe();
        if (exe != null)
        {
            StartGameProcess(exe);
        }
        else
        {
            // Base game not yet combined — open the folder in Explorer so the
            // user can follow the setup guide.
            OpenGameDirectoryInExplorer();
        }
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        string? exe = ResolveGameExe();
        if (exe != null)
        {
            StartGameProcess(exe);
        }
        else
        {
            OpenGameDirectoryInExplorer();
        }
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Optional metadata ─────────────────────────────────────────────────────

    public string? BuiltAgainstDataPackageChecksum => null;

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var accent  = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xC8, 0x72, 0x1A));
        var success = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warning = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xFF, 0xB3, 0x00));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        // ── Purchase notice ───────────────────────────────────────────────
        var noticeBox = new System.Windows.Controls.Border
        {
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromArgb(0x30, 0xC8, 0x72, 0x1A)),
            BorderBrush = accent,
            BorderThickness = new System.Windows.Thickness(1),
            CornerRadius = new System.Windows.CornerRadius(4),
            Padding = new System.Windows.Thickness(10, 8, 10, 8),
            Margin  = new System.Windows.Thickness(0, 0, 0, 14),
        };
        noticeBox.Child = new System.Windows.Controls.TextBlock
        {
            Text = "Prodigal requires a purchase on itch.io. The launcher downloads the " +
                   "Archipelago mod files from GitHub, but you must own the base game and " +
                   "follow the setup guide at the mod repo to combine them. Connection is " +
                   "done from the in-game Archipelago menu after setup.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
        };
        panel.Children.Add(noticeBox);

        // ── Section: Install directory ────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "MOD FILES DIRECTORY", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = GameDirectory, IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Browse...", Width = 90,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select Prodigal Archipelago mod folder",
                InitialDirectory = Directory.Exists(GameDirectory) ? GameDirectory
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
            }
        };
        var openBtn = new System.Windows.Controls.Button
        {
            Content = "Open Folder", Width = 90,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
        };
        openBtn.Click += (_, _) => OpenGameDirectoryInExplorer();
        System.Windows.Controls.DockPanel.SetDock(dirBtn,  System.Windows.Controls.Dock.Right);
        System.Windows.Controls.DockPanel.SetDock(openBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(openBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        // Install status
        bool installed = IsInstalled;
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = installed
                ? "Prodigal is installed and ready to launch."
                : "Mod files downloaded but game exe not found. Follow the setup guide to combine with your purchased copy of Prodigal.",
            FontSize   = 11,
            Foreground = installed ? success : warning,
            Margin     = new System.Windows.Thickness(0, 6, 0, 14),
            TextWrapping = System.Windows.TextWrapping.Wrap,
        });

        // ── Section: How to connect ───────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "HOW TO CONNECT", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "The patched game has a built-in Archipelago connection UI. After " +
                   "completing the setup guide:\n" +
                   "1. Press Play to launch the game.\n" +
                   "2. In the game, open the Archipelago connection menu.\n" +
                   "3. Enter your server address, slot name, and password.\n" +
                   "4. Connect — the game handles everything from there.\n\n" +
                   "The launcher does not hold the Archipelago slot while the game runs.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: Links ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("ProdigalArchipelago Mod (GitHub) ↗", RepoUrl),
            ("Buy Prodigal on itch.io ↗",          ItchUrl),
            ("Setup Guide ↗",                       SetupGuideUrl),
            ("Archipelago Official ↗",              "https://archipelago.gg"),
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
                        new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true });
                }
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

    /// "v1.2.3" / "1.2.3" → trimmed, leading 'v' stripped when it decorates a
    /// digit. Returns null for null/blank tags.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest release from GitHub: version + zip URL + optional
    /// apworld URL + apworld filename. Uses /releases/latest first (non-
    /// prerelease); falls back to scanning /releases for the most recent when
    /// the latest is prerelease. If the API is unreachable returns a minimal
    /// record with no download URL so the caller can surface a helpful message.
    private async Task<(string Version, string? ZipUrl, string? ApWorldUrl, string? ApWorldName)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        // Try /releases/latest (non-prerelease official).
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null
                && root.TryGetProperty("assets", out var assets)
                && assets.ValueKind == JsonValueKind.Array)
            {
                var (zip, apworld, apworldName) = PickGameZipAndApworld(assets);
                if (zip != null)
                    return (version, zip, apworld, apworldName);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through to all-releases scan */ }

        // Scan /releases (up to first 10) to find any release with a zip.
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);

            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                string? version = rel.TryGetProperty("tag_name", out var t)
                    ? NormalizeTag(t.GetString())
                    : null;
                if (version == null) continue;

                if (rel.TryGetProperty("assets", out var assets)
                    && assets.ValueKind == JsonValueKind.Array)
                {
                    var (zip, apworld, apworldName) = PickGameZipAndApworld(assets);
                    if (zip != null)
                        return (version, zip, apworld, apworldName);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable */ }

        // No download available — return a version string so UI can show it.
        return ("unknown", null, null, null);
    }

    /// From a release's assets array, pick the game zip and any prodigal apworld.
    /// The game zip is matched broadly: any .zip that isn't a source archive and
    /// isn't an apworld, linux, mac, or docs file. Prefers anything containing
    /// "prodigal" in the name, then any qualifying zip.
    private static (string? Zip, string? ApWorld, string? ApWorldName)
        PickGameZipAndApworld(JsonElement assets)
    {
        string? bestZip = null, anyZip = null;
        string? apworld = null, apworldName = null;

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
            else if (lower.EndsWith(".zip")
                     && !lower.Contains("source")
                     && !lower.Contains("linux")
                     && !lower.Contains("ubuntu")
                     && !lower.Contains("mac")
                     && !lower.Contains("osx")
                     && !lower.Contains("darwin")
                     && !lower.EndsWith(".apworld"))
            {
                anyZip ??= url;
                if (bestZip == null && lower.Contains("prodigal"))
                    bestZip = url;
            }
        }

        return (bestZip ?? anyZip, apworld, apworldName);
    }

    // ── Private helpers — exe resolution ──────────────────────────────────────

    /// Find the game exe: scan GameDirectory for any *.exe whose name contains
    /// "prodigal" (case-insensitive), or fall back to any non-helper exe. Returns
    /// null if nothing is found (base game not yet combined with mod files).
    private string? ResolveGameExe()
    {
        if (!Directory.Exists(GameDirectory)) return null;
        try
        {
            string? prodigalExe = null, anyExe = null;
            foreach (string exe in Directory.EnumerateFiles(
                GameDirectory, "*.exe", SearchOption.AllDirectories))
            {
                string nameLower = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (nameLower.Contains("unins") || nameLower.Contains("setup")
                    || nameLower.Contains("crash") || nameLower.Contains("report"))
                    continue;

                anyExe ??= exe;
                if (nameLower.Contains("prodigal") || nameLower == "game")
                {
                    prodigalExe ??= exe;
                }
            }
            return prodigalExe ?? anyExe;
        }
        catch { return null; }
    }

    // ── Private helpers — process ─────────────────────────────────────────────

    private void StartGameProcess(string exePath)
    {
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? GameDirectory,
            UseShellExecute  = false,
        }) ?? throw new InvalidOperationException("Failed to start Prodigal.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    private void OpenGameDirectoryInExplorer()
    {
        try
        {
            Directory.CreateDirectory(GameDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = $"\"{GameDirectory}\"",
                UseShellExecute = true,
            });
        }
        catch { }
    }

    // ── Private helpers — download/extract ────────────────────────────────────

    private async Task DownloadAndExtractAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"prodigal-ap-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading ProdigalArchipelago {version}..."));
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
                        int pct = (int)(5 + 60 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading ProdigalArchipelago... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting mod files..."));
            Directory.CreateDirectory(GameDirectory);
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, GameDirectory, overwriteFiles: true);

            // Flatten a lone top-level sub-folder so files land directly in
            // GameDirectory (common release zip pattern).
            if (Directory.GetFiles(GameDirectory).Length == 0)
            {
                string[] subdirs = Directory.GetDirectories(GameDirectory);
                if (subdirs.Length == 1)
                {
                    string sub = subdirs[0];
                    foreach (string fileSrc in Directory.EnumerateFiles(
                        sub, "*", SearchOption.AllDirectories))
                    {
                        string rel     = Path.GetRelativePath(sub, fileSrc);
                        string fileDst = Path.Combine(GameDirectory, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                        File.Move(fileSrc, fileDst, overwrite: true);
                    }
                    Directory.Delete(sub, recursive: true);
                }
            }

            progress.Report((80, "Mod files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // Reserved for future launcher-side options; kept out of Core/SettingsStore
    // so this plugin is a single self-contained source file.

    private sealed class ProdigalSettings
    {
        // Placeholder — no launcher-side settings required today.
    }

    private ProdigalSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<ProdigalSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(ProdigalSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }
}
