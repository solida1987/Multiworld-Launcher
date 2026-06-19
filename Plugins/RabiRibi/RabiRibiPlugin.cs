using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using LauncherV2.Core;

// IMPORTANT (real project has <UseWindowsForms>true</UseWindowsForms>):
// WPF UI types that collide with WinForms are FULLY QUALIFIED below
// (System.Windows.Controls.*, System.Windows.Media.*, System.Windows.Thickness,
// System.Windows.FontWeights, System.Windows.HorizontalAlignment,
// System.Windows.TextWrapping, …) to avoid CS0104 ambiguities. Do NOT add
// `using System.Windows.Controls;` / `using System.Windows.Media;` here.

namespace LauncherV2.Plugins.RabiRibi;

// ═══════════════════════════════════════════════════════════════════════════════
// RabiRibiPlugin — install / update / launch for "Rabi-Ribi" (CreSpirit, 2016)
// played through the tdkollins Archipelago mod. This is a NATIVE "ConnectsItself"
// integration (NOT a BizHawk / Lua emulator game): the mod bundles its own AP
// client, so Rabi-Ribi connects to the Archipelago server itself — no emulator,
// no Lua bridge.
//
// ── REALITY CHECK (2026-06-14, verified online + against the AP world) ────────
//
//   * THE AP WORLD: game string "Rabi-Ribi" — verified against the tdkollins
//     Archipelago-Rabi-Ribi repository (worlds/rabi_ribi/__init__.py, game =
//     "Rabi-Ribi"). World id "rabi_ribi". Author tdkollins.
//
//   * THE GAME: Rabi-Ribi is a GameMaker Studio anime Metroidvania / bullet-hell
//     hybrid by CreSpirit (Steam appid 400910). The base game is COMMERCIAL — the
//     user owns it via Steam. The AP mod is a custom patcher / companion tool
//     published by tdkollins on GitHub:
//         https://github.com/tdkollins/Archipelago-Rabi-Ribi
//
//   * THE MOD: the mod repo publishes releases as zips on GitHub. The zip content
//     is extracted into (or alongside) the user's Rabi-Ribi Steam install folder.
//     The mod may include a patched exe, standalone AP client, or companion app.
//     The exact zip layout was NOT inspected offline — the extractor is tolerant
//     and flattens a lone top-level wrapper subfolder. ResolveModExe() tries
//     likely candidate exes (rabi_ribi_ap.exe, rabiribi.exe, Rabi-Ribi.exe, and
//     any *.exe not matching helper/uninstaller patterns).
//
//   * HOW IT CONNECTS: ConnectsItself = true — the mod bundles its own AP client
//     (either in-game or as a companion process). The launcher downloads and
//     extracts the mod into the GameDirectory staging area and instructs the user
//     to copy mod files to their Steam install. The connection is managed by the
//     mod itself; no credential file is written by this launcher.
//
//   * INSTALL MODEL: the launcher stages the mod into Games/RabiRibi. Because the
//     mod is overlaid on the user's own Steam install (not a standalone game), the
//     settings panel instructs the user to copy files from the staging area to
//     their Rabi-Ribi folder. SupportsStandalone = true (base game can be launched
//     directly via Steam when no AP connection is needed).
//
//   * Steam appid 400910 — used to auto-detect the install folder for the copy
//     instruction and for the fallback Steam launch URL.
//
// ── DEFENSIVE / UNVERIFIED ────────────────────────────────────────────────────
//   * No specific pinned release version has been verified against the live GitHub
//     releases API — the fallback is constructed from the latest-release URL
//     pattern with a well-known generic name. The actual latest release is
//     resolved via the GitHub API at install time.
//   * The exact connection mechanism (in-game menu, config file, CLI args) was not
//     verified from a setup guide — the settings panel is honest about this and
//     directs the user to the mod repo and to follow the README / setup guide.
//   * No plaintext AP password is written to disk by this plugin.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class RabiRibiPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER = "tdkollins";
    private const string GITHUB_REPO  = "Archipelago-Rabi-Ribi";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"{GH_RELEASES_URL}/latest";

    private const string SetupGuideUrl   = $"{RepoUrl}#readme";
    private const string ArchipelagoSite = "https://archipelago.gg";

    /// Steam — Rabi-Ribi appid 400910.
    private const string SteamAppId = "400910";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The Steam common folder name for Rabi-Ribi.
    private const string SteamCommonFolderName = "Rabi-Ribi";

    /// Candidate base-game exe names (GameMaker Studio titles use these names).
    private static readonly string[] BaseExeNames =
        { "rabiribi.exe", "Rabi-Ribi.exe", "RabiRibi.exe" };

    /// Installed-version stamp, written after a successful mod download.
    private const string VersionFileName = "rabi_ribi_ap_version.dat";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "rabi_ribi";
    public string DisplayName => "Rabi-Ribi";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/rabi_ribi/__init__.py
    /// (game = "Rabi-Ribi").
    public string ApWorldName => "Rabi-Ribi";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "rabi_ribi.png");

    public string ThemeAccentColor => "#E040A0";   // magenta / anime magical-girl pink
    public string[] GameBadges     => new[] { "Steam · needs game + mod" };

    public string Description =>
        "Rabi-Ribi is a charming anime Metroidvania bullet-hell hybrid by CreSpirit: " +
        "Erina the bunny girl and her fairy companion Ribbon explore a vast world, " +
        "collecting abilities and shooting waves of adorable (but deadly) bullets. " +
        "This is the Archipelago mod by tdkollins — the mod bundles its own AP client " +
        "so items, abilities and upgrades are shuffled into the multiworld and the game " +
        "connects to the Archipelago server itself. You bring your own copy of Rabi-Ribi " +
        "(Steam appid 400910); the launcher downloads the mod and guides you through " +
        "copying it into your Rabi-Ribi folder.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the mod has been staged into GameDirectory (version stamp
    /// present, or any mod/base exe is present). The user still needs to copy mod
    /// files to their Steam install to actually play.
    public bool IsInstalled => File.Exists(VersionFilePath) || FindStagedExe() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the mod zip is extracted (staging area).
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "RabiRibi");

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained source file).
    private string SettingsSidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                        "rabi_ribi_launcher.json");

    private string SettingsSidecarDir => Path.GetDirectoryName(SettingsSidecarPath)!;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The mod's native AP client reports checks/items/goal to the AP server
    // itself — the launcher relays nothing. These exist for interface
    // compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = File.Exists(VersionFilePath)
                ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim()
                : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        try
        {
            var (version, _) = await ResolveLatestReleaseAsync(ct);
            AvailableVersion = version;
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
        progress.Report((2, "Checking latest Rabi-Ribi Archipelago mod release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        // 2. Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"Rabi-Ribi Archipelago mod {version} is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a download for the Rabi-Ribi Archipelago mod on the " +
                "GitHub release page. Check your internet connection, or download " +
                "manually from " + RepoUrl + "/releases.");

        // 3. Download + extract into GameDirectory.
        await DownloadAndExtractModAsync(zipUrl, version, progress, ct);

        // 4. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        // 5. Show user what to do next.
        string? steamDir = DetectSteamRabiRibiDir();
        string copyInstruction = steamDir != null
            ? $"Mod files staged in: {GameDirectory}\n" +
              $"Detected your Steam install at: {steamDir}\n" +
              "Copy the mod files from the staging folder into your Rabi-Ribi Steam folder, " +
              "then launch via Steam or the Play button."
            : $"Mod files staged in: {GameDirectory}\n" +
              "Copy the mod files to your Rabi-Ribi Steam install folder, then press Play. " +
              "If your Steam install is not detected automatically, use the Settings panel " +
              "to set it.";

        progress.Report((100,
            $"Rabi-Ribi Archipelago mod {version} staged. {copyInstruction}"));
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
        // HONEST: the AP connection for Rabi-Ribi is managed by the mod itself
        // (ConnectsItself = true). The launcher cannot pre-fill a connection
        // config — no documented CLI or config path was verified. Launching opens
        // the game (with mod); the user connects via whatever in-game / companion
        // mechanism the mod provides.
        _ = session; // intentionally unused — no verified prefill mechanism
        StartGame();
        return Task.CompletedTask;
    }

    /// The base game can be launched via Steam for non-AP play.
    public bool SupportsStandalone => true;

    /// The mod's bundled AP client owns the slot connection (see header).
    public bool ConnectsItself => true;

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
        // No credential file written by this plugin — nothing to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xE0, 0x40, 0xA0));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        // ── Header ────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Rabi-Ribi Archipelago uses a custom mod by tdkollins. The launcher " +
                   "downloads and stages the mod zip; you copy the mod files into your " +
                   "Steam install of Rabi-Ribi (appid 400910), then Play. The mod bundles " +
                   "its own AP client — the game connects to the Archipelago server itself.",
            FontSize = 11, Foreground = warn, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: Mod staging directory ────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "MOD STAGING FOLDER", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var stageDirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var stageDirBox = new System.Windows.Controls.TextBox
        {
            Text = GameDirectory, IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "The folder where the launcher extracts the Rabi-Ribi Archipelago " +
                      "mod. Copy the files from here to your Rabi-Ribi Steam install.",
        };
        var stageDirBtn = new System.Windows.Controls.Button
        {
            Content = "Browse...", Width = 90,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        stageDirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select mod staging folder",
                InitialDirectory = Directory.Exists(GameDirectory) ? GameDirectory
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory    = dlg.FolderName;
                stageDirBox.Text = dlg.FolderName;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(stageDirBtn,
            System.Windows.Controls.Dock.Right);
        stageDirRow.Children.Add(stageDirBtn);
        stageDirRow.Children.Add(stageDirBox);
        panel.Children.Add(stageDirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = IsInstalled
                         ? $"Mod staged (version: {InstalledVersion ?? "unknown"})"
                         : "Not staged yet — click Install on the Play tab first.",
            FontSize   = 11,
            Foreground = IsInstalled ? success : muted,
            Margin     = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Steam install detection ──────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "RABI-RIBI STEAM INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });

        string? steamDir     = DetectSteamRabiRibiDir();
        string? overrideDir  = LoadOverrideDir();
        string? resolvedDir  = overrideDir ?? steamDir;

        string detectMsg = resolvedDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + resolvedDir
                : "Detected Steam install: " + resolvedDir)
            : "Rabi-Ribi not detected automatically. Own a copy on Steam (appid 400910), " +
              "or use the picker below to point at your install folder.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = resolvedDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        var steamDirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var steamDirBox = new System.Windows.Controls.TextBox
        {
            Text = resolvedDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Your Rabi-Ribi Steam install folder (contains rabiribi.exe). " +
                      "Auto-detected from Steam; use the picker for non-standard locations.",
        };
        var steamDirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        steamDirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select your Rabi-Ribi Steam install folder (contains rabiribi.exe)",
                InitialDirectory = Directory.Exists(resolvedDir ?? "") ? resolvedDir!
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                if (!LooksLikeRabiRibiDir(picked))
                {
                    System.Windows.MessageBox.Show(
                        "That folder does not look like a Rabi-Ribi install. Pick the " +
                        "folder that contains rabiribi.exe (or Rabi-Ribi.exe).",
                        "Not a Rabi-Ribi folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                SaveOverrideDir(picked);
                steamDirBox.Text = picked;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(steamDirBtn,
            System.Windows.Controls.Dock.Right);
        steamDirRow.Children.Add(steamDirBtn);
        steamDirRow.Children.Add(steamDirBox);
        panel.Children.Add(steamDirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Steam installs are detected automatically (appid 400910). Use the " +
                   "picker for a non-standard Steam library location.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Setup steps ──────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "SETUP STEPS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });

        foreach (string step in new[]
        {
            "1. Own Rabi-Ribi on Steam (appid 400910). Install it if you have not.",
            "2. Click Install on the Play tab — the launcher downloads and stages " +
               "the Archipelago mod zip into the staging folder above.",
            "3. Copy the mod files from the staging folder into your Rabi-Ribi Steam " +
               "install folder (the folder containing rabiribi.exe). Follow the mod " +
               "README for any additional steps (see the GitHub link below).",
            "4. Press Play to launch Rabi-Ribi with the mod. The mod connects to the " +
               "Archipelago server — follow the mod's in-game or companion UI to enter " +
               "your server, slot and password.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Section: Connection note ──────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "ARCHIPELAGO CONNECTION", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "The Rabi-Ribi Archipelago mod bundles its own AP client. This " +
                   "launcher does not inject a connection — the mod handles it. Follow " +
                   "the mod README (GitHub link below) for how to enter your server, " +
                   "slot name and password.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Rabi-Ribi Archipelago mod (GitHub) ↗", RepoUrl),
            ("Rabi-Ribi on Steam ↗",                 $"https://store.steampowered.com/app/{SteamAppId}"),
            ("Archipelago Official ↗",                ArchipelagoSite),
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
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true });
                }
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
            using var doc  = JsonDocument.Parse(json);
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

    /// "v1.2.3" → "1.2.3"; otherwise the tag is returned trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest release: version + zip download URL.
    /// Prefers any Windows-tagged zip (win, windows, x64, x86_64),
    /// or else any .zip asset on the release. Falls back to null when no asset
    /// is found (caller throws a user-facing error).
    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(
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

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;   // Windows-tagged zip
                string? anyZip    = null;   // any .zip

                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u)
                                   ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    // Skip Linux / macOS / source archives.
                    if (lower.Contains("linux") || lower.Contains("ubuntu") ||
                        lower.Contains("mac")   || lower.Contains("osx")    ||
                        lower.Contains("darwin") || lower.Contains("source"))
                        continue;

                    anyZip ??= url;
                    if (preferred == null &&
                        (lower.Contains("windows") || lower.Contains("win") ||
                         lower.Contains("x64")     || lower.Contains("x86_64")))
                        preferred = url;
                }

                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);

                // Release has no .zip asset — return the version so CheckForUpdate
                // can still report the available version, but ZipUrl is null.
                return (version, null);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable */ }

        return ("unknown", null);
    }

    // ── Private helpers — Steam detection ─────────────────────────────────────

    /// The Rabi-Ribi Steam install dir (override wins, else auto-detect).
    private string? ResolveRabiRibiDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeRabiRibiDir(ov)) return ov;
        try { return DetectSteamRabiRibiDir(); }
        catch { return null; }
    }

    /// True when the folder contains any of the expected Rabi-Ribi exe names.
    private static bool LooksLikeRabiRibiDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        try
        {
            foreach (string exe in BaseExeNames)
                if (File.Exists(Path.Combine(dir, exe))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Rabi-Ribi install: read Steam roots from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_400910.acf exists → steamapps\common\<installdir>.
    private static string? DetectSteamRabiRibiDir()
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

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeRabiRibiDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeRabiRibiDir(conventional)) return conventional;
                }
                catch { /* try next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry.
    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormalizeSteamPath(hkcu);

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormalizeSteamPath(hklm);

        string? hklm64 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm64)) yield return NormalizeSteamPath(hklm64);

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

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

            string raw = text.Substring(open + 1, close - open - 1);
            yield return raw.Replace("\\\\", "\\");
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

    // ── Private helpers — staged exe detection ────────────────────────────────

    /// Find any candidate exe in the staging directory. Used by IsInstalled when
    /// the version stamp is missing (e.g. a manual extract).
    private string? FindStagedExe()
    {
        if (!Directory.Exists(GameDirectory)) return null;
        try
        {
            // Try the known base-game exe names first (staged mod may include them).
            foreach (string exe in BaseExeNames)
            {
                string candidate = Path.Combine(GameDirectory, exe);
                if (File.Exists(candidate)) return candidate;
            }
            // Then try any exe that looks like it could be the mod or game.
            foreach (string exe in Directory.EnumerateFiles(
                GameDirectory, "*.exe", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (name.Contains("unins") || name.Contains("setup") ||
                    name.Contains("crash") || name.Contains("redist"))
                    continue;
                if (name.Contains("rabi") || name.Contains("ap") ||
                    name.Contains("archipelago"))
                    return exe;
            }
        }
        catch { /* directory vanished mid-scan */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Launch Rabi-Ribi: prefer the exe from the user's Steam install (with the
    /// mod files copied in), then try the staging directory, then fall back to the
    /// Steam launch URL. Warns the user if the mod has not been copied yet.
    private void StartGame()
    {
        // Try launching from the Steam install (where the mod should be copied).
        string? steamDir = ResolveRabiRibiDir();
        if (steamDir != null)
        {
            string? steamExe = FindExeInDir(steamDir);
            if (steamExe != null)
            {
                LaunchExe(steamExe, steamDir);
                return;
            }
        }

        // Try launching from the staging directory (if the user copied mod there
        // instead, or if there is a standalone companion exe in the staging area).
        if (Directory.Exists(GameDirectory))
        {
            string? stagedExe = FindStagedExe();
            if (stagedExe != null)
            {
                LaunchExe(stagedExe, GameDirectory);
                return;
            }
        }

        // Last resort: open via Steam (launches the base game; mod may not be active).
        string? steamPath = SteamRoots().FirstOrDefault(
            r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r));
        if (steamPath != null)
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
            "Could not find Rabi-Ribi. Make sure you own and have installed the game " +
            "on Steam (appid 400910), have downloaded the mod via the Install button, " +
            "and have copied the mod files into your Rabi-Ribi folder. See the Settings " +
            "panel for guided steps and links.",
            "rabiribi.exe");
    }

    private string? FindExeInDir(string dir)
    {
        foreach (string exe in BaseExeNames)
        {
            string candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private void LaunchExe(string exePath, string workDir)
    {
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = workDir,
            UseShellExecute  = true,
        }) ?? throw new InvalidOperationException("Failed to start Rabi-Ribi.");

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
        catch { /* some processes don't expose Exited — non-fatal */ }
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"rabi-ribi-ap-{version}-{Guid.NewGuid():N}.zip");
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"rabi-ribi-ap-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((5, $"Downloading Rabi-Ribi Archipelago mod {version}..."));
            using (var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();

                long total      = response.Content.Headers.ContentLength ?? -1;
                long downloaded = 0;

                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tempZip);
                var buf = new byte[81920];
                int bytesRead;
                while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                    downloaded += bytesRead;
                    if (total > 0)
                    {
                        int pct = (int)(5 + 65 * downloaded / total);   // 5 → 70%
                        progress.Report((pct, $"Downloading mod... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((72, "Extracting mod files..."));
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            // Flatten a lone top-level wrapper subfolder so mod files land directly
            // in GameDirectory.
            string sourceDir = tempDir;
            if (Directory.GetFiles(tempDir).Length == 0)
            {
                string[] subdirs = Directory.GetDirectories(tempDir);
                if (subdirs.Length == 1) sourceDir = subdirs[0];
            }

            progress.Report((82, "Staging mod files..."));
            Directory.CreateDirectory(GameDirectory);
            foreach (string fileSrc in Directory.EnumerateFiles(
                sourceDir, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string rel     = Path.GetRelativePath(sourceDir, fileSrc);
                string fileDst = Path.Combine(GameDirectory, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                File.Copy(fileSrc, fileDst, overwrite: true);
            }

            progress.Report((94, "Mod files staged."));
        }
        finally
        {
            try { if (File.Exists(tempZip))    File.Delete(tempZip); }    catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class RabiRibiSettings
    {
        /// User-selected override for their Rabi-Ribi Steam install folder.
        public string? SteamInstallOverride { get; set; }
    }

    private RabiRibiSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<RabiRibiSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(RabiRibiSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    private string? LoadOverrideDir()
    {
        string? p = LoadSettings().SteamInstallOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }

    private void SaveOverrideDir(string path)
    {
        var s = LoadSettings();
        s.SteamInstallOverride = path;
        SaveSettings(s);
    }
}
