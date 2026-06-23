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

namespace LauncherV2.Plugins.Celeste64;

// ═══════════════════════════════════════════════════════════════════════════════
// Celeste64Plugin — install / update / launch for "Celeste 64" (Celeste 64:
// Fragments of the Mountain) played through its Archipelago build. This is a
// NATIVE "ConnectsItself" integration (NOT a BizHawk / Lua emulator game): the
// game speaks to the AP server itself, exactly like Ship of Harkinian, the
// OpenTTD Archipelago fork, and APDOOM.
//
// This is the CLEANEST kind of native plugin — there is NO bring-your-own-asset
// gate. Celeste 64 (Fragments of the Mountain) is FREE, open-source freeware
// (ExOK / Celeste64), so the Archipelago build is a fully self-contained Windows
// download: install = a single GitHub-release download + launch. It is therefore
// modelled on Plugins/Doom/Doom1993Plugin.cs but with ALL the WAD / ROM
// machinery removed — like SoH minus the ROM extractor.
//
// REALITY CHECK (2026-06-14) — facts verified this session
// ─────────────────────────────────────────────────────────────────────────────
//   * REPO (verified online + the official AP "Celeste 64" setup guide):
//       PoryGoneDev/Celeste64 — "Celeste 64: Archipelago Edition".
//     Releases are NORMAL (non-prerelease) GitHub releases, so /releases/latest
//     works. Latest at time of writing: tag "v1.4.1" ("The Final Update",
//     2026-01-31). Windows asset name pattern, verified across every release:
//       "Celeste64-Archipelago-v<ver>-win-x64.zip"
//     (e.g. "Celeste64-Archipelago-v1.4.1-win-x64.zip"). Each release also ships
//     "celeste64.apworld". The exe inside the zip is "Celeste64.exe" (verified
//     from the setup guide's launch step). v1.4.1 is pinned as the offline
//     fallback so install still works when the GitHub API is unreachable.
//
//   * HOW IT CONNECTS (VERIFIED, verbatim, against the official Archipelago
//     "Celeste 64" setup guide — https://archipelago.gg/tutorial/Celeste%2064/
//     guide_en — and the AP-main worlds/celeste64 guide doc): the AP connection
//     is provided by a CONFIG FILE named "AP.json" in the ROOT of the Celeste 64
//     install, holding exactly three string fields:
//         {
//             "Url":      "archipelago.gg:38281",
//             "SlotName": "<your slot name>",
//             "Password": ""
//         }
//     The player edits AP.json, then runs Celeste64.exe; "If you can continue
//     past the title screen, then you are successfully connected." There is NO
//     in-game connection menu and NO command line — the file IS the documented
//     interface. This is even cleaner than SoH's CVar prefill: the keys are
//     EXACT and verified, not guessed. This plugin writes AP.json on launch
//     (read-modify-write so manual edits/extra keys survive), so the player
//     never has to touch the file. AP servers allow one connection per slot, so
//     — like SoH / OpenTTD / APDOOM — the launcher must NOT hold its own
//     ApClient on the same slot while the game runs: ConnectsItself = true.
//
//   * THE AP WORLD: game string "Celeste 64" — verified against AP-main
//     worlds/celeste64/__init__.py (Celeste64World.game = "Celeste 64", line 37
//     of the local checkout in mk64src/worlds/celeste64/). World id "celeste64".
//     The plugin fetches the release's "celeste64.apworld" next to the install
//     (best effort) so the user can drop it into custom_worlds; AP-main already
//     bundles the stable world.
//
//   * NO ASSET GATE: the base game is free + open source, so the AP build ships
//     a COMPLETE, playable game. There is no IWAD/ROM to supply, no first-run
//     extractor, no asset-library copy. Plain non-AP play is supported
//     (SupportsStandalone = true) — just launch without writing AP.json (we
//     simply don't prefill; the user can also play vanilla Celeste 64).
//
// DEFENSIVE / UNVERIFIED DETAILS (flagged, "verify at build time"):
//   * The exact zip CONTENTS were not inspected offline. ResolveGameExe()
//     prefers "Celeste64.exe", then any "*celeste*" exe in the install (fuzzy),
//     skipping obvious helper/uninstaller exes. Single-subdir extracts are
//     flattened so the exe lands at the install root.
//   * AP.json is written DEFENSIVELY: if the build ever renames a key, the
//     in-game/guide path still works (the player edits the file once); we never
//     clobber unknown keys (read-modify-write). The post-install note is honest
//     that AP.json is the connection mechanism.
//   * One launcher-side setting (the install dir is the only one that matters)
//     is enough; this plugin keeps any optional setting in its OWN JSON sidecar
//     (Games/ROMs/celeste64/celeste64_launcher.json) rather than modifying the
//     shared Core/SettingsStore — it is added as a single self-contained file.
//     (The Doom plugin took the same self-contained-sidecar approach.)
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Celeste64Plugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER = "PoryGoneDev";
    private const string GITHUB_REPO  = "Celeste64";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"{GH_RELEASES_URL}/latest";

    /// Official Archipelago "Celeste 64" setup guide.
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Celeste%2064/guide_en";

    // Pinned fallback — the latest non-prerelease at time of writing, with the
    // verified asset-name pattern. Used ONLY when the GitHub API is unreachable
    // so a fresh install still works offline-of-the-API.
    private const string FallbackVersion = "1.4.1";
    private const string FallbackZipName = "Celeste64-Archipelago-v1.4.1-win-x64.zip";
    private static readonly string FallbackZipUrl =
        $"{RepoUrl}/releases/download/v{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp, written after a successful install.
    private const string VersionFileName = "celeste64_ap_version.dat";

    /// The verified AP connection file name, in the install root.
    private const string ApConfigFileName = "AP.json";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "celeste64";
    public string DisplayName => "Celeste 64";
    public string Subtitle    => "Native PC · built-in Archipelago";

    /// EXACT AP game string — verified against worlds/celeste64/__init__.py
    /// (Celeste64World.game = "Celeste 64").
    public string ApWorldName => "Celeste 64";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "celeste64.png");

    public string ThemeAccentColor => "#3B7DD8";   // Celeste blue
    public string[] GameBadges     => new[] { "Free · open source" };

    public string Description =>
        "Celeste 64: Fragments of the Mountain is a free, open-source love letter " +
        "to Celeste, made by the original team for the game's sixth anniversary. " +
        "This is the Archipelago build: it ships its own built-in multiworld " +
        "client, so strawberries and abilities are shuffled into the multiworld " +
        "and the game connects to the Archipelago server itself — no emulator, no " +
        "Lua bridge. Because the base game is free and open source, the download " +
        "is the complete game: nothing to bring, nothing to extract. The launcher " +
        "fills in your connection details and launches you straight in.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => ResolveGameExe() != null;
    public bool    IsRunning        { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the Celeste 64 Archipelago build is installed.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Celeste64");

    /// Preferred exe (verified name). Resolution falls back to a fuzzy match.
    private string PreferredExePath => Path.Combine(GameDirectory, "Celeste64.exe");

    /// The AP connection file in the install root (verified name).
    private string ApConfigPath => Path.Combine(GameDirectory, ApConfigFileName);

    /// Where the release's celeste64 apworld is saved for the user to copy into
    /// Archipelago's custom_worlds folder.
    private string ApWorldLocalPath
    {
        get
        {
            string? name = _apWorldFileName;
            return Path.Combine(GameDirectory,
                string.IsNullOrEmpty(name) ? "celeste64.apworld" : name);
        }
    }

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore
    /// so the plugin stays a single self-contained source file). Lives under the
    /// launcher's ROM-library tree for consistency with the other native plugins,
    /// even though this game brings no ROM.
    private string SettingsSidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                        "celeste64_launcher.json");

    private string SettingsSidecarDir => Path.GetDirectoryName(SettingsSidecarPath)!;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    /// Filename of the apworld asset seen on the resolved release (so the saved
    /// copy keeps the upstream name). null until a release is resolved.
    private string? _apWorldFileName;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // Celeste 64's native AP client reports checks/items/goal to the AP server
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
        progress.Report((2, "Checking latest Celeste 64 (Archipelago) release..."));
        var (version, zipUrl, apworldUrl, apworldName) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;
        _apWorldFileName = apworldName;

        // 2. Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"Celeste 64 (Archipelago) {version} is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a Windows download for Celeste 64 on the GitHub " +
                "release page. Check your internet connection, or download the " +
                "build manually from " + RepoUrl + "/releases.");

        // 3. Download + extract the build.
        await DownloadAndExtractGameAsync(zipUrl, version, progress, ct);

        // 4. Fetch the apworld next to the install (best effort).
        if (apworldUrl != null)
        {
            try
            {
                progress.Report((85, "Downloading the Celeste 64 apworld..."));
                byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                await File.WriteAllBytesAsync(ApWorldLocalPath, apworld, ct);
                progress.Report((92, $"{Path.GetFileName(ApWorldLocalPath)} saved — copy it into Archipelago's custom_worlds folder if you generate with this build."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((92, "Could not download the apworld — get it from the GitHub release page (the stable world also ships with Archipelago)."));
            }
        }

        // 5. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"Celeste 64 (Archipelago) {version} ready. Press Play to connect — " +
            "the launcher fills in your AP.json automatically."));
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
                "Celeste 64 is not installed. Click Install Game first.",
                PreferredExePath);

        // VERIFIED connection path: write the documented AP.json connection file
        // (Url / SlotName / Password) into the install root so the game connects
        // on launch. Read-modify-write so any manual edits / extra keys survive.
        // Best effort — never blocks the launch.
        try { WriteApConfig(session); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }

        StartGameProcess(exe);
        return Task.CompletedTask;
    }

    /// Celeste 64 is a complete game — plain (non-AP) play is supported.
    public bool SupportsStandalone => true;

    /// Celeste 64's native in-game AP client owns the slot connection (see
    /// header). The launcher must not connect its own ApClient to the same slot
    /// while the game runs.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        string exe = ResolveGameExe()
            ?? throw new FileNotFoundException(
                "Celeste 64 is not installed. Click Install Game first.",
                PreferredExePath);

        // No AP prefill — plain Celeste 64. Blank any leftover connection so a
        // previous session's slot/password does not silently auto-connect.
        try { ClearApConfig(); } catch { }

        StartGameProcess(exe);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // The plaintext room password lives in AP.json on disk — scrub it once
        // the session ends so it does not outlive the game.
        ScrubApConfigPassword();
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // Celeste 64's native client receives items from the AP server directly;
        // there is nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // Celeste 64 renders its own AP status in-game; no launcher HUD channel.
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
                Title            = "Select Celeste 64 install folder",
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
            Text       = IsInstalled ? "✓ Celeste 64 (Archipelago) is installed"
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
            Text = "Celeste 64 reads its connection from an AP.json file in the install " +
                   "folder (Url, SlotName, Password). The launcher writes this file for " +
                   "you each time you press Play, then runs Celeste64.exe — if you can " +
                   "continue past the title screen you are connected. You can still edit " +
                   "AP.json by hand; the launcher preserves any extra keys.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(new TextBlock
        {
            Text = "This game is free and open source — there is nothing to bring. The " +
                   "Archipelago build is the complete game, so install is just a download.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        if (IsInstalled && File.Exists(ApWorldLocalPath))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"{Path.GetFileName(ApWorldLocalPath)} is saved in the install folder — " +
                       @"copy it into your Archipelago custom_worlds folder (default: " +
                       @"C:\ProgramData\Archipelago\custom_worlds) if you generate with this build.",
                FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Celeste 64 Archipelago (GitHub) ↗", RepoUrl),
            ("Celeste 64 Setup Guide ↗",          SetupGuideUrl),
            ("Archipelago Official ↗",            "https://archipelago.gg"),
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

    /// "v1.4.1" → "1.4.1" when a leading 'v' decorates a digit; otherwise the
    /// tag is returned as-is (trimmed). Returns null for null/blank tags.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest release: version + Windows zip asset URL + apworld
    /// asset URL + apworld filename. Celeste 64 publishes normal (non-prerelease)
    /// releases, so /releases/latest is the right endpoint. Falls back to the
    /// pinned v1.4.1 direct URL when the API is unreachable.
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

        // Offline fallback: v1.4.1, known asset URL. No apworld direct URL is
        // pinned (AP-main ships the stable world anyway).
        return (FallbackVersion, FallbackZipUrl, null, null);
    }

    /// From a release's assets array, pick the Windows .zip (verified pattern
    /// "Celeste64-Archipelago-*-win-x64.zip"; match broadly on win/x64, excluding
    /// linux/mac/source) and the celeste64 apworld.
    private static (string? Zip, string? ApWorld, string? ApWorldName)
        PickWindowsAndApworld(JsonElement assets)
    {
        string? zip = null, apworld = null, apworldName = null;
        string? anyZip = null;

        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();

            if (lower.EndsWith(".apworld") && lower.Contains("celeste64"))
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
                if (zip == null &&
                    (lower.Contains("win") || lower.Contains("x64") || lower.Contains("x86_64")))
                    zip = url;
            }
        }

        // If no asset matched the Windows heuristics but a single non-Linux game
        // zip exists, use it (defensive).
        zip ??= anyZip;
        return (zip, apworld, apworldName);
    }

    // ── Private helpers — exe resolution ──────────────────────────────────────

    /// Resolve the installed exe: prefer "Celeste64.exe", then any "*celeste*"
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
                if (name.Contains("celeste"))
                    return exe;
            }
        }
        catch { /* directory vanished mid-scan */ }
        return null;
    }

    // ── Private helpers — AP.json connection file (verified) ──────────────────

    /// Write the documented AP connection file (AP.json in the install root) with
    /// the verified fields Url / SlotName / Password. Read-modify-write so any
    /// manual edits or extra keys the build adds are preserved. BOM-less UTF-8.
    private void WriteApConfig(ApSession session)
    {
        Directory.CreateDirectory(GameDirectory);

        // Preserve any existing keys (the build's own, or user-added).
        var root = new Dictionary<string, object?>();
        if (File.Exists(ApConfigPath))
        {
            try
            {
                string existing = File.ReadAllText(ApConfigPath);
                if (!string.IsNullOrWhiteSpace(existing))
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existing);
                    if (parsed != null)
                        foreach (var kv in parsed)
                            root[kv.Key] = JsonElementToObject(kv.Value);
                }
            }
            catch { /* corrupt / unknown shape — start fresh rather than fail launch */ }
        }

        // The three VERIFIED fields. AP servers want "host:port" in Url.
        root["Url"]      = FormatServerUrl(session.ServerUri);
        root["SlotName"] = session.SlotName;
        root["Password"] = session.Password ?? "";

        File.WriteAllText(ApConfigPath,
            JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    /// Blank the AP.json connection fields for a standalone (non-AP) launch so a
    /// previous session's slot/password does not silently auto-connect. Best
    /// effort; leaves any non-AP keys intact.
    private void ClearApConfig()
    {
        if (!File.Exists(ApConfigPath)) return;
        MutateApConfig(fields =>
        {
            fields["Url"]      = "";
            fields["SlotName"] = "";
            fields["Password"] = "";
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
                if (fields.TryGetValue("Password", out var pw) &&
                    pw is string s && !string.IsNullOrEmpty(s))
                    fields["Password"] = "";
            });
        }
        catch { /* best effort — the next AP launch overwrites the file anyway */ }
    }

    /// Read AP.json into a plain mutable map, apply `mutate`, write it back
    /// (BOM-less UTF-8). Preserves every key not touched by the mutator.
    private void MutateApConfig(Action<Dictionary<string, object?>> mutate)
    {
        var root = new Dictionary<string, object?>();
        try
        {
            string text = File.ReadAllText(ApConfigPath);
            if (!string.IsNullOrWhiteSpace(text))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text);
                if (parsed != null)
                    foreach (var kv in parsed)
                        root[kv.Key] = JsonElementToObject(kv.Value);
            }
        }
        catch { return; /* corrupt — leave it for the next AP launch to rewrite */ }

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

    /// Normalise the launcher's server URI into the "host:port" form AP.json
    /// wants (strip ws://wss:// scheme + any path; default port 38281). Handles
    /// bare hostnames and IPv6 literals.
    private static string FormatServerUrl(string serverUri)
    {
        var (host, port) = ParseServerHostPort(serverUri);
        // Re-bracket IPv6 literals so "host:port" stays unambiguous.
        return host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";
    }

    /// Accepts "archipelago.gg:38281", "ws://host:port", "wss://host:port", a
    /// bare hostname, and IPv6 literals (bracketed "[::1]:38281" or bare "::1").
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
        }) ?? throw new InvalidOperationException("Failed to start Celeste 64.");

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

    private sealed class Celeste64Settings
    {
        // Intentionally empty for now — a placeholder for future launcher-side
        // options (e.g. a fullscreen toggle) without touching the shared store.
    }

    private Celeste64Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Celeste64Settings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(Celeste64Settings s)
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
            $"celeste64-ap-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading Celeste 64 {version}..."));
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
                        progress.Report((pct, $"Downloading Celeste 64... {downloaded / 1_000_000}MB"));
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
