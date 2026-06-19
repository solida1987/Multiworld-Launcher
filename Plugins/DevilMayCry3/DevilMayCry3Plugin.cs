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

// The launcher project sets BOTH UseWPF and UseWindowsForms=true, so several UI
// type names (Color, Brushes, Button, TextBox, HorizontalAlignment) collide between
// WPF and WinForms. The project's GlobalUsings.cs already aliases each of these to
// its WPF type globally, so this file relies on those — no local aliases (a local
// alias duplicating a global one is CS1537).

namespace LauncherV2.Plugins.DevilMayCry3;

// ═══════════════════════════════════════════════════════════════════════════════
// DevilMayCry3Plugin — install / launch for "Devil May Cry 3" played through the
// DMC3ArchipelagoClient mod by AshIndigo, targeting the DMC HD Collection on
// Steam. This is a NATIVE "ConnectsItself" DLL-injection integration.
//
// ── VERIFIED FACTS (2026-06-14, research against AshIndigo/DMC3ArchipelagoClient
//    + AshIndigo/Archipelago dmc3-world branch + full en_setup.md) ──────────────
//
//   * THE AP WORLD game string is "Devil May Cry 3" (verified against
//     worlds/dmc3/__init__.py: `game = "Devil May Cry 3"`). GameId = "dmc3".
//     This is a community APWorld (not yet shipped in Archipelago main).
//
//   * THE TARGET GAME is "Devil May Cry HD Collection" on Steam (AppID 631510,
//     verified from the save path `...\userdata\...\631510\remote\dmc3.sav` in
//     the setup guide). The specific game inside the collection played here is
//     DMC3 Special Edition — it ships as `dmc3.exe` alongside `dmc1.exe` and
//     `dmc2.exe` in the same install directory.
//
//   * THE MOD FRAMEWORK is DLL injection — NOT a standalone game-side client that
//     can be downloaded to a standalone folder. The randomizer is injected as a DLL
//     into the game process via a two-stage pipeline (verified from en_setup.md):
//       Stage 1 — DMCHDLoader: `dinput8.dll` placed next to `dmc3.exe`. This loader
//         is downloaded from https://github.com/AshIndigo/DMCHDLoader/releases and
//         acts as a chain-loader that loads DDMK / DMC3-Crimson / the randomizer.
//       Stage 2 — Randomizer: `dmc3_randomizer.dll` placed next to `dinput8.dll`.
//         This is extracted from `DMC3-Archipelago-X.Y.Z.zip` in the
//         DMC3ArchipelagoClient releases.
//       Stage 3 — `steam_appid.txt` (also in loader zip, put next to `dmc3.exe`)
//         allows launching DMC3 from the exe directly without the Steam launcher;
//         confirmed practically required by user reports.
//     The game must be downgraded using DDMK tooling first (documented in
//     serpentiem/ddmk). This launcher does NOT automate the downgrade step.
//
//   * CONNECTION FLOW (verified en_setup.md + config.rs + lib.rs): The game's DLL
//     auto-connects to a local proxy CommonClient ("DMC3 Client") running on
//     localhost:21705 (default, configurable in `dmc3_randomizer.toml`). The user
//     must START THE DMC3 CLIENT FROM THE ARCHIPELAGO LAUNCHER before launching the
//     game — the client is what connects to the AP server and relays items to the
//     DLL. The command `/dmc3` in the client verifies the relay.
//     config.rs default: { connections: { port: 21705, address: "localhost", ... } }.
//     The DLL reads its config from `dmc3_randomizer.toml` in the game directory;
//     this launcher does NOT write that file (it is the user's config for the DLL,
//     not a launcher-level prefill).
//
//   * GAME DOWNGRADE is a REQUIRED PREREQUISITE — the mod only loads correctly on
//     the DDMK-compatible downgraded version. The launcher surfaces this plainly in
//     the guided steps but does NOT automate it.
//
//   * ConnectsItself = true: the DMC3 Client (not this launcher) owns the slot
//     connection. SupportsStandalone = true: DMC HD Collection runs fine without the
//     DLL injected.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam DMC HD Collection install via the Windows registry
//      (HKCU\Software\Valve\Steam → SteamPath, HKLM WOW6432Node), parsing
//      steamapps\libraryfolders.vdf and locating
//      steamapps\common\Devil May Cry HD Collection via appmanifest_631510.acf.
//      A manual install-dir OVERRIDE is also supported and persisted in this
//      plugin's own sidecar (Games/ROMs/dmc3/dmc3_launcher.json).
//   2. INSTALL/UPDATE (two-phase, complete):
//       Phase A — DMCHDLoader: download the latest release zip from
//         AshIndigo/DMCHDLoader, extract `dinput8.dll` and `steam_appid.txt` into
//         the game directory next to `dmc3.exe`.
//       Phase B — Randomizer: download the latest `DMC3-Archipelago-X.Y.Z.zip`
//         from AshIndigo/DMC3ArchipelagoClient, extract `dmc3_randomizer.dll` into
//         the same game directory.
//     The required DDMK game downgrade is NOT automated — the guided steps and
//     Settings panel call this out explicitly and link to the DDMK README.
//   3. LAUNCH = run `dmc3.exe` from the detected/override install (with its
//      directory as the working directory, which is important for DLL search order).
//      Falls back to steam://rungameid/631510 when the exe is not found.
//
// ── DEFENSIVE / UNVERIFIED ────────────────────────────────────────────────────
//   * "Installed" = `dmc3_randomizer.dll` present next to `dmc3.exe` in the
//     detected/override install directory.
//   * Steam library / VDF / ACF parsing is defensive (hand-written tolerant scans).
//   * No plaintext AP password is written by this plugin (connection is handled
//     inside the DMC3 Client — a separate Archipelago launcher component).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DevilMayCry3Plugin : IGamePlugin
{
    // ── Constants — repos (verified 2026-06-14) ───────────────────────────────

    private const string ModOwner      = "AshIndigo";
    private const string ModRepo       = "DMC3ArchipelagoClient";
    private const string LoaderOwner   = "AshIndigo";
    private const string LoaderRepo    = "DMCHDLoader";

    private const string ModRepoUrl    = $"https://github.com/{ModOwner}/{ModRepo}";
    private const string LoaderRepoUrl = $"https://github.com/{LoaderOwner}/{LoaderRepo}";

    private const string GhModReleasesLatestUrl =
        $"https://api.github.com/repos/{ModOwner}/{ModRepo}/releases/latest";
    private const string GhModReleasesUrl =
        $"https://api.github.com/repos/{ModOwner}/{ModRepo}/releases";
    private const string GhLoaderReleasesLatestUrl =
        $"https://api.github.com/repos/{LoaderOwner}/{LoaderRepo}/releases/latest";

    private const string SetupGuideUrl  =
        "https://github.com/AshIndigo/Archipelago/blob/dmc3-world/worlds/dmc3/docs/en_setup.md";
    private const string DdmkGuideUrl   =
        "https://github.com/serpentiem/ddmk?tab=readme-ov-file#devil-may-cry-hd-collection";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Devil May Cry HD Collection appid 631510.
    private const string SteamAppId          = "631510";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// Standard Steam install sub-folder name for the HD Collection.
    private const string SteamCommonFolderName = "Devil May Cry HD Collection";

    /// The DMC3 executable (lives directly in the install root).
    private const string GameExeName = "dmc3.exe";

    /// Files injected into the game directory by this plugin.
    private const string LoaderDllName      = "dinput8.dll";
    private const string RandoDllName       = "dmc3_randomizer.dll";
    private const string SteamAppIdFileName = "steam_appid.txt";

    /// Pinned fallback release for the randomizer when GitHub API is unreachable.
    private const string FallbackModVersion = "0.5.2";
    private const string FallbackModZipName = "DMC3-Archipelago-0.5.2.zip";
    private static readonly string FallbackModZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackModVersion}/{FallbackModZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "dmc3";
    public string DisplayName => "Devil May Cry 3";
    public string Subtitle    => "Native PC · DLL mod (HD Collection)";

    /// EXACT AP game string — verified against worlds/dmc3/__init__.py:
    /// `game = "Devil May Cry 3"` on AshIndigo's dmc3-world branch.
    public string ApWorldName => "Devil May Cry 3";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "devil_may_cry_3.png");

    public string ThemeAccentColor => "#B71C1C";   // deep devil-red
    public string[] GameBadges     => new[] { "Steam · DLL mod" };

    public string Description =>
        "Devil May Cry 3 Special Edition, played through the DMC3ArchipelagoClient " +
        "mod by AshIndigo — a DLL injected into the game to shuffle weapons, guns, " +
        "skills, styles, abilities, orbs, and mission completion across the " +
        "multiworld. You bring your own copy of Devil May Cry HD Collection (Steam " +
        "appid 631510); the randomizer DLL is added on top. The game must first be " +
        "downgraded to the DDMK-compatible version (see the setup guide and Settings). " +
        "The launcher installs the loader DLL (DMCHDLoader) and randomizer DLL for " +
        "you. Connection is managed by the DMC3 Client in the Archipelago Launcher, " +
        "which relays items between the AP server and the DLL running inside the game " +
        "on localhost:21705.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = dmc3_randomizer.dll is present in the detected game directory.
    public bool IsInstalled => FindInstalledRandoDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory
    {
        get => ResolveGameDir() ?? Path.Combine(AppContext.BaseDirectory, "Games", "DevilMayCry3");
        set { if (!string.IsNullOrWhiteSpace(value)) SaveOverrideDir(value); }
    }

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "dmc3_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The DMC3 Client + DLL relay items and check locations to the AP server
    // directly — the launcher forwards nothing. These exist for interface
    // compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself / SupportsStandalone ───────────────────────────────────

    /// The DMC3 Client (Archipelago Launcher) owns the slot connection.
    public bool ConnectsItself => true;

    /// DMC HD Collection runs fine without the randomizer DLLs present.
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = FindInstalledRandoDll() != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch { InstalledVersion = null; }

        try
        {
            var (version, _) = await ResolveLatestModAsync(ct);
            AvailableVersion = version;
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. Locate the game directory.
        progress.Report((2, "Locating your Devil May Cry HD Collection installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find Devil May Cry HD Collection. Open this game's Settings " +
                "and pick your install folder (the one containing dmc3.exe), or install " +
                "the collection via Steam first (appid 631510).");

        // 1. Resolve and download DMCHDLoader (provides dinput8.dll + steam_appid.txt).
        progress.Report((5, "Checking for the latest DMCHDLoader release..."));
        var (loaderVersion, loaderZipUrl) = await ResolveLatestLoaderAsync(ct);

        if (loaderZipUrl != null)
        {
            progress.Report((8, $"Downloading DMCHDLoader {loaderVersion}..."));
            await DownloadAndExtractSelectiveAsync(
                loaderZipUrl, loaderVersion, gameDir,
                new[] { LoaderDllName, SteamAppIdFileName },
                "DMCHDLoader", progress, startPct: 8, endPct: 35, ct);
        }
        else
        {
            progress.Report((35,
                "Could not find DMCHDLoader on GitHub (network issue?). If dinput8.dll " +
                "is already present from a prior install, install continues. " +
                $"Download it manually from {LoaderRepoUrl}/releases if needed."));
        }

        // 2. Resolve and download randomizer (provides dmc3_randomizer.dll).
        progress.Report((36, "Checking for the latest randomizer release..."));
        var (modVersion, modZipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = modVersion;

        if (modZipUrl == null)
            throw new InvalidOperationException(
                "Could not find the randomizer download on GitHub. Check your internet " +
                $"connection, or download it manually from {ModRepoUrl}/releases — " +
                "extract dmc3_randomizer.dll into your DMC HD Collection folder.");

        progress.Report((38, $"Downloading DMC3 Randomizer {modVersion}..."));
        await DownloadAndExtractSelectiveAsync(
            modZipUrl, modVersion, gameDir,
            new[] { RandoDllName },
            "randomizer", progress, startPct: 38, endPct: 80, ct);

        // 3. Stamp version.
        WriteStampedVersion(modVersion);
        InstalledVersion = modVersion;

        // Verify dinput8.dll landed (required for the DLL chain to load).
        bool loaderPresent = File.Exists(Path.Combine(gameDir, LoaderDllName));
        bool randoPresent  = File.Exists(Path.Combine(gameDir, RandoDllName));

        string summary = randoPresent
            ? $"Randomizer DLL ({RandoDllName}) installed successfully in {gameDir}."
            : $"WARNING: {RandoDllName} was not found after install — check the " +
              $"zip contents at {ModRepoUrl}/releases.";
        if (!loaderPresent)
            summary += $" WARNING: {LoaderDllName} not found — download from " +
                       $"{LoaderRepoUrl}/releases.";

        progress.Report((100,
            summary + " IMPORTANT: Before playing you must downgrade DMC HD Collection " +
            "to the DDMK-compatible version (see Settings for the guide link). Then start " +
            "the DMC3 Client from the Archipelago Launcher and connect it to your server " +
            "BEFORE launching the game. The game DLL auto-connects to the client on " +
            "localhost:21705. Use /dmc3 in the client to verify the relay."));
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
        // HONEST: the AP connection for DMC3 is established by the separate
        // "DMC3 Client" run from the Archipelago Launcher before the game starts.
        // The game's DLL auto-connects to that client on localhost:21705. There is
        // no command-line arg or config file this launcher can pre-write to pass
        // server/slot/password to the game itself (verified — see header).
        //
        // ConnectsItself = true: the DMC3 Client owns the slot, so the launcher
        // must NOT hold its own ApClient on this slot.
        _ = session; // intentionally unused — no prefill mechanism
        StartDmc3();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartDmc3();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

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
            Text = "Devil May Cry 3 is your own game (Steam appid 631510 — Devil May " +
                   "Cry HD Collection). The Archipelago randomizer is a DLL injected " +
                   "into dmc3.exe. The launcher can install the loader (DMCHDLoader) " +
                   "and randomizer DLLs for you. You must first downgrade the game to " +
                   "the DDMK-compatible version (a manual one-time step, see guide below). " +
                   "Connection is managed by the DMC3 Client in the Archipelago Launcher " +
                   "— start it BEFORE launching the game. These external steps are not " +
                   "verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: install detection / override ─────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "DEVIL MAY CRY HD COLLECTION INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Devil May Cry HD Collection not detected. Pick your install folder " +
              "below, or install via Steam first (appid 631510).";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // DLL status lines.
        bool loaderPresent = gameDir != null &&
                             File.Exists(Path.Combine(gameDir, LoaderDllName));
        bool randoPresent  = FindInstalledRandoDll() != null;
        panel.Children.Add(new TextBlock
        {
            Text = loaderPresent
                ? $"Loader DLL found: {LoaderDllName} in {gameDir}"
                : $"{LoaderDllName} not found in game directory (use Install on the " +
                  "Play tab, or download from DMCHDLoader releases).",
            FontSize = 11, Foreground = loaderPresent ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text = randoPresent
                ? $"Randomizer DLL found: {RandoDllName} in {gameDir}"
                : $"{RandoDllName} not found in game directory (use Install on the " +
                  "Play tab, or download from DMC3ArchipelagoClient releases).",
            FontSize = 11, Foreground = randoPresent ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your DMC HD Collection install folder (the one containing " +
                          "dmc3.exe). Detected from Steam automatically; set it here " +
                          "for a non-standard Steam library.",
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
                Title = "Select your DMC HD Collection folder (contains dmc3.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                                   ? (overrideDir ?? gameDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() != true) return;
            string picked = dlg.FolderName;
            string? err   = ValidateGameDir(picked);
            if (err != null)
            {
                System.Windows.MessageBox.Show(err, "Not a valid DMC HD Collection folder",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            // Descend into the conventional sub-folder if the user picked its parent.
            if (!LooksLikeGameDir(picked))
            {
                string nested = Path.Combine(picked, SteamCommonFolderName);
                if (LooksLikeGameDir(nested)) picked = nested;
            }
            SaveOverrideDir(picked);
            dirBox.Text = picked;
        };
        DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new TextBlock
        {
            Text = "Steam installs are detected automatically (appid 631510). Use this " +
                   "picker for a non-standard Steam library.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ───────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (via DMC3 Client in Archipelago Launcher)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Start the DMC3 Client from the Archipelago Launcher and connect it " +
                   "to your server (host:port, slot, password) BEFORE launching the game. " +
                   "Once connected, launch DMC3 — the randomizer DLL inside the game auto-" +
                   "connects to the client on localhost:21705 and relays items. Use the " +
                   "/dmc3 command in the client to verify the relay. The launcher does not " +
                   "pre-fill the connection.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Devil May Cry HD Collection (Steam appid 631510). Install it and " +
                "use the folder picker above if it was not auto-detected.",
            "2. BACK UP your DMC3 save file: " +
                @"C:\Program Files (x86)\Steam\userdata\<id>\631510\remote\dmc3.sav — " +
                "copy it somewhere safe before downgrading.",
            "3. Downgrade the game to the DDMK-compatible version. Follow the DDMK " +
                "guide (link below). If you already use DDMK 2.7.3 or DMC3-Crimson, " +
                "your game is most likely already properly downgraded.",
            "4. Install the mod: use the Install button on the Play tab. The launcher " +
                "downloads the DMCHDLoader (dinput8.dll + steam_appid.txt) and the " +
                "randomizer (dmc3_randomizer.dll) and places them next to dmc3.exe. " +
                "Or install manually from the repos linked below.",
            "5. To play: start the DMC3 Client from the Archipelago Launcher and " +
                "connect it to your server (host:port, slot, password). THEN launch " +
                "the game from here. The game DLL auto-connects to the client.",
            "6. Verify: use /dmc3 in the client console to confirm the relay is active. " +
                "Then use the New Game button on the DMC3 main menu to begin your run.",
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
            Text = "LINKS", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("DMC3ArchipelagoClient (GitHub) ↗", ModRepoUrl),
            ("DMCHDLoader (GitHub) ↗",            LoaderRepoUrl),
            ("DMC3 Setup Guide ↗",               SetupGuideUrl),
            ("DDMK Downgrade Guide ↗",            DdmkGuideUrl),
            ("Archipelago Official ↗",            ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
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
            string json = await _http.GetStringAsync(GhModReleasesUrl, ct);
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

    // ── Private — launch ──────────────────────────────────────────────────────

    private void StartDmc3()
    {
        string? gameDir = ResolveGameDir();
        string? exe     = gameDir != null ? Path.Combine(gameDir, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = gameDir!,   // DLLs are in this dir; WD matters for search order
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Devil May Cry 3.");

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

        // Fall back to Steam when the exe is not found.
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
            "Could not find dmc3.exe. Open this game's Settings and pick your Devil " +
            "May Cry HD Collection install folder, or install it via Steam (appid 631510).",
            GameExeName);
    }

    // ── Private — release resolution ──────────────────────────────────────────

    /// Resolve the latest mod release: returns (version, zip-url-for-randomizer-zip).
    /// Falls back to pinned v0.5.2 when the API is unreachable.
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
                string? preferred = null;  // DMC3-Archipelago-*.zip
                string? anyZip    = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;
                    anyZip ??= url;
                    if (preferred == null && lower.StartsWith("dmc3-archipelago"))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null) return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return (FallbackModVersion, FallbackModZipUrl);
    }

    /// Resolve the latest DMCHDLoader release: returns (version, zip-url).
    /// Returns (null, null) when the API is unreachable (caller handles gracefully).
    private async Task<(string? Version, string? ZipUrl)> ResolveLatestLoaderAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GhLoaderReleasesLatestUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? anyZip = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        anyZip = url;
                        break;
                    }
                }
                if (anyZip != null) return (version, anyZip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return (null, null);
    }

    // ── Private — download / extract selective files ───────────────────────────

    /// Download zipUrl, extract only the files named in `targets` (case-insensitive
    /// basename match) directly into destDir. Progress ticks from startPct to endPct.
    private async Task DownloadAndExtractSelectiveAsync(
        string zipUrl,
        string? version,
        string destDir,
        string[] targets,
        string label,
        IProgress<(int Pct, string Msg)> progress,
        int startPct, int endPct,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"dmc3-{label}-{version ?? "latest"}-{Guid.NewGuid():N}.zip");
        try
        {
            using var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;
            int  dlEnd      = startPct + (endPct - startPct) * 2 / 3;

            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempZip))
            {
                var buf = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    downloaded += read;
                    if (total > 0)
                    {
                        int pct = (int)(startPct + (dlEnd - startPct) * (double)downloaded / total);
                        progress.Report((pct, $"Downloading {label} {version}... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((dlEnd, $"Extracting {label} files into game directory..."));
            Directory.CreateDirectory(destDir);

            var targetSet = new HashSet<string>(
                targets.Select(t => t.ToLowerInvariant()), StringComparer.OrdinalIgnoreCase);

            using var archive = System.IO.Compression.ZipFile.OpenRead(tempZip);
            foreach (var entry in archive.Entries)
            {
                string entryFileName = Path.GetFileName(entry.FullName).ToLowerInvariant();
                if (!targetSet.Contains(entryFileName)) continue;

                // Find the case-correct original target name.
                string destFile = Path.Combine(destDir,
                    targets.First(t => t.Equals(Path.GetFileName(entry.FullName),
                                               StringComparison.OrdinalIgnoreCase)));
                entry.ExtractToFile(destFile, overwrite: true);
            }

            progress.Report((endPct, $"{label} files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private — install detection ───────────────────────────────────────────

    /// Path to dmc3_randomizer.dll in the game directory, or null when not found.
    private string? FindInstalledRandoDll()
    {
        try
        {
            string? gameDir = ResolveGameDir();
            if (gameDir == null) return null;
            string dll = Path.Combine(gameDir, RandoDllName);
            return File.Exists(dll) ? dll : null;
        }
        catch { return null; }
    }

    // ── Private — Steam / game detection ─────────────────────────────────────

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
            return File.Exists(Path.Combine(dir, GameExeName));
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
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeGameDir(conventional)) return conventional;
                }
                catch { }
            }
        }
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
            if (norm.Length > 0 && seen.Add(norm))
                yield return norm;
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
            string raw = text.Substring(open + 1, close - open - 1);
            yield return raw.Replace("\\\\", "\\");
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

    // ── Private — misc helpers ────────────────────────────────────────────────

    /// "v0.5.2" → "0.5.2"; pass-through otherwise.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..] : tag;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    /// Validate that a user-picked folder looks like the DMC HD Collection. Returns
    /// null when acceptable, or a human-readable error string.
    public string? ValidateGameDir(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your DMC HD Collection install folder.";
        if (LooksLikeGameDir(folder)) return null;
        string nested = Path.Combine(folder, SteamCommonFolderName);
        try { if (LooksLikeGameDir(nested)) return null; } catch { }
        return "That does not look like a Devil May Cry HD Collection installation. " +
               "Pick the folder that contains dmc3.exe (for Steam this is usually " +
               @"...\steamapps\common\Devil May Cry HD Collection).";
    }

    // ── Private — settings sidecar ────────────────────────────────────────────

    private sealed class Dmc3Settings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private Dmc3Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Dmc3Settings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(Dmc3Settings s)
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

    private void SaveOverrideDir(string p)
    {
        var s = LoadSettings();
        s.InstallOverride = p;
        SaveSettings(s);
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings();
        s.ModVersion = v;
        SaveSettings(s);
    }
}
