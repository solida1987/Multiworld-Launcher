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

// IMPORTANT (real project has <UseWindowsForms>true</UseWindowsForms>):
// WPF UI types that collide with WinForms are FULLY QUALIFIED below
// (System.Windows.Controls.*, System.Windows.Media.*, System.Windows.Thickness,
// System.Windows.FontWeights, System.Windows.HorizontalAlignment,
// System.Windows.TextWrapping, …) to avoid CS0104 ambiguities. Do NOT add
// `using System.Windows.Controls;` / `using System.Windows.Media;` here.
using LauncherV2.Core;

namespace LauncherV2.Plugins.Meritous;

// ═══════════════════════════════════════════════════════════════════════════════
// MeritousPlugin — install / update / launch for "Meritous" (Meritous Gaiden)
// played through its Archipelago build. This is a NATIVE "ConnectsItself"
// integration (NOT a BizHawk / Lua emulator game): the game speaks to the AP
// server itself, exactly like Celeste 64, Ship of Harkinian, the OpenTTD
// Archipelago fork, and APDOOM.
//
// This is the CLEANEST kind of native plugin — there is NO bring-your-own-asset
// gate. Meritous (originally by Lancer-X / Asceai) is FREE, open-source GPL
// freeware, so the Archipelago build is a fully self-contained Windows download:
// install = a single GitHub-release download + launch. It is therefore modelled
// directly on Plugins/Celeste64/Celeste64Plugin.cs (the closest sibling: a clean
// GitHub-release native with a JSON connection file and no asset gate), with the
// config-file shape adapted to Meritous's own format.
//
// REALITY CHECK (2026-06-14) — facts verified this session
// ─────────────────────────────────────────────────────────────────────────────
//   * REPO (verified online + the official AP "Meritous" setup guide and the AP
//     world's own bug_report_page in worlds/meritous/__init__.py):
//       FelicitusNeko/meritous-ap — "Meritous Gaiden" (Lancer-X's dungeon
//       crawler, modified for Archipelago Multiworld + a local itemizer).
//     Releases are NORMAL (non-prerelease) GitHub releases, so /releases/latest
//     works. Latest at time of writing: tag "v1.4.2" (2025-05-18). Each release
//     ships BOTH a 32-bit and a 64-bit Windows zip, verified across releases:
//       "meritous-ap-<ver>-win64.zip"  (preferred)
//       "meritous-ap-<ver>-win32.zip"  (fallback)
//     (e.g. "meritous-ap-1.4.2-win64.zip"). The exe inside the zip is
//     "meritous.exe" (verified from the setup guide's run step). v1.4.2 win64 is
//     pinned as the offline fallback so install still works when the GitHub API
//     is unreachable.
//
//   * HOW IT CONNECTS (VERIFIED, verbatim, against the official Archipelago
//     "Meritous" setup guide — https://archipelago.gg/tutorial/Meritous/setup_en):
//     the AP connection is provided by a CONFIG FILE named "meritous-ap.json" in
//     the ROOT of the install, holding exactly these fields (note the EXACT
//     shape, which differs from Celeste 64's AP.json — host and port are SEPARATE
//     fields, and an absent password is JSON null, not ""):
//         {
//             "ap-enable": true,
//             "server":    "archipelago.gg",
//             "port":      38281,
//             "password":  null,
//             "slotname":  "YourName"
//         }
//     The player edits meritous-ap.json, then runs meritous.exe; if the file is
//     detected, "AP Enabled" shows in the bottom-left of the menu screen, and on
//     a successful connect "Connected" shows in the bottom-left of the game
//     screen for a few seconds. There is NO in-game connection menu and NO
//     command line — the file IS the documented interface. This plugin writes
//     meritous-ap.json on launch (read-modify-write so manual edits / extra keys
//     survive), so the player never has to touch the file. AP servers allow one
//     connection per slot, so — like Celeste 64 / SoH / OpenTTD / APDOOM — the
//     launcher must NOT hold its own ApClient on the same slot while the game
//     runs: ConnectsItself = true.
//
//   * THE AP WORLD: game string "Meritous" — verified against AP-main
//     worlds/meritous/__init__.py (MeritousWorld.game = "Meritous", line 38 of
//     the local checkout in mk64src/worlds/meritous/). World id "meritous".
//     No .apworld is published on the meritous-ap releases (none of v0.5…v1.4.2
//     ship one) — AP-main already bundles the stable Meritous world, so there is
//     nothing to fetch alongside the install (unlike Celeste 64 / APDOOM which
//     ship their world as a release asset). This plugin therefore does NOT try to
//     download an apworld; the resolver still picks one up opportunistically if a
//     future release ever adds one.
//
//   * NO ASSET GATE: the base game is free + open source, so the AP build ships
//     a COMPLETE, playable game. There is no IWAD/ROM to supply, no first-run
//     extractor, no asset-library copy. Plain non-AP play is supported
//     (SupportsStandalone = true) — just launch with "ap-enable" set false (we
//     write that for a standalone launch) so the game runs vanilla Meritous.
//
// DEFENSIVE / UNVERIFIED DETAILS (flagged, "verify at build time"):
//   * The exact zip CONTENTS were not inspected offline. ResolveGameExe()
//     prefers "meritous.exe", then any "*meritous*" exe in the install (fuzzy),
//     skipping obvious helper/uninstaller exes. Single-subdir extracts are
//     flattened so the exe lands at the install root.
//   * meritous-ap.json is written DEFENSIVELY: if the build ever renames a key,
//     the in-game/guide path still works (the player edits the file once); we
//     never clobber unknown keys (read-modify-write). The post-install note is
//     honest that meritous-ap.json is the connection mechanism.
//   * The install dir is the only launcher-side setting that matters; this plugin
//     keeps any optional setting in its OWN JSON sidecar
//     (Games/ROMs/meritous/meritous_launcher.json) rather than modifying the
//     shared Core/SettingsStore — it is added as a single self-contained file.
//     (The Celeste 64 / Doom plugins took the same self-contained-sidecar
//     approach.)
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MeritousPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER = "FelicitusNeko";
    private const string GITHUB_REPO  = "meritous-ap";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"{GH_RELEASES_URL}/latest";

    /// Official Archipelago "Meritous" setup guide.
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Meritous/setup_en";

    // Pinned fallback — the latest non-prerelease at time of writing, with the
    // verified asset-name pattern (64-bit). Used ONLY when the GitHub API is
    // unreachable so a fresh install still works offline-of-the-API.
    private const string FallbackVersion = "1.4.2";
    private const string FallbackZipName = "meritous-ap-1.4.2-win64.zip";
    private static readonly string FallbackZipUrl =
        $"{RepoUrl}/releases/download/v{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp, written after a successful install.
    private const string VersionFileName = "meritous_ap_version.dat";

    /// The verified AP connection file name, in the install root.
    private const string ApConfigFileName = "meritous-ap.json";

    /// Default Archipelago port (used when the server URI carries no port).
    private const int DefaultApPort = 38281;

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "meritous";
    public string DisplayName => "Meritous";
    public string Subtitle    => "Native PC · built-in Archipelago";

    /// EXACT AP game string — verified against worlds/meritous/__init__.py
    /// (MeritousWorld.game = "Meritous").
    public string ApWorldName => "Meritous";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "meritous.png");

    public string ThemeAccentColor => "#7A3FA0";   // arcane violet
    public string[] GameBadges     => new[] { "Free · open source" };

    public string Description =>
        "Meritous Gaiden is a free, open-source procedurally generated bullet-hell " +
        "dungeon crawler by Lancer-X / Asceai. This is the Archipelago build: it " +
        "ships its own built-in multiworld client, so PSI upgrades, artifacts, PSI " +
        "Keys and crystals are shuffled into the multiworld and the game connects " +
        "to the Archipelago server itself — no emulator, no Lua bridge. Because the " +
        "base game is free and open source, the download is the complete game: " +
        "nothing to bring, nothing to extract. The launcher fills in your " +
        "connection details and launches you straight in.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => ResolveGameExe() != null;
    public bool    IsRunning        { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the Meritous Archipelago build is installed.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Meritous");

    /// Preferred exe (verified name). Resolution falls back to a fuzzy match.
    private string PreferredExePath => Path.Combine(GameDirectory, "meritous.exe");

    /// The AP connection file in the install root (verified name).
    private string ApConfigPath => Path.Combine(GameDirectory, ApConfigFileName);

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore
    /// so the plugin stays a single self-contained source file). Lives under the
    /// launcher's ROM-library tree for consistency with the other native plugins,
    /// even though this game brings no ROM.
    private string SettingsSidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                        "meritous_launcher.json");

    private string SettingsSidecarDir => Path.GetDirectoryName(SettingsSidecarPath)!;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // Meritous's native AP client reports checks/items/goal to the AP server
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
        progress.Report((2, "Checking latest Meritous (Archipelago) release..."));
        var (version, zipUrl, apworldUrl, apworldName) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;
        _apWorldFileName = apworldName;

        // 2. Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"Meritous (Archipelago) {version} is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a Windows download for Meritous on the GitHub " +
                "release page. Check your internet connection, or download the " +
                "build manually from " + RepoUrl + "/releases.");

        // 3. Download + extract the build.
        await DownloadAndExtractGameAsync(zipUrl, version, progress, ct);

        // 4. Fetch the apworld next to the install IF the release ships one
        //    (Meritous releases historically do NOT — AP-main bundles the world —
        //    so this is opportunistic, for a future release that might add one).
        if (apworldUrl != null)
        {
            try
            {
                progress.Report((85, "Downloading the Meritous apworld..."));
                byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                await File.WriteAllBytesAsync(ApWorldLocalPath, apworld, ct);
                progress.Report((92, $"{Path.GetFileName(ApWorldLocalPath)} saved — copy it into Archipelago's custom_worlds folder if you generate with this build."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((92, "Could not download the apworld — the stable Meritous world also ships with Archipelago."));
            }
        }

        // 5. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"Meritous (Archipelago) {version} ready. Press Play to connect — " +
            "the launcher fills in your meritous-ap.json automatically."));
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
                "Meritous is not installed. Click Install Game first.",
                PreferredExePath);

        // VERIFIED connection path: write the documented meritous-ap.json file
        // (ap-enable / server / port / password / slotname) into the install root
        // so the game connects on launch. Read-modify-write so any manual edits /
        // extra keys survive. Best effort — never blocks the launch.
        try { WriteApConfig(session); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }

        StartGameProcess(exe);
        return Task.CompletedTask;
    }

    /// Meritous is a complete game — plain (non-AP) play is supported.
    public bool SupportsStandalone => true;

    /// Meritous's native in-game AP client owns the slot connection (see header).
    /// The launcher must not connect its own ApClient to the same slot while the
    /// game runs.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        string exe = ResolveGameExe()
            ?? throw new FileNotFoundException(
                "Meritous is not installed. Click Install Game first.",
                PreferredExePath);

        // No AP — plain Meritous. Disable AP in the config so a previous session's
        // slot/password does not silently auto-connect (the game keys off
        // "ap-enable"). Best effort; non-AP keys are preserved.
        try { DisableApConfig(); } catch { }

        StartGameProcess(exe);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // The plaintext room password lives in meritous-ap.json on disk — scrub
        // it once the session ends so it does not outlive the game.
        ScrubApConfigPassword();
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // Meritous's native client receives items from the AP server directly;
        // there is nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // Meritous renders its own AP status in-game ("AP Enabled" / "Connected"
        // in the bottom-left); no launcher HUD channel.
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
            Text = "INSTALL DIRECTORY", FontSize = 10,
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
                Title            = "Select Meritous install folder",
                InitialDirectory = Directory.Exists(GameDirectory) ? GameDirectory
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = IsInstalled ? "✓ Meritous (Archipelago) is installed"
                                     : "Not installed (click Install in the Play tab)",
            FontSize   = 11, Foreground = IsInstalled ? success : muted,
            Margin     = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: How it connects ──────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "ARCHIPELAGO CONNECTION", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Meritous reads its connection from a meritous-ap.json file in the " +
                   "install folder (ap-enable, server, port, password, slotname). The " +
                   "launcher writes this file for you each time you press Play, then runs " +
                   "meritous.exe — when AP is enabled you see \"AP Enabled\" in the " +
                   "bottom-left of the menu, and \"Connected\" once it reaches the server. " +
                   "You can still edit meritous-ap.json by hand; the launcher preserves " +
                   "any extra keys.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "This game is free and open source — there is nothing to bring. The " +
                   "Archipelago build is the complete game, so install is just a download.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Meritous Archipelago (GitHub) ↗", RepoUrl),
            ("Meritous Setup Guide ↗",          SetupGuideUrl),
            ("Archipelago Official ↗",          "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0), FontSize = 12,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
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

    /// "v1.4.2" → "1.4.2" when a leading 'v' decorates a digit; otherwise the tag
    /// is returned as-is (trimmed). Returns null for null/blank tags. (Some
    /// meritous-ap tags are non-semver, e.g. "v1.4-almost", so keep the raw tag.)
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest release: version + Windows zip asset URL + apworld
    /// asset URL + apworld filename. Meritous publishes normal (non-prerelease)
    /// releases, so /releases/latest is the right endpoint. Falls back to the
    /// pinned v1.4.2 win64 direct URL when the API is unreachable.
    private async Task<(string Version, string? ZipUrl, string? ApWorldUrl, string? ApWorldName)>
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
                var (zip, apworld, apworldName) = PickWindowsAndApworld(assets);
                if (zip != null)
                    return (version, zip, apworld, apworldName);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback below */ }

        // Offline fallback: v1.4.2 win64, known asset URL. No apworld direct URL
        // is pinned (AP-main ships the stable world; releases ship none).
        return (FallbackVersion, FallbackZipUrl, null, null);
    }

    /// From a release's assets array, pick the Windows .zip (verified pattern
    /// "meritous-ap-<ver>-win64.zip", preferring 64-bit over 32-bit; match
    /// broadly on win/x64, excluding linux/mac/source) and any meritous apworld.
    private static (string? Zip, string? ApWorld, string? ApWorldName)
        PickWindowsAndApworld(JsonElement assets)
    {
        string? win64 = null, win32 = null, anyWinZip = null, anyZip = null;
        string? apworld = null, apworldName = null;

        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();

            if (lower.EndsWith(".apworld") && lower.Contains("meritous"))
            {
                apworld     = url;
                apworldName = name;
            }
            else if (lower.EndsWith(".zip") &&
                     !lower.Contains("source") &&
                     !lower.Contains("linux") &&
                     !lower.Contains("ubuntu") &&
                     !lower.Contains("mac") &&
                     !lower.Contains("osx") &&
                     !lower.Contains("darwin"))
            {
                anyZip ??= url;   // remember any plausible game zip
                if (lower.Contains("win64") || lower.Contains("win-x64") || lower.Contains("x86_64"))
                    win64 ??= url;
                else if (lower.Contains("win32") || lower.Contains("win-x86"))
                    win32 ??= url;
                else if (lower.Contains("win") || lower.Contains("x64"))
                    anyWinZip ??= url;
            }
        }

        // Prefer 64-bit, then 32-bit, then any windows-tagged zip, then any zip.
        string? zip = win64 ?? win32 ?? anyWinZip ?? anyZip;
        return (zip, apworld, apworldName);
    }

    // ── Private helpers — exe resolution ──────────────────────────────────────

    /// Resolve the installed exe: prefer "meritous.exe", then any "*meritous*"
    /// exe in the install (fuzzy), skipping helper/uninstaller exes. Defensive —
    /// the exact zip contents were not inspected offline.
    private string? ResolveGameExe()
    {
        if (File.Exists(PreferredExePath)) return PreferredExePath;
        if (!Directory.Exists(GameDirectory)) return null;
        try
        {
            foreach (string exe in Directory.EnumerateFiles(GameDirectory, "*.exe", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (name.Contains("unins") || name.Contains("setup") || name.Contains("crash"))
                    continue;
                if (name.Contains("meritous"))
                    return exe;
            }
        }
        catch { /* directory vanished mid-scan */ }
        return null;
    }

    // ── Private helpers — meritous-ap.json connection file (verified) ─────────

    /// Write the documented AP connection file (meritous-ap.json in the install
    /// root) with the verified fields ap-enable / server / port / password /
    /// slotname. Read-modify-write so any manual edits or extra keys the build
    /// adds are preserved. BOM-less UTF-8.
    ///
    /// NOTE the verified shape (differs from Celeste 64's AP.json): "server" is
    /// the HOST ONLY, "port" is a SEPARATE number, and an absent password is JSON
    /// null (not "").
    private void WriteApConfig(ApSession session)
    {
        Directory.CreateDirectory(GameDirectory);

        var (host, port) = ParseServerHostPort(session.ServerUri);

        MutateApConfig(root =>
        {
            root["ap-enable"] = true;
            root["server"]    = host;
            root["port"]      = (long)port;          // JSON number
            root["slotname"]  = session.SlotName;
            // Verified: an absent password is JSON null, not an empty string.
            root["password"]  = string.IsNullOrEmpty(session.Password)
                                    ? null
                                    : session.Password;
        }, createIfMissing: true);
    }

    /// Turn AP off for a standalone (non-AP) launch so a previous session's
    /// slot/password does not silently auto-connect. The game keys off
    /// "ap-enable". Best effort; leaves any non-AP keys intact.
    private void DisableApConfig()
    {
        if (!File.Exists(ApConfigPath)) return;
        MutateApConfig(fields =>
        {
            fields["ap-enable"] = false;
            if (fields.ContainsKey("password")) fields["password"] = null;
        });
    }

    /// Blank just the password field once the session ends — the plaintext room
    /// password should not outlive the session on disk. Best effort; the next AP
    /// launch rewrites the file anyway.
    private void ScrubApConfigPassword()
    {
        try
        {
            if (!File.Exists(ApConfigPath)) return;
            MutateApConfig(fields =>
            {
                if (fields.TryGetValue("password", out var pw) &&
                    pw is string s && !string.IsNullOrEmpty(s))
                    fields["password"] = null;
            });
        }
        catch { /* best effort — the next AP launch overwrites the file anyway */ }
    }

    /// Read meritous-ap.json into a plain mutable map, apply `mutate`, write it
    /// back (BOM-less UTF-8). Preserves every key not touched by the mutator.
    /// When the file is missing/corrupt: if createIfMissing is true we start from
    /// an empty object and write it; otherwise we leave the file alone.
    private void MutateApConfig(Action<Dictionary<string, object?>> mutate,
                                bool createIfMissing = false)
    {
        var root = new Dictionary<string, object?>();
        bool haveFile = false;
        try
        {
            if (File.Exists(ApConfigPath))
            {
                string text = File.ReadAllText(ApConfigPath);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text);
                    if (parsed != null)
                        foreach (var kv in parsed)
                            root[kv.Key] = JsonElementToObject(kv.Value);
                }
                haveFile = true;
            }
        }
        catch
        {
            // Corrupt — start fresh rather than fail the launch (only if allowed).
            root.Clear();
        }

        if (!haveFile && !createIfMissing) return;

        mutate(root);

        File.WriteAllText(ApConfigPath,
            JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    /// Convert a JsonElement to a plain object so it round-trips through
    /// JsonSerializer.Serialize unchanged (used to preserve unknown keys).
    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out long l) ? l : el.GetDouble(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.Null   => null,
        _                    => el.Clone(),   // objects/arrays preserved as-is
    };

    /// Accepts "archipelago.gg:38281", "ws://host:port", "wss://host:port", a
    /// bare hostname, and IPv6 literals (bracketed "[::1]:38281" or bare "::1").
    /// Default AP port is 38281. Returns the host WITHOUT brackets (Meritous's
    /// "server" field wants a bare host; "port" is carried separately).
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
            host = s; // bare IPv6 literal — no port can be carried this way
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

    // ── Private helpers — process ─────────────────────────────────────────────

    private void StartGameProcess(string exePath)
    {
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = GameDirectory,
            UseShellExecute  = false,
        }) ?? throw new InvalidOperationException("Failed to start Meritous.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            ScrubApConfigPassword();   // session over — blank the password field
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // Kept in this plugin's OWN JSON file so it stays a single self-contained
    // source file and does not modify Core/SettingsStore. Reserved for any future
    // launcher-side toggle; the install dir is the only setting that matters
    // today, so the sidecar is currently unused beyond this scaffolding.

    private sealed class MeritousSettings
    {
        // Intentionally empty for now — a placeholder for future launcher-side
        // options (e.g. a window-mode toggle) without touching the shared store.
    }

    private MeritousSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<MeritousSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(MeritousSettings s)
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

    // ── apworld asset bookkeeping (opportunistic; releases ship none today) ───

    /// Filename of the apworld asset seen on the resolved release (so the saved
    /// copy keeps the upstream name). null until a release is resolved / unless
    /// a future release adds one.
    private string? _apWorldFileName;

    /// Where the release's meritous apworld would be saved for the user to copy
    /// into Archipelago's custom_worlds folder, IF a release ever ships one.
    private string ApWorldLocalPath
    {
        get
        {
            string? name = _apWorldFileName;
            return Path.Combine(GameDirectory,
                string.IsNullOrEmpty(name) ? "meritous.apworld" : name);
        }
    }

    // ── Private helpers — download/extract ────────────────────────────────────

    private async Task DownloadAndExtractGameAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"meritous-ap-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading Meritous {version}..."));
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
                        progress.Report((pct, $"Downloading Meritous... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting..."));
            Directory.CreateDirectory(GameDirectory);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, GameDirectory, overwriteFiles: true);

            // Release zips often contain a single top-level sub-folder — flatten
            // it so the exe lands directly in GameDirectory. (ResolveGameExe
            // scans subdirectories too, so only flatten when the extract is a
            // lone wrapper folder with nothing at the root.)
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

            progress.Report((80, "Game files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }
}
