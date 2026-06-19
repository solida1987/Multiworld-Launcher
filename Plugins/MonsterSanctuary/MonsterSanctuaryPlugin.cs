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

// NOTE: All WPF UI types are FULLY QUALIFIED throughout this file
// (System.Windows.Controls.*, System.Windows.Media.*, etc.).
// The launcher project sets <UseWindowsForms>true</UseWindowsForms> alongside WPF,
// so bare names like Button, TextBox, Color, Brushes, MessageBox, FontWeights,
// Orientation, HorizontalAlignment collide with System.Windows.Forms (CS0104).
// Never add file-level "using X = System.Windows...;" aliases — CS1537.

namespace LauncherV2.Plugins.MonsterSanctuary;

// ═══════════════════════════════════════════════════════════════════════════════
// MonsterSanctuaryPlugin — install / launch for "Monster Sanctuary"
// (Moi Rai Games, 2020, Steam AppID 814370), played through the
// Archipelago.MonsterSanctuary mod by Gtaray (Saagael) — a BepInEx plugin that
// acts as the in-game Archipelago client so the game connects to the multiworld
// directly. This is a NATIVE "ConnectsItself" integration in the same family as
// the shipped Hollow Knight, Inscryption, Risk of Rain 2, and Subnautica plugins.
//
// ── VERIFIED FACTS (2026-06-15, checked against Gtaray/archipelago-monstersanctuary) ──
//
//   * REPOSITORY:      github.com/Gtaray/archipelago-monstersanctuary  (real + active)
//   * LATEST RELEASE:  v1.3.7-hotfix1  (verified: GitHub releases API, 2026-06-15)
//   * RELEASE ASSETS:  monster_sanctuary_mod.zip  (BepInEx + plugin DLL, self-contained)
//                      monster_sanctuary.apworld   (custom AP world generator)
//                      MonsterSanctuary.yaml       (player YAML template)
//   * AP GAME STRING:  "Monster Sanctuary"
//     Verified line-by-line in Client/AP/ApState.cs:
//       Session.TryConnectAndLogin("Monster Sanctuary", ...)
//   * STEAM APPID:     814370  (verified steamdb.info + store.steampowered.com)
//   * MOD LOADER:      BepInEx 5.x (Unity Mono, net48)
//   * PLUGIN DLL:      Archipelago.MonsterSanctuary.Client.dll
//     installed at:    <GameDir>\BepInEx\plugins\Archipelago.MonsterSanctuary\
//   * ZIP LAYOUT:      The release zip is self-contained — it includes BepInEx itself
//     plus the plugin DLL plus its networking dependencies. Extracting the zip into
//     the game root is the complete install (no r2modman step needed). The layout is:
//       BepInEx/
//         plugins/Archipelago.MonsterSanctuary/Archipelago.MonsterSanctuary.Client.dll
//         plugins/Archipelago.MonsterSanctuary/Archipelago.MultiClient.Net.dll
//       doorstop_config.ini
//       winhttp.dll
//   * CONNECTION:      Entered IN-GAME. After installing, launch once (BepInEx
//     completes its own install on first boot), relaunch, and the main menu shows
//     "Archipelago vX.X Status: Not Connected" in the top-left plus text fields for
//     hostname, slot name, and password. On New Game, fill in those fields and
//     connect. On Continue, the mod reads the saved session from its own sidecar
//     (Archipelago/ap_data_N.json in the game folder) and reconnects automatically.
//     There is NO command-line arg and NO launcher-writable config file — connection
//     info is entirely in-game and in the mod's own save data.
//   * ConnectsItself:  true — the mod owns the AP slot; the launcher must NOT hold
//     its own ApClient on the same slot while the game is running.
//   * SupportsStandalone: true — vanilla Monster Sanctuary runs fine without the mod.
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. DETECT the Steam Monster Sanctuary install via the Windows registry —
//      HKCU\Software\Valve\Steam → SteamPath, HKLM\...\WOW6432Node\Valve\Steam
//      → InstallPath — parsing steamapps\libraryfolders.vdf for every library
//      root and locating the install via appmanifest_814370.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported; it takes
//      precedence, is validated (must contain MonsterSanctuary.exe), and is
//      persisted in this plugin's own sidecar (Games/ROMs/monster_sanctuary/
//      ms_launcher.json) — Core/SettingsStore is NOT modified.
//   2. INSTALL/UPDATE = download monster_sanctuary_mod.zip from the latest GitHub
//      release and extract it straight into the game root. The zip is self-
//      contained (BepInEx + plugin + networking DLLs), so one Install/Update is
//      the complete install. Must be idempotent (safe to call when up-to-date).
//   3. LAUNCH = run MonsterSanctuary.exe from the detected/override install; fall
//      back to steam://rungameid/814370 if Steam is present but exe not found.
//      No connection prefill (entered in-game).
//
// ── NOTE ON THE COMMUNITY APWORLD ─────────────────────────────────────────────
// Monster Sanctuary is NOT in the main Archipelago repo. It ships as a CUSTOM
// apworld (.apworld file) released alongside the BepInEx mod by Gtaray. To
// generate a seed, users must install the .apworld manually (drag it into their
// local Archipelago Launcher's worlds folder, or use the custom-worlds option).
// The SetupGuideUrl in this plugin links to a community setup guide that covers
// both the .apworld and the mod install.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MonsterSanctuaryPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// GitHub mod repository (Gtaray/archipelago-monstersanctuary — verified real).
    private const string MOD_OWNER = "Gtaray";
    private const string MOD_REPO  = "archipelago-monstersanctuary";
    private static readonly string ModRepoUrl =
        $"https://github.com/{MOD_OWNER}/{MOD_REPO}";

    // GitHub API endpoints for releases.
    private static readonly string GH_RELEASES_LATEST_API =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private static readonly string GH_RELEASES_API =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // Release asset name — verified against the GitHub releases API (2026-06-15).
    private const string ModZipAssetName = "monster_sanctuary_mod.zip";

    /// Pinned fallback version + URL used when the GitHub API is unreachable.
    /// Verified live 2026-06-15: v1.3.7-hotfix1 is the latest release.
    private const string FallbackVersion = "1.3.7-hotfix1";
    private static readonly string FallbackZipUrl =
        $"https://github.com/{MOD_OWNER}/{MOD_REPO}/releases/download/" +
        $"v{FallbackVersion}/{ModZipAssetName}";

    // The exact plugin DLL filename — used as the "installed" signal.
    private const string ModDllName = "Archipelago.MonsterSanctuary.Client.dll";

    // Steam AppID 814370 — verified steamdb.info + store.steampowered.com.
    private const string SteamAppId = "814370";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    // Common Steam game folder name (what the ACF installdir field usually gives).
    private const string SteamCommonFolderName = "Monster Sanctuary";

    // Links shown in the settings panel.
    private const string SetupGuideUrl =
        "https://github.com/Gtaray/archipelago-monstersanctuary/blob/main/README.md";
    private const string ArchipelagoSite = "https://archipelago.gg";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "monster_sanctuary";
    public string DisplayName => "Monster Sanctuary";
    public string Subtitle    => "Native PC · BepInEx Archipelago mod";

    /// Exact AP game string — verified against Client/AP/ApState.cs line:
    ///   Session.TryConnectAndLogin("Monster Sanctuary", ...)
    public string ApWorldName => "Monster Sanctuary";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "monster_sanctuary.png");

    /// Forest green — matches Monster Sanctuary's lush wilderness aesthetic.
    public string ThemeAccentColor => "#2E7D32";

    public string[] GameBadges => new[] { "Steam · BepInEx mod", "Custom apworld" };

    public string Description =>
        "Monster Sanctuary, Moi Rai Games' 2020 monster-taming metroidvania, played " +
        "through the Archipelago mod by Saagael (Gtaray). Items, chests, and monster " +
        "rewards are all shuffled into the multiworld. The mod is a BepInEx plugin that " +
        "connects to the Archipelago server directly from inside the game — no separate " +
        "client needed. You bring your own copy of Monster Sanctuary (Steam), and the " +
        "launcher installs BepInEx and the mod in one step. Monster Sanctuary uses a " +
        "custom .apworld file (distributed alongside the mod); you will need to install " +
        "that apworld in your Archipelago installation to generate a seed. Connection " +
        "details (server, slot, password) are entered in the in-game overlay when " +
        "starting a new save.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = the plugin DLL is present under the detected/override game dir's
    /// BepInEx\plugins tree. We do NOT gate on our own version stamp so that a manual
    /// install (from the README's direct link) is also recognised.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Working directory for launcher-side files (downloads, sidecar). The actual
    /// mod is extracted into the Monster Sanctuary install folder, not here.
    public string GameDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "Games", "MonsterSanctuary");

    private string SettingsSidecarDir =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SettingsSidecarPath =>
        Path.Combine(SettingsSidecarDir, "ms_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The BepInEx mod manages the AP session entirely. ConnectsItself = true.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself / SupportsStandalone ───────────────────────────────────

    /// The BepInEx mod owns the AP slot — the launcher must not compete for it.
    public bool ConnectsItself => true;

    /// Vanilla Monster Sanctuary runs fine without the mod.
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Installed version: prefer the stamp we wrote during an Install; fall back
        // to "installed" whenever the DLL is found (e.g. manual README install).
        try
        {
            InstalledVersion = FindInstalledModDll() != null
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
        // 1. Require a game install to drop the mod into.
        progress.Report((2, "Locating Monster Sanctuary installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Monster Sanctuary installation. Open this game's " +
                "Settings, select your Monster Sanctuary folder (the one containing " +
                "MonsterSanctuary.exe), or install Monster Sanctuary via Steam first.");

        // 2. Resolve the latest release from GitHub (pinned fallback when offline).
        progress.Report((6, "Checking the latest Monster Sanctuary mod release on GitHub..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Monster Sanctuary AP mod download on GitHub. " +
                "Check your internet connection, or download the mod manually from " +
                ModRepoUrl + "/releases/latest");

        // 3. Download and extract the zip into the game root.
        //    The zip is SELF-CONTAINED (BepInEx + plugin DLL + AP networking DLLs) —
        //    no r2modman step required.
        await DownloadAndExtractModAsync(zipUrl, version, gameDir, progress, ct);

        // 4. Stamp the version into our sidecar for display.
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepInExOk = BepInExPresent(gameDir);
        progress.Report((100,
            $"Monster Sanctuary AP mod {version} installed into {gameDir}. " +
            (bepInExOk
                ? "BepInEx is now present. "
                : "") +
            "NEXT STEPS: (1) Launch the game once and close it — BepInEx will finalise " +
            "its own install on first boot. (2) Relaunch the game; you will see " +
            "\"Archipelago Status: Not Connected\" in the top-left. (3) Start a new " +
            "save and enter your server address, slot name, and password in the in-game " +
            "overlay. You also need the monster_sanctuary.apworld file to generate a " +
            "seed — download it from the GitHub releases (see Settings for the link)."));
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
        // HONEST: there is no command-line arg or pre-writable config file for
        // this mod. Connection info (host/slot/password) is entered in-game via
        // the mod's own UI overlay. ConnectsItself = true — do not hold a
        // launcher ApClient on the same slot while the mod is running.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartGame();
        return Task.CompletedTask;
    }

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
        // No plaintext AP password is ever written to disk by this plugin.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The BepInEx mod receives items directly from the AP server.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod manages its own connection state in-game.
    }

    // ── Existing-install validation ───────────────────────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Monster Sanctuary install folder.";

        if (LooksLikeGameDir(folder)) return null;

        // Forgive: user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeGameDir(nested)) return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Monster Sanctuary installation. Pick the folder " +
               "that contains MonsterSanctuary.exe (for Steam this is usually " +
               @"...\steamapps\common\Monster Sanctuary).";
    }

    // ── Settings panel ────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
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
            System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();
        string? modDll      = FindInstalledModDll();
        bool    bepInExOk   = gameDir != null && BepInExPresent(gameDir);

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Monster Sanctuary (Steam AppID 814370) is your own game. The " +
                "Archipelago mod by Saagael (Gtaray) is a self-contained BepInEx " +
                "plugin — the Install button downloads BepInEx + the plugin in one " +
                "zip and extracts it into your game folder. Monster Sanctuary uses a " +
                "CUSTOM apworld (.apworld file) that is not part of the main " +
                "Archipelago installation — you need to install it separately to " +
                "generate a seed (see the links below). Connection info (server, slot, " +
                "password) is entered in the in-game overlay; this launcher cannot " +
                "pre-fill it.",
            FontSize     = 11,
            Foreground   = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: game install ─────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text        = "MONSTER SANCTUARY INSTALL",
            FontSize    = 10,
            FontWeight  = System.Windows.FontWeights.SemiBold,
            Foreground  = muted,
            Margin      = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Monster Sanctuary not detected. Pick your install folder below, or " +
              "install the game via Steam first (AppID 814370).";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text         = detectMsg,
            FontSize     = 11,
            Foreground   = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 4),
        });

        // BepInEx status
        if (gameDir != null)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text       = bepInExOk
                    ? "BepInEx found in your Monster Sanctuary folder."
                    : "BepInEx not yet installed — use the Install button on the Play tab.",
                FontSize   = 11,
                Foreground = bepInExOk ? success : muted,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin     = new System.Windows.Thickness(0, 0, 0, 4),
            });
        }

        // Mod DLL status
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                ? "Archipelago mod found: " + modDll
                : "Archipelago mod not yet installed. Use the Install button on the Play tab.",
            FontSize     = 11,
            Foreground   = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new System.Windows.Controls.DockPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = overrideDir ?? gameDir ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Monster Sanctuary install folder (the one containing " +
                          "MonsterSanctuary.exe). Detected from Steam automatically; " +
                          "set it here to override (non-standard Steam library, etc.).",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content     = "Select folder...",
            Width       = 120,
            Padding     = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            string? currentDir = overrideDir ?? gameDir;
            var dlg = new OpenFolderDialog
            {
                Title = "Select your Monster Sanctuary install folder",
                InitialDirectory = Directory.Exists(currentDir ?? "")
                                   ? currentDir!
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Monster Sanctuary folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // Descend if the user picked the Steam "common" parent.
                if (!LooksLikeGameDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeGameDir(nested)) picked = nested;
                }
                SaveOverrideDir(picked);
                dirBox.Text = picked;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn,
            System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Steam installs are detected automatically (AppID 814370). Use " +
                   "this picker for a non-standard Steam library or another store.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: connection ───────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "CONNECTING (entered in the in-game overlay)",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "After installing, launch the game once and close it (BepInEx " +
                "finalises its install on first boot). Relaunch and you will see " +
                "\"Archipelago vX.X Status: Not Connected\" in the top-left. To " +
                "start a new multiworld run, select New Game and enter your server " +
                "address, slot name, and optional password in the fields shown. To " +
                "resume, select Continue — the mod reconnects automatically from your " +
                "save data. This launcher does not pre-fill the connection.",
            FontSize     = 11,
            Foreground   = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: custom apworld note ──────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "CUSTOM APWORLD (required to generate a seed)",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Monster Sanctuary is not in the main Archipelago installation. To " +
                "generate a multiworld seed that includes Monster Sanctuary, you need " +
                "to install the monster_sanctuary.apworld file from the mod's GitHub " +
                "releases into your Archipelago installation's custom_worlds folder " +
                "(or the Archipelago Launcher's Browse Files → lib\\worlds folder). " +
                "Download it from the GitHub releases link below.",
            FontSize     = 11,
            Foreground   = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: guided steps ─────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "HOW TO PLAY",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Monster Sanctuary on Steam. Use \"Select folder...\" above if it was " +
                "not detected automatically.",
            "2. Click Install on the Play tab. The launcher downloads the complete mod " +
                "zip (BepInEx + the Archipelago plugin + its networking DLLs) and extracts " +
                "it into your game folder automatically.",
            "3. Launch Monster Sanctuary once and close it. BepInEx will finalise its " +
                "own setup on first boot (you may see a console window — this is normal).",
            "4. Download monster_sanctuary.apworld from the GitHub releases (link below) " +
                "and place it in your Archipelago installation's lib\\worlds folder. Then " +
                "generate your multiworld as usual (edit MonsterSanctuary.yaml, place it " +
                "in Players, and run Generate from the Archipelago Launcher).",
            "5. Relaunch Monster Sanctuary. You will see \"Archipelago Status: Not " +
                "Connected\" in the top-left corner.",
            "6. Select New Game and enter your Archipelago server address, slot name, and " +
                "password in the in-game fields. The game connects to the server itself.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text         = step,
                FontSize     = 11,
                Foreground   = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin       = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Section: links ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "LINKS",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Archipelago.MonsterSanctuary (GitHub) ↗",           ModRepoUrl),
            ("Latest release (mod + apworld + yaml) ↗",
                ModRepoUrl + "/releases/latest"),
            ("Monster Sanctuary AP mod setup guide ↗",            SetupGuideUrl),
            ("Archipelago Official ↗",                            ArchipelagoSite),
            ("Monster Sanctuary on Steam ↗",
                $"https://store.steampowered.com/app/{SteamAppId}/Monster_Sanctuary/"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content             = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding             = new System.Windows.Thickness(0, 2, 0, 2),
                Background          = System.Windows.Media.Brushes.Transparent,
                BorderThickness     = new System.Windows.Thickness(0),
                FontSize            = 12,
                Margin              = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground          = accent,
                Cursor              = System.Windows.Input.Cursors.Hand,
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
        // Pull from the GitHub releases API — returns releases newest-first.
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_API, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) &&
                    d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t)
                                 ? NormalizeTag(t.GetString()) ?? "" : "",
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

    /// "v1.3.7-hotfix1" → "1.3.7-hotfix1". Trims a leading 'v' before a digit.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest release version + the mod zip download URL from the
    /// GitHub releases API. Falls back to the pinned v1.3.7-hotfix1 URL when the
    /// API is unreachable or returns an unexpected shape.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_API, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null &&
                root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                // Prefer the exact "monster_sanctuary_mod.zip" asset; accept any .zip.
                string? preferred = null;
                string? anyZip    = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u)
                                   ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null &&
                        lower == ModZipAssetName.ToLowerInvariant())
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

    // ── Private helpers — Steam / game detection ──────────────────────────────

    /// The game dir to use: override (if set + valid) wins, else Steam-detected.
    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeGameDir(ov)) return ov;
        try { return DetectSteamGameDir(); }
        catch { return null; }
    }

    /// A folder "looks like" the Monster Sanctuary install if it contains
    /// MonsterSanctuary.exe or the MonsterSanctuary_Data Unity data folder.
    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            // Primary: the exe (handles case differences on NTFS).
            foreach (string exe in new[] { "MonsterSanctuary.exe", "monstersanctuary.exe" })
                if (File.Exists(Path.Combine(dir, exe))) return true;
            // Fallback: Unity data folder (present even if exe name varies).
            if (Directory.Exists(Path.Combine(dir, "MonsterSanctuary_Data"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// True when BepInEx is present in a game folder.
    /// BepInEx drops a "BepInEx" sub-folder plus a winhttp.dll proxy at the game root.
    private static bool BepInExPresent(string gameDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
                return false;
            if (Directory.Exists(Path.Combine(gameDir, "BepInEx"))) return true;
            if (File.Exists(Path.Combine(gameDir, "winhttp.dll")))   return true;
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
                    string manifest  = Path.Combine(steamapps,
                        $"appmanifest_{SteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common     = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeGameDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeGameDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry.
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

        string? progX86 = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFilesX86);
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

    private static string? ReadRegistryString(
        RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(subKey);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    // ── Private helpers — mod DLL detection ──────────────────────────────────

    /// Find the plugin DLL under <GameDir>\BepInEx\plugins\ (recursive,
    /// case-insensitive). Returns the path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? gd = ResolveGameDir();
            if (gd == null) return null;

            string pluginsDir = Path.Combine(gd, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(
                pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dll).Equals(
                        ModDllName, StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { /* permission / directory removed */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        string? gd  = ResolveGameDir();
        string? exe = gd != null ? Path.Combine(gd, "MonsterSanctuary.exe") : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = gd!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException(
                "Failed to start Monster Sanctuary.");

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
            catch { /* some processes do not expose Exited — non-fatal */ }
            return;
        }

        // Fall back to Steam URI if we can find a Steam install.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl)
                    { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { /* fall through to the error */ }
        }

        throw new FileNotFoundException(
            "Could not find MonsterSanctuary.exe. Open this game's Settings and pick " +
            "your Monster Sanctuary install folder, or install it via Steam.",
            "MonsterSanctuary.exe");
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download monster_sanctuary_mod.zip and extract it into the game root.
    /// The zip is self-contained (BepInEx + plugin DLL + AP networking DLLs);
    /// extracting it into the game root is the complete install.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string gameDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"monstersanctuary-ap-{version}-{Guid.NewGuid():N}.zip");
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"monstersanctuary-ap-x-{Guid.NewGuid():N}");
        try
        {
            // Download with streaming progress.
            progress.Report((10, $"Downloading Monster Sanctuary AP mod {version}..."));
            using var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempZip))
            {
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    downloaded += n;
                    if (total > 0)
                    {
                        int pct = (int)(10 + 60.0 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading mod... {downloaded / 1024}KB / {total / 1024}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Extracting mod into Monster Sanctuary folder..."));
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, tempDir, overwriteFiles: true);

            // The release zip is expected to be flat (BepInEx/ + winhttp.dll at root).
            // If it has a single wrapping folder (some GitHub release artefacts do),
            // descend into it before copying.
            string extractRoot = tempDir;
            try
            {
                string[] subdirs = Directory.GetDirectories(tempDir);
                if (subdirs.Length == 1 &&
                    !Directory.Exists(Path.Combine(tempDir, "BepInEx")) &&
                    !File.Exists(Path.Combine(tempDir, "winhttp.dll")) &&
                    !File.Exists(Path.Combine(tempDir, "doorstop_config.ini")))
                {
                    extractRoot = subdirs[0];
                }
            }
            catch { /* if inspection fails, just use the tempDir root */ }

            progress.Report((85, "Copying mod files to game folder..."));
            CopyDirectoryContents(extractRoot, gameDir);

            progress.Report((95, "Mod extraction complete."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); }      catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// Recursively copy all files from src into dst (merging; overwriting files).
    private static void CopyDirectoryContents(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string file in Directory.EnumerateFiles(
            src, "*", SearchOption.AllDirectories))
        {
            string rel    = Path.GetRelativePath(src, file);
            string target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // ── Private helpers — settings sidecar ────────────────────────────────────
    // Self-contained JSON sidecar (same pattern as HollowKnight / Inscryption /
    // RiskOfRain2 plugins). BOM-less UTF-8, read-modify-write.

    private sealed class MsSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private MsSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<MsSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(MsSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(
                SettingsSidecarPath,
                JsonSerializer.Serialize(s,
                    new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
