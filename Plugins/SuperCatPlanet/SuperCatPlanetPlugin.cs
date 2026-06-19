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

namespace LauncherV2.Plugins.SuperCatPlanet;

// ═══════════════════════════════════════════════════════════════════════════════
// SuperCatPlanetPlugin — launch integration for "Super Cat Planet",
// a freeware precision platformer with a community AP world by lone01.
//
// ── VERIFIED FACTS (2026-06-19) ───────────────────────────────────────────────
//   * AP game string: "Super Cat Planet" (from worlds/__init__.py in the repo)
//   * COMMUNITY AP WORLD + GAME repo: github.com/lone01/scp
//   * The repo ships BOTH the Python apworld AND a modified version of the game
//     bundled with the randomizer — this is not a Steam game.
//   * ConnectsItself = true — the apworld uses launch_subprocess; the game
//     client connects to AP automatically when launched via the AP client or
//     when the bundled game binary starts with the right args.
//   * SupportsStandalone = false — the vanilla game can be downloaded separately
//     from its itch.io page, but the AP world ships its own patched binary.
//   * No Steam release. The game + apworld are downloaded directly from the
//     GitHub releases of lone01/scp.
//   * NOT in ArchipelagoMW/Archipelago main repo — community world only.
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. DETECT the Super Cat Planet AP install by looking for the game exe in
//      the user-set GameDirectory (or the default Games/SuperCatPlanet folder).
//   2. INSTALL/UPDATE: download the latest release zip from lone01/scp and
//      extract it into GameDirectory.
//   3. LAUNCH: run the game exe from GameDirectory.
//
// ── BUILD NOTE ────────────────────────────────────────────────────────────────
//   UseWindowsForms=true + UseWPF=true → all WPF types fully qualified (CS0104).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SuperCatPlanetPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GH_OWNER        = "lone01";
    private const string GH_REPO         = "scp";
    private const string GH_RELEASES_API =
        $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases/latest";
    private const string GH_RELEASES_URL =
        $"https://github.com/{GH_OWNER}/{GH_REPO}/releases";
    private const string REPO_URL        =
        $"https://github.com/{GH_OWNER}/{GH_REPO}";

    // The game exe bundled with the AP release (Godot-based)
    private const string GAME_EXE        = "SuperCatPlanet.exe";
    private const string FallbackVersion = "1.0.0";

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

    public string GameId       => "supercatplanet";
    public string DisplayName  => "Super Cat Planet";
    public string Subtitle     => "Free standalone · built-in AP client";
    public string ApWorldName  => "Super Cat Planet";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "supercatplanet.png");

    public string ThemeAccentColor => "#FF6B9D";   // cat-planet pink

    public string[] GameBadges => new[] { "Freeware · built-in AP client" };

    public string Description =>
        "Super Cat Planet is a freeware precision platformer where you navigate " +
        "treacherous cat-themed worlds. The community AP world by lone01 turns " +
        "every level completion and collectible into an Archipelago check. " +
        "The AP release bundles a patched game executable that connects directly " +
        "to the Archipelago server — no external client required. " +
        "The launcher downloads and installs the full game + AP integration automatically.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled => !string.IsNullOrWhiteSpace(GameDirectory) &&
                               File.Exists(Path.Combine(GameDirectory, GAME_EXE));
    public bool IsRunning   { get; private set; }

    public bool ConnectsItself    => true;
    public bool SupportsStandalone => false;

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "Games", "SuperCatPlanet");

    private string SidecarDir  => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SidecarPath => Path.Combine(SidecarDir, "supercatplanet_launcher.json");

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
        InstalledVersion = IsInstalled
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
        progress.Report((5, "Resolving latest Super Cat Planet AP release..."));
        var (version, zipUrl) = await ResolveLatestAsync(ct);
        AvailableVersion = version;

        if (string.IsNullOrWhiteSpace(zipUrl))
        {
            throw new InvalidOperationException(
                "Could not resolve the Super Cat Planet AP download URL. " +
                "Check your internet connection or download manually from: " +
                GH_RELEASES_URL);
        }

        Directory.CreateDirectory(GameDirectory);

        string tempZip = Path.Combine(Path.GetTempPath(),
            $"supercatplanet-{version}-{Guid.NewGuid():N}.zip");

        try
        {
            progress.Report((15, $"Downloading Super Cat Planet AP v{version}..."));
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
                        int pct = (int)(15 + 65 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading... {downloaded / 1024}KB / {total / 1024}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((82, "Extracting Super Cat Planet AP..."));
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, GameDirectory, overwriteFiles: true);

            WriteStampedVersion(version);
            InstalledVersion = version;

            progress.Report((100,
                $"Super Cat Planet AP v{version} installed. " +
                "Launch from the Play tab. The game will prompt for your AP " +
                "server, slot name, and password on startup."));
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

        // ── Status ─────────────────────────────────────────────────────────
        AddHeader(panel, "INSTALL STATUS", muted);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = IsInstalled
                ? "Super Cat Planet AP installed at: " + GameDirectory
                : "Not installed. Click the Install button on the Play tab to download " +
                  "the complete game + AP integration.",
            FontSize = 11, Foreground = IsInstalled ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Install folder ─────────────────────────────────────────────────
        AddHeader(panel, "INSTALL FOLDER", muted);

        var dirRow = new System.Windows.Controls.DockPanel
                     { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = GameDirectory,
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
            Content     = "Change...", Width = 90,
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
                Title = "Select Super Cat Planet AP install folder",
                InitialDirectory = Directory.Exists(GameDirectory)
                    ? GameDirectory : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
                SaveSidecarDir(dlg.FolderName);
            }
        };
        System.Windows.Controls.DockPanel.SetDock(browseBtn,
            System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(browseBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "The launcher installs the game here automatically. " +
                   "Change the folder only if you want to install elsewhere.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 4, 0, 12),
        });

        // ── Setup guide ────────────────────────────────────────────────────
        AddHeader(panel, "SETUP GUIDE", muted);
        foreach (string step in new[]
        {
            "1. Click the Install button on the Play tab. The launcher downloads " +
               "the Super Cat Planet AP package (game + randomizer) from GitHub " +
               "and extracts it automatically.",
            "2. Click Play. The game launches and asks for your Archipelago " +
               "server address, slot name, and password on the main screen.",
            "3. No external AP client is needed — the game connects directly.",
            "4. Make sure you also add the apworld file (from the same GitHub release) " +
               "to your Archipelago server's lib/worlds/ folder before generating a game.",
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
            ("Super Cat Planet AP GitHub ↗",   REPO_URL),
            ("Super Cat Planet AP releases ↗", GH_RELEASES_URL),
            ("Archipelago Official ↗",           "https://archipelago.gg"),
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
                    Title:   "Super Cat Planet AP " + tag,
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
                    if (name == null) continue;

                    // Accept any zip that looks like the game package
                    // (not the .apworld file, which is Python-side only)
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (name.EndsWith(".apworld", StringComparison.OrdinalIgnoreCase))
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
            $"v{FallbackVersion}/SuperCatPlanet-{FallbackVersion}.zip";
        return (FallbackVersion, fallbackUrl);
    }

    // ── Private helpers — game launch ─────────────────────────────────────────

    private void StartGame()
    {
        string exe = Path.Combine(GameDirectory, GAME_EXE);
        if (!File.Exists(exe))
        {
            // Try scanning for any .exe in the folder as a fallback
            string? found = null;
            try
            {
                foreach (string f in Directory.EnumerateFiles(
                    GameDirectory, "*.exe",
                    System.IO.SearchOption.TopDirectoryOnly))
                {
                    if (Path.GetFileNameWithoutExtension(f)
                            .IndexOf("cat", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        Path.GetFileNameWithoutExtension(f)
                            .IndexOf("scp",  StringComparison.OrdinalIgnoreCase) >= 0)
                    { found = f; break; }
                }
            }
            catch { }

            if (found == null)
            {
                IsRunning = false;
                GameExited?.Invoke(-1);
                throw new FileNotFoundException(
                    "Super Cat Planet executable not found. " +
                    "Use the Install button to download the game first.",
                    exe);
            }
            exe = found;
        }

        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
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
        }
        catch (Exception ex)
        {
            IsRunning = false;
            throw new InvalidOperationException(
                "Failed to launch Super Cat Planet.", ex);
        }
    }

    // ── Private helpers — sidecar ──────────────────────────────────────────────

    private sealed class ScpSettings
    {
        public string? InstallDir { get; set; }
        public string? ModVersion { get; set; }
    }

    private ScpSettings LoadSidecar()
    {
        try
        {
            if (File.Exists(SidecarPath))
            {
                string txt = File.ReadAllText(SidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<ScpSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSidecar(ScpSettings s)
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
