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

// NOTE on type qualification (BUILD GOTCHA — CS0104):
// The real launcher project sets BOTH <UseWPF>true</UseWPF> and
// <UseWindowsForms>true</UseWindowsForms>. That makes a long list of simple type
// names ambiguous between WPF and WinForms (Clipboard, MessageBox, Application,
// Color, Brush(es), Button, TextBox, CheckBox, Orientation, FontWeights,
// HorizontalAlignment, Cursors, Thickness, …). To avoid CS0104 this file
// deliberately does NOT do `using System.Windows.Controls;` /
// `using System.Windows.Media;` — every WPF UI type below is written fully
// qualified (System.Windows.Controls.*, System.Windows.Media.*,
// System.Windows.MessageBox, …). GlobalUsings.cs already aliases the short names
// for the main build, so this file also does NOT declare any file-level
// `using X = System.Windows...;` alias (that would be CS1537, a duplicate alias).

namespace LauncherV2.Plugins.Lingo;

// ═══════════════════════════════════════════════════════════════════════════════
// LingoPlugin — detect / install-guide / launch for "Lingo" (Brenton Wildes,
// 2021) played through the "Lingo Archipelago Randomizer" by hatkirby (Four
// Island). This is a NATIVE "ConnectsItself" integration in the same family as
// the shipped Subnautica / Hollow Knight / Stardew Valley plugins — the game
// itself speaks to the AP server (no emulator, no Lua bridge, no launcher-held
// ApClient on the slot). The honest integration ceiling is "automate what is
// possible, guide the irreducible parts."
//
// ── REALITY CHECK (2026-06-14, verified online + against the apworld) ──────────
//
//   * THE AP WORLD game string is "Lingo" — VERIFIED against
//     worlds/lingo/__init__.py (LingoWorld.game = "Lingo"). Lingo is a CORE
//     Archipelago world: it ships INSIDE Archipelago itself ("Lingo is included
//     with Archipelago 0.4.4 and later"), so NO custom .apworld drop is needed.
//     GameId here = "lingo".
//
//   * THE CLIENT IS A LINGO "MAP", NOT a BepInEx/MelonLoader mod and NOT a
//     separate randomizer exe. Lingo (a Godot game) has first-class custom-map
//     support; the Archipelago integration is published as a map. It is
//     distributed two ways (BOTH verified):
//       - PRIMARY (what the official AP setup guide tells players to use): the
//         Steam Workshop item titled "Archipelago" by hatkirby, for Lingo
//         (appid 1814170), Workshop id 3092505110. Subscribing makes Steam
//         auto-download it; the player then just picks it in-game. The launcher
//         CANNOT subscribe on the user's behalf, so it opens the Workshop page
//         for one-click subscribe and detects the result.
//       - MANUAL: download a release zip from the Four Island code server
//         (https://code.fourisland.com/lingo-archipelago/, a cgit/forgejo
//         instance; release zips are named like "lingo-archipelago-v5.1.0.zip"),
//         then "open Lingo's settings, click View Game Data to open Windows
//         Explorer, and unzip the randomizer into the 'maps' folder" (verified
//         quote from the Workshop description + the official setup guide). The
//         game-data folder is the Godot user dir
//         %APPDATA%\Godot\app_userdata\Lingo and the maps sub-folder is "maps"
//         (all lowercase). This plugin can do the manual install for the user:
//         download the release zip and unzip it into that maps folder.
//
//   * CONNECTION IS MADE IN-GAME (verified quote, official setup guide):
//       "Click on Settings, and then Level. Choose Archipelago from the list.
//        Start a new game ... leave the name field blank, enter the Archipelago
//        address, slot name, and password into the fields, and press Connect."
//     The three fields are: Archipelago address (host:port), slot name, and
//     password. There is NO command-line arg and NO config file this launcher
//     can pre-write (the map reads the values you type in its in-game form). So
//     this plugin does NOT attempt a connection prefill — the settings panel and
//     post-install note surface the session's host/slot so the user can copy
//     them into those three in-game fields. ConnectsItself = true: the launcher
//     must NOT hold its own ApClient on the slot while the map is connected.
//
//   * BASE GAME: the player's own copy of Lingo on Steam (appid 1814170). Lingo
//     is paid software — the launcher never ships or recreates it. There is also
//     an optional auto-tracker (lingo-ap-tracker) and the AP Text Client, but
//     neither is required to play.
//
// ── WHAT THIS PLUGIN HONESTLY DOES ─────────────────────────────────────────────
//   1. DETECT the Steam Lingo install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, HKLM\...\WOW6432Node\Valve\Steam
//      -> InstallPath), parsing steamapps\libraryfolders.vdf for every library
//      root and locating steamapps\common\Lingo via appmanifest_1814170.acf. A
//      manual install-dir OVERRIDE (settings folder picker) is supported and
//      takes precedence; it is validated (must contain Lingo.exe) and persisted
//      in this plugin's OWN sidecar (Games/ROMs/lingo/lingo_launcher.json) —
//      Core/SettingsStore is NOT modified.
//   2. DETECT the Godot game-data maps folder (%APPDATA%\Godot\app_userdata\Lingo
//      \maps) and whether the Archipelago map is present there (a
//      "*archipelago*"/"*lingo-archipelago*" file/folder under maps, OR the
//      Steam Workshop content folder for item 3092505110).
//   3. INSTALL/UPDATE (best effort) = download the map's release zip from the
//      Four Island code server and unzip it into the maps folder. Because that
//      server sits behind bot-protection that may refuse an automated download,
//      this is honestly best-effort: on any failure it falls back to clear,
//      numbered guided steps + links (Steam Workshop subscribe, releases page,
//      official setup guide) so the user can finish in seconds. It never fakes a
//      one-click that cannot exist.
//   4. LAUNCH = run Lingo.exe from the detected/override install; if the exe
//      cannot be found but Steam is present, fall back to steam://rungameid/
//      1814170. ConnectsItself = true (the map owns the slot). SupportsStandalone
//      = true (plain Lingo runs perfectly). No prefill (in-game form), stated
//      honestly.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Subnautica/HK-style) ───────
//   * The Four Island code server is a cgit/forgejo instance protected by Anubis
//     bot-protection; its exact release-zip download URL pattern could not be
//     fetched offline. The resolver tries the documented release-zip name pattern
//     "lingo-archipelago-v<tag>.zip" against a small set of plausible URL shapes
//     and degrades to guided steps on any failure — the install is best-effort by
//     design, never a hard claim.
//   * "Installed" is judged by the PRESENCE of the Archipelago map (in the maps
//     folder OR the Workshop content folder) — NOT by an our-own version stamp,
//     because the user may instead subscribe on the Workshop, which this launcher
//     honors. If no Lingo install is detected, the tile reads "not installed".
//   * Steam library / Workshop parsing is defensive: a tolerant VDF scan; any
//     failure degrades to "not found" rather than throwing.
//   * No plaintext AP password is ever written by this plugin (connection is
//     entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class LingoPlugin : IGamePlugin
{
    // ── Constants — Steam (Lingo appid 1814170, verified) ──────────────────────
    private const string SteamAppId = "1814170";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The standard Steam install sub-folder name for Lingo.
    private const string SteamCommonFolderName = "Lingo";

    /// The Lingo runtime exe name (Godot export).
    private const string PreferredExeName = "Lingo.exe";

    // ── Constants — the AP Lingo map (real source, verified 2026-06-14) ────────

    /// Steam Workshop item id for the "Archipelago" map by hatkirby (Lingo).
    private const string WorkshopItemId = "3092505110";
    private static readonly string WorkshopItemUrl =
        $"https://steamcommunity.com/sharedfiles/filedetails/?id={WorkshopItemId}";

    /// Four Island code server (cgit/forgejo) project + its releases listing.
    private const string CodeRepoUrl     = "https://code.fourisland.com/lingo-archipelago/about/";
    private const string CodeReleasesUrl = "https://code.fourisland.com/lingo-archipelago/refs";
    private const string TrackerRepoUrl  = "https://code.fourisland.com/lingo-ap-tracker/about/";

    /// Official Archipelago "Lingo" resources.
    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Lingo/setup/en";
    private const string PlayerOptionsUrl = "https://archipelago.gg/games/Lingo/player-options";
    private const string ArchipelagoSite = "https://archipelago.gg";
    private const string LingoSteamStoreUrl = $"https://store.steampowered.com/app/{SteamAppId}";

    /// Pinned fallback version for the map when the code server is unreachable.
    /// Tag v5.1.0 was the newest seen 2026-06-14 (release zips are named like
    /// "lingo-archipelago-v5.1.0.zip"). The Workshop route is the primary path;
    /// this is only the net so an offline manual Install still has a target name.
    private const string FallbackVersion = "5.1.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "lingo";
    public string DisplayName => "Lingo";
    public string Subtitle    => "Native PC · Archipelago";

    /// EXACT AP game string — VERIFIED against worlds/lingo/__init__.py
    /// (LingoWorld.game = "Lingo"). Lingo ships inside Archipelago (0.4.4+), so no
    /// custom .apworld drop is required.
    public string ApWorldName => "Lingo";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "lingo.png");

    public string ThemeAccentColor => "#C8A21E";   // Lingo's gold puzzle-wall hue
    public string[] GameBadges     => new[] { "Requires Lingo on Steam" };

    public string Description =>
        "Lingo is a first-person word-puzzle game set in a sprawling, " +
        "interconnected labyrinth of letter walls — a Myst-like world made of " +
        "anagrams, rhymes and hidden words. This is the Lingo Archipelago " +
        "Randomizer, a custom \"map\" that turns Lingo into an Archipelago " +
        "multiworld: panels and progression are shuffled, and the game connects to " +
        "the Archipelago server itself from its in-game menu (no emulator, no " +
        "bridge). You bring your own copy of Lingo on Steam, then add the " +
        "Archipelago map — easiest via the Steam Workshop (one-click subscribe), " +
        "or by unzipping a release into Lingo's maps folder. You connect to your " +
        "server in-game: Settings -> Level -> Archipelago, then enter your server " +
        "address, slot name and password and press Connect.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the Archipelago Lingo map is present — either unzipped in
    /// the Godot maps folder, or subscribed via the Steam Workshop (its content
    /// folder for item 3092505110 exists). We do NOT gate on our own stamp.
    public bool IsInstalled => FindInstalledMap() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads (the map zip) and any working files. The
    /// actual map is unzipped INTO the Godot maps folder, not here. Exposed as
    /// GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Lingo");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained source file — same as Subnautica/
    /// Doom/Undertale). BOM-less UTF-8, read-modify-write. Per the convention,
    /// lives under Games/ROMs/lingo/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "lingo_launcher.json");

    /// The Godot game-data folder for Lingo (%APPDATA%\Godot\app_userdata\Lingo)
    /// and its "maps" sub-folder (all lowercase). Custom maps live here; the
    /// in-game "View Game Data" button opens this folder.
    private static string GodotLingoDataDir
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Godot", "app_userdata", "Lingo");
        }
    }
    private static string MapsDir => Path.Combine(GodotLingoDataDir, "maps");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Lingo Archipelago map reports checks/items/goal to the AP server itself
    // — the launcher relays nothing. These exist for interface compatibility
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
            // Best-effort: report the version we stamped next to a manual install if
            // present; otherwise "installed" when the map is detected (Workshop
            // installs have no tag we control).
            InstalledVersion = FindInstalledMap() != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        // The Four Island code server is bot-protected; we cannot reliably resolve
        // a "latest tag" over HTTP. Report the pinned fallback as the known-good
        // available version rather than throwing or claiming nothing. Contract:
        // never throw on network failure.
        try
        {
            AvailableVersion = await ResolveLatestMapVersionAsync(ct) ?? FallbackVersion;
        }
        catch
        {
            AvailableVersion = FallbackVersion;
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. We need a Lingo install to launch later, and the Godot maps folder to
        //    unzip into. Locate the game (override wins, else Steam).
        progress.Report((4, "Locating your Lingo installation..."));
        string? lingoDir = ResolveLingoDir();

        // 1. If the map is already present (Workshop or manual), we're done.
        string? existing = FindInstalledMap();
        if (existing != null)
        {
            InstalledVersion = ReadStampedVersion() ?? "installed";
            progress.Report((100,
                "The Archipelago map is already installed (" + existing + "). To play: " +
                "launch Lingo, then on the main menu go to Settings -> Level, choose " +
                "Archipelago, start a new game, and enter your server address, slot name " +
                "and password, then press Connect."));
            return;
        }

        // 2. Best-effort automated manual install: download the release zip from the
        //    Four Island code server and unzip it into the Godot maps folder.
        //    HONEST: that server is bot-protected and may refuse an automated
        //    download — so this is wrapped to degrade gracefully into guided steps.
        progress.Report((10, "Trying to download the Lingo Archipelago map..."));
        string version = FallbackVersion;
        try
        {
            version = await ResolveLatestMapVersionAsync(ct) ?? FallbackVersion;
            AvailableVersion = version;

            bool ok = await TryDownloadAndInstallMapAsync(version, progress, ct);
            if (ok)
            {
                WriteStampedVersion(version);
                InstalledVersion = version;
                progress.Report((100,
                    $"Installed the Lingo Archipelago map ({version}) into your Lingo maps " +
                    "folder. To play: launch Lingo, then Settings -> Level -> Archipelago, " +
                    "start a new game, leave the name field blank, and enter your server " +
                    "address, slot name and password, then press Connect."));
                return;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through to guided steps below */ }

        // 3. Could not auto-download (bot-protection / offline). Open the Workshop
        //    page for one-click subscribe (the official primary route) and report
        //    the exact manual steps. This is the honest outcome, never a fake claim.
        progress.Report((90, "Opening the Steam Workshop page for one-click subscribe..."));
        try { Process.Start(new ProcessStartInfo(WorkshopItemUrl) { UseShellExecute = true }); }
        catch { /* opening a browser is best-effort */ }

        string where = lingoDir != null
            ? $"Lingo was detected at \"{lingoDir}\". "
            : "Lingo was not auto-detected (set your install folder in Settings if needed). ";

        progress.Report((100,
            where +
            "The Archipelago map could not be auto-downloaded (the Four Island code " +
            "server blocks automated downloads). Easiest install: on the Steam Workshop " +
            "page that just opened, click Subscribe — Steam downloads the \"Archipelago\" " +
            "map automatically. Alternatively, download the latest release from the " +
            "releases page (link in Settings) and, in Lingo, open Settings -> Gameplay -> " +
            "View Game Data, then unzip it into the \"maps\" folder. Then launch Lingo and " +
            "connect from Settings -> Level -> Archipelago (address, slot name, password)."));
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
        // HONEST: the AP server connection for Lingo is entered IN-GAME (Settings ->
        // Level -> Archipelago -> New Game -> address / slot name / password ->
        // Connect). The map reads those typed values; there is no command-line /
        // config prefill this launcher can apply (verified — see header). So
        // launching from this tile just starts the game; the user connects in-game
        // with the session credentials (the settings panel + note surface those).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the map is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartLingo();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Lingo runs perfectly well.
    public bool SupportsStandalone => true;

    /// The Lingo Archipelago map owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartLingo();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started Lingo from here. Kill what we launched.
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
        // The Lingo Archipelago map receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The map renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Lingo folder contains Lingo.exe.
    /// Return null when acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Lingo install folder.";

        if (LooksLikeLingoDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeLingoDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Lingo installation. Pick the folder that " +
               "contains Lingo.exe. For Steam this is usually " +
               @"...\steamapps\common\Lingo.";
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
            Text = "Lingo is your own game on Steam, with the Archipelago map added on " +
                   "top. The easiest way to add it is the Steam Workshop (one-click " +
                   "Subscribe); the launcher can also try to download a release and unzip " +
                   "it into Lingo's maps folder for you. You connect to your server " +
                   "in-game from Settings -> Level -> Archipelago — there is no connection " +
                   "file to pre-fill. These external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(SectionHeader("LINGO INSTALL", muted));

        string? lingoDir    = ResolveLingoDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = lingoDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + lingoDir
                : "Detected Steam install: " + lingoDir)
            : "Lingo not detected. Pick your install folder below, or install Lingo " +
              "via Steam first (appid " + SteamAppId + ").";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = lingoDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // map status line
        string? map = FindInstalledMap();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = map != null
                    ? "Archipelago map found: " + map
                    : "Archipelago map not found yet (Subscribe on the Steam Workshop, or " +
                      "use Install on the Play tab to unzip a release into your maps folder).",
            FontSize = 11, Foreground = map != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? lingoDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Lingo install folder (the one containing Lingo.exe). Detected " +
                          "from Steam automatically; set it here to override (non-standard " +
                          "Steam library, or another store).",
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
                Title            = "Select your Lingo install folder (contains Lingo.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? lingoDir ?? "")
                                   ? (overrideDir ?? lingoDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Lingo folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeLingoDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeLingoDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid " + SteamAppId + "). Use " +
                   "this picker only for a non-standard Steam library or another store.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: quick actions ────────────────────────────────────────
        panel.Children.Add(SectionHeader("ADD THE ARCHIPELAGO MAP", muted));
        var subBtn = new System.Windows.Controls.Button
        {
            Content = "Subscribe on Steam Workshop (recommended)",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Padding = new System.Windows.Thickness(12, 6, 12, 6),
            Margin  = new System.Windows.Thickness(0, 0, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
            Cursor = System.Windows.Input.Cursors.Hand,
        };
        subBtn.Click += (_, _) => { try { Process.Start(
            new ProcessStartInfo(WorkshopItemUrl) { UseShellExecute = true }); } catch { } };
        panel.Children.Add(subBtn);

        var openMapsBtn = new System.Windows.Controls.Button
        {
            Content = "Open Lingo maps folder",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Padding = new System.Windows.Thickness(12, 6, 12, 6),
            Margin  = new System.Windows.Thickness(0, 0, 0, 8),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = "Opens %APPDATA%\\Godot\\app_userdata\\Lingo\\maps — the same folder " +
                      "Lingo's in-game \"View Game Data\" button opens. Unzip a release here.",
        };
        openMapsBtn.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(MapsDir);
                Process.Start(new ProcessStartInfo(MapsDir) { UseShellExecute = true });
            }
            catch { /* best effort */ }
        };
        panel.Children.Add(openMapsBtn);

        // ── Section: Connection (this session) ────────────────────────────
        panel.Children.Add(SectionHeader("CONNECTING (entered in-game)", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "In Lingo, go to Settings -> Level and choose Archipelago, then start a " +
                   "new game (leave the name field blank). Enter three fields: the " +
                   "Archipelago address (host:port, e.g. archipelago.gg:38281), your slot " +
                   "name, and the password (if any). Press Connect. The launcher cannot " +
                   "pre-fill these — they are entered in-game.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(SectionHeader("GUIDED SETUP", muted));
        foreach (string step in new[]
        {
            "1. Own Lingo on Steam (appid " + SteamAppId + "). Install it if you have not. " +
                "Use \"Select folder...\" above if it was not auto-detected.",
            "2. Add the Archipelago map. Easiest: click \"Subscribe on Steam Workshop\" " +
                "above and Subscribe — Steam downloads the \"Archipelago\" map by hatkirby " +
                "automatically.",
            "3. Or use Install on the Play tab to download a release and unzip it into your " +
                "maps folder (the launcher will try this for you).",
            "4. Manual alternative: download the latest release from the releases page (link " +
                "below); in Lingo open Settings -> Gameplay -> View Game Data, then unzip the " +
                "release into the \"maps\" folder (all lowercase). Use \"Open Lingo maps " +
                "folder\" above to jump straight there.",
            "5. Launch Lingo from this launcher (or normally). On the main menu, go to " +
                "Settings -> Level, choose Archipelago, start a new game, and enter your " +
                "server address, slot name and password, then press Connect.",
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
        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Lingo Archipelago map (Steam Workshop) ↗", WorkshopItemUrl),
            ("Lingo Archipelago releases / source ↗",    CodeReleasesUrl),
            ("Lingo Setup Guide ↗",                      SetupGuideUrl),
            ("Lingo on Steam ↗",                         LingoSteamStoreUrl),
            ("Auto-tracker (optional) ↗",                TrackerRepoUrl),
            ("Archipelago Official ↗",                   ArchipelagoSite),
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

    private static System.Windows.Controls.TextBlock SectionHeader(
        string text, System.Windows.Media.Brush muted)
        => new System.Windows.Controls.TextBlock
        {
            Text = text, FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        };

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // The Four Island code server is bot-protected and exposes no stable JSON
        // API we can rely on offline, so we surface a small, honest static set of
        // pointers as "news" instead of fabricating release entries. Never throws.
        await Task.CompletedTask;
        var now = DateTimeOffset.UtcNow;
        return new[]
        {
            new NewsItem(
                Title:   "Add the Archipelago map via Steam Workshop",
                Body:    "Subscribe to the \"Archipelago\" map by hatkirby on the Lingo " +
                         "Steam Workshop and Steam downloads it automatically. Then pick it " +
                         "in Settings -> Level in-game.",
                Version: AvailableVersion ?? FallbackVersion,
                Date:    now,
                Url:     WorkshopItemUrl),
            new NewsItem(
                Title:   "Manual install + connecting",
                Body:    "Or download a release from the Four Island code server and unzip " +
                         "it into Lingo's maps folder (Settings -> Gameplay -> View Game " +
                         "Data). Connect in-game from Settings -> Level -> Archipelago: " +
                         "enter your server address, slot name and password, then Connect.",
                Version: AvailableVersion ?? FallbackVersion,
                Date:    now,
                Url:     SetupGuideUrl),
        };
    }

    // ── Private helpers — map version / download (best effort) ─────────────────

    /// Try to discover the newest map release version from the code server's refs
    /// page. Bot-protection usually blocks this; on any failure we return null and
    /// the caller uses the pinned fallback. Never throws beyond cancellation.
    private async Task<string?> ResolveLatestMapVersionAsync(CancellationToken ct)
    {
        try
        {
            string html = await _http.GetStringAsync(CodeReleasesUrl, ct);
            // Look for the highest "vX.Y.Z" token in the refs listing.
            string? best = null;
            Version? bestV = null;
            int i = 0;
            while ((i = html.IndexOf('v', i)) >= 0)
            {
                int j = i + 1;
                int dots = 0;
                while (j < html.Length && (char.IsDigit(html[j]) || html[j] == '.'))
                {
                    if (html[j] == '.') dots++;
                    j++;
                }
                if (j > i + 1 && dots >= 1)
                {
                    string token = html.Substring(i + 1, j - i - 1).TrimEnd('.');
                    if (Version.TryParse(token, out var v) && (bestV == null || v > bestV))
                    {
                        bestV = v;
                        best  = token;
                    }
                }
                i = j > i ? j : i + 1;
            }
            return best;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    /// Best-effort: download the release zip "lingo-archipelago-v<version>.zip" and
    /// unzip it into the Godot maps folder. The code server is a cgit/forgejo
    /// instance behind bot-protection, and the exact download path could not be
    /// verified offline, so we try a small set of plausible URL shapes. Returns
    /// true only if a zip was downloaded AND extracted into the maps folder; false
    /// on any miss (the caller then falls back to guided steps). Never throws
    /// beyond cancellation.
    private async Task<bool> TryDownloadAndInstallMapAsync(
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string zipName = $"lingo-archipelago-v{version}.zip";
        // Candidate download URLs (cgit/forgejo snapshot conventions + a generic
        // releases path). Tried in order; the first that yields a real ZIP wins.
        string[] candidates =
        {
            $"https://code.fourisland.com/lingo-archipelago/snapshot/{zipName}",
            $"https://code.fourisland.com/lingo-archipelago/snapshot/lingo-archipelago-v{version}.zip",
            $"https://code.fourisland.com/lingo-archipelago/releases/download/v{version}/{zipName}",
            $"https://code.fourisland.com/lingo-archipelago/archive/v{version}.zip",
        };

        foreach (string url in candidates)
        {
            ct.ThrowIfCancellationRequested();
            string tempZip = Path.Combine(Path.GetTempPath(),
                $"lingo-archipelago-{version}-{Guid.NewGuid():N}.zip");
            try
            {
                progress.Report((20, "Downloading the Lingo Archipelago map..."));
                using (var response = await _http.GetAsync(
                    url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    if (!response.IsSuccessStatusCode) continue;

                    // Guard against an HTML error/challenge page served as 200.
                    string? mediaType = response.Content.Headers.ContentType?.MediaType;
                    if (mediaType != null &&
                        (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) ||
                         mediaType.Contains("text", StringComparison.OrdinalIgnoreCase)))
                        continue;

                    await using var src = await response.Content.ReadAsStreamAsync(ct);
                    await using var dst = File.Create(tempZip);
                    await src.CopyToAsync(dst, ct);
                    await dst.FlushAsync(ct);
                }

                // Validate it is actually a ZIP (PK\x03\x04) before trusting it.
                if (!IsZipFile(tempZip)) continue;

                progress.Report((70, "Unzipping the map into your Lingo maps folder..."));
                Directory.CreateDirectory(MapsDir);
                // Extract into the maps folder. Most map zips contain a single
                // top-level folder (the map) — we keep whatever structure the zip
                // has, which is exactly what Lingo expects in maps/.
                System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, MapsDir, overwriteFiles: true);

                progress.Report((85, "Map files installed."));
                return FindInstalledMap() != null || Directory.Exists(MapsDir);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* try the next candidate URL */ }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
        }
        return false;
    }

    /// True when a file starts with the ZIP local-file-header magic "PK\x03\x04".
    private static bool IsZipFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) < 4) return false;
            return magic[0] == 0x50 && magic[1] == 0x4B && magic[2] == 0x03 && magic[3] == 0x04;
        }
        catch { return false; }
    }

    // ── Private helpers — Steam / Lingo detection ──────────────────────────────

    /// The Lingo install dir to use: the override (if set and valid) wins, else the
    /// Steam-detected install. Null when nothing is found.
    private string? ResolveLingoDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeLingoDir(ov))
            return ov;

        try { return DetectSteamLingoDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Lingo if it has Lingo.exe (the Godot export).
    private static bool LooksLikeLingoDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, PreferredExeName))) return true;
            // Defensive: some Godot exports differ in case / suffix — accept a lone
            // "*lingo*.exe" too.
            foreach (string exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (name.Contains("lingo")) return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Lingo install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_1814170.acf exists → steamapps\common\Lingo.
    private static string? DetectSteamLingoDir()
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
                        if (LooksLikeLingoDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeLingoDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath + HKLM InstallPath). Duplicates are harmless.
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

    // ── Private helpers — installed-map detection ──────────────────────────────

    /// Find the Archipelago Lingo map. Two valid locations:
    ///   1. The Godot maps folder (%APPDATA%\Godot\app_userdata\Lingo\maps): a
    ///      manual unzip drops an "*archipelago*" / "*lingo-archipelago*" file or
    ///      sub-folder here.
    ///   2. The Steam Workshop content folder for item 3092505110 (a Subscribe):
    ///      steamapps\workshop\content\1814170\3092505110.
    /// Returns a human-readable location string, or null. Never throws.
    private string? FindInstalledMap()
    {
        // (1) maps folder
        try
        {
            if (Directory.Exists(MapsDir))
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(
                             MapsDir, "*", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(entry).ToLowerInvariant();
                    if (name.Contains("archipelago") || name.Contains("lingo-archipelago"))
                        return entry;
                }
            }
        }
        catch { /* permission / vanished */ }

        // (2) Steam Workshop content folder
        try
        {
            foreach (string steamRoot in SteamRoots())
            {
                if (string.IsNullOrWhiteSpace(steamRoot)) continue;
                foreach (string lib in SteamLibraryRoots(steamRoot))
                {
                    string ws = Path.Combine(lib, "steamapps", "workshop", "content",
                                             SteamAppId, WorkshopItemId);
                    try
                    {
                        if (Directory.Exists(ws) &&
                            Directory.EnumerateFileSystemEntries(ws).Any())
                            return ws;
                    }
                    catch { /* try the next library */ }
                }
            }
        }
        catch { /* registry/file access failed */ }

        return null;
    }

    // ── Private helpers — launch ───────────────────────────────────────────────

    /// Start Lingo: prefer the exe in the detected/override install; if that cannot
    /// be found but Steam is present, fall back to the steam:// URL. Surfaces a
    /// clear message rather than failing opaquely.
    private void StartLingo()
    {
        string? dir = ResolveLingoDir();
        string? exe = dir != null ? ResolveLingoExe(dir) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = dir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Lingo.");

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
                IsRunning = true; // best-effort; Steam owns the process, so we won't track exit
                return;
            }
            catch { /* fall through to the error below */ }
        }

        throw new FileNotFoundException(
            "Could not find Lingo.exe. Open this game's Settings and pick your Lingo " +
            "install folder, or install Lingo via Steam.",
            PreferredExeName);
    }

    /// Resolve the Lingo exe inside an install dir: prefer "Lingo.exe", else a lone
    /// "*lingo*.exe" (Godot exports occasionally differ in case/suffix).
    private static string? ResolveLingoExe(string dir)
    {
        string preferred = Path.Combine(dir, PreferredExeName);
        if (File.Exists(preferred)) return preferred;
        try
        {
            foreach (string exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (name.Contains("unins") || name.Contains("setup") ||
                    name.Contains("crash") || name.Contains("vcredist")) continue;
                if (name.Contains("lingo")) return exe;
            }
        }
        catch { /* vanished */ }
        return null;
    }

    // ── Private helpers — self-contained settings sidecar ──────────────────────
    // This plugin keeps its launcher-side settings (the install-dir override + an
    // informational map-version stamp) in its OWN JSON file so it stays a single
    // self-contained source file and does not modify Core/SettingsStore. BOM-less
    // UTF-8, read-modify-write (same approach as Subnautica/Doom/Undertale).

    private sealed class LingoSettings
    {
        public string? InstallOverride { get; set; }
        public string? MapVersion      { get; set; }
    }

    private LingoSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<LingoSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(LingoSettings s)
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
        string? v = LoadSettings().MapVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    private void WriteStampedVersion(string v) { var s = LoadSettings(); s.MapVersion = v; SaveSettings(s); }
}
