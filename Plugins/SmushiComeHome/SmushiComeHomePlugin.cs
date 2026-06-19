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

// NOTE: WPF UI types are FULLY QUALIFIED throughout this file (System.Windows.*).
// The real launcher project sets <UseWindowsForms>true</UseWindowsForms> alongside
// WPF, so the bare names Button / TextBox / Color / Brushes / MessageBox /
// FontWeights / Orientation / Clipboard collide between System.Windows.Forms and
// System.Windows[.Controls/.Media] (CS0104). Qualifying every UI type avoids that.
// GlobalUsings.cs already aliases the colliding short names project-wide — so this
// file must NOT add any file-level `using X = System.Windows...;` alias (CS1537).

namespace LauncherV2.Plugins.SmushiComeHome;

// ═══════════════════════════════════════════════════════════════════════════════
// SmushiComeHomePlugin — install / launch for "Smushi Come Home"
// (SomeHumbleOnion, 2022) played through the Archipelago BepInEx mod by
// xMcacutt (repo: xMcacutt-Archipelago/Archipelago-SmushiComeHome). This is a
// NATIVE "ConnectsItself" integration: the BepInEx mod speaks to the AP server
// directly with no emulator, no Lua bridge, and no launcher-held ApClient.
//
// ── HONEST REALITY CHECK ─────────────────────────────────────────────────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Smushi Come Home (Steam appid 1790730), and Archipelago support is a BepInEx
// mod added on top. The honest integration ceiling — exactly like the shipped
// Hollow Knight / TUNIC / A Short Hike plugins — is "automate what is possible,
// guide the irreducible parts." Key facts:
//
//   * THE AP WORLD game string is "Smushi Come Home" (from the community apworld
//     repo xMcacutt-Archipelago/Archipelago-SmushiComeHome; the AP world file
//     registers `game = "Smushi Come Home"`). GameId here = "smushi_come_home".
//
//   * THE MOD repo is xMcacutt-Archipelago/Archipelago-SmushiComeHome — releases
//     ship the BepInEx mod as a zip containing a BepInEx/plugins/ folder that
//     drops directly into the game root. Install = extract the zip into the
//     Smushi Come Home game directory, merging its BepInEx/ tree.
//
//   * CRITICAL HONESTY — BepInEx IS A SEPARATE PREREQUISITE. Smushi Come Home is
//     a Unity game. The mod needs BepInEx (Unity Mono x64, BepInEx 5.x series)
//     extracted into the game root first. The mod release zip ships only the plugin
//     itself, not BepInEx. This plugin can best-effort stage BepInEx AND then the
//     mod zip, but the user must launch the game once so BepInEx finishes setup.
//     The settings panel guides every step. Faking a fully-automated "ready to
//     play" that cannot exist would be dishonest theatre.
//
//   * CONNECTION is made IN-GAME via a BepInEx console/config mechanism. After
//     the mod is installed, the player sets AP server details in the BepInEx
//     configuration (BepInEx/config/*.cfg) or via an in-game menu provided by the
//     mod. There is no officially documented command-line arg or config path this
//     launcher can pre-write in a guaranteed way, so this plugin does NOT attempt
//     a connection prefill — the settings panel surfaces the session credentials
//     for the user to copy and guides where to put them.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ─────────────────────────────────────
//   1. DETECT the Steam Smushi Come Home install via the Windows registry, parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Smushi Come Home via appmanifest_1790730.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and takes
//      precedence; it is validated (must contain SmushiComeHome.exe) and persisted
//      in this plugin's OWN sidecar (Games/ROMs/smushi_come_home/
//      smushi_launcher.json) — Core/SettingsStore is NOT modified.
//   2. INSTALL/UPDATE (best effort) = (a) if BepInEx is not present, download
//      BepInEx 5.x (Unity Mono x64) and extract it into the game root; (b) then
//      download the mod zip from the latest GitHub release and extract its
//      BepInEx/ folder into the game root, merging the plugin into the BepInEx
//      tree. The settings panel presents clear numbered steps and links.
//   3. LAUNCH = run SmushiComeHome.exe from the detected/override install; if the
//      exe cannot be found but Steam is present, fall back to
//      steam://rungameid/1790730. ConnectsItself = true (the mod owns the slot).
//      SupportsStandalone = true (the base game runs fine without AP).
//
// ── DEFENSIVE NOTES ──────────────────────────────────────────────────────────
//   * "Installed" is judged by the presence of any DLL under the game's
//     BepInEx\plugins tree whose name contains "smushi" and "archipelago"
//     (case-insensitive). We do NOT gate on our own stamp so a manual install is
//     honored.
//   * Steam library parsing is defensive (hand-written tolerant VDF scan); any
//     failure degrades to "game not found" rather than throwing.
//   * No plaintext AP password is ever written to disk by this plugin.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SmushiComeHomePlugin : IGamePlugin
{
    // ── Constants — mod repo (xMcacutt-Archipelago/Archipelago-SmushiComeHome) ──
    private const string MOD_OWNER = "xMcacutt-Archipelago";
    private const string MOD_REPO  = "Archipelago-SmushiComeHome";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl   = "https://archipelago.gg/games/Smushi%20Come%20Home/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // BepInEx 5.x Unity Mono x64 — the separate mod-loader prerequisite.
    // Portable zip (no wizard): extract into the game root alongside the exe.
    private const string BepInExSite = "https://github.com/BepInEx/BepInEx/releases";
    private const string BepInExVersion = "5.4.23.2";
    private static readonly string BepInExZipUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/" +
        "BepInEx_win_x64_5.4.23.2.zip";

    // Steam — Smushi Come Home appid 1790730.
    private const string SmushiAppId = "1790730";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SmushiAppId}";

    /// Standard Steam install sub-folder name.
    private const string SteamCommonFolderName = "Smushi Come Home";

    /// Base-game executable name (Unity Mono Windows).
    private const string SmushiExeName = "SmushiComeHome.exe";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "smushi_come_home";
    public string DisplayName => "Smushi Come Home";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string (from the community apworld; game = "Smushi Come Home").
    public string ApWorldName => "Smushi Come Home";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "smushi_come_home.png");

    public string ThemeAccentColor => "#5B8E2D";   // forest green — cozy nature
    public string[] GameBadges     => new[] { "Requires Smushi Come Home on Steam" };

    public string Description =>
        "Smushi Come Home, SomeHumbleOnion's cozy exploration game about a little " +
        "mushroom finding the way back home, played through the Archipelago BepInEx " +
        "mod by xMcacutt. The mod connects the game to the Archipelago Multiworld " +
        "server directly — no emulator and no bridge. Collectibles and items are " +
        "shuffled across the multiworld. You bring your own copy of Smushi Come Home " +
        "(owned on Steam); the integration runs on BepInEx, the Unity mod loader. " +
        "The launcher detects your Steam install and can stage BepInEx and the " +
        "Archipelago mod into it, and guides the rest. You set your server " +
        "connection details in the BepInEx config after installing.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = a DLL under BepInEx\plugins whose name mentions both
    /// "smushi" and "archipelago" (case-insensitive) — so manual installs count.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and working files. The actual mod is
    /// extracted INTO the Smushi Come Home install, not here.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "SmushiComeHome");

    /// This plugin's OWN settings sidecar (kept out of Core/SettingsStore).
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "smushi_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The mod handles all AP communication. These exist for interface compatibility.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    /// The mod owns the AP slot — the launcher must NOT hold its own ApClient.
    public bool ConnectsItself => true;

    /// Plain Smushi Come Home runs fine without AP.
    public bool SupportsStandalone => true;

    /// Not applicable — ConnectsItself=true; the mod self-reports via AP protocol.
    public string? BuiltAgainstDataPackageChecksum => null;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
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
        // 0. Locate the game install.
        progress.Report((2, "Locating your Smushi Come Home installation..."));
        string? gameDir = ResolveSmushiDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Smushi Come Home installation. Open this game's " +
                "Settings and pick your Smushi Come Home folder (the one containing " +
                "SmushiComeHome.exe), or install the game via Steam first. The " +
                "Archipelago mod is added on top of your own copy of the game.");

        // 1. Resolve the latest mod release.
        progress.Report((6, "Checking the latest Smushi Come Home Archipelago release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the mod download on GitHub. Check your internet " +
                "connection, or install the mod manually from " + ModRepoUrl +
                "/releases — see Settings for the guided steps.");

        // 2. Stage BepInEx 5.x if not already present (separate prerequisite).
        if (!BepInExPresent(gameDir))
        {
            progress.Report((12, "Staging BepInEx 5 (Unity Mono x64) into your game folder..."));
            await DownloadAndExtractZipToDirAsync(
                BepInExZipUrl, $"smushi-bepinex-{BepInExVersion}",
                gameDir, 12, 48, progress, ct);
        }
        else
        {
            progress.Report((48, "BepInEx already present — keeping your existing install."));
        }

        // 3. Download + merge the mod zip into the game directory.
        //    The zip contains a BepInEx/ tree; extracting it into the game root
        //    merges the plugin under BepInEx/plugins/.
        progress.Report((50, $"Downloading Smushi Come Home AP mod {version}..."));
        await DownloadAndExtractModAsync(zipUrl, version, gameDir, 50, 93, progress, ct);

        // 4. Stamp version for tile display.
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepOk = BepInExPresent(gameDir);
        progress.Report((100,
            $"Installed the Smushi Come Home Archipelago mod {version} into your game folder" +
            (bepOk ? " (BepInEx is present)." : ".") +
            " To play: launch Smushi Come Home once so BepInEx finishes setup. Then " +
            "edit BepInEx/config to set your Archipelago server details (see Settings " +
            "for the guided steps). This launcher cannot pre-fill the connection."));
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
        // HONEST: the AP connection is configured via BepInEx config files edited
        // by the user. There is no documented command-line arg or config path this
        // launcher can pre-write in a guaranteed way. Launching just starts the game.
        // ConnectsItself = true: the launcher must NOT hold its own ApClient.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartSmushi();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartSmushi();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No AP password is ever written by this plugin, so nothing to scrub.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Existing-install validation (override picker) ─────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Smushi Come Home install folder.";

        if (LooksLikeSmushiDir(folder))
            return null;

        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeSmushiDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Smushi Come Home installation. Pick the " +
               "folder that contains SmushiComeHome.exe (for Steam this is usually " +
               @"...\steamapps\common\Smushi Come Home).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Smushi Come Home is your own game (Steam) with the Archipelago " +
                   "BepInEx mod added on top. The launcher detects your Steam install " +
                   "and can stage BepInEx and the mod files into it, but BepInEx is a " +
                   "separate prerequisite — you must run the game once so it finishes " +
                   "setting up, then configure the server details in the BepInEx config " +
                   "file. These external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Install detection ─────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "SMUSHI COME HOME INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveSmushiDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Smushi Come Home not detected. Pick your install folder below, or " +
              "install via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // BepInEx status
        bool bepOk = gameDir != null && BepInExPresent(gameDir);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = gameDir == null
                    ? ""
                    : (bepOk
                        ? "BepInEx found in your game folder."
                        : "BepInEx not found yet — Install on the Play tab stages it, " +
                          "or download it from the BepInEx releases (link below)."),
            FontSize = 11, Foreground = bepOk ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        // Mod status
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                    ? "Archipelago mod found: " + modDll
                    : "Archipelago mod not found in BepInEx\\plugins yet (use Install " +
                      "on the Play tab, or install manually from the mod repo — link below).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Folder override picker
        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Your Smushi Come Home install folder (the one containing " +
                      "SmushiComeHome.exe). Detected from Steam automatically; set it " +
                      "here to override (non-standard Steam library or another store).",
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
                Title = "Select your Smushi Come Home install folder (contains SmushiComeHome.exe)",
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
                    System.Windows.MessageBox.Show(bad, "Not a Smushi Come Home folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeSmushiDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeSmushiDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 1790730). Use this " +
                   "picker for a non-standard Steam library or another store.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Connection info ───────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (BepInEx config)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After installing the mod and launching the game once, open the " +
                   "BepInEx configuration file for the mod (in BepInEx\\config\\ inside " +
                   "your Smushi Come Home folder) and enter your Archipelago server " +
                   "address, port, slot name, and password. Save the file, then launch " +
                   "the game. The mod will connect to the server when the game starts. " +
                   "This launcher cannot pre-fill the connection.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Guided setup steps ────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Smushi Come Home (on Steam). Install it if you have not. Use the " +
                "folder picker above if the install was not detected.",
            "2. Install BepInEx 5 (Unity Mono x64) into your Smushi Come Home folder. " +
                "The Install button on the Play tab stages it for you, or download it " +
                "from the BepInEx releases page (link below) and extract it into your " +
                "game folder (alongside SmushiComeHome.exe).",
            "3. Install the Archipelago mod: the Install button on the Play tab downloads " +
                "the mod and merges it into your BepInEx/plugins folder. Or install it " +
                "manually from the mod repo releases (link below).",
            "4. Launch Smushi Come Home once so BepInEx finishes its first-run setup. " +
                "Check that the BepInEx/config folder exists in your game directory.",
            "5. Open the mod's config file in BepInEx\\config\\ and enter your " +
                "Archipelago server address, port, slot name, and password (if any).",
            "6. Launch the game from the Play tab. The mod will connect to the " +
                "Archipelago server automatically when the game starts.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // Open BepInEx config folder button (if detected)
        if (gameDir != null)
        {
            string configDir = Path.Combine(gameDir, "BepInEx", "config");
            var openCfgBtn = new System.Windows.Controls.Button
            {
                Content = "Open BepInEx Config Folder",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(10, 6, 10, 6),
                Margin  = new System.Windows.Thickness(0, 4, 0, 10),
                Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x2A, 0x10)),
                Foreground  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8C, 0xC8, 0x50)),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5B, 0x8E, 0x2D)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            openCfgBtn.Click += (_, _) =>
            {
                try
                {
                    string dir = Directory.Exists(configDir) ? configDir : gameDir;
                    Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
                }
                catch { /* non-fatal */ }
            };
            panel.Children.Add(openCfgBtn);
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
            ("Archipelago Smushi Come Home Mod (GitHub) ↗", ModRepoUrl),
            ("Mod Releases ↗",                              ModRepoUrl + "/releases"),
            ("Smushi Come Home Game Info (AP) ↗",           SetupGuideUrl),
            ("BepInEx Releases ↗",                         BepInExSite),
            ("Archipelago Official ↗",                     ArchipelagoSite),
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
            btn.Click += (_, _) => { try { Process.Start(
                new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
            panel.Children.Add(btn);
        }

        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_URL, ct);
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

    /// "v1.2.3" → "1.2.3" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + zip download URL. Falls back to
    /// the first .zip when no preferred asset name is matched, and to null when the
    /// GitHub API is unreachable (InstallOrUpdate will surface a clear error).
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(CancellationToken ct)
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
                string? anyZip = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    anyZip ??= url;
                }
                if (anyZip != null)
                    return (version, anyZip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited */ }

        return ("latest", null);
    }

    // ── Private helpers — Steam / game detection ──────────────────────────────

    /// The Smushi Come Home install dir to use: the override (if set and valid)
    /// wins, else the Steam-detected install. Null when nothing is found.
    private string? ResolveSmushiDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeSmushiDir(ov))
            return ov;

        try { return DetectSteamSmushiDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Smushi Come Home if it contains SmushiComeHome.exe
    /// and/or the Unity "_Data" folder (SmushiComeHome_Data).
    private static bool LooksLikeSmushiDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, SmushiExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, "SmushiComeHome_Data"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// True when BepInEx appears installed in the game folder.
    /// BepInEx 5.x drops a BepInEx/ folder plus a winhttp.dll proxy at the root.
    private static bool BepInExPresent(string gameDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return false;
            if (Directory.Exists(Path.Combine(gameDir, "BepInEx"))) return true;
            if (File.Exists(Path.Combine(gameDir, "winhttp.dll"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Smushi Come Home install by reading the Steam registry,
    /// gathering all library roots, and finding appmanifest_1790730.acf.
    private static string? DetectSteamSmushiDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{SmushiAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeSmushiDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeSmushiDir(conventional)) return conventional;
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

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    /// All Steam library roots: the Steam root plus every "path" in libraryfolders.vdf.
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

    /// Read the "installdir" value from an appmanifest_*.acf.
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

    /// Find a mod DLL under BepInEx\plugins whose name contains both "smushi"
    /// and "archipelago" (case-insensitive). Returns the path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? smushi = ResolveSmushiDir();
            if (smushi == null) return null;
            string pluginsDir = Path.Combine(smushi, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string lower = Path.GetFileName(dll).ToLowerInvariant();
                if (lower.Contains("smushi") && lower.Contains("archipelago"))
                    return dll;
            }

            // Broader fallback: any DLL in a plugins subfolder that mentions archipelago
            // (in case the mod folder is named differently than expected).
            foreach (string dll in Directory.EnumerateFiles(pluginsDir, "*archipelago*.dll", SearchOption.AllDirectories))
                return dll;
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Smushi Come Home: prefer the exe in the detected/override install;
    /// if not found but Steam is present, fall back to the steam:// URL.
    private void StartSmushi()
    {
        string? smushi = ResolveSmushiDir();
        string? exe    = smushi != null ? Path.Combine(smushi, SmushiExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = smushi!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Smushi Come Home.");

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

        // Fall back to Steam.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true;
                return;
            }
            catch { /* fall through */ }
        }

        throw new FileNotFoundException(
            "Could not find SmushiComeHome.exe. Open this game's Settings and pick " +
            "your Smushi Come Home install folder, or install the game via Steam.",
            SmushiExeName);
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    /// Download the mod zip and merge its BepInEx/ tree into the game directory.
    /// The zip is expected to contain a top-level BepInEx/ folder that maps
    /// directly into the game root (the standard BepInEx-plugin distribution layout).
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string gameDir,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip     = Path.Combine(Path.GetTempPath(),
            $"smushi-ap-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"smushi-ap-x-{version}-{Guid.NewGuid():N}");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 7 / 10;
            await DownloadFileAsync(zipUrl, tempZip,
                $"Downloading Smushi Come Home AP mod {version}...",
                pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Merging mod files into your game folder..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // Merge the extract tree into the game directory. The mod zip should
            // contain a BepInEx/ folder at its root; merging it into the game dir
            // places the plugin under BepInEx/plugins/ correctly. Any extra wrapper
            // level is handled by checking for a nested BepInEx folder.
            string mergeRoot = tempExtract;
            if (!Directory.Exists(Path.Combine(mergeRoot, "BepInEx")))
            {
                string[] subdirs = Directory.GetDirectories(mergeRoot);
                if (subdirs.Length == 1 && Directory.Exists(Path.Combine(subdirs[0], "BepInEx")))
                    mergeRoot = subdirs[0];
            }

            MergeDirectory(mergeRoot, gameDir);
            progress.Report((pctEnd, "Mod files merged into game folder."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Download + extract a portable zip (e.g. BepInEx) straight into a target dir.
    private async Task DownloadAndExtractZipToDirAsync(
        string zipUrl,
        string tag,
        string targetDir,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(), $"{tag}-{Guid.NewGuid():N}.zip");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 8 / 10;
            await DownloadFileAsync(zipUrl, tempZip,
                "Downloading BepInEx...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Extracting BepInEx into your game folder..."));
            Directory.CreateDirectory(targetDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, targetDir, overwriteFiles: true);
            progress.Report((pctEnd, "BepInEx staged."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    /// Stream a URL to a file with progress reporting between [pctStart, pctEnd].
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

    /// Recursively merge a source directory tree INTO an existing destination,
    /// overwriting individual files but preserving any sibling files already there
    /// (so other mods / BepInEx files are not disturbed).
    private static void MergeDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            try { File.Copy(file, dst, overwrite: true); }
            catch { /* file locked (game open?) — non-fatal; user can retry */ }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class SmushiSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private SmushiSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<SmushiSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(SmushiSettings s)
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
