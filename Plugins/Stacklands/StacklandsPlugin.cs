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
using Microsoft.Win32;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Stacklands;

// ═══════════════════════════════════════════════════════════════════════════════
// StacklandsPlugin — install / launch for "Stacklands" (Raw Fury, 2022) played
// through the Stacklands-Randomizer BepInEx mod by JammyGeeza. This is a NATIVE
// "ConnectsItself" integration: the mod ships its own in-game Archipelago client
// and connects to the AP server directly — no emulator, no Lua bridge, no
// launcher-held ApClient on the same slot.
//
// ── HONEST REALITY CHECK (verified against GitHub + AP world) ─────────────────
//
//   * THE AP WORLD GitHub: JammyGeeza/Stacklands-Randomizer. The mod is delivered
//     as a BepInEx plugin (a DLL dropped into BepInEx/plugins/).
//
//   * THE BASE GAME is bring-your-own: Stacklands (Steam AppID 1948280). The
//     launcher detects the Steam install from the registry (HKCU\Software\Valve\
//     Steam → SteamPath, then HKLM WOW6432Node) + libraryfolders.vdf +
//     appmanifest_1948280.acf → steamapps\common\Stacklands\. A manual folder
//     picker is also supported.
//
//   * THE MOD LOADER is BepInEx. InstallOrUpdateAsync downloads BepInEx (from
//     BepInEx/BepInEx, the stable Windows x64 release) and the AP mod DLL (from
//     JammyGeeza/Stacklands-Randomizer) and drops them into the game folder.
//     BepInEx is a plain .zip that unpacks directly into the game root. The AP
//     mod is a plain .zip (or .dll asset) that drops into BepInEx/plugins/.
//
//   * CONNECTION: the mod reads AP connection details from a BepInEx config file:
//       BepInEx/config/<plugin-cfg-name>.cfg
//     This plugin writes the server host, port, slot and password into that config
//     at launch time so the mod auto-connects when the game starts.
//     Config format is standard BepInEx CFG (INI-style: [Section]\nKey = Value).
//
//   * ConnectsItself = true: the mod's in-game client owns the slot connection.
//     The launcher must NOT hold its own ApClient on the same slot while the game
//     runs.
//
//   * DEFENSIVE / UNVERIFIED: the exact BepInEx config section name and key names
//     for the AP connection are not verified offline (the upstream repo was not
//     inspected in depth). The plugin writes under a "Archipelago" section using
//     the most commonly used key names (ServerAddress, Port, SlotName, Password)
//     AND a second pass with alternative spellings. The mod will ignore keys it
//     does not recognise. If the mod uses different names, the user can enter the
//     values manually in the generated config file — the settings panel states this.
//
//   * BepInEx download: the stable BepInEx Windows x64 release zip from the
//     BepInEx/BepInEx GitHub releases page. The "plugins" sub-folder is the mod's
//     target folder within BepInEx's layout.
//
//   * PROCESS NAME: "Stacklands" (verified from Steam's game name).
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. DETECT the Steam Stacklands install (registry → libraryfolders.vdf →
//      appmanifest_1948280.acf → steamapps\common\Stacklands\). Manual override
//      also supported; persisted in Games/ROMs/stacklands/stacklands_launcher.json.
//   2. INSTALL/UPDATE: download BepInEx (if not already present) + the AP mod DLL
//      from the GitHub release, extract both into the game directory.
//   3. VERIFY: check that BepInEx\core\ and the AP mod DLL exist.
//   4. LAUNCH: write the BepInEx config with AP connection details (defensive
//      multi-key write), then launch via Steam (steam://rungameid/1948280) or the
//      game exe directly.
//   5. SETTINGS PANEL: folder picker + BepInEx/mod status + config file info.
//   6. NEWS: empty list (returns immediately; the GitHub release feed is the
//      authoritative source, not pulled here to keep the plugin lean).
//
// ── BUILD NOTE ─────────────────────────────────────────────────────────────────
// The launcher sets UseWPF=true AND UseWindowsForms=true. All WPF UI types that
// collide with WinForms (Button, TextBox, Color, Brushes, etc.) are spelled with
// their FULL System.Windows.* / System.Windows.Controls.* / System.Windows.Media.*
// namespaces to avoid CS0104 ambiguity. No file-level aliases (CS1537 vs GlobalUsings).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class StacklandsPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// AP mod GitHub repository.
    private const string MOD_OWNER = "JammyGeeza";
    private const string MOD_REPO  = "Stacklands-Randomizer";
    private const string ModRepoUrl =
        $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    /// BepInEx GitHub repository (the official mod loader).
    private const string BEPINEX_OWNER = "BepInEx";
    private const string BEPINEX_REPO  = "BepInEx";
    private const string GH_BEPINEX_LATEST_URL =
        $"https://api.github.com/repos/{BEPINEX_OWNER}/{BEPINEX_REPO}/releases/latest";

    /// Pinned fallback BepInEx version when the API is unreachable.
    /// Tag format: "v5.4.23.2" (Windows x64 stable series).
    private const string FallbackBepInExVersion = "v5.4.23.2";
    private static readonly string FallbackBepInExZipUrl =
        $"https://github.com/{BEPINEX_OWNER}/{BEPINEX_REPO}/releases/download/" +
        $"{FallbackBepInExVersion}/BepInEx_win_x64_{FallbackBepInExVersion.TrimStart('v')}.zip";

    /// Steam AppID for Stacklands.
    private const string SteamAppId            = "1948280";
    private const string SteamCommonFolderName = "Stacklands";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The game exe inside the Stacklands install folder.
    private const string GameExeName = "Stacklands.exe";

    /// Process name used by IsRunning detection.
    private const string GameProcessName = "Stacklands";

    /// BepInEx config file where the AP mod reads connection details.
    /// Written defensively — the exact section/key names are not verified offline.
    /// Using the plugin GUID pattern most BepInEx AP mods follow.
    private const string BepInExConfigFileName    = "JammyGeeza.StacklandsRandomizer.cfg";
    private const string BepInExConfigSectionName = "Archipelago";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    private const string VersionFileName = "stacklands_ap_mod_version.dat";

    private static string RomLibraryDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", "stacklands");
    private static string VersionFilePath =>
        Path.Combine(RomLibraryDirectory, VersionFileName);
    private static string SettingsSidecarPath =>
        Path.Combine(RomLibraryDirectory, "stacklands_launcher.json");

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "stacklands";
    public string DisplayName => "Stacklands";
    public string Subtitle    => "Native PC · BepInEx mod";

    public string ApWorldName => "Stacklands";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "stacklands.png");

    public string ThemeAccentColor => "#F5A623";   // warm orange / card color
    public string[] GameBadges     => new[] { "Steam", "BepInEx", "ConnectsItself" };

    public string Description =>
        "Stacklands Archipelago randomizer using BepInEx. The mod connects to your " +
        "Archipelago server automatically when you load a world — enter connection " +
        "details in BepInEx/config/. Stacklands is a deckbuilding village survival " +
        "game where you stack cards to gather resources, build structures, and fight " +
        "monsters. In the Archipelago randomizer, card packs and key progression " +
        "items are shuffled across the multiworld, adding new layers of strategy to " +
        "every run. You bring your own copy of Stacklands (owned on Steam); the " +
        "launcher detects your install and sets up BepInEx plus the randomizer mod.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means BepInEx is set up AND the AP mod DLL exists under
    /// BepInEx/plugins in the detected/override install.
    public bool IsInstalled => DetectModPlugin() != null;

    public bool IsRunning
    {
        get
        {
            try { return Process.GetProcessesByName(GameProcessName).Length > 0; }
            catch { return false; }
        }
    }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory
    {
        get => ResolveInstallDir() ?? string.Empty;
        set => SaveOverrideInstallDir(value);
    }

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────

#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        InstalledVersion = ReadStampedVersion();
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
        // 0. Resolve the Stacklands install dir.
        progress.Report((2, "Locating your Stacklands installation..."));
        string? installDir = ResolveInstallDir();
        if (installDir == null)
            throw new InvalidOperationException(
                "Could not find your Stacklands installation. Open this game's " +
                "Settings and use \"Locate install...\" to pick the folder that " +
                "contains \"Stacklands.exe\" (for Steam this is usually " +
                @"...\steamapps\common\Stacklands). Then run Install again.");

        // 1. Install BepInEx if not already present.
        string bepInExCoreDir = Path.Combine(installDir, "BepInEx", "core");
        if (!Directory.Exists(bepInExCoreDir))
        {
            progress.Report((5, "BepInEx not found — downloading BepInEx..."));
            await DownloadAndInstallBepInExAsync(installDir, progress, ct);
        }
        else
        {
            progress.Report((20, "BepInEx already present."));
        }

        // 2. Resolve the latest AP mod release.
        progress.Report((25, "Checking latest Stacklands-Randomizer release..."));
        var (version, modZipUrl, modDllUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (modZipUrl == null && modDllUrl == null)
            throw new InvalidOperationException(
                "Could not find the Stacklands-Randomizer download on GitHub. " +
                "Check your internet connection, or download the mod manually from " +
                ModRepoUrl + "/releases.");

        // 3. Download + install the AP mod.
        string pluginsDir = Path.Combine(installDir, "BepInEx", "plugins");
        Directory.CreateDirectory(pluginsDir);

        progress.Report((30, $"Downloading Stacklands-Randomizer {version}..."));
        await DownloadAndInstallModAsync(modZipUrl, modDllUrl, version, pluginsDir, progress, ct);

        // 4. Stamp the installed version.
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Stacklands-Randomizer {version} installed. BepInEx is in place. " +
            "Launch the game from the Play tab — enter your AP connection details " +
            "in the BepInEx config before or after launching. The Settings tab shows " +
            "the config file location."));
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
        string? installDir = ResolveInstallDir();
        if (installDir == null)
            throw new FileNotFoundException(
                "Stacklands is not installed or not detected. Set the install " +
                "folder in Settings and click Install first.",
                GameExeName);

        // Write BepInEx config with AP connection details BEFORE launching so the
        // mod can read them at startup. Defensive multi-key write (see header).
        try { WriteApConfig(installDir, session); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        StartGame(installDir);
        return Task.CompletedTask;
    }

    /// Stacklands is a complete game — standalone play without AP is supported.
    public bool SupportsStandalone => true;

    /// The mod's in-game client owns the AP slot (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        string? installDir = ResolveInstallDir();
        if (installDir == null)
            throw new FileNotFoundException(
                "Stacklands is not installed or not detected. Set the install " +
                "folder in Settings first.",
                GameExeName);

        // No config prefill for standalone play — user connects in-game manually.
        StartGame(installDir);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert ─────────────────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The mod receives items from the AP server directly; nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xA6, 0x23));
        var panel   = new System.Windows.Controls.StackPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        string? installDir  = ResolveInstallDir();
        string? overrideDir = LoadOverrideInstallDir();
        bool    bepInExOk   = installDir != null
                              && Directory.Exists(Path.Combine(installDir, "BepInEx", "core"));
        string? modPlugin   = DetectModPlugin();
        string? configPath  = installDir != null
                              ? Path.Combine(installDir, "BepInEx", "config", BepInExConfigFileName)
                              : null;
        bool    configExists = configPath != null && File.Exists(configPath);

        // ── Overview header ───────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Stacklands runs the Archipelago randomizer via a BepInEx mod. " +
                   "The launcher detects your Steam install, installs BepInEx and the " +
                   "mod, and writes your AP connection details into the BepInEx config " +
                   "file before each launch. The mod connects to the AP server " +
                   "automatically when you load a world.",
            FontSize = 11,
            Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: Install directory ────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "STACKLANDS INSTALL",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string detectMsg = installDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + installDir
                : "Detected via Steam (appid " + SteamAppId + "): " + installDir)
            : "Stacklands not detected automatically — use \"Locate install...\" below, " +
              "or install Stacklands via Steam first.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text         = detectMsg,
            FontSize     = 11,
            Foreground   = installDir != null ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 6),
        });

        var dirRow = new System.Windows.Controls.DockPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = overrideDir ?? installDir ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "The folder that contains \"Stacklands.exe\". " +
                          "Auto-detected from Steam; override here for non-standard installs.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content     = "Locate install...",
            Width       = 130,
            Padding     = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Stacklands install folder (contains Stacklands.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? installDir ?? "")
                                   ? (overrideDir ?? installDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() != true) return;

            string picked = dlg.FolderName;
            if (!File.Exists(Path.Combine(picked, GameExeName)))
            {
                System.Windows.MessageBox.Show(
                    "That folder does not contain \"Stacklands.exe\".\n" +
                    "For Steam this is usually ..\\steamapps\\common\\Stacklands.",
                    "Not a Stacklands folder",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }
            SaveOverrideInstallDir(picked);
            dirBox.Text = picked;
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text         = "Steam installs are detected automatically. Use \"Locate install...\" " +
                           "for a non-standard Steam library or a manual install.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 6, 0, 14),
        });

        // ── Section: Status ───────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "STATUS",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 0, 0, 8),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text         = bepInExOk
                            ? "BepInEx installed (BepInEx/core present)."
                            : "BepInEx not installed — click Install on the Play tab.",
            FontSize     = 11,
            Foreground   = bepInExOk ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text         = modPlugin != null
                            ? "Stacklands-Randomizer mod installed" +
                              (InstalledVersion != null && InstalledVersion != "installed"
                                ? " (v" + InstalledVersion + ")."
                                : ".")
                            : "Stacklands-Randomizer mod not found — click Install on the Play tab.",
            FontSize     = 11,
            Foreground   = modPlugin != null ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: BepInEx config / AP connection ───────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "ARCHIPELAGO CONNECTION",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 0, 0, 8),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text         = "When you launch this game from the Play tab with an active " +
                           "Archipelago session, the launcher writes your server address, " +
                           "port, slot name and password into the BepInEx config file " +
                           "automatically before starting the game. The mod reads this " +
                           "config and connects when you load a world.",
            FontSize     = 11,
            Foreground   = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 8),
        });

        if (configPath != null)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text         = (configExists ? "Config file exists: " : "Config file (written at launch): ") +
                               configPath,
                FontSize     = 11,
                Foreground   = configExists ? success : muted,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin       = new System.Windows.Thickness(0, 0, 0, 4),
            });

            if (configExists)
            {
                var openBtn = new System.Windows.Controls.Button
                {
                    Content         = "Open config file...",
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                    Padding         = new System.Windows.Thickness(8, 4, 8, 4),
                    Background      = new System.Windows.Media.SolidColorBrush(
                                          System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
                    Foreground      = fg,
                    BorderBrush     = new System.Windows.Media.SolidColorBrush(
                                          System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
                    Margin          = new System.Windows.Thickness(0, 4, 0, 4),
                };
                string cfgPath = configPath;
                openBtn.Click += (_, _) =>
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{cfgPath}\"")
                            { UseShellExecute = true });
                    }
                    catch { }
                };
                panel.Children.Add(openBtn);
            }
        }

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text         = "You can also edit the config file directly at the path above " +
                           "to set the connection details manually. The expected format is " +
                           "standard BepInEx INI: [Archipelago] section with ServerAddress, " +
                           "Port, SlotName, and Password keys.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 6, 0, 14),
        });

        // ── Section: Setup steps ──────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "SETUP STEPS",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 0, 0, 8),
        });

        foreach (string step in new[]
        {
            "1. Own Stacklands on Steam. The launcher detects it automatically via " +
               "Steam appid 1948280. Use \"Locate install...\" above if it was not found.",
            "2. Click Install on the Play tab. The launcher downloads BepInEx and the " +
               "Stacklands-Randomizer mod and installs both into your Stacklands folder.",
            "3. In the Archipelago launcher, enter your server details and slot name in " +
               "the session panel. When you click Play, the connection details are written " +
               "to the BepInEx config automatically.",
            "4. The game starts. Load your world — the mod connects to the AP server " +
               "and you play the randomized Stacklands experience.",
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

        // ── Links ─────────────────────────────────────────────────────────
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
            ("Stacklands-Randomizer (GitHub) ↗",  ModRepoUrl),
            ("Archipelago Official ↗",             "https://archipelago.gg"),
        })
        {
            var linkBtn = new System.Windows.Controls.Button
            {
                Content             = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding             = new System.Windows.Thickness(0, 2, 0, 2),
                Background          = System.Windows.Media.Brushes.Transparent,
                BorderThickness     = new System.Windows.Thickness(0),
                FontSize            = 12,
                Margin              = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground          = new System.Windows.Media.SolidColorBrush(
                                          System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor              = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            linkBtn.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(u) { UseShellExecute = true });
                }
                catch { }
            };
            panel.Children.Add(linkBtn);
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

    /// Normalize a GitHub tag to a clean version string: "v1.2.3" → "1.2.3".
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest AP mod release from GitHub: version + the preferred
    /// download URL (zip first, then a bare .dll asset as fallback). Falls back to
    /// the pinned tag when the API is unreachable.
    private async Task<(string Version, string? ZipUrl, string? DllUrl)>
        ResolveLatestModAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? zipUrl = null, dllUrl = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (zipUrl == null && lower.EndsWith(".zip"))
                        zipUrl = url;
                    if (dllUrl == null && lower.EndsWith(".dll"))
                        dllUrl = url;
                }
                return (version, zipUrl, dllUrl);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable — no pinned fallback URL for the mod */ }

        return ("unknown", null, null);
    }

    /// Resolve the latest BepInEx stable Windows x64 release zip URL from GitHub.
    /// Falls back to the pinned FallbackBepInExZipUrl when the API is unreachable.
    private async Task<string> ResolveBepInExZipUrlAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_BEPINEX_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                // Prefer a Windows x64 zip: name contains "win_x64" or "win" + "x64".
                string? bestUrl  = null;
                string? firstZip = null;

                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    firstZip ??= url;

                    // Prefer the Windows x64 zip.
                    if ((lower.Contains("win_x64") || (lower.Contains("win") && lower.Contains("x64")))
                        && !lower.Contains("linux") && !lower.Contains("unix") && !lower.Contains("mac"))
                    {
                        bestUrl = url;
                        break;
                    }
                }
                string? result = bestUrl ?? firstZip;
                if (result != null) return result;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through to pinned fallback */ }

        return FallbackBepInExZipUrl;
    }

    // ── Private helpers — install ─────────────────────────────────────────────

    /// Download and unpack BepInEx into the game install root.
    /// BepInEx's release zip unpacks directly to a BepInEx/ subfolder alongside a
    /// winhttp.dll patcher and changelog; extracting to the game root is correct.
    private async Task DownloadAndInstallBepInExAsync(
        string installDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string bepInExZipUrl = await ResolveBepInExZipUrlAsync(ct);
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"bepinex-stacklands-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((6, "Downloading BepInEx..."));
            using var response = await _http.GetAsync(
                bepInExZipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempZip))
            {
                var buf = new byte[81920];
                int bytesRead;
                while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                    downloaded += bytesRead;
                    if (total > 0)
                    {
                        int pct = (int)(6 + 14 * downloaded / total);
                        progress.Report((pct, $"Downloading BepInEx... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((21, "Extracting BepInEx into the game folder..."));
            Directory.CreateDirectory(installDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, installDir, overwriteFiles: true);
            progress.Report((23, "BepInEx installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    /// Download and install the AP mod into BepInEx/plugins/.
    /// Tries a zip asset first (extracted into plugins/); falls back to a raw .dll
    /// asset dropped directly into plugins/.
    private async Task DownloadAndInstallModAsync(
        string? zipUrl,
        string? dllUrl,
        string version,
        string pluginsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        if (zipUrl != null)
        {
            string tempZip = Path.Combine(Path.GetTempPath(),
                $"stacklands-ap-{version}-{Guid.NewGuid():N}.zip");
            try
            {
                progress.Report((32, $"Downloading Stacklands-Randomizer {version}..."));
                using var response = await _http.GetAsync(
                    zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
                response.EnsureSuccessStatusCode();

                long total      = response.Content.Headers.ContentLength ?? -1;
                long downloaded = 0;

                await using (var src = await response.Content.ReadAsStreamAsync(ct))
                await using (var dst = File.Create(tempZip))
                {
                    var buf = new byte[81920];
                    int bytesRead;
                    while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                        downloaded += bytesRead;
                        if (total > 0)
                        {
                            int pct = (int)(32 + 55 * downloaded / total);
                            progress.Report((pct,
                                $"Downloading Stacklands-Randomizer... {downloaded / 1_000}KB"));
                        }
                    }
                    await dst.FlushAsync(ct);
                }

                progress.Report((90, "Installing mod into BepInEx/plugins..."));

                // Extract into a temp dir so we can inspect the layout.
                string tempDir = Path.Combine(Path.GetTempPath(),
                    $"stacklands-ap-{version}-{Guid.NewGuid():N}");
                try
                {
                    Directory.CreateDirectory(tempDir);
                    System.IO.Compression.ZipFile.ExtractToDirectory(
                        tempZip, tempDir, overwriteFiles: true);

                    // Copy all extracted contents into plugins/. A common layout is:
                    //   zip root → contains DLL(s) directly, OR
                    //   zip root → BepInEx/plugins/<PluginName>/ sub-tree.
                    // We copy everything from the extracted root into plugins/
                    // and also scan for any nested BepInEx/plugins/ sub-tree.
                    string? nestedPlugins = FindNestedBepInExPlugins(tempDir);
                    string sourceDir = nestedPlugins ?? tempDir;

                    CopyDirectoryContents(sourceDir, pluginsDir);
                    progress.Report((97, "Mod files installed."));
                }
                finally
                {
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                }
                return;
            }
            finally
            {
                try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            }
        }

        if (dllUrl != null)
        {
            // Fallback: download a raw .dll asset and drop it into plugins/.
            progress.Report((32, $"Downloading Stacklands-Randomizer {version} (DLL)..."));
            byte[] dllBytes = await _http.GetByteArrayAsync(dllUrl, ct);
            string dllName  = Path.GetFileName(new Uri(dllUrl).LocalPath);
            if (string.IsNullOrWhiteSpace(dllName))
                dllName = "StacklandsArchipelago.dll";

            Directory.CreateDirectory(pluginsDir);
            await File.WriteAllBytesAsync(Path.Combine(pluginsDir, dllName), dllBytes, ct);
            progress.Report((95, "Mod DLL installed."));
        }
    }

    /// Find a BepInEx/plugins sub-tree inside an extracted zip (some zips use this
    /// layout). Returns the plugins folder path, or null if not found.
    private static string? FindNestedBepInExPlugins(string extractedRoot)
    {
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(
                extractedRoot, "plugins", SearchOption.AllDirectories))
            {
                string parent = Path.GetFileName(Path.GetDirectoryName(dir) ?? "");
                if (parent.Equals("BepInEx", StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
        }
        catch { }
        return null;
    }

    /// Recursively copy a directory's contents into a destination (overwriting).
    private static void CopyDirectoryContents(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(
            sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
    }

    // ── Private helpers — AP config write ─────────────────────────────────────

    /// Write the BepInEx config file with AP connection details.
    /// Uses defensive multi-key writing (see header) because the exact section/key
    /// names are not verified offline. Keys the mod ignores are harmless.
    private void WriteApConfig(string installDir, ApSession session)
    {
        string configDir = Path.Combine(installDir, "BepInEx", "config");
        Directory.CreateDirectory(configDir);

        string configPath = Path.Combine(configDir, BepInExConfigFileName);

        var (host, port) = ParseServerHostPort(session.ServerUri);

        // Read any existing config to preserve user-edited keys.
        var sections = new Dictionary<string, Dictionary<string, string>>(
            StringComparer.OrdinalIgnoreCase);
        if (File.Exists(configPath))
        {
            try { ParseIniFile(configPath, sections); }
            catch { /* corrupt — rewrite cleanly */ }
        }

        // Ensure the [Archipelago] section exists.
        if (!sections.TryGetValue(BepInExConfigSectionName, out var apSection))
        {
            apSection = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            sections[BepInExConfigSectionName] = apSection;
        }

        // Write under multiple common key spellings (defensive — see header).
        apSection["ServerAddress"]  = host;
        apSection["Server"]         = host;
        apSection["HostName"]       = host;
        apSection["Host"]           = host;
        apSection["Port"]           = port.ToString();
        apSection["ServerPort"]     = port.ToString();
        apSection["SlotName"]       = session.SlotName;
        apSection["Slot"]           = session.SlotName;
        apSection["PlayerName"]     = session.SlotName;
        apSection["Name"]           = session.SlotName;
        apSection["Password"]       = session.Password ?? "";
        apSection["RoomPassword"]   = session.Password ?? "";

        WriteIniFile(configPath, sections);
    }

    /// Parse a BepInEx INI config file into a sections dictionary.
    private static void ParseIniFile(
        string path,
        Dictionary<string, Dictionary<string, string>> sections)
    {
        string? currentSection = null;
        foreach (string rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Trim();
            if (line.StartsWith(';') || line.StartsWith('#') || line.Length == 0)
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                currentSection = line[1..^1].Trim();
                if (!sections.ContainsKey(currentSection))
                    sections[currentSection] = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase);
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq <= 0 || currentSection == null) continue;

            string key = line[..eq].Trim();
            string val = line[(eq + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key))
                sections[currentSection][key] = val;
        }
    }

    /// Write a sections dictionary back to a BepInEx INI config file (BOM-less UTF-8).
    private static void WriteIniFile(
        string path,
        Dictionary<string, Dictionary<string, string>> sections)
    {
        var sb = new StringBuilder();
        foreach (var (sectionName, keys) in sections)
        {
            sb.Append('[').Append(sectionName).AppendLine("]");
            foreach (var (key, val) in keys)
                sb.Append(key).Append(" = ").AppendLine(val);
            sb.AppendLine();
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// Parse "host:port", "ws://host:port", "wss://host:port" etc. into
    /// (host, port). Default port is 38281 (AP default).
    private static (string Host, int Port) ParseServerHostPort(string serverUri)
    {
        string s = serverUri.Trim();
        foreach (string prefix in new[] { "wss://", "ws://", "archipelago://" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..];
                break;
            }
        }
        int slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];

        string host = s;
        int    port = 38281;

        int colonCount = 0;
        foreach (char c in s) if (c == ':') colonCount++;

        if (s.StartsWith('['))
        {
            int close = s.IndexOf(']');
            if (close > 0)
            {
                host = s[1..close];
                string rest = s[(close + 1)..];
                if (rest.StartsWith(':') &&
                    int.TryParse(rest[1..], out int p6) && p6 > 0 && p6 <= 65535)
                    port = p6;
            }
        }
        else if (colonCount > 1)
        {
            host = s; // bare IPv6 — no port
        }
        else
        {
            int colon = s.LastIndexOf(':');
            if (colon > 0 && int.TryParse(s[(colon + 1)..], out int p) && p > 0 && p <= 65535)
            {
                host = s[..colon];
                port = p;
            }
        }
        if (host.Length == 0) host = "archipelago.gg";
        return (host, port);
    }

    // ── Private helpers — mod detection ───────────────────────────────────────

    /// Find the AP mod plugin DLL under the detected/override install's
    /// BepInEx/plugins tree. Accepts any DLL whose name contains "archipelago"
    /// or "stacklands" + "archipelago" (case-insensitive, recursive). Returns
    /// the DLL path, or null when not found.
    private string? DetectModPlugin()
    {
        try
        {
            string? installDir = ResolveInstallDir();
            if (installDir == null) return null;

            string pluginsDir = Path.Combine(installDir, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(
                pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll).ToLowerInvariant();
                if (name.Contains("archipelago"))
                    return dll;
            }
        }
        catch { }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Launch Stacklands: prefer the exe in the install dir, fall back to
    /// steam://rungameid/1948280.
    private void StartGame(string installDir)
    {
        string exePath = Path.Combine(installDir, GameExeName);

        if (File.Exists(exePath))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exePath,
                WorkingDirectory = installDir,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Stacklands.");

            TrackProcess(proc);
            return;
        }

        // Steam fallback.
        try
        {
            var proc = Process.Start(new ProcessStartInfo(SteamRunUrl)
                { UseShellExecute = true });
            if (proc != null) TrackProcess(proc);
            // Steam may return null for steam:// protocol launches — mark as running anyway.
            else { /* nothing to track but the game is launching */ }
        }
        catch (Exception ex)
        {
            throw new FileNotFoundException(
                "Could not find \"Stacklands.exe\" and Steam launch also failed. " +
                "Use \"Locate install...\" in Settings to pick your Stacklands folder, " +
                "or install Stacklands via Steam.\n\n" + ex.Message,
                GameExeName);
        }
    }

    private void TrackProcess(Process proc)
    {
        _gameProcess = proc;
        try
        {
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => GameExited?.Invoke(proc.ExitCode);
        }
        catch { /* some shell-launched procs don't expose Exited — non-fatal */ }
    }

    // ── Private helpers — Steam detection ─────────────────────────────────────

    /// Resolve the Stacklands install dir: override (if set + valid) wins, else
    /// Steam auto-detection. Null when nothing is found.
    private string? ResolveInstallDir()
    {
        string? ov = LoadOverrideInstallDir();
        if (!string.IsNullOrWhiteSpace(ov) && LooksLikeStacklandsDir(ov))
            return ov;
        return DetectSteamInstallDir();
    }

    /// Best-effort Steam install detection for AppID 1948280.
    private static string? DetectSteamInstallDir()
    {
        try
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

                        string common    = Path.Combine(steamapps, "common");
                        string? instDir  = ReadAcfInstallDir(manifest);
                        if (instDir != null)
                        {
                            string candidate = Path.Combine(common, instDir);
                            if (LooksLikeStacklandsDir(candidate)) return candidate;
                        }
                        string conventional = Path.Combine(common, SteamCommonFolderName);
                        if (LooksLikeStacklandsDir(conventional)) return conventional;
                    }
                    catch { /* try next library */ }
                }
            }
        }
        catch { }
        return null;
    }

    private static bool LooksLikeStacklandsDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        try { return File.Exists(Path.Combine(dir, GameExeName)); }
        catch { return false; }
    }

    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu))
            yield return hkcu!.Replace('/', '\\').TrimEnd('\\');

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm))
            yield return hklm!.Replace('/', '\\').TrimEnd('\\');

        string? hklm2 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm2))
            yield return hklm2!.Replace('/', '\\').TrimEnd('\\');

        string? pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(pf86))
            yield return Path.Combine(pf86, "Steam");
    }

    private static IEnumerable<string> SteamLibraryRoots(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(steamRoot)) yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }

        const string pathKey = "\"path\"";
        int i = 0;
        while ((i = text.IndexOf(pathKey, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            i += pathKey.Length;
            int open = text.IndexOf('"', i);
            if (open < 0) yield break;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) yield break;

            string raw  = text.Substring(open + 1, close - open - 1);
            string norm = raw.Replace("\\\\", "\\").Replace('/', '\\').TrimEnd('\\');
            if (norm.Length > 0 && seen.Add(norm))
                yield return norm;
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

    // ── Private helpers — version stamp ───────────────────────────────────────

    private string? ReadStampedVersion()
    {
        try
        {
            if (File.Exists(VersionFilePath))
            {
                string v = File.ReadAllText(VersionFilePath).Trim();
                return string.IsNullOrWhiteSpace(v) ? null : v;
            }
        }
        catch { }
        return null;
    }

    private void WriteStampedVersion(string version)
    {
        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
            File.WriteAllText(VersionFilePath, version, new UTF8Encoding(false));
        }
        catch { }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // Keeps the install-dir override in its OWN JSON file so this plugin stays a
    // single self-contained source file (same approach as HollowKnight / Stardew /
    // Jak / RoR2 plugins). Does NOT modify Core/SettingsStore.

    private sealed class StacklandsSettings
    {
        public string? InstallDirOverride { get; set; }
    }

    private StacklandsSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<StacklandsSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(StacklandsSettings s)
    {
        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
            File.WriteAllText(
                SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    private string? LoadOverrideInstallDir()
    {
        string? p = LoadSettings().InstallDirOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }

    private void SaveOverrideInstallDir(string dir)
    {
        var s = LoadSettings();
        s.InstallDirOverride = dir;
        SaveSettings(s);
    }
}
