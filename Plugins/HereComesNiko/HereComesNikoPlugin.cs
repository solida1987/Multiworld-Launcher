using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using LauncherV2.Core;

// WPF/WinForms disambiguation: all ambiguous types are fully-qualified (CS0104).

namespace LauncherV2.Plugins.HereComesNiko;

// ═══════════════════════════════════════════════════════════════════════════════
// HereComesNikoPlugin — install / update / launch for "Here Comes Niko!"
// (Frog Vibes, 2021) played through NikoArchipelagoMod, a BepInEx 5 plugin
// that serves as the in-game Archipelago client (ConnectsItself = true).
//
// VERIFIED FACTS (2026-06-14, niieli/Niko-Archipelago + niieli/NikoArchipelagoMod)
//   AP game string : "Here Comes Niko!"  (worlds/hcniko/__init__.py)
//   Steam AppID   : 925950
//   Mod loader    : BepInEx 5.4.23.3 (Unity Mono x64)
//   Mod DLL       : NikoArchipelago.dll  → <GameDir>\BepInEx\plugins\
//   Config prefill: APSavedSettings.json with Host (host:port) + SlotName
//                   Password NOT stored (mod holds it in-memory only)
//   ConnectsItself: true — mod owns the AP slot; launcher must NOT connect
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class HereComesNikoPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string MOD_OWNER          = "niieli";
    private const string MOD_REPO           = "NikoArchipelagoMod";
    private const string ModReleasesApi     = $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";
    private const string SetupGuideUrl      = "https://github.com/niieli/Niko-Archipelago/blob/main/worlds/hcniko/docs/guide_en.md";
    private const string BepInExUrl         = "https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.3";

    private const string SteamAppId           = "925950";
    private const string SteamCommonFolder    = "Here Comes Niko";
    private const string GameExeName          = "HereComesNiko.exe";
    private const string ModDllName           = "NikoArchipelago.dll";
    private const string ApSettingsFileName   = "APSavedSettings.json";
    private const string BepInExPluginSubPath = @"BepInEx\plugins";
    private const string VersionFileName      = "hcniko_mod_version.dat";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "hcniko";
    public string DisplayName => "Here Comes Niko!";
    public string Subtitle    => "Native PC · NikoArchipelagoMod (BepInEx 5)";
    public string ApWorldName => "Here Comes Niko!";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "hcniko.png");

    public string ThemeAccentColor => "#4A9F6E";   // frog green

    public string[] GameBadges => new[]
    {
        "Steam Required",
        "BepInEx 5 mod",
        "ConnectsItself",
    };

    public string Description =>
        "Here Comes Niko! (Frog Vibes, 2021) is a cozy 3D platformer where you " +
        "play as Niko, a professional friend helping the residents of a colourful " +
        "world. With NikoArchipelagoMod the game's coins, bugs, cassette tapes, " +
        "and fish are shuffled into a multiworld. The BepInEx mod adds an " +
        "Archipelago option to the main menu — enter your server, slot name, and " +
        "password there. The launcher pre-fills your host and slot automatically.";

    public string? VideoPreviewUrl  => null;
    public string[] ScreenshotUrls  => Array.Empty<string>();

    // ── Version / install state ───────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled
    {
        get
        {
            string? dir = FindSteamGameDirectory();
            if (dir != null)
                return File.Exists(Path.Combine(dir, BepInExPluginSubPath, ModDllName));
            return File.Exists(VersionFilePath) && !string.IsNullOrWhiteSpace(InstalledVersion);
        }
    }

    public bool IsRunning { get; private set; }

    public bool ConnectsItself  => true;
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "HereComesNiko");

    private string RomLibraryDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", "hcniko");

    private string VersionFilePath
        => Path.Combine(RomLibraryDir, VersionFileName);

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events — inert (ConnectsItself = true) ─────────────────────
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067
    public event Action<int>?    GameExited;

    // ── CheckForUpdateAsync ───────────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = File.Exists(VersionFilePath)
                ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim()
                : null;
        }
        catch { InstalledVersion = null; }

        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(MOD_OWNER, MOD_REPO, ct));
        }
        catch { AvailableVersion = null; }
    }

    // ── InstallOrUpdateAsync ──────────────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((2, "Checking latest NikoArchipelagoMod release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a download for NikoArchipelagoMod on GitHub. " +
                "Check your connection or download manually from " +
                $"https://github.com/{MOD_OWNER}/{MOD_REPO}/releases");

        // Fast path — already current?
        if (IsInstalled && File.Exists(VersionFilePath))
        {
            try
            {
                string stamped = (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim();
                if (stamped == version)
                {
                    InstalledVersion = version;
                    progress.Report((100, $"NikoArchipelagoMod {version} is already installed and up to date."));
                    return;
                }
            }
            catch { }
        }

        progress.Report((5, "Locating Here Comes Niko! Steam installation..."));
        string? steamDir = FindSteamGameDirectory();
        if (steamDir == null)
            progress.Report((5, "Game not found via Steam registry — will extract mod files for manual installation."));
        else
            progress.Report((8, $"Found game at: {steamDir}"));

        progress.Report((10, $"Downloading NikoArchipelagoMod {version}..."));
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"hcnikomod-{version}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string tempZip = Path.Combine(tempDir, "NikoArchipelagoMod.zip");

        try
        {
            using var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? -1;
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
                        progress.Report(((int)(10 + 40 * downloaded / total),
                            $"Downloading... {downloaded / 1_000_000}MB"));
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((52, "Extracting mod files..."));
            string extractDir = Path.Combine(tempDir, "extracted");
            ZipFile.ExtractToDirectory(tempZip, extractDir, overwriteFiles: true);
            string modSourceDir = FlattenSingleSubdir(extractDir);
            progress.Report((58, "Mod files extracted."));

            if (steamDir != null)
            {
                // Install: copy NikoArchipelago.dll (and any assets) into BepInEx\plugins\.
                progress.Report((62, "Installing mod into BepInEx\\plugins\\..."));
                string pluginDir = Path.Combine(steamDir, BepInExPluginSubPath);

                if (!Directory.Exists(Path.Combine(steamDir, "BepInEx")))
                {
                    progress.Report((62,
                        "BepInEx is not installed in the game directory. " +
                        "Please install BepInEx 5 (x64) first: " + BepInExUrl +
                        ", run the game once to let it initialise, then reinstall this mod."));
                }
                else
                {
                    Directory.CreateDirectory(pluginDir);
                    foreach (string file in Directory.EnumerateFiles(modSourceDir, "*.*",
                        SearchOption.TopDirectoryOnly))
                    {
                        File.Copy(file, Path.Combine(pluginDir, Path.GetFileName(file)),
                            overwrite: true);
                    }

                    progress.Report((85,
                        $"NikoArchipelagoMod {version} installed into BepInEx\\plugins\\."));
                }
            }
            else
            {
                // No Steam dir found — copy files to GameDirectory for manual install.
                Directory.CreateDirectory(GameDirectory);
                foreach (string file in Directory.EnumerateFiles(modSourceDir, "*.*",
                    SearchOption.TopDirectoryOnly))
                {
                    File.Copy(file, Path.Combine(GameDirectory, Path.GetFileName(file)),
                        overwrite: true);
                }

                progress.Report((85,
                    "Mod files saved to: " + GameDirectory + ". " +
                    "Install BepInEx 5 (x64) into your Here Comes Niko! folder, " +
                    "run the game once, then copy the mod files into BepInEx\\plugins\\. " +
                    "See the setup guide: " + SetupGuideUrl));
            }

            Directory.CreateDirectory(RomLibraryDir);
            await File.WriteAllTextAsync(VersionFilePath, version, ct);
            InstalledVersion = version;

            progress.Report((100,
                $"NikoArchipelagoMod {version} installed. " +
                "Launch Here Comes Niko!, click Archipelago on the main menu, " +
                "and connect to your server."));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // ── VerifyInstallAsync ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── LaunchAsync ───────────────────────────────────────────────────────────

    public async Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        string? steamDir = FindSteamGameDirectory();
        string gameDir = steamDir
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam", "steamapps", "common", SteamCommonFolder);

        // Pre-fill APSavedSettings.json so the in-game menu opens with
        // host:port and slot name already entered.
        WriteApSettings(gameDir, session);

        string? exe = FindGameExe(gameDir);
        if (exe != null && File.Exists(exe))
        {
            _gameProcess = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? gameDir,
                UseShellExecute  = true,
            });
        }
        else
        {
            _gameProcess = Process.Start(new ProcessStartInfo(
                $"steam://rungameid/{SteamAppId}") { UseShellExecute = true });
        }

        IsRunning = true;

        if (_gameProcess != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _gameProcess.WaitForExitAsync(ct);
                    IsRunning = false;
                    GameExited?.Invoke(_gameProcess.ExitCode);
                }
                catch { IsRunning = false; }
            }, ct);
        }

        await Task.CompletedTask;
    }

    // ── StopAsync ─────────────────────────────────────────────────────────────

    public async Task StopAsync()
    {
        if (_gameProcess != null && !_gameProcess.HasExited)
        {
            try { _gameProcess.CloseMainWindow(); } catch { }
            try
            {
                if (!_gameProcess.WaitForExit(5000))
                    _gameProcess.Kill();
            }
            catch { }
        }
        IsRunning = false;
        await Task.CompletedTask;
    }

    // ── LaunchStandaloneAsync ─────────────────────────────────────────────────

    public async Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        string? steamDir = FindSteamGameDirectory();
        string gameDir = steamDir
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Steam", "steamapps", "common", SteamCommonFolder);

        string? exe = FindGameExe(gameDir);
        if (exe != null && File.Exists(exe))
        {
            _gameProcess = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? gameDir,
                UseShellExecute  = true,
            });
        }
        else
        {
            _gameProcess = Process.Start(new ProcessStartInfo(
                $"steam://rungameid/{SteamAppId}") { UseShellExecute = true });
        }

        IsRunning = _gameProcess != null;
        await Task.CompletedTask;
    }

    // ── AP bridge methods — no-ops (ConnectsItself = true) ───────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index,
        CancellationToken ct = default) => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── CreateSettingsPanel ───────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var panel = new StackPanel { Margin = new Thickness(8) };

        var title = new System.Windows.Controls.TextBlock
        {
            Text       = "Here Comes Niko! — Setup",
            FontSize   = 16,
            FontWeight = System.Windows.FontWeights.Bold,
            Margin     = new Thickness(0, 0, 0, 8),
        };
        panel.Children.Add(title);

        string? steamDir = FindSteamGameDirectory();
        string status = steamDir != null
            ? $"Game found: {steamDir}"
            : "Game not detected via Steam — install Here Comes Niko! on Steam.";

        string modStatus = IsInstalled
            ? $"Mod installed: {InstalledVersion ?? "unknown version"}"
            : "Mod not installed — click Install above.";

        foreach (string line in new[] { status, modStatus })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text       = line,
                TextWrapping = TextWrapping.Wrap,
                Margin     = new Thickness(0, 0, 0, 4),
            });
        }

        var guide = new System.Windows.Controls.TextBlock
        {
            Text       = "Setup guide: " + SetupGuideUrl,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0x9F, 0x6E)),
            Margin     = new Thickness(0, 8, 0, 4),
        };
        panel.Children.Add(guide);

        var steps = new System.Windows.Controls.TextBlock
        {
            Text =
                "Steps:\n" +
                "1. Own Here Comes Niko! on Steam (AppID 925950).\n" +
                "2. Install BepInEx 5.4.23.3 (x64) into the game folder.\n" +
                "3. Run the game once to let BepInEx initialise.\n" +
                "4. Click Install — the launcher installs NikoArchipelagoMod.\n" +
                "5. Launch from here — host and slot are pre-filled in-game.\n" +
                "6. Click Archipelago on the main menu, enter your password, Connect.",
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 4, 0, 0),
        };
        panel.Children.Add(steps);

        return panel;
    }

    // ── GetNewsAsync ──────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(ModReleasesApi, ct);
            using var doc = JsonDocument.Parse(json);
            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (items.Count >= 10) break;
                string tag  = el.GetProperty("tag_name").GetString() ?? "";
                string body = el.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                string url  = el.TryGetProperty("html_url", out var u) ? u.GetString() ?? "" : "";
                DateTimeOffset date = el.TryGetProperty("published_at", out var d)
                    ? DateTimeOffset.Parse(d.GetString()!)
                    : DateTimeOffset.UtcNow;
                items.Add(new NewsItem($"NikoArchipelagoMod {tag}", body, tag, date, url));
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — APSavedSettings.json ────────────────────────────────

    private static void WriteApSettings(string gameDir, ApSession session)
    {
        try
        {
            string pluginDir    = Path.Combine(gameDir, BepInExPluginSubPath);
            string settingsPath = Path.Combine(pluginDir, ApSettingsFileName);

            NikoApSettings settings = ReadExistingSettings(settingsPath);
            settings.Host     = $"{session.ServerUri}";
            settings.SlotName = session.SlotName ?? settings.SlotName ?? "Player1";

            Directory.CreateDirectory(pluginDir);
            string json = JsonSerializer.Serialize(settings,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(settingsPath, json, new UTF8Encoding(false));
        }
        catch { }
    }

    private static NikoApSettings ReadExistingSettings(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                string txt = File.ReadAllText(path);
                if (!string.IsNullOrWhiteSpace(txt))
                {
                    var parsed = JsonSerializer.Deserialize<NikoApSettings>(txt,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (parsed != null) return parsed;
                }
            }
        }
        catch { }
        return new NikoApSettings();
    }

    // ── Private helpers — GitHub release resolution ───────────────────────────

    private async Task<(string? Version, string? ZipUrl)> ResolveLatestReleaseAsync(
        CancellationToken ct)
    {
        string json = await _http.GetStringAsync(ModReleasesApi, ct);
        using var doc = JsonDocument.Parse(json);
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            bool prerelease = release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean();
            if (prerelease) continue;

            string? version = release.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (version == null) continue;

            if (!release.TryGetProperty("assets", out var assets)) continue;
            foreach (var asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? url  = asset.TryGetProperty("browser_download_url", out var u)
                    ? u.GetString() : null;
                if (name != null && url != null && name.EndsWith(".zip",
                        StringComparison.OrdinalIgnoreCase))
                    return (version, url);
            }
        }
        return (null, null);
    }

    // ── Private helpers — Steam / game detection ──────────────────────────────

    private static string? FindSteamGameDirectory()
    {
        try { return DetectSteamGameDir(); }
        catch { return null; }
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

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeGameDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolder);
                    if (LooksLikeGameDir(conventional)) return conventional;
                }
                catch { }
            }
        }
        return null;
    }

    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, GameExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, "BepInEx"))) return true;
            return FindGameExe(dir) != null;
        }
        catch { return false; }
    }

    private static string? FindGameExe(string gameDir)
    {
        try
        {
            string direct = Path.Combine(gameDir, GameExeName);
            if (File.Exists(direct)) return direct;
            foreach (string exe in Directory.EnumerateFiles(gameDir, GameExeName,
                SearchOption.AllDirectories))
                return exe;
        }
        catch { }
        return null;
    }

    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return hkcu.Replace('/', '\\').TrimEnd('\\');

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return hklm.TrimEnd('\\');

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

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
            string norm = text.Substring(open + 1, close - open - 1)
                .Replace("\\\\", "\\").Replace('/', '\\').TrimEnd('\\');
            if (norm.Length > 0 && seen.Add(norm)) yield return norm;
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

    private static string FlattenSingleSubdir(string dir)
    {
        try
        {
            var subdirs = Directory.GetDirectories(dir);
            var files   = Directory.GetFiles(dir);
            if (files.Length == 0 && subdirs.Length == 1)
                return FlattenSingleSubdir(subdirs[0]);
        }
        catch { }
        return dir;
    }

    // ── Nested types ──────────────────────────────────────────────────────────

    private sealed class NikoApSettings
    {
        [JsonPropertyName("Host")]
        public string Host { get; set; } = "archipelago.gg:";

        [JsonPropertyName("SlotName")]
        public string SlotName { get; set; } = "Player1";

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? Extra { get; set; }
    }
}
