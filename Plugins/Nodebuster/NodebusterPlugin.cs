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

namespace LauncherV2.Plugins.Nodebuster;

// ═══════════════════════════════════════════════════════════════════════════════
// NodebusterPlugin — install / update / launch for "Nodebuster", a small
// freeware puzzle game whose Archipelago mod (Emerald836/Emerlads-Nodebuster_AP_Mod)
// IS the game (the mod bundles or replaces the base game). This is a NATIVE
// "ConnectsItself" integration: the game speaks to the AP server itself — no
// emulator, no Lua bridge, and no launcher-held ApClient on the slot.
//
// Like ChecksFinder and Meritous, this is the CLEANEST kind of native plugin —
// there is NO bring-your-own-asset gate. The download from the mod repo is the
// complete, playable AP client.
//
// REALITY CHECK (2026-06-14) — facts verified / noted this session
// ─────────────────────────────────────────────────────────────────────────────
//   * REPO:
//       Emerald836/Emerlads-Nodebuster_AP_Mod — "Nodebuster" Archipelago mod.
//     GitHub releases URL (primary source):
//       https://github.com/Emerald836/Emerlads-Nodebuster_AP_Mod/releases
//     The mod is distributed via GitHub releases. Because this is a small
//     community mod (not a core Archipelago world with an official setup guide
//     online), specific release asset filenames could not be confirmed offline.
//     ResolveLatestReleaseAsync therefore picks any Windows .zip from the latest
//     release, preferring names that match "win", "windows", or "x64" and
//     avoiding linux/mac/source assets. The exe inside is resolved by fuzzy name
//     matching ("nodebuster", "node", or the single plausible exe), so we stay
//     robust regardless of the asset naming convention the author uses.
//     A pinned fallback URL is intentionally NOT encoded here because no specific
//     release/tag/asset was confirmed offline — if the API is unreachable the
//     install path surfaces a clear error pointing the player at the releases page.
//
//   * THE AP WORLD: game string "Nodebuster" — the AP world name as given by the
//     task description (Emerald836/Emerlads-Nodebuster_AP_Mod). World id/folder
//     likely "nodebuster". This is a COMMUNITY mod, so the .apworld is expected
//     to ship alongside the game download on the GitHub releases page. If the
//     release includes a .apworld asset, this plugin saves it next to the install
//     so the player can copy it into Archipelago's custom_worlds folder.
//
//   * HOW IT CONNECTS: The base game is a small freeware puzzle game (possibly
//     available on itch.io). The mod bundles or replaces it. Connection is
//     expected to be in-game (the mod's own UI or a companion config file). Because
//     no documented connection interface (config file shape, command-line flags, or
//     in-game screen) was confirmed offline, this plugin launches the game directly
//     and trusts the player to use whatever connection method the mod provides.
//     The settings panel surfaces clear instructions pointing at the mod repo's
//     README and releases page. ConnectsItself = true — the launcher must not hold
//     its own ApClient on the same slot while the game runs.
//
//   * NO ASSET GATE: Nodebuster is freeware — the mod release ships a complete,
//     self-contained game. There is no ROM/IWAD/data.zip to supply. SupportsStandalone
//     is true so the player can run the game without an AP room if the base game
//     supports it.
//
// DEFENSIVE / UNVERIFIED DETAILS (flagged, "verify at build time"):
//   * Exact Windows asset name in releases is unverified offline — PickWindowsZip
//     uses broad heuristics (same pattern as ChecksFinderPlugin / MeritousPlugin).
//   * Exe name inside the zip is unknown — ResolveGameExe tries "Nodebuster.exe"
//     first, then any "*nodebuster*"/"*node*" exe, then the lone plausible exe.
//   * Whether a .apworld ships alongside the game binary is unverified — the plugin
//     opportunistically saves one if found in the release assets.
//   * Whether the game supports standalone (non-AP) play is unverified — kept true
//     as most freeware puzzle mods can run without a server.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class NodebusterPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER = "Emerald836";
    private const string GITHUB_REPO  = "Emerlads-Nodebuster_AP_Mod";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"{GH_RELEASES_URL}/latest";

    /// Releases page — surfaced as an informational link.
    private const string ReleasesUrl = $"{RepoUrl}/releases";

    // No pinned fallback version is encoded because no specific release tag or
    // asset name was confirmed offline (see header). The user is directed to the
    // releases page when the API is unreachable.

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp, written after a successful install.
    private const string VersionFileName = "nodebuster_ap_version.dat";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "nodebuster";
    public string DisplayName => "Nodebuster";
    public string Subtitle    => "Native PC · built-in Archipelago";

    /// EXACT AP game string per the task description.
    public string ApWorldName => "Nodebuster";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "nodebuster.png");

    public string ThemeAccentColor => "#00BCD4";   // cyan / circuit-board teal
    public string[] GameBadges     => new[] { "Free · community mod" };

    public string Description =>
        "Nodebuster is a small freeware puzzle game paired with an Archipelago mod " +
        "by Emerald836 that turns it into a built-in multiworld client. The mod " +
        "release is the complete, self-contained game — nothing to bring, nothing " +
        "to extract. The launcher downloads the latest release from GitHub, installs " +
        "it, and launches you straight in. Connect to your Archipelago room using " +
        "whatever connection screen or config file the mod provides.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => ResolveGameExe() != null;
    public bool    IsRunning        { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the Nodebuster AP mod is installed.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Nodebuster");

    /// Preferred exe (best-effort name). Resolution falls back to a fuzzy match.
    private string PreferredExePath => Path.Combine(GameDirectory, "Nodebuster.exe");

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained source file). Lives under the
    /// launcher's ROM-library tree for consistency with the other native plugins,
    /// even though this game brings no ROM.
    private string SettingsSidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                        "nodebuster_launcher.json");

    private string SettingsSidecarDir => Path.GetDirectoryName(SettingsSidecarPath)!;

    /// Where the release's .apworld would be saved if one is found in the release
    /// assets, so the player can drop it into Archipelago's custom_worlds folder.
    private string ApWorldLocalPath
        => Path.Combine(GameDirectory, _apWorldFileName ?? "nodebuster.apworld");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;
    private string?  _apWorldFileName;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // Nodebuster's native AP client reports checks/items/goal to the AP server
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
        // 1. Resolve the latest release.
        progress.Report((2, "Checking latest Nodebuster (Archipelago) release..."));
        var (version, zipUrl, apworldUrl, apworldName) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion  = version;
        _apWorldFileName  = apworldName;

        // 2. Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"Nodebuster (Archipelago) {version} is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a Windows download for Nodebuster on the GitHub " +
                "release page. Check your internet connection, or download the " +
                "build manually from " + ReleasesUrl + ".");

        // 3. Download + extract the build.
        await DownloadAndExtractGameAsync(zipUrl, version, progress, ct);

        // 4. Download the .apworld if the release ships one (opportunistic).
        if (apworldUrl != null)
        {
            try
            {
                progress.Report((85, "Downloading the Nodebuster apworld..."));
                byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                await File.WriteAllBytesAsync(ApWorldLocalPath, apworld, ct);
                progress.Report((92,
                    $"{Path.GetFileName(ApWorldLocalPath)} saved next to the game — " +
                    "copy it into Archipelago's custom_worlds folder to generate games."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((92,
                    "Could not download the Nodebuster apworld — check the releases " +
                    "page and copy it into custom_worlds manually."));
            }
        }

        // 5. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"Nodebuster (Archipelago) {version} ready. Press Play to launch — " +
            "connect to your room using the in-game connection screen."));
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
                "Nodebuster is not installed. Click Install Game first.",
                PreferredExePath);

        // Nodebuster's connection UI is in-game (no documented config file or
        // command-line interface confirmed offline). Launch the exe; the player
        // enters their room details in whatever connection screen the mod provides.
        StartGameProcess(exe);
        return Task.CompletedTask;
    }

    /// Nodebuster is a complete self-contained game — standalone play is supported
    /// (non-AP mode, if the base game has a single-player mode).
    public bool SupportsStandalone => true;

    /// Nodebuster's native in-game AP client owns the slot connection (see header).
    /// The launcher must not connect its own ApClient to the same slot while the
    /// game runs.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        string exe = ResolveGameExe()
            ?? throw new FileNotFoundException(
                "Nodebuster is not installed. Click Install Game first.",
                PreferredExePath);

        // Launch without AP — the mod should fall back to offline / menu mode.
        StartGameProcess(exe);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No plaintext credential file is known to be written by this plugin
        // (connection is in-game only), but keep the scrub hook consistent with
        // the other native ConnectsItself plugins.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // Nodebuster's native client receives items from the AP server directly;
        // there is nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // Nodebuster renders its own AP status in-game; no launcher HUD channel.
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
                Title            = "Select Nodebuster install folder",
                InitialDirectory = Directory.Exists(GameDirectory) ? GameDirectory
                                   : AppContext.BaseDirectory,
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
            Text       = IsInstalled ? "✓ Nodebuster is installed"
                                     : "Not installed (click Install in the Play tab)",
            FontSize   = 11, Foreground = IsInstalled ? success : muted,
            Margin     = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Archipelago connection ───────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "ARCHIPELAGO CONNECTION", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Nodebuster's AP mod has a built-in connection interface. Press Play to " +
                   "launch the game, then use whatever connection screen or settings the " +
                   "mod provides to enter your server address, slot name, and room password. " +
                   "The mod (not the launcher) connects to the Archipelago server, so the " +
                   "launcher does not need to fill in your credentials.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: apworld ─────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "APWORLD FILE", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });

        bool apworldPresent = File.Exists(ApWorldLocalPath);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = apworldPresent
                ? $"✓ {Path.GetFileName(ApWorldLocalPath)} downloaded — copy it into " +
                  "Archipelago's custom_worlds folder to generate Nodebuster games."
                : "The launcher will save the Nodebuster apworld next to the game when " +
                  "you install (if the release includes one). You can also download it " +
                  "manually from the GitHub releases page and copy it into " +
                  "Archipelago's custom_worlds folder.",
            FontSize = 11, Foreground = apworldPresent ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: free game note ───────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Nodebuster is freeware — there is nothing to buy or supply. The " +
                   "download from the mod repo is the complete, self-contained game client.",
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
            ("Nodebuster AP Mod (GitHub) ↗",  RepoUrl),
            ("Releases page ↗",               ReleasesUrl),
            ("Archipelago Official ↗",        "https://archipelago.gg"),
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
                    System.Windows.Media.Color.FromRgb(0x00, 0xE5, 0xFF)),
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

    /// "v1.0.0" → "1.0.0" when a leading 'v' decorates a digit; otherwise the tag
    /// is returned as-is (trimmed). Returns null for null/blank tags.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest release: version + Windows zip asset URL + apworld URL +
    /// apworld filename. Tries /releases/latest first; falls back to scanning
    /// /releases (all) when the repo may use pre-releases. Returns (version, null,
    /// null, null) if no Windows zip could be found — the caller surfaces a clear
    /// error with the releases page URL.
    private async Task<(string Version, string? ZipUrl, string? ApWorldUrl, string? ApWorldName)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        // Try /releases/latest first (works for normal non-prerelease releases).
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

                // /latest exists but has no Windows zip — return version+no-zip.
                return (version, null, apworld, apworldName);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* /latest returned 404 or API error — try the releases list */ }

        // Fall back to scanning the full releases list (handles repos where every
        // release is a pre-release or where /latest returns 404).
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
                    var (zip, apworld, apworldName) = PickWindowsAndApworld(assets);
                    if (zip != null)
                        return (version, zip, apworld, apworldName);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable */ }

        // Could not resolve — return a sentinel version so CheckForUpdate can show
        // "unknown"; the null ZipUrl is what triggers the error in InstallOrUpdate.
        return ("unknown", null, null, null);
    }

    /// From a release's assets array, pick the Windows .zip (preferring assets
    /// named "win", "windows", or "x64") and any .apworld file. Skips linux/mac/
    /// source/android/web assets explicitly.
    private static (string? Zip, string? ApWorld, string? ApWorldName)
        PickWindowsAndApworld(JsonElement assets)
    {
        string? winNamed = null, anyZip = null;
        string? apworld  = null, apworldName = null;

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

            if (!lower.EndsWith(".zip")) continue;
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

            anyZip ??= url;
            if (winNamed == null &&
                (lower.Contains("win") || lower.Contains("x64") || lower.Contains("x86_64")))
                winNamed = url;
        }

        return (winNamed ?? anyZip, apworld, apworldName);
    }

    // ── Private helpers — exe resolution ──────────────────────────────────────

    /// Resolve the installed exe: prefer "Nodebuster.exe", then any
    /// "*nodebuster*"/"*node*" exe in the install (fuzzy), then the lone plausible
    /// exe, skipping helper/uninstaller/crash-handler exes. Defensive — the exact
    /// zip contents were not inspected offline.
    private string? ResolveGameExe()
    {
        if (File.Exists(PreferredExePath)) return PreferredExePath;
        if (!Directory.Exists(GameDirectory)) return null;
        try
        {
            string? named    = null;
            string? firstExe = null;
            foreach (string exe in Directory.EnumerateFiles(GameDirectory, "*.exe",
                                                            SearchOption.AllDirectories))
            {
                string stem = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (stem.Contains("unins") || stem.Contains("setup") || stem.Contains("crash"))
                    continue;
                firstExe ??= exe;
                if (stem.Contains("nodebuster") || stem.Contains("node"))
                {
                    named = exe;
                    break;
                }
            }
            // Name match wins; fallback to lone plausible exe (Godot-style build).
            return named ?? firstExe;
        }
        catch { /* directory vanished mid-scan */ }
        return null;
    }

    // ── Private helpers — process ─────────────────────────────────────────────

    private void StartGameProcess(string exePath)
    {
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = GameDirectory,
            UseShellExecute  = false,
        }) ?? throw new InvalidOperationException("Failed to start Nodebuster.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // Kept in this plugin's OWN JSON file so it stays a single self-contained
    // source file and does not modify Core/SettingsStore. Reserved for any future
    // launcher-side toggle; the install dir is the only setting that matters today.

    private sealed class NodebusterSettings
    {
        // Intentionally empty for now — a placeholder for future launcher-side
        // options without touching the shared store.
    }

    private NodebusterSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<NodebusterSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(NodebusterSettings s)
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
            $"nodebuster-ap-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading Nodebuster {version}..."));
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
                        progress.Report((pct, $"Downloading Nodebuster... {downloaded / 1_000_000}MB"));
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
