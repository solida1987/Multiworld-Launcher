using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using LauncherV2.Core;

namespace LauncherV2.Plugins.YookaLaylee;

// ═══════════════════════════════════════════════════════════════════════════════
// YookaLayleePlugin — install / launch for "Yooka-Laylee" (Playtonic Games,
// 2017) played through the YLRandomizer BepInEx mod by SunnyBat, which
// contains the in-game Archipelago Multiworld client. This is a NATIVE
// "ConnectsItself" integration: the game itself speaks to the AP server (no
// emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-15, verified against the apworld + setup guide)
//
//   * THE AP WORLD is a CUSTOM (non-core) world maintained by Awareqwx in a fork
//     of Archipelago: github.com/Awareqwx/Archipelago. The latest release is
//     1.0.0-beta.8 (2025-09-17). The AP world's game string (verified against
//     worlds/yooka_laylee/__init__.py: `game: str = "Yooka-Laylee"`) is
//     "Yooka-Laylee". The .apworld must be dropped into the AP host's
//     custom_worlds folder to generate a game; the launcher hosts the download
//     URL but does NOT install it into an AP host (that is the player's job).
//
//   * THE IN-GAME MOD is YLRandomizer by SunnyBat
//     (github.com/SunnyBat/YLRandomizer). It is a BepInEx 5 mod. The release
//     asset is "Windows.zip", which when extracted lands BepInEx/, BepInEx.exe,
//     and doorstop_config.ini directly in the game root. Verified against:
//     - The apworld's setup_en.md (extract into the Yooka-Laylee install folder)
//     - The YLRandomizer repo structure (BepInEx 5, not BepInEx 6 IL2CPP)
//     Latest release: 1.0.0-beta.7.1 (2024-12-24). Unlike TUNIC (which needs
//     BepInEx staged separately), YLRandomizer's release zip already includes
//     BepInEx in the same Windows.zip — one extract, done.
//
//   * HOW IT CONNECTS — VIA AN IN-GAME IMGUI OVERLAY on the main menu. Verified
//     against YLRandomizer/Scripts/ArchipelagoUI.cs: the mod draws a Host /
//     PlayerName / Password text field panel at the top-left on the main menu.
//     The user fills in the fields and presses Connect. There is NO config file
//     the mod reads on startup and NO command-line argument for prefill (the
//     fields are private in-memory fields of the MonoBehaviour — confirmed
//     source-level). Connection prefill is therefore NOT possible; the settings
//     panel surfaces the session's host:port, slot name, and password for the
//     user to copy into the in-game fields. This is the same pattern as TUNIC.
//
//   * Steam appid: 360830. The Steam common folder name is "yooka-laylee"
//     (verified: Steam stores it lowercased; appmanifest_360830.acf installdir
//     is typically "Yooka-Laylee"). Any PC platform (Epic, GOG) is also
//     supported per the setup guide — the folder picker handles non-Steam.
//
//   * "Installed" is judged by the presence of YLRandomizer.dll under a detected
//     or override game dir's BepInEx tree. The mod zip extracts the BepInEx
//     subtree, so the dll lives at <game>\BepInEx\plugins\YLRandomizer\
//     YLRandomizer.dll (typical BepInEx plugin structure). We scan recursively
//     to be robust against minor folder moves between mod releases.
//
//   * ConnectsItself = true: once the user connects through the in-game fields,
//     the in-game YLRandomizer client owns the slot on the AP server. The
//     launcher must NOT hold its own ApClient on the same slot.
//
//   * SupportsStandalone = true: vanilla Yooka-Laylee runs fine without YLRandomizer
//     (BepInEx's "doorstop" proxy only engages when the game process loads, and
//     running without BepInEx installed means no modification at all). However,
//     if the mod is installed, the in-game connection panel will appear on the
//     main menu whenever the game is loaded. This is fine — the user can simply
//     cancel or ignore the panel for a non-AP run.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Yooka-Laylee install via registry (SteamPath from HKCU
//      + WOW6432Node, all library roots from libraryfolders.vdf, matching
//      appmanifest_360830.acf). A manual install-dir override is also supported
//      and takes precedence; it is validated (must contain Yooka-Laylee.exe or
//      the Yooka-Laylee_Data folder) and persisted in a sidecar JSON file under
//      Games/ROMs/yookalaylee/.
//   2. INSTALL/UPDATE — downloads and extracts YLRandomizer's Windows.zip from
//      the latest SunnyBat/YLRandomizer GitHub release (or a pinned fallback)
//      directly into the detected/override game root. The zip already bundles
//      BepInEx, so no separate prerequisite step is needed. A stale prior
//      extraction is replaced (BepInEx\plugins\YLRandomizer\ is cleaned before
//      re-extracting). After install the user must: run the game, wait for the
//      in-game panel to appear on the main menu, enter server/slot/password, and
//      press Connect. The settings panel states all of this.
//   3. LAUNCH — run Yooka-Laylee.exe from the detected/override install; fall
//      back to steam://rungameid/360830 when Steam is present but the exe cannot
//      be found. ConnectsItself = true. SupportsStandalone = true. No connection
//      prefill (entered in-game), stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ────────────────────────────────────────────────────
//   * Steam VDF / ACF parsing is tolerant: hand-written quoted-value scan; any
//     failure degrades to "game not found" rather than throwing.
//   * No AP password is written to disk by this plugin (no prefill mechanism
//     exists), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class YookaLayleePlugin : IGamePlugin
{
    // ── Constants — mod repo (verified 2026-06-15) ────────────────────────────

    /// SunnyBat/YLRandomizer — the in-game BepInEx mod with the AP client.
    private const string ModOwner    = "SunnyBat";
    private const string ModRepo     = "YLRandomizer";
    private const string ModRepoUrl  = $"https://github.com/{ModOwner}/{ModRepo}";
    private const string GhModReleasesLatestUrl =
        $"https://api.github.com/repos/{ModOwner}/{ModRepo}/releases/latest";
    private const string GhModReleasesUrl =
        $"https://api.github.com/repos/{ModOwner}/{ModRepo}/releases";

    /// The release asset name (verified 2026-06-15 against 1.0.0-beta.7.1).
    private const string ModZipAssetName = "Windows.zip";

    /// Pinned fallback version + download URL when the GitHub API is unavailable.
    private const string FallbackVersion = "1.0.0-beta.7.1";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackVersion}/{ModZipAssetName}";

    /// apworld repo — Awareqwx's Archipelago fork. Used for the news feed and
    /// to surface the .apworld download URL (for the player to install into their
    /// own AP host's custom_worlds folder).
    private const string ApWorldOwner = "Awareqwx";
    private const string ApWorldRepo  = "Archipelago";
    private const string ApWorldRepoUrl     = $"https://github.com/{ApWorldOwner}/{ApWorldRepo}";
    private const string GhApWorldReleasesUrl =
        $"https://api.github.com/repos/{ApWorldOwner}/{ApWorldRepo}/releases";

    /// The primary DLL that signals YLRandomizer is installed (BepInEx plugin).
    private const string ModPrimaryDll = "YLRandomizer.dll";

    /// Steam appid for Yooka-Laylee (verified 2026-06-15).
    private const string SteamAppId = "360830";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The standard Steam "common" install folder name. Steam uses lowercase.
    private const string SteamCommonFolderName = "Yooka-Laylee";

    /// The base-game executable name.
    private const string GameExeName = "Yooka-Laylee.exe";

    private const string SetupGuideUrl  = $"{ApWorldRepoUrl}/blob/main/worlds/yooka_laylee/docs/setup_en.md";
    private const string ArchipelagoSite = "https://archipelago.gg";
    private const string PopTrackerUrl  = "https://github.com/RisingPhoenix64/Yooka-Laylee-AP-tracker/releases";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "yookalaylee";
    public string DisplayName => "Yooka-Laylee";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/yooka_laylee/__init__.py
    /// (`game: str = "Yooka-Laylee"` in class YookaWorld).
    public string ApWorldName => "Yooka-Laylee";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "yooka_laylee.png");

    public string ThemeAccentColor => "#4CAF50";   // Yooka's green scales
    public string[] GameBadges     => new[] { "Steam · needs mod", "Custom apworld" };

    public string Description =>
        "Yooka-Laylee, the charming 3D platformer by Playtonic Games (2017), played " +
        "through the YLRandomizer BepInEx mod by SunnyBat — which bundles an in-game " +
        "Archipelago client, so the game connects to the multiworld itself with no " +
        "emulator and no bridge. Pagies, Play Coins, Mollycools, abilities and more " +
        "are shuffled across the multiworld; the goal is to collect 100 Pagies and " +
        "defeat Capital B. You bring your own copy of Yooka-Laylee (Steam, Epic, or " +
        "GOG); the launcher detects your Steam install and can stage the mod into it. " +
        "You connect to your server from the in-game panel that appears on the main " +
        "menu. The apworld for this game is custom (not in Archipelago core); the " +
        "launcher shows you where to download it.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => System.Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means YLRandomizer.dll is present under the game install's
    /// BepInEx tree. We do NOT gate on our own stamp — the user may have
    /// installed the mod by hand, which we honor.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and working files. Exposed as
    /// GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "YookaLaylee");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "yookalaylee_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private System.Diagnostics.Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The YLRandomizer mod + in-game client handle AP checks, items, and goal
    // on the slot — the launcher relays nothing. These exist for interface
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
            InstalledVersion = FindInstalledModDll() != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch { InstalledVersion = null; }
            try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(ModOwner, ModRepo, ct));
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
        // 0. We need a Yooka-Laylee install to drop the mod into.
        progress.Report((2, "Locating your Yooka-Laylee installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Yooka-Laylee installation. Open this game's Settings " +
                "and pick your Yooka-Laylee folder (the one containing Yooka-Laylee.exe), " +
                "or install the game via Steam first. The Archipelago mod is added on top " +
                "of your own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback if API is unreachable).
        progress.Report((5, "Checking the latest YLRandomizer release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the YLRandomizer mod download on GitHub. Check your " +
                "internet connection, or download it manually from " + ModRepoUrl +
                "/releases — extract Windows.zip into your Yooka-Laylee folder.");

        // 2. Download + extract Windows.zip into the game root. The zip bundles
        //    BepInEx itself (no separate prerequisite step needed). A prior
        //    YLRandomizer plugin folder is cleaned before extraction so an update
        //    is clean.
        await DownloadAndExtractModAsync(zipUrl, version, gameDir, 8, 92, progress, ct);

        // 3. Stamp the version in the sidecar so the tile can show it.
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"YLRandomizer {version} installed into your Yooka-Laylee folder. " +
            "To play: launch the game and wait for the main menu. A connection panel " +
            "will appear in the top-left — enter your server (e.g. archipelago.gg:12345), " +
            "slot name (PlayerName), and optional password, then click Connect. " +
            "Open Settings for connection details and links. " +
            "NOTE: this game uses a custom apworld (not in Archipelago core) — download " +
            "yooka_laylee.apworld from the apworld repo and add it to your AP host's " +
            "custom_worlds folder to generate a game."));
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
        // HONEST: the AP connection for Yooka-Laylee is entered via an in-game
        // IMGUI panel on the main menu (Host / PlayerName / Password text boxes).
        // These fields are private in-memory state in ArchipelagoUI.cs — there is
        // no config file to pre-write and no command-line argument for prefill
        // (verified source-level, 2026-06-15). So launching from this tile just
        // starts the game; the user connects in-game. ConnectsItself = true.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartGame();
        return Task.CompletedTask;
    }

    /// Vanilla Yooka-Laylee runs without the mod; with the mod installed the
    /// in-game panel appears but can be ignored for non-AP runs.
    public bool SupportsStandalone => true;

    /// The in-game YLRandomizer client owns the AP slot.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        // No plaintext AP password is ever written by this plugin (connection
        // is entered in-game), so there is nothing to scrub on stop.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself) ───────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The YLRandomizer mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod's in-game panel renders its own AP status.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// A valid Yooka-Laylee folder contains Yooka-Laylee.exe and/or the
    /// Yooka-Laylee_Data folder. Return null when acceptable, else a short
    /// human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Yooka-Laylee install folder.";

        if (LooksLikeGameDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeGameDir(nested)) return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Yooka-Laylee installation. Pick the folder " +
               "that contains Yooka-Laylee.exe (for Steam this is usually " +
               @"...\steamapps\common\Yooka-Laylee).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Yooka-Laylee is your own game (Steam, Epic, or GOG) with the " +
                   "YLRandomizer BepInEx mod added on top. The launcher can stage the " +
                   "mod into your game folder, but you connect to the server from the " +
                   "in-game panel that appears on the main menu (no in-game connection " +
                   "file to pre-fill). The apworld for this game is custom (not in " +
                   "Archipelago core) — you must download yooka_laylee.apworld and add " +
                   "it to your AP host's custom_worlds folder to generate a game. " +
                   "These steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "YOOKA-LAYLEE INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Yooka-Laylee not detected. Pick your install folder below, or install " +
              "the game via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // Mod status line
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                    ? "YLRandomizer mod found: " + modDll
                    : "YLRandomizer mod not found yet (use Install on the Play tab, or " +
                      "extract Windows.zip from the mod releases into your game folder).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Yooka-Laylee install folder (the one containing " +
                          "Yooka-Laylee.exe). Detected from Steam automatically; set it " +
                          "here to override for Epic, GOG, or a non-standard Steam library.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Yooka-Laylee install folder (contains Yooka-Laylee.exe)",
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
                    System.Windows.MessageBox.Show(bad, "Not a Yooka-Laylee folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeGameDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeGameDir(nested)) picked = nested;
                }
                SaveOverrideDir(picked);
                dirBox.Text = picked;
            }
        };
        DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new TextBlock
        {
            Text = "Steam installs are detected automatically (appid 360830). Use this " +
                   "picker for Epic, GOG, or a non-standard Steam library.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ───────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "On the main menu, a connection panel appears in the top-left. Enter " +
                   "your Host (e.g. archipelago.gg:12345), PlayerName (your AP slot name), " +
                   "and optional Password, then click Connect (or press Enter). Once " +
                   "connected you can start a new game. This launcher cannot pre-fill " +
                   "the connection — copy your credentials from here.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Custom apworld note ─────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CUSTOM APWORLD (required for hosting)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "The Yooka-Laylee apworld is NOT in Archipelago core. To generate a " +
                   "multiworld you (or your host) must download yooka_laylee.apworld from " +
                   "the Awareqwx/Archipelago release page and place it in your AP " +
                   "installation's custom_worlds folder. The link below opens the releases " +
                   "page. Players joining an existing server do not need to install this — " +
                   "only the host generating the game does.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Yooka-Laylee (Steam, Epic, or GOG). Install it if you have not. " +
                "Use the folder picker above if it was not detected automatically.",
            "2. Install the YLRandomizer mod: use the Install button on the Play tab, or " +
                "download Windows.zip from the mod releases (link below) and extract it " +
                "into your Yooka-Laylee folder. This includes BepInEx — no separate step.",
            "3. (HOST ONLY) Download yooka_laylee.apworld from the Awareqwx/Archipelago " +
                "releases page and place it in your AP host's custom_worlds folder. " +
                "Generate your multiworld game using the Archipelago Launcher.",
            "4. Launch Yooka-Laylee. On the main menu, a connection panel appears in the " +
                "top-left. Enter your Host (server:port), PlayerName, and optional Password.",
            "5. Click Connect. Once you see the connection confirmed, start a new game.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("YLRandomizer mod releases (GitHub) ↗",    ModRepoUrl + "/releases"),
            ("Yooka-Laylee apworld releases ↗",         ApWorldRepoUrl + "/releases"),
            ("Setup guide ↗",                            SetupGuideUrl),
            ("Map Tracker (PopTracker pack) ↗",          PopTrackerUrl),
            ("Archipelago Official ↗",                   ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(
                    new ProcessStartInfo(u) { UseShellExecute = true }); }
                catch { /* non-fatal */ }
            };
            panel.Children.Add(btn);
        }
        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Surface apworld releases (Awareqwx/Archipelago) as the primary news,
        // since the apworld changelog is what players care about most.
        try
        {
            string json = await _http.GetStringAsync(GhApWorldReleasesUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return System.Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                string tag = el.TryGetProperty("tag_name", out var t) ? NormalizeTag(t.GetString()) ?? "" : "";
                string rawTitle = el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                items.Add(new NewsItem(
                    Title:   string.IsNullOrWhiteSpace(rawTitle)
                             ? $"Yooka-Laylee apworld {tag}"
                             : rawTitle,
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: tag,
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return System.Array.Empty<NewsItem>(); }
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

    /// Resolve the latest YLRandomizer release: version + Windows.zip URL.
    /// Falls back to pinned 1.0.0-beta.7.1 when the API is unavailable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GhModReleasesLatestUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? windowsZip = null;
                string? anyZip     = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    // Skip debug zips; prefer the clean Windows.zip.
                    if (name.Equals("Windows.zip", StringComparison.OrdinalIgnoreCase))
                    { windowsZip = url; break; }

                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                        && !name.Contains("Debug", StringComparison.OrdinalIgnoreCase))
                        anyZip ??= url;
                }
                string? zip = windowsZip ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unavailable → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — Steam / game detection ──────────────────────────────

    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeGameDir(ov)) return ov;
        try { return DetectSteamGameDir(); }
        catch { return null; }
    }

    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, GameExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, "Yooka-Laylee_Data"))) return true;
            return false;
        }
        catch { return false; }
    }

    private static string? DetectSteamGameDir()
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
                        if (LooksLikeGameDir(candidate)) return candidate;
                    }
                    // Conventional lowercase fallback.
                    foreach (string attempt in new[] { "Yooka-Laylee", "yooka-laylee", "YookaLaylee" })
                    {
                        string conv = Path.Combine(common, attempt);
                        if (LooksLikeGameDir(conv)) return conv;
                    }
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    private string? FindInstalledModDll()
    {
        try
        {
            string? gameDir = ResolveGameDir();
            if (gameDir == null) return null;
            string bepInExDir = Path.Combine(gameDir, "BepInEx");
            if (!Directory.Exists(bepInExDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(
                bepInExDir, "*.dll", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dll).Equals(ModPrimaryDll, StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
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

        foreach (string path in ExtractVdfPaths(text))
        {
            string norm = path.Replace('/', '\\').TrimEnd('\\');
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

    // ── Private helpers — download / extract ──────────────────────────────────

    /// Download Windows.zip and extract it directly into the game root. The zip
    /// bundles BepInEx itself. The plugin folder for YLRandomizer is cleaned first
    /// so re-installs and updates are idempotent.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string gameDir,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"ylrandomizer-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 7 / 10;
            await DownloadFileAsync(zipUrl, tempZip,
                $"Downloading YLRandomizer {version}...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Extracting mod into your Yooka-Laylee folder..."));

            // Clean prior plugin installation so an update is clean. Leave any
            // user-placed BepInEx files untouched (only clean our plugin folder).
            string ylPluginDir = Path.Combine(gameDir, "BepInEx", "plugins", "YLRandomizer");
            if (Directory.Exists(ylPluginDir))
            {
                try { Directory.Delete(ylPluginDir, recursive: true); } catch { /* in-use — overwrite */ }
            }

            // Extract the zip directly into the game root. YLRandomizer's Windows.zip
            // lays out BepInEx/, BepInEx.exe, doorstop_config.ini, etc. at the root.
            ZipFile.ExtractToDirectory(tempZip, gameDir, overwriteFiles: true);

            progress.Report((pctEnd, "Mod extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    private async Task DownloadFileAsync(
        string url,
        string destPath,
        string msg,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        progress.Report((pctStart, msg));
        using var response = await _http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total      = response.Content.Headers.ContentLength ?? -1;
        long downloaded = 0;

        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        var buf = new byte[81920];
        int bytesRead;
        while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
            downloaded += bytesRead;
            if (total > 0)
            {
                int span = Math.Max(1, pctEnd - pctStart);
                int pct  = pctStart + (int)(span * downloaded / total);
                progress.Report((pct, $"{msg} {downloaded / 1000}KB"));
            }
        }
        await dst.FlushAsync(ct);
    }

    // ── Private helpers — sidecar settings ───────────────────────────────────

    private sealed class YookaLayleeSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private YookaLayleeSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<YookaLayleeSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(YookaLayleeSettings s)
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

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        string? gameDir = ResolveGameDir();
        string? exe     = gameDir != null ? Path.Combine(gameDir, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = System.Diagnostics.Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = gameDir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Yooka-Laylee.");

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
            catch { /* non-fatal */ }
            return;
        }

        // Fall back to Steam if we at least know Steam is installed.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { /* fall through */ }
        }

        throw new FileNotFoundException(
            "Could not find Yooka-Laylee.exe. Open this game's Settings and pick your " +
            "Yooka-Laylee install folder, or install the game via Steam.",
            GameExeName);
    }
}
