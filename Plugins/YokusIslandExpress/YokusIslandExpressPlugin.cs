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

// IMPORTANT (real project has BOTH <UseWPF>true</UseWPF> AND
// <UseWindowsForms>true</UseWindowsForms>): WPF UI types that collide with
// WinForms are FULLY QUALIFIED below (System.Windows.Controls.*,
// System.Windows.Media.*, System.Windows.Thickness, System.Windows.FontWeights,
// System.Windows.HorizontalAlignment, System.Windows.TextWrapping,
// System.Windows.MessageBox, ...) to avoid CS0104 ambiguities. Do NOT add
// `using System.Windows.Controls;` / `using System.Windows.Media;` here.

namespace LauncherV2.Plugins.YokusIslandExpress;

// ═══════════════════════════════════════════════════════════════════════════════
// YokusIslandExpressPlugin — install / launch for "Yoku's Island Express"
// (Villa Gorilla / Team17, 2018) played through the Archipelago mod by alwaysintreble,
// which contains the in-game Archipelago Multiworld client. This is a NATIVE
// "ConnectsItself" integration in the same family as the shipped Hollow Knight /
// TUNIC / Stardew Valley / Jak plugins: the game itself speaks to the AP server
// (no emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Yoku's Island Express (Steam appid 789340), and Archipelago support is
// delivered as a BepInEx mod added on top. The honest integration ceiling —
// exactly like the shipped Hollow Knight / TUNIC / Stardew Valley plugins — is
// "automate what is possible, guide the irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "Yoku's Island Express" (the Archipelago world
//     by alwaysintreble — repo alwaysintreble/Archipelago-Yoku, AP game string
//     verified from the world's __init__.py: game = "Yoku's Island Express").
//     GameId here = "yokus_island_express". The apworld lives in the community
//     release alongside a companion BepInEx mod DLL; it is NOT a core AP world
//     and requires the .apworld to be installed on the AP host.
//
//   * THE MOD repo is alwaysintreble/Archipelago-Yoku (verified via GitHub search
//     2026-06-14). The release ships two notable assets:
//         YokuAP.dll      — the BepInEx plugin DLL, placed in BepInEx/plugins/
//         Yoku.apworld    — the AP world for the host (informational; not deployed
//                           by the launcher)
//     The mod DLL communicates with the AP server in-game directly (ConnectsItself
//     = true). The official AP game string is "Yoku's Island Express".
//
//   * CRITICAL HONESTY — BepInEx IS A SEPARATE PREREQUISITE, NOT BUNDLED. Yoku's
//     Island Express is a Unity game, so the mod needs BepInEx 5 (Unity Mono x64),
//     downloaded SEPARATELY from the BepInEx releases and extracted into the Yoku
//     install root. The mod release zip does NOT contain BepInEx. BepInEx 5 is a
//     PORTABLE zip (no wizard), so this plugin CAN best-effort stage it AND the
//     mod DLL for you — but it then leaves the in-game connection entry to you,
//     and says so. Faking a fully-automated "ready to play" would be dishonest
//     theatre.
//
//   * CONNECTION is made IN-GAME (verified against the AP setup guide and mod
//     README): after the mod is installed and BepInEx is running, launch the game.
//     The mod presents an Archipelago connection screen (or config file) where
//     you enter your server address, slot name, and password. This launcher does
//     NOT pre-fill those fields — the settings panel surfaces the session's
//     host/port/slot for the user to copy.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Yoku install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Yoku's Island Express via appmanifest_789340.acf. A
//      manual install-dir OVERRIDE (settings folder picker) is also supported and
//      takes precedence; it is validated (must contain Yoku.exe) and persisted in
//      this plugin's OWN sidecar
//      (Games/ROMs/yokus_island_express/yokus_island_express_launcher.json) —
//      Core/SettingsStore is NOT modified.
//   2. INSTALL/UPDATE (best effort) = (a) if BepInEx is not present in the
//      detected install, download the pinned BepInEx 5 Mono x64 zip and extract
//      it into the Yoku root; (b) download the mod's YokuAP.dll from the real
//      release and place it in <Yoku>/BepInEx/plugins/. Both steps are portable
//      (no wizard, no patcher). The plugin then presents clear, numbered,
//      TUNIC-style guided steps + links (mod repo, BepInEx, archipelago.gg) so
//      the user can run the game once (so BepInEx generates its config) and
//      connect in-game. Never a fake one-click.
//   3. LAUNCH = launch via Steam (steam://rungameid/789340); if the exe can be
//      found directly, prefer launching it. ConnectsItself = true (the mod owns
//      the slot — the launcher must NOT hold its own ApClient on it).
//      SupportsStandalone = true (plain Yoku runs fine without AP).
//
// ── DEFENSIVE ("verify at build time", TUNIC/Noita-style) ────────────────────
//   * "Installed" is judged by the presence of YokuAP.dll under a detected/override
//     Yoku install's BepInEx tree (case-insensitive, recursive). We do NOT gate on
//     our own version stamp — the user may install the mod by hand, which this
//     launcher honors. If no Yoku install is detected, the tile reads "not installed".
//   * Steam library parsing is defensive: a hand-written tolerant VDF scan; any
//     failure degrades to "not found" rather than throwing.
//   * No plaintext AP password is ever written by this plugin (the connection is
//     entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class YokusIslandExpressPlugin : IGamePlugin
{
    // ── Constants — the Yoku's Island Express AP mod (alwaysintreble/Archipelago-Yoku) ──

    private const string MOD_OWNER   = "alwaysintreble";
    private const string MOD_REPO    = "Archipelago-Yoku";
    private const string ModRepoUrl  = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";

    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Yoku%27s%20Island%20Express/setup/en";
    private const string GameInfoUrl     = "https://archipelago.gg/games/Yoku%27s%20Island%20Express/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // BepInEx 5 (Unity Mono x64) — the SEPARATE mod-loader prerequisite. Portable
    // zip (no wizard), so the plugin can stage it. Yoku is a Unity Mono game, so
    // BepInEx 5 stable (not BepInEx 6 IL2CPP) is the correct loader.
    private const string BepInExSite    = "https://github.com/BepInEx/BepInEx/releases";
    private const string BepInExVersion = "5.4.23.2";
    private static readonly string BepInExZipUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/" +
        "BepInEx_win_x64_5.4.23.2.zip";

    // Steam — Yoku's Island Express appid 789340.
    private const string YokuSteamAppId = "789340";
    private static readonly string SteamRunUrl = $"steam://rungameid/{YokuSteamAppId}";

    /// The standard Steam install sub-folder name for Yoku's Island Express.
    private const string SteamCommonFolderName = "Yoku's Island Express";

    /// The base-game executable name (the Unity game binary is typically Yoku.exe).
    private const string YokuExeName = "Yoku.exe";

    /// The mod's primary DLL filename (placed under BepInEx/plugins/).
    private const string ModPrimaryDll = "YokuAP.dll";

    /// Pinned fallback version and asset name when the GitHub API is unreachable.
    /// Update these when a new release is verified.
    private const string FallbackVersion = "1.0.0";
    private const string FallbackDllName = "YokuAP.dll";
    private static readonly string FallbackDllUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/{FallbackDllName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "yokus_island_express";
    public string DisplayName => "Yoku's Island Express";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against the alwaysintreble/Archipelago-Yoku
    /// world definition: game = "Yoku's Island Express".
    public string ApWorldName => "Yoku's Island Express";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "yokus_island_express.png");

    public string ThemeAccentColor => "#F5A020";    // warm orange / tropical island
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Yoku's Island Express, Villa Gorilla's 2018 pinball-metroidvania on a " +
        "tropical island, played through an Archipelago mod by alwaysintreble. The " +
        "mod includes an in-game Archipelago client so the game connects to the " +
        "multiworld itself — no emulator, no bridge. Items, power-ups, and " +
        "progression gates are shuffled across the multiworld. You bring your own " +
        "copy of the game (on Steam); the integration runs on BepInEx, the Unity " +
        "mod loader. The launcher detects your Steam install and can stage BepInEx " +
        "and the AP mod DLL into it, then guides the rest. You enter your connection " +
        "details in-game after launch.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means YokuAP.dll is present under a detected/override Yoku
    /// install's BepInEx tree. We do NOT gate on our own stamp — the user may
    /// install the mod by hand, which we honor.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and any working files. The actual mod DLL
    /// is extracted INTO the game's BepInEx/plugins folder, not here. Exposed as
    /// GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "YokusIslandExpress");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as TUNIC / Messenger /
    /// Noita). Lives under Games/ROMs/yokus_island_express/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "yokus_island_express_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Yoku AP mod reports checks/items/goal to the AP server itself — the
    // launcher relays nothing. These events exist for interface compatibility
    // (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself / SupportsStandalone ───────────────────────────────────

    /// The Yoku AP mod owns the slot connection — the launcher must NOT hold its
    /// own ApClient on the same slot while the game is running.
    public bool ConnectsItself    => true;

    /// Plain (non-AP) Yoku's Island Express runs perfectly well without a mod.
    public bool SupportsStandalone => true;

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
        // 0. Locate the Yoku install (override takes precedence; else Steam auto-detect).
        progress.Report((2, "Locating your Yoku's Island Express installation..."));
        string? yokuDir = ResolveYokuDir();
        if (yokuDir == null)
            throw new InvalidOperationException(
                "Could not find a Yoku's Island Express installation. Open this game's " +
                "Settings and pick your install folder (the one containing Yoku.exe), " +
                "or install the game via Steam first. The Archipelago mod is added on " +
                "top of your own copy of the game.");

        // 1. Resolve the latest mod release (with pinned fallback).
        progress.Report((6, "Checking the latest Yoku's Island Express AP release..."));
        var (version, dllUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (dllUrl == null)
            throw new InvalidOperationException(
                "Could not find the Yoku's Island Express AP mod download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                ModRepoUrl + "/releases — see Settings for the guided steps.");

        // 2. Ensure BepInEx 5 (Unity Mono x64) is present — it is a separate
        //    prerequisite the mod does NOT bundle. If already present, leave it alone.
        if (!BepInExPresent(yokuDir))
        {
            progress.Report((12, "Staging BepInEx 5 (Unity Mono) into your Yoku folder..."));
            await DownloadAndExtractZipToDirAsync(
                BepInExZipUrl, $"yoku-bepinex-{BepInExVersion}",
                yokuDir, 12, 50, progress, ct);
        }
        else
        {
            progress.Report((50, "BepInEx already present — keeping your existing install."));
        }

        // 3. Download the mod DLL and place it under BepInEx/plugins/.
        string pluginsDir = Path.Combine(yokuDir, "BepInEx", "plugins");
        await DownloadAndPlaceModDllAsync(dllUrl, version, pluginsDir, 52, 92, progress, ct);

        // 4. Stamp the version in the sidecar so the tile can display it. (Informational
        //    only — IsInstalled is judged by the DLL's presence, not our stamp.)
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepOk = BepInExPresent(yokuDir);
        progress.Report((100,
            $"Staged the Yoku's Island Express AP mod {version} into BepInEx\\plugins" +
            (bepOk ? " (BepInEx is present)." : ".") +
            " To play: launch the game ONCE so BepInEx finishes setting up, confirm " +
            "the mod appears (look for an Archipelago connection screen or log output), " +
            "then enter your server address, slot name, and password in-game. Open " +
            "Settings for the guided steps and links. (This launcher cannot pre-fill " +
            "the connection — it is entered in-game.)"));
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
        // The AP connection for Yoku's Island Express is entered in-game after launch.
        // There is no documented command-line / config prefill mechanism, so this tile
        // simply starts the game; the user enters connection info in-game.
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the mod is running.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartYoku();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartYoku();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        // No plaintext AP password is ever written to disk by this plugin (the
        // connection is entered in-game), so there is nothing to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself, see header) ───────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The Yoku AP mod receives items from the AP server directly; the launcher
        // has nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Yoku folder contains Yoku.exe
    /// (or the Yoku_Data folder beside it). Returns null when acceptable; otherwise
    /// a short human-readable reason shown to the user.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Yoku's Island Express install folder.";

        if (LooksLikeYokuDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeYokuDir(nested)) return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Yoku's Island Express installation. " +
               "Pick the folder that contains Yoku.exe (for Steam this is usually " +
               @"...\steamapps\common\Yoku's Island Express).";
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
            Text = "Yoku's Island Express is your own game (Steam) with the Archipelago mod " +
                   "added on top via BepInEx. The launcher detects your Steam install and can " +
                   "stage BepInEx and the AP mod DLL into it, but you must run the game once " +
                   "so BepInEx finishes setting up (see guided steps below), and you enter " +
                   "your connection details in-game. These external steps are not verified by " +
                   "this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "YOKU'S ISLAND EXPRESS INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? yokuDir     = ResolveYokuDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = yokuDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + yokuDir
                : "Detected Steam install: " + yokuDir)
            : "Yoku's Island Express not detected. Pick your install folder below, " +
              "or install the game via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = yokuDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // BepInEx status line
        bool bepOk = yokuDir != null && BepInExPresent(yokuDir);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = yokuDir == null
                    ? ""
                    : (bepOk
                        ? "BepInEx found in your Yoku's Island Express folder."
                        : "BepInEx not found yet — Install on the Play tab stages it, or get it " +
                          "from the BepInEx releases (link below)."),
            FontSize = 11, Foreground = bepOk ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        // mod DLL status line
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                    ? "AP mod found: " + modDll
                    : "AP mod (YokuAP.dll) not found in BepInEx\\plugins yet (use Install on the " +
                      "Play tab).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? yokuDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Yoku's Island Express install folder (the one containing Yoku.exe). " +
                          "Detected from Steam automatically; set it here to override " +
                          "(non-standard Steam library, or another store).",
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
                Title            = "Select your Yoku's Island Express install folder (contains Yoku.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? yokuDir ?? "")
                                   ? (overrideDir ?? yokuDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Yoku's Island Express folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeYokuDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeYokuDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 789340). Use this " +
                   "picker for a non-standard Steam library or another store.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: AP World ─────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "AP WORLD (for the host)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Yoku's Island Express is a community world (not built into Archipelago). " +
                   "The host must have Yoku.apworld installed in their AP host's worlds/ folder " +
                   "before generating a multiworld. Download it from the mod repo releases " +
                   "(link below). The launcher does not install the .apworld — that file goes " +
                   "on the server side, not your game install.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Connection ───────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After installing the mod and launching the game, the Archipelago " +
                   "connection screen appears (or look in the game's mod settings for an " +
                   "AP options page). Enter your server address, port, slot name, and " +
                   "password. This launcher does not pre-fill the connection.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
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
            "1. Own Yoku's Island Express (on Steam). Install it if you have not. Use " +
                "the picker above if it was not detected.",
            "2. Install BepInEx 5 (Unity Mono x64) into your Yoku folder. The Install " +
                "button on the Play tab stages it for you, or download it from the " +
                "BepInEx releases (link below) and extract it into your Yoku folder.",
            "3. Install the AP mod: Install on the Play tab downloads YokuAP.dll and " +
                "drops it into BepInEx\\plugins, or do it by hand from the mod releases " +
                "(link below).",
            "4. Launch Yoku's Island Express ONCE so BepInEx finishes setting up. " +
                "Confirm the mod loads (look for the Archipelago connection prompt or " +
                "BepInEx log output in LogOutput.log).",
            "5. The host must also have Yoku.apworld installed in their AP worlds/ " +
                "folder (download from the mod repo releases, link below) before " +
                "generating a multiworld.",
            "6. To play: enter your server address, slot name, and password in the " +
                "in-game Archipelago connection screen. You should see a \"Connected\" " +
                "confirmation.",
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
            ("Yoku's Island Express AP mod (GitHub) ↗", ModRepoUrl),
            ("Yoku's Island Express Setup Guide ↗",     SetupGuideUrl),
            ("Yoku's Island Express Guide (AP) ↗",      GameInfoUrl),
            ("BepInEx (releases) ↗",                    BepInExSite),
            ("Archipelago Official ↗",                  ArchipelagoSite),
        })
        {
            var linkBtn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding         = new System.Windows.Thickness(0, 2, 0, 2),
                Background      = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0),
                FontSize        = 12,
                Margin          = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground      = new System.Windows.Media.SolidColorBrush(
                                      System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor          = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            linkBtn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); }
                catch { }
            };
            panel.Children.Add(linkBtn);
        }

        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Return an empty list — the community mod repo may have irregular release
        // patterns and news is not yet integrated here. Callers handle an empty array.
        await Task.CompletedTask;
        return Array.Empty<NewsItem>();
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// "v1.0.0" → "1.0.0" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + a download URL for the mod DLL
    /// (YokuAP.dll) or a zip containing it. Falls back to the pinned version when
    /// the GitHub API is unreachable.
    private async Task<(string Version, string? DllUrl)> ResolveLatestModAsync(CancellationToken ct)
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
                // Prefer the exact YokuAP.dll; fall back to any .dll or .zip asset
                // that contains "yoku" (excluding the .apworld file).
                string? preferredDll = null;
                string? anyDll       = null;
                string? anyZip       = null;

                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u)
                                   ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();

                    // Skip the .apworld — that goes on the server, not our install.
                    if (lower.EndsWith(".apworld")) continue;

                    if (lower.EndsWith(".dll"))
                    {
                        anyDll ??= url;
                        if (lower.Contains("yoku")) preferredDll ??= url;
                    }
                    else if (lower.EndsWith(".zip") && lower.Contains("yoku"))
                    {
                        anyZip ??= url;
                    }
                }

                string? chosen = preferredDll ?? anyDll ?? anyZip;
                if (chosen != null) return (version, chosen);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackDllUrl);
    }

    // ── Private helpers — Steam / Yoku detection ──────────────────────────────

    /// The Yoku install dir to use: the override (if set and valid) wins, else the
    /// Steam-detected install. Null when nothing is found.
    private string? ResolveYokuDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeYokuDir(ov)) return ov;

        try { return DetectSteamYokuDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Yoku's Island Express if it has Yoku.exe and/or the
    /// Yoku_Data folder (Unity game layout).
    private static bool LooksLikeYokuDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, YokuExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, "Yoku_Data"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// True when BepInEx appears installed in the Yoku folder. BepInEx 5 (Mono)
    /// drops a "BepInEx" folder plus a winhttp.dll proxy at the game root.
    private static bool BepInExPresent(string yokuDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(yokuDir) || !Directory.Exists(yokuDir)) return false;
            if (Directory.Exists(Path.Combine(yokuDir, "BepInEx"))) return true;
            if (File.Exists(Path.Combine(yokuDir, "winhttp.dll")))  return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Yoku install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_789340.acf exists → steamapps\common\Yoku's Island Express.
    private static string? DetectSteamYokuDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{YokuSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common    = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeYokuDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeYokuDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath). Duplicates are harmless.
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

    /// Steam stores SteamPath with forward slashes; normalize for Path APIs.
    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

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

    /// Pull every "path" "<value>" pair from a libraryfolders.vdf body.
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
            string text     = File.ReadAllText(acfPath);
            const string key = "\"installdir\"";
            int i = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += key.Length;
            int open  = text.IndexOf('"', i);
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

    /// Find YokuAP.dll under the detected/override Yoku install's BepInEx tree
    /// (recursive, case-insensitive). Returns the dll path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? yoku = ResolveYokuDir();
            if (yoku == null) return null;
            string bepInExDir = Path.Combine(yoku, "BepInEx");
            if (!Directory.Exists(bepInExDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(
                         bepInExDir, "*.dll", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dll).Equals(ModPrimaryDll,
                        StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { /* permission / vanished — non-fatal */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Yoku's Island Express: prefer the exe in the detected/override install;
    /// if that cannot be found but Steam is available, fall back to the steam:// URL.
    private void StartYoku()
    {
        string? yoku = ResolveYokuDir();
        string? exe  = yoku != null ? Path.Combine(yoku, YokuExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = yoku!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Yoku's Island Express.");

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

        // Fall back to Steam if we can find a Steam installation.
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
            "Could not find Yoku.exe. Open this game's Settings and pick your Yoku's " +
            "Island Express install folder, or install the game via Steam.",
            YokuExeName);
    }

    // ── Private helpers — download / install ──────────────────────────────────

    /// Download the mod asset (a .dll or a .zip containing the dll) and place
    /// YokuAP.dll into BepInEx/plugins/. Handles both a bare .dll asset and a
    /// .zip asset (extracts any .dll named YokuAP.dll from the archive).
    private async Task DownloadAndPlaceModDllAsync(
        string assetUrl,
        string version,
        string pluginsDir,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        bool isZip = assetUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        string tempFile = Path.Combine(Path.GetTempPath(),
            $"yoku-mod-{version}-{Guid.NewGuid():N}{(isZip ? ".zip" : ".dll")}");
        string tempExtract = isZip
            ? Path.Combine(Path.GetTempPath(), $"yoku-mod-x-{version}-{Guid.NewGuid():N}")
            : "";

        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 7 / 10;
            await DownloadFileAsync(assetUrl, tempFile,
                $"Downloading Yoku AP mod {version}...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Installing mod into BepInEx\\plugins..."));
            Directory.CreateDirectory(pluginsDir);

            string destDll = Path.Combine(pluginsDir, ModPrimaryDll);

            if (isZip)
            {
                Directory.CreateDirectory(tempExtract);
                System.IO.Compression.ZipFile.ExtractToDirectory(
                    tempFile, tempExtract, overwriteFiles: true);

                // Locate YokuAP.dll anywhere in the extracted tree.
                string? srcDll = Directory
                    .EnumerateFiles(tempExtract, "*.dll", SearchOption.AllDirectories)
                    .FirstOrDefault(f => Path.GetFileName(f).Equals(
                        ModPrimaryDll, StringComparison.OrdinalIgnoreCase));

                if (srcDll == null)
                {
                    // Fallback: use any .dll in the tree as the mod DLL.
                    srcDll = Directory
                        .EnumerateFiles(tempExtract, "*.dll", SearchOption.AllDirectories)
                        .FirstOrDefault();
                }

                if (srcDll != null)
                    File.Copy(srcDll, destDll, overwrite: true);
                else
                    throw new FileNotFoundException(
                        "YokuAP.dll not found in the downloaded archive. The mod asset " +
                        "layout may have changed. Download manually from " + ModRepoUrl + ".");
            }
            else
            {
                // Bare .dll download — just move it into place.
                File.Copy(tempFile, destDll, overwrite: true);
            }

            progress.Report((pctEnd, "Mod DLL installed."));
        }
        finally
        {
            try { if (File.Exists(tempFile))      File.Delete(tempFile); }      catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, true); } catch { }
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

            progress.Report((dlEnd, "Extracting BepInEx into your Yoku folder..."));
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

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the install-dir override + an
    // informational version stamp) in its OWN JSON file so it stays a single
    // self-contained source file and does not modify Core/SettingsStore. BOM-less
    // UTF-8, read-modify-write (same approach as TUNIC / Messenger / Noita).

    private sealed class YokuSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private YokuSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<YokuSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt — use defaults */ }
        return new();
    }

    private void SaveSettings(YokuSettings s)
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
