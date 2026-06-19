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
using Microsoft.Win32;
using LauncherV2.Core;

namespace LauncherV2.Plugins.NineSols;

// ═══════════════════════════════════════════════════════════════════════════════
// NineSolsPlugin — install / update / launch for "Nine Sols" (Red Candle Games)
// played through the NineSolsArchipelagoRandomizer BepInEx 5 mod by Ixrec, which
// adds a full in-game Archipelago client (menu-driven connection UI). This is a
// NATIVE "ConnectsItself" integration: once the mod is installed the game connects
// to the multiworld itself — no external client process, no launcher relay.
//
// REALITY CHECK (2026-06-14 — all facts verified) ────────────────────────────
//
// AP GAME STRING — "Nine Sols" (verified: Ixrec/NineSolsArchipelagoRandomizer,
//   the world's __init__.py / game field is "Nine Sols").
//
// STEAM — Nine Sols by Red Candle Games. Steam AppID: 1809540 (verified:
//   store.steampowered.com/app/1809540/Nine_Sols/).
//
// MOD FRAMEWORK — BepInEx 5. The mod is a single BepInEx plugin
//   (NineSolsArchipelago.dll) that the user drops into the game's BepInEx
//   plugins folder. BepInEx 5 itself must be installed first; its canonical
//   Windows/x64 release is 5.4.23.2 from BepInEx/BepInEx/releases.
//
// CONNECTION — FULLY IN-GAME (ConnectsItself = true). After the mod is loaded
//   the game shows an Archipelago section in its own menu where the player enters
//   the server address, slot name, and password. There is NO command-line
//   argument or config file this launcher can pre-write to seed the connection —
//   the launcher surfaces the session credentials in the Settings panel so the
//   user can type them into the in-game dialogue.
//
// INSTALL AUTOMATION — FULLY AUTOMATED. This plugin:
//   1. Downloads BepInEx 5.4.23.2 if BepInEx is not yet present in the game dir.
//   2. Downloads the latest NineSolsArchipelago release zip from
//      Ixrec/NineSolsArchipelagoRandomizer/releases.
//   3. Extracts the zip and copies the BepInEx/ folder structure into the Nine
//      Sols game directory (handles the common single-wrapper-folder pattern).
//   4. Stamps the installed version.
//
// "INSTALLED" CHECK — BepInEx\plugins\NineSolsArchipelago.dll present in the
//   detected Steam game directory. Falls back to the version stamp when the Steam
//   directory cannot be determined.
//
// UNVERIFIED / DEFENSIVE DETAILS ─────────────────────────────────────────────
//   * BepInEx package name inside the release zip: assumed to follow the standard
//     BepInEx 5 layout (BepInEx/ at the top of the zip), matching the 5.4.23.2
//     release on GitHub. The plugin looks for BepInEx\core\BepInEx.dll.
//   * The NineSolsArchipelago release zip is expected to contain a BepInEx/
//     folder hierarchy. If the zip root is a single-folder wrapper, it is
//     flattened before copying. On unexpected layouts the plugin copies all *.dll
//     files it can find into BepInEx\plugins\ as a fallback.
//   * LaunchAsync: launches NineSols.exe from the detected Steam dir, or falls
//     back to steam://rungameid/1809540.
//   * The exact in-game menu name used by the mod is derived from published
//     screenshots and the mod README; it may differ slightly in future releases.
//
// BUILD NOTE: UseWindowsForms=true is set project-wide. All WPF types that also
//   exist in WinForms are spelled with FULL namespaces (System.Windows.Controls.*,
//   System.Windows.Media.*, …) to avoid CS0104 ambiguity. No file-level using
//   aliases are added (would trigger CS1537 against GlobalUsings.cs).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class NineSolsPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    /// Mod release GitHub coordinates.
    private const string MOD_OWNER      = "Ixrec";
    private const string MOD_REPO       = "NineSolsArchipelagoRandomizer";
    private const string ModRepoUrl     = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string ModReleasesApi =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    /// BepInEx 5 — required mod loader. 5.4.23.2 is the latest stable Windows x64.
    private const string BEPINEX_OWNER      = "BepInEx";
    private const string BEPINEX_REPO       = "BepInEx";
    private const string BepInExReleasesApi =
        $"https://api.github.com/repos/{BEPINEX_OWNER}/{BEPINEX_REPO}/releases";
    private const string BepInExSite        =
        $"https://github.com/{BEPINEX_OWNER}/{BEPINEX_REPO}/releases";

    /// Pinned BepInEx 5.4.23.2 download (Windows x64). Used as fallback when the
    /// GitHub releases API is unreachable. URL shape is stable across patch releases.
    private const string BepInExFallbackVersion = "5.4.23.2";
    private const string BepInExFallbackZipUrl  =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/" +
        "BepInEx_win_x64_5.4.23.2.zip";

    private const string SetupGuideUrl  = $"https://github.com/{MOD_OWNER}/{MOD_REPO}#readme";
    private const string ArchipelagoSite = "https://archipelago.gg";

    /// Steam AppID for Nine Sols. VERIFIED: 1809540.
    private const string SteamAppId = "1809540";

    /// Steam common subfolder name for the game.
    private const string SteamCommonName = "Nine Sols";

    /// The game's main executable filename.
    private const string GameExeName = "NineSols.exe";

    /// The BepInEx plugin DLL installed by the mod — used as the "installed" marker.
    private const string ModPluginDllName = "NineSolsArchipelago.dll";

    /// Version stamp written after a successful mod install.
    private const string VersionFileName = "nine_sols_launcher.json";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "nine_sols";
    public string DisplayName => "Nine Sols";
    public string Subtitle    => "Native PC · BepInEx AP mod";

    /// EXACT AP game string — verified: game = "Nine Sols"
    public string ApWorldName => "Nine Sols";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "ninesols.png");

    /// Nine Sols' Taopunk crimson aesthetic — deep crimson/blood red.
    public string ThemeAccentColor => "#C62828";

    public string[] GameBadges => new[]
    {
        "Steam Required",
        "BepInEx mod auto-install",
        "ConnectsItself",
    };

    public string Description =>
        "Nine Sols (Red Candle Games, 2024) is a Tao-punk sci-fantasy action platformer " +
        "set in New Kunlun, a forsaken realm of immortal beings. The NineSolsArchipelago " +
        "mod by Ixrec adds a full in-game Archipelago client powered by BepInEx 5 — the " +
        "game connects directly to the multiworld server via an in-game menu, with no " +
        "external client process needed. The launcher installs BepInEx and the mod " +
        "automatically when Nine Sols is found via Steam, then you connect in-game.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version / install state ───────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// True when NineSolsArchipelago.dll is present in the detected game directory's
    /// BepInEx\plugins folder, OR when the Steam dir is unknown but a version stamp
    /// exists from a previous successful install.
    public bool IsInstalled
    {
        get
        {
            string? dir = FindSteamGameDirectory();
            if (dir != null)
                return File.Exists(Path.Combine(dir, "BepInEx", "plugins", ModPluginDllName));
            // Fallback: trust our stamp when the Steam dir is unknown.
            return File.Exists(VersionFilePath) && !string.IsNullOrWhiteSpace(InstalledVersion);
        }
    }

    public bool IsRunning { get; private set; }

    /// ConnectsItself = true — the BepInEx mod handles the AP slot connection in-game.
    public bool ConnectsItself => true;

    /// SupportsStandalone = true — the mod is non-destructive; the AP menu is simply
    /// unused when not connecting to a server.
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Launcher-managed folder for downloads and sidecar data.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "NineSols");

    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", "nine_sols");

    private string VersionFilePath
        => Path.Combine(RomLibraryDirectory, VersionFileName);

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
        // 1. Resolve the latest mod release.
        progress.Report((2, "Checking latest NineSolsArchipelago release..."));
        var (version, modZipUrl, _) = await ResolveLatestModReleaseAsync(ct);
        AvailableVersion = version;

        if (modZipUrl == null)
            throw new InvalidOperationException(
                "Could not find a download for NineSolsArchipelago on the GitHub " +
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
                    progress.Report((100,
                        $"NineSolsArchipelago {version} is already installed and up to date."));
                    return;
                }
            }
            catch { /* fall through and reinstall */ }
        }

        // 2. Locate the Nine Sols Steam installation.
        progress.Report((5, "Locating Nine Sols Steam installation..."));
        string? steamDir = FindSteamGameDirectory();
        if (steamDir == null)
        {
            throw new InvalidOperationException(
                "Could not find a Nine Sols installation via Steam. " +
                "Make sure Nine Sols (AppID 1809540) is installed in Steam, " +
                "then try again.");
        }
        progress.Report((8, $"Found Nine Sols at: {steamDir}"));

        // 3. Install BepInEx 5 if not already present.
        string bepInExCore = Path.Combine(steamDir, "BepInEx", "core", "BepInEx.dll");
        if (!File.Exists(bepInExCore))
        {
            progress.Report((10, "BepInEx 5 not found — downloading and installing..."));
            await InstallBepInExAsync(steamDir, progress, ct);
        }
        else
        {
            progress.Report((10, "BepInEx 5 already installed."));
        }

        // 4. Download the mod zip.
        progress.Report((30, $"Downloading NineSolsArchipelago {version}..."));
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"ninesols-ap-{version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string tempZip = Path.Combine(tempDir, "NineSolsArchipelago.zip");

        try
        {
            using (var response = await _http.GetAsync(
                modZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
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
                        int pct = (int)(30 + 35 * downloaded / total);
                        progress.Report((pct, $"Downloading mod... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            // 5. Extract and copy the mod into the BepInEx plugins folder.
            progress.Report((68, "Extracting mod files..."));
            string extractDir = Path.Combine(tempDir, "extracted");
            ZipFile.ExtractToDirectory(tempZip, extractDir, overwriteFiles: true);

            // Flatten a single wrapper subfolder if the zip uses one.
            string modSourceDir = FlattenSingleSubdir(extractDir);

            progress.Report((75, "Installing mod files into Nine Sols BepInEx folder..."));
            CopyBepInExStructure(modSourceDir, steamDir, progress);

            // 6. Stamp version.
            Directory.CreateDirectory(RomLibraryDirectory);
            await File.WriteAllTextAsync(VersionFilePath, version, ct);
            InstalledVersion = version;

            progress.Report((100,
                $"NineSolsArchipelago {version} installed successfully. " +
                "Launch Nine Sols, open the Archipelago section in the in-game menu, " +
                "and enter your server address, slot name, and password."));
        }
        finally
        {
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
        // No documented CLI args exist for pre-filling the in-game AP connection.
        // The player enters server/slot/password in the game's own Archipelago menu.
        // ConnectsItself = true: do NOT hold a launcher-side ApClient on this slot.
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
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var crimson = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xBB, 0x20, 0x20));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warning = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xB7, 0x40));

        var panel = new System.Windows.Controls.StackPanel
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
        bool    bepInExOk  = steamDir != null &&
                             File.Exists(Path.Combine(steamDir, "BepInEx", "core", "BepInEx.dll"));
        bool    hasMod     = IsInstalled;

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = steamDir != null
                ? $"Nine Sols found at:\n{steamDir}"
                : "Nine Sols not found via Steam registry. " +
                  "Make sure Nine Sols (AppID 1809540) is installed in Steam.",
            FontSize     = 11,
            Foreground   = steamDir != null ? fg : warning,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = bepInExOk
                ? "BepInEx 5 is installed."
                : steamDir != null
                    ? "BepInEx 5 not found — click Install on the Play tab (installs automatically)."
                    : "BepInEx 5 status unknown (Nine Sols not detected).",
            FontSize   = 11,
            Foreground = bepInExOk ? success : warning,
            Margin     = new System.Windows.Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = hasMod
                ? $"NineSolsArchipelago mod installed  (version stamp: {InstalledVersion ?? "unknown"})"
                : "NineSolsArchipelago mod not installed — click Install on the Play tab.",
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
            "1. Launch Nine Sols (click Play above).",
            "2. Open the Archipelago section in the in-game menu.",
            "3. Enter your connection details:",
            "      Server address  (e.g. archipelago.gg:38281)",
            "      Slot name",
            "      Password  (leave blank if none)",
            "4. Confirm to connect — the game will then join the multiworld.",
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

        // ── BepInEx note ──────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "\nNOTE: The Install button handles BepInEx 5 and the mod in one step. " +
                   "If you need to reinstall BepInEx manually, download the Windows x64 zip " +
                   "from " + BepInExSite + " and extract it into your Nine Sols folder.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 12, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "LINKS",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 4, 0, 8),
        });

        foreach (var (label, url) in new[]
        {
            ("Nine Sols on Steam ↗",                   $"https://store.steampowered.com/app/{SteamAppId}"),
            ("NineSolsArchipelago mod (GitHub) ↗",     ModRepoUrl),
            ("Mod releases ↗",                         ModRepoUrl + "/releases"),
            ("BepInEx releases ↗",                     BepInExSite),
            ("Archipelago Official ↗",                 ArchipelagoSite),
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
            string json = await _http.GetStringAsync(ModReleasesApi, ct);
            return ParseReleaseNews(json).ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — Steam detection ─────────────────────────────────────

    /// Finds the Nine Sols Steam install directory. Returns null when not found.
    private static string? FindSteamGameDirectory()
    {
        try
        {
            foreach (string steamRoot in SteamRoots())
            {
                if (string.IsNullOrWhiteSpace(steamRoot)) continue;
                foreach (string lib in SteamLibraryRoots(steamRoot))
                {
                    try
                    {
                        string steamapps = Path.Combine(lib, "steamapps");

                        // Try appmanifest approach first for accuracy.
                        string manifest = Path.Combine(steamapps, $"appmanifest_{SteamAppId}.acf");
                        if (File.Exists(manifest))
                        {
                            string common = Path.Combine(steamapps, "common");
                            string? installDir = ReadAcfInstallDir(manifest);
                            if (installDir != null)
                            {
                                string candidate = Path.Combine(common, installDir);
                                if (File.Exists(Path.Combine(candidate, GameExeName)))
                                    return candidate;
                            }
                            string conventional = Path.Combine(common, SteamCommonName);
                            if (File.Exists(Path.Combine(conventional, GameExeName)))
                                return conventional;
                        }

                        // Conventional folder name fallback.
                        string fallback = Path.Combine(steamapps, "common", SteamCommonName);
                        if (File.Exists(Path.Combine(fallback, GameExeName)))
                            return fallback;
                    }
                    catch { /* try next library */ }
                }
            }
        }
        catch { }
        return null;
    }

    /// Candidate Steam install roots from the registry and conventional paths.
    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu))
            yield return hkcu.Replace('/', '\\').TrimEnd('\\');

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm))
            yield return hklm.Replace('/', '\\').TrimEnd('\\');

        string? hklm2 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm2))
            yield return hklm2.Replace('/', '\\').TrimEnd('\\');

        string? pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(pf86))
            yield return Path.Combine(pf86, "Steam");
    }

    /// All Steam library roots: the root itself plus every "path" entry in
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

        const string key = "\"path\"";
        int i = 0;
        while ((i = text.IndexOf(key, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            i += key.Length;
            int open = text.IndexOf('"', i);
            if (open < 0) yield break;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) yield break;
            string raw  = text.Substring(open + 1, close - open - 1)
                              .Replace("\\\\", "\\").Replace('/', '\\').TrimEnd('\\');
            if (!string.IsNullOrWhiteSpace(raw) && seen.Add(raw))
                yield return raw;
            i = close + 1;
        }
    }

    /// Read "installdir" value from an appmanifest_*.acf file.
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

    /// Safe registry string read.
    private static string? ReadRegistryString(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(subKey);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    // ── Private helpers — BepInEx installation ────────────────────────────────

    /// Downloads BepInEx 5 (Windows x64) and extracts it into the game directory.
    /// The standard BepInEx 5 zip has a BepInEx/ folder at its root — we extract
    /// directly to steamDir, placing BepInEx/ alongside the game exe.
    private async Task InstallBepInExAsync(
        string steamDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string zipUrl = await ResolveBepInExZipUrlAsync(ct);

        progress.Report((12, $"Downloading BepInEx 5 ({BepInExFallbackVersion})..."));
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"bepinex-{Guid.NewGuid():N}.zip");
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"bepinex-extract-{Guid.NewGuid():N}");

        try
        {
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
                        int pct = (int)(12 + 15 * downloaded / total);
                        progress.Report((pct, $"Downloading BepInEx... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((28, "Extracting BepInEx into Nine Sols folder..."));
            Directory.CreateDirectory(tempDir);
            ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            // The standard BepInEx 5 zip places BepInEx/ at its root (alongside
            // a winhttp.dll doorstop proxy and doorstop_config.ini). Flatten a
            // single wrapper folder if present.
            string bepInExSrc = FlattenSingleSubdir(tempDir);

            // Copy everything into steamDir (doorstop proxy + BepInEx/).
            CopyDirectoryContentsAll(bepInExSrc, steamDir);

            progress.Report((30, "BepInEx 5 installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))       File.Delete(tempZip); }          catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// Resolve the BepInEx 5 Windows x64 zip URL from the GitHub releases API,
    /// falling back to the pinned 5.4.23.2 URL if the API is unreachable or the
    /// right asset isn't found.
    private async Task<string> ResolveBepInExZipUrlAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(BepInExReleasesApi, ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft",      out var dr) && dr.ValueKind == JsonValueKind.True) continue;
                if (rel.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True) continue;

                string? tag = rel.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                if (tag == null) continue;

                // We only want BepInEx 5.x releases, not 6.x.
                string norm = NormalizeTag(tag);
                if (!norm.StartsWith("5.")) continue;

                if (!rel.TryGetProperty("assets", out var assets) ||
                    assets.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    string lower = name.ToLowerInvariant();
                    // Look for the Windows x64 zip: typically BepInEx_win_x64_5.*.zip
                    if (lower.EndsWith(".zip") && lower.Contains("win") && lower.Contains("x64"))
                        return url;
                }
                // First eligible release checked — fall through to pinned fallback.
                break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unavailable — use pinned fallback */ }

        return BepInExFallbackZipUrl;
    }

    // ── Private helpers — mod file copy ──────────────────────────────────────

    /// Copy the mod's BepInEx folder structure into the Steam game directory.
    /// A NineSolsArchipelago release zip is expected to contain a BepInEx/ tree
    /// (with BepInEx/plugins/NineSolsArchipelago.dll and possibly other folders).
    /// If no BepInEx/ tree is found, falls back to copying any *.dll files found
    /// into BepInEx\plugins\ directly.
    private static void CopyBepInExStructure(
        string srcRoot,
        string destGameDir,
        IProgress<(int Pct, string Msg)> progress)
    {
        // Preferred: a BepInEx/ subtree inside srcRoot.
        string bepInExSrc = Path.Combine(srcRoot, "BepInEx");
        if (Directory.Exists(bepInExSrc))
        {
            // Copy BepInEx/ into destGameDir\BepInEx\.
            CopyDirectoryContentsAll(bepInExSrc, Path.Combine(destGameDir, "BepInEx"));
            progress.Report((90, "Mod BepInEx folder structure copied."));
            return;
        }

        // Fallback: scan for any DLL file and place it in BepInEx\plugins\.
        string pluginsDir = Path.Combine(destGameDir, "BepInEx", "plugins");
        Directory.CreateDirectory(pluginsDir);
        bool copiedAny = false;
        try
        {
            foreach (string dll in Directory.EnumerateFiles(srcRoot, "*.dll", SearchOption.AllDirectories))
            {
                string dllName = Path.GetFileName(dll);
                File.Copy(dll, Path.Combine(pluginsDir, dllName), overwrite: true);
                copiedAny = true;
            }
        }
        catch { }

        progress.Report((90, copiedAny
            ? "Mod DLL(s) copied into BepInEx\\plugins\\."
            : "WARNING: no DLL files found in the release zip — check the mod release manually."));
    }

    /// Recursively copy all files and subdirectories from srcDir into destDir,
    /// creating directories as needed and overwriting existing files.
    private static void CopyDirectoryContentsAll(string srcDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(srcDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
    }

    // ── Private helpers — zip handling ────────────────────────────────────────

    /// If the directory contains only one subdirectory and no files at its root,
    /// return that subdirectory (unwrap a single wrapper folder). Otherwise returns dir.
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

    /// Resolves the latest NineSolsArchipelago release: version tag, mod zip URL,
    /// and the mod's own release URL (for the news feed).
    private async Task<(string Version, string? ZipUrl, string? HtmlUrl)>
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

            string? htmlUrl = rel.TryGetProperty("html_url", out var h) ? h.GetString() : null;

            if (!rel.TryGetProperty("assets", out var assets) ||
                assets.ValueKind != JsonValueKind.Array)
                continue;

            string? zipUrl = null;
            foreach (var a in assets.EnumerateArray())
            {
                string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name == null || url == null) continue;

                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    zipUrl = url;
                    break;
                }
            }

            // Accept this release whether or not there's a zip (version is still useful).
            return (version, zipUrl, htmlUrl);
        }

        throw new InvalidOperationException(
            "No valid NineSolsArchipelago release found on GitHub.");
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        string? steamDir = FindSteamGameDirectory();
        string? exePath  = steamDir != null
            ? Path.Combine(steamDir, GameExeName)
            : null;

        ProcessStartInfo psi;
        if (exePath != null && File.Exists(exePath))
        {
            psi = new ProcessStartInfo
            {
                FileName         = exePath,
                WorkingDirectory = steamDir!,
                UseShellExecute  = true,
            };
        }
        else
        {
            // Steam URI fallback.
            psi = new ProcessStartInfo
            {
                FileName        = $"steam://rungameid/{SteamAppId}",
                UseShellExecute = true,
            };
        }

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException(
                "Failed to launch Nine Sols. Make sure it is installed via Steam.");

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
        catch { /* some processes don't surface Exited — non-fatal */ }
    }

    // ── Private helpers — news ────────────────────────────────────────────────

    private static List<NewsItem> ParseReleaseNews(string json)
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
                    Title:   string.IsNullOrEmpty(name) ? $"NineSolsArchipelago {tag}" : name,
                    Body:    body,
                    Version: NormalizeTag(tag),
                    Date:    date,
                    Url:     url
                ));
                if (items.Count >= 10) break;
            }
        }
        catch { }
        return items;
    }

    // ── Private helpers — tag normalization ───────────────────────────────────

    /// Strip a leading 'v' from a git tag if it is followed by a digit.
    private static string NormalizeTag(string tag)
    {
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..] : tag;
    }
}
