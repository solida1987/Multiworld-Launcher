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

namespace LauncherV2.Plugins.Emulated.Games;

// ═══════════════════════════════════════════════════════════════════════════════
// PanelDePonPlugin — Archipelago integration for Panel de Pon (known as
// Tetris Attack in the West), SNES, via BizHawk emulator.
//
// WORLD SOURCE: Community apworld by AgStarRay.
//   Repository: github.com/AgStarRay/TetrisAttackAP
//   AP game string: "Panel de Pon"
//   System: SNES. Emulator: BizHawk with a Lua script connector.
//
// STATUS: STUB — direct IGamePlugin implementation (no EmulatorPlugin base).
//   Launch() searches common BizHawk install paths and opens it if found;
//   falls back to opening the GitHub releases page in a browser so the user
//   can download the AP patch and Lua script.
//   ROM and patch application are the user's responsibility.
//
// ROM: Player supplies their own Panel de Pon / Tetris Attack SNES ROM.
//   Apply the AP patch from GitHub before loading in BizHawk.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PanelDePonPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GH_OWNER      = "AgStarRay";
    private const string GH_REPO       = "TetrisAttackAP";
    private const string GH_RELEASES   = "https://api.github.com/repos/AgStarRay/TetrisAttackAP/releases";
    private const string REPO_URL      = "https://github.com/AgStarRay/TetrisAttackAP";
    private const string RELEASES_URL  = "https://github.com/AgStarRay/TetrisAttackAP/releases";
    private const string BIZHAWK_URL   = "https://tasvideos.org/BizHawk/ReleaseHistory";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // Common BizHawk install locations to probe.
    private static readonly string[] BizHawkSearchPaths = new[]
    {
        @"C:\BizHawk\EmuHawk.exe",
        @"C:\BizHawk-2.9.1\EmuHawk.exe",
        @"C:\BizHawk-2.9\EmuHawk.exe",
        @"C:\BizHawk-2.8\EmuHawk.exe",
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "BizHawk", "EmuHawk.exe"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads", "BizHawk", "EmuHawk.exe"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "BizHawk", "EmuHawk.exe"),
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "panel_de_pon";
    public string DisplayName => "Panel de Pon (Tetris Attack)";
    public string Subtitle    => "SNES · BizHawk required";
    public string ApWorldName => "Panel de Pon";

    public string IconPath
        => Path.Combine(AppContext.BaseDirectory, "Assets", "panel_de_pon.png");

    public string ThemeAccentColor => "#A040C0";

    public string[] GameBadges => new[] { "SNES", "BizHawk Required", "ROM Required" };

    public string Description =>
        "Panel de Pon (known as Tetris Attack in the West) with an Archipelago " +
        "randomizer. Requires BizHawk emulator and a Panel de Pon / Tetris Attack " +
        "SNES ROM.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled => InstalledVersion != null;
    public bool IsRunning   { get; private set; }

    public bool ConnectsItself     => false;
    public bool SupportsStandalone => false;

    // ── Paths ─────────────────────────────────────────────────────────────────

    // User provides ROM path; no fixed launcher-managed game directory.
    public string GameDirectory { get; set; } = string.Empty;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _emulatorProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        InstalledVersion = FindBizHawk() != null ? "BizHawk" : null;
        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(GH_OWNER, GH_REPO, ct));
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((100,
            "To play Panel de Pon with Archipelago: " +
            "1) Install BizHawk from tasvideos.org/BizHawk/ReleaseHistory. " +
            "2) Download the AP patch from the GitHub releases page. " +
            "3) Apply the patch to your Panel de Pon / Tetris Attack SNES ROM. " +
            "4) Open BizHawk and load the patched ROM. " +
            "5) Load the Lua script from the AP patch to connect to your AP server."));
        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return FindBizHawk() != null;
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
        IsRunning = false;
        try { _emulatorProcess?.Kill(entireProcessTree: true); } catch { }
        _emulatorProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — passive (BizHawk Lua script handles the AP connection) ────

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
        var linkClr = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
                    { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        panel.Children.Add(MakeHeader("SETUP GUIDE", muted));
        foreach (string step in new[]
        {
            "1. Install BizHawk (https://tasvideos.org/BizHawk/ReleaseHistory).",
            "2. Download the AP patch from GitHub.",
            "3. Apply the patch to your Panel de Pon / Tetris Attack SNES ROM.",
            "4. Open BizHawk and load the patched ROM.",
            "5. Load the Lua script from the AP patch to connect to your AP server.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text         = step,
                FontSize     = 11,
                Foreground   = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin       = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // BizHawk detection status
        string? bizHawkExe = FindBizHawk();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text         = bizHawkExe != null
                               ? "BizHawk found: " + bizHawkExe
                               : "BizHawk not found in common locations. Install it and click Play to open its folder.",
            FontSize     = 11,
            Foreground   = bizHawkExe != null ? fg : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 8, 0, 8),
        });

        panel.Children.Add(MakeHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("AP Patch Releases ↗",  RELEASES_URL),
            ("AP World GitHub ↗",     REPO_URL),
            ("BizHawk Download ↗",    BIZHAWK_URL),
            ("Archipelago Official ↗","https://archipelago.gg"),
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
            string json = await _http.GetStringAsync(GH_RELEASES, ct);
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

    /// Returns the first BizHawk EmuHawk.exe found in common install paths, or null.
    private static string? FindBizHawk()
    {
        foreach (string candidate in BizHawkSearchPaths)
        {
            try { if (File.Exists(candidate)) return candidate; }
            catch { /* skip inaccessible paths */ }
        }
        return null;
    }

    private void StartGame()
    {
        IsRunning = true;
        string? bizHawkExe = FindBizHawk();

        try
        {
            if (bizHawkExe != null)
            {
                // BizHawk found — launch it so the user can load the patched ROM.
                var proc = Process.Start(new ProcessStartInfo(bizHawkExe)
                {
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(bizHawkExe) ?? "",
                });
                _emulatorProcess = proc;
                IsRunning = proc != null && !proc.HasExited;
            }
            else
            {
                // BizHawk not found — open the GitHub releases page as a fallback.
                Process.Start(new ProcessStartInfo(RELEASES_URL)
                              { UseShellExecute = true });
                IsRunning = false;
                GameExited?.Invoke(0);
            }
        }
        catch (Exception ex)
        {
            IsRunning = false;
            throw new InvalidOperationException(
                "Could not launch BizHawk or open the Panel de Pon releases page. " +
                "Install BizHawk from: " + BIZHAWK_URL, ex);
        }
    }

    private static System.Windows.Controls.TextBlock MakeHeader(string text,
        System.Windows.Media.Brush color) => new()
    {
        Text       = text,
        FontSize   = 10,
        FontWeight = System.Windows.FontWeights.SemiBold,
        Foreground = color,
        Margin     = new System.Windows.Thickness(0, 8, 0, 8),
    };
}
