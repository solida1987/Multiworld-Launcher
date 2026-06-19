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

namespace LauncherV2.Plugins.Blasphemous2;

// ═══════════════════════════════════════════════════════════════════════════════
// Blasphemous2Plugin — install / launch for "Blasphemous 2" (The Game Kitchen,
// 2023) played through the BlasII.Randomizer.Multiworld mod by BrandenEK and
// TRPG0, which bundles an in-game Archipelago client. This is a NATIVE
// "ConnectsItself" integration — the game itself speaks to the AP server via
// MelonLoader (no emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-15, verified against the real repo) ──────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Blasphemous 2 (Steam appid 2114740), and Archipelago support is delivered as
// a mod on top via BrandenEK's Blasphemous 2 Modding ecosystem. The verified facts:
//
//   * THE AP GAME STRING is "Blasphemous 2" (verified against
//     BlasII.Randomizer.Multiworld/Models/ServerConnection.cs line:
//     `TryConnectAndLogin("Blasphemous 2", info.Name, ...)`). This is NOT an
//     official Archipelago core world — it is a community apworld distributed
//     separately. GameId here = "blasphemous2".
//
//   * THE MOD repo is BrandenEK/BlasII.Randomizer.Multiworld (verified live
//     2026-06-15). The latest release is v1.1.0 (2025-09-13). The SINGLE release
//     asset is:
//         Multiworld.zip   (81 KB)
//     The csproj confirms the output target name is "Multiworld" and the zip layout
//     follows the BlasII.ModdingAPI convention:
//         plugins/Multiworld.dll        ← primary mod DLL
//         data/...                      ← item/location ID JSON files (itemids.json,
//                                         locationids.json, icons, etc.)
//         localization/...              ← localisation strings (en, etc.)
//     All extracted straight into <Blasphemous 2>/Modding/ (no outer wrapper folder).
//     The verified mod DLL path is:
//         <Blasphemous 2>/Modding/plugins/Multiworld.dll
//
//   * THE FRAMEWORK is MelonLoader (verified from Main.cs: `class Main : MelonMod`).
//     Blasphemous 2 uses MelonLoader rather than the BepInEx-derived loader that
//     Blasphemous 1 uses. The BlasII.ModdingAPI (BrandenEK/BlasII.ModdingAPI) is
//     the abstraction layer on top of MelonLoader.
//
//   * CRITICAL HONESTY — THE ZIP IS NOT SELF-SUFFICIENT. The README states
//     plainly: "This mod is available for download through the Blasphemous Mod
//     Installer. Required dependencies: Modding API, UI Framework, Menu Framework,
//     Randomizer." The Multiworld.zip contains ONLY the Multiworld mod files. It
//     does NOT contain MelonLoader, the BlasII.ModdingAPI, or any of the four
//     dependency mods (UI Framework, Menu Framework, and the base Randomizer).
//     Therefore the OFFICIAL and RECOMMENDED install route is BrandenEK's
//     Blasphemous Mod Installer (supports both Blasphemous 1 and 2), which installs
//     MelonLoader + ModdingAPI + the Multiworld mod + all dependencies in one step
//     after you point it at your Blasphemous 2 install. The direct-zip drop this
//     plugin can perform is a best-effort partial fallback. The plugin says exactly
//     this and guides the user to the Mod Installer — faking a one-click "fully
//     installed" that cannot exist would be dishonest.
//
//   * CONNECTION is made IN-GAME (verified from MultiworldMenu.cs + README): after
//     the mods are installed, launch the game and START A NEW SAVE FILE — a menu
//     opens prompting for your connection details:
//         Server ip:   archipelago.gg:PORT   (the mod auto-replaces "ap:" shorthand)
//         Player name: <your AP slot name>
//         Password:    <if the server has one>
//     There is NO command-line arg and NO config file this launcher can pre-write
//     for the connection (it is driven entirely from the in-game save-file menu).
//     This plugin does NOT write any connection prefill file — the settings panel
//     surfaces the session's host/port/slot for the user to copy into the in-game
//     menu. (Identical pattern to BlasphemousPlugin.)
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Blasphemous 2 install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Blasphemous 2 via appmanifest_2114740.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and takes
//      precedence; it is validated (must contain Blasphemous2.exe and/or
//      Blasphemous2_Data) and persisted in this plugin's OWN sidecar
//      (Games/ROMs/blasphemous2/blasphemous2_launcher.json).
//   2. INSTALL/UPDATE (best effort) = download Multiworld.zip from the real
//      release and extract it into <Blasphemous 2>/Modding/ (so Multiworld.dll
//      lands at Modding/plugins/Multiworld.dll, data files at Modding/data/...,
//      etc.). Present clear guided steps + links to complete the MelonLoader /
//      ModdingAPI / dependency install via the Mod Installer.
//   3. LAUNCH = run Blasphemous2.exe from the detected/override install; if the
//      exe cannot be found but Steam is present, fall back to steam://rungameid/2114740.
//      ConnectsItself = true. SupportsStandalone = true.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Blasphemous2Plugin : IGamePlugin
{
    // ── Constants — the BlasII Multiworld mod (verified 2026-06-15) ──────────────

    private const string MOD_OWNER = "BrandenEK";
    private const string MOD_REPO  = "BlasII.Randomizer.Multiworld";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // The Blasphemous Mod Installer — supports BOTH Blasphemous 1 and 2. The
    // RECOMMENDED install route: installs MelonLoader + ModdingAPI + all dependencies.
    private const string ModInstallerUrl =
        "https://github.com/BrandenEK/Blasphemous.Modding.Installer";
    private const string ModInstallerReleasesUrl =
        "https://github.com/BrandenEK/Blasphemous.Modding.Installer/releases";

    // BlasII.ModdingAPI — the MelonLoader-based modding layer every BlasII mod needs.
    private const string ModdingApiUrl = "https://github.com/BrandenEK/BlasII.ModdingAPI";

    // The BlasII Randomizer (base dependency for Multiworld).
    private const string RandoUrl = "https://github.com/BrandenEK/BlasII.Randomizer";

    // No official AP setup guide page for Blasphemous 2 (community apworld, not in
    // AP core). The closest resource is the Blasphemous 2 wiki entry and the mod repo.
    private const string Wiki2Url      = "https://archipelago.miraheze.org/wiki/Blasphemous_2";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Blasphemous 2 appid 2114740.
    private const string Blas2SteamAppId    = "2114740";
    private static readonly string SteamRunUrl = $"steam://rungameid/{Blas2SteamAppId}";

    /// The standard Steam install sub-folder name for Blasphemous 2.
    private const string SteamCommonFolderName = "Blasphemous 2";

    /// The base-game executable name (Unity/MelonLoader build).
    private const string Blas2ExeName = "Blasphemous2.exe";

    /// The Modding folder that MelonLoader mods land in under the game root.
    private const string ModdingSubfolder = "Modding";

    /// The mod's primary DLL — output target name "Multiworld" from the csproj.
    private const string ModPrimaryDll = "Multiworld.dll";

    /// Pinned fallback version when the GitHub API is unreachable. v1.1.0 is the
    /// latest release as of 2026-06-15; the single asset is "Multiworld.zip".
    private const string FallbackVersion = "1.1.0";
    private const string FallbackZipName = "Multiworld.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "blasphemous2";
    public string DisplayName => "Blasphemous 2";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against
    /// BlasII.Randomizer.Multiworld/Models/ServerConnection.cs:
    ///   `TryConnectAndLogin("Blasphemous 2", info.Name, ...)`
    public string ApWorldName => "Blasphemous 2";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "blasphemous2.png");

    public string ThemeAccentColor => "#8B0000";   // dark crimson, religious horror aesthetic
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Blasphemous 2, The Game Kitchen's 2023 dark-fantasy Metroidvania sequel, " +
        "played through the BlasII.Randomizer.Multiworld mod by BrandenEK and TRPG0 — " +
        "which bundles an in-game Archipelago client powered by MelonLoader, so the " +
        "game connects to the multiworld itself with no emulator and no bridge. " +
        "Figures, prayers, wax, cherubs, abilities and more are shuffled across the " +
        "multiworld. You bring your own copy of Blasphemous 2 (owned on Steam); the " +
        "integration runs on the BlasII Modding API and the BlasII Randomizer. The " +
        "launcher detects your Steam install and can stage the Multiworld mod files " +
        "into it, but MelonLoader, the Modding API and the mod's dependencies are " +
        "best installed in one step with the Blasphemous Mod Installer (supports " +
        "both games). You connect to your server in-game when you start a new save file.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means Multiworld.dll is present in the detected/override Blasphemous 2
    /// install's Modding/plugins tree. We do NOT gate on our own version stamp — the user
    /// may have installed the mod via the Mod Installer (the recommended route).
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and working files. The mod is extracted
    /// INTO the Blasphemous 2 install's Modding folder, not here. Exposed as
    /// GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Blasphemous2");

    /// This plugin's OWN settings sidecar (install-dir override + version stamp),
    /// kept out of the shared SettingsStore so the plugin is fully self-contained.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "blasphemous2_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The BlasII Multiworld mod reports checks/items/goal to the AP server itself.
    // These events exist for interface compatibility (ConnectsItself = true).
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
        // 0. We need a Blasphemous 2 install to drop the mod into.
        progress.Report((2, "Locating your Blasphemous 2 installation..."));
        string? blas2Dir = ResolveBlasphemous2Dir();
        if (blas2Dir == null)
            throw new InvalidOperationException(
                "Could not find a Blasphemous 2 installation. Open this game's Settings " +
                "and pick your Blasphemous 2 folder (the one containing Blasphemous2.exe), " +
                "or install Blasphemous 2 via Steam first.");

        // 1. Resolve the latest mod release.
        progress.Report((6, "Checking the latest BlasII Multiworld release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the BlasII Multiworld mod download on GitHub. " +
                "Check your internet connection, or install the mod with the " +
                "Blasphemous Mod Installer (recommended) from " + ModInstallerUrl +
                " — see Settings for the guided steps. The mod repo is " + ModRepoUrl + ".");

        // 2. Download + extract Multiworld.zip INTO <Blasphemous 2>/Modding/.
        //    HONEST: this stages the Multiworld mod only. MelonLoader, BlasII.ModdingAPI
        //    and the four dependency mods (UI Framework, Menu Framework, Randomizer) are
        //    NOT in this zip and must be provided by the Blasphemous Mod Installer.
        string moddingDir = Path.Combine(blas2Dir, ModdingSubfolder);
        await DownloadAndExtractModAsync(zipUrl, version, moddingDir, progress, ct);

        // 3. Stamp the version in our sidecar (informational only).
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool dllOk = FindInstalledModDll() != null;
        progress.Report((100,
            $"Staged BlasII Multiworld {version} into your Modding\\plugins folder" +
            (dllOk ? "." : " (verify the files landed).") +
            " IMPORTANT: this mod needs MelonLoader, the BlasII Modding API, plus " +
            "several dependency mods (UI Framework, Menu Framework, Randomizer) that " +
            "this download does NOT include. The recommended way to finish is the " +
            "Blasphemous Mod Installer (point it at your Blasphemous 2 install and it " +
            "sets up MelonLoader + the Modding API + the mod + all dependencies) — open " +
            "Settings for the guided steps and links. To play: launch Blasphemous 2, " +
            "start a NEW save file, and enter your server details (e.g. \"Server ip: " +
            "archipelago.gg:PORT\", \"Player name: <your slot>\") in the in-game menu."));
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
        // HONEST: the AP server connection for Blasphemous 2 is entered in the
        // IN-GAME menu that opens when you start a NEW save file (Server ip: host:port,
        // Player name: <slot>, Password). There is no documented command-line / config
        // prefill (verified from MultiworldMenu.cs + README). So launching from this
        // tile just starts the game; the settings panel surfaces the session credentials
        // for the user to copy.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartBlasphemous2();
        return Task.CompletedTask;
    }

    /// Plain Blasphemous 2 runs fine without AP.
    public bool SupportsStandalone => true;

    /// The BlasII Multiworld mod owns the slot connection.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartBlasphemous2();
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
    {
        // The BlasII Multiworld mod receives items from the AP server directly.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Blasphemous 2 install folder.";

        if (LooksLikeBlasphemous2Dir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeBlasphemous2Dir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Blasphemous 2 installation. Pick the folder " +
               "that contains Blasphemous2.exe (for Steam this is usually " +
               @"...\steamapps\common\Blasphemous 2).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new Thickness(0, 0, 0, 20)
        };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Blasphemous 2 is your own game (Steam) with the BlasII Multiworld " +
                   "mod added on top via MelonLoader and the BlasII Modding API. The " +
                   "launcher detects your Steam install and can stage the Multiworld " +
                   "mod files, but MelonLoader, the Modding API and the mod's dependencies " +
                   "(UI Framework, Menu Framework, Randomizer) are best installed in one " +
                   "step with the Blasphemous Mod Installer (see the guided steps below). " +
                   "You connect to your server in-game when you start a new save file. " +
                   "These external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "BLASPHEMOUS 2 INSTALL", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 0, 0, 8),
        });

        string? blas2Dir    = ResolveBlasphemous2Dir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = blas2Dir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + blas2Dir
                : "Detected Steam install: " + blas2Dir)
            : "Blasphemous 2 not detected. Pick your install folder below, or install " +
              "Blasphemous 2 via Steam first.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = blas2Dir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // mod status line
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                    ? "BlasII Multiworld mod found: " + modDll
                    : "BlasII Multiworld mod not found in Modding\\plugins yet (use " +
                      "Install on the Play tab, or install it via the Mod Installer — " +
                      "recommended).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = overrideDir ?? blas2Dir ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Blasphemous 2 install folder (the one containing " +
                          "Blasphemous2.exe). Detected from Steam automatically; set it " +
                          "here to override (non-standard Steam library, or another store).",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content     = "Select folder...",
            Width       = 120,
            Padding     = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Blasphemous 2 install folder (contains Blasphemous2.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? blas2Dir ?? "")
                                   ? (overrideDir ?? blas2Dir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    MessageBox.Show(bad, "Not a Blasphemous 2 folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeBlasphemous2Dir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeBlasphemous2Dir(nested)) picked = nested;
                }
                SaveOverrideDir(picked);
                dirBox.Text = picked;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Steam installs are detected automatically (appid 2114740). Use this " +
                   "picker for a non-standard Steam library or another store.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (entered in-game) ─────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Launch Blasphemous 2 and START A NEW SAVE FILE — a menu opens for " +
                   "your connection details. Enter \"Server ip\" as host:port (e.g. " +
                   "archipelago.gg:38281), \"Player name\" as your AP slot name, and the " +
                   "password if the server has one. The mod also accepts the shorthand " +
                   "\"ap:PORT\" and expands it to archipelago.gg:PORT automatically. This " +
                   "launcher does not pre-fill the connection.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP (recommended: Blasphemous Mod Installer)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Blasphemous 2 (on Steam). Install it if you have not. Use the picker above if " +
                "it was not detected automatically.",
            "2. Download the Blasphemous Mod Installer (link below) — it supports both Blasphemous " +
                "1 and 2. Run it and point it at your Blasphemous 2 install when prompted.",
            "3. In the Mod Installer, install \"Multiworld\" for Blasphemous 2 — it pulls in " +
                "MelonLoader, the BlasII Modding API and the required dependencies (UI Framework, " +
                "Menu Framework, Randomizer) automatically.",
            "4. Alternative (advanced): the Install button on the Play tab stages Multiworld.dll and " +
                "the mod's data files into your Modding folder, but it does NOT include MelonLoader " +
                "or the dependency mods — you would still need those from the Mod Installer.",
            "5. Launch Blasphemous 2 and confirm the mods loaded (MelonLoader logs are shown in the " +
                "console window on startup).",
            "6. To play: start a NEW save file, then enter your Server ip (host:port), Player name " +
                "(your AP slot name), and password (if any) into the in-game menu. Entering " +
                "\"ap:PORT\" as the server is also accepted and auto-expands. This launcher cannot " +
                "pre-fill the connection — it is entered in-game.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Blasphemous Mod Installer (download) ↗", ModInstallerReleasesUrl),
            ("BlasII Multiworld mod (GitHub) ↗",       ModRepoUrl),
            ("BlasII Modding API (GitHub) ↗",          ModdingApiUrl),
            ("BlasII Randomizer (GitHub) ↗",           RandoUrl),
            ("Blasphemous 2 — Archipelago Wiki ↗",     Wiki2Url),
            ("Archipelago Official ↗",                 ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content             = label,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding             = new Thickness(0, 2, 0, 2),
                Background          = Brushes.Transparent,
                BorderThickness     = new Thickness(0),
                FontSize            = 12,
                Margin              = new Thickness(0, 0, 0, 4),
                Foreground          = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor              = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
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

    /// "v1.1.0" → "1.1.0" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the mod zip download URL. Prefers
    /// the "Multiworld.zip" asset. Falls back to the pinned 1.1.0 direct URL when
    /// the API is unreachable or rate-limited.
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
                string? preferred = null;   // "Multiworld.zip"
                string? anyZip    = null;   // any .zip
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null && lower.Contains("multiworld"))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — Steam / Blasphemous 2 detection ─────────────────────

    /// The Blasphemous 2 install dir: the override (if set and valid) wins, then
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolveBlasphemous2Dir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeBlasphemous2Dir(ov))
            return ov;

        try { return DetectSteamBlasphemous2Dir(); }
        catch { return null; }
    }

    /// A folder "looks like" Blasphemous 2 if it has Blasphemous2.exe and/or the
    /// Blasphemous2_Data folder.
    private static bool LooksLikeBlasphemous2Dir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, Blas2ExeName)))              return true;
            if (Directory.Exists(Path.Combine(dir, "Blasphemous2_Data")))  return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Blasphemous 2 install: check all library roots for
    /// appmanifest_2114740.acf → steamapps\common\Blasphemous 2.
    private static string? DetectSteamBlasphemous2Dir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{Blas2SteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeBlasphemous2Dir(candidate)) return candidate;
                    }
                    // Conventional folder name has a space — important to use the right name.
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeBlasphemous2Dir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots: HKCU SteamPath + HKLM WOW6432Node InstallPath
    /// + HKLM 64-bit path + conventional ProgramFiles(x86) location.
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

    /// All Steam library roots: the Steam root + every "path" entry in
    /// steamapps\libraryfolders.vdf.
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

    /// Find Multiworld.dll under the detected/override Blasphemous 2 install's
    /// Modding tree (recursive, case-insensitive). Returns the dll path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? blas2 = ResolveBlasphemous2Dir();
            if (blas2 == null) return null;
            string moddingDir = Path.Combine(blas2, ModdingSubfolder);
            if (!Directory.Exists(moddingDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(
                moddingDir, "*.dll", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dll).Equals(ModPrimaryDll,
                    StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartBlasphemous2()
    {
        string? blas2 = ResolveBlasphemous2Dir();
        string? exe   = blas2 != null ? Path.Combine(blas2, Blas2ExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = blas2!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Blasphemous 2.");

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

        // Fall back to Steam if we can detect any Steam root.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { /* fall through to the error below */ }
        }

        throw new FileNotFoundException(
            "Could not find Blasphemous2.exe. Open this game's Settings and pick your " +
            "Blasphemous 2 install folder, or install Blasphemous 2 via Steam.",
            Blas2ExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download Multiworld.zip and extract it into <Blasphemous 2>/Modding/.
    ///
    /// The zip layout (per the csproj build target) is flat — no outer wrapper folder:
    ///     plugins/Multiworld.dll
    ///     data/...
    ///     localization/...
    /// So we extract directly into moddingDir and merge with whatever already exists
    /// (so other mods installed by the Mod Installer are not disturbed).
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string moddingDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"blasii-multiworld-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"blasii-multiworld-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading BlasII Multiworld {version}..."));
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
                        int pct = (int)(10 + 60 * downloaded / total);
                        progress.Report((pct, $"Downloading BlasII Multiworld... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Installing the mod into your Modding folder..."));
            Directory.CreateDirectory(moddingDir);
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, tempExtract, overwriteFiles: true);

            // The expected layout has "plugins" (and optionally "data"/"localization")
            // at the extract root. If the zip wraps everything in a single folder,
            // descend into it so we merge the real "plugins" folder.
            string mergeRoot = tempExtract;
            if (!Directory.Exists(Path.Combine(mergeRoot, "plugins")))
            {
                string[] subdirs = Directory.GetDirectories(mergeRoot);
                string[] files   = Directory.GetFiles(mergeRoot);
                if (subdirs.Length == 1 && files.Length == 0 &&
                    Directory.Exists(Path.Combine(subdirs[0], "plugins")))
                {
                    mergeRoot = subdirs[0];
                }
            }

            // Merge INTO the existing Modding folder — do not wipe it, because the
            // Mod Installer's other mods live alongside under the same directories.
            MergeDirectory(mergeRoot, moddingDir);

            progress.Report((90, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))          File.Delete(tempZip); }          catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Recursively copy a directory tree INTO an existing destination, overwriting
    /// individual files but preserving any sibling files already there.
    private static void MergeDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(
            sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            try { File.Copy(file, dst, overwrite: true); }
            catch { /* locked file (game open?) — skip; user can retry with game closed */ }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class Blas2Settings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private Blas2Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Blas2Settings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(Blas2Settings s)
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
