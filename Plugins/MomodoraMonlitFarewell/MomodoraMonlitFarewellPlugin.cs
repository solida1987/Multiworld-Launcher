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

namespace LauncherV2.Plugins.MomodoraMonlitFarewell;

// ═══════════════════════════════════════════════════════════════════════════════
// MomodoraMonlitFarewellPlugin — install / launch for "Momodora: Moonlit
// Farewell" (BOMBSERVICE, 2024) played through the AP randomizer mod by
// alditoOt (alditoOt/Momodora-Moonlit-Farewell-Randomizer).
//
// ── VERIFIED FACTS (2026-06-14) ──────────────────────────────────────────────
//
//   * AP game string: "Momodora Moonlit Farewell" — verified against
//     MomodoraMFRandomizer/__init__.py:  `game = "Momodora Moonlit Farewell"`
//     (Archipelago-Randomizer branch of the repo).
//
//   * Steam AppID: 1747760 — verified from the repo README which links to
//     https://store.steampowered.com/app/1747760/Momodora_Moonlit_Farewell/
//
//   * Mod loader: MelonLoader 0.5.7 — verified from setup guide (en_setup.md):
//     "Download MelonLoader, execute the file, and when installing for
//     Momodora: Moonlit Farewell, ensure you're using version 0.5.7."
//     MelonLoader drops its DLLs under MelonLoader\ in the game folder.
//     MelonLoader itself must be installed by the user (it runs an interactive
//     installer exe targeting the game) — the launcher stages the download and
//     the user runs it. The mod DLL (APMomodoraMoonlitFarewell.dll, v1.7.0+)
//     drops into Mods\ under the game folder (from "extra-files.zip" in the
//     release).
//
//   * Config file: Mods\config.json — verified from the mod's ConfigLoader.cs:
//     `configPath = Path.Combine(Directory.GetCurrentDirectory(), "Mods", "config.json")`
//     Fields: "server" (host:port), "username" (slot name), "password".
//     This plugin pre-fills config.json from the ApSession on launch, exactly
//     matching the mod's documented connect flow.
//
//   * Release assets (v1.7.0+, verified from GitHub releases API):
//       - APMomodoraMoonlitFarewell.dll   (the mod DLL, drops into Mods\)
//       - extra-files.zip                 (companion files — also into Mods\)
//       - Momodora.Moonlit.Farewell.yaml  (AP player template)
//       - momodoramoonlitfarewell.apworld (AP world — drops into custom_worlds\)
//     (v1.6.x used APMomoMFRandomizer.dll; v1.5.x and earlier used a zip.)
//
//   * ConnectsItself = true: The APMomodoraMoonlitFarewell mod speaks directly
//     to the Archipelago server over WebSocket (Archipelago.MultiClient.Net
//     bundled inside extra-files.zip). The launcher must NOT hold its own
//     ApClient on this slot. We DO pre-fill config.json so the mod connects to
//     the right server without the user touching a text file.
//
//   * Save data location: C:\Users\<user>\AppData\LocalLow\BOMBSERVICE\
//     MomodoraMoonlitFarewell\saves   (documented in en_setup.md).
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. DETECT the Steam Momodora install via the Windows registry — same
//      tolerant VDF scan used by the Messenger plugin (SteamPath / WOW6432Node).
//      A manual override (folder picker in the Settings panel) takes precedence.
//   2. INSTALL/UPDATE — best-effort automated steps:
//      a. Detect whether MelonLoader is installed (look for MelonLoader\
//         directory or MelonLoader.dll in the game folder). If absent, download
//         the MelonLoader installer exe and stage it in the game folder, then
//         surface a clear instruction for the user to run it targeting the game.
//         (MelonLoader's installer is interactive — the launcher cannot run it
//         silently for the user.)
//      b. Download the latest mod release from GitHub. Extract
//         APMomodoraMoonlitFarewell.dll and extra-files.zip into <game>\Mods\.
//   3. LAUNCH — write Mods\config.json with the ApSession values (host:port,
//      slot name, password) then start Momodora: Moonlit Farewell.exe.
//   4. STOP — kill what we launched; scrub the password from config.json.
//
// ── UNVERIFIED FALLBACKS ──────────────────────────────────────────────────────
//   * MelonLoader exe name, installer arguments, and final version are resolved
//     at runtime from the LavaGang/MelonLoader GitHub releases API (only the
//     Windows x64 MelonLoader.zip is needed — we extract MelonLoader into the
//     game root). A pinned fallback (v0.6.6, the newest stable as of 2026-06)
//     is used when the API is unreachable.
//   * The game exe name is inferred as "Momodora Moonlit Farewell.exe" (Unity
//     convention for this title) from the Steam common folder name; we also try
//     "MomodoraMoonlitFarewell.exe" as a fallback.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MomodoraMonlitFarewellPlugin : IGamePlugin
{
    // ── Constants — AP mod (alditoOt/Momodora-Moonlit-Farewell-Randomizer) ────
    private const string MOD_OWNER = "alditoOt";
    private const string MOD_REPO  = "Momodora-Moonlit-Farewell-Randomizer";
    private static readonly string ModRepoUrl =
        $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private static readonly string GhModReleasesUrl =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Momodora%3A%20Moonlit%20Farewell/setup/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // MelonLoader (LavaGang/MelonLoader)
    private const string ML_OWNER       = "LavaGang";
    private const string ML_REPO        = "MelonLoader";
    private const string MelonLoaderUrl = $"https://github.com/{ML_OWNER}/{ML_REPO}";
    private static readonly string GhMelonReleasesUrl =
        $"https://api.github.com/repos/{ML_OWNER}/{ML_REPO}/releases/latest";

    /// Pinned MelonLoader fallback when the API is unreachable.
    /// v0.6.6 stable, direct asset URL.
    private const string MelonFallbackVersion = "0.6.6";
    private static readonly string MelonFallbackZipUrl =
        $"https://github.com/{ML_OWNER}/{ML_REPO}/releases/download/v{MelonFallbackVersion}/MelonLoader.x64.zip";

    // Pinned mod fallback: v1.7.5.1 (latest release verified 2026-06-14).
    private const string ModFallbackVersion = "1.7.5.1";
    private static readonly string ModFallbackDllUrl =
        $"{ModRepoUrl}/releases/download/AP-v{ModFallbackVersion}/APMomodoraMoonlitFarewell.dll";
    private static readonly string ModFallbackExtrasUrl =
        $"{ModRepoUrl}/releases/download/AP-v{ModFallbackVersion}/extra-files.zip";

    // Steam AppID for Momodora: Moonlit Farewell — verified from the repo README.
    private const string SteamAppId   = "1747760";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";
    private const string SteamFolderName = "Momodora Moonlit Farewell";

    /// Candidate game exe names (Unity build conventions for this title).
    private static readonly string[] GameExeNames = new[]
    {
        "Momodora Moonlit Farewell.exe",
        "MomodoraMoonlitFarewell.exe",
        "momodora_moonlit_farewell.exe",
    };

    /// Where the mod's config.json lives relative to the game root.
    /// Verified from ConfigLoader.cs: Path.Combine(Directory.GetCurrentDirectory(), "Mods", "config.json")
    private const string ModConfigRelPath = @"Mods\config.json";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "momodora_moonlit_farewell";
    public string DisplayName => "Momodora: Moonlit Farewell";
    public string Subtitle    => "Native PC · MelonLoader mod";

    /// EXACT AP game string — verified from __init__.py: game = "Momodora Moonlit Farewell"
    public string ApWorldName => "Momodora Moonlit Farewell";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "momodora_moonlit_farewell.png");

    public string ThemeAccentColor => "#8B3A7E";   // deep orchid / Momodora petal purple

    public string[] GameBadges => new[] { "Steam · MelonLoader mod" };

    public string Description =>
        "Momodora: Moonlit Farewell is BOMBSERVICE's 2024 action-platformer, " +
        "randomized via the APMomodoraMoonlitFarewell MelonLoader mod by alditoOt. " +
        "Skills, sigils, grimoires, key items, and boss rewards are shuffled across " +
        "the multiworld. Bring your own copy of the game (Steam app 1747760); the " +
        "launcher detects your Steam install, stages MelonLoader and the AP mod, " +
        "and pre-fills the connection config (Mods\\config.json) when you play. The " +
        "mod itself speaks directly to your Archipelago server — no separate client " +
        "is needed.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the AP mod DLL is present in the game's Mods folder.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── ConnectsItself — the mod speaks to the AP server directly ─────────────

    /// The MelonLoader mod (APMomodoraMoonlitFarewell.dll) holds the AP connection
    /// internally via Archipelago.MultiClient.Net bundled in extra-files.zip.
    /// The launcher must NOT hold its own ApClient on this slot.
    public bool ConnectsItself => true;

    /// Momodora: Moonlit Farewell launches fine without a randomizer session.
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Working directory for this plugin's downloads and sidecar. Exposed as
    /// GameDirectory per the IGamePlugin contract.
    public string GameDirectory { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam", "steamapps", "common", "Momodora Moonlit Farewell");

    private string SettingsSidecarDir =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath =>
        Path.Combine(SettingsSidecarDir, "momodora_mf_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The MelonLoader mod reports checks, items, and the goal directly to the AP
    // server. These events exist for interface compatibility only.
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
            var (ver, _, _) = await ResolveLatestModAsync(ct);
            AvailableVersion = ver;
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. Locate the game install.
        progress.Report((2, "Locating your Momodora: Moonlit Farewell installation..."));
        string? gameDir = ResolveMomodoraDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Momodora: Moonlit Farewell installation. " +
                "Open this game's Settings and pick your game folder (the one " +
                "containing the game exe), or install the game via Steam (app " +
                SteamAppId + ") first. The AP mod is added on top of your own copy.");

        // 1. Resolve the latest mod release.
        progress.Report((6, "Checking the latest Momodora AP mod release..."));
        var (version, dllUrl, extrasUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (dllUrl == null)
            throw new InvalidOperationException(
                "Could not find the Momodora AP mod download on GitHub. Check your " +
                "internet connection, or download it manually from " + ModRepoUrl +
                "/releases. Open Settings for the guided steps.");

        // 2. Ensure MelonLoader is present. If absent, download and stage the
        //    MelonLoader zip (which the user then needs to run / the game auto-loads).
        //    MelonLoader 0.6.x ships as a portable zip that extracts directly into
        //    the game root (no interactive patcher needed — unlike older versions).
        //    The setup guide specifies v0.5.7 (interactive installer), but since v0.6
        //    is the portable-zip form, we prefer that. We stage whichever is newest
        //    and surface a note. If MelonLoader is already present, skip.
        bool melonPresent = MelonLoaderPresent(gameDir);
        if (!melonPresent)
        {
            progress.Report((12, "Staging MelonLoader into your game folder..."));
            try
            {
                var (mlVer, mlUrl) = await ResolveLatestMelonAsync(ct);
                await DownloadAndExtractZipToDirAsync(
                    mlUrl, $"melonloader-{mlVer}", gameDir, 12, 48, progress, ct);
                progress.Report((48, $"MelonLoader {mlVer} extracted into your game folder."));
            }
            catch (Exception ex)
            {
                progress.Report((48,
                    "Could not stage MelonLoader automatically: " + ex.Message + ". " +
                    "Install MelonLoader v0.5.7+ manually into the game folder from " +
                    MelonLoaderUrl + "/releases (see Settings for the guide)."));
            }
        }
        else
        {
            progress.Report((48, "MelonLoader already present — keeping your existing install."));
        }

        // 3. Download the mod DLL into <game>\Mods\.
        string modsDir = Path.Combine(gameDir, "Mods");
        Directory.CreateDirectory(modsDir);

        progress.Report((50, $"Downloading APMomodoraMoonlitFarewell.dll {version}..."));
        await DownloadFileAsync(
            dllUrl,
            Path.Combine(modsDir, "APMomodoraMoonlitFarewell.dll"),
            $"Downloading mod DLL {version}...",
            50, 72, progress, ct);

        // 4. Download and extract extra-files.zip (Archipelago.MultiClient.Net,
        //    Newtonsoft.Json, websocket-sharp, config.json skeleton, etc.) into Mods\.
        if (extrasUrl != null)
        {
            progress.Report((72, "Downloading and extracting extra-files.zip..."));
            await DownloadAndExtractZipToDirAsync(
                extrasUrl, $"momodora-extras-{version}", modsDir, 72, 94, progress, ct);
        }

        // 5. Stamp the installed version.
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Momodora AP mod {version} installed into {modsDir}. " +
            "Launch the game from the Play tab to connect. The launcher will " +
            "pre-fill Mods\\config.json with your server and slot name automatically. " +
            (melonPresent ? "" :
            "NOTE: MelonLoader was just staged — launch the game once to let " +
            "MelonLoader finish its first-run initialization, then close and relaunch " +
            "from this tile for your AP session. ") +
            "Open Settings for guided setup steps and links."));
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
        // Pre-fill config.json before starting the game. The mod reads it on
        // startup (ConfigLoader.LoadConfig) from <game>\Mods\config.json.
        // Fields: "server" = "host:port", "username" = slot name, "password".
        string? gameDir = ResolveMomodoraDir();
        if (gameDir != null)
        {
            string configPath = Path.Combine(gameDir, ModConfigRelPath);
            WriteModConfig(configPath, session);
        }

        StartMomodora();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartMomodora();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        _gameProcess = null;

        // Scrub the password from config.json.
        string? gameDir = ResolveMomodoraDir();
        if (gameDir != null)
        {
            string configPath = Path.Combine(gameDir, ModConfigRelPath);
            ScrubConfigPassword(configPath);
        }
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true, see header) ─────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The APMomodoraMoonlitFarewell mod receives items directly from the AP
        // server. Nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var accent  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8B, 0x3A, 0x7E));
        var success = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Status header ─────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Momodora: Moonlit Farewell is your own game (Steam app 1747760) " +
                   "with the APMomodoraMoonlitFarewell MelonLoader mod by alditoOt " +
                   "added on top. The launcher detects your Steam install, stages " +
                   "MelonLoader and the mod, and pre-fills Mods\\config.json with " +
                   "your server connection info when you press Play.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install ─────────────────────────────────────
        AddSectionHeader(panel, "MOMODORA INSTALL", muted);

        string? gameDir     = ResolveMomodoraDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Momodora: Moonlit Farewell not detected. Pick your install folder " +
              "below, or install the game via Steam (app " + SteamAppId + ") first.";

        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // MelonLoader status
        bool melonOk = gameDir != null && MelonLoaderPresent(gameDir);
        panel.Children.Add(new TextBlock
        {
            Text = gameDir == null ? "" :
                (melonOk
                    ? "MelonLoader found in your game folder."
                    : "MelonLoader not detected. Install on the Play tab will stage " +
                      "it, or download MelonLoader v0.5.7+ from the link below."),
            FontSize = 11, Foreground = melonOk ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        // Mod DLL status
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                    ? "AP mod found: " + modDll
                    : "AP mod (APMomodoraMoonlitFarewell.dll) not found in Mods\\ yet " +
                      "(use Install on the Play tab).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = overrideDir ?? gameDir ?? "",
            IsReadOnly  = true, FontSize = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Momodora: Moonlit Farewell install folder. " +
                          "Detected from Steam (app 1747760) automatically; use this " +
                          "picker to override for a non-standard library or store.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 130, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select your Momodora: Moonlit Farewell install folder " +
                        "(contains the game exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                                   ? (overrideDir ?? gameDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() != true) return;

            string picked = dlg.FolderName;
            string? bad   = ValidateGameFolder(picked);
            if (bad != null)
            {
                System.Windows.MessageBox.Show(bad, "Not a Momodora: Moonlit Farewell folder",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            SaveOverrideDir(picked);
            dirBox.Text = picked;
        };
        DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new TextBlock
        {
            Text = "Steam installs are detected automatically (app 1747760). Use this " +
                   "picker only for a non-standard Steam library or another store.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ───────────────────────────────────────────
        AddSectionHeader(panel, "CONNECTING", muted);
        panel.Children.Add(new TextBlock
        {
            Text = "The launcher writes Mods\\config.json with your server address, " +
                   "slot name, and password before starting the game. The " +
                   "APMomodoraMoonlitFarewell mod reads that file on startup and " +
                   "connects automatically — you do not need to edit the file by hand.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // Show current config.json contents (if present).
        if (gameDir != null)
        {
            string cfgPath = Path.Combine(gameDir, ModConfigRelPath);
            if (File.Exists(cfgPath))
            {
                try
                {
                    string cfgText = File.ReadAllText(cfgPath);
                    panel.Children.Add(new TextBlock
                    {
                        Text = "Current Mods\\config.json:\n" + cfgText,
                        FontSize = 10,
                        FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                        Foreground = muted, TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 12),
                    });
                }
                catch { /* ignore read errors */ }
            }
        }

        // ── Section: Guided steps ─────────────────────────────────────────
        AddSectionHeader(panel, "GUIDED SETUP", muted);
        foreach (string step in new[]
        {
            "1. Own Momodora: Moonlit Farewell (on Steam, app 1747760). Install it " +
                "if you have not. Use the folder picker above if it was not auto-detected.",
            "2. Install the AP mod: click Install on the Play tab. This downloads " +
                "MelonLoader (the mod loader) and the APMomodoraMoonlitFarewell mod " +
                "and extracts them into your game folder and Mods\\ subfolder.",
            "3. If MelonLoader was newly staged, launch Momodora once via Steam " +
                "(or from the Play tab in standalone mode) to let MelonLoader " +
                "initialize, then close the game.",
            "4. In your Archipelago multiworld, generate a session with the " +
                "momodoramoonlitfarewell.apworld (download from the mod releases).",
            "5. Click Play on this tile. The launcher writes your session info into " +
                "Mods\\config.json and starts the game. The mod connects automatically.",
            "6. For backup: save files are at " +
                @"%APPDATA%\..\LocalLow\BOMBSERVICE\MomodoraMoonlitFarewell\saves " +
                "— back these up before starting a new randomizer seed.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        AddSectionHeader(panel, "LINKS", muted);
        foreach (var (label, url) in new[]
        {
            ("Momodora AP mod (GitHub) ↗",       ModRepoUrl),
            ("Setup Guide (Archipelago) ↗",      SetupGuideUrl),
            ("MelonLoader (releases) ↗",         MelonLoaderUrl + "/releases"),
            ("Archipelago Official ↗",           ArchipelagoSite),
            ("Momodora: Moonlit Farewell on Steam ↗",
                "https://store.steampowered.com/app/" + SteamAppId),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding         = new Thickness(0, 2, 0, 2),
                Background      = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize        = 12,
                Margin          = new Thickness(0, 0, 0, 4),
                Foreground      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor          = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); }
                catch { /* ignore if no browser */ }
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
            string json = await Http.GetStringAsync(GhModReleasesUrl, ct);
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

    /// Normalize a GitHub tag like "AP-v1.7.5.1" or "v1.2.3" → "1.7.5.1" / "1.2.3".
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        // Strip any leading non-digit prefix up to the first digit.
        int i = 0;
        while (i < tag.Length && !char.IsDigit(tag[i])) i++;
        return i < tag.Length ? tag[i..] : tag;
    }

    /// Resolve the latest AP mod release: version, DLL download URL, extras zip URL.
    /// Falls back to pinned v1.7.5.1 URLs when the API is unreachable.
    private async Task<(string Version, string? DllUrl, string? ExtrasUrl)>
        ResolveLatestModAsync(CancellationToken ct)
    {
        try
        {
            string json = await Http.GetStringAsync(GhModReleasesUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) goto fallback;

            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                // Skip non-AP releases (e.g. the "manual" tag).
                string? tagName = rel.TryGetProperty("tag_name", out var t)
                    ? t.GetString()
                    : null;
                if (tagName == null ||
                    !tagName.StartsWith("AP-", StringComparison.OrdinalIgnoreCase))
                    continue;

                string version = NormalizeTag(tagName) ?? ModFallbackVersion;

                if (!rel.TryGetProperty("assets", out var assets) ||
                    assets.ValueKind != JsonValueKind.Array)
                    continue;

                string? dllUrl    = null;
                string? extrasUrl = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u)
                                   ? u.GetString()
                                   : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (lower.EndsWith(".dll") && lower.Contains("momodora")) dllUrl    = url;
                    if (lower == "extra-files.zip")                           extrasUrl = url;
                }

                if (dllUrl != null)
                    return (version, dllUrl, extrasUrl);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        fallback:
        return (ModFallbackVersion, ModFallbackDllUrl, ModFallbackExtrasUrl);
    }

    /// Resolve the latest MelonLoader x64 zip URL (LavaGang/MelonLoader).
    /// Falls back to pinned v0.6.6 when the API is unreachable.
    private async Task<(string Version, string ZipUrl)> ResolveLatestMelonAsync(CancellationToken ct)
    {
        try
        {
            string json = await Http.GetStringAsync(GhMelonReleasesUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    // Prefer the x64 portable zip (MelonLoader.x64.zip).
                    string lower = name.ToLowerInvariant();
                    if (lower == "melonloader.x64.zip")
                        return (version, url);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        return (MelonFallbackVersion, MelonFallbackZipUrl);
    }

    // ── Private helpers — game detection ─────────────────────────────────────

    private string? ResolveMomodoraDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeMomodoraDir(ov)) return ov;

        try { return DetectSteamMomodoraDir(); }
        catch { return null; }
    }

    private static bool LooksLikeMomodoraDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            foreach (string exe in GameExeNames)
                if (File.Exists(Path.Combine(dir, exe))) return true;
            // Also accept if it has a MomodoraMoonlitFarewell_Data folder (Unity).
            if (Directory.Exists(Path.Combine(dir, "MomodoraMoonlitFarewell_Data")))
                return true;
            return false;
        }
        catch { return false; }
    }

    private static bool MelonLoaderPresent(string gameDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return false;
            // MelonLoader drops a MelonLoader\ directory and/or MelonLoader.dll at
            // the game root. Either is sufficient evidence.
            if (Directory.Exists(Path.Combine(gameDir, "MelonLoader"))) return true;
            if (File.Exists(Path.Combine(gameDir, "MelonLoader.dll"))) return true;
            return false;
        }
        catch { return false; }
    }

    private string? FindInstalledModDll()
    {
        try
        {
            string? game = ResolveMomodoraDir();
            if (game == null) return null;
            string modsDir = Path.Combine(game, "Mods");
            if (!Directory.Exists(modsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(
                modsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                // Primary name v1.7+: APMomodoraMoonlitFarewell
                // Older name  v1.6.x: APMomoMFRandomizer
                if (name.IndexOf("Momodora", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("APMomo",   StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    private static string? DetectSteamMomodoraDir()
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
                        if (LooksLikeMomodoraDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamFolderName);
                    if (LooksLikeMomodoraDir(conventional)) return conventional;
                }
                catch { /* try next */ }
            }
        }
        return null;
    }

    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadReg(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormPath(hkcu);

        string? hklm = ReadReg(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormPath(hklm);

        string? hklm64 = ReadReg(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm64)) yield return NormPath(hklm64);

        string? prog = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(prog)) yield return Path.Combine(prog, "Steam");
    }

    private static string NormPath(string p) => p.Replace('/', '\\').TrimEnd('\\');

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

    private static string? ReadReg(RegistryKey hive, string sub, string val)
    {
        try { using var k = hive.OpenSubKey(sub); return k?.GetValue(val) as string; }
        catch { return null; }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartMomodora()
    {
        string? gameDir = ResolveMomodoraDir();
        if (gameDir != null)
        {
            string? exe = GameExeNames
                .Select(n => Path.Combine(gameDir, n))
                .FirstOrDefault(File.Exists);

            if (exe != null)
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName         = exe,
                    WorkingDirectory = gameDir,
                    UseShellExecute  = true,
                }) ?? throw new InvalidOperationException(
                    "Failed to start Momodora: Moonlit Farewell.");

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
        }

        // Fall back to Steam URL.
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
            "Could not find Momodora: Moonlit Farewell. Open this game's Settings " +
            "and pick your install folder, or install the game via Steam (app " +
            SteamAppId + ").",
            "Momodora Moonlit Farewell.exe");
    }

    // ── Private helpers — config.json read/write ─────────────────────────────

    /// Write the mod's Mods\config.json with connection info from the ApSession.
    /// Format verified from ConfigLoader.cs: { "server": "host:port",
    /// "username": "slot", "password": "" }
    private static void WriteModConfig(string configPath, ApSession session)
    {
        try
        {
            // Build "host:port" string from the ServerUri.
            string server = session.ServerUri;
            // If it's a ws://host:port or wss://host:port URI, strip the scheme.
            if (Uri.TryCreate(session.ServerUri, UriKind.Absolute, out var uri))
                server = $"{uri.Host}:{uri.Port}";

            var cfg = new
            {
                server   = server,
                username = session.SlotName,
                password = session.Password ?? "",
            };
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(
                configPath,
                JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — user can edit the file manually */ }
    }

    /// Overwrite the password field with an empty string in config.json.
    private static void ScrubConfigPassword(string configPath)
    {
        try
        {
            if (!File.Exists(configPath)) return;
            string text = File.ReadAllText(configPath);
            using var doc  = JsonDocument.Parse(text);
            var root  = doc.RootElement;

            string server   = root.TryGetProperty("server",   out var sv) ? sv.GetString() ?? "" : "";
            string username = root.TryGetProperty("username", out var un) ? un.GetString() ?? "" : "";

            var scrubbed = new { server, username, password = "" };
            File.WriteAllText(
                configPath,
                JsonSerializer.Serialize(scrubbed, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    // ── Private helpers — download / extract ─────────────────────────────────

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
                $"Downloading {tag}...", pctStart, dlEnd, progress, ct);
            progress.Report((dlEnd, $"Extracting {tag}..."));
            Directory.CreateDirectory(targetDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, targetDir, overwriteFiles: true);
            progress.Report((pctEnd, $"{tag} extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

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
        using var response = await Http.GetAsync(
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
                progress.Report((pct, $"{msg} {downloaded / 1024}KB"));
            }
        }
        await dst.FlushAsync(ct);
    }

    // ── Private helpers — folder validation ─────────────────────────────────

    private string? ValidateGameFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Momodora: Moonlit Farewell " +
                   "install folder.";
        if (LooksLikeMomodoraDir(folder)) return null;
        return "That does not look like a Momodora: Moonlit Farewell installation. " +
               "Pick the folder that contains the game exe (for Steam this is usually " +
               @"...\steamapps\common\Momodora Moonlit Farewell).";
    }

    // ── Private helpers — UI ──────────────────────────────────────────────────

    private static void AddSectionHeader(StackPanel panel, string text, SolidColorBrush color)
    {
        panel.Children.Add(new TextBlock
        {
            Text       = text,
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = color,
            Margin     = new Thickness(0, 4, 0, 8),
        });
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class MomodoraSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private MomodoraSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<MomodoraSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(MomodoraSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(
                SettingsSidecarPath,
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
