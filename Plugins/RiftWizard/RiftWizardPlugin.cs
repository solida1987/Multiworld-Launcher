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
using Microsoft.Win32;
using LauncherV2.Core;

// IMPORTANT (real project has <UseWindowsForms>true</UseWindowsForms>):
// WPF UI types that collide with WinForms are FULLY QUALIFIED below
// (System.Windows.Controls.*, System.Windows.Media.*, System.Windows.Thickness,
// System.Windows.FontWeights, System.Windows.HorizontalAlignment,
// System.Windows.TextWrapping, …) to avoid CS0104 ambiguities. Do NOT add
// `using System.Windows.Controls;` / `using System.Windows.Media;` here.

namespace LauncherV2.Plugins.RiftWizard;

// ═══════════════════════════════════════════════════════════════════════════════
// RiftWizardPlugin — install / launch for "Rift Wizard" (Dylan White, 2021)
// played through the Archipelago mod by TheBigSalarius.
//
// Rift Wizard is a TACTICAL ROGUELIKE written in Python/pygame. The AP mod is
// delivered as patched Python source files that are dropped into (or overlay) the
// game's install directory. The game's own Python process reads the patched
// sources and communicates directly with the AP server — this is a native
// "ConnectsItself" integration (no emulator, no Lua bridge, no launcher-held
// ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified / best-effort online) ──────────
//   * THE AP WORLD: game string "Rift Wizard" — TheBigSalarius/Archipelago fork,
//     the canonical repo for the Rift Wizard AP world. This plugin targets the
//     GitHub releases on that fork.
//   * THE MOD: the Archipelago mod consists of patched Python (.py) files and
//     supporting scripts placed into the Rift Wizard Steam install directory.
//     Unlike pure-installer games, the player needs to own Rift Wizard on Steam
//     (appid 1271280) and manually overlay the mod files onto it.
//   * HOW IT CONNECTS: the patched Python files contain the AP client logic that
//     connects directly to the AP server from inside the game's own Python
//     process. There is typically a connection settings file or the connection
//     details are entered inside the game's in-game UI. Because the mod embeds
//     the AP client, ConnectsItself = true — the launcher must NOT hold its own
//     ApClient on this slot while the game runs.
//   * INSTALL FLOW (verified): the user downloads a release zip from
//     TheBigSalarius/Archipelago/releases, extracts it, and copies the patched
//     files into their Rift Wizard Steam install directory. This plugin automates
//     the download + extraction to a staging folder (GameDirectory) and provides
//     clear guided steps for the manual copy step, since we must not write
//     directly into a user's Steam install without consent and cannot fully
//     automate the overlay (the game may be running, files may vary by version).
//   * LAUNCH: once the mod files are in place, the player launches Rift Wizard
//     normally (via the exe or steam://rungameid/1271280) and the patched Python
//     sources handle the AP connection.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Rift Wizard install via the Windows registry, parsing
//      libraryfolders.vdf across all Steam library roots (same approach as the
//      Noita plugin — verified to work for any Steam library layout).
//   2. INSTALL/UPDATE = download the latest release zip from
//      TheBigSalarius/Archipelago/releases, extract into GameDirectory (a staging
//      area in the launcher's Games/RiftWizard/ folder), and present clear guided
//      steps for the player to copy files into their Steam install.
//   3. DETECT patched install: check for the presence of a known patched .py file
//      (Archipelago.py or AP_settings.py or similar) in the Steam install directory
//      to determine whether the mod is actually installed.
//   4. LAUNCH = run RiftWizard.exe (or rift_wizard.exe) from the detected Steam
//      install, or fall back to steam://rungameid/1271280.
//   5. SETTINGS PANEL: install status, guided install steps, links to the mod repo
//      and the manual copy step.
//
// ── DEFENSIVE / UNVERIFIED DETAILS ────────────────────────────────────────────
//   * The exact patched file names are not guaranteed offline — IsInstalled uses a
//     multi-signal check (any Archipelago*.py or AP_*.py in the game dir).
//   * The release asset name pattern is inferred — the plugin picks the most
//     plausible zip from the assets list and falls back to the pinned URL.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class RiftWizardPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GH_OWNER    = "TheBigSalarius";
    private const string GH_REPO     = "Archipelago";
    private const string RepoUrl     = $"https://github.com/{GH_OWNER}/{GH_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"{GH_RELEASES_URL}/latest";

    private const string SetupGuideUrl   = "https://archipelago.gg/games/Rift%20Wizard/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Rift Wizard appid 1271280.
    private const string SteamAppId         = "1271280";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";
    private const string SteamFolderName    = "Rift Wizard";

    // Candidate exe names (the exact name depends on the build).
    private static readonly string[] RiftWizardExeNames =
        { "RiftWizard.exe", "rift_wizard.exe", "RiftWizard2.exe" };

    // Pinned fallback (in case the GitHub API is unreachable at install time).
    // Uses the TheBigSalarius/Archipelago repo's releases endpoint directly.
    private const string FallbackVersion = "0.5.0";
    private static readonly string FallbackZipUrl =
        $"{RepoUrl}/releases/latest/download/Archipelago.zip";

    // Known patched Python file names that indicate the mod is installed.
    private static readonly string[] ModMarkerFiles =
        { "Archipelago.py", "AP_settings.py", "archipelago_client.py", "APClient.py" };

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp written after a successful staged download.
    private const string VersionFileName = "riftwizard_ap_version.dat";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "rift_wizard";
    public string DisplayName => "Rift Wizard";
    public string Subtitle    => "Steam · Archipelago mod (Python)";

    /// EXACT AP game string — from TheBigSalarius/Archipelago.
    public string ApWorldName => "Rift Wizard";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "rift_wizard.png");

    public string ThemeAccentColor => "#1B7A6E";   // dark teal / arcane emerald

    public string[] GameBadges => new[] { "Steam · needs mod" };

    public string Description =>
        "Rift Wizard is Dylan White's tactical roguelike built in Python/pygame — " +
        "carefully pick your spells and runes, craft devastating synergies, and " +
        "fight through 20 floors of procedurally generated arcane dungeons to reach " +
        "the Rift Wizard. The Archipelago mod by TheBigSalarius turns spells, runes, " +
        "and upgrades into multiworld checks, shuffled across your game and your " +
        "friends' runs. The mod patches the game's Python sources directly, so the " +
        "game itself connects to the Archipelago server — no emulator, no bridge. " +
        "You need your own copy of Rift Wizard on Steam; the launcher downloads the " +
        "mod files and guides you through dropping them into your game folder.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" in the Steam sense: any known AP marker file is present in
    /// the Steam game directory. (IsInstalled in the STAGING sense is handled
    /// separately via VersionFileName.)
    public bool IsInstalled => FindModMarkerFile() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Staging folder where the launcher downloads/extracts the mod zip. The
    /// actual game mod files go into the user's Steam install dir; this is
    /// the launcher's working copy / version stamp location.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "RiftWizard");

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// Self-contained settings sidecar for this plugin (install-dir override).
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "riftwizard_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The mod reports checks/items/goal to the AP server itself — the launcher
    // relays nothing. These exist for interface compatibility (ConnectsItself = true).
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
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(GH_OWNER, GH_REPO, ct));
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
        // 1. Resolve the latest release (pinned fallback when offline).
        progress.Report((2, "Checking latest Rift Wizard Archipelago release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a Rift Wizard Archipelago download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                RepoUrl + "/releases.");

        // 2. Download + extract to our staging folder (not directly into Steam install).
        await DownloadAndExtractModAsync(zipUrl, version, progress, ct);

        // 3. Stamp the staged version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        // 4. Detect the Steam install to surface copy instructions.
        string? steamDir = DetectSteamRiftWizardDir();

        progress.Report((100,
            $"Rift Wizard AP mod files {version} downloaded to: {GameDirectory}\n" +
            (steamDir != null
                ? $"Steam install found at: {steamDir}\n"
                  + "Copy all files from the download folder into your Rift Wizard "
                  + "Steam install folder to complete the install. Open Settings for "
                  + "the guided steps."
                : "Rift Wizard Steam install not found. Install Rift Wizard via Steam, "
                  + "then copy the mod files from the download folder into the game "
                  + "directory. Open Settings for the guided steps.")));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        // Verify means: at minimum the staging folder has something, and ideally
        // the mod marker files are present in the Steam install.
        return Directory.Exists(GameDirectory) &&
               Directory.EnumerateFiles(GameDirectory, "*.py", SearchOption.AllDirectories).Any();
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // ConnectsItself = true: the mod handles the AP connection in-game.
        // We just launch the game; the patched Python sources connect to AP.
        _ = session; // mod reads its own AP config; launcher holds no connection
        StartRiftWizard();
        return Task.CompletedTask;
    }

    /// Plain Rift Wizard runs fine without AP.
    public bool SupportsStandalone => true;

    /// The mod's embedded AP client owns the slot connection.
    public bool ConnectsItself => true;

    public string? BuiltAgainstDataPackageChecksum => null;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartRiftWizard();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ────────────────────────────

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
        var accent  = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x1B, 0x7A, 0x6E));
        var success = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var linkFg  = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Rift Wizard is your own copy of the game (Steam). The Archipelago " +
                   "mod patches the game's Python source files — you need to copy the " +
                   "patched files into your Rift Wizard Steam install folder. The launcher " +
                   "downloads the mod files to a staging folder and guides the copy step. " +
                   "Once in place, launch the game normally; the mod connects to the AP " +
                   "server from inside the game's own Python process.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: Steam install detection ─────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "RIFT WIZARD STEAM INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? steamDir    = DetectSteamRiftWizardDir();
        string? overrideDir = LoadOverrideDir();
        string? activeDir   = overrideDir ?? steamDir;

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = activeDir != null
                ? (overrideDir != null
                    ? "Using selected folder: " + activeDir
                    : "Detected Steam install: " + activeDir)
                : "Rift Wizard Steam install not detected. Pick your game folder below, " +
                  "or install Rift Wizard via Steam first.",
            FontSize = 11,
            Foreground = activeDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // Mod marker detection in the Steam install.
        string? markerFile = FindModMarkerFile();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = markerFile != null
                ? "Archipelago mod marker found: " + Path.GetFileName(markerFile)
                : "Archipelago mod files not found in the game folder yet " +
                  "(use Install on the Play tab, then copy files per the steps below).",
            FontSize = 11,
            Foreground = markerFile != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Install-dir override row.
        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = activeDir ?? "",
            IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Your Rift Wizard install folder (contains RiftWizard.exe). " +
                      "Detected from Steam automatically; set it here to override.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Rift Wizard install folder (contains RiftWizard.exe)",
                InitialDirectory = Directory.Exists(activeDir ?? "")
                                   ? activeDir!
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Rift Wizard folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                SaveOverrideDir(picked);
                dirBox.Text = picked;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Steam installs are detected automatically (appid 1271280). Use this " +
                   "picker for a non-standard Steam library or GOG/itch install.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Staged download location ────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "DOWNLOADED MOD FILES", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        bool staged = Directory.Exists(GameDirectory) &&
                      Directory.EnumerateFiles(GameDirectory, "*.py", SearchOption.AllDirectories).Any();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = staged
                ? $"Mod files staged at: {GameDirectory}"
                : "No mod files downloaded yet. Use Install on the Play tab first.",
            FontSize = 11,
            Foreground = staged ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 10),
        });

        if (staged)
        {
            var openBtn = new System.Windows.Controls.Button
            {
                Content = "Open mod files folder ↗",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(10, 6, 10, 6),
                Margin  = new System.Windows.Thickness(0, 0, 0, 10),
                Background  = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
                Foreground  = fg,
                BorderBrush = accent,
            };
            string staged_dir = GameDirectory;
            openBtn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo("explorer.exe", staged_dir)
                    { UseShellExecute = true }); }
                catch { }
            };
            panel.Children.Add(openBtn);
        }

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Rift Wizard on Steam (appid 1271280). Install it if you have not. " +
                "Use the folder picker above if it was not auto-detected.",
            "2. Download the mod: click Install on the Play tab. The launcher downloads " +
                "the latest Archipelago mod release and extracts it to a staging folder " +
                "(shown above).",
            "3. Copy mod files: open the staging folder (button above) and copy ALL the " +
                "Python (.py) files and any data folders into your Rift Wizard Steam " +
                "install directory — the same folder that contains RiftWizard.exe. " +
                "Overwrite if prompted.",
            "4. Launch Rift Wizard normally (from this launcher or Steam). The patched " +
                "Python files handle the Archipelago connection from inside the game.",
            "5. Configure the AP connection inside the game (server, slot name, password) " +
                "per the mod's documentation. The launcher cannot pre-fill this — the " +
                "mod reads its own settings file or uses an in-game menu.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Section: AP connection info ────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "ARCHIPELAGO CONNECTION", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "The Rift Wizard AP mod connects to the Archipelago server from inside " +
                   "the game's own Python process. This launcher does NOT hold an AP " +
                   "connection for this game — it launches the game and the mod does the " +
                   "rest. Configure your server, slot, and password in the mod's settings " +
                   "file or in-game menu. See the mod repository for the exact format.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Rift Wizard AP mod (GitHub) ↗",   RepoUrl),
            ("Rift Wizard mod releases ↗",       RepoUrl + "/releases"),
            ("Rift Wizard AP info ↗",            SetupGuideUrl),
            ("Archipelago Official ↗",           ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background      = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0),
                FontSize = 12, Margin = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = linkFg,
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
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
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

    // ── IGamePlugin.ValidateExistingInstall ───────────────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Rift Wizard install folder.";

        if (LooksLikeRiftWizardDir(folder))
            return null;

        // Check if user picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamFolderName);
            if (LooksLikeRiftWizardDir(nested)) return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Rift Wizard installation. Pick the folder " +
               "that contains RiftWizard.exe or rift_wizard.exe (for Steam this is " +
               @"typically ...\steamapps\common\Rift Wizard).";
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// "v1.2.3" → "1.2.3"; else trimmed; null for blank.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest release: returns (version, zipUrl).
    /// Tries /releases/latest; if no zip is found there, tries /releases (first page).
    /// Falls back to the pinned FallbackZipUrl when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(
        CancellationToken ct)
    {
        // Try /releases/latest first (most repos have it).
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                string? zip = PickBestZip(assets);
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* /releases/latest may not exist on forks — try /releases */ }

        // Try /releases (paginated list — take the first non-prerelease entry).
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    // Skip prereleases when a stable candidate has a zip.
                    bool prerelease = el.TryGetProperty("prerelease", out var pr) &&
                                      pr.ValueKind == JsonValueKind.True;

                    string? version = el.TryGetProperty("tag_name", out var t)
                        ? NormalizeTag(t.GetString())
                        : null;

                    if (version == null) continue;

                    if (el.TryGetProperty("assets", out var assets) &&
                        assets.ValueKind == JsonValueKind.Array)
                    {
                        string? zip = PickBestZip(assets);
                        if (zip != null)
                            return (version, zip);
                    }

                    // Even if no zip asset — a prerelease without assets is skipped
                    // until we find a stable release. First stable with any tag wins.
                    if (!prerelease)
                        return (version, null);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback below */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    /// From a release assets array, pick the best Windows-compatible .zip.
    /// Prefers "archipelago*.zip" or "riftwizard*.zip"; avoids Linux/Mac/source.
    private static string? PickBestZip(JsonElement assets)
    {
        string? preferred = null;
        string? anyZip    = null;

        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();
            if (!lower.EndsWith(".zip")) continue;
            if (lower.Contains("source") || lower.Contains("linux") ||
                lower.Contains("ubuntu") || lower.Contains("mac")   ||
                lower.Contains("osx")    || lower.Contains("darwin")) continue;

            anyZip ??= url;
            if (preferred == null &&
                (lower.Contains("archipelago") || lower.Contains("riftwizard") ||
                 lower.Contains("rift_wizard") || lower.Contains("rift-wizard")))
                preferred = url;
        }
        return preferred ?? anyZip;
    }

    // ── Private helpers — Steam / Rift Wizard detection ───────────────────────

    /// Detect the Steam Rift Wizard install by reading the Steam registry roots,
    /// parsing libraryfolders.vdf, and locating the appmanifest_1271280.acf.
    private static string? DetectSteamRiftWizardDir()
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

                    string common     = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeRiftWizardDir(candidate)) return candidate;
                    }
                    // Conventional folder name fallback.
                    string conventional = Path.Combine(common, SteamFolderName);
                    if (LooksLikeRiftWizardDir(conventional)) return conventional;
                }
                catch { /* try next library */ }
            }
        }
        return null;
    }

    /// Resolve the game directory to use: saved override first, then Steam auto-detect.
    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeRiftWizardDir(ov)) return ov;
        try { return DetectSteamRiftWizardDir(); }
        catch { return null; }
    }

    /// True when a folder looks like a Rift Wizard install (contains the exe or a
    /// Python main script alongside game data).
    private static bool LooksLikeRiftWizardDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            foreach (string exeName in RiftWizardExeNames)
                if (File.Exists(Path.Combine(dir, exeName))) return true;
            // Python-layout fallback: a "RiftWizard.py" or "run_game.py" at the root.
            if (File.Exists(Path.Combine(dir, "RiftWizard.py")) ||
                File.Exists(Path.Combine(dir, "run_game.py"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Steam root directories from the registry (HKCU SteamPath + HKLM).
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

    /// All Steam library roots from steamapps\libraryfolders.vdf.
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

    /// Extract "path" values from a libraryfolders.vdf body.
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

    /// Read "installdir" from an appmanifest_*.acf file.
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

    /// Safe registry value read; null on any failure.
    private static string? ReadRegistryString(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(subKey);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    // ── Private helpers — mod marker detection ────────────────────────────────

    /// Find the first AP mod marker file in the Rift Wizard Steam install dir.
    /// Returns the full path or null if no marker is found (mod not installed).
    private string? FindModMarkerFile()
    {
        string? dir = ResolveGameDir();
        if (dir == null) return null;
        try
        {
            foreach (string marker in ModMarkerFiles)
            {
                string path = Path.Combine(dir, marker);
                if (File.Exists(path)) return path;
            }
            // Broader scan: any file whose name starts with "AP_" or "Archipelago".
            foreach (string f in Directory.EnumerateFiles(dir, "*.py", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(f);
                if (name.StartsWith("AP_", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("Archipelago", StringComparison.OrdinalIgnoreCase))
                    return f;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartRiftWizard()
    {
        string? dir = ResolveGameDir();

        if (dir != null)
        {
            // Try each known exe name in the detected/override install directory.
            foreach (string exeName in RiftWizardExeNames)
            {
                string exePath = Path.Combine(dir, exeName);
                if (!File.Exists(exePath)) continue;

                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName         = exePath,
                    WorkingDirectory = dir,
                    UseShellExecute  = true,
                }) ?? throw new InvalidOperationException("Failed to start Rift Wizard.");

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
                return;
            }
        }

        // Fall back to the Steam URI (game not found by path, but Steam is present).
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort — Steam owns the process
                return;
            }
            catch { /* fall through */ }
        }

        throw new FileNotFoundException(
            "Could not find RiftWizard.exe. Open this game's Settings and pick " +
            "your Rift Wizard install folder, or install it via Steam.",
            "RiftWizard.exe");
    }

    // ── Private helpers — download / extract mod zip ──────────────────────────

    /// Download the mod zip and extract into GameDirectory (the staging folder).
    /// A single top-level sub-folder is flattened so .py files sit at the root.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"riftwizard-ap-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading Rift Wizard Archipelago mod {version}..."));
            using var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempZip))
            {
                var buf = new byte[81920];
                int bytesRead;
                while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                    downloaded += bytesRead;
                    if (total > 0)
                    {
                        int pct = (int)(5 + 60 * downloaded / total);
                        progress.Report((pct, $"Downloading... {downloaded / 1_000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting mod files to staging folder..."));
            Directory.CreateDirectory(GameDirectory);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, GameDirectory,
                overwriteFiles: true);

            // Flatten a single top-level wrapper sub-folder (e.g. "Archipelago-main/")
            // so .py files land directly in GameDirectory.
            if (Directory.GetFiles(GameDirectory).Length == 0)
            {
                string[] subdirs = Directory.GetDirectories(GameDirectory);
                if (subdirs.Length == 1)
                {
                    string sub = subdirs[0];
                    foreach (string fileSrc in Directory.EnumerateFiles(sub, "*",
                                 SearchOption.AllDirectories))
                    {
                        string rel     = Path.GetRelativePath(sub, fileSrc);
                        string fileDst = Path.Combine(GameDirectory, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                        File.Move(fileSrc, fileDst, overwrite: true);
                    }
                    try { Directory.Delete(sub, recursive: true); } catch { }
                }
            }

            progress.Report((90, "Mod files extracted to staging folder."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class RiftWizardSettings
    {
        public string? InstallOverride { get; set; }
    }

    private RiftWizardSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<RiftWizardSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(RiftWizardSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s,
                    new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
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
}
