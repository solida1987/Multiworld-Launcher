using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using LauncherV2.Core;

namespace LauncherV2.Plugins.ADifficultGameAboutClimbing;

// ═══════════════════════════════════════════════════════════════════════════════
// ADifficultGameAboutClimbingPlugin — install / launch for
// "A Difficult Game About Climbing" (Pontypants, 2024) played through the
// GrabbingChecks Archipelago BepInEx mod by BlastSlimey.
//
// ── VERIFIED FACTS (2026-06-14) ──────────────────────────────────────────────
//
//   * THE AP GAME STRING is "A Difficult Game About Climbing"
//     Verified from Connection.cs line:
//       TryConnectAndLogin("A Difficult Game About Climbing", activeSlot, …)
//     GameId = "adgac". ApWorldName = "A Difficult Game About Climbing".
//     The apworld file ships as "difficult_climbing.apworld".
//
//   * STEAM APPID is 2497920 (verified via Steam search 2026-06-14; released
//     March 6, 2024, developed and published by Pontypants).
//
//   * THE MOD is "GrabbingChecks" by BlastSlimey
//     (https://github.com/BlastSlimey/GrabbingChecks). It is a BepInEx 5
//     (netstandard2.1) plugin. Latest release: v0.1.2 (updated for AP 0.6.2).
//     Each GitHub release ships two assets:
//       BlastSlimey-GrabbingChecks-{version}.zip  (BepInEx plugin)
//       difficult_climbing.apworld
//     The plugin DLL is BlastSlimey.GrabbingChecks.dll and lives in
//     <Game>/BepInEx/plugins/ after installation.
//
//   * THE BepInEx CONFIG FILE is at:
//       <Game>/BepInEx/config/GrabbingChecks.cfg
//     It is standard BepInEx INI format with JSON-encoded values.
//     Relevant keys (verified from Config.cs):
//
//       [General]
//       ConnectionList = [{"slot":"SlotName","addressPort":"host:38281","password":""}]
//       ActiveSlot = 0
//       OfflineItems = {...}
//
//     The launcher CAN pre-fill the connection by writing this file before
//     launch. ActiveSlot = 0 means "use the first entry in ConnectionList".
//     ActiveSlot = -1 disables connecting (offline mode).
//
//   * CONNECTION: The mod reads the BepInEx config on startup and connects
//     automatically. This launcher writes the config before launching so the
//     mod connects to the right AP session without user input in-game.
//     ConnectsItself = true (the BepInEx mod owns the AP slot connection).
//
// ── WHAT THIS PLUGIN DOES ────────────────────────────────────────────────────
//   1. DETECT the Steam install via the registry + libraryfolders.vdf,
//      locating steamapps/common/A Difficult Game About Climbing via
//      appmanifest_2497920.acf. A manual install-dir OVERRIDE is also
//      supported and takes precedence; validated by presence of
//      "A Difficult Game About Climbing.exe". Persisted in
//      Games/ROMs/adgac/adgac_launcher.json (per-plugin sidecar, NOT
//      Core/SettingsStore).
//   2. INSTALL/UPDATE = download the GitHub release zip for GrabbingChecks
//      and extract the plugin DLL into <Game>/BepInEx/plugins/. The zip is
//      a standard BepInEx plugin package. IMPORTANT: BepInEx itself is NOT
//      bundled in the mod zip — the guided steps tell the user to install
//      BepInEx 5 for Unity (Mono) first. The plugin makes the BepInEx
//      presence check clear in the settings panel.
//   3. CONNECTION PREFILL = before LaunchAsync, write the BepInEx config with
//      the session's server/slot/password as entry 0, ActiveSlot = 0. After
//      the game exits (or on StopAsync), restore ActiveSlot = -1 so a
//      subsequent standalone launch does not try to reconnect.
//   4. LAUNCH = run "A Difficult Game About Climbing.exe" from the detected
//      install, or fall back to steam://rungameid/2497920.
//   5. STANDALONE = same launch, config left as-is (or prefilled) without
//      modifying slot/password — the game connects if a previous session was
//      prefilled, or runs offline if ActiveSlot is -1.
//
// ── BUILD NOTE ───────────────────────────────────────────────────────────────
//   UseWindowsForms=true + UseWPF=true → CS0104 on ambiguous types. All WPF
//   UI types are FULLY QUALIFIED below. No file-level using aliases (CS1537).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ADifficultGameAboutClimbingPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// Steam AppID — verified 2026-06-14 via Steam search.
    private const string SteamAppId = "2497920";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The exact folder name under steamapps/common.
    private const string SteamCommonFolderName = "A Difficult Game About Climbing";

    /// The game's main executable.
    private const string GameExeName = "A Difficult Game About Climbing.exe";

    /// GitHub releases base URL for GrabbingChecks by BlastSlimey.
    private const string GitHubReleasesApiUrl =
        "https://api.github.com/repos/BlastSlimey/GrabbingChecks/releases";

    private const string GitHubReleasesPageUrl =
        "https://github.com/BlastSlimey/GrabbingChecks/releases";

    /// Pinned fallback version (verified live 2026-06-14).
    private const string FallbackVersion = "0.1.2";
    private static readonly string FallbackZipUrl =
        $"https://github.com/BlastSlimey/GrabbingChecks/releases/download/{FallbackVersion}" +
        $"/BlastSlimey-GrabbingChecks-{FallbackVersion}.zip";

    /// BepInEx config file name (relative to <Game>/BepInEx/config/).
    private const string BepInExConfigFileName = "GrabbingChecks.cfg";

    /// BepInEx 5 for Unity (Mono) download page.
    private const string BepInEx5DownloadUrl =
        "https://github.com/BepInEx/BepInEx/releases";

    private const string SetupGuideUrl =
        "https://github.com/BlastSlimey/GrabbingChecks";

    private const string ArchipelagoSite = "https://archipelago.gg";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Archipelago-Launcher/2.0" },
        },
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "adgac";
    public string DisplayName => "A Difficult Game About Climbing";
    public string Subtitle    => "Native PC · BepInEx mod";

    /// EXACT AP game string — verified from GrabbingChecks Connection.cs:
    ///   Session.TryConnectAndLogin("A Difficult Game About Climbing", slot, …)
    public string ApWorldName => "A Difficult Game About Climbing";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "adgac.png");

    /// Warm orange — climbing, effort, perseverance.
    public string ThemeAccentColor => "#E0700A";

    public string[] GameBadges =>
        new[] { "Steam · BepInEx mod", "Physics climbing", "16 zones" };

    public string Description =>
        "A Difficult Game About Climbing, the physics-based climbing game by " +
        "Pontypants (2024), played through the GrabbingChecks Archipelago mod by " +
        "BlastSlimey — a BepInEx 5 plugin that turns the game's 16 surface zones " +
        "into Archipelago location checks. Grip Strength, Metal Beam items and Cog " +
        "modifiers are shuffled into the multiworld; traps include a Deafness Trap. " +
        "You bring your own copy of the game (Steam appid 2497920) and the mod is " +
        "added on top with BepInEx 5. The launcher detects your Steam install, " +
        "downloads and installs the mod, pre-fills the connection config for your " +
        "Archipelago session, and launches the game — no in-game setup required.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    public bool ConnectsItself   => true;
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "Games", "ADifficultGameAboutClimbing");

    private string RomLibraryDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SettingsSidecarPath =>
        Path.Combine(RomLibraryDirectory, "adgac_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The GrabbingChecks mod handles the AP session directly.
    // These events exist for interface compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── CheckForUpdate ────────────────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            string? dll = FindInstalledModDll();
            InstalledVersion = dll != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch { InstalledVersion = null; }

        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync("BlastSlimey", "GrabbingChecks", ct));
        }
        catch { AvailableVersion = null; }
    }

    // ── InstallOrUpdate ───────────────────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((2, "Locating your A Difficult Game About Climbing installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find A Difficult Game About Climbing. Open this game's " +
                "Settings and pick your install folder (the one containing \"" +
                GameExeName + "\"), or install the game via Steam first. " +
                "The Archipelago mod is added on top of your own copy of the game.");

        string pluginsDir = Path.Combine(gameDir, "BepInEx", "plugins");

        progress.Report((6, "Checking the latest GrabbingChecks release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the GrabbingChecks download on GitHub. Check your " +
                "internet connection, or download the mod manually from: " +
                GitHubReleasesPageUrl);

        // Ensure BepInEx is present; warn clearly if not (we cannot install it
        // automatically since the user needs to run its installer).
        bool bepInExPresent = Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));
        if (!bepInExPresent)
            progress.Report((9,
                "BepInEx not found yet — install it before the mod will work " +
                "(see the Settings panel for the guided steps). Staging the mod now..."));

        progress.Report((10, $"Downloading GrabbingChecks {version}..."));
        await DownloadAndExtractModAsync(zipUrl, version, pluginsDir, progress, ct);

        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"GrabbingChecks {version} installed into the BepInEx plugins folder. " +
            (bepInExPresent
                ? "BepInEx looks present. "
                : "IMPORTANT: BepInEx 5 (Unity Mono) is NOT present — install it " +
                  "before launching (see the Settings panel for the guided steps and " +
                  "download link). ") +
            "The launcher will write the connection config before each AP session. " +
            "Click Play (with AP session) to launch."));
    }

    // ── VerifyInstall ─────────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── LaunchAsync (AP session) ──────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // Pre-fill the BepInEx config so the mod connects automatically.
        string? gameDir = ResolveGameDir();
        if (gameDir != null)
            WriteModConfig(gameDir, session);

        StartGame(gameDir);
        return Task.CompletedTask;
    }

    // ── LaunchStandalone ──────────────────────────────────────────────────────

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // Launch without an AP session; do not touch the existing config.
        StartGame(ResolveGameDir());
        return Task.CompletedTask;
    }

    // ── StopAsync ─────────────────────────────────────────────────────────────

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;

        // Disable auto-connect after an AP session ends, so a subsequent
        // standalone launch does not attempt to reconnect to a stale session.
        try
        {
            string? gameDir = ResolveGameDir();
            if (gameDir != null) DisableModAutoConnect(gameDir);
        }
        catch { /* non-fatal */ }

        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ────────────────────────────

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
        var success = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0xE0, 0x70, 0x0A));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20),
        };

        string? gameDir      = ResolveGameDir();
        string? overrideDir  = LoadOverrideDir();
        string? modDll       = FindInstalledModDll();
        bool    bepInExOk    = gameDir != null
                               && Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));

        // ── How it works header ───────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "A Difficult Game About Climbing is your own game (Steam) with the " +
                "GrabbingChecks mod added on top via BepInEx 5 (Unity Mono). " +
                "The launcher installs the mod, writes the BepInEx connection config " +
                "before each AP session so the mod connects automatically, and launches " +
                "the game. BepInEx must be installed separately (one-time setup — see " +
                "the guided steps below).",
            FontSize    = 11,
            Foreground  = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin      = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: Game install ─────────────────────────────────────────────
        AddSectionHeader(panel, "GAME INSTALL (Steam appid 2497920)", muted);

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Game not detected. Pick your install folder below, or install via Steam first.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text         = detectMsg,
            FontSize     = 11,
            Foreground   = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = bepInExOk
                    ? "BepInEx 5 found (BepInEx\\core present)."
                    : "BepInEx 5 NOT found. Install it before the mod will work " +
                      "(see the guided steps below).",
            FontSize     = 11,
            Foreground   = bepInExOk ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                    ? "GrabbingChecks mod found: " + modDll
                    : "GrabbingChecks mod not found. Use the Install button on the " +
                      "Play tab to download and install it.",
            FontSize     = 11,
            Foreground   = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = overrideDir ?? gameDir ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your game install folder — the one containing " +
                          "\"A Difficult Game About Climbing.exe\".",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content     = "Select folder...",
            Width       = 120,
            Padding     = new System.Windows.Thickness(0, 6, 0, 6),
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
                Title            = "Select your A Difficult Game About Climbing install folder",
                InitialDirectory =
                    Directory.Exists(overrideDir ?? gameDir ?? "")
                    ? (overrideDir ?? gameDir!)
                    : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() != true) return;

            string picked = dlg.FolderName;
            string? err   = ValidateGameFolder(picked);
            if (err != null)
            {
                System.Windows.MessageBox.Show(
                    err,
                    "Not a valid game folder",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }
            // If user picked a parent that contains the game folder, descend.
            if (!LooksLikeGameDir(picked))
            {
                string nested = Path.Combine(picked, SteamCommonFolderName);
                if (LooksLikeGameDir(nested)) picked = nested;
            }
            SaveOverrideDir(picked);
            dirBox.Text = picked;
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Steam installs are detected automatically (appid 2497920). Use this " +
                "picker for a non-standard library or a manual install.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ───────────────────────────────────────────────
        AddSectionHeader(panel, "CONNECTING", muted);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Before each AP session the launcher writes the BepInEx connection " +
                "config (BepInEx/config/GrabbingChecks.cfg) with your server, slot " +
                "and password. The mod reads this on startup and connects automatically " +
                "— no in-game typing required. After the session the config is reset " +
                "so a subsequent standalone launch does not try to reconnect.",
            FontSize     = 11,
            Foreground   = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup ─────────────────────────────────────────────
        AddSectionHeader(panel, "GUIDED SETUP (first time)", muted);
        foreach (string step in new[]
        {
            "1. Own A Difficult Game About Climbing (Steam appid 2497920). Install it " +
                "if you have not. Use \"Select folder...\" above if it was not detected.",
            "2. Install BepInEx 5 for Unity (Mono) into your game folder. Download it " +
                "from the link below, extract it into the game's root folder (the folder " +
                "that contains \"A Difficult Game About Climbing.exe\"), then launch the " +
                "game ONCE without any mods to let BepInEx initialize.",
            "3. Use the Install button on the Play tab to download and install the " +
                "GrabbingChecks mod. The launcher places it into BepInEx/plugins/ " +
                "automatically.",
            "4. Click Play (with an AP session active) — the launcher writes the " +
                "connection config and launches the game. The mod connects automatically " +
                "on startup.",
            "5. For standalone play (no AP session): use Launch Standalone. The mod " +
                "will run in offline mode (no AP connection, using the offline inventory " +
                "from the config).",
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

        // ── Links ─────────────────────────────────────────────────────────────
        AddSectionHeader(panel, "LINKS", muted);
        foreach (var (label, url) in new[]
        {
            ("BepInEx 5 Releases (Unity Mono) ↗",  BepInEx5DownloadUrl),
            ("GrabbingChecks mod (GitHub) ↗",      SetupGuideUrl),
            ("GrabbingChecks Releases ↗",          GitHubReleasesPageUrl),
            ("Archipelago Official ↗",             ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding             = new System.Windows.Thickness(0, 2, 0, 2),
                Background          = System.Windows.Media.Brushes.Transparent,
                BorderThickness     = new System.Windows.Thickness(0),
                FontSize            = 12,
                Margin              = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground          = new System.Windows.Media.SolidColorBrush(
                                          System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
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
            string json = await _http.GetStringAsync(GitHubReleasesApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string ver  = el.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                string body = el.TryGetProperty("body", out var b)     ? b.GetString() ?? "" : "";
                string? url = el.TryGetProperty("html_url", out var u) ? u.GetString() : null;

                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var p) &&
                    p.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(p.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   $"GrabbingChecks {ver}",
                    Body:    body,
                    Version: ver,
                    Date:    date,
                    Url:     url
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// Resolve the latest release from the GitHub releases API.
    /// Returns the version tag and the ZIP download URL.
    /// Falls back to the pinned version when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GitHubReleasesApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                goto useFallback;

            foreach (var release in doc.RootElement.EnumerateArray())
            {
                // Skip pre-releases and draft releases.
                bool isDraft      = release.TryGetProperty("draft", out var d)      && d.GetBoolean();
                bool isPrerelease = release.TryGetProperty("prerelease", out var pr) && pr.GetBoolean();
                if (isDraft || isPrerelease) continue;

                string? tag = release.TryGetProperty("tag_name", out var t)
                              ? t.GetString() : null;
                if (string.IsNullOrWhiteSpace(tag)) continue;

                // Find the BlastSlimey-GrabbingChecks-*.zip asset.
                if (!release.TryGetProperty("assets", out var assets) ||
                    assets.ValueKind != JsonValueKind.Array) continue;

                foreach (var asset in assets.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var n)
                                   ? n.GetString() : null;
                    string? dlUrl = asset.TryGetProperty("browser_download_url", out var u)
                                    ? u.GetString() : null;

                    if (name != null &&
                        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                        name.IndexOf("GrabbingChecks", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return (tag!, dlUrl);
                    }
                }
                // Release exists but no matching zip asset — fall through to fallback.
                break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* network failure → pinned fallback */ }

        useFallback:
        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — Steam / game detection ──────────────────────────────

    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeGameDir(ov)) return ov;

        try { return DetectSteamGameDir(); }
        catch { return null; }
    }

    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(dir)
                   && Directory.Exists(dir)
                   && File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

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

                    string common   = Path.Combine(steamapps, "common");
                    string? instDir = ReadAcfInstallDir(manifest);
                    if (instDir != null)
                    {
                        string candidate = Path.Combine(common, instDir);
                        if (LooksLikeGameDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeGameDir(conventional)) return conventional;
                }
                catch { /* try next library */ }
            }
        }
        return null;
    }

    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(
            Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormalizeSteamPath(hkcu);

        string? hklm = ReadRegistryString(
            Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormalizeSteamPath(hklm);

        string? hklm2 = ReadRegistryString(
            Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm2)) yield return NormalizeSteamPath(hklm2);

        string? px86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(px86)) yield return Path.Combine(px86, "Steam");
    }

    private static string NormalizeSteamPath(string p) => p.Replace('/', '\\').TrimEnd('\\');

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

    // ── Private helpers — mod detection ──────────────────────────────────────

    private string? FindInstalledModDll()
    {
        try
        {
            string? game = ResolveGameDir();
            if (game == null) return null;

            string pluginsDir = Path.Combine(game, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            // Look for any DLL that references "GrabbingChecks".
            foreach (string dll in Directory.EnumerateFiles(
                pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name.IndexOf("GrabbingChecks", StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }

            // Also accept a plugins sub-folder named "GrabbingChecks" that
            // contains at least one DLL.
            foreach (string sub in Directory.EnumerateDirectories(
                pluginsDir, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(sub).IndexOf(
                    "GrabbingChecks", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    if (Directory.EnumerateFiles(sub, "*.dll", SearchOption.AllDirectories).Any())
                        return sub;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame(string? gameDir)
    {
        string? exe = gameDir != null ? Path.Combine(gameDir, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = gameDir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException(
                "Failed to start A Difficult Game About Climbing.");

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
            return;
        }

        // Fall back to Steam.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true;
                return;
            }
            catch { }
        }

        throw new FileNotFoundException(
            "Could not find \"A Difficult Game About Climbing.exe\". Open this " +
            "game's Settings and pick your install folder, or install the game via Steam.",
            GameExeName);
    }

    // ── Private helpers — BepInEx config (connection pre-fill) ───────────────

    /// Write the GrabbingChecks.cfg so the mod connects to the AP session
    /// automatically on startup. BepInEx uses an INI-like format; JSON values
    /// are stored as escaped strings within it.
    ///
    /// Format (verified from Config.cs):
    ///   [General]
    ///   ConnectionList = [{"slot":"NAME","addressPort":"HOST:PORT","password":"PW"}]
    ///   ActiveSlot = 0
    ///   OfflineItems = {"Grip Strength":3,...}
    private static void WriteModConfig(string gameDir, ApSession session)
    {
        string configDir  = Path.Combine(gameDir, "BepInEx", "config");
        string configPath = Path.Combine(configDir, BepInExConfigFileName);
        Directory.CreateDirectory(configDir);

        // Parse host and port from the session URI.
        string addressPort = ParseAddressPort(session.ServerUri);
        string slot        = session.SlotName;
        string password    = session.Password ?? "";

        // Build the JSON value for ConnectionList (single-entry; the mod uses
        // ActiveSlot = 0 to select it).
        // Use System.Text.Json to ensure proper escaping.
        string connectionList = JsonSerializer.Serialize(
            new[] { new { addressPort, slot, password } });

        // Default offline inventory (from Config.cs defaults).
        const string offlineItems =
            "{\"Grip Strength\":3,\"Swinging Metal Beam\":1," +
            "\"Metal Beam Angle Increase\":2,\"Deafness Trap\":0," +
            "\"Rotating Cog Repair\":1,\"Rotating Cog Halting\":0,\"Side Cog Halting\":0}";

        // BepInEx config format: standard INI with section headers.
        // String values do not need additional quoting in BepInEx cfg files —
        // the entire line after the ' = ' is the value.
        var sb = new StringBuilder();
        sb.AppendLine("[General]");
        sb.AppendLine();
        sb.AppendLine("## A list of connection details, so you don't have to re-enter them");
        sb.AppendLine("## every time you play another slot.");
        sb.AppendLine("# Setting type: String");
        sb.AppendLine($"ConnectionList = {connectionList}");
        sb.AppendLine();
        sb.AppendLine("## The ConnectionList entry to use for connecting to a server.");
        sb.AppendLine("## Begins with 0 as the first entry. Use -1 to disable connecting.");
        sb.AppendLine("# Setting type: Int32");
        sb.AppendLine("ActiveSlot = 0");
        sb.AppendLine();
        sb.AppendLine("## Define a set inventory for offline mode.");
        sb.AppendLine("# Setting type: String");
        sb.AppendLine($"OfflineItems = {offlineItems}");

        File.WriteAllText(configPath, sb.ToString(), new UTF8Encoding(false));
    }

    /// Set ActiveSlot = -1 in the config to disable auto-connect on the next
    /// (standalone) launch. Preserves existing ConnectionList and OfflineItems.
    private static void DisableModAutoConnect(string gameDir)
    {
        string configPath = Path.Combine(gameDir, "BepInEx", "config", BepInExConfigFileName);
        if (!File.Exists(configPath)) return;

        try
        {
            string text = File.ReadAllText(configPath);
            // Replace "ActiveSlot = 0" (or any integer) with "ActiveSlot = -1".
            string updated = Regex.Replace(
                text,
                @"^(ActiveSlot\s*=\s*)-?\d+",
                "${1}-1",
                RegexOptions.Multiline);
            File.WriteAllText(configPath, updated, new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    /// Parse "host:port" from an AP server URI such as "archipelago.gg:38281"
    /// or "wss://archipelago.gg:38281". Falls back to "host:38281" if no port.
    private static string ParseAddressPort(string serverUri)
    {
        if (string.IsNullOrWhiteSpace(serverUri)) return "archipelago.gg:38281";

        // Strip protocol prefix if present.
        string s = serverUri;
        foreach (string prefix in new[] { "wss://", "ws://", "https://", "http://" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s.Substring(prefix.Length);
                break;
            }
        }

        // If it already looks like host:port, use it as-is.
        if (s.Contains(':')) return s;

        // No port — append the default AP port.
        return s + ":38281";
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string pluginsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"adgac-grabs-{version}-{Guid.NewGuid():N}.zip");
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"adgac-grabs-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((12, $"Downloading GrabbingChecks {version}..."));
            using (var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long total      = response.Content.Headers.ContentLength ?? -1;
                long downloaded = 0;

                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tempZip);
                var buf = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    downloaded += read;
                    if (total > 0)
                    {
                        int pct = (int)(12 + 50 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading... {downloaded / 1024}KB" +
                            (total > 0 ? $" / {total / 1024}KB" : "")));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((68, "Extracting mod package..."));
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            progress.Report((82, $"Installing mod into BepInEx\\plugins\\..."));
            Directory.CreateDirectory(pluginsDir);

            // The GitHub release zip (BlastSlimey-GrabbingChecks-*.zip) is a
            // BepInEx plugin zip. Its payload is the plugin DLL (and any
            // companion files) at the zip root, or under a "plugins" or
            // "BepInEx/plugins" sub-folder. We install into a named sub-folder
            // so it is clearly identified.
            string destDir   = Path.Combine(pluginsDir, "GrabbingChecks");
            string payloadRoot = ResolvePluginPayloadRoot(tempDir);
            CopyDirectoryContents(payloadRoot, destDir);

            progress.Report((96, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))       File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static string ResolvePluginPayloadRoot(string extractedRoot)
    {
        try
        {
            // 1. .../BepInEx/plugins
            foreach (string dir in Directory.EnumerateDirectories(
                extractedRoot, "plugins", SearchOption.AllDirectories))
            {
                string parent = Path.GetFileName(Path.GetDirectoryName(dir) ?? "");
                if (parent.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) &&
                    DirectoryHasDll(dir))
                    return dir;
            }

            // 2. Top-level "plugins" folder.
            string topPlugins = Path.Combine(extractedRoot, "plugins");
            if (Directory.Exists(topPlugins) && DirectoryHasDll(topPlugins))
                return topPlugins;
        }
        catch { }

        // 3. The extraction root (DLL sits next to manifest / readme).
        return extractedRoot;
    }

    private static bool DirectoryHasDll(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }

    private static void CopyDirectoryContents(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(
            sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
    }

    // ── Private helpers — settings sidecar ───────────────────────────────────

    private sealed class AdgacSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private AdgacSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<AdgacSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(AdgacSettings s)
    {
        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
            File.WriteAllText(
                SettingsSidecarPath,
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

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings();
        s.ModVersion = v;
        SaveSettings(s);
    }

    // ── Private helpers — folder validation ───────────────────────────────────

    private string? ValidateGameFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your game install folder.";

        if (LooksLikeGameDir(folder)) return null;

        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeGameDir(nested)) return null;
        }
        catch { }

        return "That does not look like an A Difficult Game About Climbing installation. " +
               "Pick the folder that contains \"A Difficult Game About Climbing.exe\". " +
               "For Steam this is usually ...\\steamapps\\common\\" + SteamCommonFolderName + ".";
    }

    // ── Private helpers — UI ──────────────────────────────────────────────────

    private static void AddSectionHeader(
        System.Windows.Controls.StackPanel panel,
        string text,
        System.Windows.Media.Brush foreground)
    {
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = text,
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = foreground,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });
    }
}
