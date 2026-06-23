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
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Shapez;

// ═══════════════════════════════════════════════════════════════════════════════
// ShapezPlugin — install / launch for "shapez" (tobspr Games, 2020), the factory/
// automation puzzle game, played through the "shapezipelago" client MOD — the
// in-game Archipelago client for shapez. This is a NATIVE "ConnectsItself"
// integration in the same family as the shipped Subnautica / Hollow Knight /
// Stardew Valley plugins (and Ship of Harkinian, APDOOM, Jak): the game itself
// speaks to the AP server (no emulator, no Lua bridge, no launcher-held ApClient
// on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native, but with one honest WIN over Subnautica/HK: shapez
// has a FIRST-PARTY, BUILT-IN MOD LOADER (added in shapez Update 1.5.1, "Official
// modloader!", 2022-02-25). A mod is a SINGLE .js file dropped into a per-user
// mods folder — no BepInEx, no mod-manager, no dependency chain. The verified
// facts (sources: the official AP "shapez" setup guide
// worlds/shapez/docs/setup_en.md, the mod's GitHub releases, and the in-game
// modloader docs):
//
//   * THE AP WORLD game string is "shapez" (verified against
//     worlds/shapez/data/strings.py: `class OTHER: game_name = "shapez"`, used as
//     `ShapezWorld.game = OTHER.game_name`). shapez is a CORE Archipelago world —
//     it ships inside Archipelago itself, no custom-world drop is required for
//     generation. GameId here = "shapez".
//
//   * THE MOD is "shapezipelago" by BlastSlimey. The official setup guide points
//     at its mod.io page (https://mod.io/g/shapez/m/shapezipelago); the same
//     author also publishes it on GitHub (BlastSlimey/shapezipelago) with
//     identical assets, which is the machine-readable source this plugin uses for
//     auto-download + version/news. Latest release verified live 2026-06-14 is
//     tag 0.6.3, with TWO assets:
//         shapezipelago@0.6.3.js   (~237 KB — the mod itself, the file to install)
//         shapez.apworld           (~1.18 MB — the world; ships in core AP too)
//     IMPORTANT — the asset filename embeds the version and an '@'
//     ("shapezipelago@X.X.X.js"). shapez's modloader keys mods by id, so the
//     filename is not load-bearing for the loader, but we keep the upstream name
//     so the user can see which version they have.
//
//   * INSTALLATION IS FULLY AUTOMATABLE (the honest WIN). The setup guide states:
//     "As the game has a built-in mod loader, all you need to do is copy the
//     `shapezipelago@X.X.X.js` mod file into the mods folder. If you don't know
//     where that is, open the game, click on `MODS`, and then `OPEN MODS FOLDER`."
//     The standalone (Steam) build is an Electron app whose user data lives under
//     %APPDATA%\shapez.io, so the mods folder is %APPDATA%\shapez.io\mods (verified
//     via the modloader docs / PCGamingWiki). This is a KNOWN per-user path that
//     does NOT depend on the Steam install location, so this plugin CAN do a real,
//     reliable one-shot install: download the .js and drop it straight into that
//     folder. We ALSO guide the in-game "OPEN MODS FOLDER" route honestly as the
//     authoritative fallback (a few users relocate %APPDATA%, or run a non-Steam
//     build), and offer a button that opens the mods folder for a manual drop.
//
//   * CONNECTION is made IN-GAME, with NO prefill mechanism (verified against the
//     setup guide): "In the main menu, type the slot name, address, port, and
//     password (optional) into the input box. Click \"Connect\". ... After creating
//     the save file and returning to the main menu, opening the save file again
//     will automatically reconnect." There is NO command-line arg and NO config
//     file this launcher can pre-write for the connection. So this plugin does NOT
//     attempt a connection prefill — the post-install note and settings panel state
//     this and surface the session's host/port/slot so the user can copy them into
//     those fields. (shapez DOES accept a `--dev` launch flag that enables a dev
//     console + mod hot-reload, but that is a developer aid, not a connection
//     channel, so we do not pass it.)
//
//   * RECOMMENDED in-game setting (from the guide, surfaced in Settings): disable
//     "HINTS & TUTORIALS" in the USER INTERFACE tab, otherwise the upgrade shop
//     stays locked until a few levels are done.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. INSTALL/UPDATE = download the mod's "shapezipelago@X.X.X.js" from the real
//      GitHub release and copy it straight into %APPDATA%\shapez.io\mods (creating
//      the folder if needed). Old shapezipelago@*.js copies are removed first so a
//      stale version cannot also load. This is a genuine one-click install — no
//      mod-manager, no dependencies. If the mods folder cannot be resolved/written,
//      it falls back to saving the .js next to the launcher and the panel guides
//      the in-game "OPEN MODS FOLDER" manual drop.
//   2. DETECT the Steam shapez install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, HKLM\...\Valve\Steam ->
//      InstallPath), parsing steamapps\libraryfolders.vdf for every library root
//      and locating steamapps\common\shapez via appmanifest_1318690.acf. This is
//      used for the EXE launch path; a manual install-dir OVERRIDE (settings
//      folder picker) is supported, validated, and persisted in this plugin's OWN
//      sidecar (Games/ROMs/shapez/shapez_launcher.json) — Core/SettingsStore is
//      NOT modified.
//   3. LAUNCH = run shapez.exe from the detected/override install; if the exe
//      cannot be found but Steam is present, fall back to
//      steam://rungameid/1318690. ConnectsItself = true (the mod owns the slot —
//      the launcher must NOT hold its own ApClient on it). SupportsStandalone =
//      true (plain shapez runs perfectly without AP). No connection prefill
//      (in-game input box), stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Subnautica/HK-style) ──────
//   * "Installed" is judged by the presence of a shapezipelago@*.js (or any
//     *.js whose name contains "shapezipelago") in the resolved mods folder —
//     case-insensitive — NOT by an OUR-OWN version stamp, because the user may
//     have dropped the .js in by hand, which this launcher honors. If the mods
//     folder cannot be resolved, the tile reads "not installed".
//   * The mods-folder path defaults to %APPDATA%\shapez.io\mods but is overridable
//     in Settings for relocated-%APPDATA% / non-Steam builds. Detection degrades
//     to "not installed" rather than throwing.
//   * Steam library parsing is defensive: a tolerant VDF scan that pulls quoted
//     "path" values; any failure degrades to "shapez not found" rather than
//     throwing.
//   * No plaintext AP password is ever written by this plugin (the connection is
//     entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ShapezPlugin : IGamePlugin
{
    // ── Constants — the shapezipelago mod (real repo, verified 2026-06-14) ─────
    private const string MOD_OWNER = "BlastSlimey";
    private const string MOD_REPO  = "shapezipelago";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string ModReleasesPageUrl = $"{ModRepoUrl}/releases";
    private const string ModIoPageUrl   = "https://mod.io/g/shapez/m/shapezipelago";
    private const string SteamStoreUrl  = "https://store.steampowered.com/app/1318690/shapez/";
    private const string SetupGuideUrl  = "https://archipelago.gg/tutorial/shapez/setup/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — shapez appid 1318690.
    private const string SteamAppId = "1318690";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The standard Steam install sub-folder name for shapez.
    private const string SteamCommonFolderName = "shapez";

    /// Pinned fallback for the mod when the GitHub API is unreachable. Tag 0.6.3
    /// verified live 2026-06-14; the mod asset is "shapezipelago@0.6.3.js". The
    /// API path is the normal route; this is the net so an offline Install still
    /// has something to fetch.
    private const string FallbackVersion = "0.6.3";
    private const string FallbackJsName  = "shapezipelago@0.6.3.js";
    // NOTE: the '@' must be URL-encoded (%40) in the direct download URL.
    private static readonly string FallbackJsUrl =
        $"{ModRepoUrl}/releases/download/{FallbackVersion}/shapezipelago%400.6.3.js";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "shapez";
    public string DisplayName => "shapez";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/shapez/data/strings.py
    /// (class OTHER: game_name = "shapez"; ShapezWorld.game = OTHER.game_name).
    public string ApWorldName => "shapez";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "shapez.png");

    public string ThemeAccentColor => "#3CA0E0";   // shapez signal-blue
    public string[] GameBadges     => new[] { "Requires shapez on Steam" };

    public string Description =>
        "shapez, tobspr Games' 2020 factory-automation puzzle game, played through " +
        "the shapezipelago mod — an in-game Archipelago client. Building variants, " +
        "upgrades, level rewards and the colossal \"shapesanity\" shape pool are " +
        "shuffled into the multiworld, and the game connects to the Archipelago " +
        "server itself (no emulator, no bridge). You bring your own copy of shapez " +
        "on Steam, and the mod is a single .js file: shapez has a built-in mod " +
        "loader, so the launcher can drop the mod straight into your mods folder " +
        "for you — no mod manager, no dependencies. You connect to your server from " +
        "the input box on shapez's main menu.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the shapezipelago mod .js is present in the resolved mods
    /// folder. We do NOT gate on our own stamp — the user may have dropped the .js
    /// in by hand, which we honor.
    public bool IsInstalled => FindInstalledModJs() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps its download fallback copy and working files. The
    /// actual mod is copied INTO the shapez mods folder (%APPDATA%\shapez.io\mods),
    /// not here. Exposed as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Shapez");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Subnautica/HK/Doom).
    /// Per the brief, lives under Games/ROMs/shapez/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "shapez_launcher.json");

    /// The default standalone-shapez mods folder on Windows: the Electron build
    /// stores user data under %APPDATA%\shapez.io, so mods live in
    /// %APPDATA%\shapez.io\mods. Overridable in Settings.
    private static string DefaultModsDir
    {
        get
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "shapez.io", "mods");
        }
    }

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The shapezipelago mod reports checks/items/goal to the AP server itself —
    // the launcher relays nothing. These exist for interface compatibility
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
            // Best-effort: read the version we stamped at install time if present;
            // otherwise report "installed" when the mod .js exists in the mods folder.
            InstalledVersion = FindInstalledModJs() != null
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
                await GitHubHelper.FetchLatestTagAsync(MOD_OWNER, MOD_REPO, ct));
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
        // 1. Resolve the latest mod release (pinned fallback when offline).
        progress.Report((4, "Checking the latest shapezipelago release..."));
        var (version, jsUrl, jsName) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (jsUrl == null)
            throw new InvalidOperationException(
                "Could not find the shapezipelago mod download on GitHub. Check your " +
                "internet connection, or download the mod (a single .js file) from " +
                ModIoPageUrl + " and drop it into your shapez mods folder (in-game: " +
                "MODS -> OPEN MODS FOLDER). See Settings for the guided steps. The mod " +
                "repo is " + ModRepoUrl + ".");

        // 2. Resolve the mods folder (override wins; else %APPDATA%\shapez.io\mods).
        //    This is a KNOWN per-user path, so a real one-shot install is possible.
        string? modsDir = ResolveModsDir(createIfMissing: true);
        string fileName = string.IsNullOrWhiteSpace(jsName) ? FallbackJsName : jsName!;

        if (modsDir != null)
        {
            // 3a. Download the .js and drop it straight into the mods folder,
            //     removing any older shapezipelago@*.js first so a stale version
            //     cannot also load.
            await DownloadModJsAsync(jsUrl, version, modsDir, fileName, progress, ct);

            WriteStampedVersion(version);
            InstalledVersion = version;

            progress.Report((100,
                $"Installed shapezipelago {version} into your shapez mods folder " +
                $"({modsDir}). To play: launch shapez, and in the MAIN MENU type your " +
                "server address, port, slot name, and optional password into the input " +
                "box, then click Connect and create a new game. Your save reconnects " +
                "automatically next time. Tip: disable \"HINTS & TUTORIALS\" in the " +
                "game's USER INTERFACE settings so the upgrade shop is not locked. See " +
                "Settings for details."));
        }
        else
        {
            // 3b. Could not resolve/create the mods folder (relocated %APPDATA%,
            //     non-Steam build, permissions). Save the .js next to the launcher
            //     and guide the in-game OPEN MODS FOLDER manual drop. HONEST — never
            //     a fake "installed".
            Directory.CreateDirectory(GameDirectory);
            string fallbackPath = Path.Combine(GameDirectory, fileName);
            await DownloadFileAsync(jsUrl, fallbackPath, version, progress, ct);

            WriteStampedVersion(version);
            // IsInstalled remains false until the user drops it into the mods folder.

            progress.Report((100,
                $"Downloaded shapezipelago {version} to {fallbackPath}. I could not " +
                "find your shapez mods folder automatically. To finish: launch shapez, " +
                "click MODS -> OPEN MODS FOLDER, and copy that .js file into the folder " +
                "that opens (then restart shapez). Or set your mods folder in Settings. " +
                "Then connect from the main-menu input box."));
        }
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
        // HONEST: the AP server connection for shapez is entered in the IN-GAME
        // input box on the main menu (slot name, address, port, optional password).
        // The mod reconnects automatically when a save is re-opened. There is no
        // command-line / config prefill we can apply (verified — see header). So
        // launching from this tile just starts the game; the user connects in-game
        // with the session credentials (the settings panel + note surface those to
        // copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartShapez();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) shapez runs perfectly well.
    public bool SupportsStandalone => true;

    /// The shapezipelago mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartShapez();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started shapez from here. Kill what we launched.
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
        // The shapezipelago mod receives items from the AP server directly; there
        // is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game (next to the Connect button).
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid shapez folder contains
    /// shapez.exe (the Steam standalone build). Return null when acceptable, else
    /// a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your shapez install folder.";

        if (LooksLikeShapezDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeShapezDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a shapez installation. Pick the folder that " +
               "contains shapez.exe. For Steam this is usually " +
               @"...\steamapps\common\shapez.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "shapez is your own game (Steam). The Archipelago client is the " +
                   "shapezipelago mod — a single .js file. shapez has a built-in mod " +
                   "loader, so the launcher can drop the mod straight into your mods " +
                   "folder for you (no mod manager, no dependencies). You connect to " +
                   "your server from the input box on shapez's main menu — there is no " +
                   "connection file to pre-fill. These external steps are not verified " +
                   "by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: mod status / mods folder ─────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "MOD STATUS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? modJs   = FindInstalledModJs();
        string? modsDir = ResolveModsDir(createIfMissing: false);
        panel.Children.Add(new TextBlock
        {
            Text = modJs != null
                    ? "shapezipelago mod found: " + modJs
                    : "shapezipelago mod not found in your mods folder yet (use Install " +
                      "on the Play tab, or drop the .js into the mods folder by hand).",
            FontSize = 11, Foreground = modJs != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });
        panel.Children.Add(new TextBlock
        {
            Text = modsDir != null
                    ? "Mods folder: " + modsDir
                    : "Mods folder not found automatically. Launch shapez and click " +
                      "MODS -> OPEN MODS FOLDER to see it, or set it below.",
            FontSize = 11, Foreground = modsDir != null ? fg : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // mods-folder override row
        var modsRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var modsBox = new TextBox
        {
            Text = LoadModsOverride() ?? modsDir ?? DefaultModsDir, IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your shapez mods folder (where mod .js files live). " +
                          @"Defaults to %APPDATA%\shapez.io\mods. Set it here if your " +
                          "%APPDATA% is relocated or you run a non-Steam build.",
        };
        var modsBtn = new Button
        {
            Content = "Set folder...", Width = 110, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        modsBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your shapez mods folder",
                InitialDirectory = Directory.Exists(modsDir ?? DefaultModsDir)
                                   ? (modsDir ?? DefaultModsDir)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                SaveModsOverride(dlg.FolderName);
                modsBox.Text = dlg.FolderName;
            }
        };
        DockPanel.SetDock(modsBtn, Dock.Right);
        modsRow.Children.Add(modsBtn);
        modsRow.Children.Add(modsBox);
        panel.Children.Add(modsRow);

        // open-mods-folder convenience button
        var openBtn = new Button
        {
            Content = "Open mods folder", HorizontalAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 6, 0, 12),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        openBtn.Click += (_, _) =>
        {
            string? dir = ResolveModsDir(createIfMissing: true);
            try
            {
                if (dir != null && Directory.Exists(dir))
                    Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                else
                    MessageBox.Show(
                        "Could not find your shapez mods folder. Launch shapez and click " +
                        "MODS -> OPEN MODS FOLDER, or set the folder above.",
                        "shapez mods folder", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { /* non-fatal */ }
        };
        panel.Children.Add(openBtn);

        // ── Section: shapez install (for launching) ───────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "SHAPEZ INSTALL (for launching)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });

        string? snDir       = ResolveShapezDir();
        string? overrideDir = LoadOverrideDir();
        panel.Children.Add(new TextBlock
        {
            Text = snDir != null
                    ? (overrideDir != null
                        ? "Using your selected folder: " + snDir
                        : "Detected Steam install: " + snDir)
                    : "shapez not detected. The launcher can still start it via Steam " +
                      "(appid 1318690), or pick your install folder below.",
            FontSize = 11, Foreground = snDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? snDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your shapez install folder (the one containing shapez.exe). " +
                          "Detected from Steam automatically; set it here to override a " +
                          "non-standard Steam library.",
        };
        var dirBtn = new Button
        {
            Content = "Select folder...", Width = 120, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your shapez install folder (contains shapez.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? snDir ?? "")
                                   ? (overrideDir ?? snDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    MessageBox.Show(bad, "Not a shapez folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeShapezDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeShapezDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 1318690). Use this " +
                   "picker for a non-standard Steam library.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (this session) ────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (entered in-game on the main-menu input box)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "On shapez's main menu, type your server address, port, slot name, and " +
                   "optional password into the input box, then click Connect and create a " +
                   "new game. The status shows next to the button. Re-opening that save " +
                   "reconnects automatically — you only enter it once per save.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own shapez on Steam (appid 1318690). Install it if you have not.",
            "2. Install the shapezipelago mod: use Install on the Play tab (it downloads " +
                "the .js and drops it into your mods folder for you). Or download it from " +
                "the mod.io page (link below) and drop it into the folder yourself.",
            "3. Manual route: launch shapez, click MODS -> OPEN MODS FOLDER, and copy the " +
                "shapezipelago@X.X.X.js file into that folder. Make sure it sits DIRECTLY " +
                "in the folder (not in a sub-folder, not still zipped). Restart shapez.",
            "4. Recommended: open the game settings, USER INTERFACE tab, and disable " +
                "\"HINTS & TUTORIALS\" — otherwise the upgrade shop stays locked until you " +
                "complete a few levels.",
            "5. To play: on the MAIN MENU, type your server address, port, slot name, and " +
                "optional password into the input box, click Connect, then create a new " +
                "game. (This launcher cannot pre-fill the connection — it is entered " +
                "in-game.)",
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
            ("shapezipelago on mod.io ↗",        ModIoPageUrl),
            ("shapezipelago (GitHub) ↗",         ModRepoUrl),
            ("shapez on Steam ↗",                SteamStoreUrl),
            ("shapez Setup Guide ↗",             SetupGuideUrl),
            ("Archipelago Official ↗",           ArchipelagoSite),
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
            btn.Click += (_, _) => { try { Process.Start(
                new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
            panel.Children.Add(btn);
        }
        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Mod releases are the AP-relevant news for this game.
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

    /// "v0.6.3" → "0.6.3" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the mod .js download URL + the .js
    /// filename. Prefers an asset named "shapezipelago*.js" (skipping the
    /// shapez.apworld); falls back to the first .js asset; falls back to the pinned
    /// 0.6.3 direct URL when the API is unreachable.
    private async Task<(string Version, string? JsUrl, string? JsName)> ResolveLatestModAsync(CancellationToken ct)
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
                string? preferredUrl = null, preferredName = null;   // shapezipelago*.js
                string? anyUrl = null, anyName = null;               // any .js
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".js")) continue;   // skip shapez.apworld etc.

                    if (anyUrl == null) { anyUrl = url; anyName = name; }
                    if (preferredUrl == null && lower.Contains("shapezipelago"))
                    {
                        preferredUrl  = url;
                        preferredName = name;
                    }
                }
                string? jsUrl  = preferredUrl  ?? anyUrl;
                string? jsName = preferredName ?? anyName;
                if (jsUrl != null)
                    return (version, jsUrl, jsName);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackJsUrl, FallbackJsName);
    }

    // ── Private helpers — mods folder resolution ──────────────────────────────

    /// The shapez mods folder to use: the override (if set) wins, else
    /// %APPDATA%\shapez.io\mods. When createIfMissing is true, the folder is
    /// created (and returned) even if it does not yet exist — used by Install and
    /// the "Open mods folder" button. When false, returns the path only if it
    /// already exists (used for status/detection). Returns null if it cannot be
    /// resolved.
    private string? ResolveModsDir(bool createIfMissing)
    {
        string? ov = LoadModsOverride();
        string candidate = !string.IsNullOrWhiteSpace(ov) ? ov! : DefaultModsDir;

        try
        {
            if (Directory.Exists(candidate)) return candidate;
            if (createIfMissing)
            {
                Directory.CreateDirectory(candidate);
                return candidate;
            }
        }
        catch { /* permission / invalid path */ }
        return null;
    }

    /// Find an installed shapezipelago mod .js in the resolved mods folder. Matches
    /// any *.js whose name contains "shapezipelago" (case-insensitive) so a renamed
    /// drop still counts. Returns the .js path or null.
    private string? FindInstalledModJs()
    {
        try
        {
            string? dir = ResolveModsDir(createIfMissing: false);
            if (dir == null) return null;

            foreach (string js in Directory.EnumerateFiles(dir, "*.js", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(js);
                if (name.IndexOf("shapezipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return js;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — Steam / shapez detection ────────────────────────────

    /// The shapez install dir to use: the override (if set and valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolveShapezDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeShapezDir(ov))
            return ov;

        try { return DetectSteamShapezDir(); }
        catch { return null; }
    }

    /// A folder "looks like" shapez if it has shapez.exe.
    private static bool LooksLikeShapezDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, "shapez.exe"));
        }
        catch { return false; }
    }

    /// Detect the Steam shapez install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_1318690.acf exists → steamapps\common\shapez.
    private static string? DetectSteamShapezDir()
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

                    // Prefer the installdir named in the manifest; fall back to the
                    // conventional "shapez" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeShapezDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeShapezDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath + HKLM 64-bit). Both are tried; duplicates are
    /// harmless.
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
    /// steamapps\libraryfolders.vdf. Tolerant text scan (the VDF is a simple quoted
    /// key/value tree; we only need the path values).
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
            // find the opening quote of the value
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

    /// Start shapez: prefer the exe in the detected/override install; if that
    /// cannot be found but Steam is present, fall back to the steam:// URL.
    /// Surfaces a clear message rather than failing opaquely.
    private void StartShapez()
    {
        string? sn  = ResolveShapezDir();
        string? exe = sn != null ? Path.Combine(sn, "shapez.exe") : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = sn!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start shapez.");

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
            "Could not find shapez.exe. Open this game's Settings and pick your shapez " +
            "install folder, or install shapez via Steam (appid 1318690).",
            "shapez.exe");
    }

    // ── Private helpers — download the mod .js ────────────────────────────────

    /// Download the mod .js into the shapez mods folder, removing any older
    /// shapezipelago@*.js first so a stale version cannot also load. This is the
    /// real one-shot install (shapez has a first-party mod loader; the .js just
    /// needs to sit directly in the mods folder).
    private async Task DownloadModJsAsync(
        string jsUrl,
        string version,
        string modsDir,
        string fileName,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(modsDir);

        // Remove older shapezipelago@*.js copies so only the new version loads.
        try
        {
            foreach (string old in Directory.EnumerateFiles(modsDir, "*.js", SearchOption.TopDirectoryOnly))
            {
                string n = Path.GetFileNameWithoutExtension(old);
                if (n.IndexOf("shapezipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    try { File.Delete(old); } catch { /* leave it; overwrite below if same name */ }
                }
            }
        }
        catch { /* enumeration failure is non-fatal */ }

        string dst = Path.Combine(modsDir, fileName);
        await DownloadFileAsync(jsUrl, dst, version, progress, ct);
    }

    /// Stream a URL to a local file with progress reporting.
    private async Task DownloadFileAsync(
        string url,
        string destPath,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempFile = destPath + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            progress.Report((10, $"Downloading shapezipelago {version}..."));
            using (var response = await _http.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();

                long total      = response.Content.Headers.ContentLength ?? -1;
                long downloaded = 0;

                Directory.CreateDirectory(Path.GetDirectoryName(tempFile)!);
                await using (var src = await response.Content.ReadAsStreamAsync(ct))
                await using (var dst = File.Create(tempFile))
                {
                    var buf = new byte[81920];
                    int bytesRead;
                    while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                        downloaded += bytesRead;
                        if (total > 0)
                        {
                            int pct = (int)(10 + 80 * downloaded / total);
                            progress.Report((pct, $"Downloading shapezipelago... {downloaded / 1000}KB"));
                        }
                    }
                    await dst.FlushAsync(ct);
                }
            }

            progress.Report((92, "Installing the mod file..."));
            // Atomic-ish replace: move the completed temp file over the target.
            if (File.Exists(destPath))
            {
                try { File.Delete(destPath); } catch { /* will overwrite via Move below */ }
            }
            File.Move(tempFile, destPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the install-dir override, the
    // mods-folder override, and an informational version stamp) in its OWN JSON
    // file so it stays a single self-contained source file and does not modify
    // Core/SettingsStore. BOM-less UTF-8, read-modify-write (same approach as
    // Subnautica/HK/Doom).

    private sealed class ShapezSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModsOverride    { get; set; }
        public string? ModVersion      { get; set; }
    }

    private ShapezSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<ShapezSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(ShapezSettings s)
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

    private string? LoadModsOverride()
    {
        string? p = LoadSettings().ModsOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }
    private void SaveModsOverride(string p) { var s = LoadSettings(); s.ModsOverride = p; SaveSettings(s); }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    private void WriteStampedVersion(string v) { var s = LoadSettings(); s.ModVersion = v; SaveSettings(s); }
}
