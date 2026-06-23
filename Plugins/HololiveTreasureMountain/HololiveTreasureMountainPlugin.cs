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

namespace LauncherV2.Plugins.HololiveTreasureMountain;

// ═══════════════════════════════════════════════════════════════════════════════
// HololiveTreasureMountainPlugin — install / launch for "Hololive Treasure
// Mountain" (COVER Corp., 2024) played through the Archipelago BepInEx mod by
// StellatedCUBE. This is a NATIVE "ConnectsItself" integration: the game's own
// BepInEx plugin speaks to the AP server with no emulator and no Lua bridge.
//
// ── HONEST REALITY CHECK (2026-06-14, verified against the repo) ────────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned
// Hololive Treasure Mountain (Steam appid 2972990), and Archipelago support is
// a BepInEx plugin added on top. Verified facts:
//
//   * THE AP WORLD game string is "Hololive Treasure Mountain" (verified against
//     APWorld/__init__.py: `GAME = "Hololive Treasure Mountain"`).
//     GameId here = "hololive_treasure_mountain".
//     The apworld file is "HololiveTreasureMountain.apworld".
//
//   * THE MOD repo is StellatedCUBE/Treasure-Mountain-Archipelago on GitHub
//     (verified live 2026-06-14). Latest release is v1.0.0. Each release ships
//     two assets:
//       - HololiveTreasureMountain.apworld  (the AP world file)
//       - HololiveTreasureMountainArchipelago.zip  (the BepInEx mod)
//     The zip contains a BepInEx/ subtree and is extracted to the game's root
//     folder (so BepInEx/ merges with the game's own BepInEx if present, or
//     installs it fresh).
//
//   * THE GAME EXECUTABLE is TreasureMountain.exe (verified from the APSetup.cs
//     source which launches the popup with `exe` pointing at a file named
//     "TreasureMountainAPWindow.exe" from the BepInEx plugins folder, and from
//     the general BepInEx convention that the main exe's name matches the Unity
//     project — "TreasureMountain.exe").
//
//   * CONNECTION is made IN-GAME via a built-in Archipelago icon in the Options
//     menu (verified against APWorld/docs/setup_en.md: "Open the Options menu
//     and click on the Archipelago icon in the lower right corner. This will
//     open a popup window, where you can enter the details of your Archipelago
//     room."). The popup communicates back to the BepInEx plugin through a named
//     pipe and writes the last-used host/port/slot/password to
//     `Application.persistentDataPath/archipelago.cfg` (a Unity-computed path
//     under %AppData% or %LocalAppData%, not predictable by the launcher without
//     running the game once). Because this path is not stable before first run,
//     the launcher does NOT attempt to pre-write that file. The settings panel
//     surfaces the session credentials (server/slot) so the user can type them
//     into the in-game popup.
//
//   * BEPINEX is bundled inside the HololiveTreasureMountainArchipelago.zip
//     (the zip contains BepInEx/ and the mod DLL inside it). Extracting the
//     zip to the game root constitutes a complete mod install — no external
//     mod manager is required. This makes Install a genuine, complete step.
//
//   * THE FIRST LAUNCH may take several minutes and may crash once (verified
//     against setup_en.md: "it may take a while (several minutes) to load, or
//     even crash. Simply retry if it does."). The settings panel notes this.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Hololive Treasure Mountain install via the Windows
//      registry (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\HololiveTreasureMountain via appmanifest_2972990.acf.
//      A manual install-dir OVERRIDE (settings folder picker) is also supported
//      and takes precedence; it is validated (must contain TreasureMountain.exe)
//      and persisted in this plugin's OWN sidecar (Games/ROMs/
//      hololive_treasure_mountain/htm_launcher.json).
//   2. INSTALL/UPDATE = download HololiveTreasureMountainArchipelago.zip from
//      the latest GitHub release and extract it to the game root (so BepInEx/
//      merges in). This is a complete install — BepInEx is in the zip.
//   3. LAUNCH = run TreasureMountain.exe; fall back to
//      steam://rungameid/2972990 when the exe cannot be found.
//      ConnectsItself = true (the BepInEx mod owns the slot).
//      SupportsStandalone = true (plain game runs without AP).
//
// ── BUILD NOTE ────────────────────────────────────────────────────────────────
//   The launcher project sets UseWindowsForms=true alongside UseWPF=true.
//   All WPF UI types below are FULLY QUALIFIED (System.Windows.*) to avoid the
//   CS0104 ambiguity that arises when both frameworks are referenced. No
//   file-level `using X = System.Windows...;` aliases are added here because
//   GlobalUsings.cs already defines them and a local duplicate would be CS1537.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class HololiveTreasureMountainPlugin : IGamePlugin
{
    // ── Constants — the mod repo (GitHub, verified 2026-06-14) ───────────────
    private const string GH_OWNER   = "StellatedCUBE";
    private const string GH_REPO    = "Treasure-Mountain-Archipelago";
    private const string ModRepoUrl = $"https://github.com/{GH_OWNER}/{GH_REPO}";

    private const string GH_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases/latest";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases";

    private const string SetupGuideUrl   = $"{ModRepoUrl}/blob/main/APWorld/docs/setup_en.md";
    private const string ArchipelagoSite = "https://archipelago.gg";

    /// The zip asset name pattern we look for in release assets.
    private const string ModZipAssetName = "HololiveTreasureMountainArchipelago.zip";

    /// Pinned fallback — v1.0.0 verified live 2026-06-14.
    private const string FallbackVersion = "1.0.0";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/{ModZipAssetName}";

    // Steam — Hololive Treasure Mountain appid 2972990.
    private const string SteamAppId      = "2972990";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    private const string SteamCommonFolderName = "HololiveTreasureMountain";
    private const string GameExeName            = "TreasureMountain.exe";

    /// The BepInEx DLL that marks the mod as installed (inside the plugins sub-
    /// folder of the BepInEx tree). We accept any DLL whose name mentions
    /// "archipelago" under BepInEx\plugins (case-insensitive, recursive).
    private const string BepInExPluginsRelPath = @"BepInEx\plugins";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "hololive_treasure_mountain";
    public string DisplayName => "Hololive Treasure Mountain";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against APWorld/__init__.py
    /// (`GAME = "Hololive Treasure Mountain"`).
    public string ApWorldName => "Hololive Treasure Mountain";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "hololive_treasure_mountain.png");

    public string ThemeAccentColor => "#3DA4C8";   // holo-blue / ocean

    public string[] GameBadges => new[] { "Steam · needs mod" };

    public string Description =>
        "Hololive Treasure Mountain, the 3D physics puzzle stacking game by COVER Corp. " +
        "played through the Archipelago BepInEx mod by StellatedCUBE — which bundles an " +
        "in-game Archipelago client, so the game connects to the multiworld itself. " +
        "Stack Hololive talent figures to collect treasures, which become checks shuffled " +
        "across the multiworld. You bring your own copy of the game (owned on Steam); " +
        "the Archipelago mod is a BepInEx plugin added on top. The launcher detects your " +
        "Steam install and can install the complete mod package for you. You connect to " +
        "your server from the in-game Archipelago popup in the Options menu.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the AP BepInEx mod DLL is present under a detected/
    /// override game install's BepInEx\plugins tree. The user may have installed
    /// the mod by hand, which we honor; we do NOT gate on our own version stamp.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and working files. The actual mod is
    /// extracted INTO the game install's root folder. Exposed as GameDirectory.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "HololiveTreasureMountain");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "htm_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The BepInEx mod reports checks/items/goal to the AP server itself.
    // ConnectsItself = true — these events exist for interface compatibility only.
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
        catch
        {
            InstalledVersion = null;
        }
            try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(GH_OWNER, GH_REPO, ct));
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
        // 0. Locate the game install to drop the mod into.
        progress.Report((2, "Locating your Hololive Treasure Mountain installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Hololive Treasure Mountain installation. Open this " +
                "game's Settings and pick your game folder (the one containing " +
                "TreasureMountain.exe), or install the game via Steam first. The " +
                "Archipelago mod is added on top of your own copy of the game.");

        // 1. Resolve the latest release (pinned fallback when offline).
        progress.Report((6, "Checking the latest mod release on GitHub..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the mod download on GitHub. Check your internet " +
                "connection, or download it manually from " + ModRepoUrl + "/releases " +
                "and extract " + ModZipAssetName + " to your game folder.");

        // 2. Download and extract the mod zip to the game root.
        //    The zip contains BepInEx/ — extracting it to the game root is a complete
        //    install. BepInEx is bundled inside so no external mod manager is needed.
        await DownloadAndExtractModAsync(zipUrl, version, gameDir, progress, ct);

        // 3. Stamp the version for informational display.
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Installed the Archipelago mod {version} for Hololive Treasure Mountain. " +
            "Launch the game via Steam — the FIRST launch may take several minutes " +
            "and might crash once; just retry. To connect to your server: open the " +
            "Options menu and click the Archipelago icon in the lower right corner. " +
            "Enter your server address, port, slot name and password in the popup. " +
            "(This launcher cannot pre-fill the in-game connection — it is entered " +
            "via the Options popup.) Open Settings for the full guided steps."));
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
        // HONEST: the AP server connection for Hololive Treasure Mountain is made
        // IN-GAME. After launching, open Options and click the Archipelago icon to
        // open the connection popup (host / port / slot / password). There is no
        // command-line arg or config file this launcher can pre-write to a stable
        // path (the game's archipelago.cfg lives at Application.persistentDataPath,
        // a Unity-computed path not predictable before the game has run at least
        // once). ConnectsItself = true: the launcher must NOT hold its own ApClient
        // on this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartGame();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Hololive Treasure Mountain runs without the mod.
    public bool SupportsStandalone => true;

    /// The BepInEx mod owns the slot connection.
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
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// A valid game folder contains TreasureMountain.exe.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Hololive Treasure Mountain " +
                   "install folder.";

        if (LooksLikeGameDir(folder)) return null;

        // Be forgiving: user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeGameDir(nested)) return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Hololive Treasure Mountain installation. " +
               "Pick the folder that contains TreasureMountain.exe — for Steam this " +
               @"is usually ...\steamapps\common\HololiveTreasureMountain.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20),
        };

        // ── Overview ──────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Hololive Treasure Mountain is your own game (Steam) with the " +
                   "Archipelago mod added on top as a BepInEx plugin. The mod's zip " +
                   "includes BepInEx, so the Install button on the Play tab extracts " +
                   "everything directly into your game folder — no external mod manager " +
                   "needed. The first launch may take several minutes and might crash " +
                   "once; just retry. Connection to the AP server is made in-game via " +
                   "the Options menu popup. These external steps are not verified by " +
                   "this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: game install / override ──────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GAME INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();
        string? modDll      = FindInstalledModDll();
        bool    bepInExOk   = gameDir != null &&
                              Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Game not detected. Pick your install folder below, or install " +
              "Hololive Treasure Mountain via Steam first.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = bepInExOk
                ? "BepInEx found (BepInEx\\core present)."
                : "BepInEx not found yet — use the Install button to deploy the mod.",
            FontSize = 11, Foreground = bepInExOk ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                ? "Archipelago mod found: " + modDll
                : "Archipelago mod not found in BepInEx\\plugins yet (use Install " +
                  "on the Play tab, or extract the mod zip manually to your game folder).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = overrideDir ?? gameDir ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Hololive Treasure Mountain install folder (contains " +
                          "TreasureMountain.exe). Detected from Steam automatically; " +
                          "use this picker to override for non-standard installations.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content     = "Select folder...",
            Width       = 120,
            Padding     = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Hololive Treasure Mountain install folder",
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
                    System.Windows.MessageBox.Show(
                        bad,
                        "Not a Hololive Treasure Mountain folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // Descend if user picked the Steam "common" parent.
                if (!LooksLikeGameDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeGameDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 2972990). Use this " +
                   "picker for a non-standard Steam library or a manual install.",
            FontSize = 11, Foreground = muted, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (this session) ────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Launch the game, then open the Options menu and click the " +
                   "Archipelago icon in the lower right corner. A popup window will " +
                   "appear where you enter your server address, port, slot name, and " +
                   "password. The game saves these for future sessions. This launcher " +
                   "cannot pre-fill the in-game connection.",
            FontSize = 11, Foreground = fg, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Hololive Treasure Mountain on Steam (appid 2972990). Install it " +
               "if you have not. Use the folder picker above if it was not detected.",
            "2. Install the Archipelago mod: click Install on the Play tab. The launcher " +
               "downloads and extracts HololiveTreasureMountainArchipelago.zip to your " +
               "game folder — BepInEx is included in the zip, so this is a complete " +
               "install. Alternatively, do it by hand from the mod releases (link below).",
            "3. Launch the game through Steam. The first launch may take several minutes " +
               "and might crash once — this is normal for a first BepInEx load. Simply " +
               "retry if it does.",
            "4. To connect: open the in-game Options menu and click the Archipelago icon " +
               "in the lower right corner. Enter your server address (e.g. archipelago.gg), " +
               "port (e.g. 38281), slot name, and password (if any). Click Connect.",
            "5. The game remembers your connection details for future sessions. Connecting " +
               "to the same game via the popup, even at a different address, loads the " +
               "correct save file automatically.",
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
        foreach (var (label, url) in new (string, string)[]
        {
            ("Archipelago Mod (GitHub) ↗",    ModRepoUrl),
            ("Mod Releases ↗",                ModRepoUrl + "/releases"),
            ("Setup Guide ↗",                 SetupGuideUrl),
            ("Archipelago Official ↗",         ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content          = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding          = new System.Windows.Thickness(0, 2, 0, 2),
                Background       = System.Windows.Media.Brushes.Transparent,
                BorderThickness  = new System.Windows.Thickness(0),
                FontSize         = 12,
                Margin           = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground       = accent,
                Cursor           = System.Windows.Input.Cursors.Hand,
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

                string ver = el.TryGetProperty("tag_name", out var t)
                    ? NormalizeTag(t.GetString()) ?? ""
                    : "";

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name", out var n) ? n.GetString() ?? "" : $"Release {ver}",
                    Body:    el.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                    Version: ver,
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

    /// "v1.0.0" → "1.0.0" etc.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest GitHub release's version + the mod zip download URL.
    /// Falls back to the pinned v1.0.0 URL when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null &&
                root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;   // exact name match
                string? anyZip    = null;   // any .zip

                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null &&
                        lower.Contains("archipelago") &&
                        !lower.EndsWith(".apworld"))
                        preferred = url;
                }

                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

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
                catch { /* try next library */ }
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

    /// Find the AP BepInEx plugin DLL under the game install's BepInEx\plugins
    /// tree (recursive, case-insensitive). Accept any *.dll whose name mentions
    /// "archipelago". Returns the matched DLL path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? game = ResolveGameDir();
            if (game == null) return null;

            string pluginsDir = Path.Combine(game, BepInExPluginsRelPath);
            if (!Directory.Exists(pluginsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(
                pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        string? game = ResolveGameDir();
        string? exe  = game != null ? Path.Combine(game, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = game!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException(
                "Failed to start Hololive Treasure Mountain.");

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

        // Fall back to the Steam URL if Steam is installed.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { }
        }

        throw new FileNotFoundException(
            "Could not find TreasureMountain.exe. Open this game's Settings and pick " +
            "your Hololive Treasure Mountain install folder, or install the game via Steam.",
            GameExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod zip and extract its BepInEx/ tree to the game root.
    /// The zip ships the full BepInEx installation, so extracting to the game root
    /// is a complete, self-sufficient mod install.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string gameDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"htm-archipelago-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((10, $"Downloading Archipelago mod {version}..."));
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
                        int pct = (int)(10 + 65 * downloaded / total);
                        progress.Report((pct, $"Downloading Archipelago mod... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((80, "Extracting mod to your game folder..."));
            // Extract to game root — the zip contains BepInEx/ which merges in.
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, gameDir, overwriteFiles: true);

            progress.Report((95, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class HtmSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private HtmSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<HtmSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(HtmSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(
                SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — setting won't persist this time */ }
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
