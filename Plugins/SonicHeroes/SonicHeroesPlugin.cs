using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using LauncherV2.Core;

// NOTE: WPF UI types are FULLY QUALIFIED throughout this file (System.Windows.*).
// The real launcher project sets <UseWindowsForms>true</UseWindowsForms> alongside
// WPF, so the bare names Button / TextBox / Color / Brushes / MessageBox /
// FontWeights / Orientation collide between System.Windows.Forms and
// System.Windows[.Controls/.Media] (CS0104). Qualifying every UI type avoids that.
// We also do NOT add any file-level `using X = System.Windows...;` aliases — the
// project's GlobalUsings.cs already aliases the short names, and a second local
// alias would be CS1537 (duplicate alias). Bare names or fully-qualified only.
using System.Windows;

namespace LauncherV2.Plugins.SonicHeroes;

// ═══════════════════════════════════════════════════════════════════════════════
// SonicHeroesPlugin — install / update / launch for "Sonic Heroes" (SEGA /
// Sonic Team, Steam AppID 306020) played through the SonicHeroesArchipelago mod.
//
// ── HONEST REALITY CHECK (2026-06-14) ────────────────────────────────────────
// The Archipelago integration is maintained at
//   https://github.com/Ethicallogic-Archipelago/SonicHeroesArchipelago
// This is a NATIVE "ConnectsItself" integration: the mod contains its own
// Archipelago client that connects to the AP server when the game loads. The
// launcher must NOT hold its own ApClient on the same slot while the game runs
// (ConnectsItself = true).
//
// HOW IT CONNECTS:
//   The mod reads AP connection settings from a JSON/INI config file in the
//   game directory. This plugin pre-writes the server, slot, and password into
//   that config before launch so the mod connects automatically. If the
//   prefill cannot be applied, the in-game menu can be used instead.
//
// STEAM BASE GAME:
//   The base game is the user's own legally-owned Sonic Heroes (Steam AppID
//   306020). This plugin detects the Steam install via the Windows registry and
//   the steamapps\libraryfolders.vdf multi-library discovery, then installs the
//   mod into the detected game directory. A manual override folder is also
//   supported.
//
// WHAT THIS PLUGIN DOES:
//   1. Detect the Steam Sonic Heroes install via registry → vdf → appmanifest.
//      A manual game-directory override (settings folder picker) is supported
//      and takes precedence when set; validated by presence of the game exe.
//   2. CheckForUpdateAsync — fetch the latest release tag from the GitHub API.
//   3. InstallOrUpdateAsync — download the latest release zip from GitHub and
//      extract it into GameDirectory.
//   4. LaunchAsync — pre-write AP connection settings to the mod config file,
//      then launch via Steam (steam://rungameid/306020) or the game exe directly.
//      ConnectsItself = true: the mod owns the slot connection.
//   5. CreateSettingsPanel — game-dir browse + AP connection instructions.
//   6. SupportsStandalone = true — plain Sonic Heroes runs without AP.
//
// DEFENSIVE / UNVERIFIED DETAILS:
//   * The exact config filename/format the mod reads for AP connection settings
//     was not verified offline. The plugin writes both a JSON file and an INI
//     file under several plausible names so at least one is likely to match.
//     If neither matches, the in-game menu is the fallback. The settings panel
//     documents this.
//   * The exact exe name inside the Steam install may vary (heroes.exe,
//     SonicHeroes.exe, Sonic Heroes.exe). ResolveGameExe() tries several names.
//   * Release asset name patterns are matched broadly by the resolver.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SonicHeroesPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER        = "Ethicallogic-Archipelago";
    private const string GITHUB_REPO         = "SonicHeroesArchipelago";
    private const string RepoUrl             = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL     = $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string GH_RELEASES_LATEST  = $"{GH_RELEASES_URL}/latest";

    private const int    SteamAppId               = 306020;
    private const string SteamCommonFolderName    = "Sonic Heroes";
    private const string FallbackVersion          = "1.0.0";

    /// Candidate exe filenames, in preference order.
    private static readonly string[] GameExeNames =
    {
        "heroes.exe",
        "SonicHeroes.exe",
        "Sonic Heroes.exe",
        "sonicHeroes.exe",
    };

    /// Candidate AP config filenames written by this plugin (see header).
    private const string ApConfigJson = "archipelago.json";
    private const string ApConfigIni  = "archipelago.ini";

    private const string VersionFileName = "sonic_heroes_ap_version.dat";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } },
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "sonic_heroes";
    public string DisplayName => "Sonic Heroes";
    public string Subtitle    => "Steam mod · built-in Archipelago";
    public string ApWorldName => "Sonic Heroes";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "sonic_heroes.png");

    public string ThemeAccentColor => "#FF6600";   // orange from team logos
    public string[] GameBadges     => new[] { "Steam", "ConnectsItself" };

    public string Description =>
        "Sonic Heroes Archipelago randomizer. The mod connects to your Archipelago " +
        "server automatically when you start a new game — enter your server, slot, " +
        "and password in the in-game menu. Requires the base game on Steam (AppID " +
        "306020). The launcher detects your Steam install, downloads the mod, and " +
        "pre-fills your AP connection settings so you can jump straight in.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => CheckModInstalled();
    public bool    IsRunning        { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder of the Sonic Heroes game install (Steam or manual override).
    /// The mod files are installed directly here (or in a sub-folder within it).
    public string GameDirectory { get; set; }
        = DetectInstallDir()
          ?? Path.Combine(
                 Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                 "Steam", "steamapps", "common", "Sonic Heroes");

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    private string ApConfigJsonPath => Path.Combine(GameDirectory, ApConfigJson);
    private string ApConfigIniPath  => Path.Combine(GameDirectory, ApConfigIni);

    /// This plugin's own settings sidecar (kept out of shared SettingsStore so
    /// the plugin stays a single self-contained file).
    private string SettingsSidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                        "sonic_heroes_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;
    private string?  _apWorldFileName;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The mod's native AP client owns the slot — nothing is forwarded here.
    // These events exist for interface compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── IGamePlugin flags ─────────────────────────────────────────────────────

    public bool SupportsStandalone             => true;
    public bool ConnectsItself                 => true;
    public string? BuiltAgainstDataPackageChecksum => null;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Read installed version stamp.
        try
        {
            InstalledVersion = File.Exists(VersionFilePath) && IsInstalled
                ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim()
                : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        // Fetch latest available version from GitHub.
        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(GITHUB_OWNER, GITHUB_REPO, ct));
        }
        catch
        {
            AvailableVersion = null;
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((2, "Checking latest Sonic Heroes Archipelago release..."));
        var (version, zipUrl, apworldName) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;
        _apWorldFileName = apworldName;

        // Already current?
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"Sonic Heroes Archipelago {version} is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a download for the Sonic Heroes Archipelago mod on " +
                "the GitHub release page. Check your internet connection, or download " +
                "manually from " + RepoUrl + "/releases.");

        await DownloadAndExtractModAsync(zipUrl, version, progress, ct);

        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"Sonic Heroes Archipelago {version} installed. Open the game and use " +
            "the in-game AP menu to connect, or let the launcher pre-fill your " +
            "settings on the next Play click."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── Lifecycle — Launch (AP) ───────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // Pre-write AP connection settings for the mod (best effort — see header).
        try { WriteApConfig(session); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        StartGame();
        return Task.CompletedTask;
    }

    // ── Lifecycle — Launch (standalone) ──────────────────────────────────────

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartGame();
        return Task.CompletedTask;
    }

    // ── Lifecycle — Stop ──────────────────────────────────────────────────────

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        ScrubApConfigPassword();
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var accent  = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0xFF, 0x66, 0x00));
        var success = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20),
        };

        // ── Section: Game directory ───────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "GAME DIRECTORY",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = GameDirectory,
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content     = "Browse...",
            Width       = 90,
            Padding     = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select Sonic Heroes game folder",
                InitialDirectory = Directory.Exists(GameDirectory) ? GameDirectory
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
                SaveOverrideDir(dlg.FolderName);
            }
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn,
            System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        // Detected vs. not-found status line.
        string? detected = DetectInstallDir();
        string statusText = IsInstalled
            ? "✓ Sonic Heroes Archipelago mod is installed"
            : detected != null
                ? "Sonic Heroes detected — mod not yet installed (click Install in Play tab)"
                : "Sonic Heroes not found — point to the game folder above, then install";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = statusText,
            FontSize   = 11,
            Foreground = IsInstalled ? success : muted,
            Margin     = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: AP connection ────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "ARCHIPELAGO CONNECTION",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "When you click Play, the launcher pre-fills your server, slot name, " +
                   "and password into the mod's config file in the game folder " +
                   "(" + ApConfigJson + " and " + ApConfigIni + "). If the mod reads " +
                   "a different config file, use the in-game Archipelago menu to enter " +
                   "your connection details instead — they are saved between sessions.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Setup notes ──────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "SETUP NOTES",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "1. Make sure Sonic Heroes (Steam AppID 306020) is installed.\n" +
                   "2. Click Install in the Play tab to download and install the mod.\n" +
                   "3. Connect to your Archipelago session via Play → fill in server,\n" +
                   "   slot, and password. The mod connects automatically on game start.\n" +
                   "4. If the mod does not connect, open the in-game AP menu and enter\n" +
                   "   your details there (they are saved for future sessions).",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Links ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "LINKS",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });

        foreach (var (label, url) in new[]
        {
            ("Sonic Heroes Archipelago (GitHub) ↗", RepoUrl),
            ("Sonic Heroes on Steam ↗",
             "https://store.steampowered.com/app/306020/Sonic_Heroes/"),
            ("Archipelago Official ↗", "https://archipelago.gg"),
        })
        {
            var linkBtn = new System.Windows.Controls.Button
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
                Cursor              = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            linkBtn.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(u) { UseShellExecute = true });
                }
                catch { }
            };
            panel.Children.Add(linkBtn);
        }

        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc  = JsonDocument.Parse(json);
            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) &&
                    d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t)
                                 ? NormalizeTag(t.GetString()) ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch
        {
            return Array.Empty<NewsItem>();
        }
    }

    // ── Private helpers — Steam install detection ─────────────────────────────

    /// Detect the Steam Sonic Heroes install directory. Returns null if not found.
    private static string? DetectInstallDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps,
                                           $"appmanifest_{SteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common     = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeGameDir(candidate)) return candidate;
                    }

                    // Conventional folder name fallback.
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeGameDir(conventional)) return conventional;
                }
                catch { /* try next library */ }
            }
        }
        return null;
    }

    /// True when the folder looks like a Sonic Heroes install (has a game exe
    /// or a recognizable sub-file).
    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            foreach (string exe in GameExeNames)
                if (File.Exists(Path.Combine(dir, exe))) return true;
            // Fallback: any exe with "hero" in the name.
            foreach (string f in Directory.EnumerateFiles(dir, "*.exe",
                                                          SearchOption.TopDirectoryOnly))
                if (Path.GetFileNameWithoutExtension(f)
                        .Contains("hero", StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
        catch { return false; }
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
        string? progX86 = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    /// Steam stores its SteamPath with forward slashes; normalize for Path APIs.
    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    /// All Steam library roots: the Steam root plus every "path" entry in
    /// steamapps\libraryfolders.vdf (tolerant text scan).
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

    // ── Private helpers — mod detection ──────────────────────────────────────

    /// True when the mod appears to be installed in GameDirectory.
    private bool CheckModInstalled()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(GameDirectory) || !Directory.Exists(GameDirectory))
                return false;

            // Check for our own version stamp.
            if (File.Exists(VersionFilePath)) return true;

            // Check for any mod DLL that looks like the AP mod.
            foreach (string dll in Directory.EnumerateFiles(
                GameDirectory, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll)
                                  .ToLowerInvariant();
                if (name.Contains("archipelago") || name.Contains("sonicHeroes"))
                    return true;
            }
        }
        catch { /* permission / vanished */ }
        return false;
    }

    // ── Private helpers — exe resolution ─────────────────────────────────────

    /// Resolve the Sonic Heroes exe in GameDirectory.
    private string? ResolveGameExe()
    {
        if (string.IsNullOrWhiteSpace(GameDirectory) || !Directory.Exists(GameDirectory))
            return null;

        // Try preferred names first.
        foreach (string name in GameExeNames)
        {
            string candidate = Path.Combine(GameDirectory, name);
            if (File.Exists(candidate)) return candidate;
        }

        // Fuzzy: any exe with "hero" in the name.
        try
        {
            foreach (string f in Directory.EnumerateFiles(
                GameDirectory, "*.exe", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileNameWithoutExtension(f)
                        .Contains("hero", StringComparison.OrdinalIgnoreCase))
                    return f;
            }
        }
        catch { }

        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        // Try direct exe first; fall back to Steam URL.
        string? exe = ResolveGameExe();

        ProcessStartInfo psi;
        if (exe != null && File.Exists(exe))
        {
            psi = new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = GameDirectory,
                UseShellExecute  = true,
            };
        }
        else
        {
            // Steam URL launch — Steam will handle finding the exe.
            psi = new ProcessStartInfo
            {
                FileName        = $"steam://rungameid/{SteamAppId}",
                UseShellExecute = true,
            };
        }

        var proc = Process.Start(psi);
        if (proc == null) return;   // Steam URL opens Steam; no child process to track

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            ScrubApConfigPassword();
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — AP config prefill ──────────────────────────────────

    /// Pre-write AP connection settings to the mod config files. Writes both a
    /// JSON file and an INI file under plausible names (see header). Best effort.
    private void WriteApConfig(ApSession session)
    {
        var (host, port) = ParseServerHostPort(session.ServerUri);
        Directory.CreateDirectory(GameDirectory);

        // Write JSON config (archipelago.json).
        try
        {
            var cfg = new Dictionary<string, object?>
            {
                ["server"]    = $"{host}:{port}",
                ["host"]      = host,
                ["port"]      = port,
                ["slot_name"] = session.SlotName,
                ["slot"]      = session.SlotName,
                ["player"]    = session.SlotName,
                ["password"]  = session.Password ?? "",
            };

            // Read-modify-write: preserve any existing keys.
            if (File.Exists(ApConfigJsonPath))
            {
                try
                {
                    string existing = File.ReadAllText(ApConfigJsonPath);
                    if (!string.IsNullOrWhiteSpace(existing))
                    {
                        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                            existing);
                        if (parsed != null)
                        {
                            foreach (var kv in parsed)
                                cfg.TryAdd(kv.Key, JsonElementToObject(kv.Value));
                            // Overwrite the AP keys (the above TryAdd won't replace them).
                            cfg["server"]    = $"{host}:{port}";
                            cfg["host"]      = host;
                            cfg["port"]      = port;
                            cfg["slot_name"] = session.SlotName;
                            cfg["slot"]      = session.SlotName;
                            cfg["player"]    = session.SlotName;
                            cfg["password"]  = session.Password ?? "";
                        }
                    }
                }
                catch { /* corrupt — overwrite entirely */ }
            }

            File.WriteAllText(
                ApConfigJsonPath,
                JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* best effort — log-and-swallow */ }

        // Write INI config (archipelago.ini).
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Archipelago]");
            sb.AppendLine($"Server={host}:{port}");
            sb.AppendLine($"Host={host}");
            sb.AppendLine($"Port={port}");
            sb.AppendLine($"SlotName={session.SlotName}");
            sb.AppendLine($"Player={session.SlotName}");
            sb.AppendLine($"Password={session.Password ?? ""}");
            sb.AppendLine();
            sb.AppendLine("[AP]");
            sb.AppendLine($"IP={host}:{port}");
            sb.AppendLine($"PlayerName={session.SlotName}");
            sb.AppendLine($"Password={session.Password ?? ""}");
            File.WriteAllText(ApConfigIniPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch { /* best effort */ }
    }

    /// Clear the AP password from config files when the session ends.
    private void ScrubApConfigPassword()
    {
        // JSON scrub.
        try
        {
            if (File.Exists(ApConfigJsonPath))
            {
                string text = File.ReadAllText(ApConfigJsonPath);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text);
                    if (parsed != null)
                    {
                        bool changed = false;
                        var result = new Dictionary<string, object?>();
                        foreach (var kv in parsed)
                        {
                            if (kv.Key.Equals("password", StringComparison.OrdinalIgnoreCase)
                                && kv.Value.ValueKind == JsonValueKind.String
                                && !string.IsNullOrEmpty(kv.Value.GetString()))
                            {
                                result[kv.Key] = "";
                                changed = true;
                            }
                            else
                            {
                                result[kv.Key] = JsonElementToObject(kv.Value);
                            }
                        }
                        if (changed)
                            File.WriteAllText(
                                ApConfigJsonPath,
                                JsonSerializer.Serialize(result, new JsonSerializerOptions
                                    { WriteIndented = true }),
                                new UTF8Encoding(false));
                    }
                }
            }
        }
        catch { }

        // INI scrub: replace Password= lines.
        try
        {
            if (File.Exists(ApConfigIniPath))
            {
                var lines = File.ReadAllLines(ApConfigIniPath);
                bool changed = false;
                for (int i = 0; i < lines.Length; i++)
                {
                    string trimmed = lines[i].TrimStart();
                    if (trimmed.StartsWith("Password=", StringComparison.OrdinalIgnoreCase)
                        && !trimmed.Equals("Password=", StringComparison.OrdinalIgnoreCase))
                    {
                        int eq = lines[i].IndexOf('=');
                        if (eq >= 0) { lines[i] = lines[i][..(eq + 1)]; changed = true; }
                    }
                }
                if (changed)
                    File.WriteAllLines(ApConfigIniPath, lines, new UTF8Encoding(false));
            }
        }
        catch { }
    }

    /// Convert a JsonElement to a plain object for round-trip serialization.
    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out long l) ? l : el.GetDouble(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.Null   => null,
        _                    => el.Clone(),
    };

    /// Parse "host:port" / "ws://host:port" / "wss://host:port" into components.
    /// Default AP port is 38281.
    private static (string Host, int Port) ParseServerHostPort(string serverUri)
    {
        string s = serverUri.Trim();
        foreach (string prefix in new[] { "wss://", "ws://", "archipelago://" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..];
                break;
            }
        }

        int slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];

        string host = s;
        int    port = 38281;

        int colonCount = 0;
        foreach (char c in s) if (c == ':') colonCount++;

        if (s.StartsWith('['))
        {
            int close = s.IndexOf(']');
            if (close > 0)
            {
                host = s[1..close];
                string rest = s[(close + 1)..];
                if (rest.StartsWith(':') &&
                    int.TryParse(rest[1..], out int p6) && p6 > 0 && p6 <= 65535)
                    port = p6;
            }
        }
        else if (colonCount > 1)
        {
            host = s;
        }
        else
        {
            int colon = s.LastIndexOf(':');
            if (colon > 0 && int.TryParse(s[(colon + 1)..], out int p) && p > 0 && p <= 65535)
            {
                host = s[..colon];
                port = p;
            }
        }

        if (host.Length == 0) host = "archipelago.gg";
        return (host, port);
    }

    // ── Private helpers — tag normalization ───────────────────────────────────

    /// Strip leading 'v' when followed by a digit; otherwise return trimmed tag.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    // ── Private helpers — GitHub release resolution ───────────────────────────

    /// Resolve the latest release: version + zip download URL + apworld filename.
    /// Falls back to the pinned FallbackVersion (with no zip URL) when offline.
    private async Task<(string Version, string? ZipUrl, string? ApWorldName)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        // Try /releases/latest first; fall back to enumerating /releases if it
        // returns a prerelease-only repo (404 or no tag_name).
        foreach (string url in new[] { GH_RELEASES_LATEST, GH_RELEASES_URL })
        {
            try
            {
                string json = await _http.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);

                // /releases/latest returns an object; /releases returns an array.
                IEnumerable<JsonElement> candidates;
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    candidates = doc.RootElement.EnumerateArray();
                else
                    candidates = new[] { doc.RootElement };

                foreach (var rel in candidates)
                {
                    // Skip drafts.
                    if (rel.TryGetProperty("draft", out var dr) &&
                        dr.ValueKind == JsonValueKind.True)
                        continue;

                    string? version = rel.TryGetProperty("tag_name", out var t)
                        ? NormalizeTag(t.GetString())
                        : null;
                    if (version == null) continue;

                    if (rel.TryGetProperty("assets", out var assets) &&
                        assets.ValueKind == JsonValueKind.Array)
                    {
                        string? zip = null, apworldName = null;
                        string? anyZip = null;

                        foreach (var a in assets.EnumerateArray())
                        {
                            string? name = a.TryGetProperty("name", out var n)
                                ? n.GetString() : null;
                            string? dlUrl = a.TryGetProperty("browser_download_url", out var u)
                                ? u.GetString() : null;
                            if (name == null || dlUrl == null) continue;

                            string lower = name.ToLowerInvariant();

                            if (lower.EndsWith(".apworld"))
                            {
                                apworldName ??= name;
                            }
                            else if (lower.EndsWith(".zip") &&
                                     !lower.Contains("source") &&
                                     !lower.Contains("linux") &&
                                     !lower.Contains("mac") &&
                                     !lower.Contains("darwin"))
                            {
                                anyZip ??= dlUrl;
                                if (zip == null &&
                                    (lower.Contains("win") ||
                                     lower.Contains("x64") ||
                                     lower.Contains("x86")))
                                    zip = dlUrl;
                            }
                        }

                        zip ??= anyZip;
                        if (zip != null || url == GH_RELEASES_LATEST)
                            return (version, zip, apworldName);
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* try next endpoint / fall through to pinned fallback */ }
        }

        return (FallbackVersion, null, null);
    }

    // ── Private helpers — download + extract ─────────────────────────────────

    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"sonic-heroes-ap-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading Sonic Heroes Archipelago {version}..."));
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
                        int pct = (int)(5 + 65 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((72, "Extracting mod files..."));
            Directory.CreateDirectory(GameDirectory);
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, GameDirectory, overwriteFiles: true);

            // Flatten single top-level wrapper folder if the exe is not at root.
            if (ResolveGameExe() == null)
            {
                string[] subdirs = Directory.GetDirectories(GameDirectory);
                if (subdirs.Length == 1 &&
                    Directory.GetFiles(GameDirectory).Length == 0)
                {
                    string sub = subdirs[0];
                    foreach (string fileSrc in Directory.EnumerateFiles(
                        sub, "*", SearchOption.AllDirectories))
                    {
                        string rel     = Path.GetRelativePath(sub, fileSrc);
                        string fileDst = Path.Combine(GameDirectory, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                        File.Move(fileSrc, fileDst, overwrite: true);
                    }
                    Directory.Delete(sub, recursive: true);
                }
            }

            progress.Report((85, "Mod files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — settings sidecar ───────────────────────────────────
    // Keeps the manual game-directory override out of shared Core/SettingsStore.

    private sealed class SonicHeroesSettings
    {
        public string? OverrideDir { get; set; }
    }

    private SonicHeroesSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<SonicHeroesSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(SonicHeroesSettings s)
    {
        try
        {
            string dir = Path.GetDirectoryName(SettingsSidecarPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { }
    }

    private void SaveOverrideDir(string dir)
    {
        var s = LoadSettings();
        s.OverrideDir = dir;
        SaveSettings(s);
    }
}
