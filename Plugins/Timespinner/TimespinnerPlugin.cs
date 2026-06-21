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

namespace LauncherV2.Plugins.Timespinner;

// ═══════════════════════════════════════════════════════════════════════════════
// TimespinnerPlugin — install / launch for "Timespinner" (Lunar Ray Games, 2018)
// played through the TsRandomizer (Timespinner Randomizer) mod by Jarno458, which
// contains the in-game Archipelago Multiworld client. This is a NATIVE
// "ConnectsItself" integration in the same family as the shipped Hollow Knight /
// TUNIC / Stardew Valley / Jak plugins: the game itself speaks to the AP server
// (no emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Timespinner (Steam appid 368620; Humble and GOG also work per the AP setup
// guide), and Archipelago support is delivered as a DROP-IN randomizer launcher
// (TsRandomizer.exe) added on top. The honest integration ceiling — exactly like
// the shipped Hollow Knight / TUNIC plugins — is "automate what is possible, guide
// the irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "Timespinner" (verified live 2026-06-14 against
//     worlds/timespinner/__init__.py: `game = "Timespinner"`). GameId here =
//     "timespinner". Timespinner is a CORE Archipelago world (it ships inside
//     Archipelago itself — no custom_worlds drop needed to generate).
//
//   * THE MOD repo is Jarno458/TsRandomizer (verified live 2026-06-14 — this is
//     the repo the official AP "Timespinner" setup guide links, by Jarno458, the
//     same author the brief's "JaThePlayer" alias refers to). The latest release
//     (v1.38.6, verified live) ships FOUR assets:
//         Windows.<ver>.zip               (the mod — Windows; preferred)
//         Linux.<ver>.zip                 (Linux — ignored)
//         Mac.<ver>.zip                   (macOS — ignored)
//         Custom.Sprites.Examples.v1.zip  (optional cosmetic pack — ignored)
//     e.g. "Windows.1.38.6.zip". v1.38.6 Windows is pinned as the offline
//     fallback so install still works when the GitHub API is unreachable.
//
//   * HOW IT INSTALLS (VERIFIED against the official setup guide): TsRandomizer is
//     NOT a separate game and NOT a BepInEx/loader mod — it is a DROP-IN. You
//     "extract the zip to the folder where your Timespinner game is installed",
//     which lands TsRandomizer.exe (plus its support DLLs) alongside
//     Timespinner.exe, and then you "launch TsRandomizer.exe instead of
//     Timespinner.exe". It loads and modifies the base game in memory. There is no
//     external prerequisite to stage (no BepInEx), so this plugin CAN best-effort
//     download + extract the Windows zip straight into the detected install — and
//     then leave the in-game connection to you, and says so. Faking a fully-
//     automated "ready to play" would be dishonest theatre.
//
//   * CONNECTION is made IN-GAME (VERIFIED against the official setup guide): after
//     the mod is installed, run TsRandomizer.exe, choose "New Game", switch the
//     seed-selection screen to "Archipelago" mode (left/right on the menu), enter
//     your Archipelago server / slot / password in the login menu that opens
//     (paste is supported), and select "Connect". There is NO command-line arg and
//     NO documented connection config file this launcher can pre-write (verified —
//     the setup guide documents only the in-game menu). So this plugin does NOT
//     attempt a connection prefill or invent an undocumented one (same honest
//     stance as Hollow Knight / TUNIC / Jak) — the settings panel + post-install
//     note surface the session's host / port / slot for the user to type into the
//     in-game fields.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Timespinner install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Timespinner via appmanifest_368620.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and takes
//      precedence; it is validated (must contain Timespinner.exe) and persisted in
//      this plugin's OWN sidecar (Games/ROMs/timespinner/timespinner_launcher.json)
//      — Core/SettingsStore is NOT modified.
//   2. INSTALL/UPDATE (best effort) = download the mod's "Windows.<ver>.zip" from
//      the real release and extract it INTO the detected Timespinner folder
//      (alongside Timespinner.exe), giving TsRandomizer.exe. The plugin then
//      presents clear, numbered, TUNIC/Hollow-Knight-style guided steps + links
//      (mod repo, the official AP setup guide, archipelago.gg) so the user can run
//      TsRandomizer.exe and connect in-game. Never a fake one-click.
//   3. LAUNCH = run TsRandomizer.exe from the detected/override install (the AP
//      build), NOT the base Timespinner.exe. ConnectsItself = true (the mod owns
//      the slot — the launcher must NOT hold its own ApClient on it).
//      SupportsStandalone = true (TsRandomizer also runs as a plain single-player
//      randomizer without AP, and plain Timespinner is right there too). No
//      connection prefill (entered in-game), stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", TUNIC/Hollow-Knight-style) ─
//   * "Installed" is judged by the presence of TsRandomizer.exe in a detected/
//     override Timespinner install (case-insensitive, recursive) — NOT by an
//     OUR-OWN version stamp, because the user may instead install the mod by hand,
//     which this launcher should honor. If no Timespinner install is detected, the
//     tile simply reads "not installed".
//   * The exact zip layout was not inspected offline. The extractor drops the zip
//     into the install root; if the zip nests everything in a single sub-folder,
//     it is flattened so TsRandomizer.exe lands next to Timespinner.exe.
//   * Steam library parsing is defensive: a hand-written tolerant VDF scan that
//     pulls quoted "path" values; any failure degrades to "Timespinner not found"
//     rather than throwing.
//   * No plaintext AP password is ever written by this plugin (the connection is
//     entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class TimespinnerPlugin : IGamePlugin
{
    // ── Constants — the TsRandomizer mod (real repo, verified 2026-06-14) ──────
    private const string MOD_OWNER = "Jarno458";
    private const string MOD_REPO  = "TsRandomizer";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Timespinner/setup/en";
    private const string GameInfoUrl     = "https://archipelago.gg/games/Timespinner/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Timespinner appid 368620.
    private const string TsSteamAppId = "368620";
    private static readonly string SteamRunUrl = $"steam://rungameid/{TsSteamAppId}";

    /// The standard Steam install sub-folder name for Timespinner.
    private const string SteamCommonFolderName = "Timespinner";

    /// The base-game executable name (what TsRandomizer is dropped beside).
    private const string BaseExeName = "Timespinner.exe";

    /// The Archipelago randomizer launcher dropped into the install (verified).
    /// This is what we run, NOT Timespinner.exe.
    private const string ModExeName = "TsRandomizer.exe";

    /// Pinned fallback for the mod when the GitHub API is unreachable. v1.38.6
    /// verified live 2026-06-14; the Windows asset is "Windows.1.38.6.zip".
    private const string FallbackVersion = "1.38.6";
    private const string FallbackZipName = "Windows.1.38.6.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "timespinner";
    public string DisplayName => "Timespinner";
    public string Subtitle    => "Native PC · Archipelago";

    /// EXACT AP game string — verified against worlds/timespinner/__init__.py
    /// (`game = "Timespinner"`).
    public string ApWorldName => "Timespinner";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "timespinner.png");

    public string ThemeAccentColor => "#7A4F9A";   // Lachiem twilight violet
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Timespinner, the 2018 time-travelling Metroidvania by Lunar Ray Games, " +
        "played through the TsRandomizer (Timespinner Randomizer) mod by Jarno458 — " +
        "which bundles an in-game Archipelago client, so the game connects to the " +
        "multiworld itself with no emulator and no bridge. Relics, orbs, spells, " +
        "familiars and more are shuffled across the multiworld. You bring your own " +
        "copy of Timespinner (Steam, Humble, or GOG); TsRandomizer is a drop-in you " +
        "run instead of the base game. The launcher detects your Steam install and " +
        "can stage the randomizer into it, and guides the rest. You connect to your " +
        "server from the in-game Archipelago menu.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means TsRandomizer.exe is present in a detected/override
    /// Timespinner install. (We do NOT gate on our own stamp — the user may have
    /// installed the mod by hand, which we honor.)
    public bool IsInstalled => FindInstalledModExe() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads (the mod zip) and any working files. The
    /// actual mod is extracted INTO the Timespinner install folder, not here.
    /// Exposed as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Timespinner");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Hollow Knight /
    /// TUNIC / Stardew / Jak). Per the brief, lives under Games/ROMs/timespinner/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "timespinner_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The TsRandomizer mod reports checks/items/goal to the AP server itself — the
    // launcher relays nothing. These exist for interface compatibility
    // (ConnectsItself = true).
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
            // Best-effort: read the version we stamped next to a direct install if
            // present; otherwise report "installed" when the mod exe exists.
            InstalledVersion = FindInstalledModExe() != null
                ? (ReadStampedVersion() ?? "installed")
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
                await GitHubHelper.FetchLatestTagAsync(MOD_OWNER, MOD_REPO, ct));
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
        // 0. We need a Timespinner install to drop the randomizer into. Prefer an
        //    explicit override; else auto-detect the Steam install.
        progress.Report((2, "Locating your Timespinner installation..."));
        string? tsDir = ResolveTimespinnerDir();
        if (tsDir == null)
            throw new InvalidOperationException(
                "Could not find a Timespinner installation. Open this game's Settings " +
                "and pick your Timespinner folder (the one containing Timespinner.exe), " +
                "or install Timespinner via Steam first. TsRandomizer is added on top " +
                "of your own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback).
        progress.Report((6, "Checking the latest TsRandomizer release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the TsRandomizer (Windows) download on GitHub. Check " +
                "your internet connection, or download it manually from " +
                ModRepoUrl + "/releases — see Settings for the guided steps. The mod " +
                "repo is " + ModRepoUrl + ".");

        // 2. Download + extract the Windows zip INTO the Timespinner install folder.
        //    TsRandomizer is a drop-in: its files (TsRandomizer.exe + DLLs) sit
        //    alongside Timespinner.exe, and you run TsRandomizer.exe instead.
        await DownloadAndExtractModAsync(zipUrl, version, tsDir, progress, ct);

        // 3. Stamp the version next to our sidecar so the tile can show it. (This is
        //    informational only — IsInstalled is judged by the exe's presence.)
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool ok = FindInstalledModExe() != null;
        progress.Report((100,
            ok
                ? $"Staged TsRandomizer {version} into your Timespinner folder. To play: " +
                  "press Play (this launcher runs TsRandomizer.exe, not the base game), " +
                  "choose New Game, switch the seed screen to Archipelago, enter your " +
                  "server / slot / password, and Connect. Open Settings for the guided " +
                  "steps and links. (This launcher cannot pre-fill the connection — it " +
                  "is entered in-game.)"
                : $"Downloaded TsRandomizer {version}, but TsRandomizer.exe was not found " +
                  "in your Timespinner folder afterwards. Confirm the install folder in " +
                  "Settings, or extract the Windows zip into it by hand (see the guided " +
                  "steps and links)."));
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
        // HONEST: the AP server connection for Timespinner is entered in the IN-GAME
        // Archipelago menu (New Game -> switch to Archipelago -> server / slot /
        // password -> Connect). There is no documented command-line / config prefill
        // this launcher can apply (verified — see header). So launching from this
        // tile just runs TsRandomizer.exe; the user connects in-game with the session
        // credentials (the settings panel + note surface those values to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartTimespinner();
        return Task.CompletedTask;
    }

    /// TsRandomizer also runs as a plain single-player randomizer without AP, and
    /// plain Timespinner is right there — standalone play is supported.
    public bool SupportsStandalone => true;

    /// The TsRandomizer mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartTimespinner();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started TsRandomizer.exe from here. Kill what we launched.
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No plaintext AP password is ever written to disk by this plugin (the
        // connection is entered in-game), so there is nothing to scrub.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The TsRandomizer mod receives items from the AP server directly; there is
        // nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Timespinner folder contains
    /// Timespinner.exe (the randomizer drops in beside it). Return null when
    /// acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Timespinner install folder.";

        if (LooksLikeTimespinnerDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeTimespinnerDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Timespinner installation. Pick the folder " +
               "that contains Timespinner.exe (for Steam this is usually " +
               @"...\steamapps\common\Timespinner).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Unverified-offline honesty header ─────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Timespinner is your own game (Steam / Humble / GOG) with the " +
                   "TsRandomizer mod added on top. TsRandomizer is a drop-in: its files " +
                   "sit next to Timespinner.exe and you run TsRandomizer.exe instead. The " +
                   "launcher detects your Steam install and can stage the randomizer into " +
                   "it (Play runs TsRandomizer.exe, not the base game). You connect to " +
                   "your server from the in-game Archipelago menu. These external steps " +
                   "are not verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "TIMESPINNER INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? tsDir       = ResolveTimespinnerDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = tsDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + tsDir
                : "Detected Steam install: " + tsDir)
            : "Timespinner not detected. Pick your install folder below, or install " +
              "Timespinner via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = tsDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // mod status line
        string? modExe = FindInstalledModExe();
        panel.Children.Add(new TextBlock
        {
            Text = modExe != null
                    ? "TsRandomizer found: " + modExe
                    : "TsRandomizer not found in your Timespinner folder yet (use Install " +
                      "on the Play tab, or extract the Windows zip into it by hand).",
            FontSize = 11, Foreground = modExe != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? tsDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Timespinner install folder (the one containing " +
                          "Timespinner.exe). Detected from Steam automatically; set it " +
                          "here to override (Humble / GOG / non-standard Steam library).",
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
                Title            = "Select your Timespinner install folder (contains Timespinner.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? tsDir ?? "")
                                   ? (overrideDir ?? tsDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    MessageBox.Show(bad, "Not a Timespinner folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeTimespinnerDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeTimespinnerDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 368620). Use this " +
                   "picker for Humble, GOG, or a non-standard Steam library.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (this session) ────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Run TsRandomizer (press Play), choose New Game, then on the seed " +
                   "selection screen switch the mode to Archipelago (left/right on the " +
                   "menu). Enter your Server, Slot name, and Password (if any) in the " +
                   "login menu that opens — paste is supported — and select Connect. This " +
                   "launcher does not pre-fill the connection.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Timespinner (Steam, Humble, or GOG). Install it if you have not. Use the " +
                "picker above if it was not detected.",
            "2. Install TsRandomizer: Install on the Play tab downloads the Windows release and " +
                "extracts it into your Timespinner folder (alongside Timespinner.exe), or do it " +
                "by hand from the mod releases (link below).",
            "3. Press Play. This launcher runs TsRandomizer.exe (the Archipelago build), NOT the " +
                "base Timespinner.exe.",
            "4. To play: choose New Game, switch the seed screen to Archipelago, enter your " +
                "Server / Slot / Password, then select Connect.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("TsRandomizer (GitHub) ↗",   ModRepoUrl),
            ("Timespinner Setup Guide ↗", SetupGuideUrl),
            ("Timespinner Guide (AP) ↗",  GameInfoUrl),
            ("Archipelago Official ↗",    ArchipelagoSite),
        })
        {
            var btn = new Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => { try { Process.Start(
                new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
            panel.Children.Add(btn);
        }
        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Mod releases are the AP-relevant news for this game.
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_URL, ct);
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

    /// "v1.38.6" → "1.38.6" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the Windows zip download URL.
    /// Prefers the "Windows.<ver>.zip" asset (the mod), NOT the Linux/Mac zips or
    /// the optional Custom.Sprites pack. Falls back to the pinned 1.38.6 direct URL
    /// when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;   // the Windows mod zip (Windows*.zip)
                string? anyWinZip = null;   // any windows-tagged zip
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    // Skip the non-Windows builds and the optional cosmetic pack.
                    if (lower.Contains("linux") || lower.Contains("mac") ||
                        lower.Contains("osx")   || lower.Contains("darwin") ||
                        lower.Contains("sprite") || lower.Contains("source"))
                        continue;

                    anyWinZip ??= url;
                    if (preferred == null &&
                        (lower.StartsWith("windows") || lower.Contains("win")))
                        preferred = url;
                }
                string? zip = preferred ?? anyWinZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — Steam / Timespinner detection ───────────────────────

    /// The Timespinner install dir to use: the override (if set and valid) wins,
    /// else the Steam-detected install. Null when nothing is found.
    private string? ResolveTimespinnerDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeTimespinnerDir(ov))
            return ov;

        try { return DetectSteamTimespinnerDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Timespinner if it has Timespinner.exe and/or the
    /// already-installed TsRandomizer.exe (so an override that points at a
    /// hand-modded install still validates even if named oddly).
    private static bool LooksLikeTimespinnerDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, BaseExeName))) return true;
            if (File.Exists(Path.Combine(dir, ModExeName)))  return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Timespinner install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_368620.acf exists → steamapps\common\Timespinner.
    private static string? DetectSteamTimespinnerDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{TsSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    // Prefer the installdir named in the manifest; fall back to the
                    // conventional "Timespinner" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeTimespinnerDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeTimespinnerDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath). Both are tried; duplicates are harmless.
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

        // Last-ditch conventional location.
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
            // find the opening quote of the value
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

    // ── Private helpers — installed-mod detection ─────────────────────────────

    /// Find TsRandomizer.exe in the detected/override Timespinner install (the
    /// drop-in sits at the install root; scan recursively + case-insensitively to
    /// be forgiving). Returns the exe path or null.
    private string? FindInstalledModExe()
    {
        try
        {
            string? ts = ResolveTimespinnerDir();
            if (ts == null) return null;

            // Fast path: the documented location, right next to Timespinner.exe.
            string direct = Path.Combine(ts, ModExeName);
            if (File.Exists(direct)) return direct;

            foreach (string exe in Directory.EnumerateFiles(ts, "*.exe", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(exe).Equals(ModExeName, StringComparison.OrdinalIgnoreCase))
                    return exe;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start TsRandomizer.exe (the Archipelago build) from the detected/override
    /// install. If the randomizer is not present but the base game is, surface a
    /// clear message (the user must install the mod). If nothing is found but Steam
    /// is present, fall back to the steam:// URL for the BASE game so the tile is
    /// not a dead end (the user can then run TsRandomizer.exe themselves).
    private void StartTimespinner()
    {
        string? modExe = FindInstalledModExe();

        if (modExe != null && File.Exists(modExe))
        {
            string workDir = Path.GetDirectoryName(modExe) ?? ResolveTimespinnerDir() ?? AppContext.BaseDirectory;
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = modExe,
                WorkingDirectory = workDir,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start TsRandomizer.");

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

        // The randomizer is not installed. If we at least know where Timespinner is,
        // tell the user to install the mod (Play runs the mod, not the base game).
        string? ts = ResolveTimespinnerDir();
        if (ts != null)
            throw new FileNotFoundException(
                "TsRandomizer.exe was not found in your Timespinner folder. Click " +
                "Install on the Play tab to stage the Archipelago randomizer, or extract " +
                "the Windows release into your Timespinner folder by hand. (Timespinner " +
                "is played through TsRandomizer.exe, not the base game.)",
                ModExeName);

        // Last resort: launch the BASE game via Steam if Steam is installed, so the
        // tile is not a complete dead end (the user still needs the mod to play AP).
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process, so we won't track exit
                return;
            }
            catch { /* fall through to the error below */ }
        }

        throw new FileNotFoundException(
            "Could not find TsRandomizer.exe or a Timespinner install. Open this game's " +
            "Settings and pick your Timespinner folder, install Timespinner via Steam, " +
            "then click Install to stage the Archipelago randomizer.",
            ModExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod's Windows zip and extract its contents INTO the Timespinner
    /// install folder (alongside Timespinner.exe). If the zip nests everything in a
    /// single sub-folder, flatten it so TsRandomizer.exe lands at the install root.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string tsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"tsrandomizer-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"tsrandomizer-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading TsRandomizer {version}..."));
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
                        int pct = (int)(10 + 55 * downloaded / total);
                        progress.Report((pct, $"Downloading TsRandomizer... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting the randomizer..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // Find the directory that actually holds TsRandomizer.exe inside the
            // extracted tree (the zip may put it at the root, or nest it one level).
            string srcDir = FindModSourceDir(tempExtract) ?? tempExtract;

            progress.Report((82, "Installing the randomizer into your Timespinner folder..."));
            Directory.CreateDirectory(tsDir);
            CopyDirectory(srcDir, tsDir);   // drop in beside Timespinner.exe (overwrite)

            progress.Report((92, "Randomizer files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Find the folder inside an extracted tree that directly contains
    /// TsRandomizer.exe (root first, then any nested wrapper). Returns null when
    /// the exe is not found anywhere (the caller then falls back to the root).
    private static string? FindModSourceDir(string root)
    {
        try
        {
            if (File.Exists(Path.Combine(root, ModExeName))) return root;
            foreach (string exe in Directory.EnumerateFiles(root, ModExeName, SearchOption.AllDirectories))
                return Path.GetDirectoryName(exe);
        }
        catch { /* ignore */ }
        return null;
    }

    /// Recursively copy a directory tree into an existing destination, overwriting.
    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the install-dir override + an
    // informational version stamp) in its OWN JSON file so it stays a single
    // self-contained source file and does not modify Core/SettingsStore. BOM-less
    // UTF-8, read-modify-write (same approach as Hollow Knight / TUNIC / Jak).

    private sealed class TimespinnerSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private TimespinnerSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<TimespinnerSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(TimespinnerSettings s)
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

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    private void WriteStampedVersion(string v) { var s = LoadSettings(); s.ModVersion = v; SaveSettings(s); }
}
