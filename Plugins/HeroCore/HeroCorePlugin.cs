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
// System.Windows.TextWrapping, ...) to avoid CS0104 ambiguities. Do NOT add
// `using System.Windows.Controls;` / `using System.Windows.Media;` here.

namespace LauncherV2.Plugins.HeroCore;

// ===============================================================================
// HeroCorePlugin — install / update / launch for "Hero Core" (Daniel Remar, 2010,
// freeware) played through the HeroCore-Archipelago randomizer by MinishLink.
// This is a NATIVE "ConnectsItself" integration: the patched herocore.exe ships
// gm-apclientpp.dll and has its own built-in AP client. The player presses
// Connect on the main menu — no Lua bridge, no emulator, no separate AP client
// exe. The launcher writes ConnectionInfo.ini and launches herocore.exe.
//
// REALITY CHECK (2026-06-14) — every fact below verified this session
// -------------------------------------------------------------------------------
//   * REPO (verified online):
//       Minish-Link/HeroCore-Archipelago
//       "Archipelago Multi-Game Randomizer for Hero Core"
//
//   * AP WORLD: game string "Hero Core" — verified against __init__.py:
//       class HeroCoreWorld(World):
//           game = "Hero Core"
//     This is NOT a core Archipelago world. It ships as an .apworld in the
//     GitHub releases. Latest release at time of writing: "v1.0.4"
//     (tagged 2025), assets: herocore.apworld, Hero.Core.yaml,
//     HeroCoreRandomizerv1-0-2.zip, HeroCoreRandoSourcev1-0-2.zip.
//
//   * WHAT THE DOWNLOAD CONTAINS (verified by extracting HeroCoreRandomizerv1-0-2.zip
//     this session — single sub-folder "herocore_randomizer/"):
//       herocore.exe               — patched GameMaker game binary
//       gm-apclientpp.dll          — native Archipelago client library (embedded)
//       fmod.dll                   — GameMaker audio
//       jbfmod.dll                 — GameMaker audio
//       ConnectionInfo.ini         — AP connection config
//       README.md
//       saves/                     — save files (pre-bundled developer saves)
//     No commercial assets: Hero Core is FREEWARE. The download is the complete
//     game — no bring-your-own-asset gate.
//
//   * CONNECTION CONFIG (verified by reading ConnectionInfo.ini from the real zip):
//     Exact INI format (Windows-standard, no external parser needed):
//         [Server]
//         Address=archipelago.gg:38281
//         SlotName=FlipHero
//         Password=
//     "Address" is host:port COMBINED. "Password" can be empty. The game reads
//     this file on startup and uses it when the user clicks Connect on the main
//     menu (README: "change the values in the ConnectionInfo.ini file to match
//     your slot name, server address/port, and password (if there is one), then
//     launch the game and press Connect on the main menu").
//
//   * ConnectsItself = true: herocore.exe + gm-apclientpp.dll form the AP client.
//     The launcher must NOT hold its own ApClient on the same slot while the game
//     runs — the two would kick each other endlessly. The launcher writes the ini,
//     launches the exe, and suppresses auto-reconnect until the game exits.
//
//   * MUSIC: the README notes the music files are NOT included in the download
//     (licensing). The user can copy music files from the original freeware game
//     (https://remar.se/daniel/herocore.php) into the randomizer's music/ folder.
//     The launcher surfaces this note in the settings panel but does not automate
//     the music copy (the original host is a personal site, not a stable API).
//
//   * RELEASES: MinishLink uses standard (non-prerelease) GitHub releases, so
//     /releases/latest works. The game zip is "HeroCoreRandomizerv<A>-<B>-<C>.zip"
//     (version components hyphen-separated, not dots — e.g. v1-0-2 for version
//     1.0.2). Pinned fallback: v1.0.4 / HeroCoreRandomizerv1-0-2.zip.
//
//   * APWORLD: releases also ship "herocore.apworld". This plugin downloads it
//     alongside the game and saves it next to the install for the user to place
//     in Archipelago's custom_worlds folder (same pattern as other plugins where
//     the world is not yet in AP-main).
// ===============================================================================

public sealed class HeroCorePlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER = "Minish-Link";
    private const string GITHUB_REPO  = "HeroCore-Archipelago";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"{GH_RELEASES_URL}/latest";

    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Hero%20Core/setup_en";
    private const string OriginalGameUrl =
        "https://remar.se/daniel/herocore.php";

    // Pinned offline fallback — the verified release at time of writing.
    // Asset naming: "HeroCoreRandomizerv<A>-<B>-<C>.zip" where version is v1.0.4
    // but the zip ships the 1.0.2 build (the tag and zip version differ; use the
    // download URL verbatim as verified).
    private const string FallbackVersion    = "1.0.4";
    private const string FallbackTag        = "v1.0.4";
    private const string FallbackZipName    = "HeroCoreRandomizerv1-0-2.zip";
    private static readonly string FallbackZipUrl =
        $"{RepoUrl}/releases/download/{FallbackTag}/{FallbackZipName}";
    private const string FallbackApWorldName = "herocore.apworld";
    private static readonly string FallbackApWorldUrl =
        $"{RepoUrl}/releases/download/{FallbackTag}/{FallbackApWorldName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp, written after a successful install.
    private const string VersionFileName = "herocore_ap_version.dat";

    /// Verified connection config file read by herocore.exe on startup.
    private const string ConnectionIniFileName = "ConnectionInfo.ini";

    /// The patched game exe (verified from the real zip).
    private const string GameExeFileName = "herocore.exe";

    /// Default Archipelago server port.
    private const int DefaultApPort = 38281;

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "herocore";
    public string DisplayName => "Hero Core";
    public string Subtitle    => "Native PC · built-in Archipelago";

    /// EXACT AP game string — verified against __init__.py (HeroCoreWorld.game).
    public string ApWorldName => "Hero Core";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "herocore.png");

    public string ThemeAccentColor => "#3A6B9F";   // steel blue, matching the game's aesthetic
    public string[] GameBadges     => new[] { "Free · freeware" };

    public string Description =>
        "Hero Core is a freeware action-exploration shooter by Daniel Remar (2010). " +
        "You play as Flip Hero on a mission to destroy the robotic villain CRUISER " +
        "TETRON. This is the Archipelago randomizer build by MinishLink: generators, " +
        "powerups, level-ups, computers, doors and save points are shuffled into the " +
        "multiworld, and the game connects to the Archipelago server through its own " +
        "built-in client — no emulator and no separate AP client exe needed. Because " +
        "Hero Core is freeware, the download is the complete game: just install and " +
        "press Play. The music files are not included in the download due to licensing; " +
        "copy them from the original freeware game if you want music.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => File.Exists(PreferredExePath);
    public bool    IsRunning        { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the Hero Core Archipelago build is installed.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "HeroCore");

    private string PreferredExePath    => Path.Combine(GameDirectory, GameExeFileName);
    private string ConnectionIniPath   => Path.Combine(GameDirectory, ConnectionIniFileName);
    private string VersionFilePath     => Path.Combine(GameDirectory, VersionFileName);

    /// Where the downloaded apworld is saved for the user to copy into
    /// Archipelago's custom_worlds folder.
    private string ApWorldLocalPath => Path.Combine(GameDirectory, "herocore.apworld");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // herocore.exe's native AP client reports checks/items/goal directly to the
    // server — the launcher relays nothing. These exist for interface compatibility
    // (ConnectsItself = true).
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
        // 1. Resolve the latest release (pinned fallback when offline).
        progress.Report((2, "Checking latest Hero Core (Archipelago) release..."));
        var (version, zipUrl, apworldUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        // 2. Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"Hero Core (Archipelago) {version} is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a Windows download for Hero Core on the GitHub release " +
                "page. Check your internet connection, or download the build manually " +
                "from " + RepoUrl + "/releases.");

        // 3. Download + extract the build.
        await DownloadAndExtractGameAsync(zipUrl, version, progress, ct);

        // 4. Fetch the apworld alongside the install so the user can place it in
        //    Archipelago's custom_worlds folder (Hero Core is not in AP-main).
        if (apworldUrl != null)
        {
            try
            {
                progress.Report((85, "Downloading herocore.apworld..."));
                byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                await File.WriteAllBytesAsync(ApWorldLocalPath, apworld, ct);
                progress.Report((92,
                    "herocore.apworld saved next to the game — copy it into " +
                    "Archipelago's custom_worlds folder if you generate with this build."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((92,
                    "Could not download the apworld. Download herocore.apworld " +
                    "manually from " + RepoUrl + "/releases."));
            }
        }

        // 5. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"Hero Core (Archipelago) {version} ready. Press Play to connect — " +
            "the launcher fills in ConnectionInfo.ini automatically."));
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
        if (!File.Exists(PreferredExePath))
            throw new FileNotFoundException(
                "Hero Core is not installed. Click Install Game first.",
                PreferredExePath);

        // VERIFIED connection path: write ConnectionInfo.ini (documented as the
        // sole connection mechanism — README: "change the values in the
        // ConnectionInfo.ini file ... then launch the game and press Connect").
        // Format verified from the real zip:
        //   [Server]
        //   Address=host:port
        //   SlotName=name
        //   Password=pw_or_empty
        try { WriteConnectionIni(session); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        StartGameProcess(PreferredExePath);
        return Task.CompletedTask;
    }

    /// Hero Core (freeware) is a complete game — plain non-AP play is supported.
    public bool SupportsStandalone => true;

    /// herocore.exe + gm-apclientpp.dll own the AP slot connection. The launcher
    /// must NOT hold its own ApClient on the same slot while the game runs.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        if (!File.Exists(PreferredExePath))
            throw new FileNotFoundException(
                "Hero Core is not installed. Click Install Game first.",
                PreferredExePath);

        // For a standalone (non-AP) launch, blank out the Address and SlotName so
        // the game cannot accidentally connect to a stale session. The game only
        // connects when the user presses Connect on the main menu.
        try { BlankConnectionIni(); } catch { }

        StartGameProcess(PreferredExePath);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // Scrub the password from ConnectionInfo.ini now the session is over.
        ScrubConnectionIniPassword();
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // Hero Core's native client receives items from the AP server directly —
        // nothing to forward from the launcher.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // Hero Core renders its own AP status in-game; no launcher HUD channel.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var panel   = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        // ── Section: Install directory ────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "INSTALL DIRECTORY", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text       = GameDirectory, IsReadOnly = true, FontSize = 12,
            Margin     = new System.Windows.Thickness(0, 0, 8, 0),
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
                Title            = "Select Hero Core (Archipelago) install folder",
                InitialDirectory = Directory.Exists(GameDirectory)
                                   ? GameDirectory : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(
            dirBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = IsInstalled
                ? "✓ Hero Core (Archipelago) is installed"
                : "Not installed (click Install in the Play tab)",
            FontSize   = 11,
            Foreground = IsInstalled ? success : muted,
            Margin     = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Archipelago connection ───────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "ARCHIPELAGO CONNECTION", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Hero Core reads its connection from ConnectionInfo.ini in the game " +
                "folder (Address=host:port, SlotName, Password). The launcher writes " +
                "this file for you each time you press Play, then runs herocore.exe. " +
                "On the main menu press Connect — the game connects to the Archipelago " +
                "server using its built-in client (gm-apclientpp.dll). You can still " +
                "edit ConnectionInfo.ini by hand; the launcher overwrites it on each " +
                "Play click with your session credentials.",
            FontSize     = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Music note ───────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "MUSIC", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "The randomizer does not include music files due to licensing. To play " +
                "with music, download the original Hero Core from the author's site " +
                "(see link below) and copy its music files into the music/ folder " +
                "inside the randomizer install.",
            FontSize     = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Links ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "LINKS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Hero Core Archipelago (GitHub) ↗", RepoUrl),
            ("Original Hero Core (freeware) ↗",  OriginalGameUrl),
            ("Archipelago Official ↗",           "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding         = new System.Windows.Thickness(0, 2, 0, 2),
                Background      = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0),
                FontSize        = 12,
                Margin          = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground      = new System.Windows.Media.SolidColorBrush(
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
                if (el.TryGetProperty("published_at", out var d)
                    && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t)
                             ? NormalizeTag(t.GetString()) ?? "" : "",
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

    /// "v1.0.4" -> "1.0.4". Returns null for null/blank.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest release: version + game zip URL + apworld URL.
    /// MinishLink publishes normal (non-prerelease) releases, so /releases/latest
    /// works. Falls back to the pinned v1.0.4 details when the API is unreachable.
    private async Task<(string Version, string? ZipUrl, string? ApWorldUrl)>
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
                var (zip, apworld) = PickAssets(assets);
                if (zip != null)
                    return (version, zip, apworld);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited -> pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl, FallbackApWorldUrl);
    }

    /// From a release's assets array, pick the game zip and apworld URL.
    /// Game zip matches "herocorerandomizerv*.zip" (case-insensitive).
    /// ApWorld matches "herocore.apworld".
    private static (string? Zip, string? ApWorld) PickAssets(JsonElement assets)
    {
        string? zip = null, apworld = null;

        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u)
                           ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();

            if (lower.EndsWith(".apworld") && lower.Contains("herocore"))
                apworld ??= url;
            else if (lower.EndsWith(".zip") &&
                     lower.Contains("herocore") &&
                     lower.Contains("randomizer") &&
                     !lower.Contains("source"))
                zip ??= url;
        }

        return (zip, apworld);
    }

    // ── Private helpers — ConnectionInfo.ini ──────────────────────────────────

    /// Write ConnectionInfo.ini with the session's credentials.
    /// VERIFIED format from the real zip:
    ///   [Server]
    ///   Address=host:port
    ///   SlotName=name
    ///   Password=pw_or_empty
    private void WriteConnectionIni(ApSession session)
    {
        Directory.CreateDirectory(GameDirectory);

        var (host, port) = ParseServerHostPort(session.ServerUri);
        string address   = $"{host}:{port}";

        // Simple Windows INI write — no external parser needed for this shape.
        var sb = new StringBuilder();
        sb.AppendLine("[Server]");
        sb.Append("Address=").AppendLine(address);
        sb.Append("SlotName=").AppendLine(session.SlotName ?? "");
        sb.Append("Password=").AppendLine(session.Password ?? "");

        File.WriteAllText(ConnectionIniPath, sb.ToString(),
            new UTF8Encoding(false));   // BOM-less UTF-8
    }

    /// Blank the Address and SlotName for a standalone (non-AP) launch so the
    /// game cannot connect to a stale session.
    private void BlankConnectionIni()
    {
        Directory.CreateDirectory(GameDirectory);
        var sb = new StringBuilder();
        sb.AppendLine("[Server]");
        sb.AppendLine("Address=");
        sb.AppendLine("SlotName=");
        sb.AppendLine("Password=");
        File.WriteAllText(ConnectionIniPath, sb.ToString(),
            new UTF8Encoding(false));
    }

    /// Clear the Password field once the session ends so the plaintext password
    /// does not outlive the session on disk. Best effort.
    private void ScrubConnectionIniPassword()
    {
        try
        {
            if (!File.Exists(ConnectionIniPath)) return;

            string text = File.ReadAllText(ConnectionIniPath);
            // Replace "Password=<anything>" with "Password=" on its own line.
            var sb    = new StringBuilder();
            bool done = false;
            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.TrimEnd('\r');
                if (!done && trimmed.StartsWith("Password=",
                        StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("Password=");
                    done = true;
                }
                else
                {
                    sb.AppendLine(trimmed);
                }
            }
            File.WriteAllText(ConnectionIniPath, sb.ToString(),
                new UTF8Encoding(false));
        }
        catch { /* best effort — next Play launch overwrites anyway */ }
    }

    // ── Private helpers — server URI parsing ──────────────────────────────────

    /// Accepts "archipelago.gg:38281", "ws://host:port", "wss://host:port", a
    /// bare hostname, and IPv6 literals ("[::1]:38281" or bare "::1").
    /// Returns the bare host (no brackets) and port (default 38281).
    private static (string Host, int Port) ParseServerHostPort(string serverUri)
    {
        string s = (serverUri ?? "").Trim();
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
        int    port = DefaultApPort;

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
            host = s; // bare IPv6 literal — no port carried this way
        }
        else
        {
            int colon = s.LastIndexOf(':');
            if (colon > 0 &&
                int.TryParse(s[(colon + 1)..], out int p) &&
                p > 0 && p <= 65535)
            {
                host = s[..colon];
                port = p;
            }
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
        }) ?? throw new InvalidOperationException("Failed to start Hero Core.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            ScrubConnectionIniPassword();   // session over — blank the password
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — download/extract ────────────────────────────────────

    private async Task DownloadAndExtractGameAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"herocore-ap-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading Hero Core (Archipelago) {version}..."));
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
                            $"Downloading Hero Core... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting..."));
            Directory.CreateDirectory(GameDirectory);
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, GameDirectory, overwriteFiles: true);

            // The zip ships a single wrapper sub-folder ("herocore_randomizer/").
            // Flatten it so herocore.exe lands directly in GameDirectory.
            if (!File.Exists(PreferredExePath))
            {
                string[] subdirs = Directory.GetDirectories(GameDirectory);
                if (subdirs.Length == 1)
                {
                    string sub = subdirs[0];
                    foreach (string fileSrc in
                        Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
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
