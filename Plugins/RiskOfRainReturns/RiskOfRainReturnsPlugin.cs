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
using LauncherV2.Core;

// NOTE on type qualification (BUILD GOTCHA — CS0104):
// This project sets BOTH <UseWPF>true</UseWPF> and <UseWindowsForms>true</UseWindowsForms>.
// WPF UI types are FULLY QUALIFIED below to avoid CS0104. Do NOT add
// `using System.Windows.Controls;` or `using System.Windows.Media;` here.

namespace LauncherV2.Plugins.RiskOfRainReturns;

// ═══════════════════════════════════════════════════════════════════════════════
// RiskOfRainReturnsPlugin — launch integration for "Risk of Rain Returns"
// (Hopoo Games / Gearbox, 2023, Steam appid 1337520), played through the
// Archipelago BepInEx mod from github.com/studkid/RoR_Archipelago.
//
// ── FACTS ─────────────────────────────────────────────────────────────────────
//   * STEAM game — user must own Risk of Rain Returns (appid 1337520).
//   * AP game string: "Risk of Rain Returns"
//   * The Archipelago mod is a BepInEx plugin that includes a built-in AP
//     client. ConnectsItself = true.
//   * SupportsStandalone = true: the base game runs without the mod.
//   * Steam install is detected via SteamLocator.FindGameDir(1337520).
//   * Connection is made IN-GAME via the mod's Archipelago lobby UI — no
//     command-line / config prefill is possible. The settings panel surfaces
//     the session credentials for the user to copy.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class RiskOfRainReturnsPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GH_OWNER    = "studkid";
    private const string GH_REPO     = "RoR_Archipelago";
    private const string GH_RELEASES = $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases";
    private const string REPO_URL    = $"https://github.com/{GH_OWNER}/{GH_REPO}";

    private const int    STEAM_APP_ID = 1337520;
    private static readonly string SteamRunUrl =
        $"steam://rungameid/{STEAM_APP_ID}";
    private const string SteamCommonFolderName = "Risk of Rain Returns";
    private const string GameExeName           = "Risk of Rain Returns.exe";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Risk%20of%20Rain%20Returns/setup_en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    private const string FallbackVersion = "latest";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "risk_of_rain_returns";
    public string DisplayName => "Risk of Rain Returns";
    public string Subtitle    => "Native PC · Archipelago mod";
    public string ApWorldName => "Risk of Rain Returns";

    public string IconPath
        => Path.Combine(AppContext.BaseDirectory, "Assets", "risk_of_rain_returns.png");

    public string ThemeAccentColor => "#C03020";   // Risk of Rain red

    public string[] GameBadges => new[] { "Steam · needs mod" };

    public string Description =>
        "Risk of Rain Returns is the 2023 HD remake of the original Risk of Rain. " +
        "The Archipelago BepInEx mod shuffles survivors, items, stages, and " +
        "achievements into the multiworld. Requires the Steam version (appid 1337520) " +
        "with the BepInEx mod installed.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled => FindInstalledModMarker() != null;

    public bool IsRunning { get; private set; }

    public bool ConnectsItself    => true;
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; }
        = SteamLocator.FindGameDir(STEAM_APP_ID) ?? string.Empty;

    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "rorr_launcher.json");
    private string VersionFilePath
        => Path.Combine(RomLibraryDirectory, "rorr_ap_mod_version.dat");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = IsInstalled
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
        progress.Report((2, "Locating your Risk of Rain Returns installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Risk of Rain Returns installation. Open this game's " +
                "Settings and pick your Risk of Rain Returns folder, or install it via " +
                "Steam first (appid 1337520).");

        string bepInExPluginsDir = Path.Combine(gameDir, "BepInEx", "plugins");
        string apModDir          = Path.Combine(bepInExPluginsDir, "Archipelago");

        progress.Report((6, "Checking the latest Archipelago mod release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Archipelago Risk of Rain Returns mod download on " +
                "GitHub. Check your internet connection, or download the mod manually " +
                "from " + REPO_URL + "/releases and follow the installation readme. " +
                "Open Settings for links.");

        await DownloadAndExtractModAsync(zipUrl, version, apModDir, progress, ct);

        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepInExPresent = Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));
        progress.Report((100,
            $"Staged the Archipelago mod {version} into your Risk of Rain Returns " +
            "BepInEx\\plugins folder. " +
            (bepInExPresent
                ? "BepInEx looks present. "
                : "IMPORTANT: BepInEx itself is required and not included in this mod " +
                  "download. Install BepInEx for Risk of Rain Returns manually before " +
                  "running. ") +
            "To play: launch the game (modded), open the Archipelago lobby, and " +
            "enter your server URL, port, and slot."));
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
        // ConnectsItself = true: the BepInEx mod owns the AP slot connection.
        // The user connects in-game via the Archipelago lobby UI — no prefill.
        _ = session;
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
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var linkClr = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
                    { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        string? gameDir   = ResolveGameDir();
        string? modMarker = FindInstalledModMarker();
        bool    bepInExOk = gameDir != null &&
                            Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));

        // Install status
        panel.Children.Add(MakeHeader("INSTALL STATUS", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = gameDir != null
                ? "Risk of Rain Returns detected: " + gameDir
                : "Risk of Rain Returns not detected. Pick your install folder below, " +
                  "or install via Steam (appid 1337520).",
            FontSize = 11,
            Foreground = gameDir != null ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = bepInExOk
                ? "BepInEx found (BepInEx\\core present)."
                : "BepInEx not found yet — install BepInEx for Risk of Rain Returns first.",
            FontSize = 11,
            Foreground = bepInExOk ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modMarker != null
                ? "Archipelago mod detected: " + modMarker
                : "Archipelago mod not found in BepInEx\\plugins yet. Use Install on the Play tab.",
            FontSize = 11,
            Foreground = modMarker != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Folder picker
        panel.Children.Add(MakeHeader("GAME DIRECTORY", muted));
        string? overrideDir = LoadOverrideDir();
        var dirRow = new System.Windows.Controls.DockPanel
                     { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select your Risk of Rain Returns install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                    ? (overrideDir ?? gameDir!) : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() != true) return;
            string picked = dlg.FolderName;
            if (!LooksLikeGameDir(picked))
            {
                string nested = Path.Combine(picked, SteamCommonFolderName);
                if (LooksLikeGameDir(nested)) picked = nested;
            }
            SaveOverrideDir(picked);
            dirBox.Text = picked;
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Steam installs are detected automatically (appid 1337520). " +
                   "Use this picker only for a non-standard Steam library path.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 4, 0, 12),
        });

        // Connection note
        panel.Children.Add(MakeHeader("CONNECTING (entered in-game)", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After launching the game with the BepInEx mod installed, use the " +
                   "Archipelago lobby in-game to enter your server address, port, slot " +
                   "name, and password. The launcher cannot pre-fill these fields.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // Setup steps
        panel.Children.Add(MakeHeader("GUIDED SETUP", muted));
        foreach (string step in new[]
        {
            "1. Own Risk of Rain Returns on Steam (appid 1337520). Install it if you " +
               "have not already.",
            "2. Install BepInEx for Risk of Rain Returns. See the mod's GitHub page for " +
               "compatible BepInEx version and instructions (link below).",
            "3. Click Install / Update on the Play tab. The launcher downloads the " +
               "Archipelago mod from GitHub and extracts it into your BepInEx\\plugins folder.",
            "4. Launch the game (with BepInEx active). Open the Archipelago lobby, enter " +
               "your server URL, port, slot name, and password, then connect.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // Links
        panel.Children.Add(MakeHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("RoR Returns AP mod (GitHub) ↗", REPO_URL),
            ("Mod releases ↗",                REPO_URL + "/releases"),
            ("Setup Guide ↗",                 SetupGuideUrl),
            ("Archipelago Official ↗",        ArchipelagoSite),
            ("Risk of Rain Returns on Steam ↗",
                "https://store.steampowered.com/app/1337520/"),
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
                Foreground = linkClr,
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
            string json = await _http.GetStringAsync(GH_RELEASES, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("draft", out var dr) &&
                    dr.ValueKind == JsonValueKind.True) continue;

                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) &&
                    d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                string ver = el.TryGetProperty("tag_name", out var t)
                    ? NormalizeTag(t.GetString()) ?? "" : "";

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name", out var n)
                               ? n.GetString() ?? $"Release {ver}" : $"Release {ver}",
                    Body:    el.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                    Version: ver,
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u)
                               ? u.GetString() : REPO_URL + "/releases"
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — game directory resolution ───────────────────────────

    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeGameDir(ov)) return ov;
        try { return SteamLocator.FindGameDir(STEAM_APP_ID); }
        catch { return null; }
    }

    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(dir) &&
                   Directory.Exists(dir) &&
                   File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

    // ── Private helpers — mod install detection ───────────────────────────────

    private string? FindInstalledModMarker()
    {
        // Version stamp we wrote on a launcher-driven install.
        if (File.Exists(VersionFilePath)) return VersionFilePath;

        // Scan BepInEx\plugins under the detected game dir.
        try
        {
            string? game = ResolveGameDir();
            if (game == null) return null;
            string pluginsDir = Path.Combine(game, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(
                         pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                if (Path.GetFileNameWithoutExtension(dll)
                        .IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }

            foreach (string sub in Directory.EnumerateDirectories(
                         pluginsDir, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(sub)
                        .IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    if (Directory.EnumerateFiles(sub, "*.dll", SearchOption.AllDirectories).Any())
                        return sub;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        string? game = ResolveGameDir();
        string? exe  = game != null ? Path.Combine(game, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = game!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException(
                "Failed to start Risk of Rain Returns.");

            _gameProcess = proc;
            IsRunning    = true;
            try
            {
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) => { IsRunning = false; GameExited?.Invoke(proc.ExitCode); };
            }
            catch { }
            return;
        }

        // Steam URL fallback.
        try
        {
            Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
            IsRunning = true;
            return;
        }
        catch { }

        throw new FileNotFoundException(
            "Could not find \"Risk of Rain Returns.exe\". Open this game's Settings " +
            "and pick your install folder, or install Risk of Rain Returns via Steam " +
            "(appid 1337520).",
            GameExeName);
    }

    // ── Private helpers — download / extract mod ──────────────────────────────

    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    if (rel.TryGetProperty("draft", out var dr) &&
                        dr.ValueKind == JsonValueKind.True) continue;

                    string? version = rel.TryGetProperty("tag_name", out var t)
                        ? NormalizeTag(t.GetString()) : null;
                    if (version == null) continue;

                    if (!rel.TryGetProperty("assets", out var assets) ||
                        assets.ValueKind != JsonValueKind.Array) continue;

                    string? zipUrl = PickWindowsZip(assets);
                    if (zipUrl != null) return (version, zipUrl);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return (FallbackVersion, null);
    }

    private static string? PickWindowsZip(JsonElement assets)
    {
        string? winZip = null;
        string? anyZip = null;
        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;
            string lower = name.ToLowerInvariant();
            if (!lower.EndsWith(".zip")) continue;
            if (lower.Contains("source") || lower.Contains("linux") ||
                lower.Contains("ubuntu") || lower.Contains("mac") ||
                lower.Contains("darwin")) continue;
            anyZip ??= url;
            if (winZip == null &&
                (lower.Contains("win") || lower.Contains("x64") || lower.Contains("x86")))
                winZip = url;
        }
        return winZip ?? anyZip;
    }

    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string apModDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"rorr-ap-{version}-{Guid.NewGuid():N}.zip");
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"rorr-ap-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading Archipelago mod {version}..."));
            using (var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? -1;
                long downloaded = 0;
                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tempZip);
                var buf = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    downloaded += read;
                    if (total > 0)
                        progress.Report(((int)(10 + 55 * downloaded / total),
                            $"Downloading... {downloaded / 1000}KB"));
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting..."));
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            progress.Report((85, "Installing into BepInEx\\plugins..."));
            Directory.CreateDirectory(apModDir);
            CopyDirectoryContents(ResolvePluginPayloadRoot(tempDir), apModDir);
            progress.Report((95, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))    File.Delete(tempZip); }           catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static string ResolvePluginPayloadRoot(string extractedRoot)
    {
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(
                         extractedRoot, "plugins", SearchOption.AllDirectories))
            {
                string parent = Path.GetFileName(Path.GetDirectoryName(dir) ?? "");
                if (parent.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) &&
                    DirectoryHasDll(dir))
                    return dir;
            }
            string topPlugins = Path.Combine(extractedRoot, "plugins");
            if (Directory.Exists(topPlugins) && DirectoryHasDll(topPlugins))
                return topPlugins;
        }
        catch { }
        return extractedRoot;
    }

    private static bool DirectoryHasDll(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }

    private static void CopyDirectoryContents(string sourceDir, string destDir)
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

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..] : tag;
    }

    // ── Private helpers — settings sidecar ───────────────────────────────────

    private sealed class RoRRSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private RoRRSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<RoRRSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(RoRRSettings s)
    {
        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
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

    private static System.Windows.Controls.TextBlock MakeHeader(string text,
        System.Windows.Media.Brush color) => new()
    {
        Text = text, FontSize = 10,
        FontWeight = System.Windows.FontWeights.SemiBold,
        Foreground = color,
        Margin = new System.Windows.Thickness(0, 8, 0, 8),
    };
}
