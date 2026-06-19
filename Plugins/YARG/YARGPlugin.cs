using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LauncherV2.Core;

// NOTE on type qualification (BUILD GOTCHA — CS0104):
// This project sets BOTH <UseWPF>true</UseWPF> and <UseWindowsForms>true</UseWindowsForms>.
// WPF UI types are FULLY QUALIFIED below to avoid CS0104. Do NOT add
// `using System.Windows.Controls;` or `using System.Windows.Media;` here.

namespace LauncherV2.Plugins.YARG;

// ═══════════════════════════════════════════════════════════════════════════════
// YARGPlugin — launch integration for "YARG" (Yet Another Rhythm Game), a free
// open-source plastic-band rhythm game, played through its Archipelago BepInEx 5
// plugin.
//
// ── FACTS (verified 2026-06-19) ─────────────────────────────────────────────
//   * NOT on Steam. Free download via the YARC Launcher (yarg.in), which installs
//     YARG and manages song setlists. GameDirectory = string.Empty default; user
//     must point the launcher at the YARG install folder via Browse.
//   * AP game string: "YARG"  (verified in the apworld worlds/yayarg folder and
//     the AP wiki page for YARG).
//   * APWorld repo (server side, fork): Thedrummonger's Archipelago fork,
//     branch YARGV2 → worlds/yayarg.  apworld file distributed alongside plugin
//     releases at github.com/Thedrummonger/YargArchipelagoPluginV2/releases.
//   * Client plugin repo: Thedrummonger/YargArchipelagoPluginV2  (BepInEx 5 / C#).
//     Plugin V2.5.4 tested against YARG nightly build b3586 (2026-03).
//   * ConnectsItself = true: once the BepInEx plugin is loaded, pressing F10
//     in-game opens the Archipelago connection window — no separate AP client
//     exe needed.
//   * SupportsStandalone = true: YARG is fully playable without AP.
//   * Install flow: install YARG via YARC Launcher → install BepInEx 5 (NOT 6)
//     → copy YargArchipelagoPlugin.dll + Archipelago.MultiClient.Net.dll into
//     BepInEx/plugins → (optional) use the YAML Creator tool to build a yaml
//     with the unique per-song hash → generate seed → launch YARG, press F10.
//   * Minimum songs required: YARG in Archipelago needs at minimum 20 songs
//     available from the YARC Launcher setlists.
//   * Special note: YARG also features Death Link and Energy Link support.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class YARGPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string YARG_SITE_URL    = "https://yarg.in/";
    private const string YARC_LAUNCHER_URL= "https://github.com/YARC-Official/YARC-Launcher/releases/latest";
    private const string PLUGIN_REPO      = "https://github.com/Thedrummonger/YargArchipelagoPluginV2";
    private const string GH_PLUGIN_RELEASES =
        "https://api.github.com/repos/Thedrummonger/YargArchipelagoPluginV2/releases";
    private const string AP_WIKI_URL      = "https://archipelago.miraheze.org/wiki/YARG";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "yarg";
    public string DisplayName => "YARG";
    public string Subtitle    => "Free · Yet Another Rhythm Game · built-in AP client (BepInEx mod)";
    public string ApWorldName => "YARG";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "yarg.png");

    public string ThemeAccentColor => "#1A8FD0";

    public string[] GameBadges => Array.Empty<string>();

    public string Description =>
        "YARG (Yet Another Rhythm Game) is a free, open-source plastic-band game " +
        "supporting guitar, bass, drums, keys, and vocals. In Archipelago mode a " +
        "BepInEx 5 plugin tasks you with playing through YARC Launcher setlists — " +
        "each song holds three checks, with a goal song to find. Items improve your " +
        "setlist access, grant Star Power bonuses, and include Death Link support. " +
        "Press F10 in-game to open the Archipelago connection window.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled =>
        !string.IsNullOrEmpty(GameDirectory) && Directory.Exists(GameDirectory);

    public bool IsRunning { get; private set; }

    public bool ConnectsItself     => true;
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    // YARG is not on Steam — user must point us at the YARG install folder.
    public string GameDirectory { get; set; } = string.Empty;

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
        InstalledVersion = IsInstalled ? "installed" : null;
        try
        {
            string json = await _http.GetStringAsync(GH_PLUGIN_RELEASES, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var el in doc.RootElement.EnumerateArray())
                    if (el.TryGetProperty("tag_name", out var t))
                    { AvailableVersion = t.GetString()?.Trim(); break; }
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((100,
            "YARG is free. Install it via the YARC Launcher (yarg.in). Then install " +
            "BepInEx 5 (NOT version 6) into your YARG folder, and copy " +
            "YargArchipelagoPlugin.dll + Archipelago.MultiClient.Net.dll from the " +
            "plugin release into BepInEx/plugins. See Settings for links."));
        return Task.CompletedTask;
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
        IsRunning = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (BepInEx plugin owns the slot connection) ──────────

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
        var ok      = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0xFF, 0xA5, 0x00));
        var linkClr = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
                    { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // Game directory
        panel.Children.Add(MakeHeader("YARG GAME DIRECTORY", muted));
        bool found = IsInstalled;
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = found ? "YARG detected: " + GameDirectory
                         : "YARG not found. Download via the YARC Launcher (yarg.in) " +
                           "and point this launcher at the install folder.",
            FontSize = 11, Foreground = found ? ok : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        var dirRow = new System.Windows.Controls.DockPanel
                     { Margin = new System.Windows.Thickness(0, 0, 0, 14) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = GameDirectory, IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var browseBtn = new System.Windows.Controls.Button
        {
            Content = "Browse...", Width = 90,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
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
                Title = "Select YARG install folder",
                InitialDirectory = found ? GameDirectory : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(browseBtn,
            System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(browseBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        // Setup guide
        panel.Children.Add(MakeHeader("MOD SETUP GUIDE", muted));
        foreach (string step in new[]
        {
            "1. Download and install YARG via the YARC Launcher (free at yarg.in). " +
                "Run it once to scan songs — you need at least 20 songs from the " +
                "YARC Launcher setlists.",
            "2. Install BepInEx 5 (NOT version 6) into your YARG folder. Extract " +
                "the BepInEx 5 zip so that BepInEx/ sits next to YARG.exe.",
            "3. Download the YargArchipelagoPluginV2 release from GitHub. Copy " +
                "YargArchipelagoPlugin.dll and Archipelago.MultiClient.Net.dll into " +
                "BepInEx/plugins/.",
            "4. Download the YARG .apworld file from the same release and drop it " +
                "into your Archipelago worlds folder, then generate a seed via the " +
                "AP Launcher.",
            "5. Use the included YAML Creator tool to generate your player YAML — " +
                "it produces a unique per-song hash required by the apworld. Manual " +
                "YAML creation is not supported.",
            "6. Launch YARG. Press F10 to open the AP connection window and enter " +
                "your server, slot name, and password.",
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
            ("YARG Official Site ↗",          YARG_SITE_URL),
            ("YARC Launcher (download YARG) ↗", YARC_LAUNCHER_URL),
            ("YargArchipelagoPluginV2 ↗",      PLUGIN_REPO),
            ("YARG — Archipelago Wiki ↗",      AP_WIKI_URL),
            ("Archipelago Official ↗",         "https://archipelago.gg"),
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
            string json = await _http.GetStringAsync(GH_PLUGIN_RELEASES, ct);
            using var doc  = JsonDocument.Parse(json);
            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);
                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t) ? t.GetString()  ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString()       : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private void StartGame()
    {
        IsRunning = true;
        string dir = GameDirectory;
        string? exe = null;

        if (Directory.Exists(dir))
        {
            foreach (string name in new[] { "YARG.exe", "yarg.exe" })
            {
                string c = Path.Combine(dir, name);
                if (File.Exists(c)) { exe = c; break; }
            }
            if (exe == null)
            {
                try
                {
                    foreach (string f in Directory.EnumerateFiles(dir, "*.exe",
                                 System.IO.SearchOption.TopDirectoryOnly))
                    {
                        if (Path.GetFileNameWithoutExtension(f)
                                .IndexOf("yarg", StringComparison.OrdinalIgnoreCase) >= 0)
                        { exe = f; break; }
                    }
                }
                catch { }
            }
        }

        if (exe == null)
        {
            IsRunning = false;
            GameExited?.Invoke(-1);
            return;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute  = false,
            };
            var proc = Process.Start(psi);
            _gameProcess = proc;
            if (proc != null)
            {
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) => { IsRunning = false; GameExited?.Invoke(proc.ExitCode); };
            }
            else { IsRunning = false; GameExited?.Invoke(0); }
        }
        catch (Exception ex)
        {
            IsRunning = false;
            throw new InvalidOperationException(
                "Could not launch YARG. Make sure the install folder is set correctly " +
                "and BepInEx 5 with the YARG AP plugin is installed.", ex);
        }
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
