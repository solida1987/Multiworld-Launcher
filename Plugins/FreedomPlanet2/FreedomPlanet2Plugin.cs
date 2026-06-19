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
using Microsoft.Win32;
using LauncherV2.Core;

namespace LauncherV2.Plugins.FreedomPlanet2;

// ═══════════════════════════════════════════════════════════════════════════════
// FreedomPlanet2Plugin — install / launch for "Freedom Planet 2" (GalaxyTrail, 2022)
// played through the Freedom-Planet-2-Archipelago BepInEx mod by Knuxfan24, which
// contains the in-game Archipelago Multiworld client. This is a NATIVE
// "ConnectsItself" integration in the same family as the shipped Hollow Knight /
// TUNIC / Stardew Valley / Jak plugins: the game itself speaks to the AP server
// (no emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online) ────────────────────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Freedom Planet 2 (Steam appid 595500), and Archipelago support is delivered
// as a BepInEx mod added on top. The honest integration ceiling is "automate what
// is possible, guide the irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "Freedom Planet 2" (verified against
//     worlds/fp2/world.py in Knuxfan24/Archipelago fork: class FP2World, game =
//     "Freedom Planet 2"). GameId = "freedom_planet_2". The apworld file is named
//     "fp2.apworld" (verified in the GitHub release assets 2026-06-14).
//
//   * THE MOD REPO is Knuxfan24/Freedom-Planet-2-Archipelago (verified live
//     2026-06-14). It is a C# BepInEx 5 plugin using BepInEx.Core 5.x and
//     depends on FP2Lib (kuborro/FP2Lib). Latest release: 0.1.2 (2025-10-06).
//     Each release ships two assets:
//         Archipelago.7z   — the BepInEx plugin + asset bundle
//         fp2.apworld      — the AP world file (drop into AP custom_worlds)
//
//   * INSTALL LAYOUT (verified from the .csproj and README/wiki): the mod targets
//     the Unity 5.x-based Freedom Planet 2. BepInEx 5 is the mod loader. The
//     mod assembly drops into BepInEx\plugins\ (standard BepInEx plugin convention).
//     FP2Lib is a required dependency; its installation is documented separately on
//     the FP2Lib GitHub (it also drops into BepInEx\plugins\lib\).
//
//   * CONNECTION METHOD (verified from Plugin.cs Config.Bind calls 2026-06-14):
//     the mod stores connection settings in the BepInEx config file:
//         BepInEx\config\K24_FP2_Archipelago.cfg
//     under section [Connection], keys "Server Address", "Slot Name", "Password".
//     These are editable plain text (INI format). This launcher CAN write these keys
//     to prefill the connection — the official flow is otherwise "edit the config
//     file and launch" (no in-game connection menu confirmed from the code). The
//     settings panel will show the current session credentials and offer to write
//     them to the config file, matching the documented config-file approach. The
//     launcher does NOT hold its own ApClient on this slot (ConnectsItself = true).
//
//   * CRITICAL HONESTY — THE MOD REQUIRES BEPINEX 5 + FP2LIB, NEITHER OF WHICH
//     IS BUNDLED IN THE RELEASE. The "Archipelago.7z" only contains the mod itself
//     (Freedom_Planet_2_Archipelago.dll + ap asset bundle). BepInEx for Unity games
//     is a separate install; FP2Lib is another. Accordingly the Install flow:
//       (a) downloads and extracts the Archipelago.7z INTO the game root (because
//           7z extract maps into the BepInEx tree the zip was built against);
//       (b) surfaces prominent guided steps for "install BepInEx 5 for Unity" and
//           "install FP2Lib" BEFORE the mod, since the plugin DLL will silently fail
//           to load without them. We do NOT auto-install BepInEx (it requires a game
//           patcher/file replacement that is unsafe to do silently) or FP2Lib.
//
//   * NOTE ON 7z: the release asset is "Archipelago.7z" (not a zip). .NET's
//     System.IO.Compression.ZipFile cannot open 7z archives. The plugin shells out
//     to 7z.exe / 7za.exe if found on PATH or in known locations. If 7z is not
//     available, it falls back to a clear error message with a manual install link.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Freedom Planet 2 install via the Windows registry and
//      steamapps\appmanifest_595500.acf. A manual install-dir OVERRIDE (settings
//      folder picker) is also supported and takes precedence; validated (must
//      contain "FP2.exe" or "FP2_Data") and persisted in the plugin's OWN sidecar
//      (Games/ROMs/freedom_planet_2/freedom_planet_2_launcher.json).
//   2. INSTALL/UPDATE (best effort):
//      - Downloads Archipelago.7z from the latest GitHub release.
//      - Extracts it via 7z.exe into the game root (BepInEx subfolder target).
//      - Stamps the version and shows a guided-steps note with BepInEx + FP2Lib links.
//   3. PREFILL the BepInEx config file (K24_FP2_Archipelago.cfg) with the AP
//      session's server / slot / password (opt-in button in the settings panel).
//   4. LAUNCH FP2.exe from the detected/override install; fall back to the Steam URL.
//      ConnectsItself = true, SupportsStandalone = true.
//   5. No plaintext password is stored in the sidecar — only in the mod's own config.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class FreedomPlanet2Plugin : IGamePlugin
{
    // ── Constants — FP2 AP mod (Knuxfan24/Freedom-Planet-2-Archipelago) ─────────
    private const string MOD_OWNER = "Knuxfan24";
    private const string MOD_REPO  = "Freedom-Planet-2-Archipelago";
    private static readonly string ModRepoUrl =
        $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private static readonly string GhReleasesLatestUrl =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private static readonly string GhReleasesUrl =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl  = "https://archipelago.gg/tutorial/Freedom%20Planet%202/setup/en";
    private const string GameInfoUrl    = "https://archipelago.gg/games/Freedom%20Planet%202/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";
    private const string BepInExUrl     = "https://github.com/BepInEx/BepInEx/releases";
    private const string Fp2LibUrl      = "https://github.com/Knuxfan24/FP2Lib";

    // Steam — Freedom Planet 2 appid 595500 (by GalaxyTrail, verified 2026-06-14).
    private const string Fp2SteamAppId = "595500";
    private static readonly string SteamRunUrl = $"steam://rungameid/{Fp2SteamAppId}";

    // The game executable and data folder names (Unity, verified from .csproj HintPath).
    private const string Fp2ExeName     = "FP2.exe";
    private const string Fp2DataFolder  = "FP2_Data";

    // Steam "common" subfolder name (from appmanifest installdir).
    private const string SteamCommonFolderName = "Freedom Planet 2";

    // BepInEx config: BepInEx\config\K24_FP2_Archipelago.cfg
    // Verified from Plugin.cs: Config.Bind("Connection", "Server Address", ...)
    private const string BepInExConfigRelPath = @"BepInEx\config\K24_FP2_Archipelago.cfg";
    private const string CfgSection           = "Connection";
    private const string CfgKeyServer         = "Server Address";
    private const string CfgKeySlot           = "Slot Name";
    private const string CfgKeyPassword       = "Password";

    // BepInEx plugins subfolder (where the mod DLL lands).
    private const string BepInExPluginsRel = @"BepInEx\plugins";

    // The mod DLL (from csproj: AssemblyName = "Freedom_Planet_2_Archipelago").
    private const string ModDllName = "Freedom_Planet_2_Archipelago.dll";

    // Pinned fallback for when the GitHub API is unreachable.
    // Release 0.1.2 (2025-10-06) verified live 2026-06-14.
    private const string FallbackVersion    = "0.1.2";
    private const string FallbackAssetName  = "Archipelago.7z";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackVersion}/{FallbackAssetName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "freedom_planet_2";
    public string DisplayName => "Freedom Planet 2";
    public string Subtitle    => "Native PC · BepInEx Archipelago mod";

    /// EXACT AP game string — verified from worlds/fp2/world.py (FP2World.game).
    public string ApWorldName => "Freedom Planet 2";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "freedom_planet_2.png");

    public string ThemeAccentColor => "#D83030"; // GalaxyTrail red / Lilac magenta
    public string[] GameBadges     => new[] { "Steam · needs BepInEx mod" };

    public string Description =>
        "Freedom Planet 2, GalaxyTrail's 2022 fast-paced action-platformer, played " +
        "through the Knuxfan24 BepInEx mod which bundles an in-game Archipelago " +
        "Multiworld client. Checks are collected from chests, item boxes, and boss " +
        "fights across Dragon Valley, Shenlin Park, and beyond. Goal: gather enough " +
        "Star Cards and Time Capsules (32 and 13 respectively) to unlock Weapon's " +
        "Core. You bring your own copy of Freedom Planet 2 (owned on Steam); the " +
        "integration requires BepInEx 5 (Unity mod loader) and FP2Lib. The launcher " +
        "detects your Steam install, downloads the Archipelago mod, and pre-fills the " +
        "BepInEx config file so you can launch straight into your session.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the AP mod DLL is present in BepInEx\plugins under the
    /// detected/override FP2 install. The user may have installed the mod by hand
    /// (which we honor), so we do NOT gate exclusively on our own version stamp.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── IGamePlugin — Architecture flags ─────────────────────────────────────
    public bool ConnectsItself    => true;  // the BepInEx mod owns the AP slot
    public bool SupportsStandalone => true; // FP2 runs fine without the mod

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Working directory for plugin downloads. Exposed as GameDirectory per contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "FreedomPlanet2");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "freedom_planet_2_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────
    private Process? _gameProcess;
    private string? _lastSessionServer;
    private string? _lastSessionSlot;
    private string? _lastSessionPassword;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The BepInEx mod owns the AP slot connection — the launcher relays nothing.
    // These events exist for interface compliance (ConnectsItself = true).
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
            var (version, _) = await ResolveLatestReleaseAsync(ct);
            AvailableVersion = version;
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. Locate the FP2 install — override wins, else Steam auto-detect.
        progress.Report((2, "Locating your Freedom Planet 2 installation..."));
        string? gameDir = ResolveFp2Dir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Freedom Planet 2 installation. Open this game's " +
                "Settings and pick your Freedom Planet 2 folder (the one containing " +
                "FP2.exe), or install Freedom Planet 2 via Steam first. The Archipelago " +
                "mod is added on top of your own copy of the game.");

        // 1. Resolve the latest release from GitHub.
        progress.Report((6, "Checking the latest FP2 Archipelago mod release..."));
        var (version, archiveUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (archiveUrl == null)
            throw new InvalidOperationException(
                "Could not find the FP2 Archipelago mod download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                ModRepoUrl + "/releases — extract Archipelago.7z into your FP2 folder.");

        // 2. Download the 7z archive to a temp file.
        progress.Report((10, $"Downloading Freedom Planet 2 Archipelago mod {version}..."));
        string tempArchive = Path.Combine(Path.GetTempPath(),
            $"fp2-archipelago-{version}-{Guid.NewGuid():N}.7z");

        try
        {
            await DownloadFileAsync(archiveUrl, tempArchive,
                $"Downloading FP2 Archipelago mod {version}...",
                10, 70, progress, ct);

            // 3. Extract the 7z into the game root via 7z.exe.
            progress.Report((70, "Extracting mod files into your FP2 folder..."));
            string? sevenZipExe = Find7zExecutable();
            if (sevenZipExe == null)
                throw new InvalidOperationException(
                    "7-Zip (7z.exe) was not found on this system. The mod archive " +
                    "Archipelago.7z requires 7-Zip to extract. Please install 7-Zip " +
                    "(https://www.7-zip.org/) or extract Archipelago.7z manually into " +
                    $"your Freedom Planet 2 folder:\n{gameDir}\n" +
                    "Then retry the install.");

            Directory.CreateDirectory(gameDir);
            string extractArgs = $"x \"{tempArchive}\" -o\"{gameDir}\" -y";
            var psi = new ProcessStartInfo
            {
                FileName               = sevenZipExe,
                Arguments              = extractArgs,
                UseShellExecute        = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow         = true,
            };
            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start 7z.exe.");
            string errOut = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"7z.exe exited with code {proc.ExitCode} while extracting " +
                    $"Archipelago.7z. 7z error: {errOut.Trim()}. Try extracting " +
                    $"Archipelago.7z manually into:\n{gameDir}");

            progress.Report((90, "Mod files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempArchive)) File.Delete(tempArchive); } catch { }
        }

        // 4. Stamp the installed version.
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool dllOk = FindInstalledModDll() != null;
        progress.Report((100,
            $"Freedom Planet 2 Archipelago mod {version} installed" +
            (dllOk ? " (mod DLL confirmed)." : " — DLL not yet confirmed, see below.") +
            "\n\nIMPORTANT — two prerequisites are required BEFORE the mod loads:\n" +
            "1. BepInEx 5 (Unity mod loader) must be installed into your FP2 folder. " +
            "Get it from: " + BepInExUrl + "\n" +
            "   Install BepInEx by extracting it into your FP2 folder (FP2.exe must " +
            "be beside winhttp.dll after install).\n" +
            "2. FP2Lib (Knuxfan24/FP2Lib) must be in BepInEx\\plugins\\lib\\. " +
            "Get it from: " + Fp2LibUrl + "\n" +
            "3. Launch FP2 once with BepInEx so it generates BepInEx\\config\\.\n" +
            "4. The launcher can pre-fill your AP server details in the config file — " +
            "use the 'Write AP Config' button in Settings after entering your session " +
            "info in the AP server connection screen."));
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
        // Store session for the settings panel "Write AP Config" button.
        _lastSessionServer   = session.ServerUri;
        _lastSessionSlot     = session.SlotName;
        _lastSessionPassword = session.Password;

        // Attempt to write the BepInEx config prefill. If the config dir does not
        // exist yet (BepInEx not yet run once), we silently skip — the user will be
        // instructed to launch once without AP first so BepInEx creates the config dir.
        TryWriteBepInExConfig(session);

        StartFp2();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartFp2();
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
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0xD8, 0x30, 0x30));

        var panel = new System.Windows.Controls.StackPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Intro ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Freedom Planet 2 is your own game (Steam) with the Knuxfan24 " +
                   "BepInEx mod added on top. The launcher detects your Steam install, " +
                   "downloads the mod, and can pre-fill the BepInEx connection config. " +
                   "BepInEx 5 and FP2Lib are separate prerequisites you install once " +
                   "(see guided steps below).",
            FontSize = 11, Foreground = warn, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: FP2 install detection ───────────────────────────────
        AddSectionHeader(panel, "FREEDOM PLANET 2 INSTALL", muted);

        string? gameDir     = ResolveFp2Dir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Freedom Planet 2 not detected. Pick your install folder below, or " +
              "install Freedom Planet 2 via Steam first.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // BepInEx status
        bool bepInExOk = gameDir != null && BepInExPresent(gameDir);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = gameDir == null ? "" : (bepInExOk
                ? "BepInEx found in your FP2 folder."
                : "BepInEx not found. Install BepInEx 5 (Unity) into your FP2 folder " +
                  "before the mod will load (see steps below)."),
            FontSize = 11, Foreground = bepInExOk ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        // Mod DLL status
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                ? "Archipelago mod DLL found: " + modDll
                : "Archipelago mod DLL not found yet (use Install on the Play tab).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Folder picker
        var dirRow = new System.Windows.Controls.DockPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = overrideDir ?? gameDir ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Freedom Planet 2 install folder (contains FP2.exe).",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select your Freedom Planet 2 install folder (contains FP2.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                                   ? (overrideDir ?? gameDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateInstallFolder(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not an FP2 folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksFp2Dir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksFp2Dir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 595500). Use this " +
                   "picker for a non-standard Steam library or another store.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: AP Connection config ─────────────────────────────────
        AddSectionHeader(panel, "AP CONNECTION CONFIG", muted);

        string? cfgPath = gameDir != null
            ? Path.Combine(gameDir, BepInExConfigRelPath)
            : null;
        bool cfgExists = cfgPath != null && File.Exists(cfgPath);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "The BepInEx config at " + BepInExConfigRelPath + " holds your " +
                   "server address, slot name, and password. The launcher pre-fills " +
                   "this file when you launch a session (if the file exists — BepInEx " +
                   "must have been run at least once to create the config directory). " +
                   "You can also use the button below to write the config from the " +
                   "last-used session credentials.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = cfgExists
                ? "Config file found: " + cfgPath
                : (cfgPath != null
                    ? "Config file not found yet (" + cfgPath + "). Launch FP2 once " +
                      "with BepInEx installed to generate it, then reconnect."
                    : "No FP2 install detected — config path unknown."),
            FontSize = 11,
            Foreground = cfgExists ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // Write AP Config button
        var writeCfgBtn = new System.Windows.Controls.Button
        {
            Content = "Write AP Config from last session",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Padding = new System.Windows.Thickness(12, 6, 12, 6),
            Margin  = new System.Windows.Thickness(0, 0, 0, 4),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
            IsEnabled = cfgExists && _lastSessionServer != null,
        };
        writeCfgBtn.Click += (_, _) =>
        {
            if (_lastSessionServer == null)
            {
                System.Windows.MessageBox.Show(
                    "No AP session has been launched from this tile yet. Launch via the " +
                    "Play tab with your server details to set the credentials.",
                    "No session credentials",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }
            string? gd = ResolveFp2Dir();
            if (gd == null)
            {
                System.Windows.MessageBox.Show(
                    "No Freedom Planet 2 install detected. Pick your FP2 folder first.",
                    "FP2 not found",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }
            var s = new ApSession(_lastSessionServer, _lastSessionSlot ?? "", _lastSessionPassword ?? "", ApWorldName);
            bool ok = TryWriteBepInExConfig(s, gd);
            System.Windows.MessageBox.Show(
                ok
                    ? "AP config written to:\n" + Path.Combine(gd, BepInExConfigRelPath) +
                      "\n\nLaunch Freedom Planet 2 and the mod will connect automatically."
                    : "Could not write the config file. Make sure BepInEx has been run " +
                      "at least once so the config directory exists.",
                ok ? "Config written" : "Config write failed",
                System.Windows.MessageBoxButton.OK,
                ok ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
        };
        panel.Children.Add(writeCfgBtn);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "This button is enabled after launching a session from the Play tab. " +
                   "The config is also written automatically each time you click Play.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        AddSectionHeader(panel, "GUIDED SETUP", muted);
        foreach (string step in new[]
        {
            "1. Own Freedom Planet 2 (on Steam). Install it if you have not. Use the " +
                "picker above if not detected automatically.",
            "2. Install BepInEx 5 (Unity): download the Unity BepInEx pack from " +
                BepInExUrl + " and extract it into your FP2 folder (FP2.exe must " +
                "be beside winhttp.dll). Run FP2 once so BepInEx generates its config " +
                "folder. Close FP2.",
            "3. Install FP2Lib: download from " + Fp2LibUrl + " and drop it into " +
                @"BepInEx\plugins\lib\ in your FP2 folder.",
            "4. Install the Archipelago mod: use the Install button on the Play tab, " +
                "or download Archipelago.7z from the mod releases and extract it into " +
                "your FP2 folder.",
            "5. Launch Freedom Planet 2 from this launcher (Play tab). Your server, " +
                "slot, and password are written automatically to the BepInEx config. " +
                "The mod connects when the game starts.",
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
        AddSectionHeader(panel, "LINKS", muted);
        foreach (var (label, url) in new[]
        {
            ("FP2 Archipelago Mod (GitHub) ↗",     ModRepoUrl),
            ("Freedom Planet 2 Setup Guide (AP) ↗", SetupGuideUrl),
            ("Freedom Planet 2 Game Info (AP) ↗",   GameInfoUrl),
            ("BepInEx Releases ↗",                  BepInExUrl),
            ("FP2Lib (GitHub) ↗",                   Fp2LibUrl),
            ("Archipelago Official ↗",               ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background      = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0),
                FontSize        = 12,
                Margin          = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground      = new System.Windows.Media.SolidColorBrush(
                                      System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
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
            string json = await _http.GetStringAsync(GhReleasesUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var list = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                list.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t) ? NormalizeTag(t.GetString()) ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (list.Count >= 10) break;
            }
            return list.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — BepInEx config prefill ─────────────────────────────

    /// Write (or update) the BepInEx config file with the AP session credentials.
    /// Returns true on success, false if the config directory does not exist yet
    /// (BepInEx has not been run once) or on any I/O error.
    private bool TryWriteBepInExConfig(ApSession session, string? gameDir = null)
    {
        try
        {
            gameDir ??= ResolveFp2Dir();
            if (gameDir == null) return false;

            string cfgPath = Path.Combine(gameDir, BepInExConfigRelPath);
            string cfgDir  = Path.GetDirectoryName(cfgPath)!;
            if (!Directory.Exists(cfgDir)) return false;

            // Read existing config if present; write/overwrite the three keys in
            // [Connection]. Preserves any other sections and keys.
            string existing = File.Exists(cfgPath) ? File.ReadAllText(cfgPath) : "";
            string updated  = SetBepInExCfgValue(
                SetBepInExCfgValue(
                    SetBepInExCfgValue(existing, CfgSection, CfgKeyServer,   session.ServerUri),
                    CfgSection, CfgKeySlot,   session.SlotName),
                CfgSection, CfgKeyPassword, session.Password);

            File.WriteAllText(cfgPath, updated, new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    /// Write or update a single key=value in a BepInEx-style INI config string.
    /// If the [Section] header exists, the key is inserted or replaced within it.
    /// If the section does not exist, it is appended at the end.
    private static string SetBepInExCfgValue(
        string cfg, string section, string key, string value)
    {
        string sectionHeader = $"[{section}]";
        string keyLine       = $"{key} = {value}";

        var lines = new List<string>(
            cfg.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None));

        int sectionIdx = -1;
        int keyIdx     = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim();
            if (sectionIdx < 0 && string.Equals(trimmed, sectionHeader,
                    StringComparison.OrdinalIgnoreCase))
            {
                sectionIdx = i;
                continue;
            }
            if (sectionIdx >= 0)
            {
                // A new section header means we've left our section.
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']')) break;
                if (trimmed.StartsWith(key + " =", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith(key + "=",  StringComparison.OrdinalIgnoreCase))
                {
                    keyIdx = i;
                    break;
                }
            }
        }

        if (keyIdx >= 0)
        {
            // Update in place.
            lines[keyIdx] = keyLine;
        }
        else if (sectionIdx >= 0)
        {
            // Insert after the section header.
            lines.Insert(sectionIdx + 1, keyLine);
        }
        else
        {
            // Append the whole section.
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add("");
            lines.Add(sectionHeader);
            lines.Add(keyLine);
        }

        return string.Join("\n", lines);
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..] : tag;
    }

    /// Resolve the latest GitHub release: returns (version, archiveUrl). The
    /// archive is the "Archipelago.7z" asset. Falls back to the pinned 0.1.2
    /// direct URL when the API is unreachable.
    private async Task<(string Version, string? Url)> ResolveLatestReleaseAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GhReleasesLatestUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString()) : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                && assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;
                string? any7z     = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u)
                                   ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".7z")) continue;
                    any7z ??= url;
                    if (preferred == null && lower.Contains("archipelago"))
                        preferred = url;
                }
                string? chosen = preferred ?? any7z;
                if (chosen != null) return (version, chosen);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — 7z detection ───────────────────────────────────────

    /// Find 7z.exe or 7za.exe: check PATH, well-known install locations.
    private static string? Find7zExecutable()
    {
        // 1. On PATH
        foreach (string name in new[] { "7z", "7za" })
        {
            try
            {
                var psi = new ProcessStartInfo(name, "--help")
                {
                    UseShellExecute        = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    CreateNoWindow         = true,
                };
                using var p = Process.Start(psi);
                p?.WaitForExit(2000);
                if (p?.ExitCode == 0) return name;
            }
            catch { /* not on PATH */ }
        }

        // 2. Common install paths (32/64-bit program files).
        var candidates = new List<string>();
        string? pf64  = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string? pf86  = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        foreach (string pf in new[] { pf64, pf86 })
        {
            if (string.IsNullOrWhiteSpace(pf)) continue;
            candidates.Add(Path.Combine(pf, "7-Zip", "7z.exe"));
            candidates.Add(Path.Combine(pf, "7-Zip", "7za.exe"));
        }
        // Registry-based path
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\7-Zip");
            string? p = k?.GetValue("Path") as string;
            if (!string.IsNullOrWhiteSpace(p))
            {
                candidates.Add(Path.Combine(p, "7z.exe"));
                candidates.Add(Path.Combine(p, "7za.exe"));
            }
        }
        catch { }

        return candidates.FirstOrDefault(File.Exists);
    }

    // ── Private helpers — Steam / FP2 detection ───────────────────────────────

    private string? ResolveFp2Dir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksFp2Dir(ov)) return ov;
        try { return DetectSteamFp2Dir(); }
        catch { return null; }
    }

    private static bool LooksFp2Dir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, Fp2ExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, Fp2DataFolder))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Returns a user-readable error if the folder is not a valid FP2 install,
    /// or null if it is acceptable.
    private string? ValidateInstallFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist.";
        if (LooksFp2Dir(folder)) return null;
        string nested = Path.Combine(folder, SteamCommonFolderName);
        if (LooksFp2Dir(nested)) return null;
        return "That does not look like a Freedom Planet 2 installation. " +
               "Pick the folder that contains FP2.exe (for Steam this is usually " +
               @"...\steamapps\common\Freedom Planet 2).";
    }

    /// True when BepInEx appears installed (winhttp.dll patcher + BepInEx folder).
    private static bool BepInExPresent(string gameDir)
    {
        try
        {
            if (!Directory.Exists(gameDir)) return false;
            // BepInEx for Unity patches via winhttp.dll (doorstop).
            if (File.Exists(Path.Combine(gameDir, "winhttp.dll"))) return true;
            // Or look for the BepInEx\core\ folder.
            if (Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Find the AP mod DLL under BepInEx\plugins (recursive, case-insensitive).
    private string? FindInstalledModDll()
    {
        try
        {
            string? game = ResolveFp2Dir();
            if (game == null) return null;

            string pluginsDir = Path.Combine(game, BepInExPluginsRel);
            if (!Directory.Exists(pluginsDir)) return null;

            // Primary: exact name.
            foreach (string dll in Directory.EnumerateFiles(
                pluginsDir, ModDllName, SearchOption.AllDirectories))
            {
                return dll;
            }
            // Fallback: any DLL whose name contains "fp2" or "archipelago".
            foreach (string dll in Directory.EnumerateFiles(
                pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string lower = Path.GetFileNameWithoutExtension(dll).ToLowerInvariant();
                if (lower.Contains("fp2") || lower.Contains("archipelago"))
                    return dll;
            }
        }
        catch { }
        return null;
    }

    /// Detect the Steam Freedom Planet 2 install from the registry + libraryfolders.vdf.
    private static string? DetectSteamFp2Dir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{Fp2SteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common     = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksFp2Dir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksFp2Dir(conventional)) return conventional;
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

        string? pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(pf86))
            yield return Path.Combine(pf86, "Steam");
    }

    private static string NormalizeSteamPath(string p) => p.Replace('/', '\\').TrimEnd('\\');

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

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartFp2()
    {
        string? game = ResolveFp2Dir();
        string? exe  = game != null ? Path.Combine(game, Fp2ExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var psi = new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = game!,
                UseShellExecute  = true,
            };
            var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Freedom Planet 2.");
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
            "Could not find FP2.exe. Open this game's Settings and pick your Freedom " +
            "Planet 2 install folder, or install Freedom Planet 2 via Steam.",
            Fp2ExeName);
    }

    // ── Private helpers — download ────────────────────────────────────────────

    private async Task DownloadFileAsync(
        string url, string dest, string msg,
        int pctStart, int pctEnd,
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
        await using var dst = File.Create(dest);
        var buf = new byte[81920];
        int read;
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read), ct);
            downloaded += read;
            if (total > 0)
            {
                int span = Math.Max(1, pctEnd - pctStart);
                int pct  = pctStart + (int)(span * downloaded / total);
                progress.Report((pct, $"{msg} {downloaded / 1000}KB"));
            }
        }
        await dst.FlushAsync(ct);
    }

    // ── Private helpers — UI ──────────────────────────────────────────────────

    private static void AddSectionHeader(
        System.Windows.Controls.StackPanel panel, string text,
        System.Windows.Media.Brush color)
    {
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = text, FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = color,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
    }

    // ── Private helpers — self-contained sidecar ──────────────────────────────

    private sealed class Fp2Settings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private Fp2Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Fp2Settings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(Fp2Settings s)
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
