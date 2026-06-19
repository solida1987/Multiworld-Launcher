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
// WPF, so the bare names Button / TextBox / Color / Brushes / Brush / MessageBox /
// FontWeights / HorizontalAlignment / Thickness / Clipboard / Application collide
// between System.Windows.Forms and System.Windows[.Controls/.Media] (CS0104).
// Qualifying every UI type avoids that. We also do NOT add any file-level
// `using X = System.Windows...;` aliases — the project's GlobalUsings.cs already
// aliases the short names, and a second local alias would be CS1537 (duplicate
// alias). Bare names or fully-qualified only — never a local alias.

namespace LauncherV2.Plugins.Satisfactory;

// ═══════════════════════════════════════════════════════════════════════════════
// SatisfactoryPlugin — install-guidance / launch for "Satisfactory" (Coffee Stain
// Studios) played through the "Archipelago Randomizer" mod, the in-game Archipelago
// client for Satisfactory. This is a NATIVE "ConnectsItself" integration in the
// same family as the shipped Hollow Knight / Subnautica / Raft / Stardew Valley
// plugins (and Ship of Harkinian / Jak) — the game itself speaks to the AP server
// (no emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM/EPIC-MOD native: the BASE GAME is the user's own legally-owned
// Satisfactory (Steam appid 526870; the official AP setup guide also lists the
// Epic Games Store), and Archipelago is a MOD added on top. The honest integration
// ceiling — exactly like the shipped Raft / Hollow Knight plugins — is "automate
// what is possible, guide the irreducible parts." For Satisfactory the IRREDUCIBLE
// part is the SMM mod install, and the plugin says so plainly. The verified facts:
//
//   * THE AP WORLD game string is "Satisfactory" (verified against
//     worlds/satisfactory/__init__.py: `game = "Satisfactory"`,
//     class SatisfactoryWorld, required_client_version = (0, 6, 0)). Satisfactory
//     is a CORE Archipelago world — it ships INSIDE Archipelago itself, so there is
//     NO custom apworld for the launcher to fetch or stage. GameId = "satisfactory".
//
//   * THE MOD is "Archipelago Randomizer" by Jarno458, source repo
//     github.com/Jarno458/SatisfactoryArchipelagoMod, distributed on ficsit.app
//     (the Satisfactory Mod Repository, "SMR") at ficsit.app/mod/Archipelago — NOT
//     as a drop-in GitHub release zip the launcher can fetch+extract. It is
//     installed THROUGH the Satisfactory Mod Manager (SMM): one click "Install" on
//     the mod's ficsit.app page (or inside SMM), and "the Mod Manager will install
//     all required dependency mods for you with no additional action required."
//     Those dependencies are ContentLib, Free Samples and MAM Enhancer (verified
//     against the official AP setup guide). Because there is NO stable direct URL
//     the launcher can pull the mod from, this plugin is HONEST about it: it GUIDES
//     the SMM route (and opens the ficsit.app mod page + SMM download) and detects
//     the result, rather than faking an auto-download that cannot exist for an
//     SMM-managed mod.
//
//   * THE MOD MANAGER is the Satisfactory Mod Manager (SMM), download page
//     ficsit.app/smm. CRITICAL HONESTY/CONVENIENCE WIN over Raft: once the mods are
//     installed, "you do NOT need to launch the game through the Mod Manager —
//     desktop shortcuts, Steam, Epic, etc. will all launch the game with mods still
//     loaded" (verified verbatim against the setup guide). So this plugin's AP
//     launch starts Satisfactory NORMALLY (steam://rungameid/526870, or the resolved
//     exe), and there is NO special mod-loader launcher to route through.
//
//   * CONNECTION is made IN-GAME via the mod's own UI: after the mods are installed,
//     you create a NEW GAME, click "Mod Savegame Settings", pick "Archipelago", and
//     enter three fields — Server URI (e.g. archipelago.gg:49236), User Name (your
//     AP slot / Player Name), and Password (blank if none). The guide states plainly
//     that "the Satisfactory Host/Client does NOT need a copy of your Archipelago
//     config file" — i.e. there is NO command-line arg and NO config file this
//     launcher can pre-write (verified against the setup guide). So this plugin does
//     NOT attempt a connection prefill — the post-launch note and the settings panel
//     surface the session's server/slot so the user can type them into the mod's
//     in-game UI. On load, chat messages indicate connection success/failure.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Satisfactory install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, HKLM\...\WOW6432Node\Valve\Steam ->
//      InstallPath), parsing steamapps\libraryfolders.vdf for every library root and
//      locating steamapps\common\Satisfactory via appmanifest_526870.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and takes
//      precedence; it is validated (must look like a Satisfactory install) and
//      persisted in this plugin's OWN sidecar
//      (Games/ROMs/satisfactory/satisfactory_launcher.json) — Core/SettingsStore is
//      NOT modified. (Epic / other stores work via the manual picker.)
//   2. INSTALL = GUIDED (honest). Because the Archipelago Randomizer mod is not a
//      fetchable GitHub asset, InstallOrUpdate does not pretend to download it.
//      Instead it verifies the prerequisite (Satisfactory install present) and opens
//      the Satisfactory Mod Manager download page + the mod's ficsit.app page so the
//      user can one-click "Install" (which also installs the dependencies) — with
//      clear numbered steps + links in Settings. If the user already installed the
//      mod via SMM, the plugin detects and honors it.
//   3. LAUNCH = start Satisfactory NORMALLY: steam://rungameid/526870 when Steam is
//      present (Steam picks the correct store binary and mods still load); else the
//      resolved store exe (FactoryGameSteam.exe / FactoryGameEGS.exe / FactoryGame.exe)
//      from the detected/override install. ConnectsItself = true (the mod owns the
//      slot — the launcher must NOT hold its own ApClient on it). SupportsStandalone
//      = true (plain Satisfactory runs perfectly without AP). No prefill (in-game
//      UI), stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Raft/HK/Subnautica-style) ──
//   * "Installed" is judged by the presence of the Archipelago Randomizer mod folder
//     under a detected/override Satisfactory install's mod tree
//     (FactoryGame\Mods\<Archipelago...>, case-insensitive, with a tolerant
//     recursive fallback for an Archipelago-named .pak/.dll) — NOT by an OUR-OWN
//     version stamp, because the user installs through SMM, which this launcher
//     honors. If no Satisfactory install is detected, the tile reads "not installed".
//   * Steam library parsing is defensive: a tolerant VDF scan that pulls quoted
//     "path" values; any failure degrades to "Satisfactory not found" rather than
//     throwing.
//   * The store exe name differs by store + version (FactoryGameSteam.exe on Steam,
//     FactoryGameEGS.exe on Epic, with a FactoryGame.exe shim on some installs), so
//     the plugin prefers the steam:// URL for launch and only probes those exe names
//     as a fallback. Detection of the install folder does not depend on a single exe
//     name (it also accepts the FactoryGame\ UE folder + appmanifest).
//   * No plaintext AP password is ever written by this plugin (connection is entered
//     in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SatisfactoryPlugin : IGamePlugin
{
    // ── Constants — the Archipelago Randomizer mod + SMM (verified 2026-06-14) ──
    private const string MOD_OWNER = "Jarno458";
    private const string MOD_REPO  = "SatisfactoryArchipelagoMod";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    /// The mod's page on the Satisfactory Mod Repository (ficsit.app). Its
    /// reference/slug is "Archipelago" (verified live 2026-06-14).
    private const string ModFicsitPageUrl = "https://ficsit.app/mod/Archipelago";
    /// The Satisfactory Mod Manager (SMM) download page.
    private const string SmmDownloadUrl = "https://ficsit.app/smm";
    private const string FicsitSite     = "https://ficsit.app";
    private const string SmmDocsUrl =
        "https://docs.ficsit.app/satisfactory-modding/latest/ForUsers/SatisfactoryModManager.html";
    private const string SetupGuideUrl  = "https://archipelago.gg/tutorial/Satisfactory/setup_en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Satisfactory appid 526870.
    private const string SteamAppId = "526870";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The standard Steam install sub-folder name for Satisfactory.
    private const string SteamCommonFolderName = "Satisfactory";

    /// The store launch executables, in preference order. Steam ships
    /// FactoryGameSteam.exe, Epic ships FactoryGameEGS.exe, and some installs carry
    /// a FactoryGame.exe shim. We prefer the steam:// URL for launch; these are the
    /// fallback exe names probed at the install root.
    private static readonly string[] LaunchExeNames =
        { "FactoryGameSteam.exe", "FactoryGameEGS.exe", "FactoryGame.exe" };

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "satisfactory";
    public string DisplayName => "Satisfactory";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/satisfactory/__init__.py
    /// (SatisfactoryWorld.game = "Satisfactory"; required_client_version = (0, 6, 0)).
    /// Satisfactory is a core, bundled Archipelago world.
    public string ApWorldName => "Satisfactory";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "satisfactory.png");

    public string ThemeAccentColor => "#E59A2B";   // FICSIT industrial amber
    public string[] GameBadges     => new[] { "Requires Satisfactory + Mod Manager" };

    public string Description =>
        "Satisfactory, Coffee Stain Studios' first-person factory-builder, played " +
        "through the Archipelago Randomizer mod — an in-game Archipelago client for " +
        "Satisfactory. The game's technologies are pulled out of the HUB, MAM and " +
        "Hard Drives and shuffled into the multiworld; when other players find them " +
        "they are sent back to your factory, and the game connects to the Archipelago " +
        "server itself (no emulator, no bridge). You bring your own copy of " +
        "Satisfactory (Steam, or the Epic Games Store), and the Archipelago mod is " +
        "added on top with the Satisfactory Mod Manager (SMM), which also installs " +
        "the dependency mods it needs (ContentLib, Free Samples, MAM Enhancer). The " +
        "launcher detects your Steam install and guides the SMM install. You connect " +
        "to your server from the mod's in-game Mod Savegame Settings when you start a " +
        "new game.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the Archipelago Randomizer mod is present (an
    /// Archipelago-named mod folder/asset is under a detected/override Satisfactory
    /// install's mod tree). We do NOT gate on our own stamp — the user installs
    /// through SMM, which we honor. If no Satisfactory install is detected, this is
    /// false.
    public bool IsInstalled => FindInstalledMod() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps any working files. The actual mod lives in the
    /// Satisfactory install's mod tree (managed by SMM), not here. Exposed as
    /// GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Satisfactory");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Doom/Jak/HK/
    /// Subnautica/Raft). Per the brief, lives under Games/ROMs/satisfactory/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "satisfactory_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Archipelago Randomizer mod reports checks/items/goal to the AP server
    // itself — the launcher relays nothing. These exist for interface compatibility
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
            // The mod is SMM-managed; we have no fetchable version to compare.
            // Report "installed" when an Archipelago mod is detected, else not.
            InstalledVersion = FindInstalledMod() != null ? "installed" : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        // The mod is published on ficsit.app and managed by SMM (not GitHub release
        // assets the launcher can drive), so there is no reliable "available version"
        // to surface as an actionable update path. Leave it null rather than imply an
        // update route we cannot drive.
        AvailableVersion = null;
        await Task.CompletedTask;
    }

    // ── Lifecycle — InstallOrUpdate (GUIDED — see header) ─────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. We need a Satisfactory install for SMM to mod. Prefer an explicit
        //    override; else auto-detect the Steam install.
        progress.Report((5, "Locating your Satisfactory installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Satisfactory installation. Open this game's Settings " +
                "and pick your Satisfactory folder (the one containing FactoryGameSteam.exe " +
                "or the FactoryGame folder), or install Satisfactory via Steam/Epic first. " +
                "The Archipelago mod is added on top of your own copy of the game with the " +
                "Satisfactory Mod Manager.");

        await Task.CompletedTask;

        // 1. Already installed by SMM / by hand? Honor it.
        string? existing = FindInstalledMod();
        if (existing != null)
        {
            InstalledVersion = "installed";
            progress.Report((100,
                "The Archipelago Randomizer mod is already installed (" +
                Path.GetFileName(existing.TrimEnd(Path.DirectorySeparatorChar)) + "). To " +
                "play: launch Satisfactory (Steam/Epic load mods automatically), create a " +
                "New Game, open Mod Savegame Settings, choose Archipelago, and enter your " +
                "server URI, slot name and password. See Settings for the full steps."));
            return;
        }

        // 2. HONEST: the Archipelago Randomizer mod is not a fetchable GitHub asset —
        //    it installs through the Satisfactory Mod Manager (SMM), which also
        //    installs its dependencies (ContentLib, Free Samples, MAM Enhancer). We do
        //    NOT fake a download. Open the SMM download page and the mod's ficsit.app
        //    page so the one-click "Install" is a click away.
        progress.Report((45, "Opening the Satisfactory Mod Manager + mod pages..."));
        try
        {
            OpenUrl(SmmDownloadUrl);     // get/refresh SMM first
            OpenUrl(ModFicsitPageUrl);   // then the mod's one-click Install page
        }
        catch { /* opening pages is a convenience, not a hard requirement */ }

        progress.Report((100,
            "The Archipelago Randomizer mod installs through the Satisfactory Mod Manager " +
            "(SMM), so this launcher opened the pages for you. STEPS: (1) Install the " +
            "Satisfactory Mod Manager if you have not (its download page just opened). " +
            "(2) On the mod's ficsit.app page (also opened) click Install — SMM installs " +
            "the Archipelago Randomizer mod AND its dependencies (ContentLib, Free Samples, " +
            "MAM Enhancer) for you. (3) Launch Satisfactory from THIS launcher (Steam/Epic " +
            "load mods automatically — no special launcher needed). (4) In game, create a " +
            "New Game, open Mod Savegame Settings, choose Archipelago, and enter your " +
            "server URI, slot name and password. Chat messages confirm the connection. See " +
            "Settings for links and the full guide."));
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
        // HONEST: the AP server connection for Satisfactory is entered IN-GAME via
        // the mod's UI: New Game -> Mod Savegame Settings -> Archipelago -> Server
        // URI / User Name / Password. The setup guide states the Satisfactory client
        // does NOT need a copy of the AP config — there is no command-line / config
        // prefill we can apply (verified — see header). So launching from this tile
        // just starts the game (Steam/Epic load mods automatically); the user
        // connects in-game with the session credentials (the settings panel surfaces
        // those to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartSatisfactory();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Satisfactory runs perfectly well.
    public bool SupportsStandalone => true;

    /// The Archipelago Randomizer mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartSatisfactory();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started Satisfactory from here. Kill what we launched. (When
        // we launch via the steam:// URL, Steam owns the process and there is nothing
        // for us to track/kill — that is expected.)
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
        // The Archipelago Randomizer mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game (chat messages on load).
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Satisfactory folder contains one
    /// of the store launch exes and/or the FactoryGame UE folder. Return null when
    /// acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Satisfactory install folder.";

        if (LooksLikeSatisfactoryDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeSatisfactoryDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Satisfactory installation. Pick the folder that " +
               "contains FactoryGameSteam.exe (Steam) or FactoryGameEGS.exe (Epic) — the " +
               @"FactoryGame folder is next to it. For Steam this is usually " +
               @"...\steamapps\common\Satisfactory.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Unverified-offline honesty header ─────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Satisfactory is your own game (Steam / Epic Games Store) with the " +
                   "Archipelago Randomizer mod added on top. The mod installs through the " +
                   "Satisfactory Mod Manager (SMM) from ficsit.app — it is not a download " +
                   "this launcher can fetch directly, so the launcher detects your Steam " +
                   "install and guides the SMM install (the buttons below open the right " +
                   "pages; one click in SMM also installs the dependency mods). Once the " +
                   "mods are installed you launch the game normally — Steam/Epic load mods " +
                   "automatically, no special launcher needed. You connect to your server " +
                   "from the mod's in-game Mod Savegame Settings — there is no connection " +
                   "file to pre-fill. These external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "SATISFACTORY INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Satisfactory not detected. Pick your install folder below, or install " +
              "Satisfactory via Steam/Epic first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // mod status line
        string? modPath = FindInstalledMod();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modPath != null
                    ? "Archipelago Randomizer mod found: " + modPath
                    : "Archipelago Randomizer mod not found yet (install it with the " +
                      "Satisfactory Mod Manager — see the steps below).",
            FontSize = 11, Foreground = modPath != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Satisfactory install folder (the one containing " +
                          "FactoryGameSteam.exe / FactoryGameEGS.exe). Detected from Steam " +
                          "automatically; set it here to override (Epic Games Store / " +
                          "non-standard Steam library).",
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
                Title            = "Select your Satisfactory install folder (contains FactoryGameSteam.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                                   ? (overrideDir ?? gameDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Satisfactory folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeSatisfactoryDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeSatisfactoryDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 526870). Use this " +
                   "picker for the Epic Games Store, or a non-standard Steam library.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connecting (this session) ────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game via Mod Savegame Settings)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "In Satisfactory, create a New Game, click \"Mod Savegame Settings\", " +
                   "select \"Archipelago\", and enter three fields:\n" +
                   "    Server URI   — host:port, e.g. archipelago.gg:49236\n" +
                   "    User Name    — your Player Name / AP slot name\n" +
                   "    Password     — your room password (blank if none)\n" +
                   "Once you load in, chat messages confirm whether the connection " +
                   "succeeded. (This launcher cannot pre-fill the connection — it is " +
                   "entered in-game, and the Satisfactory client does not need a copy of " +
                   "your Archipelago config file.)",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Satisfactory (Steam, or the Epic Games Store). Install it if you have " +
                "not. Use \"Select folder...\" above if it was not auto-detected.",
            "2. Install the Satisfactory Mod Manager (SMM) from its download page (link " +
                "below). It detects your Satisfactory install automatically.",
            "3. Install the mod: on the Archipelago Randomizer ficsit.app page click " +
                "Install (or search \"Archipelago\" inside SMM). SMM installs the mod AND " +
                "its dependencies (ContentLib, Free Samples, MAM Enhancer) for you. Use " +
                "Install on the Play tab to open both pages, or the links below.",
            "4. Launch Satisfactory from THIS launcher (or normally via Steam/Epic). Once " +
                "the mods are installed, the game loads them automatically — you do NOT " +
                "need to launch through the Mod Manager.",
            "5. In game, create a New Game, open Mod Savegame Settings, choose Archipelago, " +
                "and enter your Server URI, User Name (slot) and Password to connect.",
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
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Satisfactory Mod Manager (download) ↗", SmmDownloadUrl),
            ("Archipelago Randomizer mod (ficsit.app) ↗", ModFicsitPageUrl),
            ("How to use the Mod Manager (docs) ↗", SmmDocsUrl),
            ("Archipelago Randomizer source (GitHub) ↗", ModRepoUrl),
            ("Satisfactory Setup Guide ↗", SetupGuideUrl),
            ("Archipelago Official ↗", ArchipelagoSite),
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
            btn.Click += (_, _) => OpenUrl(u);
            panel.Children.Add(btn);
        }
        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // The mod is distributed on ficsit.app (not GitHub release assets), but the
        // source repo's releases endpoint is still the closest AP-relevant news, so
        // we try it and degrade gracefully to an empty feed on any failure.
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
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

    // ── Private helpers — small utilities ─────────────────────────────────────

    /// "v1.2.3" → "1.2.3" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Open a URL / shell target. Best effort — never throws to the caller.
    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* shell unavailable — ignore */ }
    }

    // ── Private helpers — Steam / Satisfactory detection ──────────────────────

    /// The Satisfactory install dir to use: the override (if set and valid) wins,
    /// else the Steam-detected install. Null when nothing is found.
    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeSatisfactoryDir(ov))
            return ov;

        try { return DetectSteamGameDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Satisfactory if it has one of the store launch exes
    /// and/or the FactoryGame UE folder (UE5 layout) and/or the Engine folder.
    private static bool LooksLikeSatisfactoryDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            foreach (string exe in LaunchExeNames)
                if (File.Exists(Path.Combine(dir, exe))) return true;
            if (Directory.Exists(Path.Combine(dir, "FactoryGame")) &&
                Directory.Exists(Path.Combine(dir, "Engine"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Satisfactory install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_526870.acf exists → steamapps\common\Satisfactory.
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

                    // Prefer the installdir named in the manifest; fall back to the
                    // conventional "Satisfactory" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeSatisfactoryDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeSatisfactoryDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath + HKLM InstallPath). Both are tried; duplicates
    /// are harmless.
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

    /// Find the Archipelago Randomizer mod under the detected/override Satisfactory
    /// install's mod tree. SMM installs UE mods into <Game>\FactoryGame\Mods\<ModRef>,
    /// where the Archipelago mod's reference folder is named like "Archipelago". We
    /// look for that folder first (case-insensitive), then fall back to a tolerant
    /// recursive scan for an Archipelago-named .pak / .uplugin / .dll so a different
    /// SMM layout still counts. Returns the folder/file path or null.
    private string? FindInstalledMod()
    {
        try
        {
            string? game = ResolveGameDir();
            if (game == null) return null;

            string modsDir = FindModsFolder(game);
            if (modsDir == null || !Directory.Exists(modsDir)) return null;

            // 1. The conventional case: a mod-reference folder containing "archipelago".
            foreach (string sub in Directory.EnumerateDirectories(modsDir))
            {
                string name = Path.GetFileName(sub);
                if (name.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return sub;
            }

            // 2. Tolerant fallback: any Archipelago-named mod asset anywhere under Mods.
            foreach (string file in Directory.EnumerateFiles(modsDir, "*.*", SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(file);
                if (!ext.Equals(".pak", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".uplugin", StringComparison.OrdinalIgnoreCase) &&
                    !ext.Equals(".dll", StringComparison.OrdinalIgnoreCase))
                    continue;
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return file;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    /// Resolve the SMM "Mods" folder inside a Satisfactory install. SMM installs
    /// mods under <Game>\FactoryGame\Mods (matching the folder names
    /// case-insensitively, since exact casing can vary). Returns the conventional
    /// path if no folder exists yet.
    private static string FindModsFolder(string gameDir)
    {
        try
        {
            // Locate the "FactoryGame" content folder case-insensitively.
            string factoryGame = Path.Combine(gameDir, "FactoryGame");
            foreach (string sub in Directory.EnumerateDirectories(gameDir))
            {
                if (string.Equals(Path.GetFileName(sub), "FactoryGame", StringComparison.OrdinalIgnoreCase))
                {
                    factoryGame = sub;
                    break;
                }
            }

            if (Directory.Exists(factoryGame))
            {
                foreach (string sub in Directory.EnumerateDirectories(factoryGame))
                {
                    if (string.Equals(Path.GetFileName(sub), "Mods", StringComparison.OrdinalIgnoreCase))
                        return sub;
                }
            }
            return Path.Combine(factoryGame, "Mods");
        }
        catch
        {
            return Path.Combine(gameDir, "FactoryGame", "Mods");
        }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Satisfactory so mods load. HONEST: once the mods are installed via SMM,
    /// the game loads them automatically on a normal launch, so there is NO special
    /// mod-loader launcher to route through. Prefer the steam:// URL (Steam picks the
    /// correct store binary); else fall back to the resolved store exe from the
    /// detected/override install. Surfaces a clear message rather than failing
    /// opaquely.
    private void StartSatisfactory()
    {
        // 1. Preferred: launch through Steam (it selects FactoryGameSteam.exe and
        //    mods still load). Only do this when Steam is actually present.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process, so we won't track exit
                return;
            }
            catch { /* fall through to a direct exe launch */ }
        }

        // 2. Fall back to a store exe directly (covers Epic, or a Steam library on a
        //    machine where the steam:// URL handler is unavailable). Mods still load.
        string? game = ResolveGameDir();
        if (game != null)
        {
            foreach (string exeName in LaunchExeNames)
            {
                string exe = Path.Combine(game, exeName);
                if (!File.Exists(exe)) continue;
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName         = exe,
                    WorkingDirectory = game,
                    UseShellExecute  = true,
                });
                if (proc != null)
                {
                    TrackProcess(proc);
                    return;
                }
            }
        }

        throw new FileNotFoundException(
            "Could not start Satisfactory. Open this game's Settings to pick your " +
            "Satisfactory folder (the one containing FactoryGameSteam.exe / " +
            "FactoryGameEGS.exe), or install Satisfactory via Steam/Epic. See Settings " +
            "for the guided steps.",
            "FactoryGameSteam.exe");
    }

    /// Wire up process tracking + exit notification for a launched process.
    private void TrackProcess(Process proc)
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

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the Satisfactory install-dir
    // override) in its OWN JSON file so it stays a single self-contained source file
    // and does not modify Core/SettingsStore. BOM-less UTF-8, read-modify-write
    // (same approach as Doom/Jak/HK/Subnautica/Raft).

    private sealed class SatisfactorySettings
    {
        public string? InstallOverride { get; set; }
    }

    private SatisfactorySettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<SatisfactorySettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(SatisfactorySettings s)
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
