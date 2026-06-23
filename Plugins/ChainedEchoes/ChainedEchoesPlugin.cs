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

namespace LauncherV2.Plugins.ChainedEchoes;

// ═══════════════════════════════════════════════════════════════════════════════
// ChainedEchoesPlugin — install / launch for "Chained Echoes" (Matthias Linda,
// 2022), a JRPG played through the Samupo/ChainedEchoesRandomizer BepInEx mod,
// which contains the in-game Archipelago Multiworld client. This is a NATIVE
// "ConnectsItself" integration: the mod speaks to the AP server directly — no
// emulator, no Lua bridge, no launcher-held ApClient on the slot.
//
// ── HONEST REALITY CHECK (2026-06-14, verified against the repo) ─────────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Chained Echoes (Steam appid 1229240), and Archipelago support is delivered
// as a BepInEx mod added on top.
//
//   * THE AP WORLD game string is "Chained Echoes" (verified against
//     ChainedEchoesAPWorld/__init__.py: `game = "Chained Echoes"`).
//     GameId here = "chained_echoes". The APWorld is a COMMUNITY world hosted at
//     Samupo/ChainedEchoesAPWorld — users must install it into their Archipelago
//     custom_worlds folder to generate a game.
//
//   * THE MOD repo is Samupo/ChainedEchoesRandomizer (verified live 2026-06-14).
//     The latest release (v1.1.0, 2026-06-01) ships ONE asset:
//     "CERandomizer.v1.1.0.zip". The zip contains a DLL placed into BepInEx/
//     plugins, and a "RandomizerOptions.txt" file placed alongside the game exe.
//
//   * FRAMEWORK: BepInEx 6.0.0-pre.2 (Unity IL2CPP, win-x64). This is NOT the
//     stable BepInEx 5 Mono build — Chained Echoes is an IL2CPP Unity game, so
//     the IL2CPP variant of BepInEx 6 pre-release is required. The mod README
//     explicitly names "BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2.zip".
//
//   * CONNECTION CONFIG: the mod reads "RandomizerOptions.txt" from the GAME ROOT
//     (same folder as the Chained Echoes exe). The file uses a simple key=value
//     format with these connection fields (verified against RandomizerOptions.cs):
//       ArchipelagoServer=localhost        (host, or host:port together)
//       ArchipelagoPort=38281
//       ArchipelagoUsername=Player
//       ArchipelagoPassword=
//     The parser strips ws://, wss://, trailing slashes. An "ArchipelagoServer"
//     value containing a colon causes the parser to split host+port from it.
//     BECAUSE a writeable config file IS documented, this plugin CAN pre-fill it
//     from the ApSession (server URI + slot name + password) before launching —
//     this is the honest, verifiable integration unlike BRC or Hollow Knight.
//
//   * "Installed" is judged by the presence of "CERandomizer.dll" under the
//     detected/override game install's BepInEx tree (the mod's assembly name is
//     CERandomizer per its csproj). RandomizerOptions.txt in the game root is
//     a secondary signal.
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. DETECT the Steam Chained Echoes install via the Windows registry + VDF.
//      A manual install-dir override is also supported and persisted in this
//      plugin's own sidecar (Games/ROMs/chained_echoes/chained_echoes_launcher.json).
//   2. INSTALL/UPDATE = (a) if BepInEx IL2CPP is not present, download
//      "BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2.zip" and extract it into the
//      game root; (b) download the mod zip (CERandomizer.*.zip) and extract the
//      DLL into BepInEx/plugins and RandomizerOptions.txt into the game root.
//   3. PRE-FILL "RandomizerOptions.txt" with the current ApSession credentials
//      before each Launch (server, port, slot name, password).
//   4. LAUNCH = run the game exe from the detected/override install; Steam fallback
//      via steam://rungameid/1229240 if the exe path cannot be resolved.
//      ConnectsItself = true (the mod owns the slot). SupportsStandalone = true.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ChainedEchoesPlugin : IGamePlugin
{
    // ── Constants — mod (Samupo/ChainedEchoesRandomizer, verified 2026-06-14) ─

    private const string MOD_OWNER = "Samupo";
    private const string MOD_REPO  = "ChainedEchoesRandomizer";
    private static readonly string ModRepoUrl =
        $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private static readonly string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private static readonly string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string ApWorldRepoUrl =
        "https://github.com/Samupo/ChainedEchoesAPWorld";
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Chained%20Echoes/setup/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // BepInEx 6.0.0-pre.2 Unity IL2CPP win-x64 — the separate mod-loader required
    // by Chained Echoes (an IL2CPP Unity game). Pinned to the exact version the
    // mod README specifies.
    private const string BepInExSite = "https://github.com/BepInEx/BepInEx/releases";
    private const string BepInExVersion = "6.0.0-pre.2";
    private const string BepInExZipName =
        "BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2.zip";
    private static readonly string BepInExZipUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v6.0.0-pre.2/" +
        BepInExZipName;

    // Steam — Chained Echoes appid 1229240 (verified 2026-06-14 against the
    // Steam store URL store.steampowered.com/app/1229240/Chained_Echoes/).
    private const string CeSteamAppId = "1229240";
    private static readonly string SteamRunUrl =
        $"steam://rungameid/{CeSteamAppId}";

    // The game executable and data folder (Unity: exe + <GameName>_Data).
    private const string CeExeName  = "Chained Echoes.exe";
    private const string CeDataDir  = "Chained Echoes_Data";

    // The standard Steam common install folder name (verified: usually "Chained Echoes").
    private const string SteamCommonFolderName = "Chained Echoes";

    // The mod's primary DLL in BepInEx/plugins. The csproj AssemblyName is
    // "CERandomizer", so the file on disk is "CERandomizer.dll".
    private const string ModPrimaryDll = "CERandomizer.dll";

    // The connection config file sits in the game ROOT (alongside the exe).
    private const string ConfigFileName = "RandomizerOptions.txt";

    // Pinned fallback when the GitHub API is unreachable.
    private const string FallbackVersion = "1.1.0";
    private const string FallbackZipName = "CERandomizer.v1.1.0.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "chained_echoes";
    public string DisplayName => "Chained Echoes";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against
    /// ChainedEchoesAPWorld/__init__.py (`game = "Chained Echoes"`).
    public string ApWorldName => "Chained Echoes";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "chained_echoes.png");

    public string ThemeAccentColor => "#3A8FC7";   // blue-sky JRPG palette
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Chained Echoes is a 2022 JRPG by Matthias Linda, inspired by 16-bit " +
        "classics, played through the ChainedEchoesRandomizer mod by Samupo — " +
        "which adds an Archipelago client directly into the game so it connects to " +
        "the multiworld itself. Chests, cores, emblems, and mechs are shuffled " +
        "across the multiworld. You bring your own copy of Chained Echoes (owned " +
        "on Steam); the integration runs on BepInEx 6 IL2CPP, the Unity mod " +
        "loader. The launcher detects your Steam install, can stage BepInEx and " +
        "the mod into it, and pre-fills your server credentials into the mod's " +
        "config file before each play session so you do not need to edit it by hand.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the mod DLL ("CERandomizer.dll") is present under the
    /// detected/override game install's BepInEx tree.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "ChainedEchoes");

    /// This plugin's own sidecar — per the brief, at Games/ROMs/chained_echoes/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "chained_echoes_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The mod relays items/checks/goal to the AP server directly; the launcher
    // relays nothing. These exist for interface compatibility (ConnectsItself = true).
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
        progress.Report((2, "Locating your Chained Echoes installation..."));
        string? ceDir = ResolveCeDir();
        if (ceDir == null)
            throw new InvalidOperationException(
                "Could not find a Chained Echoes installation. Open this game's " +
                "Settings and pick your Chained Echoes folder (the one containing " +
                "\"Chained Echoes.exe\"), or install Chained Echoes via Steam first. " +
                "The Archipelago mod is added on top of your own copy of the game.");

        progress.Report((6, "Checking the latest ChainedEchoesRandomizer release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the ChainedEchoesRandomizer mod download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                ModRepoUrl + "/releases. The mod repo is " + ModRepoUrl + ".");

        // 1. Ensure BepInEx 6.0.0-pre.2 IL2CPP is present — it is a SEPARATE
        //    portable prerequisite (zip extracted into the game root). BepInEx 6
        //    for IL2CPP drops a "BepInEx" folder and a "winhttp.dll" proxy at root.
        string pluginsDir = Path.Combine(ceDir, "BepInEx", "plugins");
        if (!BepInExPresent(ceDir))
        {
            progress.Report((10, "Staging BepInEx 6.0.0-pre.2 (IL2CPP) into your Chained Echoes folder..."));
            await DownloadAndExtractZipToDirAsync(
                BepInExZipUrl, $"ce-bepinex-{BepInExVersion}", ceDir, 10, 40, progress, ct);
        }
        else
        {
            progress.Report((40, "BepInEx already present — keeping your existing install."));
        }
        Directory.CreateDirectory(pluginsDir);

        // 2. Download + extract the mod zip (CERandomizer.*.zip). From the README:
        //    the .dll goes into BepInEx/plugins, and the .txt config goes into the
        //    game root alongside the exe. The extractor handles both.
        await DownloadAndExtractModAsync(zipUrl, version, ceDir, pluginsDir,
            42, 90, progress, ct);

        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepOk = BepInExPresent(ceDir);
        bool modOk = FindInstalledModDll() != null;
        bool cfgOk = File.Exists(Path.Combine(ceDir, ConfigFileName));
        progress.Report((100,
            $"Staged ChainedEchoesRandomizer {version}" +
            (bepOk ? " (BepInEx present" : " (BepInEx NOT detected") +
            (modOk ? ", CERandomizer.dll present" : ", CERandomizer.dll NOT found") +
            (cfgOk ? ", RandomizerOptions.txt present)." : ", config NOT found).") +
            " To play: click Play on this tile. The launcher will pre-fill your " +
            "server credentials in RandomizerOptions.txt before launching. You " +
            "may also need to install the ChainedEchoes APWorld into your " +
            "Archipelago custom_worlds folder to generate a game (see the AP " +
            "World repo link in Settings)."));
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
        // Pre-fill RandomizerOptions.txt with the AP session credentials before
        // launching. The mod reads this file from the game root at startup.
        // ConnectsItself = true: the launcher does NOT hold its own ApClient.
        string? ceDir = ResolveCeDir();
        if (ceDir != null)
            WriteConnectionConfig(ceDir, session);

        StartCe();
        return Task.CompletedTask;
    }

    public bool ConnectsItself    => true;
    public bool SupportsStandalone => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // Standalone launch — do not overwrite the config (may be the user's own).
        StartCe();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself) ───────────────────────────────────

    public Task ReceiveItemsAsync(
        ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;  // mod receives items from the AP server directly

    public void OnApStateChanged(ApConnectionState state) { }   // mod shows its own status

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Intro note ────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Chained Echoes is your own game (Steam) with the " +
                   "ChainedEchoesRandomizer mod added on top via BepInEx 6 IL2CPP. " +
                   "The launcher detects your Steam install, can stage BepInEx and " +
                   "the mod into it, and pre-fills RandomizerOptions.txt with your " +
                   "server credentials before each play session. You still need to " +
                   "install the ChainedEchoes APWorld into your Archipelago " +
                   "custom_worlds folder to generate a game.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: install detection ────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CHAINED ECHOES INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? ceDir       = ResolveCeDir();
        string? overrideDir = LoadOverrideDir();
        string detectMsg = ceDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + ceDir
                : "Detected Steam install: " + ceDir)
            : "Chained Echoes not detected. Pick your install folder below, " +
              "or install Chained Echoes via Steam first.";

        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = ceDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        bool bepOk = ceDir != null && BepInExPresent(ceDir);
        panel.Children.Add(new TextBlock
        {
            Text = ceDir == null ? "" :
                   (bepOk
                       ? "BepInEx 6 IL2CPP found in your Chained Echoes folder."
                       : "BepInEx not found yet. The Install button on the Play tab " +
                         "stages BepInEx 6.0.0-pre.2 IL2CPP for you, or download it " +
                         "from the BepInEx releases (link below). Chained Echoes " +
                         "requires the IL2CPP variant, NOT BepInEx 5."),
            FontSize = 11, Foreground = bepOk ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        string? modDll = FindInstalledModDll();
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                    ? "CERandomizer.dll found: " + modDll
                    : "CERandomizer.dll not found in BepInEx\\plugins yet " +
                      "(use Install on the Play tab).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        bool cfgOk = ceDir != null &&
                     File.Exists(Path.Combine(ceDir, ConfigFileName));
        panel.Children.Add(new TextBlock
        {
            Text = ceDir == null ? "" :
                   (cfgOk
                       ? ConfigFileName + " found in game root (will be " +
                         "pre-filled with your session credentials on Play)."
                       : ConfigFileName + " not found yet (created by Install " +
                         "from the Play tab, or on first launch)."),
            FontSize = 11, Foreground = cfgOk ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // Folder picker
        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? ceDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Chained Echoes install folder (contains " +
                          "\"Chained Echoes.exe\"). Detected from Steam automatically.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Chained Echoes install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? ceDir ?? "")
                                   ? (overrideDir ?? ceDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad,
                        "Not a Chained Echoes folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeCeDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeCeDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 1229240). " +
                   "Use this picker for a non-standard Steam library or another store.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection pre-fill ──────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTION (pre-filled before each Play)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "When you click Play on this tile (from the Archipelago session " +
                   "flow), the launcher writes your server address, slot name, and " +
                   "password into RandomizerOptions.txt in your Chained Echoes " +
                   "folder before starting the game. The mod reads that file at " +
                   "startup and connects automatically. For standalone (non-AP) " +
                   "play the config is left as-is.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // Show the current config contents if the file exists
        if (cfgOk)
        {
            try
            {
                string cfgPath = Path.Combine(ceDir!, ConfigFileName);
                string cfgText = File.ReadAllText(cfgPath);
                // Show only the four connection lines (hide seed / gameplay options)
                var connLines = cfgText
                    .Split('\n')
                    .Where(l => l.StartsWith("ArchipelagoServer=", StringComparison.OrdinalIgnoreCase)
                             || l.StartsWith("ArchipelagoPort=",   StringComparison.OrdinalIgnoreCase)
                             || l.StartsWith("ArchipelagoUsername=", StringComparison.OrdinalIgnoreCase)
                             || l.StartsWith("ArchipelagoPassword=", StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (connLines.Length > 0)
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = "Current connection settings in " + ConfigFileName + ":",
                        FontSize = 11, Foreground = muted,
                        Margin = new Thickness(0, 0, 0, 4),
                    });
                    panel.Children.Add(new System.Windows.Controls.TextBox
                    {
                        Text       = string.Join("\n", connLines),
                        IsReadOnly = true, FontSize = 11,
                        FontFamily  = new System.Windows.Media.FontFamily("Consolas"),
                        Foreground  = fg,
                        Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
                        BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
                        Padding     = new Thickness(6, 4, 6, 4),
                        Margin      = new Thickness(0, 0, 0, 12),
                        TextWrapping = TextWrapping.Wrap,
                    });
                }
            }
            catch { /* non-fatal */ }
        }

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Chained Echoes (on Steam). Install it if you have not. Use the " +
               "folder picker above if it was not detected automatically.",
            "2. Install the ChainedEchoes APWorld into your Archipelago custom_worlds " +
               "folder (download chainedechoes.apworld from the AP World repo, link " +
               "below). You need this to generate a Chained Echoes multiworld game.",
            "3. On the Play tab, click Install / Update to stage BepInEx 6 IL2CPP " +
               "and the ChainedEchoesRandomizer mod into your game folder. The " +
               "launcher stages all required files automatically.",
            "4. To play: start a multiworld session in the launcher, select Chained " +
               "Echoes, and click Play. The launcher pre-fills RandomizerOptions.txt " +
               "with your server credentials and starts the game. The mod connects " +
               "automatically.",
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
            Text = "LINKS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("ChainedEchoesRandomizer (GitHub) ↗",  ModRepoUrl),
            ("ChainedEchoes APWorld (GitHub) ↗",    ApWorldRepoUrl),
            ("Chained Echoes Setup Guide (AP) ↗",   SetupGuideUrl),
            ("BepInEx releases ↗",                  BepInExSite),
            ("Archipelago Official ↗",              ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding         = new Thickness(0, 2, 0, 2),
                Background      = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize        = 12, Margin = new Thickness(0, 0, 0, 4),
                Foreground      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor          = System.Windows.Input.Cursors.Hand,
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
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_URL, ct);
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

    // ── Validate a manually-selected install folder ───────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Chained Echoes install folder.";
        if (LooksLikeCeDir(folder)) return null;
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeCeDir(nested)) return null;
        }
        catch { }
        return "That does not look like a Chained Echoes installation. Pick the " +
               "folder that contains \"Chained Echoes.exe\" (for Steam this is " +
               @"usually ...\steamapps\common\Chained Echoes).";
    }

    // ── Private helpers — connection config pre-fill ──────────────────────────

    /// Write RandomizerOptions.txt into the game root with the session credentials.
    /// The mod's parser reads this at startup (connection fields only — gameplay
    /// options are loaded from AP slot data and are not written here).
    ///
    /// Config format (key=value, one per line, verified against RandomizerOptions.cs):
    ///   ArchipelagoServer=<host>       (do NOT include port here — use ArchipelagoPort)
    ///   ArchipelagoPort=<port>
    ///   ArchipelagoUsername=<slot>
    ///   ArchipelagoPassword=<password>
    ///
    /// Note: the parser's ApplyServerValue will split "host:port" from
    /// ArchipelagoServer if a colon is present. We write them separately to be
    /// unambiguous. If the file already exists we merge (preserve gameplay options).
    private static void WriteConnectionConfig(string ceDir, ApSession session)
    {
        try
        {
            string cfgPath = Path.Combine(ceDir, ConfigFileName);

            // Parse the server URI — strip ws:// / wss:// scheme, split host:port.
            string serverUri = session.ServerUri.Trim();
            if (serverUri.StartsWith("ws://",  StringComparison.OrdinalIgnoreCase))
                serverUri = serverUri[5..];
            else if (serverUri.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
                serverUri = serverUri[6..];
            serverUri = serverUri.TrimEnd('/');

            string host = serverUri;
            int    port = 38281;
            int    col  = serverUri.LastIndexOf(':');
            if (col > 0 && col < serverUri.Length - 1 &&
                int.TryParse(serverUri[(col + 1)..], out int parsedPort))
            {
                host = serverUri[..col];
                port = parsedPort;
            }

            // Read existing lines so we preserve gameplay / seed options.
            List<string> lines = new();
            if (File.Exists(cfgPath))
            {
                try { lines.AddRange(File.ReadAllLines(cfgPath)); }
                catch { lines.Clear(); }
            }

            // Remove old connection lines.
            lines.RemoveAll(l =>
                l.StartsWith("ArchipelagoServer=",  StringComparison.OrdinalIgnoreCase) ||
                l.StartsWith("ArchipelagoPort=",    StringComparison.OrdinalIgnoreCase) ||
                l.StartsWith("ArchipelagoUsername=", StringComparison.OrdinalIgnoreCase) ||
                l.StartsWith("ArchipelagoPassword=", StringComparison.OrdinalIgnoreCase));

            // Prepend the new connection lines.
            lines.InsertRange(0, new[]
            {
                $"ArchipelagoServer={host}",
                $"ArchipelagoPort={port}",
                $"ArchipelagoUsername={session.SlotName}",
                $"ArchipelagoPassword={session.Password ?? ""}",
            });

            File.WriteAllText(cfgPath,
                string.Join(Environment.NewLine, lines) + Environment.NewLine,
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — game will use its own defaults */ }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartCe()
    {
        string? ceDir = ResolveCeDir();
        string? exe   = ceDir != null ? Path.Combine(ceDir, CeExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = ceDir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException(
                     "Failed to start Chained Echoes.");

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
            catch { }
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
            catch { }
        }

        throw new FileNotFoundException(
            "Could not find \"Chained Echoes.exe\". Open this game's Settings and " +
            "pick your Chained Echoes install folder, or install Chained Echoes via Steam.",
            CeExeName);
    }

    // ── Private helpers — release resolution ─────────────────────────────────

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + zip download URL. Looks for a
    /// "CERandomizer*.zip" asset; falls back to the pinned v1.1.0 URL.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(
        CancellationToken ct)
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
                        (lower.Contains("cerandomizer") || lower.Contains("randomizer")))
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

    // ── Private helpers — Steam / CE detection ────────────────────────────────

    private string? ResolveCeDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeCeDir(ov)) return ov;
        try { return DetectSteamCeDir(); }
        catch { return null; }
    }

    private static bool LooksLikeCeDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, CeExeName)))      return true;
            if (Directory.Exists(Path.Combine(dir, CeDataDir))) return true;
            return false;
        }
        catch { return false; }
    }

    private static bool BepInExPresent(string ceDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ceDir) || !Directory.Exists(ceDir)) return false;
            if (Directory.Exists(Path.Combine(ceDir, "BepInEx"))) return true;
            if (File.Exists(Path.Combine(ceDir, "winhttp.dll")))  return true;
            return false;
        }
        catch { return false; }
    }

    private static string? DetectSteamCeDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{CeSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common    = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeCeDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeCeDir(conventional)) return conventional;
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
            int open  = text.IndexOf('"', i);
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
            string text  = File.ReadAllText(acfPath);
            const string key = "\"installdir\"";
            int i    = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i        += key.Length;
            int open  = text.IndexOf('"', i);
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

    private string? FindInstalledModDll()
    {
        try
        {
            string? ce = ResolveCeDir();
            if (ce == null) return null;
            string bepInExDir = Path.Combine(ce, "BepInEx");
            if (!Directory.Exists(bepInExDir)) return null;
            foreach (string dll in Directory.EnumerateFiles(
                bepInExDir, "*.dll", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dll).Equals(
                    ModPrimaryDll, StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { }
        return null;
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    /// Download + extract the mod zip. Files with ".txt" extension go to the game
    /// root (alongside the exe); ".dll" files go to BepInEx/plugins. Any other
    /// layout is handled defensively: if CERandomizer.dll does not land in plugins
    /// after extraction, flatten a single wrapper sub-folder.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string ceDir,
        string pluginsDir,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"cerandomizer-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"cerandomizer-extract-{Guid.NewGuid():N}");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 6 / 10;
            await DownloadFileAsync(zipUrl, tempZip,
                $"Downloading ChainedEchoesRandomizer {version}...",
                pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Extracting mod files..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, tempExtract, overwriteFiles: true);

            // Distribute files: .txt → game root; .dll → plugins.
            // (From the README: the .txt goes alongside the .exe, the .dll goes
            // in BepInEx/plugins.)
            bool dllFound = false;
            foreach (string src in Directory.EnumerateFiles(
                tempExtract, "*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(src).ToLowerInvariant();
                string dst;
                if (ext == ".dll")
                {
                    dst = Path.Combine(pluginsDir, Path.GetFileName(src));
                    dllFound = true;
                }
                else if (ext == ".txt")
                {
                    dst = Path.Combine(ceDir, Path.GetFileName(src));
                }
                else
                {
                    // Unknown file types: place next to the DLL in plugins.
                    dst = Path.Combine(pluginsDir, Path.GetFileName(src));
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: true);
            }

            // Defensive: if CERandomizer.dll still is not in plugins, look deeper.
            if (!dllFound || !File.Exists(Path.Combine(pluginsDir, ModPrimaryDll)))
            {
                string? srcDir = FindDirContaining(tempExtract, ModPrimaryDll);
                if (srcDir != null)
                {
                    foreach (string f in Directory.EnumerateFiles(srcDir, "*.dll"))
                    {
                        string ddst = Path.Combine(pluginsDir, Path.GetFileName(f));
                        File.Copy(f, ddst, overwrite: true);
                    }
                }
            }

            progress.Report((pctEnd, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, true); }
            catch { }
        }
    }

    /// Find the directory under <root> that directly contains <fileName>.
    private static string? FindDirContaining(string root, string fileName)
    {
        try
        {
            foreach (string f in Directory.EnumerateFiles(
                root, fileName, SearchOption.AllDirectories))
                return Path.GetDirectoryName(f);
        }
        catch { }
        return null;
    }

    /// Download + extract a portable zip straight into a target directory.
    private async Task DownloadAndExtractZipToDirAsync(
        string zipUrl,
        string tag,
        string targetDir,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"{tag}-{Guid.NewGuid():N}.zip");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 8 / 10;
            await DownloadFileAsync(zipUrl, tempZip,
                "Downloading BepInEx...", pctStart, dlEnd, progress, ct);
            progress.Report((dlEnd, "Extracting BepInEx into your Chained Echoes folder..."));
            Directory.CreateDirectory(targetDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, targetDir, overwriteFiles: true);
            progress.Report((pctEnd, "BepInEx staged."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    /// Stream a URL to a file with progress in [pctStart, pctEnd].
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
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
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

    private sealed class CeSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private CeSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<CeSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(CeSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions
                    { WriteIndented = true }),
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
        var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s);
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings(); s.ModVersion = v; SaveSettings(s);
    }
}
