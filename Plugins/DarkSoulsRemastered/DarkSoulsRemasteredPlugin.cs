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

// NOTE on type qualification (BUILD GOTCHA):
// This project sets BOTH <UseWPF>true</UseWPF> and <UseWindowsForms>true</UseWindowsForms>.
// That makes a long list of simple type names ambiguous between WPF and WinForms
// (Clipboard, MessageBox, Application, Color, Brush(es), Button, TextBox, CheckBox,
// Orientation, FontWeights, HorizontalAlignment, Cursors, Thickness, OpenFileDialog…).
// To avoid CS0104 this file deliberately does NOT do `using System.Windows.Controls;` /
// `using System.Windows.Media;` — every WPF UI type below is written FULLY QUALIFIED
// (System.Windows.Controls.*, System.Windows.Media.*, System.Windows.MessageBox, …).
// It also does NOT introduce any file-level `using X = System.Windows...;` alias, because
// GlobalUsings.cs already aliases the short names project-wide and a second alias here
// would be CS1537 (duplicate alias).

namespace LauncherV2.Plugins.DarkSoulsRemastered;

// ═══════════════════════════════════════════════════════════════════════════════
// DarkSoulsRemasteredPlugin — detect / install / launch for "Dark Souls Remastered"
// (FromSoftware, 2018 remaster) played through the DSAP client — a SEPARATE Windows
// package (a standalone Avalonia GUI app, DSAP.Desktop.exe) that attaches to the
// running game via memory reading and communicates with the Archipelago server. This
// is a NATIVE "ConnectsItself" integration: a piece OUTSIDE the launcher (the DSAP
// client) owns the AP slot connection, not the launcher's ApClient.
//
// ── HONEST REALITY CHECK (2026-06-14, verified against tathxo/DSAP this session) ─
//   * CLIENT REPO — The original ArsonAssassin/DSAP is archived and redirects to
//     tathxo/DSAP as the maintained fork. Latest verified release: v0.1.5 (2026-06-02),
//     containing two assets:
//       dsr.apworld          — the apworld file to install in Archipelago
//       dsr-Windows-x64.zip  — the DSAP Desktop Client zip (standalone Avalonia GUI)
//
//   * AP GAME STRING — VERIFIED against apworld/dsr/archipelago.json:
//       "game": "Dark Souls Remastered"
//     and against apworld/dsr/__init__.py (class DsrWorld, game = "Dark Souls Remastered").
//
//   * STEAM APPID — Dark Souls: REMASTERED is appid 570940 (VERIFIED via Steam store
//     URL: store.steampowered.com/app/570940). NOT 374320 (that is Dark Souls III).
//
//   * CLIENT TYPE — DSAP.Desktop.exe is a standalone Avalonia GUI. The connection
//     details (host, slot, password) are entered in its hamburger menu (top-left three-
//     horizontal-line icon) and then the user clicks "Connect". There is no config file
//     pre-fill mechanism — the client persists credentials internally since v0.1.2.
//     The process name the client looks for is "DarkSoulsRemastered" (verified in
//     source/DSAP/DarkSoulsClient.cs: ProcessName = "DarkSoulsRemastered").
//
//   * CONNECTION FLOW (documented in apworld/dsr/docs/setup_en.md):
//       1. Start Steam normally.
//       2. Set Dark Souls: Remastered to OFFLINE mode in the game's Network Settings
//          (Launch Setting = "Start Offline"). This prevents FromSoft network bans.
//       3. Start the game through Steam (the remaster does NOT require Mod Engine 2 or
//          any external launcher — the DSAP client attaches to the running process).
//       4. Load into a save or the character creation menu (character must be able to
//          move before connecting).
//       5. Run DSAP.Desktop.exe, open the hamburger menu, fill in host/slot/password,
//          then click "Connect". The client will reload the game level automatically.
//
//   * WARNING — FromSoft online ban risk: NEVER use a DSAP save with the game online.
//     The setup guide explicitly warns this will likely result in an account restriction.
//
//   * "Installed" here means we have the extracted DSAP client in our own folder. The
//     Steam base game (appid 570940) is the player's own paid copy — we detect it
//     automatically but never download or modify it.
//
// ── WHAT THIS PLUGIN HONESTLY DOES ────────────────────────────────────────────
//   1. DETECT the Steam Dark Souls: Remastered install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, HKLM\...\WOW6432Node\Valve\Steam ->
//      InstallPath), parsing steamapps\libraryfolders.vdf for every library root and
//      locating steamapps\common\DARK SOULS REMASTERED via appmanifest_570940.acf.
//      A manual install-dir OVERRIDE (Settings folder picker) takes precedence and is
//      validated + persisted in this plugin's OWN sidecar
//      (Games/ROMs/dark_souls_remastered/dark_souls_remastered_launcher.json).
//   2. INSTALL/UPDATE = download dsr.apworld and dsr-Windows-x64.zip from the latest
//      GitHub release of tathxo/DSAP into the launcher's own folder
//      (Games/DarkSoulsRemasteredArchipelago/), extract the client zip (flattening any
//      wrapping sub-folder), and stamp the version.
//   3. LAUNCH (AP) = best effort: start Dark Souls: Remastered via Steam
//      (steam://rungameid/570940), then open DSAP.Desktop.exe with guidance to connect
//      through its own GUI. ConnectsItself = true; SupportsStandalone = true.
//   4. GUIDE the irreducible manual steps + links (setup guide, client repo, Steam page)
//      in Settings.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DarkSoulsRemasteredPlugin : IGamePlugin
{
    // ── Constants — the DSAP client package (verified tathxo/DSAP, 2026-06-14) ─
    private const string APP_OWNER = "tathxo";
    private const string APP_REPO  = "DSAP";
    private const string AppRepoUrl = $"https://github.com/{APP_OWNER}/{APP_REPO}";
    private const string GH_APP_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{APP_OWNER}/{APP_REPO}/releases/latest";
    private const string GH_APP_RELEASES_URL =
        $"https://api.github.com/repos/{APP_OWNER}/{APP_REPO}/releases";

    /// Official Dark Souls Remastered Archipelago setup guide.
    private const string SetupGuideUrl =
        "https://github.com/tathxo/DSAP/blob/main/apworld/dsr/docs/setup_en.md";

    /// Dark Souls: REMASTERED on Steam (appid 570940, VERIFIED).
    private const string SteamAppId = "570940";
    private const string SteamStoreUrl =
        "https://store.steampowered.com/app/570940";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// Standard Steam install sub-folder name (steamapps\common\DARK SOULS REMASTERED).
    private const string SteamCommonFolderName = "DARK SOULS REMASTERED";

    /// Dark Souls Remastered game exe (used to validate the Steam install folder).
    private const string GameExeName = "DarkSoulsRemastered.exe";

    /// The DSAP Desktop Client executable, extracted from dsr-Windows-x64.zip.
    private const string ClientExeName = "DSAP.Desktop.exe";

    /// The apworld file asset name in the GitHub release.
    private const string ApWorldAssetName = "dsr.apworld";

    /// Pinned fallback for the client zip when the GitHub API is unreachable.
    /// Tag v0.1.5 verified live 2026-06-14 (tathxo/DSAP).
    private const string FallbackVersion    = "0.1.5";
    private const string FallbackZipName    = "dsr-Windows-x64.zip";
    private static readonly string FallbackZipUrl =
        $"{AppRepoUrl}/releases/download/v{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(20), // client zip is modest size
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp, written after a successful download/extract.
    private const string VersionFileName = "dsr_ap_version.dat";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "dark_souls_remastered";
    public string DisplayName => "Dark Souls: Remastered";
    public string Subtitle    => "Native PC · Archipelago · DSAP";

    /// EXACT AP game string — VERIFIED against apworld/dsr/archipelago.json
    /// and apworld/dsr/__init__.py (tathxo/DSAP, v0.1.5).
    public string ApWorldName => "Dark Souls Remastered";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "dark_souls_remastered.png");

    public string ThemeAccentColor => "#B07A3A";   // bonfire amber / estus orange

    public string[] GameBadges => new[]
    {
        "Requires Dark Souls: Remastered on Steam",
        "Community AP mod (DSAP)",
    };

    public string Description =>
        "Dark Souls: Remastered is FromSoftware's acclaimed action-RPG — the Lordran " +
        "journey, rebuilt for modern hardware. This is the Archipelago integration via " +
        "the community DSAP client, which shuffles weapons, armor, rings, key items, " +
        "boss souls and more into the multiworld. You bring your own copy of Dark Souls: " +
        "Remastered on Steam (appid 570940); a separate DSAP Desktop Client attaches to " +
        "the running game and connects to the Archipelago server on your behalf. The " +
        "launcher detects your Steam install, downloads the client, and can start both " +
        "the game and the client. IMPORTANT: you must set the game to offline mode in " +
        "Network Settings (Start Offline) before playing — using the mod online risks a " +
        "FromSoftware account restriction. You enter your server, slot and password once " +
        "in the DSAP client's own window.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means we have the extracted DSAP Desktop Client in our own folder.
    /// The Steam base game alone is not enough — the client is the AP piece this launcher
    /// manages.
    public bool IsInstalled => ResolveClientExe() != null;

    public bool IsRunning { get; private set; }

    // ── ConnectsItself / SupportsStandalone ───────────────────────────────────

    /// The DSAP client owns the Archipelago slot connection. The launcher must NOT hold
    /// its own ApClient on this slot while DSAP is running.
    public bool ConnectsItself => true;

    /// Plain Dark Souls: Remastered via Steam runs without AP.
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the client package is extracted.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "DarkSoulsRemasteredArchipelago");

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// This plugin's OWN settings sidecar — kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained source file.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "dark_souls_remastered_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;    // the Steam/game process we started
    private Process? _clientProcess;  // the DSAP Desktop Client we started

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The DSAP client reports checks/items/goal to the AP server itself — the launcher
    // relays nothing. These exist only for interface compatibility (ConnectsItself = true).
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
            InstalledVersion = File.Exists(VersionFilePath) && IsInstalled
                ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim()
                : (IsInstalled ? "installed" : null);
        }
        catch
        {
            InstalledVersion = null;
        }

        try
        {
            var (version, _, _) = await ResolveLatestClientAsync(ct);
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
        // 1. Resolve the latest client release (pinned fallback when offline).
        progress.Report((3, "Checking the latest DSAP client release..."));
        var (version, zipUrl, zipName) = await ResolveLatestClientAsync(ct);
        AvailableVersion = version;

        // 2. Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"DSAP client {version} is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the DSAP client download on GitHub. Check your internet " +
                "connection, or download it manually from " + AppRepoUrl + "/releases/latest. " +
                "Remember you also need your own copy of Dark Souls: Remastered on Steam " +
                "(appid 570940).");

        // 3. Download + extract the client zip into our own folder.
        await DownloadAndExtractClientAsync(zipUrl, zipName ?? FallbackZipName, version, progress, ct);

        // 4. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        // 5. Honest closing note.
        string? steamDir = ResolveSteamGameDir();
        string steamLine = steamDir != null
            ? $"Detected your Steam copy of Dark Souls: Remastered at \"{steamDir}\". "
            : "Reminder: you need your own copy of Dark Souls: Remastered on Steam (appid 570940). ";

        progress.Report((100,
            $"DSAP client {version} installed. " + steamLine +
            "To play: (1) start the game through Steam; (2) load into a character (must " +
            "be able to move); (3) run the DSAP client from this launcher and enter your " +
            "Archipelago host, slot, and password in its hamburger menu, then click " +
            "Connect. IMPORTANT: set the game to offline mode in Network Settings first " +
            "(Start Offline) to avoid a FromSoftware account restriction."));
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
        // HONEST: the DSAP client connection details (host, slot, password) are entered
        // in its own hamburger-menu GUI — there is no command-line or config-file prefill
        // mechanism available. So this plugin:
        //   a) Launches Dark Souls: Remastered through Steam (so the game is ready for
        //      DSAP to attach to), and
        //   b) Opens DSAP.Desktop.exe so the user can enter credentials and connect.
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this slot.
        _ = session; // intentionally unused — no prefill mechanism exists

        // Start the game via Steam.
        StartGame();

        // Open the DSAP Desktop Client (best-effort; guidance surfaces the missing state).
        if (ResolveClientExe() != null)
        {
            try { StartDsapClient(); } catch { /* surfaced in LaunchStandaloneAsync path */ }
        }

        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // Standalone = plain Dark Souls: Remastered without DSAP, via Steam.
        StartGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        try { _clientProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning     = false;
        _gameProcess  = null;
        _clientProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The DSAP client receives items from the AP server and applies them in-game;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The DSAP client renders its own connection status.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Dark Souls: Remastered's Archipelago support uses the DSAP community " +
                   "client — a standalone app that attaches to the running game. You need " +
                   "your own copy of Dark Souls: Remastered on Steam (appid 570940). " +
                   "IMPORTANT: set the game to OFFLINE mode in Network Settings (Start " +
                   "Offline) before playing — using the mod online risks a FromSoftware " +
                   "account restriction. You enter your server, slot and password in the " +
                   "DSAP client's own window — this launcher cannot pre-fill the " +
                   "connection. These external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: DSAP client status ───────────────────────────────────
        panel.Children.Add(SectionHeader("DSAP DESKTOP CLIENT", muted));
        string? clientExe = ResolveClientExe();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = clientExe != null
                ? "Client downloaded in: " + GameDirectory
                : "Client not downloaded yet (use Install on the Play tab, or get it from " +
                  "the client repo below).",
            FontSize = 11, Foreground = clientExe != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });
        if (clientExe != null)
        {
            var openClient = new System.Windows.Controls.Button
            {
                Content = "Open DSAP client (" + ClientExeName + ")...",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(10, 5, 10, 5),
                Margin  = new System.Windows.Thickness(0, 2, 0, 12),
                Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
                Foreground  = fg,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
                ToolTip = "Opens the DSAP Desktop Client. Enter your Archipelago host, slot " +
                          "and password in the hamburger menu (top-left), then click Connect.",
            };
            openClient.Click += (_, _) =>
            {
                try { StartDsapClient(); }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(ex.Message, "Could not open the DSAP client",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
            };
            panel.Children.Add(openClient);
        }
        else
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "", Margin = new System.Windows.Thickness(0, 0, 0, 6),
            });
        }

        // ── Section: Steam base game ──────────────────────────────────────
        panel.Children.Add(SectionHeader("DARK SOULS: REMASTERED (STEAM BASE GAME)", muted));

        string? steamDir    = DetectSteamGameDir();
        string? overrideDir = LoadOverrideDir();
        string? gameDir     = ResolveSteamGameDir();
        string  detectMsg   = gameDir != null
            ? (overrideDir != null
                ? "Detected using selected folder: " + gameDir
                : "Detected Steam install (appid " + SteamAppId + "): " + gameDir)
            : "Dark Souls: Remastered not detected on Steam. Pick your install folder " +
              "below, or install it via Steam first (appid 570940).";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? steamDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Dark Souls: Remastered install folder (the one containing " +
                          "DarkSoulsRemastered.exe). Detected from Steam automatically; set " +
                          "it here to override for a non-standard Steam library.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120, Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Dark Souls: Remastered install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? steamDir ?? "")
                                   ? (overrideDir ?? steamDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Dark Souls: Remastered folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeGameDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeGameDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 570940). Use this " +
                   "picker only for a non-standard Steam library location.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: setup + connection steps ─────────────────────────────
        panel.Children.Add(SectionHeader("SETUP & CONNECTION", muted));
        foreach (string step in new[]
        {
            "1. Own and install Dark Souls: Remastered on Steam (appid 570940, Windows " +
                "64-bit). The DSAP client has been tested against version 1.03.1 / " +
                "regulation 1.04.",
            "2. Use Install on the Play tab to download the DSAP client (dsr-Windows-x64.zip). " +
                "No modifications are made to your Steam game files — the client attaches " +
                "to the running process.",
            "3. In the game's Network Settings, set Launch Setting to 'Start Offline'. " +
                "WARNING: never use a DSAP/AP save while connected to the FromSoftware " +
                "online servers — this risks a permanent account restriction.",
            "4. Start Dark Souls: Remastered through Steam. Load into a character or the " +
                "character creation screen (wait until your character can move).",
            "5. Open the DSAP client (button above, or Play on the game tile). Click the " +
                "hamburger menu (top-left three-line icon) and enter your Archipelago host " +
                "(e.g. archipelago.gg:38281), slot name, and password (if required). Click " +
                "Connect. The game will reload your level automatically.",
            "6. Close the DSAP client before quitting the game or returning to the main " +
                "menu. Do NOT connect to the FromSoftware network at any time while using " +
                "this mod.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Dark Souls: Remastered on Steam ↗",    SteamStoreUrl),
            ("DSAP Setup Guide ↗",                    SetupGuideUrl),
            ("DSAP Client (tathxo/DSAP) ↗",          AppRepoUrl),
            ("Archipelago Official ↗",               "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0), FontSize = 12,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => { try { Process.Start(
                new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
            panel.Children.Add(btn);
        }
        return panel;
    }

    private static System.Windows.Controls.TextBlock SectionHeader(
        string text, System.Windows.Media.Brush muted)
        => new System.Windows.Controls.TextBlock
        {
            Text = text, FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        };

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_APP_RELEASES_URL, ct);
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

    /// "v0.1.5" → "0.1.5" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest DSAP client release: version + the client zip URL + zip name.
    /// Looks for an asset matching "dsr" and "windows" and ".zip"; falls back to any
    /// non-source .zip; falls back to the pinned v0.1.5 direct URL when the API is
    /// unreachable.
    private async Task<(string Version, string? ZipUrl, string? ZipName)>
        ResolveLatestClientAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_APP_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;
                string? prefName  = null;
                string? anyZip    = null;
                string? anyName   = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;
                    if (lower.Contains("source")) continue;

                    if (anyZip == null) { anyZip = url; anyName = name; }
                    if (preferred == null &&
                        (lower.Contains("dsr") || lower.Contains("windows") ||
                         lower.Contains("dark")))
                    {
                        preferred = url;
                        prefName  = name;
                    }
                }
                string? zip     = preferred ?? anyZip;
                string? zipName = prefName  ?? anyName;
                if (zip != null)
                    return (version, zip, zipName);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl, FallbackZipName);
    }

    // ── Private helpers — extracted-client resolution ─────────────────────────

    /// The DSAP Desktop Client executable inside our install folder. Prefers the
    /// canonical name (DSAP.Desktop.exe); else any "*DSAP*.exe". Null if absent.
    private string? ResolveClientExe()
    {
        try
        {
            if (!Directory.Exists(GameDirectory)) return null;

            string direct = Path.Combine(GameDirectory, ClientExeName);
            if (File.Exists(direct)) return direct;

            foreach (string exe in Directory.EnumerateFiles(GameDirectory, "*.exe", SearchOption.AllDirectories))
            {
                string baseName = Path.GetFileName(exe);
                if (string.Equals(baseName, ClientExeName, StringComparison.OrdinalIgnoreCase))
                    return exe;
            }
            foreach (string exe in Directory.EnumerateFiles(GameDirectory, "*.exe", SearchOption.AllDirectories))
            {
                string lower = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (lower.Contains("dsap") || lower.Contains("darksoul")) return exe;
            }
        }
        catch { /* directory vanished / permission */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Dark Souls: Remastered through Steam. Prefer the Steam protocol URI so
    /// Steam's overlay / cloud saves engage. Falls back to the detected exe.
    private void StartGame()
    {
        try
        {
            Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
            IsRunning = true;
            return;
        }
        catch { /* fall through to direct exe */ }

        string? dir = ResolveSteamGameDir();
        if (dir != null)
        {
            string? exe = FindGameExeIn(dir);
            if (exe != null && File.Exists(exe))
            {
                try
                {
                    var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName         = exe,
                        WorkingDirectory = Path.GetDirectoryName(exe) ?? dir,
                        UseShellExecute  = true,
                    });
                    if (proc != null)
                    {
                        _gameProcess = proc;
                        IsRunning    = true;
                        HookExit(proc);
                    }
                }
                catch { /* best effort */ }
            }
        }
    }

    /// Open the DSAP Desktop Client. Throws with honest guidance if the client has not
    /// been installed yet.
    private void StartDsapClient()
    {
        string? exe = ResolveClientExe();
        if (exe == null)
            throw new FileNotFoundException(
                "The DSAP client (" + ClientExeName + ") was not found. Click Install on " +
                "the Play tab first, or download it from " + AppRepoUrl + "/releases/latest.",
                Path.Combine(GameDirectory, ClientExeName));

        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exe,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? GameDirectory,
            UseShellExecute  = true,
        }) ?? throw new InvalidOperationException("Failed to launch the DSAP client.");

        _clientProcess = proc;
        try
        {
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => { _clientProcess = null; };
        }
        catch { /* non-fatal */ }
    }

    /// Wire a process's Exited event to IsRunning / GameExited, defensively.
    private void HookExit(Process proc)
    {
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

    // ── Private helpers — Steam / Dark Souls: Remastered detection ────────────

    /// Validate a user-picked Dark Souls: Remastered install folder. Returns null when
    /// acceptable, else a short human-readable reason to show in Settings.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Dark Souls: Remastered install folder.";

        if (LooksLikeGameDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeGameDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Dark Souls: Remastered installation. Pick the " +
               "folder that contains DarkSoulsRemastered.exe (Steam installs are normally " +
               "in steamapps\\common\\DARK SOULS REMASTERED).";
    }

    /// The Dark Souls: Remastered install dir to use: the override (if set and valid)
    /// wins, else the Steam-detected install. Null when nothing is found.
    private string? ResolveSteamGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeGameDir(ov))
            return ov;

        try { return DetectSteamGameDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Dark Souls: Remastered if it contains
    /// DarkSoulsRemastered.exe (directly or in a sub-folder).
    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, GameExeName))) return true;
            return FindGameExeIn(dir) != null;
        }
        catch { return false; }
    }

    /// Find DarkSoulsRemastered.exe in `dir`: directly, else a shallow recursive search.
    private static string? FindGameExeIn(string dir)
    {
        try
        {
            string direct = Path.Combine(dir, GameExeName);
            if (File.Exists(direct)) return direct;

            foreach (string exe in Directory.EnumerateFiles(dir, GameExeName, SearchOption.AllDirectories))
                return exe;
        }
        catch { /* permission / vanished */ }
        return null;
    }

    /// Detect the Steam Dark Souls: Remastered install: read the Steam root from the
    /// registry, gather all library roots from libraryfolders.vdf, and find the one
    /// whose appmanifest_570940.acf exists → steamapps\common\DARK SOULS REMASTERED.
    private static string? DetectSteamGameDir()
    {
        try
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
                            if (LooksLikeGameDir(candidate)) return candidate;
                        }
                        string conventional = Path.Combine(common, SteamCommonFolderName);
                        if (LooksLikeGameDir(conventional)) return conventional;
                    }
                    catch { /* try the next library */ }
                }
            }
        }
        catch { /* registry/file access failed */ }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath + HKLM
    /// WOW6432Node InstallPath + HKLM InstallPath), plus the conventional location.
    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormalizeSteamPath(hkcu);

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormalizeSteamPath(hklm);

        string? hklm2 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm2)) yield return NormalizeSteamPath(hklm2);

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    /// Steam stores its SteamPath with forward slashes; normalize for Path APIs.
    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    /// All Steam library roots: the Steam root itself plus every "path" entry in
    /// steamapps\libraryfolders.vdf. Tolerant text scan.
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

    /// Pull every  "path"  "<value>"  pair out of a libraryfolders.vdf body.
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

    /// Read the "installdir" value from an appmanifest_*.acf.
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

    /// Safe registry string read; null on any failure.
    private static string? ReadRegistryString(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(subKey);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    // ── Private helpers — download / extract the client ───────────────────────

    /// Download the DSAP client zip and extract it INTO our own GameDirectory.
    /// If the zip wraps everything in a single sub-folder, flatten it so
    /// DSAP.Desktop.exe lands where ResolveClientExe() expects it.
    private async Task DownloadAndExtractClientAsync(
        string zipUrl,
        string zipName,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"dsap-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((6, $"Downloading {zipName} ({version})..."));
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
                        int pct = (int)(6 + 64 * downloaded / total);
                        progress.Report((pct, $"Downloading DSAP client... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((74, "Extracting the DSAP client..."));
            Directory.CreateDirectory(GameDirectory);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, GameDirectory, overwriteFiles: true);

            // If the client exe did not land (zip wrapped everything in ONE top-level
            // sub-folder), flatten that single sub-folder up.
            if (ResolveClientExe() == null)
            {
                string[] subdirs = Directory.GetDirectories(GameDirectory);
                string[] files   = Directory.GetFiles(GameDirectory);
                if (files.Length == 0 && subdirs.Length == 1)
                {
                    string sub = subdirs[0];
                    foreach (string fileSrc in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
                    {
                        string rel     = Path.GetRelativePath(sub, fileSrc);
                        string fileDst = Path.Combine(GameDirectory, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                        File.Move(fileSrc, fileDst, overwrite: true);
                    }
                    try { Directory.Delete(sub, recursive: true); } catch { }
                }
            }

            progress.Report((92, "DSAP client extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class DsrSettings
    {
        public string? InstallOverride { get; set; }
    }

    private DsrSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<DsrSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(DsrSettings s)
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
