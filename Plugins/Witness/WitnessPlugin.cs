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

namespace LauncherV2.Plugins.Witness;

// ═══════════════════════════════════════════════════════════════════════════════
// WitnessPlugin — detect / install / launch for "The Witness" (Thekla, 2016)
// played through "The Witness Archipelago Randomizer" — a SEPARATE, standalone
// Windows app that HOOKS THE RUNNING STEAM GAME and owns the AP slot connection.
//
// This is a NATIVE "ConnectsItself" integration, but honestly it is an
// EXT-style / external-client case in the same family as the shipped Undertale
// plugin (a separate long-running tool holds the slot, not the game process and
// not the launcher). It is NOT a clean in-game mod like Ship of Harkinian, and
// NOT a Steam Workshop / DLL mod like Hollow Knight. The plugin is explicit
// about this throughout.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online this session) ───────────
//   * CLIENT TYPE — INJECTOR (process-memory). The randomizer is a standalone
//     C/C++ Windows EXECUTABLE (the repo is ~52% C++, ~35% C). It reads and
//     writes The Witness's PROCESS MEMORY of the already-running Steam game to
//     re-randomise puzzles, detect panel solves (each is an AP location check),
//     and apply received items. It is NOT a patcher (it does not modify the
//     game's files on disk) and NOT an in-game mod. You run the game first, then
//     run this external app alongside it.
//
//   * THE AP WORLD game string is "The Witness" — VERIFIED against
//     worlds/witness/__init__.py (`game = "The Witness"`). GameId = "witness".
//     The Witness's AP support is part of AP-MAIN (worlds/witness); there is no
//     separate apworld to ship — only the external randomizer app.
//
//   * THE CLIENT repo is NewSoupVi/The-Witness-Randomizer-for-Archipelago — the
//     CURRENT, maintained fork (the official AP "The Witness" setup guide links
//     its /releases/latest; it was originally started by NewSoupVi with major
//     early work by Jarno, and the older blastron/Jarno458 URLs are earlier
//     forks). Latest release verified live: tag 9.0.0 (2026-04-18), whose SOLE
//     asset is a single Windows executable:
//         "The.Witness.Randomizer.for.Archipelago.exe"
//     (no zip, no installer — a direct .exe download).
//
//   * CONNECTION is made IN THE RANDOMIZER APP'S OWN GUI — NOT in-game, NOT via
//     command-line, NOT via a config file the launcher can pre-write (verified
//     against the setup guide + README). The documented flow is: launch The
//     Witness → start a NEW save → launch the randomizer → type the Archipelago
//     server (host:port), slot name and (optional) password into the randomizer's
//     fields → press "Connect". The randomizer also has a "Load Credentials"
//     button that retrieves the connection data from your SAVE FILE to rejoin a
//     game — i.e. the connection is bound to the save once entered (the same
//     slot-bound-save idea as Ship of Harkinian). There are NO connection
//     command-line args and NO connection config file on the randomizer, so this
//     plugin does NOT attempt a connection prefill — it GUIDES the user. The
//     post-install note and settings panel state this plainly.
//
//   * Steam requirement: "The Witness for 64-bit Windows (e.g. Steam version)".
//     Steam appid 210970 (verified). The launcher detects the Steam install but
//     never modifies it (§11) — the randomizer attaches at runtime; nothing on
//     disk changes.
//
// ── WHAT THIS PLUGIN HONESTLY DOES ────────────────────────────────────────────
//   1. DETECT the Steam The Witness install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, HKLM\...\WOW6432Node\Valve\Steam
//      -> InstallPath), parsing steamapps\libraryfolders.vdf for every library
//      root and locating steamapps\common\The Witness via
//      appmanifest_210970.acf. A manual install-dir OVERRIDE (Settings folder
//      picker) takes precedence and is validated + persisted in this plugin's OWN
//      sidecar (Games/ROMs/witness/witness_launcher.json) — Core/SettingsStore is
//      NOT modified.
//   2. INSTALL/UPDATE = download the randomizer's single .exe asset from the
//      GitHub release (latest tag, pinned 9.0.0 fallback when the API is
//      unreachable) into the launcher's own folder
//      (Games/TheWitnessRandomizer/) and stamp the version. This is the genuine,
//      honest install — the app is a self-contained exe — but the BASE GAME is
//      the user's own paid Steam copy, which is detected, never downloaded.
//   3. LAUNCH (AP) = best effort: open The Witness (via steam://rungameid/210970
//      if Steam is present, else the detected exe) AND launch the randomizer exe,
//      so the user gets both running with one click. The connection itself is
//      entered into the randomizer's GUI (see header) — no prefill. ConnectsItself
//      = true (the randomizer owns the slot; the launcher must NOT hold its own
//      ApClient on it). SupportsStandalone = true (plain The Witness runs without
//      AP). Inert ReceiveItems / OnApStateChanged.
//   4. GUIDE the irreducible steps + links (setup guide, randomizer repo, Steam
//      page, archipelago.gg) in Settings.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", SoH/Undertale-style) ──────
//   * RELEASE ASSET: the latest release's only asset was verified to be a single
//     .exe named "The.Witness.Randomizer.for.Archipelago.exe". ResolveLatestApp
//     picks the first Windows .exe asset (fuzzy, excluding setup/installer
//     helpers); the pinned fallback URL targets that exact name at tag 9.0.0. Re-
//     verify on a fork bump.
//   * "Installed" is judged by the presence of our downloaded randomizer exe in
//     the launcher's randomizer folder (or a stamped version). The Steam base
//     game being present does NOT by itself make the game "installed" here — the
//     randomizer is the AP-specific piece this launcher manages.
//   * No plaintext AP password is ever written by this plugin (connection is
//     entered in the randomizer's GUI), so there is nothing to scrub on stop.
//   * Steam library parsing is a tolerant text scan; any failure degrades to "not
//     detected" rather than throwing.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class WitnessPlugin : IGamePlugin
{
    // ── Constants — the randomizer app (real repo, verified 2026-06-14) ────────
    private const string APP_OWNER = "NewSoupVi";
    private const string APP_REPO  = "The-Witness-Randomizer-for-Archipelago";
    private const string AppRepoUrl = $"https://github.com/{APP_OWNER}/{APP_REPO}";
    private const string GH_APP_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{APP_OWNER}/{APP_REPO}/releases/latest";
    private const string GH_APP_RELEASES_URL =
        $"https://api.github.com/repos/{APP_OWNER}/{APP_REPO}/releases";

    /// Official Archipelago "The Witness" setup guide.
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/The%20Witness/setup/en";

    /// The Witness on Steam (the base game the player must own; appid 210970).
    private const string WitnessSteamAppId = "210970";
    private const string SteamStoreUrl =
        "https://store.steampowered.com/app/210970";
    private static readonly string SteamRunUrl =
        $"steam://rungameid/{WitnessSteamAppId}";

    /// Standard Steam install sub-folder name (steamapps\common\The Witness).
    private const string SteamCommonFolderName = "The Witness";

    /// The Witness's game exe (used when launching the detected install directly
    /// if Steam's protocol launch is unavailable).
    private const string WitnessExeName = "witness64_d3d11.exe";

    /// Pinned fallback for the randomizer when the GitHub API is unreachable.
    /// tag 9.0.0 verified live 2026-06-14; the single asset is the .exe below.
    private const string FallbackVersion   = "9.0.0";
    private const string RandomizerExeName = "The.Witness.Randomizer.for.Archipelago.exe";
    private static readonly string FallbackExeUrl =
        $"{AppRepoUrl}/releases/download/{FallbackVersion}/{RandomizerExeName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(15),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp, written after a successful download.
    private const string VersionFileName = "witness_rando_version.dat";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "witness";
    public string DisplayName => "The Witness";
    public string Subtitle    => "Native PC · Archipelago";

    /// EXACT AP game string — VERIFIED against worlds/witness/__init__.py
    /// (`game = "The Witness"`).
    public string ApWorldName => "The Witness";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "witness.png");

    public string ThemeAccentColor => "#C9A227";   // panel-puzzle amber
    public string[] GameBadges     => new[] { "Requires The Witness on Steam" };

    public string Description =>
        "The Witness is Thekla's acclaimed first-person puzzle game set on a " +
        "mysterious island of line-drawing panels. This is the Archipelago " +
        "integration, which shuffles the island's panel puzzles and unlocks into " +
        "the multiworld: every last panel in a row becomes a location check, and " +
        "solving key panels grants or sends items. You bring your own copy of The " +
        "Witness on Steam (64-bit Windows); a separate companion app, \"The Witness " +
        "Archipelago Randomizer\", attaches to the running game and connects to the " +
        "multiworld. The launcher detects your Steam install, downloads the " +
        "randomizer, and can launch both with one click. You enter your server, " +
        "slot and password in the randomizer's own window and press Connect.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means we have the randomizer companion exe in our own folder.
    /// (The Steam base game alone is not enough — the randomizer is the AP piece
    /// this launcher manages.)
    public bool IsInstalled => ResolveRandomizerExe() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the randomizer companion exe is downloaded. (The base
    /// game lives in its own Steam install, which we only detect.)
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "TheWitnessRandomizer");

    /// Preferred randomizer exe path inside GameDirectory.
    private string PreferredRandomizerExePath
        => Path.Combine(GameDirectory, RandomizerExeName);

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained source file — same approach as the
    /// Hollow Knight / Undertale plugins). BOM-less UTF-8.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "witness_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _randomizerProcess;   // the companion app we launch
    private Process? _gameProcess;          // the game, if we started the exe directly

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Witness Archipelago Randomizer reports checks/items/goal to the AP
    // server itself — the launcher relays nothing. These exist only for interface
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
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(APP_OWNER, APP_REPO, ct));
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
        // 1. Resolve the latest randomizer release (pinned fallback when offline).
        progress.Report((3, "Checking the latest The Witness Archipelago Randomizer release..."));
        var (version, exeUrl, exeName) = await ResolveLatestAppAsync(ct);
        AvailableVersion = version;

        // 2. Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"The Witness Archipelago Randomizer {version} is up to date."));
            return;
        }

        if (exeUrl == null)
            throw new InvalidOperationException(
                "Could not find the The Witness Archipelago Randomizer download on " +
                "GitHub. Check your internet connection, or download it manually from " +
                AppRepoUrl + "/releases/latest. Remember you also need your own copy " +
                "of The Witness on Steam (appid 210970).");

        // 3. Download the randomizer exe into our own folder.
        await DownloadRandomizerAsync(exeUrl, exeName ?? RandomizerExeName, version, progress, ct);

        // 4. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        // 5. Honest closing note — base game + connection are not automated.
        string? steamDir = DetectSteamInstallDir();
        string steamLine = steamDir != null
            ? $"Detected your Steam copy of The Witness at \"{steamDir}\". "
            : "Reminder: you need your own copy of The Witness on Steam (appid 210970). ";

        progress.Report((100,
            $"The Witness Archipelago Randomizer {version} downloaded. " + steamLine +
            "To play: launch The Witness, start a NEW save, then launch the randomizer " +
            "(Play here launches both). In the randomizer's window, enter your server " +
            "(host:port), slot name and password, then press Connect. The launcher cannot " +
            "pre-fill the connection — it is entered in the randomizer."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── AutoMod-style validation of a user-picked Steam folder ────────────────

    /// Used by the Settings folder picker: a valid The Witness folder contains
    /// witness64_d3d11.exe (or, defensively, any *witness*.exe). Return null when
    /// acceptable, else a short human-readable reason so they can pick again.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your The Witness install folder.";

        if (LooksLikeWitnessDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeWitnessDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a The Witness installation. Pick the folder " +
               "that contains the game's executable (the Steam version is normally in " +
               "steamapps\\common\\The Witness).";
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: the AP server connection is entered into the RANDOMIZER app's own
        // GUI (server host:port, slot, password, then Connect) — there is no command
        // line / config prefill we can apply (verified — see header). So this starts
        // BOTH the game and the randomizer for convenience; the user connects in the
        // randomizer with the session credentials.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the randomizer is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartWitnessGame();
        StartRandomizer();   // throws with honest guidance if not downloaded yet
        return Task.CompletedTask;
    }

    /// Plain (non-AP) The Witness runs perfectly well.
    public bool SupportsStandalone => true;

    /// The Witness Archipelago Randomizer owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // Standalone = just the game, no randomizer, no connection.
        StartWitnessGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We launched the randomizer (and possibly the game exe) from here — stop
        // what we started. The randomizer is the AP-critical process.
        try { _randomizerProcess?.Kill(entireProcessTree: true); } catch { }
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        _randomizerProcess = null;
        _gameProcess = null;
        // No plaintext AP password is ever written to disk by this plugin (the
        // connection is entered in the randomizer's GUI), so there is nothing to
        // scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The randomizer receives items from the AP server directly and applies them
        // to the running game's memory; there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The randomizer renders its own connection state in its window.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "The Witness's Archipelago support uses a SEPARATE companion app, " +
                   "\"The Witness Archipelago Randomizer\", which attaches to the running " +
                   "Steam game and connects to the multiworld. You bring your own copy of " +
                   "The Witness on Steam (64-bit Windows, appid 210970). The launcher " +
                   "detects your install and downloads the randomizer, and can launch both " +
                   "with one click, but you connect to your server inside the randomizer's " +
                   "own window — the launcher cannot pre-fill the connection. These " +
                   "external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: randomizer download status ───────────────────────────
        panel.Children.Add(SectionHeader("ARCHIPELAGO RANDOMIZER (COMPANION APP)", muted));
        string? randoExe = ResolveRandomizerExe();
        panel.Children.Add(new TextBlock
        {
            Text = randoExe != null
                ? "✓ Randomizer downloaded: " + randoExe
                : "Randomizer not downloaded yet (use Install on the Play tab, or get it " +
                  "from the randomizer repo below).",
            FontSize = 11, Foreground = randoExe != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Steam base game (detected / override) ────────────────
        panel.Children.Add(SectionHeader("THE WITNESS (STEAM BASE GAME)", muted));

        string? steamDir    = DetectSteamInstallDir();
        string? overrideDir = LoadOverrideDir();
        string? witnessDir  = ResolveWitnessDir();
        string  detectMsg = witnessDir != null
            ? (overrideDir != null
                ? "✓ Using your selected folder: " + witnessDir
                : "✓ Detected Steam install (appid " + WitnessSteamAppId + "): " + witnessDir)
            : "The Witness not detected. Pick your install folder below, or install The " +
              "Witness via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = witnessDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? steamDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your The Witness install folder (the one containing " +
                          "witness64_d3d11.exe). Detected from Steam automatically; set it " +
                          "here to override (non-standard Steam library).",
        };
        var dirBtn = new Button
        {
            Content = "Select folder...", Width = 120, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your The Witness install folder",
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
                    MessageBox.Show(bad, "Not a The Witness folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeWitnessDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeWitnessDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 210970). Use this " +
                   "picker only for a non-standard Steam library location.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: setup + connection steps ─────────────────────────────
        panel.Children.Add(SectionHeader("SETUP & CONNECTION", muted));
        foreach (string step in new[]
        {
            "1. Own and install The Witness on Steam (64-bit Windows, appid 210970).",
            "2. Use Install on the Play tab to download \"The Witness Archipelago Randomizer\" " +
                "(a single companion .exe). It attaches to the running game — it does NOT " +
                "modify your game files.",
            "3. Launch The Witness and start a NEW save (Play here launches the game and the " +
                "randomizer together).",
            "4. Launch the randomizer. In its window, enter your Archipelago server as " +
                "host:port (e.g. archipelago.gg:38281), your slot name, and the password if " +
                "your room has one, then press Connect. (This launcher cannot pre-fill the " +
                "connection — it is entered in the randomizer.)",
            "5. To rejoin later, the randomizer's \"Load Credentials\" button reads the " +
                "connection back from your save file.",
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
        foreach (var (label, url) in new[]
        {
            ("The Witness on Steam ↗",                   SteamStoreUrl),
            ("The Witness Setup Guide ↗",                SetupGuideUrl),
            ("The Witness Archipelago Randomizer ↗",     AppRepoUrl),
            ("Archipelago Official ↗",                   "https://archipelago.gg"),
        })
        {
            var btn = new Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => { try { Process.Start(
                new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
            panel.Children.Add(btn);
        }
        return panel;
    }

    private static TextBlock SectionHeader(string text, System.Windows.Media.Brush muted)
        => new TextBlock
        {
            Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        };

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Randomizer releases are the AP-relevant news for this game.
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

    /// "v9.0.0" → "9.0.0" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest randomizer release: version + the .exe download URL +
    /// the exe asset name. Falls back to the pinned 9.0.0 direct URL when the API
    /// is unreachable.
    private async Task<(string Version, string? ExeUrl, string? ExeName)>
        ResolveLatestAppAsync(CancellationToken ct)
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
                string? preferred = null;   // an .exe whose name mentions "witness"/"rando"
                string? prefName  = null;
                string? anyExe    = null;   // any non-helper .exe
                string? anyName   = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".exe")) continue;
                    if (IsHelperExe(Path.GetFileNameWithoutExtension(lower))) continue;

                    if (anyExe == null) { anyExe = url; anyName = name; }
                    if (preferred == null &&
                        (lower.Contains("witness") || lower.Contains("rando") ||
                         lower.Contains("archipelago")))
                    {
                        preferred = url;
                        prefName  = name;
                    }
                }
                string? exe     = preferred ?? anyExe;
                string? exeName = prefName  ?? anyName;
                if (exe != null)
                    return (version, exe, exeName);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackExeUrl, RandomizerExeName);
    }

    // ── Private helpers — randomizer exe resolution ───────────────────────────

    /// The downloaded randomizer exe: prefer the canonical name, else any
    /// "*witness*.exe" / "*rando*.exe" in our folder (defensive). Null if absent.
    private string? ResolveRandomizerExe()
    {
        try
        {
            if (File.Exists(PreferredRandomizerExePath)) return PreferredRandomizerExePath;
            if (!Directory.Exists(GameDirectory)) return null;

            foreach (string exe in Directory.EnumerateFiles(GameDirectory, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (IsHelperExe(name)) continue;
                if (name.Contains("witness") || name.Contains("rando") || name.Contains("archipelago"))
                    return exe;
            }
            // Last resort: a single non-helper exe in the folder.
            string[] candidates = Directory
                .EnumerateFiles(GameDirectory, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(e => !IsHelperExe(Path.GetFileNameWithoutExtension(e).ToLowerInvariant()))
                .ToArray();
            if (candidates.Length == 1) return candidates[0];
        }
        catch { /* directory vanished / permission */ }
        return null;
    }

    /// Names that are NOT the runnable companion app (installers, helpers).
    private static bool IsHelperExe(string nameLowerNoExt)
        => nameLowerNoExt.Contains("unins")   ||
           nameLowerNoExt.Contains("setup")   ||
           nameLowerNoExt.Contains("crash")   ||
           nameLowerNoExt.Contains("vcredist")||
           nameLowerNoExt.Contains("dxsetup");

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start The Witness: prefer Steam's protocol launch (so Steam's own overlay /
    /// cloud saves engage), else the exe in the detected/override install. Best
    /// effort — does not throw if the game cannot be located (the randomizer can
    /// still attach to a game the user starts manually). Sets IsRunning so the tile
    /// reflects an active session even if we cannot track the exact process.
    private void StartWitnessGame()
    {
        // Prefer Steam protocol launch when Steam is present.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // Steam owns the process; we won't track exit
                return;
            }
            catch { /* fall through to the direct exe below */ }
        }

        // Fall back to the detected/override install's exe.
        string? dir = ResolveWitnessDir();
        string? exe = dir != null ? FindWitnessExeIn(dir) : null;
        if (exe != null && File.Exists(exe))
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName         = exe,
                    WorkingDirectory = dir!,
                    UseShellExecute  = true,
                });
                if (proc != null)
                {
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
            }
            catch { /* best effort — the randomizer can attach to a manual launch */ }
        }
        // If neither path worked we simply don't start the game; the randomizer
        // launch below still proceeds, and the user can start the game manually.
    }

    /// Start the downloaded randomizer companion app. Throws with honest guidance
    /// when it has not been downloaded yet (so the UI surfaces the real next step).
    private void StartRandomizer()
    {
        string? exe = ResolveRandomizerExe();
        if (exe == null)
            throw new FileNotFoundException(
                "The Witness Archipelago Randomizer has not been downloaded yet. Click " +
                "Install on the Play tab (or get it from " + AppRepoUrl + "/releases/latest). " +
                "Then launch The Witness, start a new save, run the randomizer, and enter " +
                "your server / slot / password in its window.",
                PreferredRandomizerExePath);

        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exe,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? GameDirectory,
            UseShellExecute  = true,
        }) ?? throw new InvalidOperationException(
            "Failed to start The Witness Archipelago Randomizer.");

        _randomizerProcess = proc;
        IsRunning          = true;
        try
        {
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                // The randomizer is the AP-critical process; when it exits the AP
                // session is effectively over from this launcher's perspective.
                IsRunning = false;
                GameExited?.Invoke(proc.ExitCode);
            };
        }
        catch { /* non-fatal */ }
    }

    // ── Private helpers — The Witness install detection ───────────────────────

    /// The Witness install dir to use: the override (if set and valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolveWitnessDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeWitnessDir(ov))
            return ov;

        try { return DetectSteamInstallDir(); }
        catch { return null; }
    }

    /// A folder "looks like" The Witness if it has the game exe (or, defensively,
    /// any *witness*.exe). The Witness ships witness64_d3d11.exe.
    private static bool LooksLikeWitnessDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, WitnessExeName))) return true;
            return FindWitnessExeIn(dir) != null;
        }
        catch { return false; }
    }

    /// Find The Witness's game exe in `dir`: prefer witness64_d3d11.exe, else a
    /// fuzzy "*witness*.exe" (excluding the randomizer/companion app, which is NOT
    /// in the Steam game folder anyway). Null if none.
    private static string? FindWitnessExeIn(string dir)
    {
        try
        {
            string preferred = Path.Combine(dir, WitnessExeName);
            if (File.Exists(preferred)) return preferred;

            foreach (string exe in Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (IsHelperExe(name)) continue;
                // Avoid mis-identifying a randomizer copy as the game exe.
                if (name.Contains("rando") || name.Contains("archipelago")) continue;
                if (name.Contains("witness")) return exe;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    /// Detect the Steam The Witness install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_210970.acf exists → steamapps\common\The Witness. Never throws.
    private static string? DetectSteamInstallDir()
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
                        string manifest  = Path.Combine(steamapps, $"appmanifest_{WitnessSteamAppId}.acf");
                        if (!File.Exists(manifest)) continue;

                        string common = Path.Combine(steamapps, "common");
                        string? installDir = ReadAcfInstallDir(manifest);
                        if (installDir != null)
                        {
                            string candidate = Path.Combine(common, installDir);
                            if (LooksLikeWitnessDir(candidate)) return candidate;
                        }
                        string conventional = Path.Combine(common, SteamCommonFolderName);
                        if (LooksLikeWitnessDir(conventional)) return conventional;
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
    /// steamapps\libraryfolders.vdf. Tolerant text scan (the VDF is a simple
    /// quoted key/value tree; we only need the path values).
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

    /// Pull every  "path"   "<value>"  pair out of a libraryfolders.vdf body.
    /// Handles the Steam-VDF escaping of backslashes (\\ → \).
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

    /// Read the "installdir" value from an appmanifest_*.acf (same quoted-pair
    /// format as VDF). Returns null if absent.
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

    /// Safe registry string read; null on any failure (key/value missing, etc.).
    private static string? ReadRegistryString(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(subKey);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    // ── Private helpers — download the randomizer exe ─────────────────────────

    /// Download the randomizer's single .exe asset directly into GameDirectory.
    private async Task DownloadRandomizerAsync(
        string exeUrl,
        string exeName,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(GameDirectory);

        // Normalise the on-disk name to the canonical one so ResolveRandomizerExe
        // finds it deterministically (and so updates overwrite cleanly).
        string destExe = Path.Combine(GameDirectory, RandomizerExeName);
        string tempExe = Path.Combine(GameDirectory,
            $".{Path.GetFileNameWithoutExtension(RandomizerExeName)}-{Guid.NewGuid():N}.part");

        try
        {
            progress.Report((8, $"Downloading {exeName} ({version})..."));
            using var response = await _http.GetAsync(
                exeUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempExe))
            {
                var buf = new byte[81920];
                int bytesRead;
                while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                    downloaded += bytesRead;
                    if (total > 0)
                    {
                        int pct = (int)(8 + 84 * downloaded / total);
                        progress.Report((pct, $"Downloading randomizer... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((95, "Finishing up..."));
            // Replace any existing copy atomically-ish.
            try { if (File.Exists(destExe)) File.Delete(destExe); } catch { }
            File.Move(tempExe, destExe, overwrite: true);
            progress.Report((98, "Randomizer downloaded."));
        }
        finally
        {
            try { if (File.Exists(tempExe)) File.Delete(tempExe); } catch { }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the Steam install-dir override)
    // in its OWN JSON file so it stays a single self-contained source file and does
    // not modify Core/SettingsStore. BOM-less UTF-8, read-modify-write.

    private sealed class WitnessSettings
    {
        public string? InstallOverride { get; set; }
    }

    private WitnessSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<WitnessSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(WitnessSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — setting just won't persist this time */ }
    }

    private string? LoadOverrideDir()
    {
        string? p = LoadSettings().InstallOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }
    private void SaveOverrideDir(string p) { var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s); }
}
