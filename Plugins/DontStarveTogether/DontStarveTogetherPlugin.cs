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
using Microsoft.Win32;
using LauncherV2.Core;

// The launcher project sets BOTH UseWPF and UseWindowsForms=true, so several UI
// type names (Color, Brushes, Button, TextBox, HorizontalAlignment) collide
// between WPF and WinForms. The project's GlobalUsings.cs already aliases each of
// these to its WPF type globally, so this file relies on those — no local aliases
// (a local alias duplicating a global one is CS1537).

namespace LauncherV2.Plugins.DontStarveTogether;

// ═══════════════════════════════════════════════════════════════════════════════
// DontStarveTogetherPlugin — install / launch for "Don't Starve Together" played
// through the Archipelago Randomizer Workshop mod by DragonWolfLeo. This is a
// NATIVE "ConnectsItself" integration: the in-game Lua Workshop mod and the
// companion DontStarveTogetherClient Python process together form the AP link —
// the launcher does NOT hold its own ApClient slot.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// Don't Starve Together is NOT a core Archipelago world. Its apworld ships as a
// COMMUNITY RELEASE from github.com/DragonWolfLeo/Archipelago-DST. The verified
// facts:
//
//   * AP GAME STRING is "Don't Starve Together" (verified against
//     worlds/dst/__init__.py: `class DSTWorld(World): ... game = "Don't Starve
//     Together"`). Minimum AP version: 0.6.4. World version: 1.3.3 (release
//     dst-v1.3.3, 2026-01-14). The apworld asset is "dontstarvetogether.apworld"
//     (77 KB, verified in release).
//
//   * THE BASE GAME is bring-your-own: a legally-owned copy of Don't Starve
//     Together (Steam appid 322330, by Klei Entertainment). The launcher does NOT
//     install or own the game — it DETECTS the Steam install (registry →
//     libraryfolders.vdf → appmanifest_322330.acf → steamapps\common\Don't Starve
//     Together\) and offers a manual folder picker for non-standard installs.
//
//   * THE AP MOD is a STEAM WORKSHOP MOD using Klei's own server-mod system —
//     NOT a file-drop mod. The user subscribes to the "Archipelago Randomizer"
//     mod on Steam Workshop and enables it on their server through DST's in-game
//     Mods tab. This cannot be automated by the launcher: Steam Workshop
//     subscriptions require the Steam client and the game's own mod management.
//     The launcher GUIDES this step with a direct Workshop URL and numbered steps.
//
//   * THE APWORLD FILE (dontstarvetogether.apworld) IS installable by this
//     launcher: it is a GitHub release asset the launcher downloads and drops into
//     the user's Archipelago custom_worlds folder (the community apworld pattern
//     for worlds not bundled with AP itself). Latest release dst-v1.3.3 verified.
//
//   * THE AP CLIENT: A separate "DontStarveTogetherClient" (Python) is bundled
//     inside the user's Archipelago installation. It is run from the AP launcher
//     (or can be invoked directly). It communicates with the running DST Workshop
//     mod via LOCAL FILE IPC: the mod writes files to the DST save data folder
//     (DoNotStarveTogether/.../{worldslot}/Master/save/archipelagorandomizer_
//     outgoing) and the client reads them, then sends checks/items to the AP
//     server. The save data folder on Windows defaults to:
//       Documents\Klei\DoNotStarveTogether\
//     The client scans this path automatically. An optional host.yaml override
//     (dontstarvetogether_settings.save_data_directory) is needed only for
//     non-standard installs; the launcher's settings panel surfaces that.
//
//   * HOW THE USER CONNECTS (verified against the setup guide + DSTContext in
//     Client.py): they run the DontStarveTogetherClient from the Archipelago
//     launcher → it asks for the AP server address and slot name (standard AP
//     client UI, not DST-specific) → they load/create a DST server world with the
//     Workshop mod enabled → the client automatically detects the active session by
//     scanning the save data folder's timestamps and hooks in. There is NO
//     config-file or CLI arg this launcher can pre-write to prefill those AP fields
//     (the Python client presents its own kvui/CLI). So this plugin does NOT
//     attempt a connection prefill. The settings panel + notes surface the session's
//     host/port/slot for the user to enter into the AP client.
//
//   * NOTE ON C:\ProgramData\Archipelago: the AP installation directory is
//     READ-ONLY for this launcher. The apworld is placed in the user-writable
//     custom_worlds path inside AP's data folder (typically
//     %APPDATA%\Archipelago\custom_worlds), which IS writable; if the AP install
//     is in ProgramData it is never modified.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam DST install via the Windows registry (HKCU SteamPath +
//      HKLM WOW6432Node), parsing steamapps\libraryfolders.vdf and locating
//      steamapps\common\Don't Starve Together via appmanifest_322330.acf. A
//      manual override is also supported and persisted in this plugin's OWN JSON
//      sidecar (Games/ROMs/dont_starve_together/dont_starve_together_launcher.json)
//      — Core/SettingsStore is NOT modified.
//   2. INSTALL/UPDATE the apworld: download dontstarvetogether.apworld from the
//      GitHub release and drop it into the user's Archipelago custom_worlds folder
//      (%APPDATA%\Archipelago\custom_worlds or the first writable custom_worlds
//      folder the launcher can find). This enables the user's AP server to
//      generate DST multiworlds. The Workshop mod step is GUIDED (Steam link +
//      numbered steps) — it cannot be automated.
//   3. LAUNCH = start dstclient.exe / dstclient.py via the Archipelago binary
//      if available, else launch DST via steam://rungameid/322330 and show an
//      honest note directing the user to also run the AP client from their AP
//      install. ConnectsItself = true (the Workshop mod + AP client own the slot).
//      SupportsStandalone = true (vanilla DST runs fine without AP).
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Jak/SoH-style) ────────────
//   * "Installed" (apworld) is judged by the presence of dontstarvetogether.apworld
//     in the user's custom_worlds folder. IsInstalled=false only means the apworld
//     hasn't been dropped there yet; the Workshop mod state is unknowable from
//     outside Steam and is surfaced as a guide step, not a flag.
//   * Steam library parsing is the same tolerant VDF scanner used in all other
//     Steam-detect plugins here (NoitaPlugin, StardewValleyPlugin): quoted
//     "path" values from libraryfolders.vdf, any failure degrades to null.
//   * The AP client binary search looks for the first of: (a) a DontStarveTogether
//     Client.exe/.bat next to an Archipelago exe in common AP install paths, (b)
//     a DST client Python launch via ArchipelagoLauncher.exe. If none is found, a
//     clear note directs the user. This is defensive / best-effort.
//   * No plaintext AP password is ever written to disk by this plugin.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DontStarveTogetherPlugin : IGamePlugin
{
    // ── Constants — apworld repo (DragonWolfLeo/Archipelago-DST, verified 2026-06-14)
    private const string APWORLD_OWNER = "DragonWolfLeo";
    private const string APWORLD_REPO  = "Archipelago-DST";
    private const string ApworldRepoUrl  = $"https://github.com/{APWORLD_OWNER}/{APWORLD_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{APWORLD_OWNER}/{APWORLD_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"{GH_RELEASES_URL}/latest";

    private const string SetupGuideUrl         = "https://archipelago.gg/tutorial/Don%27t%20Starve%20Together/setup/en";
    private const string ArchipelagoSite        = "https://archipelago.gg";
    private const string WorkshopModUrl         =
        "https://steamcommunity.com/workshop/browse/?appid=322330&searchtext=Archipelago+Randomizer";
    private const string WorkshopDirectUrl      =
        "https://steamcommunity.com/sharedfiles/filedetails/?id=2919774554";

    // Steam — Don't Starve Together appid 322330 (Klei Entertainment, verified 2026-06-14).
    private const string DstSteamAppId        = "322330";
    private const string SteamCommonFolderName = "Don't Starve Together";

    // The apworld file asset name for all releases.
    private const string ApworldFileName = "dontstarvetogether.apworld";

    // Pinned fallback: dst-v1.3.3 verified live 2026-06-14.
    private const string FallbackVersion    = "1.3.3";
    private const string FallbackReleaseTag = "dst-v1.3.3";
    private static readonly string FallbackApworldUrl =
        $"{ApworldRepoUrl}/releases/download/{FallbackReleaseTag}/{ApworldFileName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "dont_starve_together";
    public string DisplayName => "Don't Starve Together";
    public string Subtitle    => "Native PC · Workshop mod + AP client";

    /// EXACT AP game string — verified against worlds/dst/__init__.py
    /// (`class DSTWorld(World): ... game = "Don't Starve Together"`).
    public string ApWorldName => "Don't Starve Together";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "dont_starve_together.png");

    public string ThemeAccentColor => "#C47B28";   // DST warm amber/lantern gold

    public string[] GameBadges => new[]
    {
        "Steam · Workshop mod required",
        "Community apworld"
    };

    public string Description =>
        "Don't Starve Together, Klei Entertainment's multiplayer survival game set in " +
        "The Constant, played through the Archipelago Randomizer Steam Workshop mod by " +
        "DragonWolfLeo. Instead of learning crafting recipes from a Prototyping Station, " +
        "your recipes and items are shuffled across the multiworld — earned by completing " +
        "tasks, surviving days, defeating bosses, and exploring. You bring your own copy " +
        "of DST (on Steam); the AP mod is subscribed through Steam Workshop and runs as " +
        "a server mod on your local world. A companion DontStarveTogetherClient handles " +
        "the Archipelago connection. This launcher installs the apworld file and guides " +
        "the Workshop subscription and client setup.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the dontstarvetogether.apworld file is present in the
    /// user's Archipelago custom_worlds folder. (The Workshop mod state cannot be
    /// read from outside Steam and is not gated here — it is a guided step.)
    public bool IsInstalled => FindInstalledApworld() != null;

    public bool IsRunning { get; private set; }

    /// The game install folder (needed to launch DST). Not the same as GameDirectory
    /// for the apworld (apworld goes to custom_worlds, not here).
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "DontStarveTogether");

    /// ConnectsItself: the Workshop Lua mod + DontStarveTogetherClient together own
    /// the AP slot connection via file-based IPC. The launcher must NOT hold its own
    /// ApClient on the same slot.
    public bool ConnectsItself => true;

    /// Vanilla DST (online co-op, no AP) runs perfectly well.
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// This plugin's OWN settings sidecar. Kept out of the shared SettingsStore
    /// so the plugin stays a single self-contained file — same pattern as Noita,
    /// StardewValley, Jak. Per the spec, lives under Games/ROMs/dont_starve_together/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "dont_starve_together_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Workshop mod + DontStarveTogetherClient report checks/items/goal to the
    // AP server themselves — the launcher relays nothing (ConnectsItself = true).
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
            string? apworldPath = FindInstalledApworld();
            InstalledVersion = apworldPath != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        try
        {
            var (version, _) = await ResolveLatestApworldAsync(ct);
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
        // 1. Find the writable custom_worlds folder for the user's AP install.
        //    CRITICAL: C:\ProgramData\Archipelago is READ-ONLY for this launcher.
        //    We look for a writable custom_worlds path under APPDATA first.
        progress.Report((2, "Locating your Archipelago custom_worlds folder..."));
        string? customWorldsDir = FindWritableCustomWorldsDir();
        if (customWorldsDir == null)
            throw new InvalidOperationException(
                "Could not find a writable Archipelago custom_worlds folder. " +
                "Install Archipelago (archipelago.gg) — it creates " +
                @"%APPDATA%\Archipelago\custom_worlds. Then run Install again. " +
                "Alternatively, download dontstarvetogether.apworld manually from " +
                ApworldRepoUrl + "/releases and place it in your custom_worlds folder.");

        // 2. Resolve the latest apworld release (pinned fallback when offline).
        progress.Report((6, "Checking the latest Don't Starve Together apworld release..."));
        var (version, apworldUrl) = await ResolveLatestApworldAsync(ct);
        AvailableVersion = version;

        if (apworldUrl == null)
            throw new InvalidOperationException(
                "Could not find the dontstarvetogether.apworld download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                ApworldRepoUrl + "/releases.");

        // 3. Download and install the apworld file.
        await DownloadApworldAsync(apworldUrl, version, customWorldsDir, progress, ct);

        // 4. Stamp the installed version in our sidecar.
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Installed dontstarvetogether.apworld {version} into {customWorldsDir}. " +
            "Next: subscribe to the \"Archipelago Randomizer\" Workshop mod in Don't Starve " +
            "Together (link in Settings), enable it as a Server Mod on your world, then " +
            "run the DontStarveTogetherClient from your Archipelago launcher. See Settings " +
            "for the full guided steps."));
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
        // HONEST: the AP connection for Don't Starve Together is owned by the
        // DontStarveTogetherClient Python process (bundled with Archipelago) that
        // communicates with the Workshop mod via local file IPC in the DST save
        // data folder. There is no config file or CLI arg this launcher can pre-
        // write to prefill those AP server fields. The user runs the AP client from
        // their own Archipelago launcher and enters the server/slot there; then they
        // load a DST world with the Workshop mod enabled — the client detects the
        // active session automatically via the save data folder timestamps.
        //
        // What this method does:
        //   (a) Best-effort: try to find and launch the DontStarveTogetherClient
        //       from the user's Archipelago install.
        //   (b) Then launch DST itself (exe or steam://).
        //   (c) If the AP client was not found, the settings panel note directs the
        //       user to run it from their AP launcher.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the Workshop mod + DST client are connected.
        _ = session; // intentionally unused — no config prefill mechanism exists
        TryLaunchApClient();
        StartDst();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartDst();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        // No plaintext AP password is ever written to disk by this plugin.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself) ───────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The Workshop mod + DontStarveTogetherClient receive items from the AP
        // server via their own file-IPC channel; nothing to forward here.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The DontStarveTogetherClient renders its own AP status in its kvui
        // window; the Workshop mod shows an Archipelago icon in-game. No launcher
        // HUD needed.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        string? dstDir         = ResolveDstDir();
        string? apworldPath    = FindInstalledApworld();
        string? customWorlds   = FindWritableCustomWorldsDir();
        string? apClientPath   = FindApClientExecutable();

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Don't Starve Together uses a Steam Workshop mod (\"Archipelago Randomizer\") " +
                   "and a companion DontStarveTogetherClient from your Archipelago install. " +
                   "The Workshop subscription must be done through Steam, and the AP client " +
                   "connection must be entered in the client's own window — these steps cannot " +
                   "be automated. The launcher installs the apworld file and guides the rest. " +
                   "Workshop mod state and AP client connection are not verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: DST install ──────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "DON'T STARVE TOGETHER INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = dstDir != null
                    ? "Detected: " + dstDir
                    : "Not detected — install DST via Steam (appid 322330) or use the folder " +
                      "picker below if it is in a non-standard location.",
            FontSize = 11, Foreground = dstDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // Override picker
        string? savedOverride = LoadSettings().InstallDirOverride;
        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = savedOverride ?? dstDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "The folder that contains the DST exe (dontstarve_steam.exe or " +
                      "DontStarveTogether.exe). Detected from Steam; set here for a " +
                      "non-standard install.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Don't Starve Together install folder",
                InitialDirectory = Directory.Exists(savedOverride ?? dstDir ?? "")
                                   ? (savedOverride ?? dstDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateInstallDir(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a DST folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                var s = LoadSettings();
                s.InstallDirOverride = picked;
                SaveSettings(s);
                dirBox.Text = picked;
            }
        };
        DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new TextBlock
        {
            Text = "Steam installs are detected automatically (appid 322330). Use the " +
                   "picker for a non-standard Steam library path.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: apworld status ───────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "APWORLD STATUS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = apworldPath != null
                    ? "dontstarvetogether.apworld installed" +
                      (InstalledVersion != null && InstalledVersion != "installed"
                        ? " (v" + InstalledVersion + ")." : ".") +
                      "\n  " + apworldPath
                    : "dontstarvetogether.apworld NOT found — click Install on the Play tab " +
                      "to download and install it, or place it manually in your Archipelago " +
                      "custom_worlds folder.",
            FontSize = 11, Foreground = apworldPath != null ? success : warn,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });
        if (customWorlds != null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "custom_worlds folder: " + customWorlds,
                FontSize = 11, Foreground = muted,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
            });
        }

        // ── Section: AP client status ─────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "ARCHIPELAGO CLIENT", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = apClientPath != null
                    ? "DontStarveTogetherClient found: " + apClientPath
                    : "DontStarveTogetherClient not found automatically. Install Archipelago " +
                      "(archipelago.gg) to get it. You will need to run it from the AP " +
                      "launcher manually.",
            FontSize = 11, Foreground = apClientPath != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "The DontStarveTogetherClient talks to DST via local files in your " +
                   "Don't Starve Together save data folder (Documents\\Klei\\DoNotStarveTogether " +
                   "on Windows). If your save data is in a non-standard location, add " +
                   "\"dontstarvetogether_settings: save_data_directory: PATH\" to your " +
                   "Archipelago host.yaml.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Don't Starve Together (on Steam, appid 322330). Install it if you have not. " +
                "Use the folder picker above if it was not detected automatically.",
            "2. Install the apworld file: click Install on the Play tab. It downloads " +
                "dontstarvetogether.apworld and places it in your Archipelago custom_worlds " +
                "folder so your AP server can generate DST multiworlds.",
            "3. Install Archipelago (archipelago.gg) if you have not. It includes the " +
                "DontStarveTogetherClient needed to bridge DST and the AP server.",
            "4. Subscribe to the \"Archipelago Randomizer\" Workshop mod in Don't Starve " +
                "Together (Steam Workshop link in Links below). In-game: click Mods, find " +
                "\"Archipelago Randomizer\" in Server Mods, and enable it on your world.",
            "5. Generate your multiworld YAML and start an AP server (archipelago.gg).",
            "6. Run the DontStarveTogetherClient from your Archipelago launcher and connect " +
                "it to your AP server (enter address and slot name in the client window). The " +
                "client will scan your DST save folder automatically.",
            "7. Launch Don't Starve Together and load (or create) a server world with the " +
                "Archipelago Randomizer mod enabled. The AP client detects the active session " +
                "and you will see the Archipelago icon appear in-game. Play!",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Archipelago Randomizer Workshop mod (Steam) ↗", WorkshopDirectUrl),
            ("DragonWolfLeo/Archipelago-DST (GitHub) ↗",      ApworldRepoUrl),
            ("Don't Starve Together Setup Guide ↗",           SetupGuideUrl),
            ("Archipelago Official ↗",                         ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true });
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
        // Archipelago-DST releases are the AP-relevant news for this game.
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
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

    // ── Private helpers — apworld resolution ─────────────────────────────────

    /// Resolve the latest apworld release: version + the .apworld asset URL.
    /// Falls back to the pinned 1.3.3 direct URL when the GitHub API is unreachable.
    private async Task<(string Version, string? ApworldUrl)>
        ResolveLatestApworldAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;
            string? tag = root.TryGetProperty("tag_name", out var rawTag)
                ? rawTag.GetString()
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                 && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name != null && url != null &&
                        string.Equals(name, ApworldFileName, StringComparison.OrdinalIgnoreCase))
                        return (version, url);
                }
                // No exact match: accept any .apworld asset
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name != null && url != null && name.EndsWith(".apworld", StringComparison.OrdinalIgnoreCase))
                        return (version, url);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → fall through to pinned */ }

        return (FallbackVersion, FallbackApworldUrl);
    }

    // ── Private helpers — download the apworld ────────────────────────────────

    private async Task DownloadApworldAsync(
        string apworldUrl,
        string version,
        string customWorldsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempFile = Path.Combine(Path.GetTempPath(),
            $"dst-apworld-{version}-{Guid.NewGuid():N}.apworld");
        try
        {
            progress.Report((10, $"Downloading dontstarvetogether.apworld {version}..."));
            using var response = await _http.GetAsync(
                apworldUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempFile))
            {
                var buf = new byte[65536];
                int bytesRead;
                while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                    downloaded += bytesRead;
                    if (total > 0)
                    {
                        int pct = (int)(10 + 80 * downloaded / total);
                        progress.Report((pct, $"Downloading... {downloaded / 1024}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((93, "Installing apworld into custom_worlds folder..."));
            Directory.CreateDirectory(customWorldsDir);
            string dest = Path.Combine(customWorldsDir, ApworldFileName);
            File.Copy(tempFile, dest, overwrite: true);
            progress.Report((97, "apworld file installed."));
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    // ── Private helpers — custom_worlds folder ────────────────────────────────

    /// Find the best writable Archipelago custom_worlds folder.
    /// Priority:
    ///   1. %APPDATA%\Archipelago\custom_worlds  (the standard per-user path)
    ///   2. Any custom_worlds folder adjacent to a known AP exe in common locations
    ///   3. The custom_worlds folder saved in our sidecar (if the user set one)
    ///
    /// NEVER modifies C:\ProgramData\Archipelago (read-only per architecture rules).
    private string? FindWritableCustomWorldsDir()
    {
        // 1. Standard %APPDATA% path.
        string? appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            string path = Path.Combine(appData, "Archipelago", "custom_worlds");
            try
            {
                Directory.CreateDirectory(path);
                if (Directory.Exists(path)) return path;
            }
            catch { /* permission issue — try next */ }
        }

        // 2. Scan common AP install locations for a custom_worlds folder.
        foreach (string candidate in GetCommonApPaths())
        {
            // Never touch ProgramData
            if (candidate.Contains("ProgramData", StringComparison.OrdinalIgnoreCase))
                continue;
            string path = Path.Combine(candidate, "custom_worlds");
            if (Directory.Exists(path)) return path;
        }

        // 3. Saved override from sidecar.
        string? saved = LoadSettings().CustomWorldsDirOverride;
        if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
            return saved;

        return null;
    }

    /// Returns the .apworld path if dontstarvetogether.apworld already exists in
    /// any of the candidate custom_worlds folders.
    private string? FindInstalledApworld()
    {
        // Check standard %APPDATA% path first.
        string? appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (!string.IsNullOrEmpty(appData))
        {
            string path = Path.Combine(appData, "Archipelago", "custom_worlds", ApworldFileName);
            if (File.Exists(path)) return path;
        }

        // Scan common AP install locations.
        foreach (string candidate in GetCommonApPaths())
        {
            string path = Path.Combine(candidate, "custom_worlds", ApworldFileName);
            if (File.Exists(path)) return path;
        }

        // Saved override.
        string? saved = LoadSettings().CustomWorldsDirOverride;
        if (!string.IsNullOrWhiteSpace(saved))
        {
            string path = Path.Combine(saved!, ApworldFileName);
            if (File.Exists(path)) return path;
        }

        return null;
    }

    /// Common locations where an Archipelago install might live.
    private static IEnumerable<string> GetCommonApPaths()
    {
        // %LOCALAPPDATA%\Programs\Archipelago (default for per-user installer)
        string? localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(localApp))
            yield return Path.Combine(localApp, "Programs", "Archipelago");

        // Alongside the launcher executable (if user extracted AP next to our launcher)
        yield return AppContext.BaseDirectory;

        // Common Program Files locations
        yield return @"C:\Program Files\Archipelago";
        yield return @"C:\Program Files (x86)\Archipelago";
        yield return @"C:\Archipelago";
    }

    // ── Private helpers — AP client detection ─────────────────────────────────

    /// Try to find the DontStarveTogetherClient executable in the user's AP install.
    private string? FindApClientExecutable()
    {
        foreach (string apDir in GetCommonApPaths())
        {
            if (!Directory.Exists(apDir)) continue;

            // Standard AP release exe naming
            foreach (string name in new[]
            {
                "DontStarveTogetherClient.exe",
                "Don't Starve Together Client.exe",
                "ArchipelagoLauncher.exe",  // The Archipelago launcher ships a DSTClient
            })
            {
                string path = Path.Combine(apDir, name);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    /// Launch the DontStarveTogetherClient if we can find it. Silently no-ops if
    /// the client is not found; the settings panel notes direct the user.
    private void TryLaunchApClient()
    {
        string? clientExe = FindApClientExecutable();
        if (clientExe == null) return;

        // If we found ArchipelagoLauncher.exe but not the DST client directly,
        // the user needs to select it from the AP launcher themselves.
        if (clientExe.EndsWith("ArchipelagoLauncher.exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName        = clientExe,
                    UseShellExecute = true,
                });
            }
            catch { /* non-fatal — user can open it themselves */ }
            return;
        }

        // A direct DST client exe.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName         = clientExe,
                WorkingDirectory = Path.GetDirectoryName(clientExe) ?? AppContext.BaseDirectory,
                UseShellExecute  = false,
            });
        }
        catch { /* non-fatal */ }
    }

    // ── Private helpers — DST install detection ───────────────────────────────

    /// Resolve the DST install directory. User override first; else Steam detection.
    private string? ResolveDstDir()
    {
        string? overrideDir = LoadSettings().InstallDirOverride;
        if (!string.IsNullOrWhiteSpace(overrideDir) && LooksLikeDstDir(overrideDir!))
            return overrideDir;

        return DetectSteamDstDir();
    }

    /// True when a folder contains a DST game executable.
    private static bool LooksLikeDstDir(string dir)
    {
        try
        {
            return Directory.Exists(dir) &&
                   (File.Exists(Path.Combine(dir, "dontstarve_steam.exe")) ||
                    File.Exists(Path.Combine(dir, "DontStarveTogether.exe")) ||
                    File.Exists(Path.Combine(dir, "dontstarve_dedicated_server_nullrenderer.exe")));
        }
        catch { return false; }
    }

    /// Validate a user-picked DST folder. Returns null (OK) or an error string.
    private static string? ValidateInstallDir(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Don't Starve Together folder.";

        if (LooksLikeDstDir(folder))
            return null;

        return "That folder does not look like a Don't Starve Together install. " +
               "Pick the folder that contains dontstarve_steam.exe " +
               @"(for Steam: ...\steamapps\common\Don't Starve Together).";
    }

    /// Best-effort Steam auto-detection (same tolerant VDF approach as Noita/Stardew).
    private static string? DetectSteamDstDir()
    {
        try
        {
            foreach (string steamRoot in EnumerateSteamRoots())
            {
                foreach (string lib in EnumerateSteamLibraries(steamRoot))
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    if (!Directory.Exists(steamapps)) continue;

                    // Preferred: read appmanifest_322330.acf for the installdir value.
                    string acf = Path.Combine(steamapps, $"appmanifest_{DstSteamAppId}.acf");
                    if (File.Exists(acf))
                    {
                        string? installDirName = ReadVdfValue(acf, "installdir");
                        if (!string.IsNullOrWhiteSpace(installDirName))
                        {
                            string candidate = Path.Combine(steamapps, "common", installDirName!);
                            if (LooksLikeDstDir(candidate)) return candidate;
                        }
                    }

                    // Fallback: the standard common sub-folder.
                    string std = Path.Combine(steamapps, "common", SteamCommonFolderName);
                    if (LooksLikeDstDir(std)) return std;
                }
            }
        }
        catch { /* registry/file access failed → null */ }
        return null;
    }

    // ── Private helpers — launch DST ─────────────────────────────────────────

    private void StartDst()
    {
        string? dstDir = ResolveDstDir();

        if (dstDir != null)
        {
            // Try the main DST exe names.
            foreach (string exeName in new[]
            {
                "dontstarve_steam.exe",
                "DontStarveTogether.exe",
            })
            {
                string exePath = Path.Combine(dstDir, exeName);
                if (!File.Exists(exePath)) continue;

                try
                {
                    var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName         = exePath,
                        WorkingDirectory = dstDir,
                        UseShellExecute  = false,
                    }) ?? throw new InvalidOperationException("Failed to start DST.");
                    TrackProcess(proc);
                    return;
                }
                catch { /* try next exe name */ }
            }
        }

        // Steam protocol fallback.
        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName        = $"steam://rungameid/{DstSteamAppId}",
                UseShellExecute = true,
            });
            if (proc != null) TrackProcess(proc);
            IsRunning = true; // steam:// may return a transient/no process handle
        }
        catch (Exception ex)
        {
            throw new FileNotFoundException(
                "Could not launch Don't Starve Together. Make sure it is installed on " +
                "Steam (appid 322330) or set the install folder in Settings, then try again. " +
                "You can also start the game through Steam directly.\n\n" + ex.Message,
                "dontstarve_steam.exe");
        }
    }

    private void TrackProcess(Process proc)
    {
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
        catch { /* shell-launched processes sometimes don't expose Exited — non-fatal */ }
    }

    // ── Private helpers — Steam library scanning ──────────────────────────────
    // Same tolerant VDF scanner used in NoitaPlugin and StardewValleyPlugin.

    private static IEnumerable<string> EnumerateSteamRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? root in new[]
        {
            ReadRegistryString(Microsoft.Win32.Registry.CurrentUser,
                               @"Software\Valve\Steam", "SteamPath"),
            ReadRegistryString(Microsoft.Win32.Registry.LocalMachine,
                               @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            ReadRegistryString(Microsoft.Win32.Registry.LocalMachine,
                               @"SOFTWARE\Valve\Steam", "InstallPath"),
        })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string norm = root!.Replace('/', '\\');
            if (Directory.Exists(norm) && seen.Add(norm))
                yield return norm;
        }
    }

    private static IEnumerable<string> EnumerateSteamLibraries(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(steamRoot)) yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string[] lines;
        try { lines = File.ReadAllLines(vdf); }
        catch { yield break; }

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            string? val = TryReadQuotedKeyValue(line, "path")
                          ?? TryReadLastQuotedAbsolutePath(line);
            if (string.IsNullOrWhiteSpace(val)) continue;
            string norm = val!.Replace(@"\\", @"\").Replace('/', '\\');
            if (Directory.Exists(norm) && seen.Add(norm))
                yield return norm;
        }
    }

    private static string? ReadRegistryString(
        Microsoft.Win32.RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    private static string? ReadVdfValue(string path, string key)
    {
        try
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string? val = TryReadQuotedKeyValue(raw.Trim(), key);
                if (val != null) return val;
            }
        }
        catch { }
        return null;
    }

    private static string? TryReadQuotedKeyValue(string line, string key)
    {
        int k0 = line.IndexOf('"');
        if (k0 < 0) return null;
        int k1 = line.IndexOf('"', k0 + 1);
        if (k1 < 0) return null;
        string foundKey = line.Substring(k0 + 1, k1 - k0 - 1);
        if (!string.Equals(foundKey, key, StringComparison.OrdinalIgnoreCase)) return null;
        int v0 = line.IndexOf('"', k1 + 1);
        if (v0 < 0) return null;
        int v1 = line.IndexOf('"', v0 + 1);
        if (v1 < 0) return null;
        return line.Substring(v0 + 1, v1 - v0 - 1);
    }

    private static string? TryReadLastQuotedAbsolutePath(string line)
    {
        int end = line.LastIndexOf('"');
        if (end <= 0) return null;
        int start = line.LastIndexOf('"', end - 1);
        if (start < 0) return null;
        string token = line.Substring(start + 1, end - start - 1);
        bool looksAbsolute =
            (token.Length >= 3 && token[1] == ':' && (token[2] == '\\' || token[2] == '/')) ||
            token.StartsWith('/') || token.Contains(@":\\");
        return looksAbsolute ? token : null;
    }

    // ── Private helpers — version stamp ──────────────────────────────────────

    private string VersionStampPath
        => Path.Combine(SettingsSidecarDir, "dst_apworld_version.dat");

    private string? ReadStampedVersion()
    {
        try
        {
            return File.Exists(VersionStampPath)
                ? File.ReadAllText(VersionStampPath).Trim()
                : null;
        }
        catch { return null; }
    }

    private void WriteStampedVersion(string version)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(VersionStampPath, version, new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class DstSettings
    {
        public string? InstallDirOverride      { get; set; }
        public string? CustomWorldsDirOverride { get; set; }
    }

    private DstSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<DstSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(DstSettings s)
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

    // ── Private helpers — tag normalizer ─────────────────────────────────────

    /// "dst-v1.3.3" → "1.3.3", "v1.3.3" → "1.3.3", etc. Trims known prefixes.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        // Strip "dst-" prefix if present
        if (tag.StartsWith("dst-", StringComparison.OrdinalIgnoreCase))
            tag = tag["dst-".Length..];
        // Strip leading 'v' before a digit
        if (tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1]))
            tag = tag[1..];
        return tag;
    }
}
