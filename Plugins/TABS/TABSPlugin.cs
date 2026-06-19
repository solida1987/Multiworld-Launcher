using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

// NOTE on type qualification (BUILD GOTCHA — CS0104):
// This project sets BOTH <UseWPF>true</UseWPF> and <UseWindowsForms>true</UseWindowsForms>.
// WPF UI types are FULLY QUALIFIED below to avoid CS0104. Do NOT add
// `using System.Windows.Controls;` or `using System.Windows.Media;` here.

namespace LauncherV2.Plugins.TABS;

// ═══════════════════════════════════════════════════════════════════════════════
// TABSPlugin — launch integration for "Totally Accurate Battle Simulator"
// (Landfall Games, 2021), played through the community AP world by duckboycool.
//
// ── VERIFIED FACTS (2026-06-19) ───────────────────────────────────────────────
//   * AP game string: "Totally Accurate Battle Simulator"
//     (confirmed from worlds/__init__.py and archipelago.json in the repo)
//   * APWORLD repo:   github.com/duckboycool/TABS-Archipelago
//   * MOD repo:       github.com/duckboycool/TABS_AP_Plugin (C# MelonLoader mod)
//   * apworld file:   tabs.apworld — released in the TABS-Archipelago repo
//   * GAME MOD:       TABS_AP_Plugin.dll — a MelonLoader mod placed in
//     <TABS>\Mods\. It adds an in-game Archipelago connection panel.
//   * ConnectsItself = true — the MelonLoader mod owns the AP slot.
//   * SupportsStandalone = true — TABS runs normally without the mod.
//   * STEAM APPID: 508440 (correct — confirmed).
//   * Minimum AP version required: 0.6.4.
//   * MelonLoader must be installed into TABS first (the mod is a .dll drop).
//   * NOT in ArchipelagoMW/Archipelago main repo — community world only.
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. DETECT TABS via SteamLocator (appid 508440); manual folder override stored
//      in a sidecar json.
//   2. CHECK for MelonLoader (version.dll or MelonLoader\ folder in game dir).
//   3. INSTALL/UPDATE: download the TABS_AP_Plugin.dll from the latest GitHub
//      release of duckboycool/TABS_AP_Plugin and place it in <TABS>\Mods\.
//   4. LAUNCH: run TABS.exe or fall back to steam://rungameid/508440.
//
// ── BUILD NOTE ────────────────────────────────────────────────────────────────
//   UseWindowsForms=true + UseWPF=true → all WPF types fully qualified (CS0104).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class TABSPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const int    SteamAppId      = 508440;
    private const string GH_OWNER        = "duckboycool";
    private const string GH_MOD_REPO     = "TABS_AP_Plugin";
    private const string GH_WORLD_REPO   = "TABS-Archipelago";

    private const string GH_MOD_API =
        $"https://api.github.com/repos/{GH_OWNER}/{GH_MOD_REPO}/releases/latest";
    private const string GH_MOD_URL =
        $"https://github.com/{GH_OWNER}/{GH_MOD_REPO}/releases";
    private const string GH_WORLD_URL =
        $"https://github.com/{GH_OWNER}/{GH_WORLD_REPO}";

    private const string MOD_DLL         = "TABS_AP_Plugin.dll";
    private const string GAME_EXE        = "TABS.exe";
    private const string FallbackVersion = "1.0.0";

    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(15),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Archipelago-Launcher/2.0" },
            { "Accept",     "application/vnd.github+json" },
        }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId       => "tabs";
    public string DisplayName  => "Totally Accurate Battle Simulator";
    public string Subtitle     => "Native PC · MelonLoader mod";
    public string ApWorldName  => "Totally Accurate Battle Simulator";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "tabs.png");

    public string ThemeAccentColor => "#00BFFF";   // TABS blue/cyan

    public string[] GameBadges => new[] { "Steam · MelonLoader mod" };

    public string Description =>
        "Totally Accurate Battle Simulator, the physics-based auto-battler from " +
        "Landfall Games, transformed into an Archipelago multiworld game by duckboycool. " +
        "Winning battles and unlocking units become checks across the multiworld. " +
        "The MelonLoader mod TABS_AP_Plugin connects the game directly to the " +
        "Archipelago server in-game. You bring your own copy of TABS (Steam appid 508440); " +
        "the launcher installs the mod after MelonLoader is set up.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled => FindInstalledDll() != null;
    public bool IsRunning   { get; private set; }

    public bool ConnectsItself    => true;
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; } = string.Empty;

    private string SidecarDir  => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SidecarPath => Path.Combine(SidecarDir, "tabs_launcher.json");

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
        InstalledVersion = FindInstalledDll() != null
            ? (ReadStampedVersion() ?? "installed") : null;
        try
        {
            var (ver, _) = await ResolveLatestAsync(ct);
            AvailableVersion = ver;
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((5, "Locating Totally Accurate Battle Simulator install..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
        {
            throw new InvalidOperationException(
                "Totally Accurate Battle Simulator install not found. " +
                "Install TABS via Steam (appid 508440) or set the folder " +
                "manually in the Settings panel.");
        }

        bool mlDetected = IsMelonLoaderInstalled(gameDir);
        if (!mlDetected)
        {
            throw new InvalidOperationException(
                "MelonLoader is not installed in the TABS game folder. " +
                "Download and run the MelonLoader installer (see Settings panel), " +
                "point it at your TABS folder, and launch TABS once to create " +
                "the Mods\\ folder before installing this mod.");
        }

        string modsDir = Path.Combine(gameDir, "Mods");
        Directory.CreateDirectory(modsDir);

        progress.Report((15, "Resolving latest TABS_AP_Plugin release..."));
        var (version, dllUrl) = await ResolveLatestAsync(ct);
        AvailableVersion = version;

        if (string.IsNullOrWhiteSpace(dllUrl))
        {
            throw new InvalidOperationException(
                "Could not resolve the TABS_AP_Plugin download URL. " +
                "Check your internet connection or download manually from: " +
                GH_MOD_URL);
        }

        progress.Report((25, $"Downloading {MOD_DLL} v{version}..."));
        string tempPath = Path.Combine(Path.GetTempPath(),
            $"tabsap-{version}-{Guid.NewGuid():N}.dll");

        try
        {
            using (var resp = await _http.GetAsync(
                dllUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total      = resp.Content.Headers.ContentLength ?? -1;
                long downloaded = 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tempPath);
                var buf = new byte[65536];
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    downloaded += read;
                    if (total > 0)
                    {
                        int pct = (int)(25 + 60 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading... {downloaded / 1024}KB / {total / 1024}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            string destDll = Path.Combine(modsDir, MOD_DLL);
            progress.Report((88, "Installing TABS_AP_Plugin.dll into Mods folder..."));
            File.Copy(tempPath, destDll, overwrite: true);

            WriteStampedVersion(version);
            InstalledVersion = version;

            progress.Report((100,
                $"TABS_AP_Plugin v{version} installed. " +
                "Launch the game from the Play tab. Open the in-game mod menu " +
                "to find the Archipelago panel and enter your server, slot, and password."));
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
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
        var muted   = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var warn    = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var success = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var linkClr = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
                    { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        string? gameDir  = ResolveGameDir();
        string? dll      = FindInstalledDll();
        bool    mlLoaded = gameDir != null && IsMelonLoaderInstalled(gameDir);

        // ── Status ─────────────────────────────────────────────────────────
        AddHeader(panel, "INSTALL STATUS", muted);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = gameDir != null
                ? "TABS detected: " + gameDir
                : "TABS not found — install via Steam (appid 508440) or set folder below.",
            FontSize = 11, Foreground = gameDir != null ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = mlLoaded
                ? "MelonLoader detected."
                : "MelonLoader not detected. Install MelonLoader into your TABS folder first " +
                  "(see MelonLoader link below), then launch TABS once.",
            FontSize = 11, Foreground = mlLoaded ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = dll != null
                ? "TABS_AP_Plugin mod installed: " + dll
                : "TABS_AP_Plugin.dll not found — use the Install button on the Play tab.",
            FontSize = 11, Foreground = dll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Folder override ────────────────────────────────────────────────
        AddHeader(panel, "GAME FOLDER OVERRIDE", muted);
        string? sidecarDir = LoadSidecarDir();

        var dirRow = new System.Windows.Controls.DockPanel
                     { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = sidecarDir ?? gameDir ?? string.Empty,
            IsReadOnly  = true, FontSize = 11,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var browseBtn = new System.Windows.Controls.Button
        {
            Content     = "Select folder...", Width = 120,
            Padding     = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        browseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Totally Accurate Battle Simulator install folder",
                InitialDirectory = Directory.Exists(sidecarDir ?? gameDir ?? "")
                    ? (sidecarDir ?? gameDir!) : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                SaveSidecarDir(dlg.FolderName);
                dirBox.Text = dlg.FolderName;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(browseBtn,
            System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(browseBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Steam installs are detected automatically (appid 508440). " +
                   "Use the folder picker for non-standard installs.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 4, 0, 12),
        });

        // ── Setup guide ────────────────────────────────────────────────────
        AddHeader(panel, "SETUP GUIDE (first time)", muted);
        foreach (string step in new[]
        {
            "1. Own Totally Accurate Battle Simulator on Steam (appid 508440).",
            "2. Download and run the MelonLoader installer. " +
               "Point it at your TABS game folder and install MelonLoader.",
            "3. Launch TABS once after MelonLoader install — it creates the Mods\\ folder. " +
               "Close the game.",
            "4. Click Install on the Play tab. The launcher downloads TABS_AP_Plugin.dll " +
               "and places it in <TABS>\\Mods\\.",
            "5. Launch TABS from the Play tab. Open the in-game mod settings menu to find " +
               "the Archipelago panel and enter your server, slot, and password.",
            "6. Make sure the TABS apworld (tabs.apworld) is installed in your AP server's " +
               "lib/worlds/ folder. Download it from the TABS-Archipelago GitHub releases.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ──────────────────────────────────────────────────────────
        AddHeader(panel, "LINKS", muted);
        foreach (var (label, url) in new[]
        {
            ("TABS_AP_Plugin GitHub (game mod) ↗",  $"https://github.com/{GH_OWNER}/{GH_MOD_REPO}"),
            ("TABS_AP_Plugin releases ↗",            GH_MOD_URL),
            ("TABS-Archipelago (apworld) ↗",         GH_WORLD_URL),
            ("MelonLoader installer ↗",              "https://melonwiki.xyz/#/"),
            ("Archipelago Official ↗",                "https://archipelago.gg"),
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
                Foreground          = linkClr,
                Cursor              = System.Windows.Input.Cursors.Hand,
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
            string json = await _http.GetStringAsync(
                $"https://api.github.com/repos/{GH_OWNER}/{GH_MOD_REPO}/releases", ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) &&
                    d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                string tag = el.TryGetProperty("tag_name", out var t)
                             ? (t.GetString() ?? "") : "";
                items.Add(new NewsItem(
                    Title:   "TABS AP Plugin " + tag,
                    Body:    el.TryGetProperty("body",     out var b) ? (b.GetString() ?? "") : "",
                    Version: tag.TrimStart('v'),
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    private async Task<(string Version, string? DllUrl)> ResolveLatestAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_API, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string tag = root.TryGetProperty("tag_name", out var t)
                         ? (t.GetString() ?? FallbackVersion) : FallbackVersion;
            string ver = tag.TrimStart('v');

            if (root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var n)
                                   ? n.GetString() : null;
                    if (!string.Equals(name, MOD_DLL, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string? url = asset.TryGetProperty("browser_download_url", out var u)
                                  ? u.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(url))
                        return (ver, url);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        string fallbackUrl =
            $"https://github.com/{GH_OWNER}/{GH_MOD_REPO}/releases/download/" +
            $"v{FallbackVersion}/{MOD_DLL}";
        return (FallbackVersion, fallbackUrl);
    }

    // ── Private helpers — game dir resolution ─────────────────────────────────

    private string? ResolveGameDir()
    {
        string? ov = LoadSidecarDir();
        if (!string.IsNullOrWhiteSpace(ov) && LooksLikeTABSDir(ov)) return ov;
        string? steam = SteamLocator.FindGameDir(SteamAppId);
        return steam != null && LooksLikeTABSDir(steam) ? steam : null;
    }

    private static bool LooksLikeTABSDir(string dir)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) &&
                   File.Exists(Path.Combine(dir, GAME_EXE));
        }
        catch { return false; }
    }

    private static bool IsMelonLoaderInstalled(string gameDir)
    {
        try
        {
            // MelonLoader presence: version.dll doorstop proxy OR MelonLoader\ folder
            return File.Exists(Path.Combine(gameDir, "version.dll")) ||
                   Directory.Exists(Path.Combine(gameDir, "MelonLoader"));
        }
        catch { return false; }
    }

    private string? FindInstalledDll()
    {
        string? gameDir = ResolveGameDir();
        if (gameDir == null) return null;
        string dll = Path.Combine(gameDir, "Mods", MOD_DLL);
        return File.Exists(dll) ? dll : null;
    }

    // ── Private helpers — game launch ─────────────────────────────────────────

    private void StartGame()
    {
        string? gameDir = ResolveGameDir();
        string? exe     = gameDir != null ? Path.Combine(gameDir, GAME_EXE) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = gameDir!,
                UseShellExecute  = true,
            });
            _gameProcess = proc;
            IsRunning    = true;
            if (proc != null)
            {
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
            }
            return;
        }

        // Steam fallback
        try
        {
            Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
            IsRunning = true;
            return;
        }
        catch { }

        IsRunning = false;
        throw new FileNotFoundException(
            "Could not find TABS.exe. " +
            "Install Totally Accurate Battle Simulator via Steam (appid 508440) " +
            "or set the folder in Settings.",
            GAME_EXE);
    }

    // ── Private helpers — sidecar ──────────────────────────────────────────────

    private sealed class TabsSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private TabsSettings LoadSidecar()
    {
        try
        {
            if (File.Exists(SidecarPath))
            {
                string txt = File.ReadAllText(SidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<TabsSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSidecar(TabsSettings s)
    {
        try
        {
            Directory.CreateDirectory(SidecarDir);
            File.WriteAllText(SidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(false));
        }
        catch { }
    }

    private string? LoadSidecarDir()
    {
        string? p = LoadSidecar().InstallOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }

    private void SaveSidecarDir(string p)
    {
        var s = LoadSidecar(); s.InstallOverride = p; SaveSidecar(s);
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSidecar().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSidecar(); s.ModVersion = v; SaveSidecar(s);
    }

    // ── Private helpers — UI ──────────────────────────────────────────────────

    private static void AddHeader(
        System.Windows.Controls.StackPanel panel,
        string text,
        System.Windows.Media.Brush color)
    {
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = text,
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = color,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });
    }
}
