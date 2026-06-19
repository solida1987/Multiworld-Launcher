using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
// FontWeights / Orientation / HorizontalAlignment collide between
// System.Windows.Forms and System.Windows[.Controls/.Media] (CS0104). Qualifying
// every UI type avoids that.

namespace LauncherV2.Plugins.SentinelsOfTheMultiverse;

// ═══════════════════════════════════════════════════════════════════════════════
// SentinelsOfTheMultiversePlugin — install / launch for "Sentinels of the
// Multiverse" (Greater Than Games, 2014) played through the Archipelago mod by
// Totox00 (Archipelago-sotm). This is a NATIVE "ConnectsItself" integration —
// the game's mod speaks to the AP server itself (no emulator, no Lua bridge, no
// launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified against the apworld repo) ──────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned
// Sentinels of the Multiverse (Steam appid 337150), and Archipelago is a MOD
// added on top. Framework is Unity-based (very likely BepInEx or a custom
// patcher) — the exact patcher type was not confirmed at time of writing, so
// installation uses GitHub releases and the mod's own instructions. The honest
// integration ceiling is "automate what is possible, guide the irreducible parts."
//
//   * THE AP WORLD is "Sentinels of the Multiverse" — game string verified against
//     the Totox00/Archipelago-sotm repository. GameId = "sentinels_of_the_multiverse".
//
//   * THE MOD is distributed via GitHub releases on Totox00/Archipelago-sotm.
//     Releases carry a downloadable zip with the patcher / mod files that are
//     extracted into the Sentinels of the Multiverse install directory.
//
//   * CONNECTION is made IN-GAME once the mod is installed and the game is
//     launched. There is NO documented command-line or config-file prefill this
//     launcher can apply, so this plugin does NOT attempt a connection prefill.
//     The settings panel surfaces the session's server/slot so the user can type
//     them into the in-game fields.
//
//   * ConnectsItself = true: the mod owns the slot connection; the launcher must
//     NOT hold its own ApClient on the same slot while the game runs.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Sentinels install via Windows registry (SteamPath /
//      libraryfolders.vdf / appmanifest_337150.acf). A manual folder override is
//      also supported (settings folder picker) and takes precedence; it is
//      validated (must contain SentinelsOfTheMultiverse.exe or equivalent) and
//      persisted in this plugin's own sidecar
//      (Games/ROMs/sentinels_of_the_multiverse/sotm_launcher.json).
//   2. INSTALL/UPDATE = fetch the latest GitHub release from Totox00/Archipelago-sotm,
//      download the first zip asset, extract it into the game directory.
//   3. LAUNCH = run the game exe from the detected/override install; if the exe
//      cannot be found but Steam is present, fall back to steam://rungameid/337150.
//   4. NEWS = fetch GitHub release history from the Totox00/Archipelago-sotm repo.
//
// ── DEFENSIVE NOTES ──────────────────────────────────────────────────────────
//   * "Installed" is judged by the presence of any mod-related DLL or marker file
//     in the game directory deposited by the mod installer.
//   * Steam library parsing is defensive: any failure degrades to "not found"
//     rather than throwing.
//   * No plaintext AP password is ever written to disk by this plugin.
//   * BUILD NOTE: this project sets UseWindowsForms=true alongside UseWPF=true,
//     so all WPF UI types are spelled with their FULL namespaces below (CS0104).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SentinelsOfTheMultiversePlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string GH_OWNER = "Totox00";
    private const string GH_REPO  = "Archipelago-sotm";

    private const string GH_RELEASES_API_URL =
        $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases";
    private const string ModRepoUrl =
        $"https://github.com/{GH_OWNER}/{GH_REPO}";
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Sentinels%20of%20the%20Multiverse/setup/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Sentinels of the Multiverse appid 337150.
    private const string SteamAppId      = "337150";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    private const string SteamCommonFolderName = "Sentinels of the Multiverse";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "sentinels_of_the_multiverse";
    public string DisplayName => "Sentinels of the Multiverse";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against Totox00/Archipelago-sotm.
    public string ApWorldName => "Sentinels of the Multiverse";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "sentinels_of_the_multiverse.png");

    public string ThemeAccentColor => "#2B5FBF";   // heroic blue / comic book
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Sentinels of the Multiverse, the cooperative superhero deckbuilder by " +
        "Greater Than Games, played through the Archipelago mod by Totox00. " +
        "The mod is the in-game Archipelago client, connecting the game to the " +
        "multiworld itself with no emulator and no bridge. Hero and villain " +
        "encounters, environments, and unlocks are shuffled across the multiworld. " +
        "You bring your own copy of Sentinels of the Multiverse (owned on Steam), " +
        "and the Archipelago mod is added on top. The launcher detects your Steam " +
        "install, downloads and installs the mod from GitHub releases, and guides " +
        "you through the rest. You connect to your server from inside the game.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the mod marker is present in the detected/override
    /// Sentinels install directory.
    public bool IsInstalled => FindInstalledModMarker() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and bookkeeping. Exposed as GameDirectory
    /// for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "SentinelsOfTheMultiverse");

    /// This plugin's own settings sidecar (out of the shared SettingsStore so
    /// the plugin stays self-contained — same pattern as HollowKnight / Inscryption).
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "sotm_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The mod reports checks/items/goal to the AP server directly — the launcher
    // relays nothing. These exist for interface compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself / Standalone ───────────────────────────────────────────

    /// The Archipelago SOTM mod owns the slot connection.
    public bool ConnectsItself => true;

    /// Plain (non-AP) Sentinels of the Multiverse runs fine without the mod.
    public bool SupportsStandalone => true;

    /// No checksum-coupled integration (the mod handles its own datapackage sync).
    public string? BuiltAgainstDataPackageChecksum => null;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = FindInstalledModMarker() != null
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
        // 0. Locate the Sentinels install to drop the mod into.
        progress.Report((2, "Locating your Sentinels of the Multiverse installation..."));
        string? gameDir = ResolveSotmDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Sentinels of the Multiverse installation. Open this " +
                "game's Settings and pick your install folder (the one containing the " +
                "game executable), or install Sentinels of the Multiverse via Steam first. " +
                "The Archipelago mod is added on top of your own copy of the game.");

        // 1. Resolve the latest GitHub release (version + zip asset URL).
        progress.Report((6, "Checking the latest Archipelago mod release on GitHub..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a downloadable zip asset in the latest release of the " +
                "Archipelago SOTM mod (" + ModRepoUrl + "). Check your internet " +
                "connection, or download and install the mod manually from the GitHub " +
                "releases page — see Settings for the link. Open Settings for the guided " +
                "steps.");

        // 2. Download + extract the mod zip into the Sentinels install directory.
        await DownloadAndExtractModAsync(zipUrl, version ?? "unknown", gameDir, progress, ct);

        // 3. Stamp the installed version (informational — IsInstalled is judged by
        //    the mod marker's presence, not this stamp).
        WriteStampedVersion(version ?? "unknown");
        InstalledVersion = version;

        progress.Report((100,
            $"Installed Archipelago SOTM mod {version} into your Sentinels of the " +
            "Multiverse folder. To play: launch the game, then follow the in-game " +
            "prompts to enter your Archipelago server address, port, slot name, and " +
            "optional password. This launcher cannot pre-fill the connection — it is " +
            "entered in-game. Open Settings for links and guided steps."));
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
        // HONEST: the AP server connection for Sentinels is entered IN-GAME. There
        // is no documented command-line / config-file prefill this launcher can apply.
        // So launching from this tile just starts the game; the user connects
        // in-game with the session credentials (the settings panel surfaces those).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartSotm();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartSotm();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert ─────────────────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The mod receives items from the AP server directly.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Sentinels of the Multiverse " +
                   "install folder.";

        if (LooksLikeSotmDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeSotmDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Sentinels of the Multiverse installation. " +
               "Pick the folder that contains the game executable (for Steam this is " +
               @"usually ...\steamapps\common\Sentinels of the Multiverse).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2B, 0x5F, 0xBF));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        string? gameDir     = ResolveSotmDir();
        string? overrideDir = LoadOverrideDir();
        string? modMarker   = FindInstalledModMarker();

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Sentinels of the Multiverse is your own game (Steam) with the " +
                   "Archipelago mod by Totox00 added on top. The launcher detects your " +
                   "Steam install and can download and extract the mod from GitHub releases. " +
                   "You connect to your Archipelago server from inside the game — this " +
                   "launcher cannot pre-fill the connection. External steps are not " +
                   "verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: Install detection / override ─────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "SENTINELS OF THE MULTIVERSE INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Sentinels of the Multiverse not detected. Pick your install folder below, " +
              "or install the game via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modMarker != null
                    ? "Archipelago mod found: " + modMarker
                    : "Archipelago mod not found yet. Use the Install button on the " +
                      "Play tab, or download the mod manually from the GitHub link below.",
            FontSize = 11, Foreground = modMarker != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Sentinels of the Multiverse install folder. Detected from " +
                          "Steam automatically; set it here to override (non-standard Steam " +
                          "library, etc.).",
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
                Title            = "Select your Sentinels of the Multiverse install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                                   ? (overrideDir ?? gameDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Sentinels install folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // Descend into the game folder if the user picked the Steam "common" parent.
                if (!LooksLikeSotmDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeSotmDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 337150). Use this " +
                   "picker for a non-standard Steam library or manual install.",
            FontSize = 11, Foreground = muted, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After installing the mod and launching the game, connect to your " +
                   "Archipelago server from inside the game. Enter your server address " +
                   "(e.g. archipelago.gg:38281), slot name, and password (if any). " +
                   "This launcher does not pre-fill the connection — it is entered in-game.",
            FontSize = 11, Foreground = fg, TextWrapping = System.Windows.TextWrapping.Wrap,
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
            "1. Own Sentinels of the Multiverse (on Steam, appid 337150). Install it if " +
                "you have not. Use the folder picker above if it was not detected.",
            "2. Use the Install button on the Play tab — the launcher downloads the latest " +
                "Archipelago SOTM mod release from GitHub and extracts it into your Sentinels " +
                "folder automatically. Alternatively, download the mod manually from the " +
                "GitHub releases page (link below) and extract it there yourself.",
            "3. Launch Sentinels of the Multiverse (via the Play tab or directly from Steam).",
            "4. From inside the game, navigate to the Archipelago connection screen and " +
                "enter your server address, port, slot name, and optional password, then " +
                "connect. You should see a confirmation that you are connected to the " +
                "multiworld.",
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
            ("Archipelago-sotm (GitHub) ↗",            ModRepoUrl),
            ("GitHub Releases (mod download) ↗",       ModRepoUrl + "/releases"),
            ("Sentinels of the Multiverse Setup Guide ↗", SetupGuideUrl),
            ("Archipelago Official ↗",                 ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background      = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0), FontSize = 12,
                Margin   = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor   = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
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
            string json = await _http.GetStringAsync(GH_RELEASES_API_URL, ct);
            using var doc = JsonDocument.Parse(json);
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
                    Version: NormalizeTag(el.TryGetProperty("tag_name", out var t) ? t.GetString() : null) ?? "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var hu) ? hu.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — GitHub release resolution ───────────────────────────

    /// "v1.2.3" → "1.2.3" (strip leading 'v' before a digit). Trimmed otherwise.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Fetch the latest GitHub release for the Archipelago-sotm repo.
    /// Returns (version, first-zip-asset-url). Falls back to (null, null) when
    /// the network is unreachable or no zip asset is found.
    private async Task<(string? Version, string? ZipUrl)> ResolveLatestReleaseAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(
                $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases/latest", ct);
            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = NormalizeTag(
                root.TryGetProperty("tag_name", out var t) ? t.GetString() : null);

            // Find the first zip asset in the release.
            if (root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var an) ? an.GetString() : null;
                    string? url  = asset.TryGetProperty("browser_download_url", out var au)
                                   ? au.GetString()
                                   : null;
                    if (!string.IsNullOrWhiteSpace(url) &&
                        name != null &&
                        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        return (version, url);
                    }
                }
            }

            // No zip asset found — return the version without a download URL.
            return (version, null);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (null, null); }
    }

    // ── Private helpers — Steam / Sentinels detection ─────────────────────────

    /// The Sentinels install dir to use: the override (if set and valid) wins,
    /// else the Steam-detected install. Null when nothing is found.
    private string? ResolveSotmDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeSotmDir(ov))
            return ov;

        try { return DetectSteamSotmDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Sentinels of the Multiverse if it contains the game
    /// executable or the Sentinels_Data folder (Unity-standard layout).
    private static bool LooksLikeSotmDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            // Try several plausible exe names (the exact exe name is not formally
            // documented; these cover the common Unity app naming patterns).
            if (File.Exists(Path.Combine(dir, "SentinelsOfTheMultiverse.exe"))) return true;
            if (File.Exists(Path.Combine(dir, "Sentinels of the Multiverse.exe"))) return true;
            if (File.Exists(Path.Combine(dir, "Sentinels.exe"))) return true;
            if (Directory.Exists(Path.Combine(dir, "SentinelsOfTheMultiverse_Data"))) return true;
            if (Directory.Exists(Path.Combine(dir, "Sentinels of the Multiverse_Data"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Sentinels install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_337150.acf exists → steamapps\common\Sentinels of the Multiverse.
    private static string? DetectSteamSotmDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{SteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common      = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeSotmDir(candidate)) return candidate;
                        // Accept even if exe not yet present (partial install / update).
                        if (Directory.Exists(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeSotmDir(conventional)) return conventional;
                    // Accept conventional path when the folder exists but exe is absent.
                    if (Directory.Exists(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath + last-ditch conventional path).
    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormalizeSteamPath(hkcu);

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormalizeSteamPath(hklm);

        string? hklm2 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm2)) yield return NormalizeSteamPath(hklm2);

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    /// Steam stores its SteamPath with forward slashes; normalize for Path APIs.
    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    /// All Steam library roots: the Steam root itself plus every "path" entry in
    /// steamapps\libraryfolders.vdf (tolerant text scan).
    private static IEnumerable<string> SteamLibraryRoots(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(steamRoot)) yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }

        foreach (string path in ExtractVdfPaths(text))
        {
            string norm = path.Replace('/', '\\').TrimEnd('\\');
            if (norm.Length > 0 && seen.Add(norm))
                yield return norm;
        }
    }

    /// Pull every "path" "<value>" pair out of a libraryfolders.vdf body.
    private static IEnumerable<string> ExtractVdfPaths(string text)
    {
        const string key = "\"path\"";
        int i = 0;
        while ((i = text.IndexOf(key, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            i += key.Length;
            int open = text.IndexOf('"', i);
            if (open < 0) yield break;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) yield break;
            string raw = text.Substring(open + 1, close - open - 1);
            yield return raw.Replace("\\\\", "\\");
            i = close + 1;
        }
    }

    /// Read the "installdir" value from an appmanifest_*.acf. Returns null if absent.
    private static string? ReadAcfInstallDir(string acfPath)
    {
        try
        {
            string text = File.ReadAllText(acfPath);
            const string key = "\"installdir\"";
            int i = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += key.Length;
            int open = text.IndexOf('"', i);
            if (open < 0) return null;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) return null;
            return text.Substring(open + 1, close - open - 1).Replace("\\\\", "\\");
        }
        catch { return null; }
    }

    /// Safe registry string read; null on any failure.
    private static string? ReadRegistryString(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(subKey);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    // ── Private helpers — mod detection ───────────────────────────────────────

    /// Find a marker indicating the Archipelago SOTM mod is installed.
    /// Looks for any file in the game directory (or a BepInEx plugins sub-folder)
    /// whose name contains "archipelago" (case-insensitive). Returns the found
    /// path or null.
    private string? FindInstalledModMarker()
    {
        try
        {
            string? game = ResolveSotmDir();
            if (game == null || !Directory.Exists(game)) return null;

            // Check for an Archipelago-named DLL or file anywhere in BepInEx\plugins.
            string pluginsDir = Path.Combine(game, "BepInEx", "plugins");
            if (Directory.Exists(pluginsDir))
            {
                foreach (string f in Directory.EnumerateFiles(pluginsDir, "*", SearchOption.AllDirectories))
                {
                    if (Path.GetFileNameWithoutExtension(f)
                        .IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                        return f;
                }
            }

            // Also check for Archipelago-named files directly in the game root
            // (some Unity mods patch the root, not BepInEx).
            foreach (string f in Directory.EnumerateFiles(game, "*", SearchOption.TopDirectoryOnly))
            {
                string fn = Path.GetFileNameWithoutExtension(f);
                if (fn.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return f;
            }

            // Check for a dedicated sub-folder named "Archipelago" in the root or
            // in a Mods/Plugins sibling directory.
            foreach (string sub in Directory.EnumerateDirectories(game, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(sub).IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return sub;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Sentinels of the Multiverse: prefer the exe in the detected/override
    /// install; if that cannot be found but Steam is present, fall back to the
    /// steam:// URL.
    private void StartSotm()
    {
        string? game = ResolveSotmDir();
        string? exe  = FindGameExe(game);

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = game!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException(
                    "Failed to start Sentinels of the Multiverse.");

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
            return;
        }

        // Fall back to Steam if available.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { /* fall through to the error below */ }
        }

        throw new FileNotFoundException(
            "Could not find the Sentinels of the Multiverse executable. Open this " +
            "game's Settings and pick your install folder, or install the game via Steam.",
            "SentinelsOfTheMultiverse.exe");
    }

    /// Find the game executable in an install directory, trying several plausible
    /// names in the order most likely for this game's Unity build.
    private static string? FindGameExe(string? gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            return null;

        foreach (string name in new[]
        {
            "SentinelsOfTheMultiverse.exe",
            "Sentinels of the Multiverse.exe",
            "Sentinels.exe",
        })
        {
            string path = Path.Combine(gameDir, name);
            if (File.Exists(path)) return path;
        }

        // Last resort: any .exe in the root that is not a Unity crashpad/crash helper.
        try
        {
            foreach (string f in Directory.EnumerateFiles(gameDir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string fn = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (!fn.Contains("crash") && !fn.Contains("helper") &&
                    !fn.Contains("report") && !fn.Contains("unity"))
                    return f;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod zip from GitHub and extract it into the game directory.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string gameDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"sotm-archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"sotm-archipelago-x-{version}-{Guid.NewGuid():N}");
        try
        {
            // Download with progress.
            progress.Report((10, $"Downloading Archipelago SOTM mod {version}..."));
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
                        int pct = (int)(10 + 55 * downloaded / total);
                        progress.Report((pct, $"Downloading mod... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            // Extract to a temp dir first, then copy into the game directory.
            progress.Report((70, "Extracting mod files..."));
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            progress.Report((85, "Installing mod files into the Sentinels folder..."));
            CopyDirectoryContents(tempDir, gameDir);

            progress.Report((97, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))  File.Delete(tempZip); }        catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// Recursively copy the CONTENTS of a source directory into a destination
    /// directory (merging / overwriting).
    private static void CopyDirectoryContents(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class SotmSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private SotmSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<SotmSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(SotmSettings s)
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
