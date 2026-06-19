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

namespace LauncherV2.Plugins.PlacidPlasticDuckSimulator;

// ═══════════════════════════════════════════════════════════════════════════════
// PlacidPlasticDuckSimulatorPlugin — launch integration for
// "Placid Plastic Duck Simulator" (Turbolento Games, 2022), played through
// the community AP world by SWCreeperKing (PPDSArchipelago).
//
// ── VERIFIED FACTS (2026-06-19) ───────────────────────────────────────────────
//   * COMMUNITY AP WORLD repo: github.com/SWCreeperKing/PPDSArchipelago
//   * AP game string: "Placid Plastic Duck Simulator" (from __init__.py)
//   * apworld file: placidplasticducksim.apworld (latest v0.3.2)
//   * GAME MOD: Duckipelago — a MelonLoader mod (Duckipelago.dll) from the same
//     repo. Provides the in-game AP connection UI and item/location bridge.
//   * ConnectsItself = true — the MelonLoader mod speaks directly to the AP server.
//   * SupportsStandalone = true — the game runs fine without the mod.
//   * STEAM APPID: 1999360 (the base game; 1794680 is a DLC/bundle variant).
//   * MelonLoader must be installed into the PPDS game folder first (the mod is
//     a .dll drop into <PPDS>\Mods\). The launcher installs Duckipelago.dll; the
//     user must install MelonLoader separately.
//   * NOT in ArchipelagoMW/Archipelago main repo — community world only.
//
// ── BUILD NOTE ────────────────────────────────────────────────────────────────
//   UseWindowsForms=true + UseWPF=true → all WPF types fully qualified (CS0104).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PlacidPlasticDuckSimulatorPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const int    SteamAppId      = 1999360;
    private const string GH_OWNER        = "SWCreeperKing";
    private const string GH_REPO         = "PPDSArchipelago";
    private const string GH_RELEASES_API =
        $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases/latest";
    private const string GH_RELEASES_URL =
        $"https://github.com/{GH_OWNER}/{GH_REPO}/releases";
    private const string REPO_URL        =
        $"https://github.com/{GH_OWNER}/{GH_REPO}";

    private const string MOD_DLL      = "Duckipelago.dll";
    private const string STEAM_EXE    = "Placid Plastic Duck Simulator.exe";
    private const string FallbackVersion = "0.3.2";

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

    public string GameId       => "placidplasticducksimulator";
    public string DisplayName  => "Placid Plastic Duck Simulator";
    public string Subtitle     => "Native PC · MelonLoader mod";
    public string ApWorldName  => "Placid Plastic Duck Simulator";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "placidplasticducksimulator.png");

    public string ThemeAccentColor => "#F7C948";   // duck yellow

    public string[] GameBadges => new[] { "Steam · MelonLoader mod" };

    public string Description =>
        "Placid Plastic Duck Simulator lets you watch ducks float serenely across " +
        "scenic waterways — now as an Archipelago multiworld game. The community AP " +
        "world by SWCreeperKing adds randomized checks and items across the duck " +
        "pool. The MelonLoader mod Duckipelago connects the game directly to the " +
        "Archipelago server in-game. You bring your own copy of PPDS (Steam); " +
        "the launcher installs the Duckipelago mod after MelonLoader is set up.";

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
    private string SidecarPath => Path.Combine(SidecarDir, "ppds_launcher.json");

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
        progress.Report((5, "Locating Placid Plastic Duck Simulator install..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
        {
            throw new InvalidOperationException(
                "Placid Plastic Duck Simulator install not found. " +
                "Install the game via Steam (appid 1999360) or set the folder " +
                "manually in the Settings panel.");
        }

        if (!Directory.Exists(Path.Combine(gameDir, "MelonLoader")))
        {
            throw new InvalidOperationException(
                "MelonLoader is not installed in the PPDS game folder. " +
                "Download and run the MelonLoader installer, point it at your " +
                "Placid Plastic Duck Simulator folder, then launch the game once " +
                "to create the Mods\\ folder, and try again.");
        }

        string modsDir = Path.Combine(gameDir, "Mods");
        Directory.CreateDirectory(modsDir);

        progress.Report((15, "Resolving latest Duckipelago release..."));
        var (version, dllUrl) = await ResolveLatestAsync(ct);
        AvailableVersion = version;

        if (string.IsNullOrWhiteSpace(dllUrl))
        {
            throw new InvalidOperationException(
                "Could not resolve the Duckipelago download URL. " +
                "Check your internet connection or download manually from: " +
                GH_RELEASES_URL);
        }

        progress.Report((25, $"Downloading {MOD_DLL} v{version}..."));
        string tempPath = Path.Combine(Path.GetTempPath(),
            $"duckipelago-{version}-{Guid.NewGuid():N}.dll");

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
            progress.Report((88, "Installing Duckipelago.dll into Mods folder..."));
            File.Copy(tempPath, destDll, overwrite: true);

            WriteStampedVersion(version);
            InstalledVersion = version;

            progress.Report((100,
                $"Duckipelago v{version} installed successfully. " +
                "Launch the game and use the MelonLoader mod menu to open the " +
                "Archipelago connection panel and enter your server, slot, and password."));
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
        bool    mlLoaded = gameDir != null &&
                           Directory.Exists(Path.Combine(gameDir, "MelonLoader"));

        // ── Status block ───────────────────────────────────────────────────
        AddHeader(panel, "INSTALL STATUS", muted);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = gameDir != null
                ? "Game detected: " + gameDir
                : "Game not found — install via Steam (appid 1999360) or set folder below.",
            FontSize = 11, Foreground = gameDir != null ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = mlLoaded
                ? "MelonLoader detected."
                : "MelonLoader not detected. Install it first (see Links below), " +
                  "then launch the game once to create the Mods\\ folder.",
            FontSize = 11, Foreground = mlLoaded ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = dll != null
                ? "Duckipelago mod installed: " + dll
                : "Duckipelago.dll not found — use the Install button on the Play tab.",
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
                Title = "Select Placid Plastic Duck Simulator install folder",
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
            Text = "Steam installs are detected automatically (appid 1999360). " +
                   "Use the folder picker only if the game is not found above.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 4, 0, 12),
        });

        // ── Setup guide ────────────────────────────────────────────────────
        AddHeader(panel, "SETUP GUIDE (first time)", muted);
        foreach (string step in new[]
        {
            "1. Own Placid Plastic Duck Simulator on Steam (appid 1999360).",
            "2. Download and run the MelonLoader installer. " +
               "Point it at your PPDS game folder and install MelonLoader.",
            "3. Launch PPDS once after MelonLoader install — it creates the Mods\\ folder. " +
               "Close the game.",
            "4. Click Install on the Play tab. The launcher downloads Duckipelago.dll " +
               "and places it in <PPDS>\\Mods\\.",
            "5. Launch the game. Open the MelonLoader mod menu to find the Archipelago " +
               "panel, then enter your server, slot, and password to connect.",
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
            ("PPDSArchipelago GitHub ↗",   REPO_URL),
            ("PPDSArchipelago releases ↗", GH_RELEASES_URL),
            ("MelonLoader installer ↗",    "https://melonwiki.xyz/#/"),
            ("Archipelago Official ↗",      "https://archipelago.gg"),
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
                $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases", ct);
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
                    Title:   "PPDS AP " + tag,
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
            string json = await _http.GetStringAsync(GH_RELEASES_API, ct);
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
            $"https://github.com/{GH_OWNER}/{GH_REPO}/releases/download/" +
            $"v{FallbackVersion}/{MOD_DLL}";
        return (FallbackVersion, fallbackUrl);
    }

    // ── Private helpers — game dir resolution ─────────────────────────────────

    private string? ResolveGameDir()
    {
        string? ov = LoadSidecarDir();
        if (!string.IsNullOrWhiteSpace(ov) && LooksLikePPDSDir(ov)) return ov;
        string? steam = SteamLocator.FindGameDir(SteamAppId);
        return steam != null && LooksLikePPDSDir(steam) ? steam : null;
    }

    private static bool LooksLikePPDSDir(string dir)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) &&
                   File.Exists(Path.Combine(dir, STEAM_EXE));
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
        string? exe     = gameDir != null ? Path.Combine(gameDir, STEAM_EXE) : null;

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
            "Could not find Placid Plastic Duck Simulator. " +
            "Install it via Steam (appid 1999360) or set the folder in Settings.",
            STEAM_EXE);
    }

    // ── Private helpers — sidecar ──────────────────────────────────────────────

    private sealed class PpdsSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private PpdsSettings LoadSidecar()
    {
        try
        {
            if (File.Exists(SidecarPath))
            {
                string txt = File.ReadAllText(SidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<PpdsSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSidecar(PpdsSettings s)
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
