using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Lunacid;

// ════════════════════════════════════════════════════════════════════════════════
// LunacidPlugin — install / launch for "Lunacid" (Kira, 2023) played through
// the LunacidAP mod by Witchybun, a BepInEx plugin that bundles an in-game
// Archipelago client. The game connects to the AP server itself — no emulator
// and no bridge process. This is a NATIVE "ConnectsItself" integration.
//
// ── HONEST REALITY CHECK (2026-06-14, verified against mod source + GitHub) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Lunacid (Steam appid 1745510, by Kira), and Archipelago support is delivered
// as a BepInEx 5.x (Unity Mono) mod added on top. Verified facts:
//
//   * THE AP WORLD game string is "Lunacid" (verified against
//     Witchybun/LunacidAPClient ArchipelagoClient.cs:
//     `public const string GAME_NAME = "Lunacid";`
//     and the login call: `Session.LoginAsync(GAME_NAME, slotName, ...)`).
//     GameId = "lunacid".
//
//   * THE MOD repo is Witchybun/LunacidAPClient (GitHub, verified 2026-06-14).
//     Latest release: v1.1.2 "Lunacid Randomizer v1.1.2". Each release ships
//     two assets:
//         lunacid.apworld   — the AP world definition
//         Lunacid112.zip    — the BepInEx mod archive (name varies by version)
//     The mod DLL is "LunacidAP.dll" (AssemblyName = LunacidAP in LunacidAP.csproj).
//     BepInPlugin GUID is inferred from PluginInfo.PLUGIN_GUID; the project name
//     is "LunacidAP" (netstandard2.0, BepInEx 5.x).
//
//   * BepInEx 5.x (Unity Mono) is a SEPARATE prerequisite — a portable zip
//     extracted into the Lunacid game install root. The mod targets netstandard2.0
//     with BepInEx.Core 5.x. The game uses Unity 2020.3.4 on Windows.
//
//   * CONNECTION happens during NEW GAME character creation. From NewGameUI.cs:
//     the mod adds Host, Port, and Password text fields to the CHAR_CREATE scene.
//     After successful login the data is written to a per-save JSON file at
//         <game_root>/ArchSaves/Save{N}.json
//     which stores HostName, Port, SlotName, Password and the full session state.
//     Connection runs in-game; there is no standalone config file the launcher
//     can pre-write to prefill host/port/slot BEFORE the game starts. The
//     in-game fields accept freetext, so this launcher cannot pre-populate them.
//     NOTE: the README's troubleshooting confirms that to change host/port you
//     must "open the .json file for your related save, and change the port in the
//     save directly" — meaning the save JSON is writable after first creation.
//     For NEW games, however, the slot number is not known before play; there is
//     no single config file to pre-fill.
//
//   * The apworld ("lunacid.apworld") ships in the SAME release zip as the mod
//     (Witchybun/LunacidAPClient releases). Users place it in their Archipelago
//     custom_worlds folder.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ────────────────────────────────────
//   1. DETECT the Steam Lunacid install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Lunacid via appmanifest_1745510.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and takes
//      precedence; it is validated (must contain LUNACID.exe or LUNACID_Data
//      folder) and persisted in a plugin sidecar
//      (Games/ROMs/lunacid/lunacid_launcher.json).
//   2. INSTALL/UPDATE = (a) if BepInEx not present, download BepInEx 5.4.22
//      x64 (Unity Mono) and extract into game root; (b) download the latest
//      mod zip from Witchybun/LunacidAPClient releases and extract into game
//      root; (c) download lunacid.apworld alongside the mod so the user can
//      place it in Archipelago custom_worlds. Version stamped in the sidecar.
//   3. LAUNCH = run LUNACID.exe from the detected/override install; if the exe
//      cannot be found but Steam is present, fall back to
//      steam://rungameid/1745510. ConnectsItself = true (the mod owns the slot).
//      SupportsStandalone = true. No config pre-fill is possible for new games;
//      the settings panel explains this and shows the in-game instructions.
//
// ── DEFENSIVE / UNVERIFIED ──────────────────────────────────────────────────
//   * "Installed" = LunacidAP.dll present under the BepInEx tree of the
//     detected/override install (case-insensitive, recursive). Honors hand-
//     installed and mod-manager installs.
//   * The mod zip layout was not run against a live install; the plugin extracts
//     it into the game root (standard BepInEx mod zip convention) and then
//     checks for LunacidAP.dll under BepInEx/. If extraction succeeds but the
//     DLL is in an unexpected subfolder, verify marks false and guides the user.
//   * Steam library / VDF / ACF parsing is defensive (hand-written tolerant
//     scans). Any failure degrades to "Lunacid not found" rather than throwing.
// ════════════════════════════════════════════════════════════════════════════════

#pragma warning disable CS0067

public sealed class LunacidPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string MOD_OWNER  = "Witchybun";
    private const string MOD_REPO   = "LunacidAPClient";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // Pinned fallbacks when GitHub API is unreachable.
    private const string FallbackModVersion   = "1.1.2";
    private const string FallbackModZipName   = "Lunacid112.zip";
    private const string FallbackApworldName  = "lunacid.apworld";
    private static readonly string FallbackModZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackModVersion}/{FallbackModZipName}";
    private static readonly string FallbackApworldUrl =
        $"{ModRepoUrl}/releases/download/{FallbackModVersion}/{FallbackApworldName}";

    // BepInEx 5.4.22 Unity Mono x64 — portable zip, no wizard.
    private const string BepInExVersion = "5.4.22";
    private static readonly string BepInExZipUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.22/" +
        "BepInEx_x64_5.4.22.0.zip";
    private const string BepInExSite = "https://github.com/BepInEx/BepInEx/releases";

    // Steam — Lunacid appid 1745510 (verified via SteamDB search 2026-06-14).
    private const string SteamAppId  = "1745510";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    // Steam library directory name for Lunacid (from ACF installdir field;
    // conventionally matches the common folder name).
    private const string SteamCommonFolderName = "Lunacid";

    // Game exe / data dir names (standard Unity export for this title,
    // confirmed from csproj HintPath: .../steamapps/common/Lunacid/LUNACID_Data/...).
    private const string GameExeName = "LUNACID.exe";
    private const string GameDataDir = "LUNACID_Data";

    // The mod's BepInEx plugin DLL (AssemblyName = LunacidAP from LunacidAP.csproj).
    private const string ModPrimaryDll = "LunacidAP.dll";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ───────────────────────────────────────────────

    public string GameId      => "lunacid";
    public string DisplayName => "Lunacid";
    public string Subtitle    => "Native PC · BepInEx mod";

    // EXACT AP game string — verified against Witchybun/LunacidAPClient
    // ArchipelagoClient.cs: `public const string GAME_NAME = "Lunacid";`
    public string ApWorldName => "Lunacid";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "lunacid.png");

    public string ThemeAccentColor => "#7C3AED";   // deep violet, matches Lunacid's gothic palette
    public string[] GameBadges     => new[] { "Steam · BepInEx mod" };

    public string Description =>
        "Lunacid, Kira's 2023 first-person dungeon crawling RPG inspired by the " +
        "classic King's Field series, played through the LunacidAP mod by Witchybun " +
        "— a BepInEx plugin that embeds an in-game Archipelago client directly inside " +
        "the game. Weapons, spells, items, and abilities are shuffled across the " +
        "multiworld. You bring your own copy of Lunacid (owned on Steam); the mod runs " +
        "on BepInEx 5.x (Unity Mono). Connection details are entered during new-game " +
        "character creation. The launcher detects your Steam install, stages BepInEx " +
        "and the mod automatically, and guides you through the in-game connection steps.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    // "Installed" = LunacidAP.dll present under the BepInEx tree of the
    // detected or override install. Honors hand-installed and mod-manager installs.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; }
        = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam", "steamapps", "common", "Lunacid");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "lunacid_launcher.json");

    // ── Internal state ───────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ─────────────────────────────────────────────────────
    // The LunacidAP mod reports checks/items/goal to the AP server itself.
    // These exist for interface compatibility (ConnectsItself = true).

    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;

#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Lifecycle — CheckForUpdate ───────────────────────────────────────────

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

    // ── Lifecycle — InstallOrUpdate ──────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. Locate the game install.
        progress.Report((2, "Locating your Lunacid installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Lunacid installation. Open this game's Settings and pick " +
                "your install folder (the one containing LUNACID.exe), or install Lunacid " +
                "via Steam first. The Archipelago mod is added on top of your own copy.");

        // 1. Resolve the latest release from GitHub (pinned fallback when offline).
        progress.Report((5, "Checking the latest LunacidAP release on GitHub..."));
        var (modVersion, modZipUrl, apworldUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = modVersion;

        if (modZipUrl == null)
            throw new InvalidOperationException(
                "Could not find the LunacidAP mod download on GitHub. Check your internet " +
                "connection, or download the mod zip manually from " +
                ModRepoUrl + "/releases and extract it into your Lunacid folder.");

        // 2. Stage BepInEx 5.4.22 (Unity Mono x64) if not already present.
        if (!BepInExPresent(gameDir))
        {
            progress.Report((10, "Staging BepInEx 5.4.22 (Unity Mono x64) into your Lunacid folder..."));
            await DownloadAndExtractZipAsync(
                BepInExZipUrl, gameDir, "bepinex-5.4.22", 10, 45, progress, ct);
        }
        else
        {
            progress.Report((45, "BepInEx already present — keeping existing install."));
        }

        // 3. Download + extract the mod zip into the game root. The zip follows
        //    standard BepInEx mod zip convention: BepInEx/plugins/LunacidAP.dll
        //    (and its dependencies) land where BepInEx expects them.
        progress.Report((47, $"Downloading LunacidAP {modVersion}..."));
        await DownloadAndExtractZipAsync(
            modZipUrl, gameDir, $"lunacid-ap-{modVersion}", 47, 85, progress, ct);

        // 4. Download the apworld next to the game dir so the user can copy it
        //    to Archipelago's custom_worlds folder.
        if (apworldUrl != null)
        {
            progress.Report((87, "Downloading lunacid.apworld..."));
            string apworldDest = Path.Combine(gameDir, FallbackApworldName);
            try
            {
                byte[] apworldBytes = await _http.GetByteArrayAsync(apworldUrl, ct);
                await File.WriteAllBytesAsync(apworldDest, apworldBytes, ct);
                progress.Report((92, $"lunacid.apworld saved to: {apworldDest}"));
            }
            catch (Exception ex)
            {
                progress.Report((92, $"Warning: could not download lunacid.apworld: {ex.Message}"));
            }
        }

        // 5. Stamp version in the sidecar.
        WriteStampedVersion(modVersion);
        InstalledVersion = modVersion;

        bool bepOk = BepInExPresent(gameDir);
        bool modOk = FindInstalledModDll() != null;

        progress.Report((100,
            $"Staged LunacidAP {modVersion} into your Lunacid folder" +
            (bepOk ? " (BepInEx OK" : " (BepInEx NOT detected") +
            (modOk ? ", mod DLL present). " : ", mod DLL NOT detected — check the zip layout). ") +
            "Launch Lunacid ONCE so BepInEx generates its config files. " +
            "Then generate your AP game, start a NEW save, and enter your server details " +
            "in the Host/Port/Password fields during character creation. " +
            "IMPORTANT: place lunacid.apworld (downloaded next to your game exe) into " +
            "your Archipelago custom_worlds folder before generating a game."));
    }

    // ── Lifecycle — Verify ───────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── Lifecycle — Launch ───────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // Connection details are entered in-game during character creation.
        // There is no standalone config file to pre-fill for new saves.
        // For existing saves the user can manually edit ArchSaves/Save{N}.json
        // to change host/port — the settings panel explains this.
        StartGame();
        return Task.CompletedTask;
    }

    public bool SupportsStandalone => true;

    // The LunacidAP mod owns the slot connection — the launcher must NOT hold
    // its own ApClient on this slot.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert ────────────────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ──────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new SolidColorBrush(Color.FromRgb(0x9D, 0x6B, 0xFF));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Info header ──────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Lunacid uses BepInEx 5.x (Unity Mono) and the LunacidAP mod " +
                   "(Witchybun/LunacidAPClient). The launcher can auto-install BepInEx " +
                   "and the mod. Connection details are entered during new-game character " +
                   "creation — type Host, Port, and Password into the in-game fields on the " +
                   "character creation screen. For existing saves you can edit the " +
                   "ArchSaves/Save{N}.json file in your game folder to change server details. " +
                   "You also need lunacid.apworld placed in your Archipelago custom_worlds folder.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install ────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LUNACID INSTALL", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Lunacid not detected. Pick your install folder below, or install the " +
              "game via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        string? modDll = FindInstalledModDll();
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                ? "LunacidAP mod present: " + modDll
                : "LunacidAP mod not detected (use Install on the Play tab to stage it).",
            FontSize = 11,
            Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        bool bepOk = gameDir != null && BepInExPresent(gameDir);
        panel.Children.Add(new TextBlock
        {
            Text = bepOk
                ? "BepInEx present in install folder."
                : "BepInEx not detected (the launcher stages it automatically during Install).",
            FontSize = 11,
            Foreground = bepOk ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Lunacid install folder (containing LUNACID.exe). " +
                          "Detected from Steam automatically; use the picker to override.",
        };
        var dirBtn = new Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        string? capturedOverride = overrideDir;
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select your Lunacid install folder (contains LUNACID.exe)",
                InitialDirectory = Directory.Exists(capturedOverride ?? gameDir ?? "")
                    ? (capturedOverride ?? gameDir!)
                    : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                if (!LooksLikeGameDir(picked))
                {
                    // Try one level deeper (user may have picked the Steam library root).
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeGameDir(nested))
                    {
                        picked = nested;
                    }
                    else
                    {
                        System.Windows.MessageBox.Show(
                            "That does not look like a Lunacid installation. Pick the " +
                            "folder that contains LUNACID.exe or the LUNACID_Data folder " +
                            @"(for Steam this is usually ...\steamapps\common\Lunacid).",
                            "Not a Lunacid folder",
                            MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
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
            Text = "Steam installs are detected automatically (appid 1745510). " +
                   "Use the picker for a non-standard library location.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: connecting ──────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING TO ARCHIPELAGO", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Connection details are entered in-game during character creation. " +
                   "When you start a NEW SAVE, the LunacidAP mod adds Host, Port, and " +
                   "Password text fields to the character creation screen. Fill these in " +
                   "with your Archipelago server address (e.g. archipelago.gg), port " +
                   "(e.g. 38281), and slot name, then proceed to create your character. " +
                   "A successful login is required before the character creation continues.\n\n" +
                   "For EXISTING SAVES: to change server details (e.g. if your port changed) " +
                   "open the file ArchSaves/Save{N}.json in your Lunacid game folder with a " +
                   "text editor and update the HostName and Port fields directly.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: apworld ─────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "APWORLD FILE", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "The lunacid.apworld file is bundled with each mod release on " +
                   "Witchybun/LunacidAPClient. The Install step downloads it next to " +
                   "your LUNACID.exe. You must place it in your Archipelago custom_worlds " +
                   @"folder (%LocalAppData%\Archipelago\lib\worlds\ or custom_worlds\ " +
                   "next to ArchipelagoLauncher.exe) before generating a game.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        string? cwDir = FindArchipelagoCustomWorldsDir();
        panel.Children.Add(new TextBlock
        {
            Text = cwDir != null
                ? "Archipelago custom_worlds folder: " + cwDir
                : "Archipelago installation not detected. Install Archipelago first, then " +
                  "drop lunacid.apworld into its custom_worlds folder.",
            FontSize = 11,
            Foreground = cwDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: setup steps ─────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "SETUP STEPS", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own and install Lunacid on Steam. Use the folder picker above if " +
                "it was not auto-detected.",
            "2. Click Install on the Play tab — the launcher stages BepInEx 5.4.22 x64 " +
                "(Unity Mono) and the LunacidAP mod, and downloads lunacid.apworld next " +
                "to your game exe.",
            "3. Place lunacid.apworld into your Archipelago custom_worlds folder " +
                "(detected above). Generate your game from the Archipelago Launcher " +
                "or web client.",
            "4. Launch Lunacid ONCE so BepInEx generates its config files. You will see " +
                "a BepInEx console window appear briefly — this is normal.",
            "5. Start a NEW SAVE. On the character creation screen, fill in the Host, " +
                "Port, and Password fields the mod added. Enter your server address, " +
                "port number, and slot name (slot name goes in the player name field). " +
                "A successful AP login is required to proceed to character creation.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ────────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("LunacidAP mod (Witchybun/LunacidAPClient) ↗", ModRepoUrl + "/releases"),
            ("BepInEx 5.x releases ↗",                      BepInExSite),
            ("Lunacid on Steam ↗",
                $"https://store.steampowered.com/app/{SteamAppId}/Lunacid/"),
            ("Archipelago Official ↗", "https://archipelago.gg"),
        })
        {
            var btn = new Button
            {
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = accent,
                Cursor = Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => OpenUrl(u);
            panel.Children.Add(btn);
        }

        return panel;
    }

    // ── News feed ────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(
                $"{GH_MOD_RELEASES_URL}?per_page=5", ct);
            using var doc = JsonDocument.Parse(json);
            var results = new List<NewsItem>();
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                string tag  = rel.TryGetProperty("tag_name",     out var t) ? t.GetString() ?? "" : "";
                string body = rel.TryGetProperty("body",         out var b) ? b.GetString() ?? "" : "";
                string dt   = rel.TryGetProperty("published_at", out var d) ? d.GetString() ?? "" : "";
                string url  = rel.TryGetProperty("html_url",     out var u) ? u.GetString() ?? "" : "";
                DateTimeOffset date = DateTimeOffset.TryParse(dt, out var parsed)
                    ? parsed : DateTimeOffset.MinValue;
                results.Add(new NewsItem(
                    Title:   $"LunacidAP {tag}",
                    Body:    string.IsNullOrWhiteSpace(body)
                                 ? "See the release page for details."
                                 : body.Length > 400 ? body[..400] + "..." : body,
                    Version: tag,
                    Date:    date,
                    Url:     url));
            }
            if (results.Count > 0) return results.ToArray();
        }
        catch { /* fall through to static item */ }

        return new[]
        {
            new NewsItem(
                Title:   "LunacidAP — Archipelago mod for Lunacid",
                Body:    "A BepInEx mod for Lunacid (Steam, appid 1745510) that adds an " +
                         "in-game Archipelago client. Connection is entered during character " +
                         "creation on new saves. AP game string: \"Lunacid\".",
                Version: FallbackModVersion,
                Date:    DateTimeOffset.MinValue,
                Url:     ModRepoUrl + "/releases"),
        };
    }

    // ── Private helpers — Steam / game detection ─────────────────────────────

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
            if (File.Exists(Path.Combine(dir, GameExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, GameDataDir))) return true;
            return Directory.EnumerateFiles(dir, "LUNACID*.exe",
                                            SearchOption.TopDirectoryOnly).Any();
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

                    string common    = Path.Combine(steamapps, "common");
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
        var roots = new List<string>();

        // HKCU path (most common on single-user machines)
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            string? path = key?.GetValue("SteamPath") as string;
            if (!string.IsNullOrWhiteSpace(path)) roots.Add(path!);
        }
        catch { /* ignore */ }

        // HKLM path (32-bit registry on 64-bit Windows)
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\WOW6432Node\Valve\Steam");
            string? path = key?.GetValue("InstallPath") as string;
            if (!string.IsNullOrWhiteSpace(path)) roots.Add(path!);
        }
        catch { /* ignore */ }

        foreach (var r in roots) yield return r;

        // Common default paths
        foreach (string pf in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        })
        {
            if (string.IsNullOrWhiteSpace(pf)) continue;
            string guess = Path.Combine(pf, "Steam");
            if (Directory.Exists(guess)) yield return guess;
        }
    }

    private static IEnumerable<string> SteamLibraryRoots(string steamRoot)
    {
        yield return steamRoot;

        var extras = new List<string>();
        // Parse steamapps\libraryfolders.vdf for additional library paths.
        // We use a tolerant hand-written scan — no real VDF parser dependency.
        try
        {
            string vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdfPath))
            {
                foreach (string line in File.ReadLines(vdfPath))
                {
                    string trimmed = line.Trim();
                    // Lines look like: "path"  "D:\\SteamLibrary"
                    if (!trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
                        continue;
                    int first = trimmed.IndexOf('"', 6);
                    if (first < 0) continue;
                    int second = trimmed.IndexOf('"', first + 1);
                    if (second < 0) continue;
                    string libPath = trimmed[(first + 1)..second].Replace(@"\\", @"\");
                    if (!string.IsNullOrWhiteSpace(libPath) && Directory.Exists(libPath))
                        extras.Add(libPath);
                }
            }
        }
        catch { /* ignore corrupt VDF */ }

        foreach (var extra in extras) yield return extra;
    }

    private static string? ReadAcfInstallDir(string acfPath)
    {
        // Scan ACF manifest for the "installdir" field.
        try
        {
            foreach (string line in File.ReadLines(acfPath))
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("\"installdir\"", StringComparison.OrdinalIgnoreCase))
                    continue;
                int first = trimmed.IndexOf('"', 12);
                if (first < 0) continue;
                int second = trimmed.IndexOf('"', first + 1);
                if (second < 0) continue;
                return trimmed[(first + 1)..second];
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private string? FindInstalledModDll()
    {
        string? dir = ResolveGameDir();
        if (dir == null) return null;
        try
        {
            string bepDir = Path.Combine(dir, "BepInEx");
            if (!Directory.Exists(bepDir)) return null;
            return Directory.EnumerateFiles(bepDir, ModPrimaryDll,
                                            SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { return null; }
    }

    private static bool BepInExPresent(string gameDir)
    {
        try
        {
            // BepInEx 5 portable zip extracts winhttp.dll to the game root as the
            // loader patcher and creates a BepInEx/ folder.
            return File.Exists(Path.Combine(gameDir, "winhttp.dll")) &&
                   Directory.Exists(Path.Combine(gameDir, "BepInEx"));
        }
        catch { return false; }
    }

    // ── Private helpers — Archipelago custom_worlds detection ────────────────

    private static string? FindArchipelagoCustomWorldsDir()
    {
        // Standard AP installation: %LocalAppData%\Archipelago\lib\worlds\
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string candidate = Path.Combine(local, "Archipelago", "lib", "worlds");
            if (Directory.Exists(candidate)) return candidate;
        }
        catch { /* ignore */ }

        // Fallback: custom_worlds next to ArchipelagoLauncher.exe (portable install).
        try
        {
            foreach (string prog in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            })
            {
                if (string.IsNullOrWhiteSpace(prog)) continue;
                string launcherExe = Path.Combine(prog, "Archipelago", "ArchipelagoLauncher.exe");
                if (!File.Exists(launcherExe)) continue;
                string cw = Path.Combine(Path.GetDirectoryName(launcherExe)!, "custom_worlds");
                if (Directory.Exists(cw)) return cw;
            }
        }
        catch { /* ignore */ }

        return null;
    }

    // ── Private helpers — sidecar (override dir + version stamp) ────────────

    private string? LoadOverrideDir()
    {
        try
        {
            if (!File.Exists(SettingsSidecarPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsSidecarPath));
            if (doc.RootElement.TryGetProperty("install_dir", out var v))
            {
                string? s = v.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    private void SaveOverrideDir(string dir)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            var obj = new Dictionary<string, object?> { ["install_dir"] = dir };
            // Preserve existing keys (e.g. installed_version).
            if (File.Exists(SettingsSidecarPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(SettingsSidecarPath));
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        if (prop.Name != "install_dir")
                            obj[prop.Name] = prop.Value.GetString();
                }
                catch { /* ignore */ }
            }
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* ignore */ }
    }

    private string? ReadStampedVersion()
    {
        try
        {
            if (!File.Exists(SettingsSidecarPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(SettingsSidecarPath));
            if (doc.RootElement.TryGetProperty("installed_version", out var v))
                return v.GetString();
        }
        catch { /* ignore */ }
        return null;
    }

    private void WriteStampedVersion(string version)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            var obj = new Dictionary<string, object?> { ["installed_version"] = version };
            // Preserve existing keys (e.g. install_dir).
            if (File.Exists(SettingsSidecarPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(SettingsSidecarPath));
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        if (prop.Name != "installed_version")
                            obj[prop.Name] = prop.Value.GetString();
                }
                catch { /* ignore */ }
            }
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* ignore */ }
    }

    // ── Private helpers — GitHub release resolution ──────────────────────────

    private async Task<(string version, string? zipUrl, string? apworldUrl)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            string tag = doc.RootElement.TryGetProperty("tag_name", out var t)
                ? t.GetString() ?? FallbackModVersion
                : FallbackModVersion;

            string? zipUrl     = null;
            string? apworldUrl = null;

            if (doc.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = asset.TryGetProperty("browser_download_url", out var u)
                        ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    if (name.EndsWith(".apworld", StringComparison.OrdinalIgnoreCase))
                        apworldUrl = url;
                    else if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                             !name.Contains("apworld", StringComparison.OrdinalIgnoreCase))
                        zipUrl = url;
                }
            }

            return (tag, zipUrl ?? FallbackModZipUrl, apworldUrl ?? FallbackApworldUrl);
        }
        catch
        {
            return (FallbackModVersion, FallbackModZipUrl, FallbackApworldUrl);
        }
    }

    // ── Private helpers — download + extract ─────────────────────────────────

    private async Task DownloadAndExtractZipAsync(
        string url, string destDir, string label,
        int pctStart, int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempFile = Path.Combine(Path.GetTempPath(), $"lunacid_{label}_{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((pctStart, $"Downloading {label}..."));
            using (var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long? total = response.Content.Headers.ContentLength;
                await using var src  = await response.Content.ReadAsStreamAsync(ct);
                await using var dest = File.Create(tempFile);
                var  buffer = new byte[65536];
                long read   = 0;
                int  n;
                while ((n = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dest.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total > 0)
                    {
                        int pct = pctStart + (int)((double)read / total.Value * (pctEnd - pctStart - 5));
                        progress.Report((pct, $"Downloading {label} ({read / 1024}KB / {total.Value / 1024}KB)..."));
                    }
                }
            }

            progress.Report((pctEnd - 4, $"Extracting {label}..."));
            Directory.CreateDirectory(destDir);
            ZipFile.ExtractToDirectory(tempFile, destDir, overwriteFiles: true);
            progress.Report((pctEnd, $"{label} extracted."));
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    // ── Private helpers — game launch ────────────────────────────────────────

    private void StartGame()
    {
        string? gameDir = ResolveGameDir();
        string? exePath = gameDir != null ? Path.Combine(gameDir, GameExeName) : null;

        if (exePath != null && File.Exists(exePath))
        {
            _gameProcess = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName         = exePath,
                    WorkingDirectory = gameDir!,
                    UseShellExecute  = true,
                },
                EnableRaisingEvents = true,
            };
            _gameProcess.Exited += (_, _) =>
            {
                IsRunning = false;
                GameExited?.Invoke(_gameProcess?.ExitCode ?? 0);
            };
            _gameProcess.Start();
            IsRunning = true;
        }
        else
        {
            // Fallback: launch via Steam protocol.
            Process.Start(new ProcessStartInfo
            {
                FileName        = SteamRunUrl,
                UseShellExecute = true,
            });
            IsRunning = true;
        }
    }

    // ── Private helpers — URL opener ─────────────────────────────────────────

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = url,
                UseShellExecute = true,
            });
        }
        catch { /* ignore */ }
    }
}
