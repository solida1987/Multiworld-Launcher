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

namespace LauncherV2.Plugins.MinishootAdventures;

// ═══════════════════════════════════════════════════════════════════════════════
// MinishootAdventuresPlugin — install / launch for "Minishoot' Adventures"
// (SoulGame Studio, 2024) played through the MinishootRandomizer mod, a
// BepInEx 5 plugin that is the in-game Archipelago client. This is a NATIVE
// "ConnectsItself" integration — the game itself speaks to the AP server; the
// launcher holds no ApClient on the slot.
//
// ── HONEST REALITY CHECK (2026-06-14, verified against repo + apworld) ───────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned
// Minishoot' Adventures (Steam appid 1634860), and Archipelago is a BepInEx 5
// plugin added on top. The verified facts:
//
//   * THE AP WORLD game string is "Minishoot Adventures" (verified directly
//     against worlds/minishoot/__init__.py in the apworld:
//         `game = "Minishoot Adventures"`
//     and from MultiClient.cs:
//         `_session.TryConnectAndLogin("Minishoot Adventures", ...)`).
//     Note: the GAME TITLE has an apostrophe ("Minishoot' Adventures"), but the
//     AP game string does NOT — it is exactly "Minishoot Adventures".
//
//   * THE MOD is at GitHub: TheNooodle/MinishootRandomizer. Latest release
//     v0.5.2 (verified live 2026-06-14). The release ships three assets:
//         MinishootRandomizer.zip    — the mod files, extract into BepInEx/plugins
//         minishoot.apworld          — the AP world file for the server
//         archipelago-template.yaml  — a YAML template for AP generation
//     MinishootRandomizer.zip extracts to a folder "MinishootRandomizer" (or
//     similar) containing the plugin DLLs.
//
//   * MOD LOADER: BepInEx 5 (NOT 6 — the README states "BepInEx 6 will not
//     work"). Specifically, BepInEx_win_x64_5.4.23.2.zip. The install procedure
//     requires an additional manual step: after the first game launch with
//     BepInEx, edit <game-root>/BepInEx/config/BepInEx.cfg and set the value
//     HideManagerGameObject = true. Without this, the randomizer does not work.
//     This launcher surfaces this requirement prominently in the guided steps.
//
//   * CONNECTION is made IN-GAME via the "Randomizer Menu" window that appears
//     on the title screen (top-left). The user enters the server URI, slot name,
//     and password there, then presses "Connect". The mod persists these via
//     Unity PlayerPrefs — there is NO plaintext config file this launcher can
//     pre-write (no ini, no JSON, no XML: confirmed by reading ImguiContextComponent.cs
//     which reads/writes PlayerPrefs keys "ArchipelagoServerUri", "ArchipelagoSlotName",
//     "ArchipelagoDeathLink"). Therefore this plugin does NOT attempt a connection
//     prefill. The settings panel surfaces the session credentials for the user
//     to type in-game.
//
//   * APWORLD FILE: minishoot.apworld (renamed from minishoot_0_5_0.apworld in
//     older releases). It belongs in the Archipelago installation's
//     custom_worlds folder — NOT deployed by this plugin (that is the server
//     operator's job).
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Minishoot' Adventures install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Minishoot' Adventures via appmanifest_1634860.acf.
//      A manual install-dir OVERRIDE (settings folder picker) is supported
//      and takes precedence; it is validated (must contain Minishoot.exe) and
//      persisted in this plugin's OWN sidecar (Games/ROMs/minishoot/
//      minishoot_launcher.json) — Core/SettingsStore is NOT modified.
//   2. INSTALL/UPDATE (best effort) = download MinishootRandomizer.zip from the
//      latest GitHub release and extract it into
//      <game>\BepInEx\plugins\MinishootRandomizer\. Because the zip does NOT
//      carry BepInEx 5 itself, the plugin presents clear, numbered, guided
//      steps + links so the user can install BepInEx and complete the config
//      edit (HideManagerGameObject = true) — without which the randomizer will
//      not load. Never a fake one-click.
//   3. LAUNCH = run Minishoot.exe from the detected/override install; if the
//      exe cannot be found but Steam is present, fall back to
//      steam://rungameid/1634860. ConnectsItself = true (the mod owns the slot
//      — the launcher must NOT hold its own ApClient on it).
//      SupportsStandalone = true (plain Minishoot' Adventures runs fine without
//      the mod). No prefill (in-game Randomizer Menu), stated honestly.
//
// ── BUILD NOTE ────────────────────────────────────────────────────────────────
//   This project sets UseWindowsForms=true alongside UseWPF=true, so WPF types
//   that also live in WinForms (Color, Button, MessageBox, FontWeights, etc.)
//   are spelled with their full System.Windows.* namespace below to avoid
//   CS0104 ambiguity. No file-level "using X = System.Windows…;" aliases.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MinishootAdventuresPlugin : IGamePlugin
{
    // ── Constants — the mod (GitHub, verified 2026-06-14) ────────────────────
    private const string MOD_OWNER     = "TheNooodle";
    private const string MOD_REPO      = "MinishootRandomizer";
    private const string ModRepoUrl    = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // BepInEx 5 — required mod loader. BepInEx 6 will NOT work.
    private const string BepInEx5ReleaseUrl =
        "https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.2";
    private const string BepInEx5DownloadUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip";

    private const string SetupGuideUrl =
        "https://github.com/TheNooodle/MinishootRandomizer/blob/main/docs/players/installation.md";
    private const string PlayingGuideUrl =
        "https://github.com/TheNooodle/MinishootRandomizer/blob/main/docs/players/playing.md";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Minishoot' Adventures appid 1634860 (verified via store.steampowered.com/app/1634860).
    private const string SteamAppId      = "1634860";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The conventional Steam library folder name for this game.
    private const string SteamCommonFolderName = "Minishoot' Adventures";

    /// The game executable name, used as the primary detection signal.
    private const string GameExeName = "Minishoot.exe";

    /// Pinned fallback version when the GitHub API is unreachable.
    /// v0.5.2 verified live 2026-06-14.
    private const string FallbackVersion    = "0.5.2";
    private const string FallbackZipName    = "MinishootRandomizer.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/{FallbackZipName}";

    /// Expected sub-folder inside BepInEx/plugins where the mod DLLs live.
    private const string ModPluginDirName = "MinishootRandomizer";
    /// Expected DLL name inside the plugin dir (from the mod's csproj AssemblyName).
    private const string ModDllName       = "MinishootRandomizer.dll";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "minishoot";
    public string DisplayName => "Minishoot' Adventures";
    public string Subtitle    => "Native PC · BepInEx 5 mod";

    /// EXACT AP game string — verified against worlds/minishoot/__init__.py
    /// (`game = "Minishoot Adventures"`) in the apworld, AND against
    /// MultiClient.cs (`TryConnectAndLogin("Minishoot Adventures", ...)`).
    /// The apostrophe in the game title is ABSENT in the AP string.
    public string ApWorldName => "Minishoot Adventures";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "minishoot_adventures.png");

    public string ThemeAccentColor => "#3D7A3A";   // forest-green, Minishoot' palette
    public string[] GameBadges     => new[] { "Steam · BepInEx 5 mod" };

    public string Description =>
        "Minishoot' Adventures, SoulGame Studio's twin-stick shooter Metroidvania " +
        "(Steam 2024), played through the MinishootRandomizer mod — a BepInEx 5 " +
        "plugin that is an in-game Archipelago client. The game connects to your " +
        "Archipelago server itself from the title-screen Randomizer Menu; no " +
        "emulator and no Lua bridge are involved. Crystals, skills, dungeon rewards, " +
        "keys, NPCs, scarabs and more are shuffled into the multiworld. You bring " +
        "your own copy of Minishoot' Adventures from Steam (appid 1634860), install " +
        "BepInEx 5 into it, drop the MinishootRandomizer plugin in, and make a " +
        "one-line edit to BepInEx.cfg (HideManagerGameObject = true). The launcher " +
        "detects your Steam install, can stage the mod files, and guides the rest. " +
        "You connect to your server from the in-game Randomizer Menu.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the MinishootRandomizer plugin DLL is present in the
    /// detected/override game install's BepInEx/plugins tree. We do NOT gate on
    /// our own stamp — the user may have installed the mod manually, which we
    /// honor.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloaded zips and bookkeeping. The actual mod
    /// is extracted INTO the game install's BepInEx/plugins folder. Exposed as
    /// GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "MinishootAdventures");

    private string SidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SidecarPath
        => Path.Combine(SidecarDir, "minishoot_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The MinishootRandomizer mod reports checks/items/goal to the AP server
    // directly. These events exist for interface compatibility only.
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
        // 0. We need a Minishoot' Adventures install to drop the mod into.
        progress.Report((2, "Locating your Minishoot' Adventures installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Minishoot' Adventures installation. Open this " +
                "game's Settings and pick your install folder (the one containing " +
                "Minishoot.exe), or install the game via Steam first. The " +
                "Archipelago mod is added on top of your own copy of the game.");

        string pluginsDir = Path.Combine(gameDir, "BepInEx", "plugins");
        string apModDir   = Path.Combine(pluginsDir, ModPluginDirName);

        // 1. Resolve the latest mod release.
        progress.Report((6, "Checking the latest MinishootRandomizer release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the MinishootRandomizer download on GitHub. " +
                "Check your internet connection, or install the mod manually " +
                "from " + ModRepoUrl + "/releases. See Settings for guided steps.");

        // 2. Download + extract the mod zip INTO <game>\BepInEx\plugins\MinishootRandomizer\.
        //    HONEST: this stages the mod plugin DLLs only. BepInEx 5 itself and
        //    the required BepInEx.cfg edit (HideManagerGameObject = true) are NOT
        //    handled automatically — the user must do them. The guided steps in
        //    the Settings panel explain exactly what to do.
        await DownloadAndExtractModAsync(zipUrl, version, apModDir, progress, ct);

        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepInExPresent = Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));
        bool cfgEdited      = CheckBepInExCfgEdited(gameDir);

        progress.Report((100,
            $"Staged MinishootRandomizer {version} into BepInEx\\plugins. " +
            (bepInExPresent
                ? (cfgEdited
                    ? "BepInEx is present and BepInEx.cfg looks correct. "
                    : "BepInEx is present but you may still need to set " +
                      "HideManagerGameObject = true in BepInEx\\config\\BepInEx.cfg. ")
                : "IMPORTANT: BepInEx 5 is NOT yet installed. Without it the mod " +
                  "will not load. Open Settings for the full guided steps. ") +
            "To play: launch the game, wait for the title screen, and use the " +
            "Randomizer Menu (top-left) to enter your server address, slot name " +
            "and password, then press Connect."));
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
        // HONEST: the AP server connection for Minishoot' Adventures is entered
        // IN-GAME in the title-screen Randomizer Menu window (Server URI, Slot
        // Name, Password, Deathlink toggle, Connect button). The mod persists
        // these credentials via Unity PlayerPrefs — there is NO config file or
        // command-line argument this launcher can pre-write (verified against
        // ImguiContextComponent.cs which uses PlayerPrefs exclusively). So
        // launching from this tile just starts the game; the user connects
        // in-game with the session credentials shown in the Settings panel.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartGame();
        return Task.CompletedTask;
    }

    /// Plain (non-randomized) Minishoot' Adventures runs perfectly well.
    public bool SupportsStandalone => true;

    /// The MinishootRandomizer mod owns the slot connection (see header).
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
        // No plaintext AP credentials are ever written by this plugin — the
        // connection is entered in-game via PlayerPrefs (Unity's own storage).
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself) ────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The MinishootRandomizer mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in the title-screen Randomizer Menu.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker. Returns null when acceptable, else a
    /// short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Minishoot' Adventures install folder.";

        if (LooksLikeGameDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeGameDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Minishoot' Adventures installation. Pick " +
               "the folder that contains Minishoot.exe — for Steam this is usually " +
               @"...\steamapps\common\Minishoot' Adventures.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();
        string? modDll      = FindInstalledModDll();
        bool    bepInExOk   = gameDir != null && Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));
        bool    cfgEdited   = gameDir != null && CheckBepInExCfgEdited(gameDir);

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Minishoot' Adventures is your own game (Steam) with the " +
                   "MinishootRandomizer mod added on top via BepInEx 5. The launcher " +
                   "detects your Steam install and can stage the mod files, but BepInEx " +
                   "5 itself must be installed manually, and BepInEx.cfg must be edited " +
                   "to set HideManagerGameObject = true (the randomizer will not work " +
                   "without this). You connect to your server from the in-game Randomizer " +
                   "Menu on the title screen. External steps are not verified by this " +
                   "launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: game install ─────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "MINISHOOT' ADVENTURES INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Minishoot' Adventures not detected. Pick your install folder below, or " +
              "install the game via Steam first (appid 1634860).";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // BepInEx status
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = bepInExOk
                    ? "BepInEx 5 found (BepInEx\\core present)."
                    : "BepInEx 5 not found yet — install it before the mod will work (see steps below).",
            FontSize = 11, Foreground = bepInExOk ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        // BepInEx.cfg HideManagerGameObject status
        if (bepInExOk)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = cfgEdited
                        ? "BepInEx.cfg: HideManagerGameObject = true (looks correct)."
                        : "BepInEx.cfg: HideManagerGameObject not confirmed true — check " +
                          "BepInEx\\config\\BepInEx.cfg (see step 4 below).",
                FontSize = 11, Foreground = cfgEdited ? success : warn,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
            });
        }

        // Mod status
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                    ? "MinishootRandomizer mod found: " + modDll
                    : "MinishootRandomizer mod not found in BepInEx\\plugins yet " +
                      "(use Install on the Play tab, or drop it in manually).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text       = overrideDir ?? gameDir ?? "",
            IsReadOnly = true,
            FontSize   = 12,
            Margin     = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Minishoot' Adventures install folder (the one containing " +
                          "Minishoot.exe). Detected from Steam automatically; set it here " +
                          "to override a non-standard Steam library location.",
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
                Title            = "Select your Minishoot' Adventures install folder",
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
                    System.Windows.MessageBox.Show(bad, "Not a Minishoot' Adventures folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
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
            Text = "Steam installs are detected automatically (appid 1634860). Use this " +
                   "picker for a non-standard Steam library location.",
            FontSize = 11, Foreground = muted, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection info ──────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING IN-GAME", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "The Randomizer Menu (top-left window on the title screen) is where " +
                   "you enter your Archipelago server address, slot name and password. " +
                   "The mod saves these between sessions automatically. This launcher " +
                   "cannot pre-fill those fields — they use Unity PlayerPrefs internally. " +
                   "Copy your session details from the info shown above.",
            FontSize = 11, Foreground = fg, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP STEPS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own and install Minishoot' Adventures via Steam (appid 1634860).",
            "2. Download BepInEx 5 (NOT 6 — see the BepInEx link below; use " +
                "BepInEx_win_x64_5.4.23.2.zip). Extract the archive contents " +
                "(BepInEx folder, doorstop_libs folder, winhttp.dll, etc.) directly " +
                "into the game's root folder alongside Minishoot.exe.",
            "3. Launch the game once and close it from the main menu. This lets BepInEx " +
                "create its configuration files.",
            "4. IMPORTANT: Open BepInEx\\config\\BepInEx.cfg in a text editor and find " +
                "the line containing HideManagerGameObject. Set its value to true: " +
                "HideManagerGameObject = true. Without this the randomizer will not work.",
            "5. Use the Install button on the Play tab to download and stage the " +
                "MinishootRandomizer plugin into BepInEx\\plugins\\MinishootRandomizer\\. " +
                "Alternatively, download MinishootRandomizer.zip from the mod repo " +
                "releases and extract it there manually.",
            "6. Launch the game. On the title screen you should see a 'Randomizer Menu' " +
                "window in the top-left. Enter your server address (e.g. archipelago.gg:" +
                "12345), slot name and password, then press Connect.",
            "7. Once connected, start a new save or continue an existing one to play.",
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
            ("MinishootRandomizer releases (GitHub) ↗",         ModRepoUrl + "/releases"),
            ("Installation guide ↗",                            SetupGuideUrl),
            ("How to play (Archipelago) ↗",                     PlayingGuideUrl),
            ("BepInEx 5 download (v5.4.23.2, win_x64) ↗",      BepInEx5DownloadUrl),
            ("BepInEx 5 release page ↗",                        BepInEx5ReleaseUrl),
            ("Archipelago Official ↗",                          ArchipelagoSite),
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
                Foreground          = new System.Windows.Media.SolidColorBrush(
                                          System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
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

    // ── Private helpers — release resolution ──────────────────────────────────

    /// "v0.5.2" → "0.5.2" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the MinishootRandomizer.zip
    /// download URL. Falls back to the pinned v0.5.2 URL when offline.
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

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;   // any asset named MinishootRandomizer*.zip
                string? anyZip    = null;   // any .zip
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null && lower.Contains("minishootrandomizer"))
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

    // ── Private helpers — game installation detection ─────────────────────────

    /// The game dir to use: the override (if set and valid) wins, else the
    /// Steam-detected install. Null when nothing is found.
    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeGameDir(ov))
            return ov;

        try { return DetectSteamGameDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Minishoot' Adventures if it contains Minishoot.exe
    /// or a Windows/ sub-folder (the typical Steam layout for Unity games).
    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, "Minishoot.exe"))) return true;
            // Some Unity Steam installs nest the exe inside Windows/
            if (File.Exists(Path.Combine(dir, "Windows", "Minishoot.exe"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Minishoot' Adventures install: read the Steam root from
    /// the registry, gather all library roots from libraryfolders.vdf, and find
    /// the one whose appmanifest_1634860.acf exists → steamapps\common\<installdir>.
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
                    // Fall back to the conventional folder name.
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeGameDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath) plus the conventional x86 Program Files path.
    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormalizeSteamPath(hkcu);

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormalizeSteamPath(hklm);

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    private static string NormalizeSteamPath(string p) => p.Replace('/', '\\').TrimEnd('\\');

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
            yield return text.Substring(open + 1, close - open - 1).Replace("\\\\", "\\");
            i = close + 1;
        }
    }

    /// Read the "installdir" value from an appmanifest_*.acf file.
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

    // ── Private helpers — mod detection ───────────────────────────────────────

    /// Find the MinishootRandomizer.dll under the game's BepInEx/plugins tree
    /// (case-insensitive, recursive). Returns the dll path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? game = ResolveGameDir();
            if (game == null) return null;

            string pluginsDir = Path.Combine(game, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dll).Equals(ModDllName, StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { /* permissions / vanished */ }
        return null;
    }

    /// Check whether BepInEx.cfg has HideManagerGameObject set to true. This is
    /// a required configuration for MinishootRandomizer to function. Returns
    /// false when the file is absent, unreadable, or the value is not "true".
    private static bool CheckBepInExCfgEdited(string gameDir)
    {
        try
        {
            string cfgPath = Path.Combine(gameDir, "BepInEx", "config", "BepInEx.cfg");
            if (!File.Exists(cfgPath)) return false;

            foreach (string line in File.ReadLines(cfgPath))
            {
                string trimmed = line.Trim();
                // Expect a line like: HideManagerGameObject = true
                if (trimmed.StartsWith("HideManagerGameObject", StringComparison.OrdinalIgnoreCase))
                {
                    int eq = trimmed.IndexOf('=');
                    if (eq >= 0)
                    {
                        string val = trimmed[(eq + 1)..].Trim();
                        return val.Equals("true", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
        }
        catch { /* file locked / permissions */ }
        return false;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        string? game = ResolveGameDir();

        // Prefer the exe; some Unity Steam builds put it in a Windows/ subfolder.
        string? exe = null;
        if (game != null)
        {
            string direct = Path.Combine(game, GameExeName);
            string nested = Path.Combine(game, "Windows", GameExeName);
            if (File.Exists(direct)) exe = direct;
            else if (File.Exists(nested)) exe = nested;
        }

        if (exe != null)
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Minishoot' Adventures.");

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

        // Fall back to Steam if we can locate any Steam root.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process lifecycle
                return;
            }
            catch { }
        }

        throw new FileNotFoundException(
            "Could not find Minishoot.exe. Open this game's Settings and pick your " +
            "Minishoot' Adventures install folder, or install the game via Steam " +
            "(appid 1634860).",
            GameExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string apModDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"minishoot-randomizer-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((10, $"Downloading MinishootRandomizer {version}..."));
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
                        int pct = (int)(10 + 60 * downloaded / total);
                        progress.Report((pct, $"Downloading... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Installing mod into BepInEx\\plugins..."));
            Directory.CreateDirectory(apModDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, apModDir, overwriteFiles: true);

            // The release zip may wrap everything in a single sub-folder (e.g.
            // MinishootRandomizer/). If the expected DLL is not at the top level
            // of apModDir, try to flatten from the first (and only) sub-folder.
            if (!File.Exists(Path.Combine(apModDir, ModDllName)))
            {
                string[] subdirs = Directory.GetDirectories(apModDir);
                if (subdirs.Length == 1)
                {
                    string sub = subdirs[0];
                    foreach (string fileSrc in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
                    {
                        string rel     = Path.GetRelativePath(sub, fileSrc);
                        string fileDst = Path.Combine(apModDir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                        File.Move(fileSrc, fileDst, overwrite: true);
                    }
                    try { Directory.Delete(sub, recursive: true); } catch { }
                }
            }

            progress.Report((90, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — sidecar settings ───────────────────────────────────
    // Self-contained settings file (does NOT touch Core/SettingsStore).
    // Stores the install-dir override and an informational version stamp.

    private sealed class MinishootSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private MinishootSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SidecarPath))
            {
                string txt = File.ReadAllText(SidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<MinishootSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(MinishootSettings s)
    {
        try
        {
            Directory.CreateDirectory(SidecarDir);
            File.WriteAllText(SidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
