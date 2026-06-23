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

// NOTE on type qualification (BUILD GOTCHA — verified against this repo):
// LauncherV2.csproj sets BOTH <UseWPF>true</UseWPF> AND <UseWindowsForms>true</UseWindowsForms>.
// That makes many simple type names ambiguous between WPF and WinForms
// (Application, MessageBox, Color, Brush(es), Button, TextBox, CheckBox, Orientation,
// FontWeights, HorizontalAlignment, Cursors, Thickness, …) → CS0104.
// The repo's GlobalUsings.cs aliases the short names project-wide, but this plugin
// is written to compile EVEN WITHOUT GlobalUsings, so every WPF UI type below is
// FULLY QUALIFIED (System.Windows.Controls.*, System.Windows.Media.*,
// System.Windows.MessageBox, …) and this file adds NO file-level
// `using X = System.Windows...;` alias (that would collide with the GlobalUsings
// aliases → CS1537). Bare names / fully-qualified only.

namespace LauncherV2.Plugins.HatsuneMikuDiva;

// ═══════════════════════════════════════════════════════════════════════════════
// HatsuneMikuDivaPlugin — install / launch for
// "Hatsune Miku: Project DIVA Mega Mix+" with the Cynichill DivaAPworld mod.
//
// ── VERIFIED FACTS (2026-06-14) ───────────────────────────────────────────────
//
//   * GAME: Hatsune Miku: Project DIVA Mega Mix+ (Steam appid 1761390).
//     Source: setup_en.md + APSettings.cpp in Cynichill/Diva-Archipelago-Mod.
//
//   * AP WORLD game string (EXACT):  "Hatsune Miku Project Diva Mega Mix+"
//     Verified against Cynichill/DivaAPworld/__init__.py:
//       `game = "Hatsune Miku Project Diva Mega Mix+"`
//     (Note: the C++ mod uses the same string in APClient::GameName.)
//
//   * AP WORLD repo:  Cynichill/DivaAPworld
//     MOD repo:       Cynichill/Diva-Archipelago-Mod
//     GameBanana mod: https://gamebanana.com/mods/514140
//
//   * MOD LOADER: DivaModLoader (blueskythlikesclouds/DivaModLoader).
//     Installed as dinput8.dll in the game root (portable, no wizard).
//     Mods live in <game root>\mods\, each in their own sub-folder.
//     The Archipelago mod folder is mods\ArchipelagoMod\ and ships:
//         ArchipelagoMod.dll, config.toml, …
//     (Optionally DivaModManager by Enomoto can be used as a GUI front-end
//      for DivaModLoader, but it is not required for the launcher integration.)
//
//   * CONNECTION CONFIG: settings.toml written to the GAME ROOT directory.
//     Verified from Cynichill/Diva-Archipelago-Mod/APSettings.cpp:
//         const fs::path SettingsTOML = LocalPath / "settings.toml";
//         // LocalPath = fs::current_path() = the game exe's working directory
//     And from APClient.cpp (APClient::config + APClient::save):
//         [client]
//         slot_name     = "Player1"
//         slot_server   = "archipelago.gg:38281"   # host:port as a single string
//         slot_server_hide = false
//         slot_password = ""
//     The mod reads this file on startup (only when not already connected).
//     This plugin pre-writes settings.toml before launching the game so the
//     connection fields are pre-filled — the user just clicks Connect in-game.
//     The password field is scrubbed (set to "") on StopAsync.
//
//   * HOW IT CONNECTS: the mod provides an in-game ImGui overlay (tab "Client")
//     where the user enters server, slot name and password, then clicks Connect.
//     Because settings.toml is pre-written, those fields are already populated.
//     ConnectsItself = true (the ArchipelagoMod.dll owns the AP slot — the
//     launcher must NOT hold its own ApClient on the same slot while the game
//     runs; the launcher and game would kick each other off).
//
//   * LAUNCH: The game executable is "DivaMegaMix.exe". The default Steam path
//     is "C:\Program Files (x86)\Steam\steamapps\common\
//         Hatsune Miku Project DIVA Mega Mix Plus\DivaMegaMix.exe"
//     (Verified from MegaMixSettings.GameExe in DivaAPworld/__init__.py.)
//     If not found directly, fall back to steam://rungameid/1761390.
//
//   * DETECTION: an installation is considered "modded" (for the mod) when
//     mods\ArchipelagoMod\ArchipelagoMod.dll exists relative to the game root.
//     DivaModLoader is detected by the presence of dinput8.dll in the game root.
//
// ── WHAT THIS PLUGIN DOES ────────────────────────────────────────────────────
//   1. DETECT the Steam install via registry + libraryfolders.vdf (appid 1761390).
//      A manual install-dir override is supported (Settings folder picker) and
//      stored in this plugin's own sidecar JSON.
//   2. INSTALL/UPDATE (best effort):
//       a. If DivaModLoader is not present (no dinput8.dll), download and install
//          it (blueskythlikesclouds/DivaModLoader, latest release).
//       b. Download and extract the latest ArchipelagoMod from
//          Cynichill/Diva-Archipelago-Mod releases into <game>\mods\ArchipelagoMod\.
//   3. PRE-FILL settings.toml in the game root with the session's host:port,
//      slot name and password before launching. Scrub the password field on stop.
//   4. LAUNCH DivaMegaMix.exe (or fall back to the steam:// URL).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class HatsuneMikuDivaPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    // Steam
    private const string SteamAppId          = "1761390";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";
    private const string SteamCommonFolder   = "Hatsune Miku Project DIVA Mega Mix Plus";
    private const string GameExeName         = "DivaMegaMix.exe";

    // AP world
    // EXACT string from Cynichill/DivaAPworld/__init__.py, verified 2026-06-14.
    private const string ApGame              = "Hatsune Miku Project Diva Mega Mix+";

    // Mod loader — DivaModLoader (blueskythlikesclouds/DivaModLoader)
    private const string DmlOwner            = "blueskythlikesclouds";
    private const string DmlRepo             = "DivaModLoader";
    private static readonly string DmlReleasesLatestUrl =
        $"https://api.github.com/repos/{DmlOwner}/{DmlRepo}/releases/latest";
    private static readonly string DmlRepoUrl =
        $"https://github.com/{DmlOwner}/{DmlRepo}";
    // DivaModLoader drops a "dinput8.dll" proxy into the game root.
    private const string DmlProxyDll         = "dinput8.dll";
    // Pinned fallback version when the GitHub API is unreachable.
    private const string DmlFallbackVersion  = "1.8";
    private static readonly string DmlFallbackZipUrl =
        $"{DmlRepoUrl}/releases/download/{DmlFallbackVersion}/DivaModLoader.zip";

    // Archipelago mod — Cynichill/Diva-Archipelago-Mod
    private const string ModOwner            = "Cynichill";
    private const string ModRepo             = "Diva-Archipelago-Mod";
    private static readonly string ModReleasesLatestUrl =
        $"https://api.github.com/repos/{ModOwner}/{ModRepo}/releases/latest";
    private static readonly string ModReleasesUrl =
        $"https://api.github.com/repos/{ModOwner}/{ModRepo}/releases";
    private static readonly string ModRepoUrl =
        $"https://github.com/{ModOwner}/{ModRepo}";
    // The AP mod's primary DLL name.
    private const string ModDllName          = "ArchipelagoMod.dll";
    // The mods sub-folder name the mod expects.
    private const string ModFolderName       = "ArchipelagoMod";
    // Pinned fallback.
    private const string ModFallbackVersion  = "1.3.0";
    private static readonly string ModFallbackZipName = "ArchipelagoMod.zip";
    private static readonly string ModFallbackZipUrl  =
        $"{ModRepoUrl}/releases/download/{ModFallbackVersion}/{ModFallbackZipName}";

    // AP world repo (for the news feed)
    private static readonly string ApWorldReleasesUrl =
        $"https://api.github.com/repos/{ModOwner}/DivaAPworld/releases";

    // Links shown in the Settings panel
    private const string GameBananaUrl   = "https://gamebanana.com/mods/514140";
    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/" +
        "Hatsune%20Miku%20Project%20Diva%20Mega%20Mix%2B/setup_en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "hatsune_miku_diva";
    public string DisplayName => "Hatsune Miku: Project DIVA Mega Mix+";
    public string Subtitle    => "Rhythm game · DivaModLoader · built-in AP client";
    public string ApWorldName => ApGame;   // "Hatsune Miku Project Diva Mega Mix+"

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "hatsune_miku_diva.png");

    public string ThemeAccentColor => "#00BFFF";  // aqua-cyan, Miku's signature teal
    public string[] GameBadges     => new[] { "Requires Steam", "Needs mod" };

    public string Description =>
        "Hatsune Miku: Project DIVA Mega Mix+ is a rhythm game featuring 250+ songs " +
        "from Hatsune Miku's discography. In Archipelago mode (via Cynichill's " +
        "DivaAPworld and ArchipelagoMod), songs are locked behind items scattered " +
        "across the multiworld. Play through a set of randomly chosen songs, " +
        "collecting leeks until you have enough to challenge and clear the goal song. " +
        "The mod ships its own in-game Archipelago client powered by DivaModLoader, " +
        "so the game connects to the AP server directly — no external bridge needed. " +
        "You need the Steam version of the game; the launcher detects your install, " +
        "stages DivaModLoader and the ArchipelagoMod, and pre-fills your connection " +
        "details so you just click Connect in the in-game overlay.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = ArchipelagoMod.dll is present under the detected/override
    /// game root's mods\ArchipelagoMod\ folder.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Steam", "steamapps", "common", SteamCommonFolder);

    private string SettingsSidecarDir =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SettingsSidecarPath =>
        Path.Combine(SettingsSidecarDir, "diva_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The ArchipelagoMod connects to the AP server directly (ConnectsItself).
    // These events are declared for interface compatibility and never raised.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself / SupportsStandalone ───────────────────────────────────

    /// The ArchipelagoMod owns the AP slot connection; the launcher must NOT
    /// hold a parallel ApClient session on the same slot while the game runs.
    public bool ConnectsItself    => true;

    /// Project DIVA Mega Mix+ plays perfectly without an Archipelago connection.
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
        catch { InstalledVersion = null; }
            try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(ModOwner, ModRepo, ct));
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
        progress.Report((2, "Locating your Project DIVA Mega Mix+ installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Hatsune Miku: Project DIVA Mega Mix+ installation. " +
                "Open this game's Settings and pick your game folder (the one containing " +
                "\"DivaMegaMix.exe\"), or install the game via Steam first.");

        // 1. Ensure DivaModLoader is present (dinput8.dll in game root).
        if (!DivaModLoaderPresent(gameDir))
        {
            progress.Report((6, "Downloading DivaModLoader..."));
            var (dmlVer, dmlZipUrl) = await ResolveLatestDmlAsync(ct);
            if (dmlZipUrl != null)
            {
                await DownloadAndExtractZipToDirAsync(
                    dmlZipUrl, $"dml-{dmlVer}", gameDir, 6, 35, progress, ct);
            }
            else
            {
                progress.Report((35,
                    "Could not resolve DivaModLoader automatically. " +
                    "Install it manually from " + DmlRepoUrl + " (see Settings)."));
            }
        }
        else
        {
            progress.Report((35, "DivaModLoader already present — skipping."));
        }

        // 2. Ensure the mods/ folder exists.
        string modsDir  = Path.Combine(gameDir, "mods");
        string modDir   = Path.Combine(modsDir, ModFolderName);
        Directory.CreateDirectory(modDir);

        // 3. Download the latest ArchipelagoMod and extract into mods\ArchipelagoMod\.
        progress.Report((38, "Checking the latest ArchipelagoMod release..."));
        var (modVersion, modZipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = modVersion;

        if (modZipUrl == null)
            throw new InvalidOperationException(
                "Could not find the ArchipelagoMod download on GitHub. Check your " +
                "internet connection, or download it manually from " + ModRepoUrl +
                "/releases (see Settings).");

        await DownloadAndExtractModAsync(modZipUrl, modVersion, modDir, 40, 92, progress, ct);

        WriteStampedVersion(modVersion);
        InstalledVersion = modVersion;

        bool dmlOk = DivaModLoaderPresent(gameDir);
        bool modOk = File.Exists(Path.Combine(modDir, ModDllName));
        progress.Report((100,
            $"ArchipelagoMod {modVersion} staged into mods\\{ModFolderName}" +
            (dmlOk ? " (DivaModLoader present)." : " (DivaModLoader NOT detected).") +
            (!modOk ? " ArchipelagoMod.dll not confirmed — check the mods folder." : "") +
            " Launch the game and use the in-game client overlay (Client tab) to connect. " +
            "Your server details will be pre-filled when you launch from this tile."));
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
        // Pre-fill the settings.toml in the game root so the mod picks up the
        // connection details on startup (slot_name, slot_server host:port,
        // slot_password). Verified format from APClient.cpp (APClient::config +
        // APClient::save): TOML [client] table with those three keys.
        string? gameDir = ResolveGameDir();
        if (gameDir != null)
            WriteSettingsToml(gameDir, session);

        StartGame();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // Standalone launch (no AP connection pre-fill).
        StartGame();
        return Task.CompletedTask;
    }

    // ── Lifecycle — Stop ──────────────────────────────────────────────────────

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        _gameProcess = null;

        // Scrub the password from settings.toml so it is not left on disk.
        try
        {
            string? gameDir = ResolveGameDir();
            if (gameDir != null)
                ScrubSettingsTomlPassword(gameDir);
        }
        catch { }

        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── ValidateExistingInstall ───────────────────────────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Project DIVA Mega Mix+ install folder.";

        if (LooksLikeGameDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolder);
            if (LooksLikeGameDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Project DIVA Mega Mix+ installation. Pick the " +
               "folder that contains \"DivaMegaMix.exe\" (for Steam this is usually " +
               @"...\steamapps\common\Hatsune Miku Project DIVA Mega Mix Plus).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0xBF, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Description header ────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Hatsune Miku: Project DIVA Mega Mix+ (Steam) is your own copy of the " +
                   "game. The Archipelago mod runs on DivaModLoader (a small DLL injector " +
                   "dropped into the game folder). The launcher can stage DivaModLoader and " +
                   "the ArchipelagoMod for you, then pre-fills your AP connection details " +
                   "in settings.toml before each launch so you just click Connect in-game.",
            FontSize = 11, Foreground = warn, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install ─────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GAME INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();
        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Project DIVA Mega Mix+ not detected. Pick your install folder below " +
              "or install the game via Steam first.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // DivaModLoader status
        bool dmlOk = gameDir != null && DivaModLoaderPresent(gameDir);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = gameDir == null
                    ? ""
                    : (dmlOk
                        ? "DivaModLoader found (dinput8.dll present)."
                        : "DivaModLoader not found yet. Use Install on the Play tab " +
                          "or download it from " + DmlRepoUrl + " and extract it " +
                          "into your game folder."),
            FontSize = 11, Foreground = dmlOk ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        // ArchipelagoMod status
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                    ? "ArchipelagoMod found: " + modDll
                    : "ArchipelagoMod not found yet. Use Install on the Play tab to " +
                      "stage it, or download it manually from " + ModRepoUrl + "/releases.",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new System.Windows.Controls.DockPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text       = overrideDir ?? gameDir ?? "",
            IsReadOnly = true,
            FontSize   = 12,
            Margin     = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip    = "Your Project DIVA Mega Mix+ install folder (containing DivaMegaMix.exe).",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Project DIVA Mega Mix+ install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                                   ? (overrideDir ?? gameDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a valid game folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeGameDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolder);
                    if (LooksLikeGameDir(nested)) picked = nested;
                }
                SaveOverrideDir(picked);
                dirBox.Text = picked;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(
            dirBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Steam installs are detected automatically (appid 1761390). " +
                   "Use the picker for a non-standard library location.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ───────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (in-game overlay)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "When you launch from this tile your server address, slot name, and " +
                   "password are pre-filled in the game's settings.toml. After the game " +
                   "starts, open the DivaModLoader overlay (usually F1 or the key shown " +
                   "in config.toml), go to the Client tab, verify the fields, and click " +
                   "Connect.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "SETUP GUIDE", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own and install Hatsune Miku: Project DIVA Mega Mix+ on Steam. " +
                "Use the folder picker above if it was not auto-detected.",
            "2. Click Install on the Play tab. The launcher stages DivaModLoader " +
                "(dinput8.dll) and the ArchipelagoMod into your game folder.",
            "3. Launch the game from this tile. Your server address, slot name, and " +
                "password are pre-filled in settings.toml.",
            "4. In-game, open the mod overlay and go to the Client tab. Verify the " +
                "pre-filled fields and click Connect. That's it.",
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
            Text = "LINKS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("ArchipelagoMod (GitHub) ↗",       ModRepoUrl),
            ("DivaAPworld (GitHub) ↗",           $"https://github.com/{ModOwner}/DivaAPworld"),
            ("Archipelago Mod (GameBanana) ↗",   GameBananaUrl),
            ("DivaModLoader (GitHub) ↗",         DmlRepoUrl),
            ("Setup Guide ↗",                   SetupGuideUrl),
            ("Archipelago Official ↗",           ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0),
                FontSize = 12,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = accent,
                Cursor = System.Windows.Input.Cursors.Hand,
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
            string json = await _http.GetStringAsync(ApWorldReleasesUrl, ct);
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
                    Version: NormalizeTag(el.TryGetProperty("tag_name", out var t) ? t.GetString() : null) ?? "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — settings.toml (connection pre-fill) ─────────────────

    /// The path to settings.toml — written to the game's working directory (the
    /// game root), matching APSettings.cpp: `LocalPath = fs::current_path()`.
    private static string SettingsTomlPath(string gameDir)
        => Path.Combine(gameDir, "settings.toml");

    /// Pre-write (or overwrite) settings.toml with the session's AP credentials.
    /// Format verified from APClient.cpp (save + config functions):
    ///
    ///   [client]
    ///   slot_name = "Player1"
    ///   slot_server = "archipelago.gg:38281"
    ///   slot_server_hide = false
    ///   slot_password = ""
    ///
    /// slot_server is "host:port" as a single string (the mod parses it that way).
    private static void WriteSettingsToml(string gameDir, ApSession session)
    {
        string path = SettingsTomlPath(gameDir);

        // Parse host and port from session.ServerUri.
        // ServerUri is typically "host:port" or "ws://host:port".
        string serverField = ParseSlotServer(session.ServerUri);

        // Escape double-quotes inside string values (TOML basic strings).
        string slotName   = TomlEscapeString(session.SlotName);
        string server     = TomlEscapeString(serverField);
        string password   = TomlEscapeString(session.Password ?? "");

        string content =
            "# Pre-filled by the Archipelago Launcher. Settings can be changed in-game.\n" +
            "# Invalid or missing settings use their defaults.\n\n" +
            "[client]\n" +
            $"slot_name = \"{slotName}\"\n" +
            $"slot_server = \"{server}\"\n" +
            "slot_server_hide = false\n" +
            $"slot_password = \"{password}\"\n";

        Directory.CreateDirectory(gameDir);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    /// Remove the password from settings.toml (blank it out) on session end.
    private static void ScrubSettingsTomlPassword(string gameDir)
    {
        string path = SettingsTomlPath(gameDir);
        if (!File.Exists(path)) return;

        string text = File.ReadAllText(path);
        // Replace the slot_password line value with an empty string.
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            string trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("slot_password", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = "slot_password = \"\"";
            }
        }
        File.WriteAllText(path, string.Join('\n', lines), new UTF8Encoding(false));
    }

    /// Parse "host:port" (or "ws://host:port") → return the raw "host:port"
    /// string that the mod expects in slot_server. When no port is present, use
    /// the default AP port 38281.
    private static string ParseSlotServer(string serverUri)
    {
        // Strip ws:// or wss:// prefix if present.
        string s = serverUri.Trim();
        if (s.StartsWith("ws://",  StringComparison.OrdinalIgnoreCase)) s = s[5..];
        else if (s.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)) s = s[6..];

        // If a port is already in the string, return as-is.
        // A colon is present: could be "host:port" or IPv6 — keep it.
        // For bare hostnames with no colon, append the default port.
        int colonIdx = s.LastIndexOf(':');
        if (colonIdx < 0)
            return s + ":38281";

        return s;
    }

    /// Minimal TOML basic-string escaping: escape backslash and double-quote.
    private static string TomlEscapeString(string value)
        => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ── Private helpers — game dir detection ──────────────────────────────────

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
            return false;
        }
        catch { return false; }
    }

    private static bool DivaModLoaderPresent(string gameDir)
    {
        try { return File.Exists(Path.Combine(gameDir, DmlProxyDll)); }
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
                    string conventional = Path.Combine(common, SteamCommonFolder);
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
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormSteamPath(hkcu);

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormSteamPath(hklm);

        string? hklm64 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm64)) yield return NormSteamPath(hklm64);

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    private static string NormSteamPath(string p)
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

    // ── Private helpers — mod detection ──────────────────────────────────────

    private string? FindInstalledModDll()
    {
        try
        {
            string? gameDir = ResolveGameDir();
            if (gameDir == null) return null;
            string modDllPath = Path.Combine(gameDir, "mods", ModFolderName, ModDllName);
            return File.Exists(modDllPath) ? modDllPath : null;
        }
        catch { return null; }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        string? gameDir = ResolveGameDir();
        string? exe     = gameDir != null ? Path.Combine(gameDir, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = gameDir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException(
                "Failed to start DivaMegaMix.exe.");

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

        // Fall back to Steam if the exe was not found.
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
            "Could not find \"DivaMegaMix.exe\". Open this game's Settings and pick " +
            "your Project DIVA Mega Mix+ install folder, or install it via Steam.",
            GameExeName);
    }

    // ── Private helpers — GitHub release resolution ───────────────────────────

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..] : tag;
    }

    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(ModReleasesLatestUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString()) : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u)
                                   ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        return (version, url);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return (ModFallbackVersion, ModFallbackZipUrl);
    }

    private async Task<(string Version, string? ZipUrl)> ResolveLatestDmlAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(DmlReleasesLatestUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString()) : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u)
                                   ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        return (version, url);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return (DmlFallbackVersion, DmlFallbackZipUrl);
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    /// Download and extract the ArchipelagoMod zip into modDir (mods\ArchipelagoMod\).
    /// The zip may ship with a single wrapper folder; if so, flatten it so
    /// ArchipelagoMod.dll lands directly in modDir.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string modDir,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"diva-ap-mod-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 7 / 10;
            await DownloadFileAsync(zipUrl, tempZip,
                $"Downloading ArchipelagoMod {version}...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, $"Extracting ArchipelagoMod into mods\\{ModFolderName}..."));
            Directory.CreateDirectory(modDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, modDir, overwriteFiles: true);

            // Flatten a single wrapper folder if the DLL did not land directly.
            if (!File.Exists(Path.Combine(modDir, ModDllName)))
            {
                string? srcDir = FindDirContaining(modDir, ModDllName);
                if (srcDir != null && !PathEquals(srcDir, modDir))
                {
                    foreach (string src in Directory.EnumerateFiles(
                        srcDir, "*", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(srcDir, src);
                        string dst = Path.Combine(modDir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        File.Copy(src, dst, overwrite: true);
                    }
                }
            }
            progress.Report((pctEnd, "ArchipelagoMod installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    /// Download and extract a portable zip (e.g. DivaModLoader) into targetDir.
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
                "Downloading DivaModLoader...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Extracting DivaModLoader into game folder..."));
            Directory.CreateDirectory(targetDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, targetDir, overwriteFiles: true);
            progress.Report((pctEnd, "DivaModLoader staged."));
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

    private static bool PathEquals(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    // ── Private helpers — sidecar settings ───────────────────────────────────

    private sealed class DivaSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private DivaSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<DivaSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(DivaSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
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
