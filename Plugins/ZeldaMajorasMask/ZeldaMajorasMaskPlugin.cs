using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

// NOTE on type qualification (BUILD GOTCHA — CS0104):
// This project sets BOTH <UseWPF>true</UseWPF> and <UseWindowsForms>true</UseWindowsForms>.
// That makes a long list of simple type names ambiguous between WPF and WinForms.
// To avoid CS0104 this file writes every WPF UI type FULLY QUALIFIED
// (System.Windows.Controls.*, System.Windows.Media.*, …) and adds NO file-level
// `using X = System.Windows...;` alias (CS1537). Bare names from GlobalUsings only.

namespace LauncherV2.Plugins.ZeldaMajorasMask;

// ═══════════════════════════════════════════════════════════════════════════════
// ZeldaMajorasMaskPlugin — install / guide / launch for
// "The Legend of Zelda: Majora's Mask" via the RecompRando + Archipelago integration.
//
// ── HONEST REALITY CHECK (2026-06-16) ────────────────────────────────────────
//
//   * AP WORLD — community apworld from RecompRando/MMRecompRando.
//     AP game string: "Majora's Mask" (inferred from the project name; verify
//     against worlds/__init__.py / archipelago.json when integrating).
//     This is a COMMUNITY apworld / standalone PC native port — NOT in AP main.
//
//   * PLATFORM — This is a PC NATIVE port, NOT an N64 emulator game. "Zelda 64:
//     Recompiled" (N64Recomp) is a native PC recompilation of the Majora's Mask
//     N64 ROM that runs on modern hardware at native speed, with wide-screen and
//     HD texture support. RecompRando is the Archipelago randomizer built on top
//     of the Zelda64 Recompiled infrastructure. The player provides their own
//     legally-owned Majora's Mask N64 ROM (USA 1.0, z64 format) to build the
//     recompiled port. ConnectsItself = true (the recompiled game or its bundled
//     client speaks to the AP server directly).
//
//   * OFFICIAL REPO — RecompRando/MMRecompRando on GitHub.
//     Release assets: zip/installer for Windows; exact asset names not verified
//     offline — best-effort heuristic download.
//
//   * NO STEAM APPID — This is a standalone PC native port, not a Steam game.
//     GameDirectory = string.Empty (no detection possible without the user's
//     chosen install location). Install path override via Settings picker is the
//     primary flow.
//
//   * CONNECTION — The RecompRando AP client speaks to the AP server directly from
//     within the game (in-game menu or config). ConnectsItself = true.
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. Offer download/install from RecompRando/MMRecompRando GitHub releases.
//   2. Let the user set their install path via Settings.
//   3. Launch the recompiled game executable on Play.
//   4. Guide the player through ROM-provisioning and setup honestly.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ZeldaMajorasMaskPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER = "RecompRando";
    private const string GITHUB_REPO  = "MMRecompRando";

    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string RepoUrl =
        $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string ReleasesUrl =
        $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string ArchipelagoSite = "https://archipelago.gg";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // The preferred exe name inside the install (exact name may vary by release).
    private const string PreferredExeName = "Zelda64Recompiled.exe";

    // ── IGamePlugin — Identity ─────────────────────────────────────────────────

    public string GameId      => "majoras_mask_recomp";
    public string DisplayName => "Zelda: Majora's Mask";
    public string Subtitle    => "PC Native (RecompRando) · Archipelago";
    public string ApWorldName => "Majora's Mask";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "majoras_mask.png");

    public string ThemeAccentColor => "#4A1E7E";   // Majora's mask purple
    public string[] GameBadges     => new[] { "Requires MM N64 ROM", "PC Native Port" };

    public string Description =>
        "The Legend of Zelda: Majora's Mask is Nintendo's haunting 2000 Nintendo 64 " +
        "adventure — Link has three days to stop the moon from crashing into Termina. " +
        "RecompRando is a native PC recompilation of Majora's Mask built on the " +
        "Zelda64 Recompiled technology, running natively at full speed with modern " +
        "rendering. In the Archipelago integration, items and checks across Termina " +
        "join the multiworld pool. You provide your own legally-obtained Majora's Mask " +
        "N64 ROM (USA v1.0, .z64); RecompRando builds the native port from it. " +
        "Source: github.com/RecompRando/MMRecompRando.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ──────────────────────────────────────────────────────────
    public string? InstalledVersion => ReadInstalledVersion();
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled => ResolveGameExe() != null;
    public bool IsRunning   { get; private set; }

    /// No Steam detection possible for a native port — use Settings folder picker.
    public string GameDirectory { get; set; } = string.Empty;

    // ── Paths ──────────────────────────────────────────────────────────────────

    private string InstallDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "MMRecompRando");
    private string VersionFile
        => Path.Combine(InstallDir, "mmrecomp_ap_version.dat");

    private Process? _gameProcess;

    // ── AP bridge events ───────────────────────────────────────────────────────
    // The RecompRando client owns the AP slot. Events exist for interface compat only.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) continue;
                if (rel.TryGetProperty("tag_name", out var tag))
                {
                    AvailableVersion = tag.GetString();
                    break;
                }
            }
        }
        catch { /* non-fatal — network unavailable */ }
    }

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((5, "Fetching latest MMRecompRando release..."));
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                progress.Report((100,
                    "Could not fetch release info. Download manually from " + ReleasesUrl));
                return;
            }

            string? assetUrl = null, tagName = null;
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) continue;
                if (rel.TryGetProperty("tag_name", out var tag)) tagName = tag.GetString();
                if (!rel.TryGetProperty("assets", out var assets)) continue;
                foreach (var asset in assets.EnumerateArray())
                {
                    if (!asset.TryGetProperty("browser_download_url", out var url)) continue;
                    string? urlStr = url.GetString();
                    if (urlStr == null) continue;
                    string nameLower = urlStr.ToLowerInvariant();
                    // Prefer Windows zip/installer asset
                    if ((nameLower.EndsWith(".zip") || nameLower.EndsWith(".exe"))
                        && !nameLower.Contains("linux") && !nameLower.Contains("mac")
                        && !nameLower.Contains("source"))
                    {
                        assetUrl = urlStr;
                        break;
                    }
                }
                if (assetUrl != null) break;
            }

            if (assetUrl == null)
            {
                progress.Report((100,
                    "No Windows release asset found. Download manually from " + ReleasesUrl +
                    " and point the launcher at your install folder in Settings."));
                return;
            }

            progress.Report((20,
                "Downloading MMRecompRando" + (tagName != null ? " " + tagName : "") + "..."));
            byte[] data = await _http.GetByteArrayAsync(assetUrl, ct);
            progress.Report((70, "Extracting..."));

            Directory.CreateDirectory(InstallDir);

            if (assetUrl.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                string zipPath = Path.Combine(InstallDir, "mmrecomp_ap.zip");
                await File.WriteAllBytesAsync(zipPath, data, ct);
                System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, InstallDir,
                    overwriteFiles: true);
                File.Delete(zipPath);
            }
            else
            {
                // Single-file installer — write and run
                string exePath = Path.Combine(InstallDir, "MMRecompRando_setup.exe");
                await File.WriteAllBytesAsync(exePath, data, ct);
                progress.Report((80,
                    "An installer was downloaded. Please run it and point the launcher " +
                    "at your install folder in Settings. File: " + exePath));
            }

            if (tagName != null)
                File.WriteAllText(VersionFile, tagName);

            progress.Report((100,
                "MMRecompRando downloaded" + (tagName != null ? " (" + tagName + ")" : "") +
                " to " + InstallDir + ". You still need to provide your own Majora's Mask " +
                "N64 ROM (USA v1.0, .z64 format). Point the launcher at your install folder " +
                "in Settings if needed, then press Play."));
        }
        catch (Exception ex)
        {
            progress.Report((100,
                "Download failed: " + ex.Message + ". Download manually from " + ReleasesUrl));
        }
    }

    public Task<bool> VerifyInstallAsync(CancellationToken ct = default)
        => Task.FromResult(IsInstalled);

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist.";
        if (FindExeInDir(folder) != null) return null;
        return "No Zelda64Recompiled or MMRecompRando executable found in that folder.";
    }

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        _ = session;
        LaunchGame();
        return Task.CompletedTask;
    }

    public bool SupportsStandalone => false;
    public bool ConnectsItself     => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        LaunchGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        _gameProcess = null;
        IsRunning    = false;
        return Task.CompletedTask;
    }

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ────────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD0, 0xA0, 0x40));

        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Majora's Mask Recompiled is a native PC port of the N64 game. You must provide " +
                "your own legally-obtained Majora's Mask ROM (USA v1.0, .z64). The Archipelago " +
                "integration (RecompRando/MMRecompRando) handles the AP connection in-game. " +
                "Install the apworld from the project's releases page and press Install to " +
                "download the recompiled port. Point the launcher at your install folder below.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        panel.Children.Add(SectionHeader("INSTALL FOLDER", muted));

        string? exePath = ResolveGameExe();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = exePath != null
                ? "✓ Found executable:\n" + exePath
                : "Not found. Press Install to download, or set the folder below.",
            FontSize = 11, Foreground = exePath != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var folderRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var folderBox = new System.Windows.Controls.TextBox
        {
            Text = GameDirectory, IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var folderBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120, Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        folderBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select your MMRecompRando install folder",
                InitialDirectory = Directory.Exists(GameDirectory) ? GameDirectory : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a valid MMRecompRando folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                GameDirectory  = picked;
                folderBox.Text = picked;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(folderBtn, System.Windows.Controls.Dock.Right);
        folderRow.Children.Add(folderBtn);
        folderRow.Children.Add(folderBox);
        panel.Children.Add(folderRow);

        panel.Children.Add(SectionHeader("SETUP STEPS", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "1) Provide your own Majora's Mask N64 ROM (USA v1.0, .z64 format).\n" +
                "2) Press Install to download MMRecompRando, or download it manually " +
                "from the releases page and set the folder above.\n" +
                "3) Follow the MMRecompRando setup guide to import your ROM.\n" +
                "4) Install the Majora's Mask apworld from the same releases page into " +
                "your Archipelago worlds/ folder.\n" +
                "5) Press Play to launch the game. Connect to your AP server in-game.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("MMRecompRando Releases ↗",   ReleasesUrl),
            ("MMRecompRando Repository ↗", RepoUrl),
            ("Archipelago Official ↗",      ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0), FontSize = 12,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = new System.Windows.Media.SolidColorBrush(
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

    private static System.Windows.Controls.TextBlock SectionHeader(
        string text, System.Windows.Media.Brush muted)
        => new()
        {
            Text = text, FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        };

    // ── News feed ──────────────────────────────────────────────────────────────
    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<NewsItem>();

            var items = new System.Collections.Generic.List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private string? ResolveGameExe()
    {
        // 1) Settings override
        if (!string.IsNullOrWhiteSpace(GameDirectory) && Directory.Exists(GameDirectory))
        {
            string? found = FindExeInDir(GameDirectory);
            if (found != null) return found;
        }
        // 2) Default install dir
        return FindExeInDir(InstallDir);
    }

    private static string? FindExeInDir(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        // Check preferred name first
        string preferred = Path.Combine(dir, PreferredExeName);
        if (File.Exists(preferred)) return preferred;
        // Search for any likely recomp exe
        try
        {
            foreach (string exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string n = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (n.Contains("zelda") || n.Contains("recomp") || n.Contains("mmrecomp"))
                    return exe;
            }
        }
        catch { }
        return null;
    }

    private void LaunchGame()
    {
        string? exe = ResolveGameExe();
        if (exe == null || !File.Exists(exe))
            throw new FileNotFoundException(
                "MMRecompRando executable not found. Press Install to download it, " +
                "or set your install folder in Settings.", PreferredExeName);

        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exe,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            UseShellExecute  = true,
        });
        if (proc != null)
        {
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
        }
    }

    private string? ReadInstalledVersion()
    {
        try { return File.Exists(VersionFile) ? File.ReadAllText(VersionFile).Trim() : null; }
        catch { return null; }
    }
}
