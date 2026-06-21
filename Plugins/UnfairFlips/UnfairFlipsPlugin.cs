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

namespace LauncherV2.Plugins.UnfairFlips;

// ═══════════════════════════════════════════════════════════════════════════════
// UnfairFlipsPlugin — launch integration for "Unfair Flips" (HEATHER FLOWERS,
// 2025), a coin-flipping idle/clicker game with Archipelago support via a
// BepInEx 5 (Mono) mod.
//
// ── FACTS (verified 2026-06-19) ─────────────────────────────────────────────
//   * Steam release: appid 3925760 ($1.99 / Windows).
//   * AP game string: "Unfair Flips"  (verified in ArchipelagoHandler.cs →
//     LoginAsync("Unfair Flips", ...)).
//   * Mod repo: robotzurg/UnfairFlipsAPMod  (BepInEx 5 Mono plugin for Unity).
//     Thunderstore: thunderstore.io/c/unfair-flips/p/Jeffdev/Unfair_Flips_Archipelago/
//     The same package is also published by Jeffdev-Archipelago-Implementations.
//   * ConnectsItself = true: the BepInEx plugin embeds the AP client; once the
//     mod is loaded the game connects directly to the AP server via an in-game
//     UI (no external AP client exe needed).
//   * SupportsStandalone = true: the base game is fully playable without AP.
//   * Install flow: own Unfair Flips on Steam → install BepInEx 5 Mono for x64
//     → drop the UnfairFlipsAPMod plugin folder into BepInEx/plugins → launch
//     the game (connection dialog appears in-game). Easiest path: use r2modman
//     with the Thunderstore package which handles BepInEx + mod in one click.
//   * This plugin detects the Steam install, launches the game via Steam's
//     protocol, and guides the mod setup in Settings. It does NOT download or
//     install the mod — that must be done by the player.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class UnfairFlipsPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const int    STEAM_APPID      = 3925760;
    private const string STEAM_STORE_URL  = "https://store.steampowered.com/app/3925760/Unfair_Flips/";
    private const string STEAM_RUN_URL    = "steam://rungameid/3925760";
    private const string MOD_REPO_URL     = "https://github.com/robotzurg/UnfairFlipsAPMod";
    private const string THUNDERSTORE_URL = "https://thunderstore.io/c/unfair-flips/p/Jeffdev/Unfair_Flips_Archipelago/";
    private const string GH_MOD_RELEASES  = "https://api.github.com/repos/robotzurg/UnfairFlipsAPMod/releases";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "unfair_flips";
    public string DisplayName => "Unfair Flips";
    public string Subtitle    => "Steam · built-in AP client (BepInEx mod)";
    public string ApWorldName => "Unfair Flips";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "unfair_flips.png");

    public string ThemeAccentColor => "#C0882A";

    public string[] GameBadges => new[] { "Requires Steam" };

    public string Description =>
        "Unfair Flips is a casual coin-flipping clicker by HEATHER FLOWERS where " +
        "your coin starts with a stubborn 20% heads chance. In Archipelago mode a " +
        "BepInEx 5 mod sends checks when shop upgrades are purchased and receives " +
        "items that improve your coin's fairness, flip speed, and combo multiplier. " +
        "Requires a Steam copy of Unfair Flips (appid 3925760) and the " +
        "UnfairFlipsAPMod plugin installed via r2modman or manually.";

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

    public string GameDirectory { get; set; } =
        SteamLocator.FindGameDir(STEAM_APPID) ?? string.Empty;

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
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync("robotzurg", "UnfairFlipsAPMod", ct));
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((100,
            "Install the Unfair Flips Archipelago mod via r2modman (Thunderstore) " +
            "or manually: install BepInEx 5 Mono x64 into the game folder, then drop " +
            "the UnfairFlipsAPMod folder into BepInEx/plugins. See Settings for links."));
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

    // ── AP bridge — inert (game mod owns the slot connection) ────────────────

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

        // Game directory detection
        panel.Children.Add(MakeHeader("GAME DIRECTORY", muted));
        bool found = IsInstalled;
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = found ? "Unfair Flips detected: " + GameDirectory
                         : "Unfair Flips not found. Install via Steam (appid 3925760) or browse below.",
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
                Title = "Select Unfair Flips install folder",
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
            "1. Own Unfair Flips on Steam (appid 3925760) and install it.",
            "2. Install r2modman (Thunderstore mod manager) — easiest method.",
            "3. In r2modman, select Unfair Flips and install the " +
                "\"Unfair Flips Archipelago\" mod by Jeffdev. This auto-installs " +
                "BepInEx 5 and the mod plugin.",
            "4. Alternatively: manually install BepInEx 5 Mono x64 into the game " +
                "folder, launch/close the game once, then drop the UnfairFlipsAPMod " +
                "folder into BepInEx/plugins.",
            "5. Generate your Archipelago seed (drop the .apworld into your AP " +
                "worlds folder and generate via the AP Launcher).",
            "6. Launch via \"Play Modded\" in r2modman or via this launcher. " +
                "Enter your AP server, slot name, and password in the in-game " +
                "connection dialog.",
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
            ("Unfair Flips on Steam ↗",   STEAM_STORE_URL),
            ("UnfairFlipsAPMod on GitHub ↗", MOD_REPO_URL),
            ("Thunderstore mod page ↗",    THUNDERSTORE_URL),
            ("Archipelago Official ↗",     "https://archipelago.gg"),
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
            string json = await _http.GetStringAsync(GH_MOD_RELEASES, ct);
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

        // Prefer launching via Steam so that Steam overlay, achievements, and
        // DRM are satisfied correctly.
        try
        {
            var psi = new ProcessStartInfo(STEAM_RUN_URL) { UseShellExecute = true };
            _gameProcess = Process.Start(psi);
            // Steam protocol launch returns immediately; we cannot reliably track
            // the game process this way, so mark running and leave it.
        }
        catch
        {
            // Fallback: try direct exe launch from detected directory.
            string dir = GameDirectory;
            string? exe = null;
            if (Directory.Exists(dir))
            {
                foreach (string name in new[] { "Unfair Flips.exe", "UnfairFlips.exe" })
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
                            string stem = Path.GetFileNameWithoutExtension(f);
                            if (stem.IndexOf("unfair", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                stem.IndexOf("flips",  StringComparison.OrdinalIgnoreCase) >= 0)
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
                var psi2 = new ProcessStartInfo
                {
                    FileName         = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                    UseShellExecute  = false,
                };
                var proc = Process.Start(psi2);
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
                    "Could not launch Unfair Flips. Make sure the game is installed via Steam.", ex);
            }
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
