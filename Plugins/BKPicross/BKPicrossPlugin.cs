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

namespace LauncherV2.Plugins.BKPicross;

// ═══════════════════════════════════════════════════════════════════════════════
// BKPicrossPlugin — install / launch for "BKPicross / Nonograms" by CommandTM.
//
// BKPicross (also listed as "Nonograms" in some AP world registrations) is a
// picross/nonogram puzzle game with Archipelago integration by CommandTM.
// Available as a downloadable release from GitHub (CommandTM/ap-nonograms).
//
// INTEGRATION MODEL
// ─────────────────
//   * ConnectsItself = false: this game does NOT have a built-in AP client.
//     The AP TextClient (or the launcher's own ApClient) must be running and
//     connected before the user launches the game.
//   * SupportsStandalone = false: no meaningful play without AP.
//   * IsInstalled = true: we assume the user always has it (no auto-install).
//     The user downloads the release manually from GitHub and browses to it here.
//   * GameDirectory starts as string.Empty — the user must click Browse to set
//     the folder containing the game exe.
//   * Launch: search GameDirectory for BKPicross.exe, Nonograms.exe, or any
//     other .exe. If GameDirectory is not set or nothing is found, fall back to
//     opening the GitHub releases page in the browser.
//
// SETUP FLOW
// ──────────
//   1. Download the latest release from GitHub (releases page linked in Settings).
//   2. Extract to any folder on disk.
//   3. Click Browse in Settings to select that folder.
//   4. Connect the AP TextClient to your Archipelago server before pressing Play.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class BKPicrossPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GH_OWNER     = "CommandTM";
    private const string GH_REPO      = "ap-nonograms";
    private const string REPO_URL     = "https://github.com/CommandTM/ap-nonograms";
    private const string GH_RELEASES  = "https://api.github.com/repos/CommandTM/ap-nonograms/releases";
    private const string RELEASES_URL = "https://github.com/CommandTM/ap-nonograms/releases";
    private const string AP_SITE      = "https://archipelago.gg";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "bk_picross";
    public string DisplayName => "BKPicross / Nonograms";
    public string Subtitle    => "PC · AP client required";

    /// EXACT AP game string registered by CommandTM.
    public string ApWorldName => "BKPicross";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "bk_picross.png");

    public string ThemeAccentColor => "#6080A0";

    public string[] GameBadges => new[] { "Free Download" };

    public string Description =>
        "BKPicross / Nonograms is a picross/nonogram puzzle game with Archipelago " +
        "integration by CommandTM. Download from GitHub and follow setup instructions.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// Always treated as installed (no auto-install; user manages the download).
    public bool IsInstalled => true;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// The folder the user browses to that contains the BKPicross/Nonograms exe.
    /// Starts empty — must be set by the user via Browse in Settings.
    public string GameDirectory { get; set; } = string.Empty;

    private string SettingsSidecarDir =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath =>
        Path.Combine(SettingsSidecarDir, "bk_picross_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // ConnectsItself = false: the launcher's own ApClient holds the slot.
    // These events are wired to the launcher's ApClient forwarding logic.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Capability flags ──────────────────────────────────────────────────────

    /// The launcher's ApClient holds the AP slot (AP TextClient must also be
    /// connected externally if the game expects to find it on a local port).
    public bool ConnectsItself => false;

    /// No meaningful standalone play without AP.
    public bool SupportsStandalone => false;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        InstalledVersion = null;   // no auto-install, version unknown locally
        try
        {
            var (version, _) = await ResolveLatestReleaseAsync(ct);
            AvailableVersion = version;
        }
        catch
        {
            AvailableVersion = null;
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    /// No auto-install: guide the user to the releases page.
    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((50, "Opening the BKPicross releases page in your browser..."));
        try
        {
            Process.Start(new ProcessStartInfo(RELEASES_URL) { UseShellExecute = true });
        }
        catch { /* browser not found — non-fatal */ }

        progress.Report((100,
            "Download the latest release from GitHub, extract it to a folder, " +
            "then use Browse in Settings to point the launcher at that folder."));

        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return ResolveGameExe() != null;
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        string? exe = ResolveGameExe();
        if (exe != null)
        {
            StartGameProcess(exe);
            return Task.CompletedTask;
        }

        // No exe found — open the releases page as fallback.
        try
        {
            Process.Start(new ProcessStartInfo(RELEASES_URL) { UseShellExecute = true });
        }
        catch { }

        throw new FileNotFoundException(
            "BKPicross / Nonograms executable not found. " +
            "Download the latest release from GitHub, extract it, and use " +
            "Browse in Settings to select the game folder.",
            "BKPicross.exe");
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
        => LaunchAsync(null!, ct);

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge ─────────────────────────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xCC, 0x44));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── How this works ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "BKPicross / Nonograms requires the AP TextClient to be connected " +
                "to your Archipelago server before you launch the game. Download " +
                "the game from GitHub (link below), extract it anywhere, then " +
                "use Browse to tell the launcher where it is.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Game folder ───────────────────────────────────────────────────
        panel.Children.Add(MakeLabel("GAME FOLDER", muted));

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
            ToolTip = "The folder containing BKPicross.exe / Nonograms.exe. " +
                      "Set this after downloading and extracting the release.",
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
                Title            = "Select the BKPicross / Nonograms game folder",
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
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = exe != null
                ? "Game exe found: " + exe
                : string.IsNullOrWhiteSpace(GameDirectory)
                    ? "No folder selected — click Browse after downloading the game."
                    : "No BKPicross or Nonograms exe found in the selected folder.",
            FontSize   = 11,
            Foreground = exe != null ? success : muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Setup guide ───────────────────────────────────────────────────
        panel.Children.Add(MakeLabel("SETUP GUIDE", muted));
        foreach (string step in new[]
        {
            "1. Download the latest release from GitHub (link below). The game is free.",
            "2. Extract the zip to any folder on your PC.",
            "3. Click Browse above and select the extracted folder.",
            "4. Connect the AP TextClient to your Archipelago server before pressing Play.",
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
            ("BKPicross / Nonograms Releases (GitHub) ↗", RELEASES_URL),
            ("BKPicross / Nonograms Repository ↗",        REPO_URL),
            ("Archipelago Official ↗",                    AP_SITE),
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

    /// Resolve the game exe from GameDirectory.
    /// Resolution order:
    ///   1. BKPicross.exe
    ///   2. Nonograms.exe
    ///   3. Any *.exe in the directory root (excluding helper/uninstaller names).
    private string? ResolveGameExe()
    {
        if (string.IsNullOrWhiteSpace(GameDirectory) || !Directory.Exists(GameDirectory))
            return null;

        // Pass 1: known names.
        foreach (string name in new[] { "BKPicross.exe", "Nonograms.exe" })
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

    // ── Private helpers — process ─────────────────────────────────────────────

    private void StartGameProcess(string exePath)
    {
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = GameDirectory,
            UseShellExecute  = true,
        }) ?? throw new InvalidOperationException("Failed to start BKPicross / Nonograms.");

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

    private sealed class BKPicrossSettings
    {
        public string? GameDirectory { get; set; }
    }

    private BKPicrossSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<BKPicrossSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(BKPicrossSettings s)
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
