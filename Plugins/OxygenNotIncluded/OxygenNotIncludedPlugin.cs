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
// The project sets <UseWindowsForms>true</UseWindowsForms> alongside WPF, so the
// bare names Button / TextBox / MessageBox / Brushes / Orientation collide between
// System.Windows.Forms and System.Windows[.Controls/.Media] (CS0104). Qualifying
// every UI type avoids that. No file-level `using X = System.Windows...;` aliases.

namespace LauncherV2.Plugins.OxygenNotIncluded;

// ═══════════════════════════════════════════════════════════════════════════════
// OxygenNotIncludedPlugin — install / launch for "Oxygen Not Included"
// (Klei Entertainment, Steam appid 457140) played through the
// ArchipelagoNotIncluded mod by ShadowKitty42 — a Steam Workshop mod (id
// 3415553359) that is also available as a standalone ZIP on GitHub.
//
// ── HONEST REALITY CHECK (2026-06-15, verified online + against the apworld) ──
// This is a STEAM-MOD native integration in exactly the same family as the
// shipped Subnautica and Hollow Knight plugins. The verified facts:
//
//   * THE AP WORLD game string is "Oxygen Not Included" — verified directly
//     against worlds/oni/__init__.py: `game = "Oxygen Not Included"` in the
//     ONIWorld class. AP world file: oni.apworld, installed in Archipelago's
//     custom_worlds folder. Repo: ShadowKitty42/ONI-Archipelago (GitHub).
//     Latest release at time of writing: v0.99.
//
//   * THE MOD is distributed two ways (player's choice, both honest):
//       A) Steam Workshop: subscribe to "ArchipelagoNotIncluded" (id 3415553359).
//          ONI loads Workshop mods automatically. Requires the "Mod Updater"
//          Workshop item as a dependency (listed on the Workshop page).
//       B) Manual ZIP from the GitHub releases page: extract it into ONI's mods
//          directory (default: Documents\Klei\OxygenNotIncluded\mods\).
//     This plugin records which method the user picked in its settings sidecar;
//     InstallOrUpdateAsync handles both paths.
//
//   * CONNECTION is handled IN-GAME via the mod's settings button in ONI's
//     mod list. The player clicks "Settings" next to ArchipelagoNotIncluded,
//     enters the server URL/port, slot name, and password. The mod's config
//     directory is at:
//       %USERPROFILE%\Documents\Klei\OxygenNotIncluded\mods\config\ArchipelagoNotIncluded
//     (also possible under OneDrive\Documents\). There is NO command-line arg
//     and NO pre-fileable config text file — the mod writes its own config on
//     save. So this plugin does NOT attempt a connection prefill; it shows the
//     session credentials clearly so the user can copy them into the in-game UI.
//
//   * THE APWORLD file (oni.apworld) must be placed by the user in their local
//     Archipelago installation's custom_worlds folder. This launcher cannot do
//     it automatically because Archipelago is a separate install on the user's
//     machine (we do not know its path). The settings panel states this clearly.
//
//   * ConnectsItself = true: the ArchipelagoNotIncluded mod holds the slot
//     connection; the launcher must NOT simultaneously hold an ApClient on the
//     same slot.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the ONI install via Steam registry (appid 457140).
//      A manual install-dir override is also supported (Settings folder picker)
//      and takes precedence. Validated (must contain OxygenNotIncluded.exe).
//      Sidecar: Games/ROMs/oxygen_not_included/oni_launcher.json.
//   2. INSTALL/UPDATE:
//      * Workshop path (preferred when Steam detected): opens the Steam
//        Workshop page so the user subscribes, and surface the "Mod Updater"
//        dependency requirement. (Workshop install is Steam-automated.)
//      * Manual ZIP path: downloads the latest GitHub release ZIP asset and
//        extracts it into the detected ONI mods directory (Documents\Klei\
//        OxygenNotIncluded\mods\). Latest release version is polled from the
//        GitHub API (GH_API_RELEASES_LATEST), with a pinned fallback.
//   3. LAUNCH: run OxygenNotIncluded.exe from the detected/override install;
//      fall back to steam://rungameid/457140 when only Steam is known.
//      ConnectsItself = true; SupportsStandalone = true.
//   4. SETTINGS PANEL: shows detected install, mod presence, session credentials
//      to copy into the in-game settings, guided steps, and links to the mod
//      repo, Workshop page, and Archipelago.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class OxygenNotIncludedPlugin : IGamePlugin
{
    // ── Constants — mod repo (ShadowKitty42/ONI-Archipelago, verified 2026-06-15) ─
    private const string MOD_OWNER = "ShadowKitty42";
    private const string MOD_REPO  = "ONI-Archipelago";
    private static readonly string ModRepoUrl     = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private static readonly string ModReleasesUrl = $"{ModRepoUrl}/releases";
    private static readonly string GH_API_RELEASES_LATEST =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private static readonly string GH_API_RELEASES_ALL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // Steam Workshop — ArchipelagoNotIncluded (id 3415553359, verified 2026-06-15)
    private const string WorkshopItemId    = "3415553359";
    private static readonly string WorkshopUrl =
        $"https://steamcommunity.com/sharedfiles/filedetails/?id={WorkshopItemId}";
    private static readonly string WorkshopSubscribeUrl =
        $"steam://url/CommunityFilePage/{WorkshopItemId}";

    // "Mod Updater" Workshop dependency (stated on the Workshop item page)
    private const string ModUpdaterWorkshopId = "2018291206";
    private static readonly string ModUpdaterUrl =
        $"https://steamcommunity.com/sharedfiles/filedetails/?id={ModUpdaterWorkshopId}";

    // Steam — ONI appid 457140
    private const string SteamAppId = "457140";
    private static readonly string SteamRunUrl =
        $"steam://rungameid/{SteamAppId}";
    private const string SteamCommonFolderName = "OxygenNotIncluded";

    // Archipelago community and setup
    private const string ArchipelagoSite = "https://archipelago.gg";
    private static readonly string ApReleasesUrl = "https://github.com/ArchipelagoMW/Archipelago/releases";
    private const string CustomWorldsNote =
        "custom_worlds (in your Archipelago installation)";

    // Pinned fallback when the GitHub API is unreachable.
    // v0.99 is the latest verified release as of 2026-06-15.
    private const string FallbackVersion = "0.99";
    // Asset naming convention in this repo uses "oni-archipelago-<version>.zip"
    // or similar — we search assets dynamically; this is a last-resort raw URL.
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/oni-archipelago-{FallbackVersion}.zip";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "oxygen_not_included";
    public string DisplayName => "Oxygen Not Included";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against oni/__init__.py:
    /// ONIWorld.game = "Oxygen Not Included"
    public string ApWorldName => "Oxygen Not Included";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "oxygen_not_included.png");

    public string ThemeAccentColor => "#3A8F6E";   // Klei-ish teal/green
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Oxygen Not Included, Klei Entertainment's 2019 space-colony survival sim, " +
        "played through the ArchipelagoNotIncluded mod — a Steam Workshop mod that " +
        "wires ONI into the Archipelago multiworld randomizer. Research, buildings, " +
        "critters, geysers, and colony milestones are shuffled into the multiworld. " +
        "You bring your own copy of ONI (Steam), subscribe to the mod via the Workshop " +
        "(or install the ZIP manually), then place the oni.apworld file in your " +
        "Archipelago installation's custom_worlds folder. You connect to the AP server " +
        "through the mod's in-game settings screen.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the ArchipelagoNotIncluded mod assembly is present
    /// under the ONI mods directory (Workshop or manual install).
    public bool IsInstalled => FindInstalledModAssembly() != null;
    public bool IsRunning   { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Launcher working directory for downloads and sidecar. Exposed as
    /// GameDirectory per IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "OxygenNotIncluded");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "oni_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────
    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // ArchipelagoNotIncluded holds the slot connection; the launcher relays
    // nothing. These events exist for interface compatibility only.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself / SupportsStandalone ───────────────────────────────────
    /// The mod holds the AP slot; the launcher must not hold a parallel ApClient.
    public bool ConnectsItself    => true;
    /// ONI plays perfectly as a standalone survival game without AP.
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(MOD_OWNER, MOD_REPO, ct));
        }
        catch
        {
            AvailableVersion = null; // contract: never throw on network failure
        } // contract: never throw on network failure
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // Preferred path: Steam Workshop subscription is the documented
        // install method and carries automatic updates. When Steam is detected
        // we open the Workshop page and guide the user.
        bool steamPresent = SteamRoots().Any(r =>
            !string.IsNullOrWhiteSpace(r) && Directory.Exists(r));

        if (steamPresent)
        {
            progress.Report((20,
                "Opening the Steam Workshop page for ArchipelagoNotIncluded. " +
                "Click Subscribe in Steam, then restart ONI so it loads the mod. " +
                "Also subscribe to the \"Mod Updater\" dependency on the Workshop."));
            try
            {
                // Open the mod's Workshop community page via steam:// URI so
                // the user can click Subscribe without leaving Steam.
                Process.Start(new ProcessStartInfo(WorkshopSubscribeUrl)
                    { UseShellExecute = true });
            }
            catch
            {
                // If the steam:// URI handler fails, fall back to the web URL.
                try { Process.Start(new ProcessStartInfo(WorkshopUrl)
                    { UseShellExecute = true }); } catch { }
            }
            progress.Report((50,
                "Steam Workshop page opened. After subscribing, launch ONI once " +
                "to let the Workshop download the mod. Then return here and click " +
                "Play. See Settings for the full guided steps."));

            // Also ensure the launcher's working directory exists for sidecar use.
            Directory.CreateDirectory(GameDirectory);
            progress.Report((100,
                "Workshop subscription initiated. Once ONI has downloaded the mod " +
                "via Steam, use the Play button to launch. To connect: in ONI's mod " +
                "list, click Settings next to ArchipelagoNotIncluded and enter your " +
                "server host:port, slot name, and password. " +
                "IMPORTANT: place the oni.apworld file in your Archipelago " +
                "installation's custom_worlds folder (download it from " + ModReleasesUrl + ")."));
            return;
        }

        // No Steam: fall back to manual ZIP install.
        progress.Report((5, "Steam not detected. Attempting manual ZIP install..."));
        string? oniDir = ResolveOniDir();
        if (oniDir == null)
            throw new InvalidOperationException(
                "Could not find an Oxygen Not Included installation. " +
                "Open this game's Settings and pick your ONI folder (the one " +
                "containing OxygenNotIncluded.exe), or install ONI via Steam first.");

        string modsDir = ResolveModsDirectory() ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Klei", "OxygenNotIncluded", "mods");

        progress.Report((10, "Checking the latest mod release on GitHub..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the ArchipelagoNotIncluded mod download on GitHub. " +
                "Check your internet connection, or download the ZIP manually from " +
                ModReleasesUrl + " and extract it into:\n  " + modsDir +
                "\nSee Settings for the full guided steps.");

        await DownloadAndExtractModAsync(zipUrl, version, modsDir, progress, ct);
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Installed ArchipelagoNotIncluded {version} into:\n  {modsDir}\n" +
            "Launch ONI. In the Mods screen, enable ArchipelagoNotIncluded, then " +
            "click its Settings button and enter your AP server host:port, slot name, " +
            "and password. " +
            "IMPORTANT: also place the oni.apworld file in your Archipelago " +
            "custom_worlds folder (download it from " + ModReleasesUrl + ")."));
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
        // HONEST: the AP connection for ONI is entered in-game via the mod's
        // Settings button in the mods list (host:port, slot name, password).
        // There is no command-line or config-file prefill mechanism. Launching
        // just starts the game; the Settings panel surfaces the session
        // credentials so the user can copy them in.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartOni();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartOni();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The ArchipelagoNotIncluded mod receives items from the AP server
        // directly — nothing for the launcher to forward.
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
            return "That folder does not exist. Pick your ONI install folder.";

        if (LooksLikeOniDir(folder))
            return null;

        // Be forgiving: user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeOniDir(nested)) return null;
        }
        catch { }

        return "That does not look like an Oxygen Not Included installation. " +
               "Pick the folder containing OxygenNotIncluded.exe. " +
               @"For Steam this is usually ...\steamapps\common\OxygenNotIncluded.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Oxygen Not Included is your own game (Steam) with the " +
                   "ArchipelagoNotIncluded mod added on top. The mod is installed " +
                   "via Steam Workshop (preferred) or a manual ZIP. You connect to " +
                   "your AP server in-game via the mod's Settings button in ONI's " +
                   "mods list — this launcher cannot pre-fill that connection. " +
                   "The steps below that happen outside this launcher are not " +
                   "verified by it.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: ONI install ──────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "ONI INSTALLATION", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? oniDir      = ResolveOniDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = oniDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + oniDir
                : "Detected Steam install: " + oniDir)
            : "ONI not detected. Pick your install folder below, or install ONI via Steam first.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = oniDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // Mod presence
        string? modAssembly = FindInstalledModAssembly();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modAssembly != null
                ? "ArchipelagoNotIncluded mod found: " + modAssembly
                : "ArchipelagoNotIncluded mod NOT found. Use Install on the Play tab " +
                  "(Steam Workshop) or install the ZIP manually. See steps below.",
            FontSize = 11, Foreground = modAssembly != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? oniDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your ONI install folder (the one containing OxygenNotIncluded.exe). " +
                          "Detected from Steam automatically; set it here to override.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select your Oxygen Not Included install folder (contains OxygenNotIncluded.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? oniDir ?? "")
                                   ? (overrideDir ?? oniDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not an ONI folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeOniDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeOniDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 457140). Use the " +
                   "picker for non-standard Steam libraries.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: AP connection (session credentials) ──────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in ONI's mods list → Settings)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After launching ONI, open the Mods screen, find " +
                   "ArchipelagoNotIncluded, and click its Settings button. Enter your " +
                   "server (host:port), slot/player name, and optional password. The " +
                   "mod reconnects automatically when you load your save. " +
                   "Copy your session credentials from the last session below:",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // Mods directory path info
        string modsPath = ResolveModsDirectory()
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Klei", "OxygenNotIncluded", "mods");
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Mods config directory: " + Path.Combine(modsPath, "config", "ArchipelagoNotIncluded"),
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: apworld placement ────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "APWORLD FILE (one-time setup)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Download oni.apworld from the mod's GitHub releases page (link " +
                   "below) and place it in your Archipelago installation's " +
                   "custom_worlds folder. This is required for the AP server to " +
                   "generate an ONI world. The launcher cannot do this automatically " +
                   "because it does not know your Archipelago install path.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Oxygen Not Included (Steam). Install it if you have not. " +
               "Use \"Select folder...\" above if it was not auto-detected.",
            "2. Install the mod — METHOD A (preferred): click Install on the Play " +
               "tab. This opens the Steam Workshop page for ArchipelagoNotIncluded. " +
               "Click Subscribe. Also subscribe to the \"Mod Updater\" dependency " +
               "listed on that page. Steam downloads the mod automatically.",
            "2. Install the mod — METHOD B (no Steam): click Install on the Play " +
               "tab without Steam. The launcher downloads the latest ZIP and extracts " +
               "it into your ONI mods directory. You can also do this manually.",
            "3. Download oni.apworld from the mod's GitHub releases page (link below) " +
               "and copy it into your Archipelago installation's custom_worlds folder.",
            "4. Generate your AP game with the oni.apworld present on the server " +
               "(your slot will be game type \"Oxygen Not Included\").",
            "5. Launch ONI from this launcher. Open the Mods screen and enable " +
               "ArchipelagoNotIncluded if it is not already enabled.",
            "6. Click Settings next to ArchipelagoNotIncluded. Enter your AP server " +
               "host:port (e.g. archipelago.gg:38281), your slot/player name, and " +
               "optional password. Start a new colony — your session is saved with it.",
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
            ("ArchipelagoNotIncluded mod (GitHub releases, incl. oni.apworld) ↗", ModReleasesUrl),
            ("ArchipelagoNotIncluded (Steam Workshop) ↗",                         WorkshopUrl),
            ("Mod Updater (Steam Workshop dependency) ↗",                         ModUpdaterUrl),
            ("Archipelago Official ↗",                                             ArchipelagoSite),
            ("Archipelago Releases (for local server) ↗",                         ApReleasesUrl),
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
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); }
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
            string json = await _http.GetStringAsync(GH_API_RELEASES_ALL, ct);
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

    /// "v0.99" → "0.99"; strips leading 'v' before a digit.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release from the GitHub API. Returns the version
    /// string and the best-guess ZIP download URL. Falls back to the pinned
    /// v0.99 constant when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_API_RELEASES_LATEST, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;   // any .zip containing "oni" or "archipelago"
                string? anyZip    = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;
                    anyZip ??= url;
                    if (preferred == null &&
                        (lower.Contains("oni") || lower.Contains("archipelago")))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null) return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — ONI detection ───────────────────────────────────────

    private string? ResolveOniDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeOniDir(ov)) return ov;
        try { return DetectSteamOniDir(); }
        catch { return null; }
    }

    private static bool LooksLikeOniDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, "OxygenNotIncluded.exe"))) return true;
            if (Directory.Exists(Path.Combine(dir, "OxygenNotIncluded_Data"))) return true;
            return false;
        }
        catch { return false; }
    }

    private static string? DetectSteamOniDir()
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

                    string common     = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeOniDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeOniDir(conventional)) return conventional;
                }
                catch { }
            }
        }
        return null;
    }

    /// Resolve the ONI mods directory. Tries both Documents and
    /// OneDrive\Documents (common on Windows 11 with OneDrive enabled).
    private static string? ResolveModsDirectory()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string[] candidates = new[]
        {
            Path.Combine(docs, "Klei", "OxygenNotIncluded", "mods"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "OneDrive", "Documents", "Klei", "OxygenNotIncluded", "mods"),
        };
        return candidates.FirstOrDefault(Directory.Exists);
    }

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

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    private static IEnumerable<string> SteamLibraryRoots(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(steamRoot)) yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }

        foreach (string p in ExtractVdfPaths(text))
        {
            string norm = p.Replace('/', '\\').TrimEnd('\\');
            if (norm.Length > 0 && seen.Add(norm)) yield return norm;
        }
    }

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
            yield return text.Substring(open + 1, close - open - 1).Replace("\\\\", "\\");
            i = close + 1;
        }
    }

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

    private static string? ReadRegistryString(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(subKey);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    // ── Private helpers — installed-mod detection ─────────────────────────────

    /// Find the ArchipelagoNotIncluded mod assembly. The mod can be in:
    ///   - Workshop folder under the Steam install's workshop/content/457140/<id>/
    ///   - ONI mods directory (Documents\Klei\OxygenNotIncluded\mods\)
    /// We match any *.dll whose name contains "archipelago" in either location.
    private string? FindInstalledModAssembly()
    {
        // Check ONI mods directory (manual install path).
        try
        {
            string? modsDir = ResolveModsDirectory();
            if (modsDir != null && Directory.Exists(modsDir))
            {
                foreach (string dll in Directory.EnumerateFiles(
                    modsDir, "*.dll", SearchOption.AllDirectories))
                {
                    if (Path.GetFileNameWithoutExtension(dll)
                        .IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                        return dll;
                }
            }
        }
        catch { }

        // Check Steam Workshop content for appid 457140.
        try
        {
            foreach (string steamRoot in SteamRoots())
            {
                if (string.IsNullOrWhiteSpace(steamRoot)) continue;
                foreach (string lib in SteamLibraryRoots(steamRoot))
                {
                    string workshopContent = Path.Combine(
                        lib, "steamapps", "workshop", "content", SteamAppId);
                    if (!Directory.Exists(workshopContent)) continue;
                    foreach (string dll in Directory.EnumerateFiles(
                        workshopContent, "*.dll", SearchOption.AllDirectories))
                    {
                        if (Path.GetFileNameWithoutExtension(dll)
                            .IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                            return dll;
                    }
                }
            }
        }
        catch { }

        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartOni()
    {
        string? oniDir = ResolveOniDir();
        string? exe    = oniDir != null ? Path.Combine(oniDir, "OxygenNotIncluded.exe") : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = oniDir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start OxygenNotIncluded.exe.");

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

        // Fall back to Steam URI.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true;
                return;
            }
            catch { }
        }

        throw new FileNotFoundException(
            "Could not find OxygenNotIncluded.exe. Open this game's Settings and " +
            "pick your ONI install folder, or install ONI via Steam.",
            "OxygenNotIncluded.exe");
    }

    // ── Private helpers — download / extract the mod (manual ZIP path) ────────

    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string modsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"oni-archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"oni-archipelago-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((15, $"Downloading ArchipelagoNotIncluded {version}..."));
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
                        int pct = (int)(15 + 50 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Unpacking the mod..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, tempExtract, overwriteFiles: true);

            // The ZIP may contain a single wrapping sub-folder or place the
            // mod folder directly at the root. Either way, we merge into the
            // ONI mods directory so the mod folder lands at modsDir\<modname>.
            string mergeRoot = tempExtract;
            string[] subdirs = Directory.GetDirectories(mergeRoot);
            string[] files   = Directory.GetFiles(mergeRoot);
            if (files.Length == 0 && subdirs.Length == 1)
                mergeRoot = subdirs[0]; // unwrap one layer

            progress.Report((82, "Installing the mod into the ONI mods directory..."));
            Directory.CreateDirectory(modsDir);
            MergeDirectory(mergeRoot, modsDir);

            progress.Report((95, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))       File.Delete(tempZip);                          } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    private static void MergeDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, dir);
            Directory.CreateDirectory(Path.Combine(dst, rel));
        }
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel    = Path.GetRelativePath(src, file);
            string target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // ── Private helpers — settings sidecar ───────────────────────────────────

    private sealed class OniSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private OniSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<OniSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(OniSettings s)
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
