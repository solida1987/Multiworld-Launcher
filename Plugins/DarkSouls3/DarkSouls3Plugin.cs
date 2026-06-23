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

namespace LauncherV2.Plugins.DarkSouls3;

// ═══════════════════════════════════════════════════════════════════════════════
// DarkSouls3Plugin — detect / install / launch for "Dark Souls III" (FromSoftware,
// 2016) played through the Dark Souls III Archipelago Client — a SEPARATE Windows
// package (a Mod Engine 2 DLL mod + a one-time setup tool) layered on the user's own
// Steam copy of the game. This is a NATIVE "ConnectsItself" integration in the same
// EXT-style family as the shipped Witness and Undertale plugins: a piece OUTSIDE the
// launcher (here, the injected archipelago.dll driven by the setup tool) owns the AP
// slot connection — not the launcher's ApClient, and not a launcher-held pipe.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online this session) ───────────
//   * CLIENT TYPE — MOD-ENGINE DLL + SETUP TOOL. The download is a single zip,
//     "DS3.Archipelago.<ver>.zip", that contains a Mod Engine 2 tree: the AP mod
//     (archipelago.dll, injected into the running game by Mod Engine 2), a one-time
//     setup/static-randomizer tool (randomizer\DS3Randomizer.exe), and a launcher
//     batch file (launchmod_darksouls3.bat). It is NOT an in-game menu client like
//     Ship of Harkinian and NOT a pure memory injector like The Witness randomizer —
//     it is a static randomizer + a DLL mod run through Mod Engine 2. The static
//     randomizer is thefifthmatt / gracenotes' single-player DS3 randomizer.
//
//   * THE AP WORLD game string is "Dark Souls III" — VERIFIED against
//     worlds/dark_souls_3/__init__.py (class DarkSouls3World, game = "Dark Souls III",
//     required_client_version = (0, 4, 2)). Dark Souls III is a CORE Archipelago
//     world (it ships inside Archipelago itself) — there is NO separate apworld to
//     drop, only the external client package. GameId here = "dark_souls_3".
//
//   * THE CLIENT repo is nex3/Dark-Souls-III-Archipelago-client — the CURRENT,
//     maintained fork (the official AP "Dark Souls III" setup guide points its
//     /releases/latest there; it is a fork of the original Marechal-L client).
//     Latest release verified live via the GitHub API this session: tag v3.0.13
//     (2025-06-24), whose SOLE asset is the single zip "DS3.Archipelago.3.0.13.zip"
//     (~26.7 MB). Version 3.x supports ONLY the latest Dark Souls III, 1.15.2.
//
//   * CONNECTION is entered in the SETUP TOOL'S OWN GUI — NOT in-game, NOT via the
//     game's command line, and NOT in a config file the launcher can reliably
//     pre-write (verified against the official setup guide + README). The documented
//     flow is: run  randomizer\DS3Randomizer.exe  ONCE, type your Archipelago room
//     address (host:port, e.g. archipelago.gg:38281), your player/slot name, and an
//     optional password, then click "Load" and wait. After that, Mod Engine 2 reads
//     the staged data; you launch via launchmod_darksouls3.bat and the game shows
//     "Archipelago connected". Because the connection is captured by DS3Randomizer's
//     dialog (not a documented flat key/value file), this plugin does NOT fake a
//     prefill — it GUIDES, and surfaces the session host/slot/password so the user
//     can paste them into the tool. (DEFENSIVE prefill would risk corrupting the
//     tool's staged state, so it is intentionally NOT attempted.)
//
//   * MOD-ENGINE / OFFLINE / EAC: Dark Souls III's anti-cheat must be bypassed by
//     running OFFLINE. The verified, documented sequence is: start Steam NORMALLY
//     (NOT in Steam offline mode), set Dark Souls III to "Start offline" / offline
//     mode IN THE GAME'S OWN options ONCE, then ALWAYS launch the modded game with
//     launchmod_darksouls3.bat (Mod Engine 2) — never through Steam's Play button.
//     This plugin LAUNCHES the batch file for you and GUIDES the one-time in-game
//     offline-mode toggle (it cannot flip that in-game setting from outside).
//
//   * Steam requirement: the user's own paid copy of Dark Souls III (Steam appid
//     374320). The launcher DETECTS the Steam install but never modifies it (§11) —
//     Mod Engine 2 layers the mod at launch via the batch file; nothing in the Steam
//     game folder is changed by this plugin.
//
// ── WHAT THIS PLUGIN HONESTLY DOES ────────────────────────────────────────────
//   1. DETECT the Steam Dark Souls III install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, HKLM\...\WOW6432Node\Valve\Steam ->
//      InstallPath), parsing steamapps\libraryfolders.vdf for every library root and
//      locating steamapps\common\DARK SOULS III via appmanifest_374320.acf. A manual
//      install-dir OVERRIDE (Settings folder picker) takes precedence and is validated
//      + persisted in this plugin's OWN sidecar
//      (Games/ROMs/dark_souls_3/dark_souls_3_launcher.json) — Core/SettingsStore is
//      NOT modified.
//   2. INSTALL/UPDATE = download the client zip "DS3.Archipelago.<ver>.zip" from the
//      GitHub release (latest tag, pinned v3.0.13 fallback when the API is
//      unreachable) into the launcher's own folder (Games/DarkSouls3Archipelago/),
//      extract it (flattening a single wrapping sub-folder), and stamp the version.
//      This is the genuine, honest install — the package is self-contained — but the
//      BASE GAME is the user's own paid Steam copy, which is detected, never
//      downloaded.
//   3. LAUNCH (AP) = best effort: run the package's launchmod_darksouls3.bat (Mod
//      Engine 2) so the modded game starts. The connection itself is entered in the
//      DS3Randomizer setup tool (see header) — so this also OFFERS to open that tool,
//      and surfaces the session creds in Settings. No connection prefill is attempted.
//      ConnectsItself = true (the injected mod owns the slot; the launcher must NOT
//      hold its own ApClient on it). SupportsStandalone = true (plain Dark Souls III
//      via Steam runs without AP). Inert ReceiveItems / OnApStateChanged.
//   4. GUIDE the irreducible steps + links (setup guide, client repo, Steam page,
//      archipelago.gg) in Settings.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Witness/Undertale-style) ──
//   * RELEASE ASSET: the latest release's only asset was verified to be the single
//     zip "DS3.Archipelago.3.0.13.zip". ResolveLatestAppAsync picks the first Windows
//     .zip asset matching DS3/Archipelago (excluding source archives); the pinned
//     fallback URL targets that exact name at tag v3.0.13. Re-verify on a fork bump.
//   * "Installed" is judged by the presence of the extracted client in our own folder
//     — specifically the Mod Engine 2 launcher batch (launchmod_darksouls3.bat) and/or
//     the DS3Randomizer setup tool. The Steam base game being present does NOT by
//     itself make the game "installed" here — the client package is the AP-specific
//     piece this launcher manages.
//   * No plaintext AP password is ever written to disk by THIS plugin (the connection
//     is entered in the setup tool's GUI), so there is nothing to scrub on stop.
//   * Steam library parsing is a tolerant text scan; any failure degrades to "not
//     detected" rather than throwing.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DarkSouls3Plugin : IGamePlugin
{
    // ── Constants — the AP client package (real repo, verified 2026-06-14) ─────
    private const string APP_OWNER = "nex3";
    private const string APP_REPO  = "Dark-Souls-III-Archipelago-client";
    private const string AppRepoUrl = $"https://github.com/{APP_OWNER}/{APP_REPO}";
    private const string GH_APP_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{APP_OWNER}/{APP_REPO}/releases/latest";
    private const string GH_APP_RELEASES_URL =
        $"https://api.github.com/repos/{APP_OWNER}/{APP_REPO}/releases";

    /// Official Archipelago "Dark Souls III" setup guide.
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Dark%20Souls%20III/setup/en";

    /// Dark Souls III on Steam (the base game the player must own; appid 374320).
    private const string SteamAppId = "374320";
    private const string SteamStoreUrl =
        "https://store.steampowered.com/app/374320";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// Standard Steam install sub-folder name (steamapps\common\DARK SOULS III).
    private const string SteamCommonFolderName = "DARK SOULS III";

    /// Dark Souls III's game exe inside the Steam install (used to validate the
    /// folder; the game itself is launched through Mod Engine 2's batch file, NOT
    /// this exe directly).
    private const string GameExeName = "DarkSoulsIII.exe";

    /// The Mod Engine 2 launcher batch shipped in the client zip — the documented,
    /// correct way to start the modded game.
    private const string LaunchBatName = "launchmod_darksouls3.bat";

    /// The one-time setup / static-randomizer tool shipped in the client zip. Run it
    /// ONCE to enter the Archipelago room address / slot / password and click "Load".
    private const string RandomizerExeName = "DS3Randomizer.exe";

    /// Pinned fallback for the client when the GitHub API is unreachable. Tag v3.0.13
    /// verified live 2026-06-14; the single asset is the zip below. The API path is
    /// the normal route; this is only the net so an offline Install still has
    /// something to fetch.
    private const string FallbackVersion = "3.0.13";
    private const string FallbackZipName = "DS3.Archipelago.3.0.13.zip";
    private static readonly string FallbackZipUrl =
        $"{AppRepoUrl}/releases/download/v{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30), // the client zip is ~27 MB
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp, written after a successful download/extract.
    private const string VersionFileName = "ds3_ap_version.dat";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "dark_souls_3";
    public string DisplayName => "Dark Souls III";
    public string Subtitle    => "Native PC · Archipelago";

    /// EXACT AP game string — VERIFIED against worlds/dark_souls_3/__init__.py
    /// (class DarkSouls3World, game = "Dark Souls III").
    public string ApWorldName => "Dark Souls III";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "dark_souls_3.png");

    public string ThemeAccentColor => "#9A7B2E";   // Lothric gold / bonfire amber
    public string[] GameBadges     => new[] { "Requires Dark Souls III on Steam" };

    public string Description =>
        "Dark Souls III is FromSoftware's acclaimed action-RPG and the grim finale of " +
        "the Souls trilogy. This is the Archipelago integration, which shuffles its " +
        "weapons, rings, estus upgrades, key items and more into the multiworld. You " +
        "bring your own copy of Dark Souls III on Steam (64-bit Windows); a separate " +
        "Dark Souls III Archipelago client — a Mod Engine 2 mod plus a one-time setup " +
        "tool — is layered on top, and the mod connects to the multiworld itself (no " +
        "emulator, no bridge). The launcher detects your Steam install, downloads the " +
        "client, and can launch the modded game with one click. IMPORTANT: set Dark " +
        "Souls III to offline mode in the game's options before playing (anti-cheat " +
        "must be off), and always launch the modded game from the client — never " +
        "through Steam's Play button. You enter your server, slot and password once in " +
        "the client's setup tool.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means we have the extracted client package in our own folder
    /// (the Mod Engine 2 launcher batch and/or the DS3Randomizer setup tool). The
    /// Steam base game alone is not enough — the client is the AP piece this launcher
    /// manages.
    public bool IsInstalled => ResolveLaunchBat() != null || ResolveRandomizerExe() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the client package is extracted. (The base game lives in
    /// its own Steam install, which we only detect.)
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "DarkSouls3Archipelago");

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so the
    /// plugin stays a single self-contained source file — same approach as the Witness
    /// / Subnautica / Undertale plugins). BOM-less UTF-8.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "dark_souls_3_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;       // the Mod Engine 2 launch (batch) we started
    private Process? _randomizerProcess; // the setup tool, if we opened it

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Dark Souls III Archipelago mod reports checks/items/goal to the AP server
    // itself — the launcher relays nothing. These exist only for interface
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
        // 1. Resolve the latest client release (pinned fallback when offline).
        progress.Report((3, "Checking the latest Dark Souls III Archipelago client release..."));
        var (version, zipUrl, zipName) = await ResolveLatestAppAsync(ct);
        AvailableVersion = version;

        // 2. Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"Dark Souls III Archipelago client {version} is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Dark Souls III Archipelago client download on " +
                "GitHub. Check your internet connection, or download it manually from " +
                AppRepoUrl + "/releases/latest. Remember you also need your own copy of " +
                "Dark Souls III on Steam (appid 374320).");

        // 3. Download + extract the client package into our own folder.
        await DownloadAndExtractClientAsync(zipUrl, zipName ?? FallbackZipName, version, progress, ct);

        // 4. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        // 5. Honest closing note — base game, offline-mode toggle, and connection are
        //    not automated.
        string? steamDir = ResolveSteamGameDir();
        string steamLine = steamDir != null
            ? $"Detected your Steam copy of Dark Souls III at \"{steamDir}\". "
            : "Reminder: you need your own copy of Dark Souls III on Steam (appid 374320). ";

        progress.Report((100,
            $"Dark Souls III Archipelago client {version} installed. " + steamLine +
            "To play: (1) run the setup tool (" + RandomizerExeName + ") once and enter " +
            "your Archipelago room address (host:port), slot name and optional password, " +
            "then click Load; (2) set Dark Souls III to OFFLINE mode in the game's own " +
            "options; (3) launch the modded game from this launcher (it runs " +
            LaunchBatName + " via Mod Engine 2 — never use Steam's Play button). See " +
            "Settings for the full guided steps."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── AutoMod-style validation of a user-picked Steam folder ────────────────

    /// Used by the Settings folder picker: a valid Dark Souls III folder contains
    /// DarkSoulsIII.exe (or, defensively, the Game sub-folder convention). Return null
    /// when acceptable, else a short human-readable reason so they can pick again.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Dark Souls III install folder.";

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

        return "That does not look like a Dark Souls III installation. Pick the folder " +
               "that contains the game's executable (the Steam version is normally in " +
               "steamapps\\common\\DARK SOULS III, with the exe under its Game sub-folder).";
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: the AP server connection for Dark Souls III is entered in the SETUP
        // TOOL'S own dialog (DS3Randomizer.exe → room address / slot / password →
        // "Load"), then the modded game is launched via Mod Engine 2's batch file.
        // There is no command-line / config prefill we can reliably apply (verified —
        // see header). So launching from this tile starts the modded game; the user
        // must have run the setup tool once with the session credentials (the settings
        // panel + note surface those to copy, and Settings has a button to open the
        // setup tool).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartModdedGame();   // throws with honest guidance if the client isn't installed
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Dark Souls III runs perfectly well through Steam.
    public bool SupportsStandalone => true;

    /// The Dark Souls III Archipelago mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // Standalone = plain Dark Souls III, no mod. Prefer the Steam protocol launch
        // (so Steam's overlay / cloud saves engage) — and crucially this is the NORMAL
        // (un-modded) launch, which is the only safe way to play online.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true;
                return Task.CompletedTask;
            }
            catch { /* fall through to the detected exe */ }
        }

        string? dir = ResolveSteamGameDir();
        string? exe = dir != null ? FindGameExeIn(dir) : null;
        if (exe != null && File.Exists(exe))
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName         = exe,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? dir!,
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
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We launched the Mod Engine 2 batch (and possibly the setup tool) from here —
        // stop what we started. NOTE: the batch typically spawns the game as a child;
        // killing the process tree covers the launched chain. The AP "server" is never
        // ours to stop.
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        try { _randomizerProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        _gameProcess = null;
        _randomizerProcess = null;
        // No plaintext AP password is ever written to disk by this plugin (the
        // connection is entered in the setup tool's GUI), so there is nothing to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The Dark Souls III Archipelago mod receives items from the AP server directly
        // and applies them in-game; there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own "Archipelago connected" status in-game.
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
            Text = "Dark Souls III's Archipelago support uses a SEPARATE client — a Mod " +
                   "Engine 2 mod plus a one-time setup tool — layered on your own Steam " +
                   "copy of the game (64-bit Windows, appid 374320). The launcher detects " +
                   "your install and downloads the client, and can launch the modded game " +
                   "with one click. IMPORTANT: you must set Dark Souls III to OFFLINE mode " +
                   "in the game's options (anti-cheat must be off) and always launch the " +
                   "modded game from the client — never through Steam's Play button. You " +
                   "enter your server, slot and password in the setup tool's own window — " +
                   "the launcher cannot pre-fill the connection. These external steps are " +
                   "not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: client download status ───────────────────────────────
        panel.Children.Add(SectionHeader("ARCHIPELAGO CLIENT (MOD ENGINE 2 + SETUP TOOL)", muted));
        string? bat   = ResolveLaunchBat();
        string? rando = ResolveRandomizerExe();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = (bat != null || rando != null)
                ? "✓ Client downloaded in: " + GameDirectory
                : "Client not downloaded yet (use Install on the Play tab, or get it from " +
                  "the client repo below).",
            FontSize = 11, Foreground = (bat != null || rando != null) ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });
        if (rando != null)
        {
            var openTool = new System.Windows.Controls.Button
            {
                Content = "Open setup tool (" + RandomizerExeName + ")...",
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(10, 5, 10, 5),
                Margin  = new System.Windows.Thickness(0, 2, 0, 12),
                Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
                Foreground  = fg,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
                ToolTip = "Runs the one-time Archipelago setup tool. Enter your room address " +
                          "(host:port), slot name and optional password, then click Load.",
            };
            openTool.Click += (_, _) =>
            {
                try { StartRandomizer(); }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(ex.Message, "Could not open the setup tool",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
            };
            panel.Children.Add(openTool);
        }
        else
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "", Margin = new System.Windows.Thickness(0, 0, 0, 6),
            });
        }

        // ── Section: Steam base game (detected / override) ────────────────
        panel.Children.Add(SectionHeader("DARK SOULS III (STEAM BASE GAME)", muted));

        string? steamDir    = DetectSteamGameDir();
        string? overrideDir = LoadOverrideDir();
        string? gameDir     = ResolveSteamGameDir();
        string  detectMsg   = gameDir != null
            ? (overrideDir != null
                ? "✓ Using your selected folder: " + gameDir
                : "✓ Detected Steam install (appid " + SteamAppId + "): " + gameDir)
            : "Dark Souls III not detected. Pick your install folder below, or install " +
              "Dark Souls III via Steam first.";
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
            ToolTip     = "Your Dark Souls III install folder (the one whose Game sub-folder " +
                          "contains DarkSoulsIII.exe). Detected from Steam automatically; set " +
                          "it here to override (non-standard Steam library).",
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
                Title            = "Select your Dark Souls III install folder",
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
                    System.Windows.MessageBox.Show(bad, "Not a Dark Souls III folder",
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
            Text = "Steam installs are detected automatically (appid 374320). Use this " +
                   "picker only for a non-standard Steam library location.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: setup + connection steps ─────────────────────────────
        panel.Children.Add(SectionHeader("SETUP & CONNECTION", muted));
        foreach (string step in new[]
        {
            "1. Own and install Dark Souls III on Steam (64-bit Windows, appid 374320). " +
                "Version 3.x of the client supports only the latest game patch, 1.15.2.",
            "2. Use Install on the Play tab to download the Dark Souls III Archipelago " +
                "client (a single zip with a Mod Engine 2 mod + the setup tool). It is " +
                "layered on the game at launch — it does NOT modify your Steam game files.",
            "3. Run the setup tool (" + RandomizerExeName + ") ONCE — there is a button " +
                "above when the client is installed. Enter your Archipelago room address as " +
                "host:port (e.g. archipelago.gg:38281), your slot name, and the password if " +
                "your room has one, then click Load and wait a minute or two. (This launcher " +
                "cannot pre-fill the connection — it is entered in the setup tool.)",
            "4. Start Steam normally (NOT in Steam's offline mode), then set Dark Souls III " +
                "to OFFLINE mode in the GAME'S OWN options (anti-cheat must be off). You only " +
                "do this once.",
            "5. Launch the modded game from this launcher (Play). It runs " + LaunchBatName +
                " via Mod Engine 2 — never start the modded game through Steam's Play button. " +
                "Wait for the on-screen \"Archipelago connected\" message.",
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
            ("Dark Souls III on Steam ↗",            SteamStoreUrl),
            ("Dark Souls III Setup Guide ↗",         SetupGuideUrl),
            ("Dark Souls III Archipelago Client ↗",  AppRepoUrl),
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
        // Client releases are the AP-relevant news for this game.
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

    /// "v3.0.13" → "3.0.13" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest client release: version + the zip download URL + the zip
    /// asset name. Prefers an asset matching "DS3"/"archipelago" and ".zip"; falls
    /// back to the first non-source .zip; falls back to the pinned v3.0.13 direct URL
    /// when the API is unreachable.
    private async Task<(string Version, string? ZipUrl, string? ZipName)>
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
                string? preferred = null;   // a .zip named like DS3*/Archipelago*
                string? prefName  = null;
                string? anyZip    = null;   // any non-source .zip
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
                        (lower.Contains("ds3") || lower.Contains("archipelago") ||
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
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl, FallbackZipName);
    }

    // ── Private helpers — extracted-client resolution ─────────────────────────

    /// The Mod Engine 2 launcher batch inside our install, recursive (the zip may
    /// nest it under a ModEngine sub-folder). Prefer the canonical name; else any
    /// "launchmod*darksouls*.bat". Null if absent.
    private string? ResolveLaunchBat()
    {
        try
        {
            if (!Directory.Exists(GameDirectory)) return null;

            string direct = Path.Combine(GameDirectory, LaunchBatName);
            if (File.Exists(direct)) return direct;

            foreach (string bat in Directory.EnumerateFiles(GameDirectory, "*.bat", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(bat).ToLowerInvariant();
                if (name == LaunchBatName.ToLowerInvariant()) return bat;
                if (name.StartsWith("launchmod") &&
                    (name.Contains("darksouls") || name.Contains("ds3"))) return bat;
            }
        }
        catch { /* directory vanished / permission */ }
        return null;
    }

    /// The setup / static-randomizer tool inside our install, recursive (the zip
    /// places it under a "randomizer" sub-folder). Prefer the canonical name; else any
    /// "*ds3randomizer*.exe" / "*randomizer*.exe". Null if absent.
    private string? ResolveRandomizerExe()
    {
        try
        {
            if (!Directory.Exists(GameDirectory)) return null;

            // Conventional location first.
            string conv = Path.Combine(GameDirectory, "randomizer", RandomizerExeName);
            if (File.Exists(conv)) return conv;
            string flat = Path.Combine(GameDirectory, RandomizerExeName);
            if (File.Exists(flat)) return flat;

            foreach (string exe in Directory.EnumerateFiles(GameDirectory, "*.exe", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (name == Path.GetFileNameWithoutExtension(RandomizerExeName).ToLowerInvariant()) return exe;
            }
            foreach (string exe in Directory.EnumerateFiles(GameDirectory, "*.exe", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (name.Contains("ds3randomizer") || name.Contains("randomizer")) return exe;
            }
        }
        catch { /* directory vanished / permission */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Launch the modded game through the client's Mod Engine 2 batch file. Throws
    /// with honest guidance when the client has not been installed yet (so the UI
    /// surfaces the real next step). The connection itself must already have been
    /// entered in the setup tool (see header).
    private void StartModdedGame()
    {
        string? bat = ResolveLaunchBat();
        if (bat == null)
            throw new FileNotFoundException(
                "The Dark Souls III Archipelago client has not been installed yet (the " +
                "Mod Engine 2 launcher \"" + LaunchBatName + "\" was not found). Click " +
                "Install on the Play tab (or get it from " + AppRepoUrl + "/releases/latest). " +
                "Then run the setup tool once, set the game to offline mode, and launch again.",
                Path.Combine(GameDirectory, LaunchBatName));

        string workDir = Path.GetDirectoryName(bat) ?? GameDirectory;

        // Run the batch via cmd.exe so the Mod Engine 2 launcher's relative paths
        // resolve against the batch's own folder. UseShellExecute=true keeps it
        // simple and lets the batch open its own console as designed.
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = bat,
            WorkingDirectory = workDir,
            UseShellExecute  = true,
        }) ?? throw new InvalidOperationException(
            "Failed to launch the Dark Souls III Archipelago client (Mod Engine 2).");

        _gameProcess = proc;
        IsRunning    = true;
        HookExit(proc);
    }

    /// Open the one-time setup / static-randomizer tool. Throws with honest guidance
    /// when it has not been downloaded yet.
    private void StartRandomizer()
    {
        string? exe = ResolveRandomizerExe();
        if (exe == null)
            throw new FileNotFoundException(
                "The Dark Souls III setup tool (\"" + RandomizerExeName + "\") was not " +
                "found. Click Install on the Play tab first (or get the client from " +
                AppRepoUrl + "/releases/latest).",
                Path.Combine(GameDirectory, "randomizer", RandomizerExeName));

        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exe,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? GameDirectory,
            UseShellExecute  = true,
        }) ?? throw new InvalidOperationException(
            "Failed to start the Dark Souls III setup tool.");

        _randomizerProcess = proc;
        try
        {
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) => { _randomizerProcess = null; };
        }
        catch { /* non-fatal */ }
    }

    /// Wire a process's Exited event to our IsRunning / GameExited, defensively.
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

    // ── Private helpers — Steam / Dark Souls III detection ─────────────────────

    /// The Dark Souls III install dir to use: the override (if set and valid) wins,
    /// else the Steam-detected install. Null when nothing is found.
    private string? ResolveSteamGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeGameDir(ov))
            return ov;

        try { return DetectSteamGameDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Dark Souls III if it (or its Game sub-folder) contains
    /// DarkSoulsIII.exe. The Steam layout is ...\DARK SOULS III\Game\DarkSoulsIII.exe;
    /// accept either the parent or the Game folder being pointed at.
    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, GameExeName))) return true;
            if (File.Exists(Path.Combine(dir, "Game", GameExeName))) return true;
            return FindGameExeIn(dir) != null;
        }
        catch { return false; }
    }

    /// Find DarkSoulsIII.exe in `dir`: directly, in a "Game" sub-folder, else a
    /// shallow recursive search (the Steam tree is small). Null if none.
    private static string? FindGameExeIn(string dir)
    {
        try
        {
            string direct = Path.Combine(dir, GameExeName);
            if (File.Exists(direct)) return direct;

            string inGame = Path.Combine(dir, "Game", GameExeName);
            if (File.Exists(inGame)) return inGame;

            foreach (string exe in Directory.EnumerateFiles(dir, GameExeName, SearchOption.AllDirectories))
                return exe;
        }
        catch { /* permission / vanished */ }
        return null;
    }

    /// Detect the Steam Dark Souls III install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_374320.acf exists → steamapps\common\DARK SOULS III. Never throws.
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
    /// steamapps\libraryfolders.vdf. Tolerant text scan (the VDF is a simple quoted
    /// key/value tree; we only need the path values).
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

    /// Read the "installdir" value from an appmanifest_*.acf (same quoted-pair format
    /// as VDF). Returns null if absent.
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

    // ── Private helpers — download / extract the client ───────────────────────

    /// Download the client's release zip and extract it INTO our own GameDirectory.
    /// The zip is a self-contained Mod Engine 2 tree (mod + setup tool + launcher
    /// batch). If the zip wraps everything in a single sub-folder, flatten it so the
    /// launcher batch / setup tool land where ResolveLaunchBat / ResolveRandomizerExe
    /// expect them.
    private async Task DownloadAndExtractClientAsync(
        string zipUrl,
        string zipName,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"ds3-archipelago-{version}-{Guid.NewGuid():N}.zip");
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
                        progress.Report((pct, $"Downloading client... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((74, "Extracting the client..."));
            Directory.CreateDirectory(GameDirectory);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, GameDirectory, overwriteFiles: true);

            // If the launcher batch / setup tool did not land (zip wrapped everything
            // in ONE top-level sub-folder), flatten that single sub-folder up.
            if (ResolveLaunchBat() == null && ResolveRandomizerExe() == null)
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

            progress.Report((92, "Client files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the Steam install-dir override)
    // in its OWN JSON file so it stays a single self-contained source file and does
    // not modify Core/SettingsStore. BOM-less UTF-8, read-modify-write.

    private sealed class DarkSouls3Settings
    {
        public string? InstallOverride { get; set; }
    }

    private DarkSouls3Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<DarkSouls3Settings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(DarkSouls3Settings s)
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
