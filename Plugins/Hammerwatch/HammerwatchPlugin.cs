using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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

namespace LauncherV2.Plugins.Hammerwatch;

// ═══════════════════════════════════════════════════════════════════════════════
// HammerwatchPlugin — install / update / launch for "Hammerwatch" (Crackshell)
// played through the HammerwatchAP mod by Parcosmic, which injects a full
// Archipelago client into the game via HarmonyX at SDL2 initialisation time.
// This is a NATIVE "ConnectsItself" integration: once the mod is installed, the
// game's own in-game "Archipelago" main-menu option handles the slot connection.
//
// REALITY CHECK (2026-06-14 — all facts verified against live repos/wiki) ─────
//
// AP GAME STRING — "Hammerwatch" (verified: worlds/hammerwatch/__init__.py on
//   the Parcosmic/Hammerwatch-Archipelago fork dev branch:
//   `class HammerwatchWorld(World): ... game = "Hammerwatch"`).
//   This world lives in a FORK of the AP repo (not AP-main) and is distributed
//   as "hammerwatch.apworld". Latest apworld release: v4.1 (2026-03-12, 4.2 MB).
//   The plugin downloads hammerwatch.apworld alongside the mod zip so the user
//   can drop it into Archipelago's custom_worlds folder.
//
// STEAM — Hammerwatch by Crackshell. Steam AppID: 239070 (verified:
//   store.steampowered.com/app/239070 — NOT 1905530 Anniversary Edition,
//   NOT 677120 Heroes of Hammerwatch, NOT 1538970 Hammerwatch II).
//
// MOD FRAMEWORK — HarmonyX / BepInEx-style runtime patching. The mod:
//   • Patches SDL2-CS.dll (via Mono.Cecil) to bootstrap HammerwatchAP.dll at
//     SDL2 static-constructor time on every game launch.
//   • Adds an "Archipelago" option to the game's main menu (via Harmony patch on
//     ARPGGame.Menus.MainMenu.LoadGUI).
//   • The mod is managed by `HammerwatchAPModInstaller.exe` shipped inside
//     HammerwatchAP-v*.zip from Parcosmic/HammerwatchAPMod/releases.
//
// CONNECTION — FULLY IN-GAME (ConnectsItself = true). Once the mod is installed
//   the player launches Hammerwatch normally (Steam / direct exe), selects
//   "Archipelago" on the main menu, enters the server address (host:port), slot
//   name, and password in the three-field dialogue, and presses OK. There is NO
//   documented connection config file or CLI argument that this launcher can
//   pre-write: the mod stores connection info inside its own save files (keys
//   "ap-ip", "ap-slot-name", "ap-password") and manages them through the
//   in-game GUI. Per the honest "don't invent an undocumented prefill" stance, the
//   launcher surfaces the session details in the Settings panel for the user to
//   type into the in-game dialogue.
//
// INSTALL AUTOMATION — PARTIALLY AUTOMATED. The release zip contains:
//   HammerwatchAPModInstaller.exe, HammerwatchAP.dll, archipelago-assets/, and
//   supporting DLLs. The installer is a WinForms console app that:
//     1. Auto-detects Hammerwatch via Steam registry (HKLM\...\Valve\Steam).
//     2. Copies mod files into the Hammerwatch game directory.
//     3. Patches SDL2-CS.dll using Mono.Cecil.
//     4. Shows an OpenFileDialog if the game directory cannot be auto-detected.
//   This plugin:
//     1. Downloads the zip into a temp directory.
//     2. Extracts it.
//     3. Attempts to resolve the Hammerwatch Steam install path from the registry
//        (same logic the installer uses). If found, runs the installer silently
//        (the Steam registry lookup will succeed, no file picker needed).
//     4. If not found, opens the extracted temp folder and surfaces numbered
//        steps to run the installer manually — honest, Hollow-Knight-style guidance.
//
// UNVERIFIED / DEFENSIVE DETAILS ──────────────────────────────────────────────
//   * Installer exe name inside the zip: assumed "HammerwatchAPModInstaller.exe"
//     (matches the setup guide). Falls back to any "*installer*" exe in the zip.
//   * The installer's auto-detect relies on the user having Hammerwatch in a
//     Steam library whose libraryfolders.vdf uses a "path" key — standard format.
//   * LaunchAsync: launches Hammerwatch.exe from the detected Steam path, or falls
//     back to "steam://rungameid/239070". No AP CLI args are passed (none documented).
//   * "Installed" check: looks for HammerwatchAP.dll in the detected Steam game dir.
//     If the game dir cannot be determined, falls back to true if a version stamp
//     exists (last successful install). Returns false if neither applies.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class HammerwatchPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    /// Mod release GitHub coordinates (HammerwatchAPMod — the client).
    private const string MOD_OWNER       = "Parcosmic";
    private const string MOD_REPO        = "HammerwatchAPMod";
    private const string ModRepoUrl      = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string ModReleasesApi  =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    /// apworld release GitHub coordinates (separate repo for the world generator).
    private const string APWORLD_OWNER  = "Parcosmic";
    private const string APWORLD_REPO   = "Hammerwatch-Archipelago";
    private const string ApWorldRepoUrl = $"https://github.com/{APWORLD_OWNER}/{APWORLD_REPO}";
    private const string ApWorldReleasesApi =
        $"https://api.github.com/repos/{APWORLD_OWNER}/{APWORLD_REPO}/releases";

    private const string SetupGuideUrl  =
        "https://github.com/Parcosmic/Hammerwatch-Archipelago/blob/dev/worlds/hammerwatch/docs/setup_en.md";
    private const string ArchipelagoSite = "https://archipelago.gg";

    /// Steam AppID for Hammerwatch (Crackshell, 2013). VERIFIED: 239070.
    private const string SteamAppId = "239070";

    /// The DLL the mod installs in the game folder — used as the "installed" marker.
    private const string ModDllName = "HammerwatchAP.dll";

    /// Version stamp written after a successful install.
    private const string VersionFileName = "hammerwatch_mod_version.dat";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "hammerwatch";
    public string DisplayName => "Hammerwatch";
    public string Subtitle    => "Native PC · HammerwatchAP mod";

    /// EXACT AP game string — verified: worlds/hammerwatch/__init__.py → game = "Hammerwatch"
    public string ApWorldName => "Hammerwatch";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "hammerwatch.png");

    /// Hammerwatch's stone dungeon palette — dark slate with warm amber torchlight.
    public string ThemeAccentColor => "#C8A028";

    public string[] GameBadges => new[]
    {
        "Steam Required",
        "AP mod auto-install",
        "ConnectsItself",
    };

    public string Description =>
        "Hammerwatch (Crackshell, 2013) is a top-down hack-and-slash dungeon crawler. " +
        "Castle Hammerwatch and Temple of the Sun are fully randomized: keys, items, " +
        "shops, bosses, and more are shuffled into the Archipelago multiworld. " +
        "The HammerwatchAP mod adds an \"Archipelago\" option to the game's main menu " +
        "where you enter your server, slot name, and password — no external client " +
        "needed. The launcher downloads and installs the mod automatically when " +
        "Hammerwatch is found via Steam.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version / install state ───────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// True when HammerwatchAP.dll is present in the detected game directory, OR
    /// when the Steam game dir cannot be determined but we have a version stamp.
    public bool IsInstalled
    {
        get
        {
            string? dir = FindSteamGameDirectory();
            if (dir != null)
                return File.Exists(Path.Combine(dir, ModDllName));
            // Fallback: trust our own version stamp when the Steam dir is unknown.
            return File.Exists(VersionFilePath) && !string.IsNullOrWhiteSpace(InstalledVersion);
        }
    }

    public bool IsRunning { get; private set; }

    /// ConnectsItself = true — the mod's in-game client handles the slot connection.
    public bool ConnectsItself => true;

    /// SupportsStandalone = true — the mod patches the game non-destructively; the
    /// "Archipelago" menu item is simply unused in a non-AP session.
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Launcher-managed folder for downloads and sidecar data.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Hammerwatch");

    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", "hammerwatch");

    private string VersionFilePath
        => Path.Combine(RomLibraryDirectory, VersionFileName);

    /// Sidecar for this plugin's own launcher settings.
    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "hammerwatch_launcher.json");

    /// apworld file saved next to the launcher download (user copies to AP custom_worlds).
    private string ApWorldLocalPath
        => Path.Combine(GameDirectory, "hammerwatch.apworld");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events — inert (ConnectsItself = true) ─────────────────────
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067
    public event Action<int>? GameExited;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Installed version from our own stamp.
        try
        {
            InstalledVersion = File.Exists(VersionFilePath)
                ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim()
                : null;
        }
        catch { InstalledVersion = null; }

        // Latest available from GitHub releases.
        try
        {
            var (version, _, _) = await ResolveLatestModReleaseAsync(ct);
            AvailableVersion = version;
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((2, "Checking latest HammerwatchAP mod release..."));
        var (version, zipUrl, apworldUrl) = await ResolveLatestModReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a Windows download for HammerwatchAP on the GitHub " +
                "release page. Check your internet connection or download manually " +
                "from " + ModRepoUrl + "/releases");

        // Fast path — already current?
        if (IsInstalled && File.Exists(VersionFilePath))
        {
            try
            {
                string stamped = (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim();
                if (stamped == version)
                {
                    InstalledVersion = version;
                    progress.Report((100, $"HammerwatchAP {version} is already installed and up to date."));
                    return;
                }
            }
            catch { /* fall through and reinstall */ }
        }

        // 1. Resolve Steam game directory.
        progress.Report((5, "Locating Hammerwatch Steam installation..."));
        string? steamDir = FindSteamGameDirectory();
        if (steamDir == null)
        {
            progress.Report((5, "Hammerwatch not found via Steam registry — guided install."));
        }
        else
        {
            progress.Report((8, $"Found Hammerwatch at: {steamDir}"));
        }

        // 2. Download the mod zip.
        progress.Report((10, $"Downloading HammerwatchAP {version}..."));
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"hammerwatchap-{version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string tempZip = Path.Combine(tempDir, "HammerwatchAP.zip");

        try
        {
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
                        int pct = (int)(10 + 40 * downloaded / total);
                        progress.Report((pct, $"Downloading... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            // 3. Extract the zip.
            progress.Report((52, "Extracting mod files..."));
            string extractDir = Path.Combine(tempDir, "extracted");
            ZipFile.ExtractToDirectory(tempZip, extractDir, overwriteFiles: true);

            // Flatten a single wrapper subfolder if the zip uses one.
            string modSourceDir = FlattenSingleSubdir(extractDir);

            progress.Report((58, "Mod files extracted."));

            // 4. Run or guide installation.
            if (steamDir != null)
            {
                progress.Report((60, "Running HammerwatchAP installer..."));
                string? installerExe = FindInstallerExe(modSourceDir);
                if (installerExe != null)
                {
                    await RunInstallerAsync(installerExe, modSourceDir, ct, progress);
                }
                else
                {
                    // Installer exe not found — fall back to manual copy.
                    progress.Report((60, "Installer exe not found — falling back to direct file copy..."));
                    CopyModFilesDirectly(modSourceDir, steamDir, progress);
                }
            }
            else
            {
                // Cannot auto-install — surface numbered steps.
                progress.Report((62,
                    "Hammerwatch not found via Steam registry. " +
                    "The mod files are in: " + modSourceDir + " — " +
                    "run HammerwatchAPModInstaller.exe there and " +
                    "select your Hammerwatch.exe when prompted."));
            }

            // 5. Download the apworld alongside.
            if (apworldUrl != null)
            {
                try
                {
                    progress.Report((85, "Downloading hammerwatch.apworld..."));
                    Directory.CreateDirectory(GameDirectory);
                    byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                    await File.WriteAllBytesAsync(ApWorldLocalPath, apworld, ct);
                    progress.Report((92,
                        "hammerwatch.apworld saved — copy it into your " +
                        @"Archipelago custom_worlds folder (e.g. C:\ProgramData\Archipelago\custom_worlds)."));
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                    progress.Report((92,
                        "Could not download the apworld — get it from " +
                        ApWorldRepoUrl + "/releases"));
                }
            }

            // 6. Stamp version.
            Directory.CreateDirectory(RomLibraryDirectory);
            await File.WriteAllTextAsync(VersionFilePath, version, ct);
            InstalledVersion = version;

            progress.Report((100,
                $"HammerwatchAP {version} installed. " +
                "Launch Hammerwatch, select \"Archipelago\" on the main menu, " +
                "and enter your server address, slot name, and password."));
        }
        finally
        {
            // Clean up temp dir (best effort).
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
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
        // No documented CLI args exist for pre-filling the in-game AP dialogue.
        // The player connects via the game's own "Archipelago" main-menu option
        // (server:port, slot name, password). We simply launch the game.
        StartGame();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert ─────────────────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The mod's in-game client receives items directly from the AP server.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game; no launcher HUD channel.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var amber   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC8, 0xA0, 0x28));
        var success = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warning = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xB7, 0x40));
        var panel   = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20),
        };

        // ── Install status ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "INSTALLATION STATUS",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? steamDir   = FindSteamGameDirectory();
        bool    hasMod     = IsInstalled;
        bool    hasSteam   = steamDir != null;

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = hasSteam
                ? $"Hammerwatch found at:\n{steamDir}"
                : "Hammerwatch not found via Steam registry. " +
                  "Make sure Hammerwatch (AppID 239070) is installed in Steam.",
            FontSize    = 11,
            Foreground  = hasSteam ? fg : warning,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = hasMod
                ? $"HammerwatchAP mod installed  (version stamp: {InstalledVersion ?? "unknown"})"
                : "HammerwatchAP mod not installed — click Install in the Play tab.",
            FontSize   = 11,
            Foreground = hasMod ? success : warning,
            Margin     = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Connection instructions ───────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "HOW TO CONNECT",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });

        var steps = new[]
        {
            "1. Launch Hammerwatch (click Play above).",
            "2. On the main menu select \"Archipelago\".",
            "3. In the dialogue box enter:",
            "      Server address  (e.g. archipelago.gg:38281)",
            "      Slot name",
            "      Password (leave blank if none)",
            "4. Click OK — the status at the bottom of the menu confirms the connection.",
            "5. Select \"Single\" to start a new game, or \"Resume\" to load an existing save.",
        };
        foreach (string step in steps)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text         = step,
                FontSize     = 11,
                Foreground   = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin       = new System.Windows.Thickness(0, 1, 0, 1),
            });
        }

        // ── apworld note ──────────────────────────────────────────────────
        if (File.Exists(ApWorldLocalPath))
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "\nhammerwatch.apworld is saved at:\n" + ApWorldLocalPath +
                       "\nCopy it into your Archipelago custom_worlds folder " +
                       @"(default: C:\ProgramData\Archipelago\custom_worlds) " +
                       "to generate Hammerwatch multiworld games.",
                FontSize     = 11,
                Foreground   = muted,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin       = new System.Windows.Thickness(0, 12, 0, 12),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "LINKS",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 12, 0, 8),
        });

        foreach (var (label, url) in new[]
        {
            ("Hammerwatch on Steam ↗",        $"https://store.steampowered.com/app/{SteamAppId}"),
            ("HammerwatchAP mod (GitHub) ↗",  ModRepoUrl),
            ("AP apworld releases ↗",          ApWorldRepoUrl + "/releases"),
            ("Setup Guide ↗",                  SetupGuideUrl),
            ("Archipelago Official ↗",         ArchipelagoSite),
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
                Foreground          = new SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(u) { UseShellExecute = true });
                }
                catch { }
            };
            panel.Children.Add(btn);
        }

        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Pull from both repos and merge by date (newest first).
        var items = new List<NewsItem>();
        try
        {
            string json = await _http.GetStringAsync(ModReleasesApi, ct);
            items.AddRange(ParseReleaseNews(json, "HammerwatchAP mod"));
        }
        catch { }
        try
        {
            string json = await _http.GetStringAsync(ApWorldReleasesApi, ct);
            items.AddRange(ParseReleaseNews(json, "apworld"));
        }
        catch { }
        items.Sort((a, b) => DateTimeOffset.Compare(b.Date, a.Date));
        return items.Count > 10 ? items.GetRange(0, 10).ToArray() : items.ToArray();
    }

    // ── Private helpers — Steam detection ─────────────────────────────────────

    /// Finds the Hammerwatch Steam install directory using the same registry +
    /// libraryfolders.vdf logic as the official HammerwatchAPModInstaller.
    /// Returns null when the game directory cannot be determined.
    private static string? FindSteamGameDirectory()
    {
        try
        {
            // Try HKLM 32-bit WOW node first (matches the installer's own lookup).
            string? steamPath = ReadSteamInstallPath();
            if (steamPath == null) return null;

            var libraryPaths = ParseSteamLibraryFolders(steamPath);

            foreach (string libPath in libraryPaths)
            {
                foreach (string stem in new[] { "steam", "steamapps" })
                {
                    string candidate = Path.Combine(libPath, stem, "common", "Hammerwatch");
                    if (File.Exists(Path.Combine(candidate, "Hammerwatch.exe")))
                        return candidate;
                }
                // Standard path (most libraries use steamapps\common directly).
                string standard = Path.Combine(libPath, "steamapps", "common", "Hammerwatch");
                if (File.Exists(Path.Combine(standard, "Hammerwatch.exe")))
                    return standard;
            }
        }
        catch { /* Steam not installed or registry inaccessible */ }
        return null;
    }

    private static string? ReadSteamInstallPath()
    {
        // Try the same key the installer uses, then the user-hive fallback.
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Wow6432Node\Valve\Steam") ??
            Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        return key?.GetValue("InstallPath") as string;
    }

    /// Tolerant VDF parser — extracts "path" values from steamapps\libraryfolders.vdf.
    private static List<string> ParseSteamLibraryFolders(string steamPath)
    {
        var paths = new List<string>();
        // The steam install itself is the first library.
        paths.Add(steamPath);

        string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) return paths;

        try
        {
            foreach (string line in File.ReadAllLines(vdf))
            {
                // Each library entry contains a line like:  "path"  "D:\\Steam"
                int pi = line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase);
                if (pi < 0) continue;

                string rest = line[(pi + 6)..].Trim();
                if (rest.Length < 2 || rest[0] != '"') continue;
                rest = rest[1..];
                int end = rest.IndexOf('"');
                if (end < 0) continue;
                string p = rest[..end].Replace("\\\\", "\\");
                if (!string.IsNullOrWhiteSpace(p) && !paths.Contains(p))
                    paths.Add(p);
            }
        }
        catch { /* tolerate bad VDF */ }
        return paths;
    }

    // ── Private helpers — installer execution ─────────────────────────────────

    private static string? FindInstallerExe(string dir)
    {
        if (!Directory.Exists(dir)) return null;

        // Preferred name (matches documentation and release contents).
        string preferred = Path.Combine(dir, "HammerwatchAPModInstaller.exe");
        if (File.Exists(preferred)) return preferred;

        // Fuzzy fallback — any *installer* exe in the directory.
        try
        {
            foreach (string f in Directory.EnumerateFiles(dir, "*.exe"))
            {
                string name = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                if (name.Contains("installer")) return f;
            }
        }
        catch { }
        return null;
    }

    private static async Task RunInstallerAsync(
        string installerExe,
        string workingDir,
        CancellationToken ct,
        IProgress<(int Pct, string Msg)> progress)
    {
        // Run the installer with its working directory set to the extracted zip
        // folder (the installer copies files from CWD to the Hammerwatch dir).
        // The installer uses the Steam registry to auto-detect Hammerwatch —
        // no interactive file picker is needed.
        var psi = new ProcessStartInfo
        {
            FileName         = installerExe,
            WorkingDirectory = workingDir,
            UseShellExecute  = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        progress.Report((65, "Running HammerwatchAPModInstaller.exe..."));
        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException(
                "Could not start HammerwatchAPModInstaller.exe");

        // Drain stdout/stderr to prevent deadlock.
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        string stdout = await stdoutTask;
        string stderr = await stderrTask;

        string output = (stdout + "\n" + stderr).Trim();
        progress.Report((80,
            proc.ExitCode == 0
                ? "Installer completed. " + (output.Contains("successful", StringComparison.OrdinalIgnoreCase)
                    ? "Patching successful!"
                    : "Mod files installed.")
                : $"Installer exited with code {proc.ExitCode}. " +
                  "If Hammerwatch was not found automatically, run HammerwatchAPModInstaller.exe " +
                  "manually and select your Hammerwatch.exe."));
    }

    /// Direct file copy fallback when the installer exe is not found in the zip.
    /// Copies HammerwatchAP.dll and archipelago-assets/ to the game directory.
    private static void CopyModFilesDirectly(
        string srcDir,
        string destDir,
        IProgress<(int Pct, string Msg)> progress)
    {
        progress.Report((65, "Copying mod files to game directory..."));
        try
        {
            // Copy HammerwatchAP.dll.
            string dllSrc = Path.Combine(srcDir, ModDllName);
            if (File.Exists(dllSrc))
                File.Copy(dllSrc, Path.Combine(destDir, ModDllName), overwrite: true);

            // Copy archipelago-assets directory recursively.
            string assetsSrc = Path.Combine(srcDir, "archipelago-assets");
            if (Directory.Exists(assetsSrc))
                CopyDirectory(assetsSrc, Path.Combine(destDir, "archipelago-assets"));

            // Copy any other DLLs at the root (HarmonyX libs, etc.).
            foreach (string dll in Directory.EnumerateFiles(srcDir, "*.dll"))
            {
                string name = Path.GetFileName(dll);
                if (name.Equals(ModDllName, StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(dll, Path.Combine(destDir, name), overwrite: true);
            }

            progress.Report((80, "Mod files copied. " +
                "NOTE: SDL2-CS.dll patching requires the official installer — " +
                "run HammerwatchAPModInstaller.exe manually if the mod does not load."));
        }
        catch (Exception ex)
        {
            progress.Report((80, $"File copy failed: {ex.Message} — " +
                "run HammerwatchAPModInstaller.exe manually."));
        }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string f in Directory.EnumerateFiles(src))
            File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: true);
        foreach (string d in Directory.EnumerateDirectories(src))
            CopyDirectory(d, Path.Combine(dest, Path.GetFileName(d)));
    }

    // ── Private helpers — zip handling ────────────────────────────────────────

    /// If the extract directory contains only one subdirectory and no files,
    /// return that subdirectory (flatten the wrapper folder). Otherwise return dir.
    private static string FlattenSingleSubdir(string dir)
    {
        try
        {
            if (Directory.GetFiles(dir).Length == 0)
            {
                string[] subs = Directory.GetDirectories(dir);
                if (subs.Length == 1) return subs[0];
            }
        }
        catch { }
        return dir;
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// Resolves the latest HammerwatchAPMod release: version, Windows zip URL,
    /// and apworld URL from the separate apworld repo (best effort).
    private async Task<(string Version, string? ZipUrl, string? ApWorldUrl)>
        ResolveLatestModReleaseAsync(CancellationToken ct)
    {
        string json = await _http.GetStringAsync(ModReleasesApi, ct);
        using var doc = JsonDocument.Parse(json);

        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var dr) && dr.ValueKind == JsonValueKind.True)
                continue;

            string? tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (tag == null) continue;
            string version = NormalizeTag(tag);

            if (!rel.TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Array)
                continue;

            string? zipUrl = null;
            foreach (var a in assets.EnumerateArray())
            {
                string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name == null || url == null) continue;

                string lower = name.ToLowerInvariant();
                if (lower.EndsWith(".zip") && lower.Contains("hammerwatch"))
                {
                    zipUrl = url;
                    break;
                }
            }

            if (zipUrl == null) continue; // try next release

            // Best-effort apworld from the apworld repo (separate fetch).
            string? apworldUrl = await TryResolveApWorldUrlAsync(ct);
            return (version, zipUrl, apworldUrl);
        }

        throw new InvalidOperationException("No valid HammerwatchAP release found.");
    }

    private async Task<string?> TryResolveApWorldUrlAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(ApWorldReleasesApi, ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft", out var dr) && dr.ValueKind == JsonValueKind.True)
                    continue;
                if (!rel.TryGetProperty("assets", out var assets) ||
                    assets.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    if (name.ToLowerInvariant().EndsWith(".apworld"))
                        return url;
                }
                // Take first non-draft release that has assets.
                break;
            }
        }
        catch { /* non-fatal */ }
        return null;
    }

    private static string NormalizeTag(string tag)
    {
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..] : tag;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        // Prefer launching the actual exe (avoids Steam overlay delays).
        // Fall back to steam:// URI if the exe path is unknown.
        string? steamDir = FindSteamGameDirectory();
        string? exePath  = steamDir != null
            ? Path.Combine(steamDir, "Hammerwatch.exe")
            : null;

        ProcessStartInfo psi;
        if (exePath != null && File.Exists(exePath))
        {
            psi = new ProcessStartInfo
            {
                FileName         = exePath,
                WorkingDirectory = steamDir!,
                UseShellExecute  = false,
            };
        }
        else
        {
            // Steam protocol fallback.
            psi = new ProcessStartInfo
            {
                FileName        = $"steam://rungameid/{SteamAppId}",
                UseShellExecute = true,
            };
        }

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException(
                "Failed to launch Hammerwatch. Make sure it is installed via Steam.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — news ────────────────────────────────────────────────

    private static List<NewsItem> ParseReleaseNews(string json, string source)
    {
        var items = new List<NewsItem>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                string name = el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "";
                string tag  = el.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                string body = el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "";
                string? url = el.TryGetProperty("html_url", out var u) ? u.GetString() : null;

                items.Add(new NewsItem(
                    Title:   string.IsNullOrEmpty(name) ? $"{source} {tag}" : name,
                    Body:    body,
                    Version: NormalizeTag(tag),
                    Date:    date,
                    Url:     url
                ));
                if (items.Count >= 5) break;
            }
        }
        catch { }
        return items;
    }
}
