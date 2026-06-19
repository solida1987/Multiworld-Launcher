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

namespace LauncherV2.Plugins.EnterTheGungeon;

// ═══════════════════════════════════════════════════════════════════════════════
// EnterTheGungeonPlugin — install / launch for "Enter the Gungeon" (Dodge Roll /
// Devolver Digital, 2016), played through the ArchipelaGun BepInEx / Mod the
// Gungeon (MTG) plugin (GitHub: MaoBoulve/GungeonArchipelagoPlugin, Thunderstore:
// MaoBoulve/ArchipelaGun_Multiworld_Randomizer). This is a NATIVE "ConnectsItself"
// integration — the game mod owns the AP slot connection.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned Enter
// the Gungeon (Steam appid 311690), and Archipelago is delivered via a BepInEx /
// Mod the Gungeon (MTG) plugin installed on top.
//
//   * THE AP WORLD game string is "Enter The Gungeon" (verified against
//     worlds/enter_the_gungeon/__init__.py: `game: str = "Enter The Gungeon"`).
//     APWorld release: MaoBoulve/ArchipelaGunAPWorld v0.1.1.
//     GameId = "enter_the_gungeon".
//
//   * THE MOD is the Thunderstore package
//     MaoBoulve/ArchipelaGun_Multiworld_Randomizer, latest v0.1.7. Its GitHub
//     source is MaoBoulve/GungeonArchipelagoPlugin. The mod is a BepInEx 5.4.21
//     plugin and requires:
//         BepInEx 5.4.21 (the mod loader)
//         Mod the Gungeon API (MtG_API) 1.8.4  (MTG API — backwards-compat layer)
//         Alexandria 0.4.18                     (shared ETG modding utilities)
//     These three dependencies are NOT bundled in the ArchipelaGun zip. The
//     RECOMMENDED route is the r2modman / Thunderstore Mod Manager for Enter the
//     Gungeon, which resolves every dependency in one step.
//
//   * CONNECTION is made IN-GAME (verified). On run start the "Archipelagun" weapon
//     spawns in the player's inventory. Firing it (or using the in-game ETG console)
//     opens the mod menu. Commands:
//         connect <ip> <port> <slot name>       — join a room
//         fullconnect <ip> <port>               — for slot names with spaces, or
//                                                 password-protected rooms (the
//                                                 mod then prompts for name/pass)
//         retrieve                              — fetch items once per run
//         reconnect                             — re-establish a previous connection
//         set <option>                          — configure Name or Password
//     Alternatively, use `archipel console <cmd>` in the ETG Gungeon console.
//     There is NO command-line argument or config file this launcher can pre-write
//     to seed the AP connection. The settings panel surfaces the session server /
//     slot so the user can type them in-game.
//
//   * GAME STEAM DETECTION: ETG writes its install location to the Steam library
//     via appmanifest_311690.acf (Steam common folder name: "Enter the Gungeon").
//     We detect it the same way the RoR2 plugin detects RoR2: registry Steam roots
//     → libraryfolders.vdf → appmanifest_311690.acf → common\Enter the Gungeon.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Enter the Gungeon install via the Windows registry and
//      Steam library VDF (appmanifest_311690.acf). A manual install-dir OVERRIDE
//      (settings folder picker) is also supported and takes precedence; it is
//      validated (must contain "EtG.exe") and persisted in this plugin's OWN
//      sidecar (Games/ROMs/enter_the_gungeon/enter_the_gungeon_launcher.json).
//   2. INSTALL/UPDATE (best effort) = download the mod's Thunderstore zip from the
//      MaoBoulve/ArchipelaGun_Multiworld_Randomizer package and extract the plugin
//      into <ETG>\BepInEx\plugins\ArchipelaGun\. HONEST SCOPE: this does NOT
//      include BepInEx, MTG API or Alexandria; the recommended route (and what the
//      settings panel foregrounds) is r2modman or the Thunderstore Mod Manager.
//   3. LAUNCH = run EtG.exe from the detected/override install; if the exe cannot
//      be found but Steam is present, fall back to steam://rungameid/311690.
//      ConnectsItself = true (the mod owns the slot). SupportsStandalone = true
//      (ETG runs perfectly without AP).
//
// ── BUILD NOTE ────────────────────────────────────────────────────────────────
// This project sets UseWindowsForms=true alongside UseWPF=true, so WPF UI types
// that also exist in WinForms (Color, Button, Brushes, MessageBox, FontWeights,
// Orientation, …) are fully qualified below to prevent CS0104 ambiguity. No
// file-level `using X = System.Windows…;` aliases (would cause CS1537 with
// GlobalUsings). OpenFolderDialog is a WinForms type that does NOT conflict.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class EnterTheGungeonPlugin : IGamePlugin
{
    // ── Constants — the AP ETG mod (Thunderstore, verified 2026-06-14) ─────────
    private const string TS_NAMESPACE = "MaoBoulve";
    private const string TS_NAME      = "ArchipelaGun_Multiworld_Randomizer";

    /// Thunderstore package landing page (used for links + the manual route).
    private const string ModPackageUrl =
        $"https://thunderstore.io/package/{TS_NAMESPACE}/{TS_NAME}/";

    /// Thunderstore experimental package API — version history, newest first.
    private const string TS_PACKAGE_API_URL =
        $"https://thunderstore.io/api/experimental/package/{TS_NAMESPACE}/{TS_NAME}/";

    // GitHub release page for the mod source (for reference links).
    private const string GithubPluginUrl =
        "https://github.com/MaoBoulve/GungeonArchipelagoPlugin";

    // APWorld GitHub releases page.
    private const string ApWorldReleasesUrl =
        "https://github.com/MaoBoulve/ArchipelaGunAPWorld/releases";

    // r2modman — the RECOMMENDED installer.
    private const string R2ModManSite        = "https://thunderstore.io/package/ebkr/r2modman/";
    private const string ThunderstoreEtgSite = "https://thunderstore.io/c/enter-the-gungeon/";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Enter%20The%20Gungeon/setup/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Enter the Gungeon appid 311690 (Dodge Roll / Devolver Digital).
    private const string EtgSteamAppId    = "311690";
    private static readonly string SteamRunUrl = $"steam://rungameid/{EtgSteamAppId}";

    /// The standard Steam install sub-folder name (inside steamapps\common\).
    private const string SteamCommonFolderName = "Enter the Gungeon";

    /// The main game executable name.
    private const string GameExeName = "EtG.exe";

    /// Pinned fallback for the mod when the Thunderstore API is unreachable.
    /// v0.1.7 verified live 2026-06-14 from MaoBoulve/GungeonArchipelagoPlugin.
    private const string FallbackVersion = "0.1.7";
    private static readonly string FallbackZipUrl =
        $"https://thunderstore.io/package/download/{TS_NAMESPACE}/{TS_NAME}/{FallbackVersion}/";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "enter_the_gungeon";
    public string DisplayName => "Enter the Gungeon";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/enter_the_gungeon/__init__.py
    /// (`game: str = "Enter The Gungeon"`). APWorld: MaoBoulve/ArchipelaGunAPWorld v0.1.1.
    public string ApWorldName => "Enter The Gungeon";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "enter_the_gungeon.png");

    public string ThemeAccentColor => "#B84033";   // Gungeon chest red

    public string[] GameBadges => new[] { "Steam · needs mod" };

    public string Description =>
        "Enter the Gungeon, the bullet-hell dungeon crawler by Dodge Roll, played " +
        "through the ArchipelaGun mod — a BepInEx / Mod the Gungeon (MTG) plugin " +
        "that connects the game to Archipelago. Chest pickups, boss kills and run " +
        "milestones become location checks shuffled across the multiworld. You bring " +
        "your own copy of Enter the Gungeon (owned on Steam); the ArchipelaGun mod " +
        "is added via BepInEx 5 and the Mod the Gungeon API. The recommended way to " +
        "install everything is the r2modman mod manager, which resolves BepInEx, MTG " +
        "API and Alexandria automatically. You connect to your Archipelago server " +
        "in-game by firing the Archipelagun weapon that spawns on run start.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the ArchipelaGun plugin DLL is present in a detected /
    /// override ETG install's BepInEx\plugins tree. We do NOT gate on our own stamp
    /// — the user may have used r2modman (the recommended route), which we honor.
    public bool IsInstalled => FindInstalledModPlugin() != null;

    public bool IsRunning { get; private set; }

    // ── IGamePlugin — Connect-mode flags ──────────────────────────────────────
    /// The ArchipelaGun mod owns the AP slot (no launcher-side ApClient needed).
    public bool ConnectsItself => true;

    /// ETG runs fine without AP.
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and bookkeeping. The actual mod is
    /// extracted INTO the ETG install's BepInEx\plugins folder, not here.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "EnterTheGungeon");

    /// Per the brief: sidecar lives under Games/ROMs/enter_the_gungeon/.
    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "enter_the_gungeon_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────
    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The ArchipelaGun mod reports checks and goal to the AP server directly;
    // the launcher relays nothing (ConnectsItself = true).
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
            InstalledVersion = FindInstalledModPlugin() != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        try
        {
            var (version, _) = await ResolveLatestModAsync(ct);
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
        // 0. Need an ETG install to drop the mod into.
        progress.Report((2, "Locating your Enter the Gungeon installation..."));
        string? gameDir = ResolveEtgDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find an Enter the Gungeon installation. Open this game's " +
                "Settings and pick your Enter the Gungeon folder (the one containing " +
                "\"EtG.exe\"), or install Enter the Gungeon via Steam first. " +
                "The Archipelago mod is added on top of your own copy of the game.");

        string pluginsDir = Path.Combine(gameDir, "BepInEx", "plugins");
        string apModDir   = Path.Combine(pluginsDir, "ArchipelaGun");

        // 1. Resolve the latest mod release (pinned fallback when offline).
        progress.Report((6, "Checking the latest ArchipelaGun mod release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the ArchipelaGun mod download on Thunderstore. " +
                "Check your internet connection, or install the mod with r2modman " +
                "(recommended) — see Settings for the guided steps. " +
                "The mod package is at: " + ModPackageUrl);

        // 2. Download + extract the mod zip into <ETG>\BepInEx\plugins\ArchipelaGun\.
        //    HONEST: stages the ArchipelaGun plugin only — BepInEx, MTG API and
        //    Alexandria are NOT in this zip and must be provided by r2modman.
        await DownloadAndExtractModAsync(zipUrl, version, apModDir, progress, ct);

        // 3. Stamp version (informational — IsInstalled is judged by plugin presence).
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepInExPresent = Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));
        progress.Report((100,
            $"Staged the ArchipelaGun mod {version} into your Enter the Gungeon " +
            "BepInEx\\plugins folder. " +
            (bepInExPresent
                ? "BepInEx looks present. "
                : "IMPORTANT: this download does NOT include BepInEx, Mod the Gungeon " +
                  "API or Alexandria — all three are required. The recommended way to " +
                  "install everything in one step is the r2modman mod manager (select " +
                  "Enter the Gungeon as the game, then install \"ArchipelaGun\") — see " +
                  "Settings for guided steps and links. ") +
            "To play: launch the game, start a run, and fire the Archipelagun weapon " +
            "that spawns on run start to connect: connect <ip> <port> <slot name>."));
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
        // HONEST: The AP server connection for Enter the Gungeon is entered IN-GAME
        // by firing the Archipelagun weapon that spawns on run start. The connection
        // command is:
        //     connect <ip> <port> <slot name>
        // (or fullconnect <ip> <port> for names with spaces / password rooms).
        // There is no command-line argument or config file this launcher can
        // pre-write to seed the connection. Launching just starts ETG; the user
        // connects in-game. The settings panel surfaces server / slot to copy.
        //
        // ConnectsItself = true: do NOT hold a launcher-side ApClient on this slot.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartEnterTheGungeon();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartEnterTheGungeon();
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
    {
        // The ArchipelaGun mod receives items from the AP server directly.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        string? gameDir     = ResolveEtgDir();
        string? overrideDir = LoadOverrideDir();
        string? modPlugin   = FindInstalledModPlugin();
        bool    bepInExOk   = gameDir != null && Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Enter the Gungeon is your own game (Steam) with the ArchipelaGun " +
                   "mod added on top via BepInEx 5 and Mod the Gungeon API. The mod " +
                   "requires BepInEx 5.4.21, Mod the Gungeon API 1.8.4 and Alexandria " +
                   "0.4.18, which it does not bundle. The recommended way to install " +
                   "everything in one step is the r2modman mod manager for Enter the " +
                   "Gungeon (see the guided steps below). You connect to your " +
                   "Archipelago server in-game by firing the Archipelagun weapon that " +
                   "spawns on run start. These external steps are not verified by this " +
                   "launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "ENTER THE GUNGEON INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Enter the Gungeon not detected. Pick your install folder below, or " +
              "install Enter the Gungeon via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // BepInEx + mod status lines
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = bepInExOk
                    ? "BepInEx found (BepInEx\\core present)."
                    : "BepInEx not found yet — install it via r2modman (recommended).",
            FontSize = 11, Foreground = bepInExOk ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modPlugin != null
                    ? "ArchipelaGun mod found: " + modPlugin
                    : "ArchipelaGun mod not found in BepInEx\\plugins yet (use Install " +
                      "on the Play tab, or install it via r2modman — recommended).",
            FontSize = 11, Foreground = modPlugin != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Enter the Gungeon install folder (the one containing " +
                          "\"EtG.exe\"). Detected from Steam automatically; set it here " +
                          "to override (non-standard Steam library, etc.).",
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
                Title            = "Select your Enter the Gungeon install folder",
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
                    System.Windows.MessageBox.Show(bad, "Not an Enter the Gungeon folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend.
                if (!LooksLikeEtgDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeEtgDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 311690). Use this " +
                   "picker for a non-standard Steam library or a manual install.",
            FontSize = 11, Foreground = muted, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game via the Archipelagun weapon)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "On run start the Archipelagun weapon spawns in your inventory. Fire it " +
                   "to open the mod menu, then type: connect <ip> <port> <slot name>. " +
                   "For slot names with spaces or a password-protected server, use: " +
                   "fullconnect <ip> <port> (then set Name and Password with the set " +
                   "command). You can also use the Gungeon console: " +
                   "archipel console connect <ip> <port> <slot name>.",
            FontSize = 11, Foreground = fg, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP (recommended: r2modman for Enter the Gungeon)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Enter the Gungeon (Steam). Install it if you have not. Use " +
                "\"Select folder...\" above if it was not detected automatically.",
            "2. Install the r2modman mod manager (see the link below), then select " +
                "Enter the Gungeon as the game.",
            "3. In r2modman, search for and install \"ArchipelaGun\" by MaoBoulve. " +
                "r2modman will automatically install the required dependencies: " +
                "BepInEx 5.4.21, Mod the Gungeon API 1.8.4 and Alexandria 0.4.18. " +
                "Then click \"Start modded\".",
            "4. Also install the Enter the Gungeon APWorld (v0.1.1) on your Archipelago " +
                "server — download from the APWorld releases link below.",
            "5. Alternative (advanced): the Install button on the Play tab stages the " +
                "ArchipelaGun mod files into your BepInEx\\plugins\\ArchipelaGun folder, " +
                "but it does NOT include BepInEx, Mod the Gungeon API or Alexandria — " +
                "you would still need those from r2modman.",
            "6. To play: start a run. The Archipelagun weapon spawns automatically. " +
                "Fire it and type: connect <ip> <port> <slot name>. The launcher " +
                "cannot pre-fill the connection — it is entered in-game.",
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
            ("r2modman (mod manager) ↗",              R2ModManSite),
            ("Thunderstore — Enter the Gungeon ↗",    ThunderstoreEtgSite),
            ("ArchipelaGun mod (Thunderstore) ↗",     ModPackageUrl),
            ("ArchipelaGun source (GitHub) ↗",        GithubPluginUrl),
            ("ArchipelaGun APWorld releases ↗",       ApWorldReleasesUrl),
            ("Enter the Gungeon AP Setup Guide ↗",    SetupGuideUrl),
            ("Archipelago Official ↗",                ArchipelagoSite),
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
        // Use the Thunderstore package version history as the AP-relevant news feed.
        try
        {
            string json = await _http.GetStringAsync(TS_PACKAGE_API_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("versions", out var versions) ||
                versions.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in versions.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("date_created", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                string ver = el.TryGetProperty("version_number", out var v) ? v.GetString() ?? "" : "";

                items.Add(new NewsItem(
                    Title:   "ArchipelaGun " + ver,
                    Body:    el.TryGetProperty("description", out var b) ? b.GetString() ?? "" : "",
                    Version: ver,
                    Date:    date,
                    Url:     el.TryGetProperty("download_url", out var u) ? u.GetString() : ModPackageUrl
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// Resolve the latest mod release from the Thunderstore experimental package
    /// API. Falls back to the pinned v0.1.7 download URL when the API is
    /// unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(TS_PACKAGE_API_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Preferred shape: { latest: { version_number, download_url }, ... }.
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("latest", out var latest) &&
                latest.ValueKind == JsonValueKind.Object)
            {
                string? ver = latest.TryGetProperty("version_number", out var lv) ? lv.GetString() : null;
                string? url = latest.TryGetProperty("download_url", out var lu) ? lu.GetString() : null;
                if (!string.IsNullOrWhiteSpace(ver) && !string.IsNullOrWhiteSpace(url))
                    return (ver!, url);
            }

            // Fallback shape: first entry of the versions array (newest first).
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("versions", out var versions) &&
                versions.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in versions.EnumerateArray())
                {
                    string? ver = el.TryGetProperty("version_number", out var ev) ? ev.GetString() : null;
                    string? url = el.TryGetProperty("download_url", out var eu) ? eu.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(ver) && !string.IsNullOrWhiteSpace(url))
                        return (ver!, url);
                    break; // only the first (latest) entry needed
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / shape changed → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — Steam / ETG detection ───────────────────────────────

    /// The ETG install dir to use: the override (if set and valid) wins, else the
    /// Steam-detected install. Null when nothing is found.
    private string? ResolveEtgDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeEtgDir(ov))
            return ov;

        try { return DetectSteamEtgDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Enter the Gungeon if it contains "EtG.exe".
    private static bool LooksLikeEtgDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

    /// Detect the Steam Enter the Gungeon install: registry Steam roots →
    /// libraryfolders.vdf → appmanifest_311690.acf → common\Enter the Gungeon.
    private static string? DetectSteamEtgDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{EtgSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common    = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeEtgDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeEtgDir(conventional)) return conventional;
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

        string? hklm2 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm2)) yield return NormalizeSteamPath(hklm2);

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    /// All Steam library roots from the Steam root + libraryfolders.vdf.
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

    // ── Private helpers — installed-mod detection ─────────────────────────────

    /// Find the ArchipelaGun plugin under the detected/override install's
    /// BepInEx\plugins tree (recursive, case-insensitive). The mod places its
    /// DLL as ArchiGungeon.dll (or similar) inside a folder. We accept either a
    /// *.dll whose name mentions "archigun" or "archipelagun" or "archipelago",
    /// or a plugins sub-folder whose name mentions any of those that contains a
    /// DLL (the r2modman layout). Returns the matched path or null.
    private string? FindInstalledModPlugin()
    {
        try
        {
            string? game = ResolveEtgDir();
            if (game == null) return null;
            string pluginsDir = Path.Combine(game, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            // 1. A DLL named like ArchipelaGun / ArchiGungeon anywhere under plugins.
            foreach (string dll in Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name.IndexOf("archigun", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("archipelagun", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }

            // 2. A plugins sub-folder named like ArchipelaGun that holds a DLL
            //    (the r2modman layout uses per-mod sub-folders).
            foreach (string sub in Directory.EnumerateDirectories(pluginsDir, "*", SearchOption.AllDirectories))
            {
                string folder = Path.GetFileName(sub);
                if (folder.IndexOf("archigun", StringComparison.OrdinalIgnoreCase) < 0 &&
                    folder.IndexOf("archipelagun", StringComparison.OrdinalIgnoreCase) < 0 &&
                    folder.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                try
                {
                    if (Directory.EnumerateFiles(sub, "*.dll", SearchOption.AllDirectories).Any())
                        return sub;
                }
                catch { /* permission — keep scanning */ }
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Enter the Gungeon: prefer the exe in the detected/override install;
    /// if that cannot be found but Steam is present, fall back to the steam:// URL.
    private void StartEnterTheGungeon()
    {
        string? game = ResolveEtgDir();
        string? exe  = game != null ? Path.Combine(game, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = game!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Enter the Gungeon.");

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
            catch { /* fall through to error */ }
        }

        throw new FileNotFoundException(
            "Could not find \"EtG.exe\". Open this game's Settings and pick your " +
            "Enter the Gungeon install folder, or install Enter the Gungeon via Steam.",
            GameExeName);
    }

    // ── Private helpers — install override validation ─────────────────────────

    /// Used by the Settings folder picker: a valid ETG folder contains "EtG.exe".
    /// Returns null when acceptable, else a short reason string.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Enter the Gungeon install folder.";

        if (LooksLikeEtgDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeEtgDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like an Enter the Gungeon installation. Pick the " +
               "folder that contains \"EtG.exe\" — for Steam this is usually " +
               @"...\steamapps\common\Enter the Gungeon.";
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod's Thunderstore zip and extract it into
    /// <ETG>\BepInEx\plugins\ArchipelaGun\. HONEST scope: stages the ArchipelaGun
    /// plugin only; BepInEx / MTG API / Alexandria come from r2modman.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string apModDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"etg-archipelagun-{version}-{Guid.NewGuid():N}.zip");
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"etg-archipelagun-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading ArchipelaGun mod {version}..."));
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
                        progress.Report((pct, $"Downloading ArchipelaGun mod... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting the mod package..."));
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            progress.Report((85, "Installing mod into the Enter the Gungeon plugins folder..."));
            Directory.CreateDirectory(apModDir);

            // Find the payload root from the extracted tree, then copy it into
            // the install's plugins\ArchipelaGun folder.
            string payloadRoot = ResolvePluginPayloadRoot(tempDir);
            CopyDirectoryContents(payloadRoot, apModDir);

            progress.Report((95, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))  File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// Decide which extracted folder holds the BepInEx plugin payload to install.
    /// Order: a nested "BepInEx/plugins" folder, then a top-level "plugins" folder,
    /// then the extraction root (Thunderstore puts the DLL next to manifest.json).
    private static string ResolvePluginPayloadRoot(string extractedRoot)
    {
        try
        {
            // 1. .../BepInEx/plugins (canonical BepInEx mod layout).
            foreach (string dir in Directory.EnumerateDirectories(extractedRoot, "plugins", SearchOption.AllDirectories))
            {
                string parent = Path.GetFileName(Path.GetDirectoryName(dir) ?? "");
                if (parent.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) &&
                    DirectoryHasDll(dir))
                    return dir;
            }

            // 2. A top-level "plugins" folder with a DLL inside.
            string topPlugins = Path.Combine(extractedRoot, "plugins");
            if (Directory.Exists(topPlugins) && DirectoryHasDll(topPlugins))
                return topPlugins;
        }
        catch { /* fall through */ }

        // 3. The extraction root.
        return extractedRoot;
    }

    private static bool DirectoryHasDll(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }

    /// Recursively copy a directory's contents into a destination folder
    /// (overwriting), creating sub-folders as needed.
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
    // Keeps install-dir override + version stamp in its OWN JSON sidecar under
    // Games/ROMs/enter_the_gungeon/ — does NOT touch the shared SettingsStore.

    private sealed class EtgSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private EtgSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<EtgSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(EtgSettings s)
    {
        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
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
