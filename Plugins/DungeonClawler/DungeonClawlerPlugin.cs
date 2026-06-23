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
using Microsoft.Win32;
using LauncherV2.Core;

// NOTE: WPF UI types are FULLY QUALIFIED throughout this file (System.Windows.*).
// The real launcher project sets <UseWindowsForms>true</UseWindowsForms> alongside
// WPF, so the bare names Button / TextBox / Color / Brushes / MessageBox /
// FontWeights / HorizontalAlignment collide between System.Windows.Forms and
// System.Windows[.Controls/.Media] (CS0104). Qualifying every UI type avoids that.
// No file-level aliases (using X = System.Windows...) either — GlobalUsings.cs
// already imports them globally and a second file-level alias is CS1537.

namespace LauncherV2.Plugins.DungeonClawler;

// ═══════════════════════════════════════════════════════════════════════════════
// DungeonClawlerPlugin — install / launch for "Dungeon Clawler" (Funtastic,
// 2024) played through Clawrchipelago, a BepInEx 5 / Harmony mod by agilbert1412
// that IS the in-game Archipelago client. ConnectsItself = true (the mod owns the
// AP slot — the launcher must NOT hold its own ApClient while the game runs).
//
// ── VERIFIED FACTS (2026-06-14, checked online against repo + GitHub API) ──────
//
//   * AP GAME STRING: "Dungeon Clawler"
//     Verified in DungeonClawlerArchipelagoClient.cs:
//       public override string GameName => "Dungeon Clawler";
//     GameId (internal / filesystem) = "dungeon_clawler".
//
//   * STEAM APP ID: 2356780
//     Verified from store.steampowered.com/app/2356780/Dungeon_Clawler/
//     Demo AppID: 2878040 (NOT used here).
//
//   * MOD FRAMEWORK: BepInEx 5.x (netstandard2.1) + HarmonyLib.
//     The game is a Unity Mono title (2020.3 LTS).
//     BepInEx pack for Unity Mono: the "Clawrchipelago Full" zip on GitHub
//     already includes a pre-configured BepInEx. The "Plugin only" zip is for
//     UPDATES to an existing BepInEx install. This plugin installs the Full zip
//     for first-time setup (BepInEx included), and the Plugin zip for updates.
//
//   * GITHUB REPO: agilbert1412/Clawrchipelago
//     Latest release: 1.0.3 (verified 2026-06-14).
//     Release asset names (verified against /releases API):
//       "Clawrchipelago.Full.{version}.zip"    — BepInEx + mod (first install)
//       "Clawrchipelago.Plugin.{version}.zip"  — mod DLL only (update)
//       "dungeon_clawler_{version}.apworld"     — AP world
//
//   * CONNECTION METHOD: JSON config file.
//     The mod reads "ArchipelagoConnectionInfo.json" from the Windows game folder
//     (the folder that contains DungeonClawler.exe / the BepInEx tree). The file
//     is loaded by the KaitoKid.ArchipelagoUtilities.Net library:
//       {
//         "HostUrl": "archipelago.gg",
//         "Port": 38281,
//         "SlotName": "Player",
//         "Password": "",
//         "DeathLink": false
//       }
//     This launcher PRE-WRITES that file at launch time (host/port/slot/password
//     parsed from the ApSession). DeathLink defaults to false (no setting exposed
//     in the launcher UI for now; the user can edit the JSON directly).
//     The field names come from ArchipelagoConnectionInfo.cs in agilbert1412/
//     ArchipelagoUtilities (the shared KaitoKid library used by Clawrchipelago):
//       HostUrl, Port, SlotName, DeathLink, Password.
//
//   * INSTALL STRUCTURE: the Full zip extracts with a top-level "Windows\" folder
//     whose CONTENTS must be merged into the Dungeon Clawler game directory (the
//     folder that contains DungeonClawler.exe). After extraction, BepInEx lives
//     at <gameDir>\BepInEx and the mod at
//     <gameDir>\BepInEx\plugins\Clawrchipelago\Clawrchipelago.dll.
//
//   * VERIFICATION SENTINEL: presence of
//     <gameDir>\BepInEx\plugins\Clawrchipelago\Clawrchipelago.dll
//     We also look for any *Archipelago*.dll under BepInEx\plugins (defensive).
//
// ── WHAT THIS PLUGIN DOES ──────────────────────────────────────────────────────
//   1. Detect the Steam Dungeon Clawler install via registry VDF parsing.
//      A manual install-dir override (settings folder picker) takes precedence
//      and is persisted in this plugin's OWN sidecar:
//        Games/ROMs/dungeon_clawler/dungeon_clawler_launcher.json
//   2. Install/Update: download the Full zip (first install) or Plugin zip
//      (update) from GitHub, extract into the game directory, stamp the version.
//   3. Launch: pre-write ArchipelagoConnectionInfo.json, then launch the game
//      exe (DungeonClawler.exe) or fall back to steam://rungameid/2356780.
//      The JSON is cleared/deleted on StopAsync so no plaintext password lingers.
//   4. SupportsStandalone = true: vanilla launch without a session is allowed
//      (the mod simply fails to connect and the game remains playable on its own).
//
// ── DEFENSIVE NOTES ────────────────────────────────────────────────────────────
//   * The Full zip's internal structure ("Windows\" sub-folder) is inferred from
//     the README install instructions ("Extract in the Windows folder of the game").
//     ResolveGameExeInDir() scans for DungeonClawler.exe defensively.
//   * BepInEx "run the game once to generate configs" is handled automatically
//     because the Full zip already ships a pre-configured BepInEx (the README
//     "Easy version" path explicitly skips the "run once" step — the config is
//     pre-baked in the Full zip). The Plugin-only path still needs BepInEx present
//     which we verify; if absent we fall back to the Full zip.
//   * No plaintext password is ever left on disk: the connection JSON is deleted
//     on StopAsync and only written at LaunchAsync time.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DungeonClawlerPlugin : IGamePlugin
{
    // ── Constants — GitHub release source ─────────────────────────────────────
    private const string GITHUB_OWNER    = "agilbert1412";
    private const string GITHUB_REPO     = "Clawrchipelago";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string RepoUrl         = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";

    // Pinned fallback (latest as of 2026-06-14, verified against /releases API).
    private const string FallbackVersion    = "1.0.3";
    private const string FallbackFullZip    = "Clawrchipelago.Full.1.0.3.zip";
    private const string FallbackPluginZip  = "Clawrchipelago.Plugin.1.0.3.zip";
    private static readonly string FallbackFullUrl =
        $"{RepoUrl}/releases/download/{FallbackVersion}/{FallbackFullZip}";
    private static readonly string FallbackPluginUrl =
        $"{RepoUrl}/releases/download/{FallbackVersion}/{FallbackPluginZip}";

    // Archipelago connection JSON filename — the mod reads this from the game dir.
    private const string AP_CONNECTION_FILE = "ArchipelagoConnectionInfo.json";

    // BepInEx sentinel — mod is installed when this DLL is present.
    private const string MOD_DLL_SUBPATH =
        @"BepInEx\plugins\Clawrchipelago\Clawrchipelago.dll";

    // Dungeon Clawler main executable name.
    private const string GAME_EXE = "DungeonClawler.exe";

    // Steam AppID: 2356780 (verified store.steampowered.com/app/2356780/)
    private const string STEAM_APP_ID = "2356780";
    private const string STEAM_FOLDER = "Dungeon Clawler";
    private static readonly string SteamRunUrl = $"steam://rungameid/{STEAM_APP_ID}";

    private const string SetupGuideUrl  = "https://archipelago.gg/tutorial/Dungeon%20Clawler/setup/en";
    private const string GameInfoUrl    = "https://archipelago.gg/games/Dungeon%20Clawler/info/en";
    private const string SteamStoreUrl  = $"https://store.steampowered.com/app/{STEAM_APP_ID}/";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "dungeon_clawler";
    public string DisplayName => "Dungeon Clawler";
    public string Subtitle    => "Native PC · Steam · BepInEx mod";

    /// EXACT AP game string — verified against DungeonClawlerArchipelagoClient.cs
    /// (GameName => "Dungeon Clawler").
    public string ApWorldName => "Dungeon Clawler";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "dungeon_clawler.png");

    public string ThemeAccentColor => "#8855DD";   // claw-purple
    public string[] GameBadges     => new[] { "BepInEx", "ConnectsItself" };

    public string Description =>
        "Dungeon Clawler is a roguelike deckbuilder where you grab cards with a " +
        "claw machine and fight your way through procedurally generated dungeons. " +
        "Clawrchipelago (by agilbert1412) adds full Archipelago multiworld support " +
        "as a BepInEx mod: items, traps, and location checks are wired into the " +
        "claw machine mechanics. The mod connects directly to the Archipelago " +
        "server — no emulator, no Lua bridge. Requires a legally owned Steam copy " +
        "of Dungeon Clawler (AppID 2356780).";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => IsModInstalled();
    public bool    IsRunning        { get; private set; }
    public bool    ConnectsItself   => true;
    public bool    SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root game directory (where DungeonClawler.exe lives). Auto-detected from
    /// Steam or set via the settings panel override.
    public string GameDirectory { get; set; } = string.Empty;

    private string ModDllPath   => Path.Combine(GameDirectory, MOD_DLL_SUBPATH);
    private string GameExePath  => Path.Combine(GameDirectory, GAME_EXE);
    private string ApConfigPath => Path.Combine(GameDirectory, AP_CONNECTION_FILE);

    private string VersionStampPath =>
        Path.Combine(GameDirectory, "dungeon_clawler_ap_version.dat");

    /// Plugin-local sidecar (override dir + future options). Never touches Core/SettingsStore.
    private string SettingsSidecarPath =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                     "dungeon_clawler_launcher.json");

    private string SettingsSidecarDir => Path.GetDirectoryName(SettingsSidecarPath)!;

    // ── Constructor ───────────────────────────────────────────────────────────

    public DungeonClawlerPlugin()
    {
        // Try to restore a user-overridden game dir from the sidecar.
        var settings = LoadSettings();
        if (!string.IsNullOrEmpty(settings.GameDirectoryOverride)
            && Directory.Exists(settings.GameDirectoryOverride))
        {
            GameDirectory = settings.GameDirectoryOverride;
        }
        else
        {
            // Auto-detect from Steam.
            GameDirectory = FindSteamInstallDir() ?? string.Empty;
        }
    }

    // ── Internal state ────────────────────────────────────────────────────────
    private Process? _gameProcess;
    private string?  _writtenApConfigPath;  // track what we wrote so we can clean up

    // ── AP bridge events ──────────────────────────────────────────────────────
    // Clawrchipelago's native AP client reports checks/goal to the server itself.
    // These events exist for interface compliance (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Installed version — read our own stamp.
        try
        {
            InstalledVersion =
                IsInstalled && File.Exists(VersionStampPath)
                    ? (await File.ReadAllTextAsync(VersionStampPath, ct)).Trim()
                    : null;
        }
        catch { InstalledVersion = null; }

        // Available version — query GitHub.
        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(GITHUB_OWNER, GITHUB_REPO, ct));
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(GameDirectory) || !Directory.Exists(GameDirectory))
            throw new InvalidOperationException(
                "Dungeon Clawler install directory not found. Open the Settings tab, " +
                "click Browse, and select the folder that contains DungeonClawler.exe. " +
                $"Steam AppID: {STEAM_APP_ID}.");

        progress.Report((2, "Checking latest Clawrchipelago release..."));
        var (version, fullUrl, pluginUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        // Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionStampPath)
            && (await File.ReadAllTextAsync(VersionStampPath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"Clawrchipelago {version} is already up to date."));
            return;
        }

        // Decide: full install (includes BepInEx) or plugin-only update?
        bool needsFull = !IsBepInExPresent();
        string zipUrl = needsFull ? (fullUrl ?? FallbackFullUrl)
                                  : (pluginUrl ?? FallbackPluginUrl);

        string zipLabel = needsFull
            ? $"Clawrchipelago Full (with BepInEx) {version}"
            : $"Clawrchipelago Plugin {version}";

        progress.Report((5, $"Downloading {zipLabel}..."));
        await DownloadAndExtractReleaseAsync(zipUrl, version, needsFull, progress, ct);

        // Stamp version.
        await File.WriteAllTextAsync(VersionStampPath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"Clawrchipelago {version} installed. Press Play to connect to Archipelago."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public async Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        if (!IsInstalled)
            throw new FileNotFoundException(
                "Clawrchipelago is not installed. Click Install Game first.",
                ModDllPath);

        // Pre-write ArchipelagoConnectionInfo.json so the mod connects on launch.
        await WriteApConnectionFileAsync(session, ct);

        string? exe = ResolveGameExe();
        if (exe != null)
            StartGameProcess(exe, GameDirectory);
        else
            // Steam fallback: launch via protocol if exe is missing from detected path.
            Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
    }

    public async Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;

        // Standalone: do NOT write an AP connection file. The mod will fail to
        // connect (no JSON) and the game remains playable without Archipelago.
        string? exe = ResolveGameExe();
        if (exe != null)
            StartGameProcess(exe, GameDirectory);
        else
            Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        DeleteApConnectionFile();   // remove plaintext password/credentials
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;   // Clawrchipelago receives items from AP server directly.

    public void OnApStateChanged(ApConnectionState state) { }   // mod owns its own HUD.

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var accent  = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x88, 0x55, 0xDD));

        var panel = new System.Windows.Controls.StackPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Section: Game Directory ────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "DUNGEON CLAWLER GAME DIRECTORY",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string detectedInfo = string.IsNullOrEmpty(GameDirectory)
            ? "Not detected (Browse to set manually)"
            : $"Detected: {GameDirectory}";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = detectedInfo,
            FontSize   = 11,
            Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin     = new System.Windows.Thickness(0, 0, 0, 6),
        });

        var dirRow = new System.Windows.Controls.DockPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = GameDirectory,
            IsReadOnly  = true,
            FontSize    = 11,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content     = "Browse...",
            Width       = 90,
            Padding     = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select Dungeon Clawler install folder (contains DungeonClawler.exe)",
                InitialDirectory = Directory.Exists(GameDirectory) ? GameDirectory
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() != true) return;
            string chosen = dlg.FolderName;
            // Validate: must contain the game exe.
            if (!File.Exists(Path.Combine(chosen, GAME_EXE)))
            {
                System.Windows.MessageBox.Show(
                    $"DungeonClawler.exe was not found in that folder.\n" +
                    $"Please select the folder that contains {GAME_EXE}.",
                    "Dungeon Clawler — Invalid Directory",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }
            GameDirectory = chosen;
            dirBox.Text   = chosen;
            var s = LoadSettings();
            s.GameDirectoryOverride = chosen;
            SaveSettings(s);
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        // Install status.
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = IsInstalled
                ? $"Clawrchipelago is installed" +
                  (InstalledVersion != null ? $" (v{InstalledVersion})" : "")
                : "Clawrchipelago is NOT installed — click Install in the Play tab",
            FontSize   = 11,
            Foreground = IsInstalled ? success : muted,
            Margin     = new System.Windows.Thickness(0, 6, 0, 14),
        });

        // ── Section: How it connects ───────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "ARCHIPELAGO CONNECTION",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "When you press Play, the launcher automatically writes " +
                   "ArchipelagoConnectionInfo.json into the Dungeon Clawler game " +
                   "folder with your server address, slot name, and password. " +
                   "Clawrchipelago reads this file on startup and connects to the " +
                   "Archipelago server automatically — no manual entry needed. " +
                   "The file is deleted when you stop the game so no credentials " +
                   "remain on disk.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: Steam prerequisite note ──────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "REQUIREMENTS",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 0, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Requires a legally owned copy of Dungeon Clawler (Steam AppID " +
                   $"{STEAM_APP_ID}). The launcher installs Clawrchipelago (BepInEx " +
                   "mod) into your existing Dungeon Clawler folder. The \"Install\" " +
                   "button downloads the Full zip (BepInEx pre-configured + mod) for " +
                   "first-time setup, or the Plugin-only zip for updates.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "LINKS",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 0, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Dungeon Clawler on Steam ↗",            SteamStoreUrl),
            ("Clawrchipelago (GitHub) ↗",             RepoUrl),
            ("Dungeon Clawler AP Setup Guide ↗",      SetupGuideUrl),
            ("Dungeon Clawler AP Game Info ↗",        GameInfoUrl),
            ("Archipelago Official ↗",                "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content          = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding          = new System.Windows.Thickness(0, 2, 0, 2),
                Background       = System.Windows.Media.Brushes.Transparent,
                BorderThickness  = new System.Windows.Thickness(0),
                FontSize         = 12,
                Margin           = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground       = new System.Windows.Media.SolidColorBrush(
                                       System.Windows.Media.Color.FromRgb(0x88, 0x55, 0xDD)),
                Cursor           = System.Windows.Input.Cursors.Hand,
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
            using var doc  = JsonDocument.Parse(json);
            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) &&
                    d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",      out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",      out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name",  out var t) ? NormalizeTag(t.GetString()) ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url",  out var u) ? u.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — AP connection file ──────────────────────────────────

    /// Write ArchipelagoConnectionInfo.json into the game directory so Clawrchipelago
    /// picks it up at startup. The field names match ArchipelagoConnectionInfo.cs in
    /// agilbert1412/ArchipelagoUtilities: HostUrl, Port, SlotName, DeathLink, Password.
    private async Task WriteApConnectionFileAsync(ApSession session, CancellationToken ct)
    {
        var (host, port) = ParseServerHostPort(session.ServerUri);

        var obj = new
        {
            HostUrl   = host,
            Port      = port,
            SlotName  = session.SlotName,
            Password  = session.Password ?? string.Empty,
            DeathLink = (bool?)false,
            ConnectionTags = Array.Empty<string>(),
        };

        string json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });

        // Write to a temp file first, then move atomically so the mod never reads
        // a half-written file (defensive on fast SSDs where the mod may start quickly).
        string configPath = ApConfigPath;
        string tempPath   = configPath + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, new UTF8Encoding(false), ct);
        File.Move(tempPath, configPath, overwrite: true);
        _writtenApConfigPath = configPath;
    }

    /// Delete the AP connection file we wrote — called at StopAsync to ensure
    /// no plaintext password lingers on disk after the session.
    private void DeleteApConnectionFile()
    {
        string? path = _writtenApConfigPath;
        _writtenApConfigPath = null;
        if (path == null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    // ── Private helpers — install state ──────────────────────────────────────

    private bool IsModInstalled()
    {
        if (string.IsNullOrEmpty(GameDirectory) || !Directory.Exists(GameDirectory))
            return false;

        // Primary sentinel: the known mod DLL path.
        if (File.Exists(ModDllPath)) return true;

        // Defensive: any *Archipelago*.dll under BepInEx\plugins (covers renamed builds).
        string pluginsDir = Path.Combine(GameDirectory, "BepInEx", "plugins");
        if (!Directory.Exists(pluginsDir)) return false;
        try
        {
            return Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories)
                .Any(f => Path.GetFileNameWithoutExtension(f).IndexOf(
                              "archipelago", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        catch { return false; }
    }

    private bool IsBepInExPresent()
    {
        if (string.IsNullOrEmpty(GameDirectory)) return false;
        string bepInExCore = Path.Combine(GameDirectory, "BepInEx", "core");
        return Directory.Exists(bepInExCore);
    }

    // ── Private helpers — exe resolution ─────────────────────────────────────

    private string? ResolveGameExe()
    {
        if (string.IsNullOrEmpty(GameDirectory)) return null;
        string preferred = GameExePath;
        if (File.Exists(preferred)) return preferred;
        // Fuzzy: any *DungeonClawler* or *dungeon*clawler* exe in the folder.
        try
        {
            return Directory.EnumerateFiles(GameDirectory, "*.exe", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f =>
                {
                    string n = Path.GetFileNameWithoutExtension(f).ToLowerInvariant();
                    return n.Contains("dungeon") && n.Contains("clawler");
                });
        }
        catch { return null; }
    }

    // ── Private helpers — release resolution ─────────────────────────────────

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..] : tag;
    }

    /// Resolve the latest GitHub release: (version, fullZipUrl, pluginZipUrl).
    /// Falls back to pinned 1.0.3 URLs when the API is unreachable.
    private async Task<(string Version, string? FullUrl, string? PluginUrl)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    // Skip drafts; accept prereleases (none expected for Clawrchipelago).
                    if (rel.TryGetProperty("draft", out var dr) &&
                        dr.ValueKind == JsonValueKind.True) continue;

                    string? version = rel.TryGetProperty("tag_name", out var t)
                        ? NormalizeTag(t.GetString()) : null;
                    if (version == null) continue;

                    if (!rel.TryGetProperty("assets", out var assets) ||
                        assets.ValueKind != JsonValueKind.Array) continue;

                    string? fullUrl   = null;
                    string? pluginUrl = null;

                    foreach (var a in assets.EnumerateArray())
                    {
                        string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        string? url  = a.TryGetProperty("browser_download_url", out var u)
                                       ? u.GetString() : null;
                        if (name == null || url == null) continue;

                        string lower = name.ToLowerInvariant();
                        // Pattern: "Clawrchipelago.Full.{ver}.zip"
                        if (fullUrl   == null && lower.Contains("full")   && lower.EndsWith(".zip"))
                            fullUrl   = url;
                        // Pattern: "Clawrchipelago.Plugin.{ver}.zip"
                        if (pluginUrl == null && lower.Contains("plugin") && lower.EndsWith(".zip"))
                            pluginUrl = url;
                    }

                    // We need at least one zip to consider this release valid.
                    if (fullUrl != null || pluginUrl != null)
                        return (version, fullUrl, pluginUrl);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        return (FallbackVersion, FallbackFullUrl, FallbackPluginUrl);
    }

    // ── Private helpers — download + extract ─────────────────────────────────

    private async Task DownloadAndExtractReleaseAsync(
        string zipUrl, string version, bool isFull,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string label  = isFull ? "Full (BepInEx + mod)" : "Plugin";
        string tmpZip = Path.Combine(Path.GetTempPath(),
            $"clawrchipelago-{label.Replace(' ', '_').ToLower()}-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            // Download.
            progress.Report((5, $"Downloading Clawrchipelago {label} {version}..."));
            using var resp = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            long total      = resp.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tmpZip))
            {
                var buf = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    downloaded += read;
                    if (total > 0)
                    {
                        int pct = (int)(5 + 55 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading Clawrchipelago {label}... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((65, "Extracting..."));

            // Extract to a temp staging folder, then merge into the game directory.
            string stagingDir = Path.Combine(Path.GetTempPath(),
                $"clawrchipelago-staging-{Guid.NewGuid():N}");
            try
            {
                System.IO.Compression.ZipFile.ExtractToDirectory(
                    tmpZip, stagingDir, overwriteFiles: true);

                // The Full zip has a top-level "Windows\" sub-folder per the README.
                // The Plugin zip has a "Clawrchipelago\" sub-folder that goes into
                // BepInEx\plugins. We merge everything into GameDirectory with the
                // correct relative path.
                //
                // Strategy: pick the staging root to copy FROM.
                //   Full zip:   <staging>\Windows\  → merge into GameDirectory
                //     (detecting by "windows" folder name).
                //   Plugin zip: <staging>\Clawrchipelago\ (or BepInEx\...)
                //     → merge into GameDirectory (detected by "bepinex" or game-
                //     adjacent folder structure).
                //   Fallback: just flatten into GameDirectory.
                string mergeRoot = PickMergeRoot(stagingDir, isFull);
                MergeDirectory(mergeRoot, GameDirectory);
            }
            finally
            {
                try { Directory.Delete(stagingDir, recursive: true); } catch { }
            }

            progress.Report((90, "Game files extracted."));
        }
        finally
        {
            try { if (File.Exists(tmpZip)) File.Delete(tmpZip); } catch { }
        }
    }

    /// Pick the folder inside the staging dir whose CONTENTS should be merged into
    /// GameDirectory. For the Full zip, this is the "Windows" sub-folder. For the
    /// Plugin zip, the staging root itself (its sub-folders are BepInEx/plugins/...).
    private static string PickMergeRoot(string stagingDir, bool isFull)
    {
        if (isFull)
        {
            // Look for a sub-folder named "Windows" (case-insensitive).
            foreach (string sub in Directory.EnumerateDirectories(stagingDir))
            {
                if (Path.GetFileName(sub).Equals("Windows", StringComparison.OrdinalIgnoreCase))
                    return sub;
            }
        }
        // For the Plugin zip (or fallback): use the staging root; the zip
        // contains BepInEx\plugins\Clawrchipelago which merges correctly.
        return stagingDir;
    }

    /// Recursively merge src into dst: create directories, overwrite files.
    private static void MergeDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string fileSrc in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel     = Path.GetRelativePath(src, fileSrc);
            string fileDst = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
            File.Copy(fileSrc, fileDst, overwrite: true);
        }
    }

    // ── Private helpers — process ─────────────────────────────────────────────

    private void StartGameProcess(string exePath, string workDir)
    {
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = workDir,
            UseShellExecute  = false,
        }) ?? throw new InvalidOperationException("Failed to start Dungeon Clawler.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            DeleteApConnectionFile();   // clean up credentials once the game exits
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — Steam detection ────────────────────────────────────

    /// Scan the Steam library VDF to find the Dungeon Clawler game folder.
    /// Returns null if Steam is not installed or the game is not found.
    private static string? FindSteamInstallDir()
    {
        try
        {
            string? steamPath = ReadSteamPath();
            if (steamPath == null) return null;

            string vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdfPath)) return null;

            foreach (string libRoot in ParseSteamLibraryRoots(vdfPath))
            {
                string manifestPath = Path.Combine(libRoot, "steamapps",
                    $"appmanifest_{STEAM_APP_ID}.acf");
                if (!File.Exists(manifestPath)) continue;

                string candidate = Path.Combine(libRoot, "steamapps", "common", STEAM_FOLDER);
                if (Directory.Exists(candidate)) return candidate;

                // Some installs use a different folder name — parse the manifest.
                string? folderName = ParseInstallDirFromManifest(manifestPath);
                if (folderName != null)
                {
                    string alt = Path.Combine(libRoot, "steamapps", "common", folderName);
                    if (Directory.Exists(alt)) return alt;
                }
            }
        }
        catch { /* Steam detection is best-effort */ }
        return null;
    }

    private static string? ReadSteamPath()
    {
        // Try HKCU first, then HKLM WOW6432Node.
        foreach (var (hive, sub) in new[]
        {
            (RegistryHive.CurrentUser,  @"Software\Valve\Steam"),
            (RegistryHive.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam"),
            (RegistryHive.LocalMachine, @"SOFTWARE\Valve\Steam"),
        })
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64)
                                           .OpenSubKey(sub);
                string? path = key?.GetValue("SteamPath") as string
                            ?? key?.GetValue("InstallPath") as string;
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    return path;
            }
            catch { }
        }
        return null;
    }

    /// Parse all library root paths from libraryfolders.vdf (tolerant, hand-written).
    private static IEnumerable<string> ParseSteamLibraryRoots(string vdfPath)
    {
        string content;
        try { content = File.ReadAllText(vdfPath); }
        catch { yield break; }

        // Match "path"   "<value>" lines (the VDF format uses tab-delimited quoted pairs).
        foreach (string line in content.Split('\n'))
        {
            string trimmed = line.Trim();
            int pathIdx = trimmed.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase);
            if (pathIdx < 0) continue;
            int q1 = trimmed.IndexOf('"', pathIdx + 6);
            if (q1 < 0) continue;
            int q2 = trimmed.IndexOf('"', q1 + 1);
            if (q2 < 0) continue;
            string libPath = trimmed.Substring(q1 + 1, q2 - q1 - 1)
                .Replace("\\\\", "\\");
            if (Directory.Exists(libPath)) yield return libPath;
        }
    }

    /// Parse the "installdir" field from a Steam appmanifest .acf file.
    private static string? ParseInstallDirFromManifest(string manifestPath)
    {
        try
        {
            foreach (string line in File.ReadAllLines(manifestPath))
            {
                string t = line.Trim();
                if (!t.StartsWith("\"installdir\"", StringComparison.OrdinalIgnoreCase))
                    continue;
                int q1 = t.LastIndexOf('"');
                int q0 = t.LastIndexOf('"', q1 - 1);
                if (q0 < 0 || q1 <= q0) continue;
                return t.Substring(q0 + 1, q1 - q0 - 1);
            }
        }
        catch { }
        return null;
    }

    // ── Private helpers — server URI parsing ──────────────────────────────────

    /// Split a launcher ApSession.ServerUri ("host:port", "ws://host:port", etc.)
    /// into a bare HostUrl and numeric Port for the AP connection JSON.
    /// Default AP port: 38281.
    private static (string Host, int Port) ParseServerHostPort(string serverUri)
    {
        string s = serverUri.Trim();
        foreach (string prefix in new[] { "wss://", "ws://", "archipelago://" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..];
                break;
            }
        }
        int slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];

        string host = s;
        int    port = 38281;
        int colonCount = 0;
        foreach (char c in s) if (c == ':') colonCount++;

        if (s.StartsWith('['))
        {
            int close = s.IndexOf(']');
            if (close > 0)
            {
                host = s[1..close];
                string rest = s[(close + 1)..];
                if (rest.StartsWith(':') &&
                    int.TryParse(rest[1..], out int p6) && p6 > 0 && p6 <= 65535)
                    port = p6;
            }
        }
        else if (colonCount > 1)
        {
            host = s;   // bare IPv6 — no port
        }
        else
        {
            int colon = s.LastIndexOf(':');
            if (colon > 0 && int.TryParse(s[(colon + 1)..], out int p) && p > 0 && p <= 65535)
            {
                host = s[..colon];
                port = p;
            }
        }

        if (host.Length == 0) host = "archipelago.gg";
        return (host, port);
    }

    // ── Private helpers — settings sidecar ────────────────────────────────────
    // Kept in this plugin's own JSON rather than Core/SettingsStore so the plugin
    // stays a single self-contained source file.

    private sealed class DungeonClawlerSettings
    {
        public string? GameDirectoryOverride { get; set; }
    }

    private DungeonClawlerSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<DungeonClawlerSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(DungeonClawlerSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }
}
