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

namespace LauncherV2.Plugins.Cuphead;

// ═══════════════════════════════════════════════════════════════════════════════
// CupheadPlugin — install / launch for "Cuphead" (Studio MDHR, 2017)
// played through the CupheadArchipelago mod by JKLeckr, which contains the
// in-game Archipelago Multiworld client. This is a NATIVE "ConnectsItself"
// integration in the same family as the shipped Hollow Knight / Stardew Valley /
// TUNIC plugins: the game itself speaks to the AP server (no emulator, no Lua
// bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Cuphead (Steam appid 268910), and Archipelago support is delivered as a
// BepInEx 5.x Mono mod added on top. The verified facts:
//
//   * THE AP WORLD game string is "Cuphead" (verified against
//     varis.py: `game_name: str = "Cuphead"` and
//     __init__.py: `GAME_NAME: ClassVar[str] = "Cuphead"`
//                  `game: ClassVar[str] = GAME_NAME`
//     and APClient.cs: `protected const string GAME_NAME = "Cuphead";`).
//     GameId here = "cuphead". Cuphead ships from
//     https://github.com/JKLeckr/Archipelago-cuphead (a custom apworld).
//
//   * THE MOD repo is JKLeckr/CupheadArchipelagoMod (verified live 2026-06-14).
//     Latest release (alpha03d, 2026-05-10) ships ONE asset:
//         CupheadArchipelago-alpha03d_win64.zip  (the mod for BepInEx)
//     The primary BepInEx plugin: GUID "com.JKLeckr.CupheadArchipelago",
//     process target "Cuphead.exe" (verified from Plugin.cs).
//     Expected DLL: CupheadArchipelago.dll in BepInEx/plugins/CupheadArchipelago/.
//
//   * CRITICAL HONESTY — BepInEx IS A SEPARATE PREREQUISITE, NOT BUNDLED.
//     Cuphead is a Unity Mono game, so the mod needs BepInEx 5.x (Mono x64),
//     downloaded SEPARATELY from the BepInEx releases. The mod release zip does
//     NOT contain BepInEx. BepInEx 5 is a PORTABLE zip (no wizard), so this
//     plugin CAN best-effort stage it AND the mod for you — but it then leaves
//     the first-run-to-generate-config and the in-game connection to you.
//
//   * CONNECTION is made IN-GAME (verified against the official AP setup guide
//     setup_en.md): after the mod is installed, launch the game, select an empty
//     save slot, press C+Z (keyboard default) to show the AP setup menu, set it
//     to enabled, enter server / slot / password, close the menu, then start the
//     save. There is a BepInEx config file at
//     BepInEx/config/com.JKLeckr.CupheadArchipelago.cfg (mod options only, not
//     the connection data). Connection is slot-stored in the save data itself.
//     The officially documented connection method is the IN-GAME menu. This
//     plugin does NOT write those save files — the settings panel surfaces the
//     session host/slot for the user to type in-game.
//
//   * STEAM AppID: 268910 (Cuphead base game).
//     DLC AppID: 1117850 (The Delicious Last Course — optional).
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Cuphead install via Windows registry → libraryfolders.vdf
//      → appmanifest_268910.acf. Manual install-dir OVERRIDE (settings folder
//      picker) is also supported and takes precedence; persisted in the plugin's
//      OWN sidecar (Games/ROMs/cuphead/cuphead_launcher.json).
//   2. INSTALL/UPDATE (best effort) = (a) if BepInEx 5.x is not present, download
//      the pinned BepInEx 5.4.23.2 Mono x64 zip and extract into the Cuphead root;
//      (b) download the mod zip from the real release and extract the
//      CupheadArchipelago folder into <Cuphead>/BepInEx/plugins/. Presents clear
//      guided steps + links so the user can run the game once and connect in-game.
//   3. LAUNCH = run Cuphead.exe from the detected/override install; if the exe
//      cannot be found but Steam is present, fall back to steam://rungameid/268910.
//      ConnectsItself = true (the mod owns the slot — launcher must NOT hold its
//      own ApClient). SupportsStandalone = true (plain Cuphead works fine).
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time") ──────────────────────────
//   * "Installed" is judged by the presence of CupheadArchipelago.dll under a
//     detected/override Cuphead install's BepInEx tree (recursive, case-insensitive)
//     — NOT by our own version stamp, because the user may install the mod by hand.
//   * Steam library parsing is defensive: a tolerant VDF scan; any failure degrades
//     to "Cuphead not found" rather than throwing.
//   * No plaintext AP password is ever written by this plugin.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CupheadPlugin : IGamePlugin
{
    // ── Constants — the CupheadArchipelago mod (real repo, verified 2026-06-14) ──
    private const string MOD_OWNER   = "JKLeckr";
    private const string MOD_REPO    = "CupheadArchipelagoMod";
    private const string ModRepoUrl  = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Cuphead/setup_en";
    private const string GameInfoUrl     = "https://archipelago.gg/games/Cuphead/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // BepInEx 5.4.23.2 (Unity Mono x64) — the SEPARATE mod-loader prerequisite.
    // Portable zip (no wizard), so the plugin can stage it. Pinned to the stable
    // 5.x series (what the setup guide names: "BepInEx 5.x").
    private const string BepInExSite    = "https://github.com/BepInEx/BepInEx/releases";
    private const string BepInExVersion = "5.4.23.2";
    private static readonly string BepInExZipUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/" +
        "BepInEx_win_x64_5.4.23.2.zip";

    // Steam — Cuphead appid 268910.
    private const string CupheadSteamAppId = "268910";
    private static readonly string SteamRunUrl = $"steam://rungameid/{CupheadSteamAppId}";

    /// The standard Steam install sub-folder name for Cuphead.
    private const string SteamCommonFolderName = "Cuphead";

    /// The base-game executable name (verified from Plugin.cs BepInProcess attribute).
    private const string CupheadExeName = "Cuphead.exe";

    /// The BepInEx plugin folder name and primary DLL (verified from Plugin.cs:
    /// GUID "com.JKLeckr.CupheadArchipelago", folder named "CupheadArchipelago").
    private const string ModFolderName = "CupheadArchipelago";
    private const string ModPrimaryDll = "CupheadArchipelago.dll";

    /// Pinned fallback for the mod when the GitHub API is unreachable.
    /// alpha03d verified live 2026-06-14; asset "CupheadArchipelago-alpha03d_win64.zip".
    private const string FallbackVersion = "alpha03d";
    private const string FallbackZipName = $"CupheadArchipelago-{FallbackVersion}_win64.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "cuphead";
    public string DisplayName => "Cuphead";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against varis.py (`game_name: str = "Cuphead"`),
    /// __init__.py (`GAME_NAME = "Cuphead"`, `game = GAME_NAME`),
    /// and APClient.cs (`const string GAME_NAME = "Cuphead"`).
    public string ApWorldName => "Cuphead";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "cuphead.png");

    public string ThemeAccentColor => "#D4433A";   // classic Cuphead red

    public string[] GameBadges => new[] { "Steam · needs mod", "BepInEx 5.x" };

    public string Description =>
        "Cuphead, the 2017 run-and-gun action game by Studio MDHR, played through " +
        "the CupheadArchipelago mod by JKLeckr — which bundles an in-game Archipelago " +
        "Multiworld client, so the game connects to the multiworld itself with no " +
        "emulator and no bridge. Weapons, charms, abilities, boss contracts and more " +
        "are shuffled across the multiworld. You bring your own copy of Cuphead (owned " +
        "on Steam); the integration runs on BepInEx 5.x, the Unity Mono mod loader. " +
        "The launcher detects your Steam install and can stage BepInEx and the " +
        "Archipelago mod into it, and guides the rest. You connect to your server " +
        "from the in-game Archipelago setup menu (press C+Z on the keyboard).";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means CupheadArchipelago.dll is present in a detected/override
    /// Cuphead install's BepInEx tree. We do NOT gate on our own stamp — the user
    /// may have installed the mod by hand, which we honor.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── ConnectsItself / Standalone ───────────────────────────────────────────

    /// The CupheadArchipelago mod owns the slot connection. The launcher must NOT
    /// hold its own ApClient on this slot while the mod is connected.
    public bool ConnectsItself => true;

    /// Plain Cuphead runs perfectly well without AP.
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads (the mod / BepInEx zips) and any working
    /// files. The actual mod is extracted INTO the Cuphead install's BepInEx folder,
    /// not here. Exposed as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Cuphead");

    /// This plugin's OWN settings sidecar — kept out of the shared SettingsStore
    /// so the plugin stays a single self-contained file. Lives under Games/ROMs/cuphead/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "cuphead_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The CupheadArchipelago mod reports checks/items/goal to the AP server itself —
    // the launcher relays nothing. These exist for interface compatibility
    // (ConnectsItself = true).
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
        // 0. We need a Cuphead install to drop BepInEx + the mod into.
        progress.Report((2, "Locating your Cuphead installation..."));
        string? cupheadDir = ResolveCupheadDir();
        if (cupheadDir == null)
            throw new InvalidOperationException(
                "Could not find a Cuphead installation. Open this game's Settings and " +
                "pick your Cuphead folder (the one containing Cuphead.exe), or install " +
                "Cuphead via Steam first. The Archipelago mod is added on top of your " +
                "own copy of the game.");

        // 1. Resolve the latest mod release (with pinned fallback).
        progress.Report((6, "Checking the latest CupheadArchipelago release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the CupheadArchipelago mod download on GitHub. Check " +
                "your internet connection, or download it manually from " +
                ModRepoUrl + "/releases — see Settings for the guided steps.");

        // 2. Ensure BepInEx 5.x (Mono x64) is present — it is a SEPARATE, portable
        //    prerequisite the mod zip does NOT bundle. If it is already there
        //    (winhttp.dll / BepInEx folder), we leave it alone.
        if (!BepInExPresent(cupheadDir))
        {
            progress.Report((12, "Staging BepInEx 5.x (Mono x64) into your Cuphead folder..."));
            await DownloadAndExtractZipToDirAsync(
                BepInExZipUrl, $"cuphead-bepinex-{BepInExVersion}", cupheadDir, 12, 45, progress, ct);
        }
        else
        {
            progress.Report((45, "BepInEx already present — keeping your existing install."));
        }

        // 3. Download + extract the mod into <Cuphead>/BepInEx/plugins/.
        string pluginsDir = Path.Combine(cupheadDir, "BepInEx", "plugins");
        await DownloadAndExtractModAsync(zipUrl, version, pluginsDir, 48, 92, progress, ct);

        // 4. Stamp the version next to our sidecar so the tile can show it.
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepOk = BepInExPresent(cupheadDir);
        progress.Report((100,
            $"Staged CupheadArchipelago {version} into your BepInEx\\plugins folder" +
            (bepOk ? " (BepInEx is present)." : ".") +
            " To play: launch Cuphead ONCE so BepInEx finishes setting up, then select " +
            "an empty save slot and press C+Z (keyboard default) to open the Archipelago " +
            "setup menu. Set it to enabled and enter your server details. Open Settings " +
            "for the guided steps and links. (This launcher cannot pre-fill the connection " +
            "— it is entered in-game per save slot.)"));
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
        // HONEST: the AP server connection for Cuphead is entered IN-GAME
        // (per-save-slot, via the in-game AP setup menu: press C+Z on the title
        // screen or in a save slot — Player, Hostname, Port, Password). There is
        // no command-line or config file prefill this launcher can apply.
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartCuphead();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartCuphead();
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
        // The CupheadArchipelago mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// A valid Cuphead folder contains Cuphead.exe. Returns null when acceptable,
    /// else a short human-readable reason string.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Cuphead install folder.";

        if (LooksLikeCupheadDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeCupheadDir(nested)) return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Cuphead installation. Pick the folder that " +
               "contains Cuphead.exe (for Steam this is usually " +
               @"...\steamapps\common\Cuphead).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Cuphead is your own game (Steam) with the CupheadArchipelago mod " +
                   "added on top via BepInEx 5.x. The launcher detects your Steam install " +
                   "and can stage BepInEx and the mod files into it, but you must launch " +
                   "the game once so BepInEx finishes setting up, then connect in-game via " +
                   "the AP setup menu (C+Z). These external steps are not verified by this " +
                   "launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CUPHEAD INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? cupheadDir  = ResolveCupheadDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = cupheadDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + cupheadDir
                : "Detected Steam install: " + cupheadDir)
            : "Cuphead not detected. Pick your install folder below, or install Cuphead " +
              "via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = cupheadDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // BepInEx status line
        bool bepOk = cupheadDir != null && BepInExPresent(cupheadDir);
        panel.Children.Add(new TextBlock
        {
            Text = cupheadDir == null
                    ? ""
                    : (bepOk
                        ? "BepInEx found in your Cuphead folder."
                        : "BepInEx not found yet — Install on the Play tab stages it, or get " +
                          "it from the BepInEx releases (link below)."),
            FontSize = 11, Foreground = bepOk ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        // Mod status line
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                    ? "CupheadArchipelago mod found: " + modDll
                    : "CupheadArchipelago mod not found in BepInEx\\plugins yet (use Install on " +
                      "the Play tab).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? cupheadDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Cuphead install folder (the one containing Cuphead.exe). " +
                          "Detected from Steam automatically; set it here to override " +
                          "(non-standard Steam library, or another store).",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Cuphead install folder (contains Cuphead.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? cupheadDir ?? "")
                                   ? (overrideDir ?? cupheadDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Cuphead folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeCupheadDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeCupheadDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 268910). Use this " +
                   "picker for a non-standard Steam library or another store.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (this session) ────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (entered in-game per save slot)",
            FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Select an empty save slot (the slot must be empty to enable AP on it). " +
                   "Press C+Z (keyboard default) to open the Archipelago setup menu. Set it " +
                   "to Enabled, and fill in Server (host:port), Slot (your player name), and " +
                   "Password (if required). Close the menu, then start the save slot — it will " +
                   "show \"AP\" in the corner when Archipelago is enabled. This launcher does " +
                   "not pre-fill the connection — it is entered in-game.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Cuphead (on Steam). Install it if you have not. Use the picker above if it " +
                "was not detected.",
            "2. Install BepInEx 5.x (Mono x64) into your Cuphead folder. The Install button on " +
                "the Play tab stages it for you, or download it from the BepInEx releases (link " +
                "below) and extract it into your Cuphead folder.",
            "3. Install the CupheadArchipelago mod: Install on the Play tab downloads the mod and " +
                "drops the CupheadArchipelago folder into BepInEx\\plugins, or do it by hand from " +
                "the mod releases (link below).",
            "4. Launch Cuphead ONCE so BepInEx finishes setting up. After that first launch you " +
                "will see a BepInEx/config folder appear in your Cuphead directory.",
            "5. To play: select an empty save slot, press C+Z (keyboard default) to show the AP " +
                "setup menu, enable Archipelago, enter your server / slot / password, close the " +
                "menu, and start the slot. It shows \"AP\" in the corner when connected.",
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
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("CupheadArchipelago mod (GitHub) ↗", ModRepoUrl),
            ("Cuphead AP Setup Guide ↗",          SetupGuideUrl),
            ("Cuphead (AP game info) ↗",          GameInfoUrl),
            ("BepInEx releases ↗",                BepInExSite),
            ("Archipelago Official ↗",            ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
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
        // Mod releases are the AP-relevant news for this game.
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

    /// Normalize a release tag: strip a leading 'v' before a digit; else trim.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the mod zip download URL.
    /// Prefers a zip asset that contains "CupheadArchipelago" in the name
    /// (the _win64 build). Falls back to the pinned alpha03d URL when the API
    /// is unreachable.
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
                string? preferred = null;   // the mod zip (CupheadArchipelago*.zip)
                string? anyZip    = null;   // any .zip
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name",     out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    // Prefer the win64 build; also accept any cuphead-arch zip.
                    if (preferred == null &&
                        (lower.Contains("cupheadarchipelago") || lower.Contains("cuphead")))
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

    // ── Private helpers — Steam / Cuphead detection ───────────────────────────

    /// The Cuphead install dir to use: the override (if set and valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolveCupheadDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeCupheadDir(ov))
            return ov;

        try { return DetectSteamCupheadDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Cuphead if it contains Cuphead.exe or Cuphead_Data/.
    private static bool LooksLikeCupheadDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, CupheadExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, "Cuphead_Data"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// True when BepInEx appears installed in a Cuphead folder. BepInEx 5.x
    /// (Mono) drops a "BepInEx" folder plus a winhttp.dll proxy at the game root.
    private static bool BepInExPresent(string cupheadDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(cupheadDir) || !Directory.Exists(cupheadDir)) return false;
            if (Directory.Exists(Path.Combine(cupheadDir, "BepInEx"))) return true;
            if (File.Exists(Path.Combine(cupheadDir, "winhttp.dll"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Cuphead install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_268910.acf exists → steamapps\common\Cuphead.
    private static string? DetectSteamCupheadDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{CupheadSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeCupheadDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeCupheadDir(conventional)) return conventional;
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

        // Last-ditch conventional location.
        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    /// Steam stores SteamPath with forward slashes; normalize for Path APIs.
    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    /// All Steam library roots: the Steam root itself plus every "path" entry in
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

    /// Find CupheadArchipelago.dll under the detected/override Cuphead install's
    /// BepInEx tree (recursive, case-insensitive). Returns the dll path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? cuphead = ResolveCupheadDir();
            if (cuphead == null) return null;
            string bepInExDir = Path.Combine(cuphead, "BepInEx");
            if (!Directory.Exists(bepInExDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(
                bepInExDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(dll);
                if (name.Equals(ModPrimaryDll, StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Cuphead: prefer the exe in the detected/override install; if that
    /// cannot be found but Steam is present, fall back to the steam:// URL.
    private void StartCuphead()
    {
        string? cuphead = ResolveCupheadDir();
        string? exe     = cuphead != null ? Path.Combine(cuphead, CupheadExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = cuphead!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Cuphead.");

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
            catch { /* fall through to the error below */ }
        }

        throw new FileNotFoundException(
            "Could not find Cuphead.exe. Open this game's Settings and pick your Cuphead " +
            "install folder, or install Cuphead via Steam.",
            CupheadExeName);
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    /// Download the mod zip and extract the CupheadArchipelago folder into
    /// <Cuphead>/BepInEx/plugins/. A stale mod folder is replaced cleanly.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string pluginsDir,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"cuphead-ap-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"cuphead-ap-x-{version}-{Guid.NewGuid():N}");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 7 / 10;
            await DownloadFileAsync(zipUrl, tempZip,
                $"Downloading CupheadArchipelago {version}...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Installing the mod into BepInEx\\plugins..."));
            Directory.CreateDirectory(pluginsDir);
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // Locate the mod source folder within the extracted tree.
            string? srcModDir = FindModSourceDir(tempExtract);

            string destModDir = Path.Combine(pluginsDir, ModFolderName);
            if (Directory.Exists(destModDir))
            {
                try { Directory.Delete(destModDir, recursive: true); } catch { /* in-use */ }
            }

            if (srcModDir != null)
            {
                CopyDirectory(srcModDir, destModDir);
            }
            else
            {
                // Last resort: dump the entire extract into the dest folder so
                // DLLs at least land under plugins\CupheadArchipelago.
                CopyDirectory(tempExtract, destModDir);
            }

            progress.Report((pctEnd, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))      File.Delete(tempZip); }      catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Find the source mod folder inside an extracted tree: a directory named
    /// "CupheadArchipelago", else the directory that directly contains
    /// CupheadArchipelago.dll.
    private static string? FindModSourceDir(string root)
    {
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dir).Equals(ModFolderName, StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
            foreach (string dll in Directory.EnumerateFiles(root, ModPrimaryDll, SearchOption.AllDirectories))
            {
                return Path.GetDirectoryName(dll);
            }
        }
        catch { /* ignore */ }
        return null;
    }

    /// Download + extract a portable zip (e.g. BepInEx 5.x) straight into a target dir.
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
                "Downloading BepInEx 5.x...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Extracting BepInEx into your Cuphead folder..."));
            Directory.CreateDirectory(targetDir);
            // BepInEx zips extract their files at the archive root (BepInEx\,
            // winhttp.dll, doorstop_config.ini, ...), so extract straight in.
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

    /// Recursively copy a directory tree (creating the destination).
    private static void CopyDirectory(string sourceDir, string destDir)
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
    // This plugin keeps its launcher-side settings (install-dir override + version
    // stamp) in its OWN JSON file so it stays self-contained and does not modify
    // Core/SettingsStore. BOM-less UTF-8, read-modify-write.

    private sealed class CupheadSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private CupheadSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<CupheadSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(CupheadSettings s)
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
