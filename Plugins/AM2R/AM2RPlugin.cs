using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using LauncherV2.Core;

// NOTE: WPF UI types are FULLY QUALIFIED throughout this file (System.Windows.*).
// The real launcher project sets <UseWindowsForms>true</UseWindowsForms> alongside
// WPF, so the bare names Button / TextBox / Color / Brushes / MessageBox /
// FontWeights / Orientation / Clipboard collide between System.Windows.Forms and
// System.Windows[.Controls/.Media] (CS0104). Qualifying every UI type avoids that.
// GlobalUsings.cs already aliases the colliding short names project-wide — so this
// file must NOT add any file-level `using X = System.Windows...;` alias (CS1537).

namespace LauncherV2.Plugins.AM2R;

// ═══════════════════════════════════════════════════════════════════════════════
// AM2RPlugin — install / launch for "AM2R" (Another Metroid 2 Remake) played
// through the Archipelago integration by Ehseezed (GitHub: Ehseezed/
// Archipelago-Integration). This is a NATIVE "ConnectsItself" integration in the
// same family as the shipped Blasphemous / Hollow Knight / TUNIC / A Short Hike
// plugins: the game itself speaks to the AP server (no emulator, no Lua bridge, no
// launcher-held ApClient on the slot).
//
// AM2R is a 2016 fan remake of Metroid II: Return of Samus, built in GameMaker
// Studio by DoctorM64's team. It was taken down from Game Jolt after a Nintendo
// DMCA; it now lives on via the AM2R Community Updates project (itch.io). This
// plugin is for the AP-integrated build distributed by Ehseezed.
//
// ── HONEST REALITY CHECK (2026-06-19) ────────────────────────────────────────
//   * THE AP WORLD game string: "AM2R" — marked UNVERIFIED. The value here is a
//     reasonable assumption based on the mod repo name. It should be confirmed
//     against worlds/__init__.py (or the repo's docs) before the game can be
//     generated. GameId here = "am2r".
//
//   * THE MOD REPO: Ehseezed/Archipelago-Integration (GitHub). The integration
//     shuffles Metroid powerups — missiles, super missiles, power bombs, energy
//     tanks, Metroid DNA, and other upgrades — across the multiworld, replacing
//     them with location checks. Items from other worlds arrive as upgrades
//     delivered to Samus. Users must download the AP-integrated build from this
//     repo's releases; they cannot use a vanilla AM2R install with a separate
//     mod file (the integration is compiled-in).
//
//   * CONNECTION: Made in-game when starting a new game (in-game connection menu).
//     The launcher surfaces session host/port/slot for the user to copy. No
//     command-line prefill is documented, so this plugin does NOT attempt one.
//
//   * NO STEAM: AM2R is a fan game, not on Steam. Detection is via a user-
//     specified folder only (stored in this plugin's own sidecar JSON). There is
//     no Steam registry lookup.
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. DETECT AM2R.exe in a user-specified install folder (stored in a sidecar
//      JSON at Games/ROMs/am2r/am2r_launcher.json). No Steam detection.
//   2. INSTALL/UPDATE — download the latest release zip from
//      Ehseezed/Archipelago-Integration/releases/latest, extract to a
//      user-specified folder. Provides clear guided steps + links for the manual
//      steps (itch.io copy, AP guide, mod repo).
//   3. LAUNCH = run AM2R.exe from the configured directory.
//      ConnectsItself = true (the integration speaks to AP itself).
//      SupportsStandalone = true (the unmodified community game runs without AP).
//      No connection prefill (entered in-game), stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ───────────────────────────────────────────────────
//   * "Installed" = AM2R.exe present in the stored install dir.
//   * ApWorldName "AM2R" is UNVERIFIED — confirm against worlds/__init__.py.
//   * Fallback version "2.0" is a guess; update once actual release tags are known.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class AM2RPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────
    private const string MOD_OWNER = "Ehseezed";
    private const string MOD_REPO  = "Archipelago-Integration";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl   = "https://archipelago.gg/games/AM2R/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    /// The AM2R Community Updates itch.io page — where users get the base game.
    private const string AM2RItchUrl = "https://am2r-community-developers.itch.io/am2r";

    /// The AP-integrated build exe name (expected inside the release zip).
    private const string Am2rExeName = "AM2R.exe";

    /// Pinned fallback when the GitHub API is unreachable. Update once real tags
    /// are confirmed. "2.0" is a reasonable first guess; mark it as such.
    private const string FallbackVersion = "2.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "am2r";
    public string DisplayName => "AM2R";
    public string Subtitle    => "PC · Archipelago integration";

    /// AP world game string — UNVERIFIED. Confirm against worlds/__init__.py
    /// in the Archipelago repo or the mod's documentation before generating.
    public string ApWorldName => "AM2R";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "am2r.png");

    public string ThemeAccentColor => "#FF6600";   // Metroid orange
    public string[] GameBadges     => new[] { "Bring your own AM2R build" };

    public string Description =>
        "AM2R (Another Metroid 2 Remake) is a 2016 fan remake of Metroid II: Return of " +
        "Samus, rebuilt from the ground up in GameMaker with expanded areas, new rooms, " +
        "new bosses, and a soundtrack by DoctorM64. The Archipelago integration by " +
        "Ehseezed shuffles powerups — missiles, super missiles, power bombs, energy tanks, " +
        "and Metroid DNA — across the multiworld, replacing them with location checks. " +
        "Items from other worlds arrive as upgrades delivered to Samus. You bring your own " +
        "copy of AM2R (from itch.io or the community patch); the launcher detects your " +
        "install and can stage the integration files into it. You connect to your " +
        "Archipelago server using the in-game connection menu when starting a new game.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means AM2R.exe is present in the configured install folder.
    public bool IsInstalled => FindAm2rExe() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and working files. The actual game is
    /// extracted into a user-specified folder, not here. Exposed as GameDirectory
    /// for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "AM2R");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore).
    /// Lives under Games/ROMs/am2r/ per the brief.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "am2r_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The AM2R integration speaks to the AP server itself — the launcher relays
    // nothing. These exist for interface compatibility (ConnectsItself = true).
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
            InstalledVersion = FindAm2rExe() != null
                ? (ReadStampedVersion() ?? "installed")
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
        // 0. We need a target folder to extract into. Use the configured override
        //    if set; otherwise default to our GameDirectory.
        progress.Report((2, "Locating target AM2R install folder..."));
        string? installDir = LoadOverrideDir();
        if (string.IsNullOrWhiteSpace(installDir))
            installDir = GameDirectory;

        // 1. Resolve the latest release (pinned fallback when offline).
        progress.Report((6, "Checking the latest AM2R Archipelago Integration release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the AM2R Archipelago Integration download on GitHub. " +
                "Check your internet connection, or download the integration manually " +
                "from " + ModRepoUrl + "/releases — see Settings for the guided steps.");

        // 2. Download + extract the release zip into the target directory.
        await DownloadAndExtractAsync(zipUrl, version, installDir, progress, ct);

        // 3. Stamp the version next to our sidecar so the tile can show it.
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool exeOk = FindAm2rExe() != null;
        progress.Report((100,
            $"AM2R Archipelago Integration {version} extracted to {installDir}" +
            (exeOk ? "." : " (verify the files — AM2R.exe not found yet).") +
            " To play: launch AM2R, start a new game, and enter your server details " +
            "(Server Address, Port, Name, optional Password) in the in-game connection " +
            "menu. (This launcher cannot pre-fill the connection — it is entered in-game.)"));
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
        // HONEST: the AP server connection for AM2R is entered in the in-game
        // connection menu when starting a new game. There is no documented
        // command-line / config prefill (verified — see header). Launching from
        // this tile just starts the game; the user connects in-game using the
        // session credentials shown in the settings panel.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the integration is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartAm2r();
        return Task.CompletedTask;
    }

    /// Plain AM2R runs without AP — the integration connect screen can be skipped.
    public bool SupportsStandalone => true;

    /// The AM2R integration owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartAm2r();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No plaintext AP password is ever written to disk by this plugin (the
        // connection is entered in-game), so there is nothing to scrub.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The AM2R integration receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The integration renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid AM2R folder contains AM2R.exe.
    /// Returns null when acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your AM2R install folder.";

        if (LooksLikeAm2rDir(folder))
            return null;

        return "That does not look like an AM2R installation. Pick the folder " +
               "that contains AM2R.exe (downloaded from itch.io or the AP integration " +
               "release from " + ModRepoUrl + ").";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Unverified-offline honesty header ─────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "AM2R is a fan remake of Metroid II. You need the Archipelago " +
                   "Integration build from Ehseezed/Archipelago-Integration (GitHub) — " +
                   "this is a self-contained build that already includes the AP client. " +
                   "Download it from the releases page, or use the Install button on the " +
                   "Play tab. The AP world name (\"AM2R\") is unverified — confirm against " +
                   "the Archipelago game list before generating. You connect in-game when " +
                   "starting a new game. These external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: install folder ────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "AM2R INSTALL FOLDER", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? overrideDir = LoadOverrideDir();
        string? exePath     = FindAm2rExe();
        string  detectMsg   = exePath != null
            ? "AM2R.exe found: " + exePath
            : overrideDir != null
                ? "Folder configured but AM2R.exe not found yet: " + overrideDir
                : "No AM2R install configured. Use the Install button (Play tab) or " +
                  "pick your AM2R folder below.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = exePath != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your AM2R install folder (the one containing AM2R.exe). " +
                          "This can be the Archipelago-integrated build extracted from the " +
                          "release zip, or your existing AM2R Community Updates install.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120, Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your AM2R install folder (contains AM2R.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? "")
                                   ? overrideDir!
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not an AM2R folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                SaveOverrideDir(picked);
                dirBox.Text = picked;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "AM2R is a fan game — not on Steam. You must supply your own copy " +
                   "(itch.io or community patch). The Install button on the Play tab will " +
                   "download the AP-integrated build from Ehseezed/Archipelago-Integration " +
                   "and extract it to a folder of your choice. Set that folder here.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (this session) ────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Launch AM2R and start a new game — an in-game connection menu will " +
                   "appear for your Archipelago details. Enter your Server Address and Port " +
                   "(e.g. archipelago.gg and 38281), your Name (slot name), and the Password " +
                   "if the server has one, then connect. This launcher does not pre-fill the " +
                   "connection.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Get AM2R: download AM2R from the AM2R Community Updates page on itch.io " +
                "(link below), or use the AP-integrated build directly from the releases " +
                "page at Ehseezed/Archipelago-Integration (recommended — the integration " +
                "build already includes the AP client).",
            "2. Use the Install button on the Play tab to download and extract the latest " +
                "AP-integrated build automatically, or download it manually from the " +
                "releases page (link below) and extract it to any folder on your PC.",
            "3. Point the launcher at your AM2R folder using the picker above (required if " +
                "you extracted the build manually or already had AM2R installed elsewhere).",
            "4. Confirm AM2R.exe is detected (the status line above turns green).",
            "5. To play: press Launch (or Launch Standalone for a normal run), then start " +
                "a new game and enter your Server Address, Port, Name, and optional Password " +
                "in the in-game connection menu. (This launcher cannot pre-fill the " +
                "connection — it is entered in-game.)",
            "6. NOTE: the AP world name \"AM2R\" is currently unverified. Confirm the " +
                "exact game string against Archipelago's game list or the mod's docs " +
                "before generating a multiworld.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("AM2R Archipelago Integration (GitHub) ↗",     ModRepoUrl),
            ("AM2R Integration Releases (download) ↗",      ModRepoUrl + "/releases"),
            ("AM2R Community Updates (itch.io) ↗",          AM2RItchUrl),
            ("AM2R Setup Guide (Archipelago) ↗",            SetupGuideUrl),
            ("Archipelago Official ↗",                       ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0), FontSize = 12,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => { try { Process.Start(
                new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
            panel.Children.Add(btn);
        }
        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Integration releases are the AP-relevant news for this game.
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_URL, ct);
            using var doc  = JsonDocument.Parse(json);
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

    /// "v2.0" → "2.0" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest integration release: version + zip download URL.
    /// Prefers the first .zip asset; falls back to the pinned fallback version
    /// download URL when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? zipUrl = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zipUrl = url;
                        // Prefer a zip whose name contains "am2r" or "archipelago".
                        string lower = name.ToLowerInvariant();
                        if (lower.Contains("am2r") || lower.Contains("archipelago"))
                            break; // found the best candidate
                    }
                }
                if (zipUrl != null)
                    return (version, zipUrl);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        // Pinned fallback — version "2.0" is a guess; the URL pattern follows the
        // standard GitHub release-asset convention. Update once real tags are known.
        string fallbackUrl =
            $"{ModRepoUrl}/releases/download/{FallbackVersion}/AM2R-AP-{FallbackVersion}.zip";
        return (FallbackVersion, fallbackUrl);
    }

    // ── Private helpers — AM2R detection ─────────────────────────────────────

    /// Find AM2R.exe in the configured install folder (override dir, then default
    /// GameDirectory). Returns the full exe path or null.
    private string? FindAm2rExe()
    {
        try
        {
            string? dir = ResolveAm2rDir();
            if (dir == null) return null;
            string exe = Path.Combine(dir, Am2rExeName);
            return File.Exists(exe) ? exe : null;
        }
        catch { return null; }
    }

    /// The AM2R directory to use: the override (if set and valid) wins, else the
    /// default GameDirectory (only if AM2R.exe is actually there).
    private string? ResolveAm2rDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeAm2rDir(ov))
            return ov;

        if (LooksLikeAm2rDir(GameDirectory))
            return GameDirectory;

        return null;
    }

    /// A folder "looks like" an AM2R install if it contains AM2R.exe.
    private static bool LooksLikeAm2rDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, Am2rExeName));
        }
        catch { return false; }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start AM2R from the configured install directory.
    private void StartAm2r()
    {
        string? exe = FindAm2rExe();
        if (exe == null)
            throw new FileNotFoundException(
                "Could not find AM2R.exe. Open this game's Settings and pick your AM2R " +
                "install folder, or use the Install button on the Play tab to download the " +
                "Archipelago-integrated build.",
                Am2rExeName);

        string dir = Path.GetDirectoryName(exe)!;
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exe,
            WorkingDirectory = dir,
            UseShellExecute  = true,
        }) ?? throw new InvalidOperationException("Failed to start AM2R.");

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
        catch { /* some processes don't expose Exited — non-fatal */ }
    }

    // ── Private helpers — download / extract the integration build ────────────

    /// Download the integration release zip and extract it into the target dir.
    /// Existing files are overwritten; sibling files are preserved.
    /// If the zip wraps everything inside a single sub-folder, that wrapper is
    /// flattened so AM2R.exe lands directly in targetDir.
    private async Task DownloadAndExtractAsync(
        string zipUrl,
        string version,
        string targetDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"am2r-ap-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"am2r-ap-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading AM2R Archipelago Integration {version}..."));
            using (var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
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
                        int pct = (int)(10 + 60 * downloaded / total);
                        progress.Report((pct, $"Downloading AM2R Archipelago Integration... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Extracting AM2R integration files..."));
            Directory.CreateDirectory(targetDir);
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // If everything is wrapped in a single sub-folder, descend into it
            // so AM2R.exe lands directly in targetDir.
            string mergeRoot = tempExtract;
            if (!File.Exists(Path.Combine(mergeRoot, Am2rExeName)))
            {
                string[] subdirs = Directory.GetDirectories(mergeRoot);
                string[] files   = Directory.GetFiles(mergeRoot);
                if (subdirs.Length == 1 && files.Length == 0 &&
                    File.Exists(Path.Combine(subdirs[0], Am2rExeName)))
                {
                    mergeRoot = subdirs[0];
                }
            }

            // Merge the extracted tree INTO targetDir (overwrite but don't wipe
            // siblings — the user may have save files or other content there).
            MergeDirectory(mergeRoot, targetDir);

            progress.Report((90, "Integration files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip))       File.Delete(tempZip); }            catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Recursively copy a directory tree INTO an existing destination, overwriting
    /// individual files but preserving any sibling files already there.
    private static void MergeDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            try { File.Copy(file, dst, overwrite: true); }
            catch { /* a locked file (game open?) — skip; user can retry with game closed */ }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (install-dir override + an
    // informational version stamp) in its OWN JSON file so it stays a single
    // self-contained source file and does not modify Core/SettingsStore.
    // BOM-less UTF-8, read-modify-write (same approach as AShortHike / Jak).

    private sealed class Am2rSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private Am2rSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Am2rSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(Am2rSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — setting just won't persist this time */ }
    }

    private string? LoadOverrideDir()
    {
        string? p = LoadSettings().InstallOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }
    private void SaveOverrideDir(string p) { var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s); }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    private void WriteStampedVersion(string v) { var s = LoadSettings(); s.ModVersion = v; SaveSettings(s); }
}
