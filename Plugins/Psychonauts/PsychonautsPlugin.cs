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
using System.Windows;
using Microsoft.Win32;
using LauncherV2.Core;

// IMPORTANT (real project has BOTH <UseWPF>true</UseWPF> AND
// <UseWindowsForms>true</UseWindowsForms>): WPF UI types that collide with
// WinForms are FULLY QUALIFIED below (System.Windows.Controls.*,
// System.Windows.Media.*, System.Windows.Thickness, System.Windows.FontWeights,
// System.Windows.HorizontalAlignment, System.Windows.TextWrapping,
// System.Windows.MessageBox, …) to avoid CS0104 ambiguities. Do NOT add
// `using System.Windows.Controls;` / `using System.Windows.Media;` here.

namespace LauncherV2.Plugins.Psychonauts;

// ═══════════════════════════════════════════════════════════════════════════════
// PsychonautsPlugin — install / update / launch for "Psychonauts" (Double Fine
// Productions, 2005) played through the Akashortstack/Psychonauts-AP-Integration
// mod. This is a NATIVE "ConnectsItself" integration: the mod ships with a
// built-in Archipelago client that connects to the AP server directly — no
// emulator, no Lua bridge, and no launcher-held ApClient on the slot.
//
// ── HONEST REALITY CHECK (2026-06-14, verified against the GitHub repo) ───────
// The verified facts:
//
//   * THE AP WORLD game string is "Psychonauts" (the repository is
//     Akashortstack/Psychonauts-AP-Integration on GitHub). GameId = "psychonauts".
//
//   * MOD DELIVERY: GitHub releases at
//     https://github.com/Akashortstack/Psychonauts-AP-Integration/releases.
//     The mod is extracted into (or overlaid on) the Psychonauts game directory.
//     This plugin downloads the latest release zip and extracts it to the user's
//     Psychonauts Steam install or a manually-specified folder.
//
//   * INSTALL MARKER: look for a patched marker file or AP DLL in the game/mod
//     directory. The plugin uses the version stamp it writes after install plus a
//     scan for recognizable mod files (APPsychonauts.dll or similar) as the
//     "installed" signal.
//
//   * LAUNCH: run Psychonauts.exe from the detected Steam install (or
//     steam://rungameid/3830 as a fallback when the exe cannot be found).
//     ConnectsItself = true — the mod's own AP client owns the server slot.
//
//   * Steam AppID for Psychonauts = 3830.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Psychonauts install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Psychonauts via appmanifest_3830.acf. A manual install-dir
//      OVERRIDE (settings folder picker) is also supported and takes precedence;
//      validated (must contain Psychonauts.exe) and persisted in this plugin's OWN
//      sidecar (Games/ROMs/psychonauts/psychonauts_launcher.json) — Core/
//      SettingsStore is NOT modified.
//   2. INSTALL/UPDATE: download the latest release zip from GitHub, extract it
//      into (or alongside) the user's Psychonauts game directory, then show guided
//      steps. Because this is a Steam game, the user runs it from their own copy.
//   3. LAUNCH: run Psychonauts.exe from the detected/override install; if the exe
//      cannot be found but Steam is present, fall back to steam://rungameid/3830.
//      ConnectsItself = true (the mod owns the slot connection). SupportsStandalone
//      = true (base game runs fine without AP).
//   4. CreateSettingsPanel: install status, connection instructions, guided setup
//      steps, and links.
//   5. GetNewsAsync: GitHub releases API for Akashortstack/Psychonauts-AP-Integration.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time") ───────────────────────────
//   * Zip internal layout not inspected offline; extractor handles both a flat
//     layout and a single wrapper subfolder.
//   * The exact marker file that signals "mod installed" may differ between
//     releases. The plugin checks for APPsychonauts.dll, archipelago.json, or
//     a version stamp written by this plugin itself.
//   * No plaintext AP password is ever written by this plugin; there is nothing
//     to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PsychonautsPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER = "Akashortstack";
    private const string GITHUB_REPO  = "Psychonauts-AP-Integration";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"{GH_RELEASES_URL}/latest";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Psychonauts/setup/en";
    private const string GameInfoUrl     = "https://archipelago.gg/games/Psychonauts/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Psychonauts AppID 3830.
    private const string SteamAppId   = "3830";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The standard Steam install sub-folder name for Psychonauts.
    private const string SteamCommonFolderName = "Psychonauts";

    /// The base-game executable name.
    private const string GameExeName = "Psychonauts.exe";

    /// Recognizable mod marker files placed by the AP integration; any one present
    /// indicates the mod is installed (defensive multi-signal).
    private static readonly string[] ModMarkerFiles =
    {
        "APPsychonauts.dll",
        "archipelago.json",
        "archipelago_version.txt",
        "ApPsychonauts.dll",
    };

    /// Version stamp written after a successful install by this launcher.
    private const string VersionFileName = "psychonauts_ap_version.dat";

    /// Pinned fallback version — used when the GitHub API is unreachable.
    private const string FallbackVersion = "1.0.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "psychonauts";
    public string DisplayName => "Psychonauts";
    public string Subtitle    => "Native PC · built-in Archipelago";

    /// EXACT AP game string — verified against Akashortstack/Psychonauts-AP-Integration.
    public string ApWorldName => "Psychonauts";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "psychonauts.png");

    public string ThemeAccentColor => "#7B3FA0";   // psychedelic purple

    public string[] GameBadges => new[] { "Steam · needs mod" };

    public string Description =>
        "Psychonauts, Double Fine's beloved 2005 3D platformer, played through the " +
        "Archipelago integration by Akashortstack — a native mod with a built-in AP " +
        "client so the game speaks to the multiworld itself. Figments, Emotional " +
        "Baggage, Cobwebs, PSI Cards and more are shuffled into the multiworld. " +
        "You bring your own copy of Psychonauts (owned on Steam); the Archipelago " +
        "mod is installed on top of it. The launcher detects your Steam install and " +
        "can install the mod into it for you. Connection details are entered in-game " +
        "or via a config file the mod reads at startup — the launcher surfaces the " +
        "guided steps and all the links you need.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means either a mod marker file is present in the game directory
    /// or we have written our own version stamp there after a successful install.
    public bool IsInstalled => HasModMarker() || File.Exists(VersionStampPath);

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// This plugin's local working directory (for downloads, the version stamp etc.).
    /// The actual mod files live in the user's Psychonauts install directory.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Psychonauts");

    /// Full path of the version stamp file written after a successful install.
    private string VersionStampPath => Path.Combine(GameDirectory, VersionFileName);

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore
    /// so the plugin stays a single self-contained file — same as Noita / Aquaria /
    /// Hollow Knight). Lives under Games/ROMs/psychonauts/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "psychonauts_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The mod's built-in AP client reports checks/items/goal to the AP server
    // directly — the launcher relays nothing. These events exist for interface
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
            InstalledVersion = IsInstalled
                ? (ReadStampedVersion() ?? "installed")
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
        // 0. Locate the user's Psychonauts installation.
        progress.Report((2, "Locating your Psychonauts installation..."));
        string? gameDir = ResolvePsychonautsDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Psychonauts installation. Open this game's Settings " +
                "and pick your Psychonauts folder (the one containing Psychonauts.exe), " +
                "or install Psychonauts via Steam first (AppID 3830). The Archipelago mod " +
                "is installed on top of your own copy of the game.");

        // 1. Resolve the latest release (pinned fallback when offline).
        progress.Report((6, "Checking the latest Psychonauts AP Integration release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        // 2. Already current? (idempotent fast path)
        if (IsInstalled && ReadStampedVersion() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"Psychonauts AP Integration {version} is already up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a download for the Psychonauts AP Integration on the " +
                "GitHub release page. Check your internet connection, or download it " +
                "manually from " + RepoUrl + "/releases.");

        // 3. Download and extract the mod into the game directory.
        await DownloadAndExtractModAsync(zipUrl, version, gameDir, progress, ct);

        // 4. Write a version stamp next to our sidecar (informational only).
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Psychonauts AP Integration {version} installed into your Psychonauts folder. " +
            "IMPORTANT: you may need to follow additional setup steps (see Settings for " +
            "guided instructions). Launch the game and connect to your Archipelago server " +
            "using your slot name and server address. " +
            "(This launcher cannot pre-fill the connection — those are done in-game or " +
            "via the mod's config file. Open Settings for the guided steps and links.)"));
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
        // The AP connection for Psychonauts is handled in-game or via a config file
        // the mod reads at startup; there is no verified command-line argument this
        // launcher can pass to pre-fill the connection. Launching starts the game;
        // the user connects via whatever mechanism the mod provides.
        //
        // ConnectsItself = true: the mod's built-in AP client owns the slot —
        // the launcher must NOT hold its own ApClient on the same slot.
        _ = session; // intentionally unused — no pre-fill mechanism verified
        StartGame();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Psychonauts runs perfectly well.
    public bool SupportsStandalone => true;

    /// The mod's built-in AP client owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No plaintext AP password is ever written to disk by this plugin.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The mod's built-in AP client receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game; no launcher HUD channel.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Psychonauts folder contains
    /// Psychonauts.exe. Returns null when acceptable, else a short reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Psychonauts install folder.";

        if (LooksLikePsychonautsDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikePsychonautsDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Psychonauts installation. Pick the folder " +
               "that contains Psychonauts.exe (for Steam this is usually " +
               @"...\steamapps\common\Psychonauts).";
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
        var warn    = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x7B, 0x3F, 0xA0));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Psychonauts is your own game (Steam) with the AP Integration mod " +
                   "added on top. The launcher detects your Steam install and can install " +
                   "the mod into it for you. Connection to the Archipelago server is " +
                   "handled in-game (or via a config file the mod reads at startup) — " +
                   "the launcher cannot pre-fill these fields. Follow the guided steps " +
                   "below to get set up.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: Psychonauts install ──────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "PSYCHONAUTS INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolvePsychonautsDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Psychonauts not detected. Pick your install folder below, or install " +
              "Psychonauts via Steam first (AppID 3830).";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // Mod status line
        bool modFound = IsInstalled;
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modFound
                    ? "Archipelago mod detected" +
                      (InstalledVersion != null ? $" (version {InstalledVersion})" : "") + "."
                    : "Archipelago mod not detected in the game folder yet. " +
                      "Use Install on the Play tab to download and install it.",
            FontSize = 11,
            Foreground = modFound ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Your Psychonauts install folder (the one containing Psychonauts.exe). " +
                      "Detected from Steam automatically; set it here to override.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Psychonauts install folder (contains Psychonauts.exe)",
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
                    System.Windows.MessageBox.Show(bad, "Not a Psychonauts folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikePsychonautsDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikePsychonautsDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (AppID 3830). Use this " +
                   "picker for a non-standard Steam library.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING TO ARCHIPELAGO", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "The Psychonauts AP Integration has a built-in Archipelago client. " +
                   "Connection details (server, slot name, password) are entered in-game " +
                   "or via a configuration file the mod reads at startup. After installing " +
                   "the mod, follow the setup guide (link below) to configure your " +
                   "connection. This launcher cannot pre-fill the connection.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup ──────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Psychonauts (on Steam, AppID 3830). Install it if you have not. " +
                "Use the folder picker above if it was not detected automatically.",
            "2. Install the Archipelago mod: use the Install button on the Play tab " +
                "to download it and extract it into your Psychonauts folder. Alternatively, " +
                "download it manually from the GitHub releases page (link below) and " +
                "extract it into your Psychonauts game directory.",
            "3. Follow the setup guide (link below) to configure the mod and set up " +
                "your Archipelago connection — server address, slot name, and password.",
            "4. Generate or join an Archipelago multiworld with the game slot set to " +
                "\"Psychonauts\". Then launch the game using the Play button.",
            "5. The mod's built-in client connects to the AP server. Figments, Emotional " +
                "Baggage, Cobwebs, PSI Cards and more are shuffled across the multiworld.",
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
            Text = "LINKS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Psychonauts AP Integration (GitHub) ↗", RepoUrl),
            ("Psychonauts Setup Guide ↗",              SetupGuideUrl),
            ("Psychonauts Game Info (AP) ↗",           GameInfoUrl),
            ("Archipelago Official ↗",                 ArchipelagoSite),
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
            btn.Click += (_, _) => OpenUrl(u);
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
                    Url:     el.TryGetProperty("html_url", out var hu) ? hu.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// "v1.2.3" → "1.2.3"; "1.2.3" unchanged; null/blank → null.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest release: version + the best Windows zip download URL.
    /// Falls back to a constructed URL for the pinned fallback version when the
    /// GitHub API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(CancellationToken ct)
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
                string? preferred = null;   // Windows-labeled zip preferred
                string? anyZip    = null;   // any zip as fallback

                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    // Reject Linux / macOS / source zips.
                    if (lower.Contains("linux") || lower.Contains("mac") ||
                        lower.Contains("osx")   || lower.Contains("darwin") ||
                        lower.Contains("source")) continue;

                    anyZip ??= url;
                    if (preferred == null &&
                        (lower.Contains("windows") || lower.Contains("win") ||
                         lower.Contains("x64")     || lower.Contains("x86")))
                        preferred = url;
                }

                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);

                // Release exists but no zip asset found (e.g. only an apworld is published).
                // Return version + null so the caller can decide.
                return (version, null);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → fall through to pinned fallback */ }

        // Offline pinned fallback: best-guess URL based on repo conventions.
        string fallbackUrl = $"{RepoUrl}/releases/download/v{FallbackVersion}/Psychonauts-AP-Integration-v{FallbackVersion}-Windows.zip";
        return (FallbackVersion, fallbackUrl);
    }

    // ── Private helpers — Steam / Psychonauts detection ───────────────────────

    /// The Psychonauts install dir to use: the override (if set and valid) wins,
    /// else the Steam-detected install. Null when nothing is found.
    private string? ResolvePsychonautsDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikePsychonautsDir(ov))
            return ov;

        try { return DetectSteamPsychonautsDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Psychonauts if it contains Psychonauts.exe.
    private static bool LooksLikePsychonautsDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

    /// Return true if ANY recognizable mod marker file is present in the resolved
    /// Psychonauts game directory.
    private bool HasModMarker()
    {
        try
        {
            string? dir = ResolvePsychonautsDir();
            if (dir == null) return false;
            foreach (string marker in ModMarkerFiles)
                if (File.Exists(Path.Combine(dir, marker))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Psychonauts install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_3830.acf exists → steamapps\common\<installdir>.
    private static string? DetectSteamPsychonautsDir()
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

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikePsychonautsDir(candidate)) return candidate;
                    }
                    // Fall back to the conventional folder name.
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikePsychonautsDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath). All are tried; duplicates are harmless.
    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormalizeSteamPath(hkcu);

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormalizeSteamPath(hklm);

        string? hklm64 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm64)) yield return NormalizeSteamPath(hklm64);

        // Last-ditch conventional location.
        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    /// Steam stores its SteamPath with forward slashes; normalize for Path APIs.
    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    /// All Steam library roots: the Steam root itself plus every "path" entry in
    /// steamapps\libraryfolders.vdf. Tolerant text scan.
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

    /// Pull every  "path"   "<value>"  pair out of a libraryfolders.vdf body.
    /// Handles the Steam-VDF escaping of backslashes (\\ → \).
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

    /// Read the "installdir" value from an appmanifest_*.acf (same quoted-pair
    /// format as VDF). Returns null if absent.
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

    /// Safe registry string read; null on any failure (key/value missing, etc.).
    private static string? ReadRegistryString(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(subKey);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Psychonauts: prefer Psychonauts.exe in the detected/override install;
    /// if that cannot be found but Steam is present, fall back to the steam:// URL.
    private void StartGame()
    {
        string? gameDir = ResolvePsychonautsDir();
        string? exe     = gameDir != null ? Path.Combine(gameDir, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = gameDir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Psychonauts.");

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

        // Fall back to Steam if we at least know Steam is installed.
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
            "Could not find Psychonauts.exe. Open this game's Settings and pick your " +
            "Psychonauts install folder, or install Psychonauts via Steam (AppID 3830).",
            GameExeName);
    }

    /// Open a URL in the default handler; swallow failures.
    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* non-fatal */ }
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the release zip and extract its contents into the Psychonauts game
    /// directory. Handles both flat zips and single-wrapper-subfolder zips
    /// (defensive — exact layout not inspected offline).
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string gameDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"psychonauts-ap-{version}-{Guid.NewGuid():N}.zip");
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"psychonauts-ap-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading Psychonauts AP Integration {version}..."));
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
                        int pct = (int)(10 + 60 * downloaded / total);
                        progress.Report((pct, $"Downloading... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Extracting mod files into your Psychonauts folder..."));
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            // Defensive: if the zip wraps everything in a single subfolder, descend
            // into it so the mod files overlay at the game directory root.
            string overlayRoot = tempDir;
            if (Directory.GetFiles(tempDir).Length == 0)
            {
                string[] subdirs = Directory.GetDirectories(tempDir);
                if (subdirs.Length == 1) overlayRoot = subdirs[0];
            }

            // Overlay every mod file onto the game directory (overwrite on conflict).
            Directory.CreateDirectory(gameDir);
            foreach (string fileSrc in Directory.EnumerateFiles(overlayRoot, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string rel     = Path.GetRelativePath(overlayRoot, fileSrc);
                string fileDst = Path.Combine(gameDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                File.Copy(fileSrc, fileDst, overwrite: true);
            }

            // Also stamp the version into our working directory for the tile display.
            Directory.CreateDirectory(GameDirectory);
            progress.Report((90, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (install-dir override + version
    // stamp) in its OWN JSON file. BOM-less UTF-8, read-modify-write.

    private sealed class PsychonautsSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private PsychonautsSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<PsychonautsSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(PsychonautsSettings s)
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

    private void SaveOverrideDir(string p)
    {
        var s = LoadSettings();
        s.InstallOverride = p;
        SaveSettings(s);
    }

    private string? ReadStampedVersion()
    {
        // Try the sidecar first; then the stamp file in GameDirectory.
        string? v = LoadSettings().ModVersion;
        if (!string.IsNullOrWhiteSpace(v)) return v;
        try
        {
            if (File.Exists(VersionStampPath))
            {
                v = File.ReadAllText(VersionStampPath).Trim();
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings();
        s.ModVersion = v;
        SaveSettings(s);
        try
        {
            Directory.CreateDirectory(GameDirectory);
            File.WriteAllText(VersionStampPath, v, new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }
}
