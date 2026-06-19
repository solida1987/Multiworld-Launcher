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

namespace LauncherV2.Plugins.IttleDew2;

// ═══════════════════════════════════════════════════════════════════════════════
// IttleDew2Plugin — install / launch for "Ittle Dew 2+" (Ludosity, 2016)
// played through the ArchipelagoRandomizer mod by Extra-2-Dew, which ships an
// in-game Archipelago client. This is a NATIVE "ConnectsItself" integration: the
// mod itself holds the AP slot connection; the launcher does not touch it.
//
// ── VERIFIED FACTS (2026-06-14, github.com/Extra-2-Dew/ArchipelagoRandomizer) ─
//
//   * AP GAME STRING: "Ittle Dew 2"
//     Confirmed in APHandler.cs: Session.TryConnectAndLogin("Ittle Dew 2", ...)
//     apworld filename: ittle_dew_2.apworld (release asset 0.3.0).
//
//   * MOD REPO: github.com/Extra-2-Dew/ArchipelagoRandomizer
//     Latest release (0.3.0): "0.3.0 - Quality of Strife Update"
//     Release asset: "ArchipelagoRandomizer.zip"
//       → extracts "ArchipelagoRandomizer" folder, drops into BepInEx/plugins/.
//     Dependencies (from ArchipelagoRandomizer.csproj):
//       • BepInEx 5.x (Unity MONO — NOT IL2CPP; Ittle Dew 2 uses Mono runtime)
//       • ModCore (from github.com/Extra-2-Dew/ModCore)
//     Build target: net35 (the game's Mono version).
//
//   * STEAM APPID: 395620
//     Confirmed via store.steampowered.com/app/395620/Ittle_Dew_2/
//     Game data (saves, BepInEx) lives under:
//       %AppData%\..\LocalLow\Ludosity\Ittle Dew 2\
//     i.e. Environment.SpecialFolder.LocalApplicationData\..\..\LocalLow\Ludosity\Ittle Dew 2
//     (same path used in ArchipelagoRandomizer.csproj GameDataPath).
//
//   * CONNECTION: fully in-game.
//     After installation, launch the game and start a new file. A new Archipelago
//     button appears on the file-select / main-menu screen. Click it → Connection
//     Info menu → enter Server / Port / Slot Name / Password → press Back → game
//     connects (logo turns colored when successful). The mod saves per-slot-file
//     connection data as  <save_name>_apData.json  inside the ModCore data folder
//     (AppData\LocalLow\Ludosity\Ittle Dew 2\BepInEx\plugins\ModCore\...).
//     There is NO standalone config file we can pre-fill before launch — the JSON
//     path depends on the save-file name the user types, which is only known at
//     game time. Like Hollow Knight / Jak / TUNIC, we surface session credentials
//     in the Settings panel for the user to copy into the in-game fields; we do NOT
//     write any file on their behalf.
//
//   * ConnectsItself = true — the mod holds the slot; the launcher must NOT open a
//     competing ApClient connection on the same slot.
//   * SupportsStandalone = false — the mod requires an AP server to start a game;
//     unmodded Ittle Dew 2 launches fine but there is no "AP-less" mode in the mod.
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. DETECT: find the Steam Ittle Dew 2 install (appid 395620) via Steam registry
//      + libraryfolders.vdf. User can override with a folder picker.
//   2. INSTALL PREREQUISITES (best-effort):
//      a. BepInEx 5.x (Unity Mono x64) — staged into the game root if absent.
//         Must be the 5.x (Mono) variant, NOT 6.x (IL2CPP).
//      b. ModCore — downloaded from Extra-2-Dew/ModCore latest release and
//         extracted into BepInEx/plugins/ModCore/.
//      c. ArchipelagoRandomizer — the mod zip extracted into BepInEx/plugins/.
//   3. LAUNCH: run the game exe (or steam://rungameid/395620 fallback).
//      The user enters their AP credentials in the in-game Connection Info menu.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class IttleDew2Plugin : IGamePlugin
{
    // ── Constants — mod repo (verified 2026-06-14) ───────────────────────────
    private const string ModOwner   = "Extra-2-Dew";
    private const string ModRepo    = "ArchipelagoRandomizer";
    private const string ModRepoUrl = $"https://github.com/{ModOwner}/{ModRepo}";
    private const string GhModReleasesLatestUrl =
        $"https://api.github.com/repos/{ModOwner}/{ModRepo}/releases/latest";
    private const string GhModReleasesUrl =
        $"https://api.github.com/repos/{ModOwner}/{ModRepo}/releases";

    // ModCore dependency
    private const string ModCoreOwner   = "Extra-2-Dew";
    private const string ModCoreRepo    = "ModCore";
    private const string ModCoreRepoUrl = $"https://github.com/{ModCoreOwner}/{ModCoreRepo}";
    private const string GhModCoreReleasesLatestUrl =
        $"https://api.github.com/repos/{ModCoreOwner}/{ModCoreRepo}/releases/latest";

    // BepInEx 5.x Unity Mono x64 — required by this game (NOT IL2CPP).
    // Pinned to the stable 5.4.23.2 release.
    private const string BepInExSite     = "https://github.com/BepInEx/BepInEx/releases";
    private const string BepInExVersion  = "5.4.23.2";
    private static readonly string BepInExZipUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/" +
        "BepInEx_win_x64_5.4.23.2.zip";

    // Steam / exe
    private const string SteamAppId         = "395620";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";
    private const string SteamCommonName    = "Ittle Dew 2";   // steamapps\common subfolder
    private const string GameExeName        = "ID2.exe";         // main executable

    // Mod layout inside BepInEx/plugins/
    private const string ModFolderName     = "ArchipelagoRandomizer";
    private const string ModPrimaryDll     = "ArchipelagoRandomizer.dll";
    private const string ModCoreFolderName = "ModCore";
    private const string ModCorePrimaryDll = "ModCore.dll";

    // Pinned fallback (latest as of 2026-06-14)
    private const string FallbackModVersion = "0.3.0";
    private const string FallbackModZipName = "ArchipelagoRandomizer.zip";
    private static readonly string FallbackModZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackModVersion}/{FallbackModZipName}";

    private const string SetupGuideUrl  = "https://archipelago.gg/tutorial/Ittle%20Dew%202/setup/en";
    private const string GameInfoUrl    = "https://archipelago.gg/games/Ittle%20Dew%202/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "ittle_dew_2";
    public string DisplayName => "Ittle Dew 2+";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against APHandler.cs:
    /// Session.TryConnectAndLogin("Ittle Dew 2", ...)
    public string ApWorldName => "Ittle Dew 2";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "ittle_dew_2.png");

    public string ThemeAccentColor => "#D44A6B"; // Ittle's pink/magenta palette

    public string[] GameBadges => new[] { "Steam · needs mod" };

    public string Description =>
        "Ittle Dew 2+ (Ludosity, 2016) is a top-down action adventure " +
        "inspired by classic Zelda games. In the Archipelago randomizer, items " +
        "from all over the island — chest contents, dungeon keys, outfits, cards, " +
        "and more — are shuffled across the multiworld. The mod is installed on " +
        "top of your own Steam copy of the game via BepInEx (the Unity Mono mod " +
        "loader) and ModCore (the Extra-2-Dew mod framework). You enter your " +
        "Archipelago connection details from the in-game menu when starting a new " +
        "file. Supports four randomizer goals: Raft Quest, Queen of Adventure, " +
        "Queen of Dreams, and Potion Hunt.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = the ArchipelagoRandomizer.dll is present under the game's
    /// BepInEx tree. We do not gate on a launcher-own stamp so hand-installs work.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Working / download directory for this plugin. The actual mod is extracted
    /// INTO the game's BepInEx folder (which lives in the LocalLow data path).
    public string GameDirectory { get; set; }
        = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "..", "LocalLow", "Ludosity", "Ittle Dew 2");

    /// Plugin sidecar (install-dir override + version stamp), separate from the
    /// shared SettingsStore so the plugin remains one self-contained file.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "ittledew2_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The mod holds the AP slot connection directly. These exist for interface
    // compliance only (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself / Standalone ───────────────────────────────────────────

    /// The ArchipelagoRandomizer mod owns the AP connection; launcher must not
    /// open a competing client on the same slot.
    public bool ConnectsItself => true;

    /// Unmodded Ittle Dew 2 runs fine, but the mod requires an AP server to start
    /// a new randomized file. We expose no standalone AP-less mode here.
    public bool SupportsStandalone => false;

    public Task LaunchStandaloneAsync(CancellationToken ct = default) => Task.CompletedTask;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = FindInstalledModDll() != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch { InstalledVersion = null; }

        try
        {
            var (version, _) = await ResolveLatestModAsync(ct);
            AvailableVersion = version;
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 1. Locate the game install — we must have it to know where to put BepInEx.
        progress.Report((2, "Locating your Ittle Dew 2 installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find an Ittle Dew 2 installation. Open Settings and pick " +
                "your game folder (the one containing ID2.exe), or install Ittle Dew 2+ " +
                "via Steam first.");

        // BepInEx / plugins go INTO the game root (same as every Unity BepInEx mod).
        string pluginsDir = Path.Combine(gameDir, "BepInEx", "plugins");

        // 2. BepInEx 5.x (Mono) — stage it if absent.
        if (!BepInExPresent(gameDir))
        {
            progress.Report((6, "Staging BepInEx 5 (Unity Mono) into your Ittle Dew 2 folder..."));
            await DownloadAndExtractZipToDirAsync(
                BepInExZipUrl, $"id2-bepinex-{BepInExVersion}", gameDir,
                6, 30, progress, ct);
        }
        else
        {
            progress.Report((30, "BepInEx already present — keeping your existing install."));
        }

        // 3. ModCore — required dependency; stage if absent.
        if (!ModCorePresent(pluginsDir))
        {
            progress.Report((32, "Downloading ModCore (required dependency)..."));
            await InstallModCoreAsync(pluginsDir, 32, 58, progress, ct);
        }
        else
        {
            progress.Report((58, "ModCore already present."));
        }

        // 4. The ArchipelagoRandomizer mod itself.
        progress.Report((60, "Checking the latest ArchipelagoRandomizer release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the ArchipelagoRandomizer mod download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                ModRepoUrl + "/releases.");

        await DownloadAndExtractModAsync(zipUrl, version, pluginsDir, 62, 94, progress, ct);

        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepOk  = BepInExPresent(gameDir);
        bool mcOk   = ModCorePresent(pluginsDir);
        progress.Report((100,
            $"ArchipelagoRandomizer {version} installed into BepInEx\\plugins." +
            (bepOk ? " BepInEx: OK." : "") +
            (mcOk  ? " ModCore: OK." : "") +
            " To play: launch Ittle Dew 2+, start a new file, click the Archipelago " +
            "button on the file-select screen, and enter your server details. " +
            "Open Settings for the full guided steps."));
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
        // CONNECTION is made entirely in-game (see header). There is no config file
        // the launcher can pre-fill before launch (the per-save JSON path depends on
        // the save-file name the user types at game startup). We just launch the exe
        // and let the user enter their session details into the in-game Connection
        // Info menu. ConnectsItself = true: we must NOT open a competing ApClient.
        _ = session; // unused — no pre-fill mechanism exists
        StartGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning     = false;
        _gameProcess  = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var ok      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Ittle Dew 2+ is your own game (Steam) with the ArchipelagoRandomizer " +
                   "mod added on top via BepInEx 5 (Unity Mono) and ModCore. The Install " +
                   "button on the Play tab stages all three. You connect to your Archipelago " +
                   "server from the in-game Connection Info menu when starting a new file — " +
                   "this launcher cannot pre-fill that connection.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Detected install ──────────────────────────────────────────────
        panel.Children.Add(SectionHeader("GAME INSTALLATION", muted));

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();
        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Ittle Dew 2 not detected. Pick your install folder below, or " +
              "install Ittle Dew 2+ via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? ok : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // BepInEx status
        bool bepOk = gameDir != null && BepInExPresent(gameDir);
        panel.Children.Add(new TextBlock
        {
            Text = gameDir == null ? "" : bepOk
                ? "BepInEx found in your Ittle Dew 2 folder."
                : "BepInEx not found — Install on the Play tab stages it (Unity Mono 5.x).",
            FontSize = 11, Foreground = bepOk ? ok : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        // ModCore status
        string pluginsDir = gameDir != null
            ? Path.Combine(gameDir, "BepInEx", "plugins") : "";
        bool mcOk = gameDir != null && ModCorePresent(pluginsDir);
        panel.Children.Add(new TextBlock
        {
            Text = gameDir == null ? "" : mcOk
                ? "ModCore found in BepInEx\\plugins."
                : "ModCore not found — Install on the Play tab stages it.",
            FontSize = 11, Foreground = mcOk ? ok : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        // Mod status
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                ? "ArchipelagoRandomizer mod found: " + modDll
                : "ArchipelagoRandomizer mod not found (use Install on the Play tab).",
            FontSize = 11, Foreground = modDll != null ? ok : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });

        // Folder picker
        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Your Ittle Dew 2 install folder (contains ID2.exe). " +
                      "Detected from Steam automatically; use this picker for a " +
                      "non-standard library or another store.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select your Ittle Dew 2 install folder (contains ID2.exe)",
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
                    System.Windows.MessageBox.Show(bad, "Not an Ittle Dew 2 folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
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
            Text = "Steam installs are detected automatically (appid 395620). Use this " +
                   "picker for a non-standard Steam library or another store.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Connecting ────────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("CONNECTING (entered in-game)", muted));
        panel.Children.Add(new TextBlock
        {
            Text = "Launch Ittle Dew 2+ and start a new file. You will see an Archipelago " +
                   "button on the file-select screen. Click it to open the Connection Info " +
                   "menu and enter your Server (host), Port, Slot Name, and Password. Press " +
                   "Back — the Archipelago logo turns colored when connected. You can adjust " +
                   "settings (DeathLink, auto-equip outfits, chests matching contents, etc.) " +
                   "from the same menu. This launcher does not pre-fill the connection.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Guided setup steps ────────────────────────────────────────────
        panel.Children.Add(SectionHeader("GUIDED SETUP", muted));
        foreach (string step in new[]
        {
            "1. Own Ittle Dew 2+ on Steam. Install it if you have not already done so. " +
               "Use the folder picker above if it was not detected automatically.",
            "2. Click Install on the Play tab. This stages BepInEx 5 (Unity Mono x64), " +
               "ModCore, and the ArchipelagoRandomizer mod into your game folder.",
            "3. Launch Ittle Dew 2+ from the Play tab or Steam. On the title screen you " +
               "should see no errors. Start a new file — the Archipelago button will " +
               "appear on the file-select / main menu screen.",
            "4. Click the Archipelago button, enter your Server / Port / Slot Name / " +
               "Password, press Back. The gray logo turns colored when the connection " +
               "is established. Enter your file name and confirm to start your adventure!",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new (string, string)[]
        {
            ("ArchipelagoRandomizer (GitHub) ↗", ModRepoUrl),
            ("ModCore (GitHub) ↗",               ModCoreRepoUrl),
            ("BepInEx (releases) ↗",             BepInExSite),
            ("Ittle Dew 2 on Steam ↗",           $"https://store.steampowered.com/app/{SteamAppId}/"),
            ("Archipelago Official ↗",           ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
                Foreground = accent,
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
            string json = await _http.GetStringAsync(GhModReleasesUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var dp) && dp.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(dp.GetString(), out date);

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

    // ── Validate an existing install folder ───────────────────────────────────

    public string? ValidateGameDir(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist.";
        if (LooksLikeGameDir(folder)) return null;
        // Accept the Steam "common" parent (descend one level).
        try
        {
            string nested = Path.Combine(folder, SteamCommonName);
            if (LooksLikeGameDir(nested)) return null;
        }
        catch { /* ignore */ }
        return "That does not look like an Ittle Dew 2 installation. " +
               @"Pick the folder that contains ID2.exe (Steam: …\steamapps\common\Ittle Dew 2).";
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..] : tag;
    }

    /// Resolve the latest ArchipelagoRandomizer mod release: version + zip URL.
    /// Falls back to the pinned 0.3.0 release when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GhModReleasesLatestUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var tv)
                ? NormalizeTag(tv.GetString()) : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;
                string? anyZip    = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var an) ? an.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var au) ? au.GetString() : null;
                    if (name == null || url == null) continue;
                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;
                    anyZip ??= url;
                    if (preferred == null && lower.Contains("archipelago"))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null) return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable — fall through to pinned */ }

        return (FallbackModVersion, FallbackModZipUrl);
    }

    /// Fetch the latest ModCore release zip URL. Returns null on failure.
    private async Task<string?> ResolveLatestModCoreZipAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GhModCoreReleasesLatestUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var a in assets.EnumerateArray())
            {
                string? name = a.TryGetProperty("name", out var an) ? an.GetString() : null;
                string? url  = a.TryGetProperty("browser_download_url", out var au) ? au.GetString() : null;
                if (name == null || url == null) continue;
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    return url;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through */ }
        return null;
    }

    // ── Private helpers — detection ───────────────────────────────────────────

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
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, GameExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, "ID2_Data"))) return true;
            return false;
        }
        catch { return false; }
    }

    private static bool BepInExPresent(string gameDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return false;
            if (Directory.Exists(Path.Combine(gameDir, "BepInEx"))) return true;
            if (File.Exists(Path.Combine(gameDir, "winhttp.dll"))) return true;
            return false;
        }
        catch { return false; }
    }

    private static bool ModCorePresent(string pluginsDir)
    {
        try
        {
            string modCorePath = Path.Combine(pluginsDir, ModCoreFolderName, ModCorePrimaryDll);
            if (File.Exists(modCorePath)) return true;
            // Also check a loose ModCore.dll directly in plugins/
            if (File.Exists(Path.Combine(pluginsDir, ModCorePrimaryDll))) return true;
            return false;
        }
        catch { return false; }
    }

    private string? FindInstalledModDll()
    {
        try
        {
            string? game = ResolveGameDir();
            if (game == null) return null;
            string bepDir = Path.Combine(game, "BepInEx");
            if (!Directory.Exists(bepDir)) return null;
            foreach (string dll in Directory.EnumerateFiles(bepDir, "*.dll", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dll).Equals(ModPrimaryDll, StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { }
        return null;
    }

    // ── Private helpers — Steam detection ────────────────────────────────────

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

                    string common     = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeGameDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonName);
                    if (LooksLikeGameDir(conventional)) return conventional;
                }
                catch { }
            }
        }
        return null;
    }

    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadReg(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormSteam(hkcu);

        string? hklm = ReadReg(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormSteam(hklm);

        string? hklm64 = ReadReg(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm64)) yield return NormSteam(hklm64);

        string? pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(pf86))
            yield return Path.Combine(pf86, "Steam");
    }

    private static string NormSteam(string p) => p.Replace('/', '\\').TrimEnd('\\');

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
            if (norm.Length > 0 && seen.Add(norm)) yield return norm;
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

    private static string? ReadReg(RegistryKey hive, string sub, string val)
    {
        try { using var k = hive.OpenSubKey(sub); return k?.GetValue(val) as string; }
        catch { return null; }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        string? game = ResolveGameDir();
        string? exe  = game != null ? Path.Combine(game, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = game!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Ittle Dew 2+.");

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

        // Fall back to Steam URL
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
            "Could not find ID2.exe. Open Settings and pick your Ittle Dew 2 folder, " +
            "or install Ittle Dew 2+ via Steam.",
            GameExeName);
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    /// Download the ArchipelagoRandomizer zip and extract it into BepInEx/plugins/.
    private async Task DownloadAndExtractModAsync(
        string zipUrl, string version, string pluginsDir,
        int pctStart, int pctEnd,
        IProgress<(int Pct, string Msg)> progress, CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"id2-ar-{version}-{Guid.NewGuid():N}.zip");
        string tempExt = Path.Combine(Path.GetTempPath(),
            $"id2-ar-x-{version}-{Guid.NewGuid():N}");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 7 / 10;
            await DownloadFileAsync(zipUrl, tempZip,
                $"Downloading ArchipelagoRandomizer {version}...",
                pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Installing ArchipelagoRandomizer into BepInEx\\plugins..."));
            Directory.CreateDirectory(pluginsDir);
            Directory.CreateDirectory(tempExt);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExt, overwriteFiles: true);

            string? srcDir = FindFolderOrDllParent(tempExt, ModFolderName, ModPrimaryDll);
            string destDir = Path.Combine(pluginsDir, ModFolderName);
            if (Directory.Exists(destDir))
            {
                try { Directory.Delete(destDir, recursive: true); } catch { }
            }
            CopyDirectory(srcDir ?? tempExt, destDir);

            progress.Report((pctEnd, "ArchipelagoRandomizer installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExt)) Directory.Delete(tempExt, recursive: true); } catch { }
        }
    }

    /// Download and install ModCore into BepInEx/plugins/ModCore/.
    private async Task InstallModCoreAsync(
        string pluginsDir, int pctStart, int pctEnd,
        IProgress<(int Pct, string Msg)> progress, CancellationToken ct)
    {
        string? zipUrl = await ResolveLatestModCoreZipAsync(ct);
        if (zipUrl == null)
        {
            progress.Report((pctEnd,
                "Could not locate a ModCore release zip. Download ModCore manually " +
                "from " + ModCoreRepoUrl + "/releases and place the ModCore folder " +
                "into BepInEx\\plugins\\."));
            return;
        }

        string tempZip = Path.Combine(Path.GetTempPath(), $"id2-modcore-{Guid.NewGuid():N}.zip");
        string tempExt = Path.Combine(Path.GetTempPath(), $"id2-modcore-x-{Guid.NewGuid():N}");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 7 / 10;
            await DownloadFileAsync(zipUrl, tempZip,
                "Downloading ModCore...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Installing ModCore into BepInEx\\plugins..."));
            Directory.CreateDirectory(pluginsDir);
            Directory.CreateDirectory(tempExt);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExt, overwriteFiles: true);

            string? srcDir = FindFolderOrDllParent(tempExt, ModCoreFolderName, ModCorePrimaryDll);
            string destDir = Path.Combine(pluginsDir, ModCoreFolderName);
            if (Directory.Exists(destDir))
            {
                try { Directory.Delete(destDir, recursive: true); } catch { }
            }
            CopyDirectory(srcDir ?? tempExt, destDir);

            progress.Report((pctEnd, "ModCore installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExt)) Directory.Delete(tempExt, recursive: true); } catch { }
        }
    }

    /// Download BepInEx (portable zip) and extract into the game root.
    private async Task DownloadAndExtractZipToDirAsync(
        string zipUrl, string tag, string targetDir,
        int pctStart, int pctEnd,
        IProgress<(int Pct, string Msg)> progress, CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(), $"{tag}-{Guid.NewGuid():N}.zip");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 8 / 10;
            await DownloadFileAsync(zipUrl, tempZip,
                "Downloading BepInEx 5 (Unity Mono)...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Extracting BepInEx into your Ittle Dew 2 folder..."));
            Directory.CreateDirectory(targetDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, targetDir, overwriteFiles: true);
            progress.Report((pctEnd, "BepInEx staged."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    /// Find a source directory: first a folder named <folderName> anywhere in the
    /// tree; then the directory that directly contains <dllName>. Returns null if
    /// neither is found (caller falls back to the extract root).
    private static string? FindFolderOrDllParent(string root, string folderName, string dllName)
    {
        try
        {
            foreach (string d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(d).Equals(folderName, StringComparison.OrdinalIgnoreCase))
                    return d;
            }
            foreach (string f in Directory.EnumerateFiles(root, dllName, SearchOption.AllDirectories))
            {
                return Path.GetDirectoryName(f);
            }
        }
        catch { }
        return null;
    }

    private async Task DownloadFileAsync(
        string url, string destPath, string msg,
        int pctStart, int pctEnd,
        IProgress<(int Pct, string Msg)> progress, CancellationToken ct)
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
        int read;
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read), ct);
            downloaded += read;
            if (total > 0)
            {
                int span = Math.Max(1, pctEnd - pctStart);
                int pct  = pctStart + (int)(span * downloaded / total);
                progress.Report((pct, $"{msg} {downloaded / 1000}KB"));
            }
        }
        await dst.FlushAsync(ct);
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel  = Path.GetRelativePath(src, file);
            string dest = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    // ── Private helpers — UI ──────────────────────────────────────────────────

    private static TextBlock SectionHeader(string text, Brush foreground) =>
        new()
        {
            Text       = text,
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = foreground,
            Margin     = new Thickness(0, 4, 0, 8),
        };

    // ── Private helpers — settings sidecar ───────────────────────────────────

    private sealed class IttleDew2Settings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private IttleDew2Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<IttleDew2Settings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(IttleDew2Settings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
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
        var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s);
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings(); s.ModVersion = v; SaveSettings(s);
    }
}
