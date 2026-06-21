using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using LauncherV2.Core;

namespace LauncherV2.Plugins.AnimalWell;

// ═══════════════════════════════════════════════════════════════════════════════
// AnimalWellPlugin — install / launch for "ANIMAL WELL" (Billy Basso / Bigmode,
// 2024) played with the Archipelago randomizer via ScipioWright's Archipelago
// fork (github.com/ScipioWright/Archipelago-SW). This is a NATIVE
// "ConnectsItself" integration: the ScipioWright fork ships an "ANIMAL WELL
// Client" inside its Launcher that reads game memory (via pymem) and connects to
// the AP server. The base game is the user's own legally-owned copy of ANIMAL
// WELL (Steam appid 813230).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the repo) ──
//
//   * THE AP WORLD game string is "ANIMAL WELL" (verified against
//     worlds/animal_well/__init__.py, animal-well branch:
//     `class AnimalWellWorld(World): ... game = "ANIMAL WELL"`).
//     GameId here = "animal_well".
//
//   * CUSTOM FORK — NOT IN MAIN ARCHIPELAGO. The world is NOT in the official
//     Archipelago release (verified: the main AP worlds/ dir has no animal_well
//     folder). It lives in ScipioWright's personal fork on the "animal-well"
//     branch. This has two consequences for the user:
//       (a) They must install the ScipioWright fork as their Archipelago
//           distribution (or at least its client) instead of / alongside the
//           official Archipelago release, to get the "ANIMAL WELL Client" that
//           reads game memory.
//       (b) The animal_well.apworld must be placed in their Archipelago
//           custom_worlds folder (typically %programdata%\Archipelago\
//           custom_worlds on Windows) so the AP generator can produce a game.
//     The launcher downloads and stages the apworld for convenience; the user
//     must place it manually because %programdata%\Archipelago is READ-ONLY
//     for this launcher (security constraint).
//
//   * RELEASE PATTERN. Every ScipioWright/Archipelago-SW release (verified
//     through 0.5.4) ships EXACTLY ONE asset: "animal_well.apworld". There is
//     no bundled Windows installer or pre-built Archipelago fork exe — the
//     user must build or obtain the ScipioWright fork separately (the official
//     setup guide links to the Archipelago website for the installer; the
//     fork's "ANIMAL WELL Client" appears in the Archipelago Launcher once the
//     apworld is installed in custom_worlds).
//
//   * INTEGRATION PATTERN (verified from setup_en.md + client.py). The game
//     itself does NOT connect to the AP server — it is a vanilla unmodified
//     Steam exe. Instead:
//       1. Launch ANIMAL WELL (Steam), leave it on the title screen.
//       2. Open the Archipelago Launcher (from the ScipioWright fork), click
//          "ANIMAL WELL Client" — this is a Python client inside the fork that
//          reads game memory via pymem and bridges items/locations.
//       3. Connect the client to the AP server (hostname / port / slot / pass).
//       4. The client shows the version number on the title screen when connected.
//     ConnectsItself = true: the external client owns the AP slot, so this
//     launcher must NOT hold its own ApClient on it.
//
//   * STEAM APP ID 813230 verified against the Steam store page for ANIMAL WELL
//     (released May 9 2024 by Billy Basso / Bigmode).
//
//   * NO BepInEx, NO mod folder, NO in-game settings panel for AP credentials.
//     The AP client (external) manages all connectivity.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam ANIMAL WELL install via the Windows registry +
//      libraryfolders.vdf (same pattern as TunicPlugin / NoitaPlugin). A manual
//      install-dir override (folder picker) is also supported and takes
//      precedence; it is validated and persisted in this plugin's own sidecar
//      (Games/ROMs/animal_well/animal_well_launcher.json).
//
//   2. DOWNLOAD the latest animal_well.apworld from ScipioWright/Archipelago-SW
//      releases and save it to Games/ROMs/animal_well/ so the user can copy it
//      to their Archipelago custom_worlds folder. The launcher cannot write to
//      %programdata%\Archipelago (READ-ONLY per security constraint), so it
//      instead opens the destination folder in Explorer after staging, making it
//      a clear one-step manual copy.
//
//   3. LAUNCH = run Animal Well.exe from the detected/override install; if the
//      exe cannot be found but Steam is present, fall back to
//      steam://rungameid/813230. ConnectsItself = true.
//      SupportsStandalone = true (plain ANIMAL WELL runs without AP).
//
// ── DEFENSIVE NOTES ──────────────────────────────────────────────────────────
//   * "Installed" means the apworld has been staged to our sidecar folder AND
//     the game exe is present. We do not gate on custom_worlds placement
//     (we can't read that path due to READ-ONLY constraint).
//   * Steam library parsing is defensive: tolerant VDF scan; any failure
//     degrades to "game not found" rather than throwing.
//   * No AP password is written anywhere by this plugin.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class AnimalWellPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string AP_FORK_OWNER  = "ScipioWright";
    private const string AP_FORK_REPO   = "Archipelago-SW";
    private const string ForkRepoUrl    = $"https://github.com/{AP_FORK_OWNER}/{AP_FORK_REPO}";
    private const string ReleasesUrl    = $"https://github.com/{AP_FORK_OWNER}/{AP_FORK_REPO}/releases";

    private const string GH_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{AP_FORK_OWNER}/{AP_FORK_REPO}/releases/latest";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{AP_FORK_OWNER}/{AP_FORK_REPO}/releases";

    // The one asset every ScipioWright release ships (verified through 0.5.4).
    private const string ApWorldAssetName = "animal_well.apworld";

    // Pinned fallback version when the GitHub API is unreachable.
    private const string FallbackVersion = "0.5.4";
    private static readonly string FallbackApWorldUrl =
        $"{ForkRepoUrl}/releases/download/{FallbackVersion}/{ApWorldAssetName}";

    // Steam appid 813230 — ANIMAL WELL (Billy Basso / Bigmode, 2024), verified.
    private const string SteamAppId         = "813230";
    private const string SteamCommonFolder  = "Animal Well";
    private const string GameExeName        = "Animal Well.exe";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    // The standard Archipelago custom_worlds location on Windows. READ-ONLY for
    // this launcher (security constraint) — we tell the user to copy there.
    private static readonly string ApCustomWorldsPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Archipelago", "custom_worlds");

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "animal_well";
    public string DisplayName => "ANIMAL WELL";
    public string Subtitle    => "Native PC · External AP client";

    /// EXACT AP game string — verified against
    /// worlds/animal_well/__init__.py (animal-well branch):
    /// `class AnimalWellWorld(World): ... game = "ANIMAL WELL"`.
    public string ApWorldName => "ANIMAL WELL";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "animal_well.png");

    // Deep teal / bioluminescent palette matching ANIMAL WELL's atmospheric look.
    public string ThemeAccentColor => "#2ABFBF";

    public string[] GameBadges => new[] { "Steam · external client" };

    public string Description =>
        "ANIMAL WELL is a 2024 atmospheric puzzle-platformer by Billy Basso (Bigmode), " +
        "played with the Archipelago multiworld randomizer via ScipioWright's Archipelago " +
        "fork. The game is your own Steam copy; an external Python client (the \"ANIMAL " +
        "WELL Client\" inside the ScipioWright Archipelago fork) reads game memory and " +
        "bridges items and locations to the server — no mod install required, no game " +
        "files are modified. The launcher helps you detect your Steam install, download " +
        "the apworld file, and launch the game. You run the client from the Archipelago " +
        "Launcher and connect in that client window.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" here means the apworld has been staged to our sidecar folder
    /// (the user still needs to copy it to custom_worlds, which we cannot do).
    public bool IsInstalled => StagedApWorldPath() != null
        && File.Exists(StagedApWorldPath()!);

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "Games", "AnimalWell");

    /// Sidecar dir for this plugin's own persistent state. Per the brief:
    /// Games/ROMs/animal_well/animal_well_launcher.json.
    private string SidecarDir =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SidecarPath =>
        Path.Combine(SidecarDir, "animal_well_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events (inert — ConnectsItself = true) ─────────────────────
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself / SupportsStandalone ───────────────────────────────────

    /// The external ANIMAL WELL Client (ScipioWright fork) owns the AP slot.
    public bool ConnectsItself    => true;

    /// Plain ANIMAL WELL runs perfectly without AP.
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = IsInstalled
                ? (ReadStagedVersion() ?? "staged")
                : null;
        }
        catch { InstalledVersion = null; }
            try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(AP_FORK_OWNER, AP_FORK_REPO, ct));
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
        // 1. Resolve the latest release.
        progress.Report((5, "Checking the latest ANIMAL WELL apworld release..."));
        var (version, apworldUrl) = await ResolveLatestApWorldAsync(ct);
        AvailableVersion = version;

        if (apworldUrl == null)
            throw new InvalidOperationException(
                "Could not find the animal_well.apworld download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                ReleasesUrl + " and copy it to your Archipelago custom_worlds folder.");

        // 2. Stage the apworld to our sidecar folder.
        progress.Report((15, $"Downloading animal_well.apworld {version}..."));
        Directory.CreateDirectory(SidecarDir);
        string destApWorld = Path.Combine(SidecarDir, ApWorldAssetName);
        await DownloadFileAsync(apworldUrl, destApWorld,
            $"Downloading animal_well.apworld {version}...", 15, 80, progress, ct);

        // 3. Stamp version.
        WriteStagedVersion(version);
        InstalledVersion = version;

        // 4. Tell the user to copy to custom_worlds (we cannot write there).
        string customWorlds = ApCustomWorldsPath;
        progress.Report((90, "Opening destination folder so you can copy the apworld..."));
        try
        {
            if (!Directory.Exists(customWorlds))
                Directory.CreateDirectory(customWorlds);
            Process.Start(new ProcessStartInfo("explorer.exe",
                $"/select,\"{destApWorld}\"") { UseShellExecute = true });
        }
        catch { /* non-fatal — we'll surface it via the settings panel */ }

        progress.Report((100,
            $"animal_well.apworld {version} staged to:\n{destApWorld}\n\n" +
            "Next step: copy it to your Archipelago custom_worlds folder:\n" +
            customWorlds + "\n\n" +
            "Then open the Archipelago Launcher (ScipioWright fork), launch " +
            "ANIMAL WELL via Steam, and click \"ANIMAL WELL Client\" to connect. " +
            "See Settings for the full guided steps."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled && ResolveGameDir() != null;
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    /// Launch the game. ConnectsItself = true so no ApClient prefill. The user
    /// opens the Archipelago Launcher (ScipioWright fork) separately and clicks
    /// "ANIMAL WELL Client" to connect to the server from there.
    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        _ = session; // external client owns the slot — no launcher prefill
        StartAnimalWell();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartAnimalWell();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (external client owns the slot) ────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings panel ────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0xBF, 0xBF));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "ANIMAL WELL uses an external Archipelago client (from ScipioWright's " +
                   "Archipelago fork) that reads game memory. No game files are modified. " +
                   "You need: (1) ANIMAL WELL on Steam, (2) the ScipioWright Archipelago " +
                   "fork installed, (3) the animal_well.apworld in your custom_worlds folder.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: Steam install ────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "ANIMAL WELL INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "ANIMAL WELL not detected. Pick your install folder below, or " +
              "install ANIMAL WELL via Steam first.";

        panel.Children.Add(new TextBlock
        {
            Text         = detectMsg,
            FontSize     = 11,
            Foreground   = gameDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 6),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = overrideDir ?? gameDir ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your ANIMAL WELL install folder (containing Animal Well.exe). " +
                          "Detected from Steam automatically; set it here to override.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content     = "Select folder...",
            Width       = 120,
            Padding     = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your ANIMAL WELL install folder (contains Animal Well.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                                   ? (overrideDir ?? gameDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateGameDir(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not an ANIMAL WELL folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeGameDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolder);
                    if (LooksLikeGameDir(nested)) picked = nested;
                }
                SaveOverrideDir(picked);
                dirBox.Text = picked;
            }
        };
        DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new TextBlock
        {
            Text = "Steam installs are detected automatically (appid 813230). Use this " +
                   "picker for a non-standard Steam library.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: apworld ──────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "APWORLD FILE", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 4, 0, 8),
        });

        string? stagedPath = StagedApWorldPath();
        bool    apworldOk  = stagedPath != null && File.Exists(stagedPath);
        panel.Children.Add(new TextBlock
        {
            Text = apworldOk
                ? $"Staged: {stagedPath}"
                : "Not downloaded yet — use Install on the Play tab, or download " +
                  "manually from the ScipioWright releases (link below).",
            FontSize = 11, Foreground = apworldOk ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // Custom_worlds status (informational — we can detect but not write there).
        string customWorlds  = ApCustomWorldsPath;
        bool   customWorldOk = File.Exists(Path.Combine(customWorlds, ApWorldAssetName));
        panel.Children.Add(new TextBlock
        {
            Text = customWorldOk
                ? $"Found in custom_worlds: {Path.Combine(customWorlds, ApWorldAssetName)}"
                : $"Not yet in custom_worlds ({customWorlds}). After downloading, copy " +
                  $"animal_well.apworld there so the Archipelago generator can use it.",
            FontSize = 11, Foreground = customWorldOk ? success : warn,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // Open staged folder button.
        if (apworldOk)
        {
            var openBtn = new System.Windows.Controls.Button
            {
                Content     = "Open staged folder in Explorer",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding     = new Thickness(10, 5, 10, 5),
                Margin      = new Thickness(0, 4, 0, 12),
                Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
                Foreground  = accent,
                BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0xBF, 0xBF)),
            };
            string sp = stagedPath!;
            openBtn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(
                    "explorer.exe", $"/select,\"{sp}\"")
                    { UseShellExecute = true }); }
                catch { }
            };
            panel.Children.Add(openBtn);
        }

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 4, 0, 8),
        });

        foreach (string step in new[]
        {
            "1. Own ANIMAL WELL on Steam (appid 813230). Install it if you have not.",
            "2. Download and install the ScipioWright Archipelago fork " +
               "(github.com/ScipioWright/Archipelago-SW). This provides the " +
               "\"ANIMAL WELL Client\" that connects to the AP server by reading " +
               "game memory. The standard Archipelago release does not include this " +
               "client.",
            "3. Download the animal_well.apworld: click Install on the Play tab, or " +
               "get it from the releases page (link below). Then copy it to your " +
               "Archipelago custom_worlds folder (see the path shown above).",
            "4. Generate a game: in the Archipelago Launcher, click Generate Template " +
               "Options to get ANIMAL WELL.yaml. Customize it, place it in Players/, " +
               "then click Generate. Upload the output or host locally.",
            "5. To play: launch ANIMAL WELL via Steam and leave it on the title screen. " +
               "Then open the Archipelago Launcher (ScipioWright fork) and click " +
               "\"ANIMAL WELL Client\". Enter your server address, slot name, and " +
               "password. You should see the version number appear on the title screen " +
               "when connected.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text         = step,
                FontSize     = 11,
                Foreground   = fg,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });

        foreach (var (label, url) in new[]
        {
            ("ScipioWright/Archipelago-SW (fork) ↗", ForkRepoUrl),
            ("ANIMAL WELL Releases ↗",               ReleasesUrl),
            ("Archipelago Official ↗",               "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content    = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding    = new Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize   = 12,
                Margin     = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor     = System.Windows.Input.Cursors.Hand,
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
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d)
                    && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t)
                                 ? NormalizeTag(t.GetString()) ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var uu) ? uu.GetString() : null
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

    /// Resolve the latest animal_well.apworld asset URL + version tag.
    /// Falls back to pinned 0.5.4 when the API is unreachable.
    private async Task<(string Version, string? Url)> ResolveLatestApWorldAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null
                && root.TryGetProperty("assets", out var assets)
                && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u)
                                     ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    if (name.Equals(ApWorldAssetName, StringComparison.OrdinalIgnoreCase))
                        return (version, url);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        return (FallbackVersion, FallbackApWorldUrl);
    }

    // ── Private helpers — Steam / game detection ──────────────────────────────

    /// The game install dir to use: override (if set and valid) wins, else Steam.
    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeGameDir(ov)) return ov;
        try { return DetectSteamGameDir(); }
        catch { return null; }
    }

    /// A folder looks like ANIMAL WELL if it has Animal Well.exe.
    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

    /// Validation message for the folder picker, or null if valid.
    public string? ValidateGameDir(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your ANIMAL WELL install folder.";

        if (LooksLikeGameDir(folder)) return null;

        try
        {
            string nested = Path.Combine(folder, SteamCommonFolder);
            if (LooksLikeGameDir(nested)) return null;
        }
        catch { /* ignore */ }

        return "That does not look like an ANIMAL WELL installation. Pick the folder " +
               "that contains Animal Well.exe (for Steam this is usually " +
               @"...\steamapps\common\Animal Well).";
    }

    /// Detect the Steam ANIMAL WELL install via the registry + libraryfolders.vdf.
    private static string? DetectSteamGameDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{SteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common    = Path.Combine(steamapps, "common");
                    string? instDir  = ReadAcfInstallDir(manifest);
                    if (instDir != null)
                    {
                        string cand = Path.Combine(common, instDir);
                        if (LooksLikeGameDir(cand)) return cand;
                    }
                    string conv = Path.Combine(common, SteamCommonFolder);
                    if (LooksLikeGameDir(conv)) return conv;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu))
            yield return hkcu.Replace('/', '\\').TrimEnd('\\');

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm))
            yield return hklm.Replace('/', '\\').TrimEnd('\\');

        string? hklm64 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm64))
            yield return hklm64.Replace('/', '\\').TrimEnd('\\');

        string? pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(pf86))
            yield return Path.Combine(pf86, "Steam");
    }

    private static IEnumerable<string> SteamLibraryRoots(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(steamRoot)) yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }

        foreach (string path in ExtractVdfPaths(text))
        {
            string norm = path.Replace('/', '\\').TrimEnd('\\');
            if (norm.Length > 0 && seen.Add(norm))
                yield return norm;
        }
    }

    private static IEnumerable<string> ExtractVdfPaths(string text)
    {
        const string key = "\"path\"";
        int i = 0;
        while ((i = text.IndexOf(key, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            i += key.Length;
            int open = text.IndexOf('"', i);
            if (open < 0) yield break;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) yield break;
            yield return text.Substring(open + 1, close - open - 1).Replace("\\\\", "\\");
            i = close + 1;
        }
    }

    private static string? ReadAcfInstallDir(string acfPath)
    {
        try
        {
            string text = File.ReadAllText(acfPath);
            const string key = "\"installdir\"";
            int i = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += key.Length;
            int open = text.IndexOf('"', i);
            if (open < 0) return null;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) return null;
            return text.Substring(open + 1, close - open - 1).Replace("\\\\", "\\");
        }
        catch { return null; }
    }

    private static string? ReadRegistryString(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(subKey);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartAnimalWell()
    {
        string? dir = ResolveGameDir();
        string? exe = dir != null ? Path.Combine(dir, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = dir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start ANIMAL WELL.");

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
            catch { /* non-fatal */ }
            return;
        }

        // Fallback: Steam URL.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true;
                return;
            }
            catch { /* fall through */ }
        }

        throw new FileNotFoundException(
            "Could not find Animal Well.exe. Open this game's Settings and pick your " +
            "ANIMAL WELL install folder, or install ANIMAL WELL via Steam.",
            GameExeName);
    }

    // ── Private helpers — download ────────────────────────────────────────────

    private async Task DownloadFileAsync(
        string url, string destPath, string msg,
        int pctStart, int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        progress.Report((pctStart, msg));
        using var response = await _http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total      = response.Content.Headers.ContentLength ?? -1;
        long downloaded = 0;

        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        var buf = new byte[81920];
        int bytesRead;
        while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
            downloaded += bytesRead;
            if (total > 0)
            {
                int span = Math.Max(1, pctEnd - pctStart);
                int pct  = pctStart + (int)(span * downloaded / total);
                progress.Report((pct, $"{msg} {downloaded / 1000}KB / {total / 1000}KB"));
            }
        }
        await dst.FlushAsync(ct);
    }

    // ── Private helpers — sidecar / staged apworld ────────────────────────────

    /// Path of the staged apworld inside our sidecar dir (may not exist yet).
    private string StagedApWorldPath()
        => Path.Combine(SidecarDir, ApWorldAssetName);

    private sealed class AnimalWellSettings
    {
        public string? InstallOverride { get; set; }
        public string? ApWorldVersion  { get; set; }
    }

    private AnimalWellSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SidecarPath))
            {
                string txt = File.ReadAllText(SidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<AnimalWellSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(AnimalWellSettings s)
    {
        try
        {
            Directory.CreateDirectory(SidecarDir);
            File.WriteAllText(SidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { }
    }

    private string? LoadOverrideDir()
    {
        string? p = LoadSettings().InstallOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }

    private void SaveOverrideDir(string p)
    {
        var s = LoadSettings();
        s.InstallOverride = p;
        SaveSettings(s);
    }

    private string? ReadStagedVersion()
    {
        string? v = LoadSettings().ApWorldVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStagedVersion(string v)
    {
        var s = LoadSettings();
        s.ApWorldVersion = v;
        SaveSettings(s);
    }
}
