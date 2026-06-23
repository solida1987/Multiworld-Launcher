using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LauncherV2.Core;

namespace LauncherV2.Plugins.NonogramAP;

// ═══════════════════════════════════════════════════════════════════════════════
// NonogramAPPlugin — install / launch for "Nonogram (Picross)" by spineraks-org.
//
// This is a DIFFERENT nonogram game from BKPicross / Nonograms by CommandTM.
// Nonogram (Picross) is by spineraks-org and is available both as a web game
// (hosted on GitHub Pages) and as a downloadable release from GitHub.
//
// INTEGRATION MODEL
// ─────────────────
//   * ConnectsItself = true: the game has a BUILT-IN AP client. It connects to
//     the Archipelago server directly from within the game. The launcher must NOT
//     hold its own ApClient on the same slot while the game is running.
//   * SupportsStandalone = false: the game is built around AP multiworld play.
//   * IsInstalled = true: the web version needs no installation. The download
//     version is optional. We always treat the game as "ready."
//   * Primary launch: open https://spineraks-org.github.io/ArchipelagoNonogram/
//     in the default browser (GitHub Pages web app).
//   * Secondary launch: if GameDirectory is set and contains an exe, launch it.
//   * The in-game AP client prompts the user for server/slot/password details.
//
// SETUP FLOW
// ──────────
//   1. Click Play to open the game in your browser. No installation required.
//      OR download the desktop release from GitHub.
//   2. The game's built-in AP client will prompt for your server details
//      (host:port, slot name, password).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class NonogramAPPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GH_OWNER       = "spineraks-org";
    private const string GH_REPO        = "ArchipelagoNonogram";
    private const string REPO_URL       = "https://github.com/spineraks-org/ArchipelagoNonogram";
    private const string GH_RELEASES    = "https://api.github.com/repos/spineraks-org/ArchipelagoNonogram/releases";
    private const string RELEASES_URL   = "https://github.com/spineraks-org/ArchipelagoNonogram/releases";
    private const string WEB_APP_URL    = "https://spineraks-org.github.io/ArchipelagoNonogram/";
    private const string AP_SITE        = "https://archipelago.gg";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "nonogram_ap";
    public string DisplayName => "Nonogram (Picross)";
    public string Subtitle    => "Web/PC · built-in AP client";

    /// EXACT AP game string registered by spineraks-org.
    public string ApWorldName => "Nonogram";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "nonogram_ap.png");

    public string ThemeAccentColor => "#40A080";

    public string[] GameBadges => new[] { "Free", "Web Browser" };

    public string Description =>
        "Nonogram (Picross) by spineraks-org is a picross/nonogram puzzle game " +
        "with built-in Archipelago support. Available as a web game in your browser " +
        "or as a downloadable release.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// Always ready — the web version needs no installation.
    public bool IsInstalled => true;
    public bool IsWebBased => true;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Optional: folder containing a downloaded desktop release.
    /// Starts empty — the web version is used when this is not set.
    public string GameDirectory { get; set; } = string.Empty;

    private string SettingsSidecarDir =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath =>
        Path.Combine(SettingsSidecarDir, "nonogram_ap_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // ConnectsItself = true: the game's built-in AP client owns the slot.
    // These exist for interface compatibility only.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Capability flags ──────────────────────────────────────────────────────

    /// The game's built-in AP client owns the slot connection.
    /// The launcher must NOT also hold an ApClient on this slot.
    public bool ConnectsItself => true;

    /// No meaningful standalone play without AP.
    public bool SupportsStandalone => false;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        InstalledVersion = null;   // web version has no local version stamp
        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(GH_OWNER, GH_REPO, ct));
        }
        catch
        {
            AvailableVersion = null;
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    /// For Nonogram (Picross), "install" means either pointing the user at the
    /// web app (no install needed) or at the GitHub releases page for a desktop build.
    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((50,
            "Nonogram (Picross) is available as a web game — no installation needed. " +
            "Click Play to open in your browser, or download from GitHub."));

        // For the desktop option, open the releases page.
        try
        {
            Process.Start(new ProcessStartInfo(RELEASES_URL) { UseShellExecute = true });
        }
        catch { /* browser not found — non-fatal */ }

        progress.Report((100,
            "Nonogram (Picross) is available as a web game (no installation needed) " +
            "or as a downloadable release. Click Play to open in your browser, or " +
            "download from GitHub."));

        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        // Web version is always available; desktop version is verified by exe presence.
        return true;
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    /// Launch strategy:
    ///   1. If GameDirectory is set and contains an exe, launch it directly.
    ///   2. Otherwise, open the GitHub Pages web app in the default browser.
    /// ConnectsItself = true: the game handles the AP connection prompt itself.
    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // Prefer a desktop exe if the user has set a game folder.
        string? exe = ResolveGameExe();
        if (exe != null)
        {
            StartGameProcess(exe);
            return Task.CompletedTask;
        }

        // Fall back to the web version.
        OpenWebApp();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        string? exe = ResolveGameExe();
        if (exe != null)
        {
            StartGameProcess(exe);
            return Task.CompletedTask;
        }
        OpenWebApp();
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

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── How this works ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Nonogram (Picross) by spineraks-org is available as a free web game " +
                "— no download or installation needed. Click Play to open it in your " +
                "browser. The game's built-in AP client will prompt you for your " +
                "Archipelago server address, slot name, and password. " +
                "A downloadable desktop release is also available on GitHub.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Web app ───────────────────────────────────────────────────────
        panel.Children.Add(MakeLabel("WEB VERSION", muted));

        var webBtn = new System.Windows.Controls.Button
        {
            Content             = "Open in Browser ↗",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Padding             = new Thickness(12, 6, 12, 6),
            Background          = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x2A, 0x1E)),
            Foreground          = success,
            BorderBrush         = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x50, 0x30)),
            Margin              = new Thickness(0, 0, 0, 8),
        };
        webBtn.Click += (_, _) => OpenWebApp();
        panel.Children.Add(webBtn);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = WEB_APP_URL,
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Desktop version (optional) ─────────────────────────────────────
        panel.Children.Add(MakeLabel("DESKTOP VERSION (OPTIONAL)", muted));

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "If you prefer the desktop release, download it from GitHub and use " +
                "Browse to point the launcher at the extracted folder. Leave blank " +
                "to always use the web version.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = GameDirectory,
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Optional: the folder containing the Nonogram (Picross) desktop exe. " +
                      "Leave blank to use the web version.",
        };
        var browseBtn = new System.Windows.Controls.Button
        {
            Content     = "Browse...",
            Width       = 90,
            Padding     = new Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        browseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select the Nonogram (Picross) desktop game folder",
                InitialDirectory = Directory.Exists(GameDirectory)
                                   ? GameDirectory
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
                SaveGameDirectory(dlg.FolderName);
            }
        };
        System.Windows.Controls.DockPanel.SetDock(browseBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(browseBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        string? exe = ResolveGameExe();
        if (!string.IsNullOrWhiteSpace(GameDirectory))
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = exe != null
                    ? "Desktop exe found: " + exe
                    : "No Nonogram exe found in the selected folder.",
                FontSize   = 11,
                Foreground = exe != null ? success : muted,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 12),
            });
        }
        else
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "No folder selected — web version will be used.",
                FontSize = 11, Foreground = muted,
                Margin = new Thickness(0, 6, 0, 12),
            });
        }

        // ── Setup guide ───────────────────────────────────────────────────
        panel.Children.Add(MakeLabel("SETUP GUIDE", muted));
        foreach (string step in new[]
        {
            "1. Click Play to open in your browser, OR download the desktop release from GitHub.",
            "2. The game's built-in AP client will prompt for your server details " +
               "(host:port, slot name, password).",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(MakeLabel("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Nonogram (Picross) Web App ↗",        WEB_APP_URL),
            ("Nonogram (Picross) Releases (GitHub) ↗", RELEASES_URL),
            ("Nonogram (Picross) Repository ↗",     REPO_URL),
            ("Archipelago Official ↗",               AP_SITE),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content             = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding             = new Thickness(0, 2, 0, 2),
                Background          = System.Windows.Media.Brushes.Transparent,
                BorderThickness     = new Thickness(0),
                FontSize            = 12,
                Margin              = new Thickness(0, 0, 0, 4),
                Foreground          = accent,
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
            string json = await _http.GetStringAsync(GH_RELEASES, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t) ? NormalizeTag(t.GetString()) ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    private async Task<(string? Version, string? Url)> ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES + "?per_page=5", ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string? tag = el.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                    if (string.IsNullOrEmpty(tag)) continue;
                    string? htmlUrl = el.TryGetProperty("html_url", out var u) ? u.GetString() : null;
                    return (NormalizeTag(tag), htmlUrl);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* network unavailable */ }
        return (null, null);
    }

    // ── Private helpers — exe resolution ──────────────────────────────────────

    /// Resolve a desktop exe from GameDirectory (optional).
    /// Returns null when GameDirectory is not set or contains no suitable exe.
    private string? ResolveGameExe()
    {
        if (string.IsNullOrWhiteSpace(GameDirectory) || !Directory.Exists(GameDirectory))
            return null;

        // Pass 1: known / likely names.
        foreach (string name in new[] { "Nonogram.exe", "ArchipelagoNonogram.exe", "nonogram.exe" })
        {
            string path = Path.Combine(GameDirectory, name);
            if (File.Exists(path)) return path;
        }

        // Pass 2: any exe that is not a helper/uninstaller.
        try
        {
            foreach (string exe in Directory.EnumerateFiles(GameDirectory, "*.exe",
                SearchOption.TopDirectoryOnly))
            {
                string lower = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (lower.Contains("unins") || lower.Contains("setup") ||
                    lower.Contains("crash") || lower.Contains("report"))
                    continue;
                return exe;
            }
        }
        catch { /* permission error */ }

        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void OpenWebApp()
    {
        try
        {
            Process.Start(new ProcessStartInfo(WEB_APP_URL) { UseShellExecute = true });
            // Browser session — we can't track IsRunning reliably.
        }
        catch
        {
            throw new InvalidOperationException(
                "Could not open the web browser. Navigate to " + WEB_APP_URL + " manually.");
        }
    }

    private void StartGameProcess(string exePath)
    {
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = GameDirectory,
            UseShellExecute  = true,
        }) ?? throw new InvalidOperationException("Failed to start Nonogram (Picross).");

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
        catch { /* some processes don't expose Exited */ }
    }

    // ── Private helpers — UI builder ──────────────────────────────────────────

    private static System.Windows.Controls.TextBlock MakeLabel(
        string text, System.Windows.Media.SolidColorBrush color) => new()
    {
        Text       = text,
        FontSize   = 10,
        FontWeight = System.Windows.FontWeights.SemiBold,
        Foreground = color,
        Margin     = new Thickness(0, 0, 0, 8),
    };

    // ── Settings sidecar ──────────────────────────────────────────────────────

    private sealed class NonogramAPSettings
    {
        public string? GameDirectory { get; set; }
    }

    private NonogramAPSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<NonogramAPSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(NonogramAPSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { }
    }

    private void SaveGameDirectory(string dir)
    {
        var s = LoadSettings();
        s.GameDirectory = dir;
        SaveSettings(s);
    }
}
