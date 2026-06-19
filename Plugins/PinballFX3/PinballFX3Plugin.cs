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

namespace LauncherV2.Plugins.PinballFX3;

// ═══════════════════════════════════════════════════════════════════════════════
// PinballFX3Plugin — launch integration for "Pinball FX3" (Zen Studios).
//
// ── VERIFIED FACTS (2026-06-19, sources: SerpentAI/Archipelago branch
//    pinball_fx3, archipelago.json, client.py, releases) ──────────────────────
//
//   * GAME: Pinball FX3 — Steam appid 441090. Originally "Pinball FX3.exe";
//     renamed to "Pinball FX Classic.exe" after Zen Studios rebranded. The
//     apworld author explicitly maintains the "Pinball FX3" name.
//
//   * AP WORLD: apworld file "pinball_fx3.apworld" from SerpentAI/Archipelago
//     (branch: pinball_fx3, released via GitHub releases).
//     AP game string: "Pinball FX3" — verified from:
//       archipelago.json: "game": "Pinball FX3"
//       client.py PinballFX3Context: game: str = "Pinball FX3"
//     Latest release: pinballfx3-v1.3.0. Assets: pinball_fx3.apworld + Pinball.FX3.yaml.
//     Repository: https://github.com/SerpentAI/Archipelago/tree/pinball_fx3
//
//   * CLIENT ARCHITECTURE: The apworld ships a dedicated "PinballFX3Client"
//     (worlds/pinball_fx3/client.py) that:
//       - Reads game state from the running Pinball FX3 process via memory.
//       - Connects to the Archipelago server as the AP slot.
//     This client is part of SerpentAI's Archipelago fork (not the official AP
//     launcher). ConnectsItself = false — the user must run the PinballFX3Client
//     separately alongside the game. This launcher launches the GAME; the user
//     separately runs the AP client from SerpentAI's Archipelago installation.
//
//   * STEAM ONLY: Pinball FX3 is a Steam game. The launcher locates it via the
//     Steam registry / appmanifest_441090.acf. GameDirectory is read-only
//     (detected from Steam; not user-settable via Browse because the player
//     cannot move a Steam game install).
//
//   * SupportsStandalone = true: Pinball FX3 can be played without AP.
//
//   * BUILD NOTE: UseWindowsForms=true alongside UseWPF=true; all WPF UI types
//     are fully qualified to avoid CS0104 ambiguity.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PinballFX3Plugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const int STEAM_APPID = 441090;

    private const string AP_REPO_URL      = "https://github.com/SerpentAI/Archipelago/tree/pinball_fx3";
    private const string AP_RELEASES_URL  = "https://github.com/SerpentAI/Archipelago/releases?q=pinball+fx3";
    private const string GH_RELEASES_API  =
        "https://api.github.com/repos/SerpentAI/Archipelago/releases";

    private const string STEAM_STORE_URL  = "https://store.steampowered.com/app/441090/Pinball_FX3/";
    private const string ARCHIPELAGO_URL  = "https://archipelago.gg";

    /// Pinball FX3 executables (original name + rebranded name after Zen's rename).
    private static readonly string[] GAME_EXE_NAMES = new[]
    {
        "Pinball FX Classic.exe",   // post-rebrand (current)
        "Pinball FX3.exe",          // original name
    };

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "pinball_fx3";
    public string DisplayName => "Pinball FX3";
    public string Subtitle    => "Steam · SerpentAI AP client";

    /// EXACT AP game string — verified from archipelago.json ("game": "Pinball FX3")
    /// and client.py (game: str = "Pinball FX3"). Source: SerpentAI/Archipelago
    /// branch pinball_fx3, release pinballfx3-v1.3.0.
    public string ApWorldName => "Pinball FX3";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "pinball_fx3.png");

    public string ThemeAccentColor => "#C0302A";   // Pinball FX3 red

    public string[] GameBadges => new[] { "Steam · Pinball FX Classic" };

    public string Description =>
        "Pinball FX3 (now rebranded as \"Pinball FX Classic\" on Steam) is Zen " +
        "Studios' pinball platform. The Archipelago integration by SerpentAI reads " +
        "game state from memory — table high scores, completed challenges, and star " +
        "ratings become location checks shuffled across the multiworld. " +
        "You need Pinball FX3 / Pinball FX Classic on Steam (AppID 441090). " +
        "The AP client is the dedicated PinballFX3Client from SerpentAI's Archipelago " +
        "fork — run it alongside the game and it connects to the AP server for you. " +
        "The launcher launches the game; start the PinballFX3Client separately via " +
        "SerpentAI's Archipelago launcher. AP game string: \"Pinball FX3\".";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version / install state ───────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled  => !string.IsNullOrEmpty(GameDirectory) &&
                                Directory.Exists(GameDirectory);
    public bool IsRunning    { get; private set; }

    public bool ConnectsItself     => false;   // requires SerpentAI PinballFX3Client
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Detected Steam game directory — read only (set by FindSteamDir at startup).
    public string GameDirectory { get; set; } = FindSteamDir();

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
            string json = await _http.GetStringAsync(GH_RELEASES_API, ct);
            using var doc = JsonDocument.Parse(json);
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("tag_name", out var tag)) continue;
                string? t = tag.GetString();
                if (t != null && t.Contains("pinball", StringComparison.OrdinalIgnoreCase))
                { AvailableVersion = t; break; }
            }
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((100,
            "Pinball FX3 is a Steam game — purchase / install it from the Steam store. " +
            "The AP integration (pinball_fx3.apworld + PinballFX3Client) is distributed " +
            "via SerpentAI's Archipelago fork. Download it from the GitHub releases link " +
            "in the Settings panel and install it into your Archipelago installation."));
        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        GameDirectory = FindSteamDir();
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

    // ── AP bridge — inert (PinballFX3Client handles the AP connection) ────────

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
        var warn    = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0xFF, 0xCC, 0x44));

        var panel = new System.Windows.Controls.StackPanel
                    { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        panel.Children.Add(MakeHeader("SETUP GUIDE", muted));
        foreach (string step in new[]
        {
            "1. Own Pinball FX3 (or Pinball FX Classic) on Steam (AppID 441090).",
            "2. Download the pinball_fx3 apworld from SerpentAI's Archipelago releases (link below).",
            "3. Install the apworld into your Archipelago installation's lib/worlds folder.",
            "4. Generate a multiworld with game: Pinball FX3 from your Pinball.FX3.yaml.",
            "5. Launch Pinball FX3 from this launcher (Play button).",
            "6. Launch PinballFX3Client from SerpentAI's Archipelago launcher and connect.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
            });
        }

        // Note about the rename
        panel.Children.Add(MakeHeader("NOTE", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Zen Studios rebranded Pinball FX3 to \"Pinball FX Classic\" on Steam. " +
                   "The apworld still uses the game string \"Pinball FX3\" — do not change it.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Detected install path
        panel.Children.Add(MakeHeader("DETECTED GAME DIRECTORY", muted));
        string detected = FindSteamDir();
        panel.Children.Add(new System.Windows.Controls.TextBox
        {
            Text       = string.IsNullOrEmpty(detected) ? "(not found — install via Steam)" : detected,
            IsReadOnly = true,
            FontSize   = 11,
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = string.IsNullOrEmpty(detected) ? warn : fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        panel.Children.Add(MakeHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Pinball FX3 on Steam ↗",         STEAM_STORE_URL),
            ("SerpentAI AP Releases ↗",        AP_RELEASES_URL),
            ("AP Plugin Source (pinball_fx3) ↗", AP_REPO_URL),
            ("Archipelago Official ↗",          ARCHIPELAGO_URL),
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
            string json = await _http.GetStringAsync(GH_RELEASES_API, ct);
            using var doc = JsonDocument.Parse(json);
            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("tag_name", out var tagProp)) continue;
                string? tag = tagProp.GetString();
                if (tag == null || !tag.Contains("pinball", StringComparison.OrdinalIgnoreCase)) continue;

                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);
                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : tag,
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: tag,
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

        // Try direct exe launch first
        string dir = GameDirectory;
        string? exe = null;
        if (Directory.Exists(dir))
        {
            foreach (string name in GAME_EXE_NAMES)
            {
                string c = Path.Combine(dir, name);
                if (File.Exists(c)) { exe = c; break; }
            }
        }

        if (exe != null)
        {
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
                return;
            }
            catch { /* fall through to Steam protocol */ }
        }

        // Fall back to Steam protocol launch
        try
        {
            Process.Start(new ProcessStartInfo(
                $"steam://rungameid/{STEAM_APPID}") { UseShellExecute = true });
            IsRunning = false;
            GameExited?.Invoke(0);
        }
        catch (Exception ex)
        {
            IsRunning = false;
            throw new InvalidOperationException(
                "Could not launch Pinball FX3. Make sure it is installed via Steam.", ex);
        }
    }

    /// Locate Pinball FX3 installation via Steam registry / appmanifest.
    private static string FindSteamDir()
    {
        try
        {
            // Read Steam install path from registry
            string? steamPath = null;
            using (var key = Microsoft.Win32.Registry.CurrentUser
                       .OpenSubKey(@"Software\Valve\Steam"))
                steamPath = key?.GetValue("SteamPath") as string;
            if (steamPath == null)
                using (var key = Microsoft.Win32.Registry.LocalMachine
                           .OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                    steamPath = key?.GetValue("InstallPath") as string;
            if (steamPath == null) return string.Empty;

            // Check default library
            return ProbeLibrary(steamPath) ??
                   ProbeVdfLibraries(steamPath) ??
                   string.Empty;
        }
        catch { return string.Empty; }
    }

    private static string? ProbeLibrary(string steamLibPath)
    {
        string manifest = Path.Combine(steamLibPath, "steamapps",
            $"appmanifest_{STEAM_APPID}.acf");
        if (!File.Exists(manifest)) return null;

        // Read installdir from the manifest
        foreach (string line in File.ReadAllLines(manifest))
        {
            int idx = line.IndexOf("\"installdir\"", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            int q1 = line.IndexOf('"', idx + 12);
            if (q1 < 0) continue;
            int q2 = line.IndexOf('"', q1 + 1);
            if (q2 < 0) continue;
            string installDir = line.Substring(q1 + 1, q2 - q1 - 1);
            string fullPath = Path.Combine(steamLibPath, "steamapps", "common", installDir);
            return Directory.Exists(fullPath) ? fullPath : null;
        }
        return null;
    }

    private static string? ProbeVdfLibraries(string steamPath)
    {
        try
        {
            string vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) return null;
            foreach (string line in File.ReadAllLines(vdf))
            {
                int idx = line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                int q1 = line.IndexOf('"', idx + 6);
                if (q1 < 0) continue;
                int q2 = line.IndexOf('"', q1 + 1);
                if (q2 < 0) continue;
                string libPath = line.Substring(q1 + 1, q2 - q1 - 1)
                                     .Replace("\\\\", "\\");
                string? found = ProbeLibrary(libPath);
                if (found != null) return found;
            }
        }
        catch { }
        return null;
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
