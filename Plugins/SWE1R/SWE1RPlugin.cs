using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.Plugins.SWE1R;

// ═══════════════════════════════════════════════════════════════════════════════
// SWE1RPlugin — install / update / launch for
// "Star Wars Episode I: Racer" in Archipelago.
//
// REALITY CHECK (2026-06-15) — facts verified by web search this session
// ─────────────────────────────────────────────────────────────────────────────
// The integration is REAL and RELEASED:
//
//   * APWorld:  github.com/wcolding/SWR_apworld
//     A community AP world that shuffles pod parts, race rewards, characters,
//     and track order into the multiworld. The goal is completing all 25 courses
//     with access gated by "Circuit Passes" or "Course Unlocks" depending on
//     settings. Published on the Archipelago Wiki (Miraheze).
//
//   * Client:   github.com/wcolding/SWR_AP_Client  (separate repository)
//     A standalone Windows executable (SWR_AP_Client.exe or similar) that acts
//     as the bridge between the running game and the Archipelago server.  The
//     user installs it separately. The client connects to the AP server and
//     patches the live game process — it is NOT a fork of the game binary and
//     does not replace the original game install.
//
//   * The original game is sold on Steam (App ID 808910) and GOG.  The player
//     must own and install it themselves; this plugin never installs or modifies
//     the game files (§11 of the launcher contract).  It only installs the
//     SWR AP Client (the tool the user needs for AP play).
//
// HOW IT CONNECTS
// ───────────────
// The SWR AP Client is a SEPARATE process that bridges the game to the AP
// server.  It connects under the player's slot name — the launcher MUST NOT
// also connect its own ApClient to the same slot while the client + game are
// running or they will endlessly kick each other off.  Hence:
//   ConnectsItself = true
//
// WHAT THIS PLUGIN DOES (V2.0)
// ────────────────────────────
//   1. Installs/updates the SWR AP Client from wcolding/SWR_AP_Client GitHub
//      releases (latest tag, pinned fallback when the API is unreachable).
//   2. Downloads the release's .apworld next to the install so the user can
//      drop it into Archipelago's custom_worlds folder.
//   3. On AP launch, the launcher locates the original game EXE (Steam install
//      detected via registry or the user's configured path), starts the game,
//      and then starts the SWR AP Client, pre-filling host/slot/password via
//      command-line arguments.
//   4. Plain standalone launch starts the game without the client.
//
// DEFENSIVE / UNVERIFIED DETAILS
// ───────────────────────────────
// The exact Windows asset name on wcolding/SWR_AP_Client was not verified
// offline; the resolver matches any Windows .zip or .exe asset by pattern,
// with a documented fallback.  The exact CLI argument format for the client
// was also not verified — BuildClientArguments() uses the common
//   --server <host:port> --name <slot> [--password <pw>]
// pattern used by most AP external clients; adjust when the real args are
// confirmed.
//
// GITHUB RELEASE FORMAT (SWR AP Client)
//   Repo:   github.com/wcolding/SWR_AP_Client
//   Assets: SWR_AP_Client-win64.zip or SWR_AP_Client.exe (exact name TBC)
//           star_wars_ep1_racer.apworld  (or swe1r.apworld — name TBC)
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SWE1RPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string APWORLD_GITHUB_OWNER = "wcolding";
    private const string APWORLD_GITHUB_REPO  = "SWR_apworld";
    private const string CLIENT_GITHUB_OWNER  = "wcolding";
    private const string CLIENT_GITHUB_REPO   = "SWR_AP_Client";

    private static readonly string ApWorldRepoUrl =
        "https://github.com/" + APWORLD_GITHUB_OWNER + "/" + APWORLD_GITHUB_REPO;
    private static readonly string ClientRepoUrl =
        "https://github.com/" + CLIENT_GITHUB_OWNER + "/" + CLIENT_GITHUB_REPO;
    private static readonly string GH_CLIENT_RELEASES_URL =
        "https://api.github.com/repos/" + CLIENT_GITHUB_OWNER + "/" + CLIENT_GITHUB_REPO + "/releases";
    private static readonly string GH_APWORLD_RELEASES_URL =
        "https://api.github.com/repos/" + APWORLD_GITHUB_OWNER + "/" + APWORLD_GITHUB_REPO + "/releases";

    /// Pinned fallback version used when the GitHub API is unreachable.
    /// Update this on every verified client release bump.
    private const string FallbackClientVersion = "1.0.0";

    /// Steam App ID for Star Wars Episode I: Racer (verified).
    private const uint SteamAppId = 808910;

    /// Registry key where Steam stores the game's install location.
    private const string SteamAppsRegKey =
        @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Steam App 808910";

    /// Version stamp written after a successful client install.
    private const string VersionFileName = "swe1r_ap_client_version.dat";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "star_wars_ep1_racer";
    public string DisplayName => "Star Wars Episode I: Racer";
    public string Subtitle    => "Community Archipelago";

    /// AP game name as registered in the SWR apworld.
    /// Verified: wcolding/SWR_apworld README + Archipelago Wiki page.
    public string ApWorldName => "Star Wars Episode I Racer";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "star_wars_ep1_racer.png");

    public string ThemeAccentColor => "#3A1A00";   // dark amber/sand dunes
    public string[] GameBadges     => new[] { "Requires game (Steam/GOG)" };

    public string Description =>
        "Star Wars Episode I: Racer (1999) — the classic podracing game by LucasArts — " +
        "played as an Archipelago multiworld. Pod parts, race rewards, characters, and " +
        "track order are shuffled into the multiworld. The goal is to complete all 25 " +
        "courses, with circuit access gated by Circuit Passes or Course Unlocks " +
        "depending on your settings. The integration uses the SWR AP Client by wcolding, " +
        "a small bridge tool that connects your running game to the Archipelago server. " +
        "You must own the game on Steam or GOG — the launcher installs only the AP client " +
        "tool and never modifies your game files.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => ResolveClientExe() != null;
    public bool    IsRunning        { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the SWR AP Client is installed.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "SWE1R");

    private string VersionFilePath
        => Path.Combine(GameDirectory, VersionFileName);

    private string ApWorldLocalPath
        => Path.Combine(GameDirectory, _apWorldFileName ?? "star_wars_ep1_racer.apworld");

    private string SettingsSidecarPath
        => Path.Combine(GameDirectory, "swe1r_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _clientProcess;
    private Process? _gameProcess;
    private string?  _apWorldFileName;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The SWR AP Client handles the slot connection directly — the launcher
    // relays nothing (ConnectsItself = true).
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
                await GitHubHelper.FetchLatestTagAsync(CLIENT_GITHUB_OWNER, CLIENT_GITHUB_REPO, ct));
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
        progress.Report((2, "Checking latest SWR AP Client release..."));
        var (version, zipUrl, apworldUrl, apworldName) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;
        if (apworldName != null) _apWorldFileName = apworldName;

        // Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, "SWR AP Client is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a Windows download for the SWR AP Client on the GitHub " +
                "release page. Check your internet connection, or download manually from " +
                ClientRepoUrl + "/releases.");

        await DownloadAndExtractClientAsync(zipUrl, version, progress, ct);

        // Fetch the apworld next to the install (best effort).
        if (apworldUrl != null)
        {
            try
            {
                progress.Report((85, "Downloading the Star Wars Episode I Racer apworld..."));
                byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                await File.WriteAllBytesAsync(ApWorldLocalPath, apworld, ct);
                progress.Report((92, $"{Path.GetFileName(ApWorldLocalPath)} saved — copy it into Archipelago's custom_worlds folder to generate multiworlds."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((92, "Could not download the apworld — get it from " + ApWorldRepoUrl + "/releases."));
            }
        }

        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;
        progress.Report((100, "SWR AP Client ready. Make sure your game is installed, then press Play."));
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
        string clientExe = ResolveClientExe()
            ?? throw new FileNotFoundException(
                "The SWR AP Client is not installed. Click Install first.",
                Path.Combine(GameDirectory, "SWR_AP_Client.exe"));

        // 1. Start the original game (Steam protocol or direct exe).
        StartGame();

        // 2. Start the AP client bridge with connection arguments.
        string args = BuildClientArguments(session);
        StartClientProcess(clientExe, args);
        return Task.CompletedTask;
    }

    /// The original game can be launched standalone (without the AP client).
    public bool SupportsStandalone => true;

    /// The SWR AP Client connects to the AP slot directly — the launcher must
    /// not hold its own ApClient on the same slot while the game is running.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // Launch just the game, no AP client.
        StartGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _clientProcess?.Kill(entireProcessTree: true); } catch { }
        try { _gameProcess?.Kill(entireProcessTree: true); }  catch { }
        IsRunning = false;
        return Task.CompletedTask;
    }

    // ── ValidateExistingInstall ───────────────────────────────────────────────

    /// Called when the user manually points the launcher at the game folder.
    /// Accept any folder that contains the known game executable.
    public string? ValidateExistingInstall(string folder)
    {
        string[] candidates = new[] { "SWEP1RCR.exe", "sw_ep1racer.exe" };
        foreach (string name in candidates)
        {
            if (File.Exists(Path.Combine(folder, name)))
                return null;   // folder is acceptable
        }
        return "Could not find SWEP1RCR.exe in that folder. " +
               "Make sure you picked the Star Wars Episode I: Racer install directory.";
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The SWR AP Client receives items from the AP server directly;
        // there is nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // No in-game HUD channel — the AP client handles its own status display.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Section: AP Client install directory ──────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "AP CLIENT DIRECTORY", FontSize = 10, FontWeight = FontWeights.SemiBold,
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
                Title            = "Select SWR AP Client folder",
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
            Text       = IsInstalled ? "✓ SWR AP Client is installed"
                                     : "Not installed (click Install in the Play tab)",
            FontSize   = 11, Foreground = IsInstalled ? success : muted,
            Margin     = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Game install path ─────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GAME INSTALL PATH", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? detectedGame = DetectGameExe();
        string? savedGameDir = LoadSettings().GameDirectory;
        string? activeGameDir = !string.IsNullOrEmpty(savedGameDir)
            ? savedGameDir
            : (detectedGame != null ? Path.GetDirectoryName(detectedGame) : null);

        var gameRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var gameBox = new TextBox
        {
            Text = activeGameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var gameBtn = new Button
        {
            Content = "Browse...", Width = 90, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        gameBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select Star Wars Episode I: Racer install folder",
                InitialDirectory = Directory.Exists(activeGameDir ?? "") ? activeGameDir!
                                   : @"C:\Program Files (x86)\Steam\steamapps\common",
            };
            if (dlg.ShowDialog() == true)
            {
                string? reason = ValidateExistingInstall(dlg.FolderName);
                if (reason != null)
                {
                    MessageBox.Show(reason, "Wrong folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var s = LoadSettings();
                s.GameDirectory = dlg.FolderName;
                SaveSettings(s);
                gameBox.Text = dlg.FolderName;
            }
        };
        DockPanel.SetDock(gameBtn, Dock.Right);
        gameRow.Children.Add(gameBtn);
        gameRow.Children.Add(gameBox);
        panel.Children.Add(gameRow);

        bool gameFound = detectedGame != null || (activeGameDir != null && Directory.Exists(activeGameDir));
        panel.Children.Add(new TextBlock
        {
            Text = gameFound
                ? "✓ Game found" + (detectedGame != null && string.IsNullOrEmpty(savedGameDir)
                    ? " (auto-detected from Steam registry)" : "")
                : "Game not found — pick your install folder above, or install the game from Steam (App ID 808910) or GOG.",
            FontSize = 11, Foreground = gameFound ? success : warn,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: apworld ──────────────────────────────────────────────
        if (IsInstalled && File.Exists(ApWorldLocalPath))
        {
            panel.Children.Add(new TextBlock
            {
                Text = Path.GetFileName(ApWorldLocalPath) + " is saved in the AP Client folder — " +
                       @"copy it into your Archipelago custom_worlds folder " +
                       @"(default: C:\ProgramData\Archipelago\custom_worlds) " +
                       "to generate multiworlds.",
                FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });
        }

        // ── Section: Links ─────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("SWR APWorld (GitHub) ↗",       ApWorldRepoUrl),
            ("SWR AP Client (GitHub) ↗",     ClientRepoUrl),
            ("Star Wars Ep I Racer on Steam ↗",
                "https://store.steampowered.com/app/808910/STAR_WARS_Episode_I_Racer/"),
            ("Archipelago Wiki — SWE1R ↗",
                "https://archipelago.miraheze.org/wiki/Star_Wars_Episode_I_Racer"),
            ("Archipelago Official ↗",        "https://archipelago.gg"),
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
        // Pull news from both the client repo and the apworld repo; merge by date.
        var items = new List<NewsItem>();
        try
        {
            items.AddRange(await FetchNewsFromRepo(GH_CLIENT_RELEASES_URL, "SWR Client", ct));
        }
        catch { }
        try
        {
            items.AddRange(await FetchNewsFromRepo(GH_APWORLD_RELEASES_URL, "SWR APWorld", ct));
        }
        catch { }
        items.Sort((a, b) => b.Date.CompareTo(a.Date));
        return items.Count > 10 ? items.GetRange(0, 10).ToArray() : items.ToArray();
    }

    private async Task<List<NewsItem>> FetchNewsFromRepo(
        string releasesUrl, string label, CancellationToken ct)
    {
        string json = await _http.GetStringAsync(releasesUrl, ct);
        using var doc  = JsonDocument.Parse(json);
        var result = new List<NewsItem>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            DateTimeOffset date = DateTimeOffset.MinValue;
            if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                DateTimeOffset.TryParse(d.GetString(), out date);

            string name = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            result.Add(new NewsItem(
                Title:   string.IsNullOrWhiteSpace(name) ? label : name,
                Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                Version: el.TryGetProperty("tag_name", out var t) ? NormalizeTag(t.GetString()) ?? "" : "",
                Date:    date,
                Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
            ));
            if (result.Count >= 10) break;
        }
        return result;
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

    /// Resolve the latest SWR AP Client release: version + Windows asset URL +
    /// apworld asset URL + apworld filename. Falls back to a pinned version when
    /// the primary API is unreachable.
    private async Task<(string Version, string? ZipUrl, string? ApWorldUrl, string? ApWorldName)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_CLIENT_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    // Skip draft releases.
                    if (rel.TryGetProperty("draft", out var dr) &&
                        dr.ValueKind == JsonValueKind.True)
                        continue;

                    string? version = rel.TryGetProperty("tag_name", out var t)
                        ? NormalizeTag(t.GetString())
                        : null;
                    if (version == null) continue;

                    if (rel.TryGetProperty("assets", out var assets) &&
                        assets.ValueKind == JsonValueKind.Array)
                    {
                        var (zip, apworld, apworldName) = PickWindowsAssets(assets);
                        if (zip != null)
                            return (version, zip, apworld, apworldName);
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable — fall through to pinned fallback */ }

        // Pinned fallback: construct a direct URL pattern that matches the common
        // convention (update FallbackClientVersion on every verified release bump).
        string fallbackZip =
            "https://github.com/" + CLIENT_GITHUB_OWNER + "/" + CLIENT_GITHUB_REPO +
            "/releases/download/v" + FallbackClientVersion +
            "/SWR_AP_Client-win64.zip";
        return (FallbackClientVersion, fallbackZip, null, null);
    }

    /// From a release's assets JSON array, pick the Windows zip/exe and the
    /// apworld asset.  Asset names vary by release so match broadly.
    private static (string? Zip, string? ApWorld, string? ApWorldName)
        PickWindowsAssets(JsonElement assets)
    {
        string? zip = null, apworld = null, apworldName = null;
        string? anyExe = null;

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
                continue;
            }

            // Skip source, linux, mac assets.
            if (lower.Contains("source") || lower.Contains("linux") ||
                lower.Contains("ubuntu") || lower.Contains("mac") ||
                lower.Contains("darwin"))
                continue;

            if (lower.EndsWith(".zip"))
            {
                if (zip == null &&
                    (lower.Contains("win") || lower.Contains("x64") || lower.Contains("x86")))
                    zip = url;
                else
                    anyExe ??= url;  // keep as last-resort
            }
            else if (lower.EndsWith(".exe"))
            {
                // Some small AP clients ship as a single exe, not a zip.
                anyExe ??= url;
            }
        }

        zip ??= anyExe;
        return (zip, apworld, apworldName);
    }

    // ── Private helpers — exe resolution ──────────────────────────────────────

    /// Find the installed SWR AP Client executable.
    private string? ResolveClientExe()
    {
        if (!Directory.Exists(GameDirectory)) return null;
        // Preferred: the well-known name.
        string preferred = Path.Combine(GameDirectory, "SWR_AP_Client.exe");
        if (File.Exists(preferred)) return preferred;
        // Fuzzy: any exe with "swr" or "ap" in the name, avoiding installer
        // and uninstaller stubs.
        try
        {
            foreach (string exe in Directory.EnumerateFiles(GameDirectory, "*.exe"))
            {
                string nameLower = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (nameLower.Contains("unins") || nameLower.Contains("setup")) continue;
                if (nameLower.Contains("swr") || nameLower.Contains("racer"))
                    return exe;
            }
        }
        catch { }
        return null;
    }

    /// Try to find the original game executable by Steam registry, then the
    /// user-saved path from settings.
    private string? DetectGameExe()
    {
        // 1. User-saved path from settings.
        string? savedDir = LoadSettings().GameDirectory;
        if (!string.IsNullOrEmpty(savedDir))
        {
            string? found = FindGameExeIn(savedDir);
            if (found != null) return found;
        }

        // 2. Steam registry: "InstallLocation" under the app uninstall key.
        try
        {
            object? loc = Microsoft.Win32.Registry.GetValue(SteamAppsRegKey, "InstallLocation", null);
            if (loc is string dir && Directory.Exists(dir))
            {
                string? found = FindGameExeIn(dir);
                if (found != null) return found;
            }
        }
        catch { }

        // 3. Common default Steam library path.
        string defaultSteamPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam", "steamapps", "common", "STAR WARS Episode I Racer");
        if (Directory.Exists(defaultSteamPath))
        {
            string? found = FindGameExeIn(defaultSteamPath);
            if (found != null) return found;
        }

        return null;
    }

    private static string? FindGameExeIn(string dir)
    {
        // Known exe names for the remastered Steam/GOG release and the original.
        string[] candidates = new[] { "SWEP1RCR.exe", "sw_ep1racer.exe", "racer.exe" };
        foreach (string name in candidates)
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        string? gameExe = DetectGameExe();
        if (gameExe != null)
        {
            // Direct launch — we know exactly where the game lives.
            var psi = new ProcessStartInfo
            {
                FileName         = gameExe,
                WorkingDirectory = Path.GetDirectoryName(gameExe) ?? "",
                UseShellExecute  = false,
            };
            try
            {
                var proc = Process.Start(psi);
                if (proc != null)
                {
                    _gameProcess = proc;
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (_, _) =>
                    {
                        // If the client is still running after the game exits, kill it.
                        try { _clientProcess?.Kill(entireProcessTree: true); } catch { }
                        IsRunning = false;
                        GameExited?.Invoke(proc.ExitCode);
                    };
                    return;
                }
            }
            catch { /* fall through to Steam URI */ }
        }

        // Fallback: launch via the Steam URI protocol (requires Steam to be running).
        try
        {
            Process.Start(new ProcessStartInfo("steam://rungameid/" + SteamAppId)
            {
                UseShellExecute = true,
            });
            IsRunning = true;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Could not find or launch Star Wars Episode I: Racer. " +
                "Make sure the game is installed (Steam App ID 808910) and pick " +
                "its install folder in Settings.", ex);
        }
    }

    /// Build the AP client command-line arguments.
    /// Uses the common pattern shared by most AP external clients.
    /// Verify against the actual SWR_AP_Client --help output when available.
    private static string BuildClientArguments(ApSession session)
    {
        var (host, port) = ParseServerHostPort(session.ServerUri);
        string server = host + ":" + port;

        // Common AP client CLI convention: --server and --name.
        // If the client uses different flag names, update here.
        var sb = new System.Text.StringBuilder();
        sb.Append("--server ").Append(Quote(server));
        sb.Append(" --name ").Append(Quote(session.SlotName));
        if (!string.IsNullOrEmpty(session.Password))
            sb.Append(" --password ").Append(Quote(session.Password));

        return sb.ToString();
    }

    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        bool needs = value.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0;
        if (!needs) return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    private void StartClientProcess(string exePath, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? GameDirectory,
            UseShellExecute  = false,
        };
        if (!string.IsNullOrEmpty(arguments))
            psi.Arguments = arguments;

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the SWR AP Client.");

        _clientProcess = proc;
        IsRunning      = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            // Client exited — consider the session over if the game is also gone.
            if (_gameProcess == null || _gameProcess.HasExited)
            {
                IsRunning = false;
                GameExited?.Invoke(proc.ExitCode);
            }
        };
    }

    /// Parse "host:port", "ws://host:port", "wss://host:port", or bare hostname.
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
        return (host, port);
    }

    // ── Private helpers — download/extract ────────────────────────────────────

    private async Task DownloadAndExtractClientAsync(
        string downloadUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        bool isZip = downloadUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        string tempFile = Path.Combine(Path.GetTempPath(),
            "swe1r_apclient_" + version + "_" + Guid.NewGuid().ToString("N") +
            (isZip ? ".zip" : ".exe"));

        try
        {
            progress.Report((5, "Downloading SWR AP Client " + version + "..."));
            using var response = await _http.GetAsync(
                downloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempFile))
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
                        progress.Report((pct, "Downloading SWR AP Client... " + downloaded / 1024 + " KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Installing SWR AP Client..."));
            Directory.CreateDirectory(GameDirectory);

            if (isZip)
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(tempFile, GameDirectory, overwriteFiles: true);

                // Flatten a single wrapper sub-folder if the exe is not at the root.
                if (ResolveClientExe() == null)
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
            }
            else
            {
                // Single-exe distribution — copy directly into GameDirectory.
                string dest = Path.Combine(GameDirectory, "SWR_AP_Client.exe");
                File.Copy(tempFile, dest, overwrite: true);
            }

            progress.Report((80, "SWR AP Client extracted."));
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    // ── Private helpers — settings sidecar ────────────────────────────────────

    private sealed class Swe1rSettings
    {
        public string? GameDirectory { get; set; }
    }

    private Swe1rSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Swe1rSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(Swe1rSettings s)
    {
        try
        {
            Directory.CreateDirectory(GameDirectory);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(false));
        }
        catch { }
    }
}
