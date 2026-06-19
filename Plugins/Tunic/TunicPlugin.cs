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

namespace LauncherV2.Plugins.Tunic;

// ═══════════════════════════════════════════════════════════════════════════════
// TunicPlugin — install / launch for "TUNIC" (Andrew Shouldice / Finji, 2022)
// played through the TUNIC Randomizer mod by silent-destroyer, which contains the
// in-game Archipelago Multiworld client. This is a NATIVE "ConnectsItself"
// integration in the same family as the shipped Hollow Knight / Stardew Valley /
// Jak plugins: the game itself speaks to the AP server (no emulator, no Lua
// bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of TUNIC (Steam appid 553420), and Archipelago support is delivered as a
// BepInEx mod added on top. The honest integration ceiling — exactly like the
// shipped Hollow Knight / Stardew Valley plugins — is "automate what is possible,
// guide the irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "TUNIC" (verified against
//     worlds/tunic/__init__.py: `class TunicWorld(World): ... game = "TUNIC"`).
//     GameId here = "tunic". TUNIC is a CORE Archipelago world (it ships inside
//     Archipelago itself — no custom_worlds drop needed to generate).
//
//   * THE MOD repo is silent-destroyer/tunic-randomizer (verified live
//     2026-06-14). Its README states plainly: "This mod contains both the
//     standalone, single-player randomizer and the Archipelago Multiworld
//     integration." (The older silent-destroyer/tunic-randomizer-archipelago repo
//     is a now-superseded AP-only mirror; the combined main repo is the canonical
//     source.) The latest release (4.2.7, 2025-07-11) ships THREE assets:
//         TunicRandomizer.zip   (the mod — install into BepInEx/plugins)
//         tunic.apworld         (the AP world, for the AP host — NOT used here;
//                                TUNIC is already core, so the launcher ignores it)
//         TUNIC.yaml            (a sample player YAML — informational)
//     The mod zip extracts a "Tunic Randomizer" folder containing TunicRandomizer.dll
//     (+ supporting .dll files) that drops into <TUNIC>/BepInEx/plugins/, giving
//     the verified path <TUNIC>/BepInEx/plugins/Tunic Randomizer/TunicRandomizer.dll.
//
//   * CRITICAL HONESTY — BepInEx IS A SEPARATE PREREQUISITE, NOT BUNDLED. TUNIC is
//     a Unity IL2CPP game, so the mod needs BepInEx 6.0.0-pre.1 (Unity IL2CPP x64),
//     downloaded SEPARATELY from the BepInEx releases and extracted into the TUNIC
//     install root. The mod release zip does NOT contain BepInEx. BepInEx 6 is a
//     PORTABLE zip (no wizard), so this plugin CAN best-effort stage it AND the mod
//     for you — but it then leaves the run-once-to-generate-config and the in-game
//     connection to you, and says so. Faking a fully-automated "ready to play"
//     would be dishonest theatre.
//
//   * CONNECTION is made IN-GAME (verified against the official AP setup guide and
//     the mod README): after the mod is installed, launch the game, on the Title
//     Screen pick "Archipelago" under "Randomizer Mode", click "Edit AP Config",
//     and type Player / Hostname / Port / Password into the in-game fields — then
//     Close, and look for "Status: Connected!". There is an ArchipelagoSettings.json
//     behind those fields (the in-game "Open Settings File" button edits it; it
//     lives under %localappdata%low\Andrew Shouldice\Secret Legend\Randomizer), but
//     the OFFICIALLY DOCUMENTED connection method is the IN-GAME menu, and the
//     exact JSON property names are NOT officially documented. Per an honest
//     "don't invent an undocumented prefill" stance (same as Hollow Knight / Jak),
//     this plugin does NOT write that file or fake a connection prefill — the
//     settings panel + post-install note surface the session's host/port/slot for
//     the user to type into the in-game fields, and point at the in-game "Open
//     Settings File" option for power users.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam TUNIC install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\TUNIC via appmanifest_553420.acf. A manual install-dir
//      OVERRIDE (settings folder picker) is also supported and takes precedence;
//      it is validated (must contain TUNIC.exe) and persisted in this plugin's OWN
//      sidecar (Games/ROMs/tunic/tunic_launcher.json) — Core/SettingsStore is NOT
//      modified.
//   2. INSTALL/UPDATE (best effort) = (a) if BepInEx is not present in the detected
//      install, download the pinned BepInEx 6 IL2CPP x64 zip and extract it into
//      the TUNIC root; (b) download the mod's "TunicRandomizer.zip" from the real
//      release and extract its "Tunic Randomizer" folder into
//      <TUNIC>/BepInEx/plugins/. Both are portable zips. The plugin then presents
//      clear, numbered, Hollow-Knight-style guided steps + links (mod repo, the
//      official AP setup guide, BepInEx, archipelago.gg) so the user can run the
//      game once (to let BepInEx generate its config) and connect in-game. Never a
//      fake one-click.
//   3. LAUNCH = run TUNIC.exe from the detected/override install; if the exe cannot
//      be found but Steam is present, fall back to steam://rungameid/553420.
//      ConnectsItself = true (the mod owns the slot — the launcher must NOT hold
//      its own ApClient on it). SupportsStandalone = true (plain TUNIC runs fine
//      without AP). No connection prefill (entered in-game), stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Hollow-Knight/Jak-style) ──
//   * "Installed" is judged by the presence of TunicRandomizer.dll under a
//     detected/override TUNIC install's BepInEx tree (case-insensitive, recursive)
//     — NOT by an OUR-OWN version stamp, because the user may instead install the
//     mod by hand (or via a mod manager), which this launcher should honor. If no
//     TUNIC install is detected, the tile simply reads "not installed".
//   * Steam library parsing is defensive: a hand-written tolerant VDF scan that
//     pulls quoted "path" values; any failure degrades to "TUNIC not found" rather
//     than throwing.
//   * No plaintext AP password is ever written by this plugin (the connection is
//     entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class TunicPlugin : IGamePlugin
{
    // ── Constants — the TUNIC Randomizer mod (real repo, verified 2026-06-14) ──
    private const string MOD_OWNER = "silent-destroyer";
    private const string MOD_REPO  = "tunic-randomizer";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl  = "https://archipelago.gg/tutorial/TUNIC/setup_en";
    private const string GameInfoUrl    = "https://archipelago.gg/games/TUNIC/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // BepInEx 6 (Unity IL2CPP x64) — the SEPARATE mod-loader prerequisite. Portable
    // zip (no wizard), so the plugin can stage it. Pinned to the build the TUNIC
    // setup guide names (6.0.0-pre.1) for compatibility.
    private const string BepInExSite = "https://github.com/BepInEx/BepInEx/releases";
    private const string BepInExVersion = "6.0.0-pre.1";
    private static readonly string BepInExZipUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v6.0.0-pre.1/" +
        "BepInEx_UnityIL2CPP_x64_6.0.0-pre.1.zip";

    // Steam — TUNIC appid 553420.
    private const string TunicSteamAppId = "553420";
    private static readonly string SteamRunUrl = $"steam://rungameid/{TunicSteamAppId}";

    /// The standard Steam install sub-folder name for TUNIC.
    private const string SteamCommonFolderName = "TUNIC";

    /// The base-game executable name.
    private const string TunicExeName = "TUNIC.exe";

    /// The mod folder + primary DLL placed under BepInEx/plugins (verified).
    private const string ModFolderName = "Tunic Randomizer";
    private const string ModPrimaryDll = "TunicRandomizer.dll";

    /// Pinned fallback for the mod when the GitHub API is unreachable. 4.2.7
    /// verified live 2026-06-14; the mod asset is "TunicRandomizer.zip".
    private const string FallbackVersion = "4.2.7";
    private const string FallbackZipName = "TunicRandomizer.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "tunic";
    public string DisplayName => "TUNIC";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/tunic/__init__.py
    /// (`class TunicWorld(World): ... game = "TUNIC"`).
    public string ApWorldName => "TUNIC";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "tunic.png");

    public string ThemeAccentColor => "#C8923A";   // golden fox / Secret Legend
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "TUNIC, the 2022 isometric action-adventure by Andrew Shouldice (Finji), " +
        "played through the TUNIC Randomizer mod by silent-destroyer — which bundles " +
        "an in-game Archipelago client, so the game connects to the multiworld " +
        "itself with no emulator and no bridge. Pages of the in-game manual, key " +
        "items, abilities, money and more are shuffled across the multiworld. You " +
        "bring your own copy of TUNIC (owned on Steam); the integration runs on " +
        "BepInEx, the Unity mod loader. The launcher detects your Steam install and " +
        "can stage BepInEx and the Archipelago mod into it, and guides the rest. " +
        "You connect to your server from the in-game Archipelago menu.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the TUNIC Randomizer mod DLL is present in a detected/
    /// override TUNIC install's BepInEx tree. (We do NOT gate on our own stamp —
    /// the user may have installed the mod by hand, which we honor.)
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads (the mod / BepInEx zips) and any working
    /// files. The actual mod is extracted INTO the TUNIC install's BepInEx folder,
    /// not here. Exposed as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Tunic");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Hollow Knight /
    /// Stardew / Jak). Per the brief, lives under Games/ROMs/tunic/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "tunic_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The TUNIC Randomizer mod reports checks/items/goal to the AP server itself —
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
            // Best-effort: read the version we stamped next to a direct install if
            // present; otherwise report "installed" when the mod DLL exists.
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
        // 0. We need a TUNIC install to drop BepInEx + the mod into. Prefer an
        //    explicit override; else auto-detect the Steam install.
        progress.Report((2, "Locating your TUNIC installation..."));
        string? tunicDir = ResolveTunicDir();
        if (tunicDir == null)
            throw new InvalidOperationException(
                "Could not find a TUNIC installation. Open this game's Settings and " +
                "pick your TUNIC folder (the one containing TUNIC.exe), or install " +
                "TUNIC via Steam first. The Archipelago mod is added on top of your " +
                "own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback).
        progress.Report((6, "Checking the latest TUNIC Randomizer release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the TUNIC Randomizer mod download on GitHub. Check " +
                "your internet connection, or download it manually from " +
                ModRepoUrl + "/releases — see Settings for the guided steps. The mod " +
                "repo is " + ModRepoUrl + ".");

        // 2. Ensure BepInEx 6 (IL2CPP x64) is present — it is a SEPARATE, portable
        //    prerequisite the mod zip does NOT bundle. If it is already there
        //    (winhttp.dll / BepInEx folder), we leave it alone.
        if (!BepInExPresent(tunicDir))
        {
            progress.Report((12, "Staging BepInEx 6 (Unity IL2CPP) into your TUNIC folder..."));
            await DownloadAndExtractZipToDirAsync(
                BepInExZipUrl, $"tunic-bepinex-{BepInExVersion}", tunicDir, 12, 45, progress, ct);
        }
        else
        {
            progress.Report((45, "BepInEx already present — keeping your existing install."));
        }

        // 3. Download + extract the mod's "Tunic Randomizer" folder INTO
        //    <TUNIC>/BepInEx/plugins/. The zip already nests the "Tunic Randomizer"
        //    folder, so extracting into the plugins dir lands it correctly.
        string pluginsDir = Path.Combine(tunicDir, "BepInEx", "plugins");
        await DownloadAndExtractModAsync(zipUrl, version, pluginsDir, 48, 92, progress, ct);

        // 4. Stamp the version next to our sidecar so the tile can show it. (This is
        //    informational only — IsInstalled is judged by the DLL's presence.)
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepOk = BepInExPresent(tunicDir);
        progress.Report((100,
            $"Staged the TUNIC Randomizer mod {version} into your BepInEx\\plugins folder" +
            (bepOk ? " (BepInEx is present)." : ".") +
            " To play: launch TUNIC ONCE so BepInEx finishes setting up, confirm " +
            "\"Randomizer Mod Ver. " + version + "\" shows at the top-left of the title " +
            "screen, then on the Title Screen pick Archipelago under Randomizer Mode, " +
            "click Edit AP Config, and enter your server details. Open Settings for the " +
            "guided steps and links. (This launcher cannot pre-fill the connection — it " +
            "is entered in-game.)"));
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
        // HONEST: the AP server connection for TUNIC is entered in the IN-GAME
        // Archipelago menu (Title Screen -> Randomizer Mode -> Archipelago -> Edit
        // AP Config -> Player / Hostname / Port / Password). There is no documented
        // command-line / config prefill this launcher can apply (verified — see
        // header). So launching from this tile just starts the game; the user
        // connects in-game with the session credentials (the settings panel + note
        // surface those values to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartTunic();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) TUNIC runs perfectly well.
    public bool SupportsStandalone => true;

    /// The TUNIC Randomizer mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartTunic();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started TUNIC from here. Kill what we launched.
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No plaintext AP password is ever written to disk by this plugin (the
        // connection is entered in-game), so there is nothing to scrub.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The TUNIC Randomizer mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game (the "Status: Connected!" line).
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid TUNIC folder contains TUNIC.exe
    /// (and the TUNIC_Data folder is next to it). Return null when acceptable, else
    /// a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your TUNIC install folder.";

        if (LooksLikeTunicDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeTunicDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a TUNIC installation. Pick the folder that " +
               "contains TUNIC.exe (for Steam this is usually " +
               @"...\steamapps\common\TUNIC).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Unverified-offline honesty header ─────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "TUNIC is your own game (Steam) with the TUNIC Randomizer mod added " +
                   "on top via BepInEx. The launcher detects your Steam install and can " +
                   "stage BepInEx and the Archipelago mod files into it, but BepInEx is a " +
                   "separate prerequisite and you must run the game once so it finishes " +
                   "setting up (see the guided steps below). You connect to your server " +
                   "from the in-game Archipelago menu. These external steps are not " +
                   "verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "TUNIC INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? tunicDir    = ResolveTunicDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = tunicDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + tunicDir
                : "Detected Steam install: " + tunicDir)
            : "TUNIC not detected. Pick your install folder below, or install TUNIC " +
              "via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = tunicDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // BepInEx status line
        bool bepOk = tunicDir != null && BepInExPresent(tunicDir);
        panel.Children.Add(new TextBlock
        {
            Text = tunicDir == null
                    ? ""
                    : (bepOk
                        ? "BepInEx found in your TUNIC folder."
                        : "BepInEx not found yet — Install on the Play tab stages it, or get " +
                          "it from the BepInEx releases (link below)."),
            FontSize = 11, Foreground = bepOk ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        // mod status line
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                    ? "TUNIC Randomizer mod found: " + modDll
                    : "TUNIC Randomizer mod not found in BepInEx\\plugins yet (use Install on " +
                      "the Play tab).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? tunicDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your TUNIC install folder (the one containing TUNIC.exe). " +
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
                Title            = "Select your TUNIC install folder (contains TUNIC.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? tunicDir ?? "")
                                   ? (overrideDir ?? tunicDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a TUNIC folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeTunicDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeTunicDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 553420). Use this " +
                   "picker for a non-standard Steam library or another store.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (this session) ────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "On the Title Screen, pick Archipelago under Randomizer Mode, click " +
                   "Edit AP Config, and enter Player (your slot name), Hostname, Port, and " +
                   "Password (if any). Close, and look for \"Status: Connected!\". Power " +
                   "users can instead use the in-game \"Open Settings File\" button to edit " +
                   "ArchipelagoSettings.json directly. This launcher does not pre-fill the " +
                   "connection.",
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
            "1. Own TUNIC (on Steam). Install it if you have not. Use the picker above if it " +
                "was not detected.",
            "2. Install BepInEx 6 (Unity IL2CPP x64) into your TUNIC folder. The Install button " +
                "on the Play tab stages it for you, or download it from the BepInEx releases " +
                "(link below) and extract it into your TUNIC folder.",
            "3. Install the TUNIC Randomizer mod: Install on the Play tab downloads the mod and " +
                "drops its \"Tunic Randomizer\" folder into BepInEx\\plugins, or do it by hand " +
                "from the mod releases (link below).",
            "4. Launch TUNIC ONCE so BepInEx finishes setting up. Confirm \"Randomizer Mod " +
                "Ver. x.y.z\" appears at the top-left of the title screen.",
            "5. To play: on the Title Screen pick Archipelago under Randomizer Mode, click Edit " +
                "AP Config, enter your Player / Hostname / Port / Password, then Close. You " +
                "should see \"Status: Connected!\".",
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
            ("TUNIC Randomizer (GitHub) ↗",  ModRepoUrl),
            ("TUNIC Setup Guide ↗",          SetupGuideUrl),
            ("TUNIC Guide (AP) ↗",           GameInfoUrl),
            ("BepInEx (releases) ↗",         BepInExSite),
            ("Archipelago Official ↗",       ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2), Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
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
            using var doc  = JsonDocument.Parse(json);
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

    /// "v4.2.7" → "4.2.7" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the mod zip download URL. Prefers
    /// the "TunicRandomizer.zip" asset (the mod), NOT the tunic.apworld / .yaml
    /// sidecar assets. Falls back to the pinned 4.2.7 direct URL when the API is
    /// unreachable.
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
                string? preferred = null;   // the mod zip (TunicRandomizer*.zip)
                string? anyZip    = null;   // any .zip
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;     // skip .apworld / .yaml

                    anyZip ??= url;
                    if (preferred == null && lower.Contains("tunicrandomizer"))
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

    // ── Private helpers — Steam / TUNIC detection ─────────────────────────────

    /// The TUNIC install dir to use: the override (if set and valid) wins, else the
    /// Steam-detected install. Null when nothing is found.
    private string? ResolveTunicDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeTunicDir(ov))
            return ov;

        try { return DetectSteamTunicDir(); }
        catch { return null; }
    }

    /// A folder "looks like" TUNIC if it has TUNIC.exe and/or the TUNIC_Data folder.
    private static bool LooksLikeTunicDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, TunicExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, "TUNIC_Data"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// True when BepInEx appears installed in a TUNIC folder. BepInEx 6 (IL2CPP)
    /// drops a "BepInEx" folder plus a winhttp.dll proxy at the game root.
    private static bool BepInExPresent(string tunicDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(tunicDir) || !Directory.Exists(tunicDir)) return false;
            if (Directory.Exists(Path.Combine(tunicDir, "BepInEx"))) return true;
            if (File.Exists(Path.Combine(tunicDir, "winhttp.dll"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam TUNIC install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_553420.acf exists → steamapps\common\TUNIC.
    private static string? DetectSteamTunicDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{TunicSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    // Prefer the installdir named in the manifest; fall back to the
                    // conventional "TUNIC" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeTunicDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeTunicDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath). Both are tried; duplicates are harmless.
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

    /// Steam stores its SteamPath with forward slashes; normalize for Path APIs.
    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    /// All Steam library roots: the Steam root itself plus every "path" entry in
    /// steamapps\libraryfolders.vdf. Tolerant text scan (the VDF is a simple quoted
    /// key/value tree; we only need the path values).
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

    /// Pull every  "path"   "<value>"  pair out of a libraryfolders.vdf body.
    /// Handles the Steam-VDF escaping of backslashes (\\ → \).
    private static IEnumerable<string> ExtractVdfPaths(string text)
    {
        const string key = "\"path\"";
        int i = 0;
        while ((i = text.IndexOf(key, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            i += key.Length;
            // find the opening quote of the value
            int open = text.IndexOf('"', i);
            if (open < 0) yield break;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) yield break;

            string raw = text.Substring(open + 1, close - open - 1);
            yield return raw.Replace("\\\\", "\\");
            i = close + 1;
        }
    }

    /// Read the "installdir" value from an appmanifest_*.acf (same quoted-pair
    /// format as VDF). Returns null if absent.
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

    /// Safe registry string read; null on any failure (key/value missing, etc.).
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

    /// Find TunicRandomizer.dll under the detected/override TUNIC install's BepInEx
    /// tree (recursive, case-insensitive). Returns the dll path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? tunic = ResolveTunicDir();
            if (tunic == null) return null;
            string bepInExDir = Path.Combine(tunic, "BepInEx");
            if (!Directory.Exists(bepInExDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(bepInExDir, "*.dll", SearchOption.AllDirectories))
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

    /// Start TUNIC: prefer the exe in the detected/override install; if that cannot
    /// be found but Steam is present, fall back to the steam:// URL. Surfaces a
    /// clear message rather than failing opaquely.
    private void StartTunic()
    {
        string? tunic = ResolveTunicDir();
        string? exe   = tunic != null ? Path.Combine(tunic, TunicExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = tunic!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start TUNIC.");

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
                IsRunning = true; // best-effort; Steam owns the process, so we won't track exit
                return;
            }
            catch { /* fall through to the error below */ }
        }

        throw new FileNotFoundException(
            "Could not find TUNIC.exe. Open this game's Settings and pick your TUNIC " +
            "install folder, or install TUNIC via Steam.",
            TunicExeName);
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    /// Download the mod's TunicRandomizer.zip and extract its "Tunic Randomizer"
    /// folder into <TUNIC>/BepInEx/plugins/. The zip already nests the folder, so
    /// extracting into the plugins dir lands it at plugins/Tunic Randomizer/. A
    /// stale mod folder is replaced cleanly so an update is clean.
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
            $"tunic-randomizer-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"tunic-randomizer-x-{version}-{Guid.NewGuid():N}");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 7 / 10;
            await DownloadFileAsync(zipUrl, tempZip,
                $"Downloading TUNIC Randomizer {version}...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Installing the mod into BepInEx\\plugins..."));
            Directory.CreateDirectory(pluginsDir);
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // Locate the "Tunic Randomizer" folder within the extracted tree (the
            // zip nests it; be defensive about an extra wrapper level). If we can't
            // find a named folder but the DLL is loose at the root, treat the root
            // as the source.
            string? srcModDir = FindModSourceDir(tempExtract);

            string destModDir = Path.Combine(pluginsDir, ModFolderName);
            if (Directory.Exists(destModDir))
            {
                try { Directory.Delete(destModDir, recursive: true); } catch { /* in-use — overwrite below */ }
            }

            if (srcModDir != null)
            {
                CopyDirectory(srcModDir, destModDir);
            }
            else
            {
                // Last resort: dump the whole extract into the dest folder so the
                // DLLs at least land under plugins\Tunic Randomizer.
                CopyDirectory(tempExtract, destModDir);
            }

            progress.Report((pctEnd, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Find the source mod folder inside an extracted tree: a directory named
    /// "Tunic Randomizer", else the directory that directly contains
    /// TunicRandomizer.dll. Returns null when neither is found.
    private static string? FindModSourceDir(string root)
    {
        try
        {
            // Exact-name folder anywhere in the tree.
            foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dir).Equals(ModFolderName, StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
            // Else: the folder that holds the primary DLL.
            foreach (string dll in Directory.EnumerateFiles(root, ModPrimaryDll, SearchOption.AllDirectories))
            {
                return Path.GetDirectoryName(dll);
            }
        }
        catch { /* ignore */ }
        return null;
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

            progress.Report((dlEnd, "Extracting BepInEx into your TUNIC folder..."));
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
    // This plugin keeps its launcher-side settings (the install-dir override + an
    // informational version stamp) in its OWN JSON file so it stays a single
    // self-contained source file and does not modify Core/SettingsStore. BOM-less
    // UTF-8, read-modify-write (same approach as Hollow Knight / Stardew / Jak).

    private sealed class TunicSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private TunicSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<TunicSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(TunicSettings s)
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
    private void SaveOverrideDir(string p) { var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s); }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    private void WriteStampedVersion(string v) { var s = LoadSettings(); s.ModVersion = v; SaveSettings(s); }
}
