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

namespace LauncherV2.Plugins.Rayman2;

// ═══════════════════════════════════════════════════════════════════════════════
// Rayman2Plugin — launch integration for "Rayman 2: The Great Escape" (1999),
// played through the community AP world by Aeltumn.
//
// ── VERIFIED FACTS (2026-06-19) ───────────────────────────────────────────────
//   * AP game string: "Rayman 2" (from worlds/rayman2/__init__.py in Aeltumn's fork)
//   * AP WORLD repo:  github.com/Aeltumn/Archipelago (branch "rayman2",
//                     world at worlds/rayman2/)
//   * GAME MOD repo:  github.com/Aeltumn/Rayman2AP (DLL patch + connector)
//   * apworld file:   rayman2.apworld — shipped in Rayman2AP releases (v0.1.5).
//   * CONNECTION:     NOT ConnectsItself. A separate Rayman2APConnector.exe
//     handles the AP server connection; it is bundled with the game mod release.
//     The connector writes received items/locations into the game via DLL injection.
//   * SUPPORTED VERSION: GOG version ONLY. Ubisoft Connect and retail CD/DVD are
//     explicitly not supported by the mod author.
//   * NOT in ArchipelagoMW/Archipelago main repo — community world only.
//   * No Steam release for this version; the GOG installer places the game
//     wherever the user chooses. GameDirectory is user-set via Browse.
//
// ── BUILD NOTE ────────────────────────────────────────────────────────────────
//   UseWindowsForms=true + UseWPF=true → all WPF types fully qualified (CS0104).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Rayman2Plugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GH_OWNER        = "Aeltumn";
    private const string GH_REPO         = "Rayman2AP";
    private const string GH_RELEASES_API =
        $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases/latest";
    private const string GH_RELEASES_URL =
        $"https://github.com/{GH_OWNER}/{GH_REPO}/releases";
    private const string REPO_URL        =
        $"https://github.com/{GH_OWNER}/{GH_REPO}";
    private const string APWORLD_REPO_URL =
        "https://github.com/Aeltumn/Archipelago/tree/rayman2/worlds/rayman2";

    private const string CONNECTOR_EXE   = "Rayman2APConnector.exe";
    private const string GAME_EXE        = "Rayman2.exe";
    private const string FallbackVersion = "0.1.5";

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

    public string GameId       => "rayman2";
    public string DisplayName  => "Rayman 2: The Great Escape";
    public string Subtitle     => "Native PC · external AP connector";
    public string ApWorldName  => "Rayman 2";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "rayman2.png");

    public string ThemeAccentColor => "#F2A900";   // Rayman gold/orange

    public string[] GameBadges => new[] { "GOG · external connector" };

    public string Description =>
        "Rayman 2: The Great Escape, the classic 3D platformer from 1999, now playable " +
        "as an Archipelago multiworld game via the community mod by Aeltumn. " +
        "Lums, cages, and bosses become checks across the multiworld. " +
        "The mod uses a separate Rayman2APConnector.exe to bridge the game and the AP " +
        "server. Only the GOG version is supported — Ubisoft Connect and retail CD/DVD " +
        "are not compatible. You supply the GOG install; the launcher installs the mod.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled => FindConnectorExe() != null;
    public bool IsRunning   { get; private set; }

    public bool ConnectsItself    => false;
    public bool SupportsStandalone => false;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// User-set GOG install folder (set via Browse in Settings).
    public string GameDirectory { get; set; } = string.Empty;

    private string SidecarDir  => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SidecarPath => Path.Combine(SidecarDir, "rayman2_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;
    private Process? _connectorProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        InstalledVersion = FindConnectorExe() != null
            ? (ReadStampedVersion() ?? "installed") : null;
            try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(GH_OWNER, GH_REPO, ct));
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
        progress.Report((5, "Checking Rayman 2 install folder..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
        {
            throw new InvalidOperationException(
                "Rayman 2 game folder not set or not found. " +
                "Set the GOG install folder in the Settings panel first. " +
                "Note: only the GOG version is supported.");
        }

        progress.Report((15, "Resolving latest Rayman2AP release..."));
        var (version, zipUrl) = await ResolveLatestAsync(ct);
        AvailableVersion = version;

        if (string.IsNullOrWhiteSpace(zipUrl))
        {
            throw new InvalidOperationException(
                "Could not resolve the Rayman2AP download URL. " +
                "Check your internet connection or download manually from: " +
                GH_RELEASES_URL);
        }

        string tempZip = Path.Combine(Path.GetTempPath(),
            $"rayman2ap-{version}-{Guid.NewGuid():N}.zip");

        try
        {
            progress.Report((25, $"Downloading Rayman2AP v{version}..."));
            using (var resp = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total      = resp.Content.Headers.ContentLength ?? -1;
                long downloaded = 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tempZip);
                var buf = new byte[65536];
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    downloaded += read;
                    if (total > 0)
                    {
                        int pct = (int)(25 + 55 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading... {downloaded / 1024}KB / {total / 1024}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((82, "Extracting mod files into game folder..."));
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, gameDir, overwriteFiles: true);

            WriteStampedVersion(version);
            InstalledVersion = version;

            progress.Report((100,
                $"Rayman2AP v{version} installed. " +
                "Launch the connector and game via the Play tab. " +
                "Enter your AP server, slot, and password in the Rayman2APConnector window."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
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
        StartGameAndConnector(session);
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartGameAndConnector(null);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); }      catch { }
        try { _connectorProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning         = false;
        _gameProcess      = null;
        _connectorProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (external connector owns the session) ───────────────

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

        string? gameDir   = ResolveGameDir();
        string? connector = FindConnectorExe();

        // ── Version notice ─────────────────────────────────────────────────
        AddHeader(panel, "IMPORTANT — GOG ONLY", muted);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Only the GOG version of Rayman 2 is supported by this mod. " +
                "The Ubisoft Connect version and retail CD/DVD are NOT compatible. " +
                "Purchase the GOG version if you do not already own it.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Status ─────────────────────────────────────────────────────────
        AddHeader(panel, "INSTALL STATUS", muted);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = gameDir != null
                ? "Game folder set: " + gameDir
                : "Game folder not set. Use the Browse button below to locate your GOG Rayman 2 folder.",
            FontSize = 11, Foreground = gameDir != null ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = connector != null
                ? "Rayman2APConnector found: " + connector
                : "Rayman2APConnector.exe not found. Use the Install button on the Play tab.",
            FontSize = 11, Foreground = connector != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Folder browse ──────────────────────────────────────────────────
        AddHeader(panel, "GAME FOLDER (GOG INSTALL)", muted);

        var dirRow = new System.Windows.Controls.DockPanel
                     { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = gameDir ?? GameDirectory,
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
            Content     = "Browse...", Width = 90,
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
                Title = "Select Rayman 2 (GOG) install folder",
                InitialDirectory = Directory.Exists(gameDir ?? "") ? gameDir! : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                SaveSidecarDir(dlg.FolderName);
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(browseBtn,
            System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(browseBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Point the Browse button at your GOG Rayman 2 install folder " +
                   "(the folder containing Rayman2.exe).",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 4, 0, 12),
        });

        // ── Setup guide ────────────────────────────────────────────────────
        AddHeader(panel, "SETUP GUIDE", muted);
        foreach (string step in new[]
        {
            "1. Own Rayman 2: The Great Escape on GOG. Install it.",
            "2. Use the Browse button above to point the launcher at your GOG install folder.",
            "3. Click Install on the Play tab. The launcher downloads the Rayman2AP mod " +
               "and extracts it into the game folder.",
            "4. Click Play. The game and Rayman2APConnector.exe both launch. " +
               "Enter your AP server, slot, and password in the connector window.",
            "5. Start a new game in Rayman 2 after the connector shows Connected.",
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
            ("Rayman2AP GitHub (game mod) ↗",    REPO_URL),
            ("Rayman2AP releases ↗",              GH_RELEASES_URL),
            ("AP world source (Aeltumn fork) ↗",  APWORLD_REPO_URL),
            ("Archipelago Official ↗",             "https://archipelago.gg"),
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
                    Title:   "Rayman2AP " + tag,
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

    private async Task<(string Version, string? ZipUrl)> ResolveLatestAsync(CancellationToken ct)
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
                    // Accept any zip from this release
                    if (name == null ||
                        !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
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

        // Pinned fallback
        string fallbackUrl =
            $"https://github.com/{GH_OWNER}/{GH_REPO}/releases/download/" +
            $"v{FallbackVersion}/Rayman2AP-v{FallbackVersion}.zip";
        return (FallbackVersion, fallbackUrl);
    }

    // ── Private helpers — dir resolution ──────────────────────────────────────

    private string? ResolveGameDir()
    {
        string? ov = LoadSidecarDir();
        if (!string.IsNullOrWhiteSpace(ov) && LooksLikeRayman2Dir(ov)) return ov;
        if (!string.IsNullOrWhiteSpace(GameDirectory) && LooksLikeRayman2Dir(GameDirectory))
            return GameDirectory;
        return null;
    }

    private static bool LooksLikeRayman2Dir(string dir)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) &&
                   File.Exists(Path.Combine(dir, GAME_EXE));
        }
        catch { return false; }
    }

    private string? FindConnectorExe()
    {
        string? gameDir = ResolveGameDir();
        if (gameDir == null) return null;
        string exe = Path.Combine(gameDir, CONNECTOR_EXE);
        return File.Exists(exe) ? exe : null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGameAndConnector(ApSession? session)
    {
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
        {
            throw new InvalidOperationException(
                "Rayman 2 folder not set. Open Settings and use the Browse button " +
                "to locate your GOG Rayman 2 install.");
        }

        string gameExe      = Path.Combine(gameDir, GAME_EXE);
        string connectorExe = Path.Combine(gameDir, CONNECTOR_EXE);

        if (!File.Exists(gameExe))
        {
            throw new FileNotFoundException(
                "Rayman2.exe not found in the configured folder. " +
                "Check the game folder in Settings.", gameExe);
        }

        if (!File.Exists(connectorExe))
        {
            throw new FileNotFoundException(
                "Rayman2APConnector.exe not found. " +
                "Use the Install button on the Play tab to download it.", connectorExe);
        }

        // Launch the connector first (it needs to be running before the game reads AP data)
        try
        {
            var connectorPsi = new ProcessStartInfo
            {
                FileName         = connectorExe,
                WorkingDirectory = gameDir,
                UseShellExecute  = true,
            };
            _connectorProcess = Process.Start(connectorPsi);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Failed to start Rayman2APConnector.exe.", ex);
        }

        // Then launch the game
        try
        {
            var gamePsi = new ProcessStartInfo
            {
                FileName         = gameExe,
                WorkingDirectory = gameDir,
                UseShellExecute  = true,
            };
            var proc = Process.Start(gamePsi);
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
        }
        catch (Exception ex)
        {
            IsRunning = false;
            throw new InvalidOperationException("Failed to start Rayman2.exe.", ex);
        }
    }

    // ── Private helpers — sidecar ──────────────────────────────────────────────

    private sealed class Rayman2Settings
    {
        public string? InstallDir  { get; set; }
        public string? ModVersion  { get; set; }
    }

    private Rayman2Settings LoadSidecar()
    {
        try
        {
            if (File.Exists(SidecarPath))
            {
                string txt = File.ReadAllText(SidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Rayman2Settings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSidecar(Rayman2Settings s)
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
        string? p = LoadSidecar().InstallDir;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }

    private void SaveSidecarDir(string p)
    {
        var s = LoadSidecar(); s.InstallDir = p; SaveSidecar(s);
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
