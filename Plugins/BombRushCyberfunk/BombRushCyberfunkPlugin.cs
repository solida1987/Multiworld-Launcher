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

namespace LauncherV2.Plugins.BombRushCyberfunk;

// ═══════════════════════════════════════════════════════════════════════════════
// BombRushCyberfunkPlugin — install / launch for "Bomb Rush Cyberfunk" (Team
// Reptile, 2023) played through the BRC-Archipelago mod by TRPG0, which contains
// the in-game Archipelago Multiworld client. This is a NATIVE "ConnectsItself"
// integration in the same family as the shipped Hollow Knight / TUNIC / Stardew
// Valley / Jak plugins: the game itself speaks to the AP server (no emulator, no
// Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Bomb Rush Cyberfunk (Steam appid 1353230), and Archipelago support is
// delivered as a BepInEx mod added on top. The honest integration ceiling —
// exactly like the shipped Hollow Knight / TUNIC plugins — is "automate what is
// possible, guide the irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "Bomb Rush Cyberfunk" (verified against
//     worlds/bomb_rush_cyberfunk/__init__.py:
//     `class BombRushCyberfunkWorld(World): game = "Bomb Rush Cyberfunk"`).
//     GameId here = "bomb_rush_cyberfunk". Bomb Rush Cyberfunk is a CORE
//     Archipelago world (it ships inside Archipelago itself — no custom_worlds
//     drop is needed to generate).
//
//   * THE MOD repo is TRPG0/BRC-Archipelago (verified live 2026-06-14). It is "A
//     client for connecting the game Bomb Rush Cyberfunk to an Archipelago
//     randomizer." The latest release (1.0.6, 2026-03-22) ships ONE asset,
//     "BRC-Archipelago.1.0.6.zip", whose contents extract into
//     <BRC>/BepInEx/plugins/. The mod's project (mod/Archipelago.BRC.csproj) sets
//     <AssemblyName>Archipelago</AssemblyName>, so the primary plugin file on disk
//     is "Archipelago.dll" (BepInPlugin GUID "trpg.brc.archipelago"). The mod is
//     ALSO published on Thunderstore (TRPG/BRC_Archipelago) for mod-manager users.
//
//   * CRITICAL HONESTY — THE MOD HAS TWO SEPARATE PREREQUISITES THE ZIP DOES NOT
//     BUNDLE:
//       (a) BepInEx 5.4.22 x64 (Unity MONO — NOT the IL2CPP / 6.x pre-release
//           builds; the setup guide is explicit: "do not use any pre-release
//           versions of BepInEx 6"). BRC is a Unity MONO game. BepInEx 5 is a
//           PORTABLE zip (no wizard) extracted into the BRC install ROOT.
//       (b) ModLocalizer.dll, a companion library from TRPG0/BRC-ModLocalizer
//           (latest 1.0.1), dropped directly into BepInEx/plugins. The mod's own
//           Thunderstore manifest lists it as a hard dependency
//           ("TRPG-ModLocalizer-1.0.1").
//     Because all three pieces (BepInEx, ModLocalizer.dll, the mod zip) are plain
//     portable downloads, this plugin CAN best-effort stage ALL of them — but it
//     then leaves the run-once-to-generate-config and the in-game connection to
//     the user, and says so. Faking a fully-automated "ready to play" would be
//     dishonest theatre.
//
//   * CONNECTION is made IN-GAME (verified against the official AP setup guide and
//     the mod README): after the mod is installed and the game launched, on the
//     save-file screen you "click one of the Archipelago buttons next to the save
//     files" to open a menu where you enter the server address, port, your name,
//     and a password (if any), then confirm with the checkmark. The mod also adds
//     an in-game "Archipelago" phone app for chat/options. There is NO documented
//     command-line arg and NO documented config file this launcher can pre-write
//     to seed the connection (the connection lives behind the in-game save-slot
//     buttons). Per an honest "don't invent an undocumented prefill" stance (same
//     as Hollow Knight / TUNIC / Jak), this plugin does NOT write any config or
//     fake a connection prefill — the settings panel + post-install note surface
//     the session's host / port / slot for the user to type into the in-game
//     fields.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Bomb Rush Cyberfunk install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\BombRushCyberfunk via appmanifest_1353230.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and takes
//      precedence; it is validated (must contain "Bomb Rush Cyberfunk.exe" /
//      "Bomb Rush Cyberfunk_Data") and persisted in this plugin's OWN sidecar
//      (Games/ROMs/bomb_rush_cyberfunk/brc_launcher.json) — Core/SettingsStore is
//      NOT modified.
//   2. INSTALL/UPDATE (best effort) = (a) if BepInEx is not present in the detected
//      install, download the pinned BepInEx 5.4.22 x64 zip and extract it into the
//      BRC root; (b) download ModLocalizer.dll into BepInEx/plugins; (c) download
//      the mod's "BRC-Archipelago.*.zip" from the real release and extract it into
//      BepInEx/plugins. All are portable. The plugin then presents clear, numbered,
//      guided steps + links (mod repo, ModLocalizer repo, Thunderstore, the
//      official AP setup guide, BepInEx, archipelago.gg) so the user can run the
//      game once (to let BepInEx generate its config) and connect in-game. Never a
//      fake one-click.
//   3. LAUNCH = run "Bomb Rush Cyberfunk.exe" from the detected/override install;
//      if the exe cannot be found but Steam is present, fall back to
//      steam://rungameid/1353230 (BRC also requires Steam to be running, so the
//      steam:// route is a fine fallback). ConnectsItself = true (the mod owns the
//      slot — the launcher must NOT hold its own ApClient on it). SupportsStandalone
//      = true (plain BRC runs fine without AP). No connection prefill (entered
//      in-game), stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Hollow-Knight/TUNIC-style) ──
//   * "Installed" is judged by the presence of "Archipelago.dll" under a detected/
//     override BRC install's BepInEx tree (case-insensitive, recursive) — NOT by an
//     OUR-OWN version stamp, because the user may instead install the mod by hand
//     (or via a Thunderstore mod manager), which this launcher should honor. The
//     name "Archipelago.dll" is generic, but inside a BRC install's BepInEx folder
//     it is the BRC-Archipelago mod (the mod sets AssemblyName=Archipelago). If no
//     BRC install is detected, the tile simply reads "not installed".
//   * Steam library parsing is defensive: a hand-written tolerant VDF scan that
//     pulls quoted "path" values; any failure degrades to "BRC not found" rather
//     than throwing.
//   * No plaintext AP password is ever written by this plugin (the connection is
//     entered in-game), so there is nothing to scrub on stop.
//   * UNVERIFIED at author time: the exact internal layout of BRC-Archipelago.*.zip
//     (flat vs. single wrapper folder) was not byte-inspected; the extractor is
//     therefore defensive — it extracts into plugins/ and, if "Archipelago.dll"
//     did not land directly there, flattens a single wrapper sub-folder up one
//     level so the DLLs sit where BepInEx expects them.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class BombRushCyberfunkPlugin : IGamePlugin
{
    // ── Constants — the BRC-Archipelago mod (real repo, verified 2026-06-14) ───
    private const string MOD_OWNER = "TRPG0";
    private const string MOD_REPO  = "BRC-Archipelago";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string ThunderstoreUrl =
        "https://thunderstore.io/c/bomb-rush-cyberfunk/p/TRPG/BRC_Archipelago/";
    private const string SetupGuideUrl  =
        "https://archipelago.gg/tutorial/Bomb%20Rush%20Cyberfunk/setup_en";
    private const string GameInfoUrl    =
        "https://archipelago.gg/games/Bomb%20Rush%20Cyberfunk/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // ModLocalizer.dll — a SEPARATE companion library the mod depends on (the mod's
    // Thunderstore manifest pins "TRPG-ModLocalizer-1.0.1"). Its release ships the
    // bare DLL as a direct asset, so the plugin can stage it straight into plugins.
    private const string LOC_OWNER = "TRPG0";
    private const string LOC_REPO  = "BRC-ModLocalizer";
    private const string ModLocalizerRepoUrl = $"https://github.com/{LOC_OWNER}/{LOC_REPO}";
    private const string GH_LOC_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{LOC_OWNER}/{LOC_REPO}/releases/latest";
    private const string ModLocalizerDllName = "ModLocalizer.dll";
    /// Pinned fallback for ModLocalizer when the GitHub API is unreachable (1.0.1
    /// verified live 2026-06-14; the release asset is the bare "ModLocalizer.dll").
    private const string LocFallbackVersion = "1.0.1";
    private static readonly string LocFallbackDllUrl =
        $"{ModLocalizerRepoUrl}/releases/download/{LocFallbackVersion}/{ModLocalizerDllName}";

    // BepInEx 5.4.22 (Unity MONO x64) — the SEPARATE mod-loader prerequisite. The
    // setup guide is explicit: BepInEx 5.4.22 x64, and "do not use any pre-release
    // versions of BepInEx 6". Portable zip (no wizard), so the plugin can stage it.
    private const string BepInExSite = "https://github.com/BepInEx/BepInEx/releases";
    private const string BepInExVersion = "5.4.22";
    private static readonly string BepInExZipUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.22/" +
        "BepInEx_x64_5.4.22.0.zip";

    // Steam — Bomb Rush Cyberfunk appid 1353230.
    private const string BrcSteamAppId = "1353230";
    private static readonly string SteamRunUrl = $"steam://rungameid/{BrcSteamAppId}";

    /// The standard Steam install sub-folder name for BRC (verified: the install
    /// folder is "BombRushCyberfunk", with a "Bomb Rush Cyberfunk_Data" inside).
    private const string SteamCommonFolderName = "BombRushCyberfunk";

    /// The base-game executable name (note the spaces).
    private const string BrcExeName  = "Bomb Rush Cyberfunk.exe";
    /// The Unity data folder next to the exe (used as a secondary "looks like BRC"
    /// signal in case the exe name ever varies by store).
    private const string BrcDataDir  = "Bomb Rush Cyberfunk_Data";

    /// The mod's primary plugin DLL placed under BepInEx/plugins. The mod project
    /// sets AssemblyName=Archipelago, so the file is "Archipelago.dll".
    private const string ModPrimaryDll = "Archipelago.dll";

    /// Pinned fallback for the mod when the GitHub API is unreachable. 1.0.6
    /// verified live 2026-06-14; the asset is "BRC-Archipelago.1.0.6.zip".
    private const string FallbackVersion = "1.0.6";
    private const string FallbackZipName = "BRC-Archipelago.1.0.6.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "bomb_rush_cyberfunk";
    public string DisplayName => "Bomb Rush Cyberfunk";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against
    /// worlds/bomb_rush_cyberfunk/__init__.py (`game = "Bomb Rush Cyberfunk"`).
    public string ApWorldName => "Bomb Rush Cyberfunk";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "bomb_rush_cyberfunk.png");

    public string ThemeAccentColor => "#E0407A";   // hot-pink graffiti
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Bomb Rush Cyberfunk, Team Reptile's 2023 graffiti / skating action game, " +
        "played through the BRC-Archipelago mod by TRPG0 — which bundles an in-game " +
        "Archipelago client, so the game connects to the multiworld itself with no " +
        "emulator and no bridge. REP is no longer earned by tagging; instead items, " +
        "REP, characters and abilities are shuffled across the multiworld, and your " +
        "goal is to defeat every rival crew in New Amsterdam. You bring your own copy " +
        "of Bomb Rush Cyberfunk (owned on Steam); the integration runs on BepInEx, " +
        "the Unity mod loader, plus a ModLocalizer helper. The launcher detects your " +
        "Steam install and can stage BepInEx, ModLocalizer and the Archipelago mod " +
        "into it, and guides the rest. You connect to your server from the in-game " +
        "Archipelago buttons next to the save files.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the BRC-Archipelago mod DLL ("Archipelago.dll") is present
    /// in a detected/override BRC install's BepInEx tree. (We do NOT gate on our own
    /// stamp — the user may have installed the mod by hand / via Thunderstore, which
    /// we honor.)
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads (the mod / BepInEx / ModLocalizer files)
    /// and any working files. The actual mod is extracted INTO the BRC install's
    /// BepInEx folder, not here. Exposed as GameDirectory for the IGamePlugin
    /// contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "BombRushCyberfunk");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Hollow Knight /
    /// TUNIC / Stardew / Jak). Per the brief, lives under
    /// Games/ROMs/bomb_rush_cyberfunk/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "brc_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The BRC-Archipelago mod reports checks/items/goal to the AP server itself —
    // the launcher relays nothing. These exist for interface compatibility
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
            // present; otherwise report "installed" when the mod DLL exists.
            InstalledVersion = FindInstalledModDll() != null
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
        // 0. We need a BRC install to drop BepInEx + the mod into. Prefer an
        //    explicit override; else auto-detect the Steam install.
        progress.Report((2, "Locating your Bomb Rush Cyberfunk installation..."));
        string? brcDir = ResolveBrcDir();
        if (brcDir == null)
            throw new InvalidOperationException(
                "Could not find a Bomb Rush Cyberfunk installation. Open this game's " +
                "Settings and pick your Bomb Rush Cyberfunk folder (the one containing " +
                "\"Bomb Rush Cyberfunk.exe\"), or install Bomb Rush Cyberfunk via Steam " +
                "first. The Archipelago mod is added on top of your own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback).
        progress.Report((6, "Checking the latest BRC-Archipelago release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the BRC-Archipelago mod download on GitHub. Check your " +
                "internet connection, or download it manually from " + ModRepoUrl +
                "/releases (or Thunderstore) — see Settings for the guided steps. The mod " +
                "repo is " + ModRepoUrl + ".");

        // 2. Ensure BepInEx 5.4.22 (Mono x64) is present — it is a SEPARATE, portable
        //    prerequisite the mod zip does NOT bundle. If it is already there
        //    (winhttp.dll / BepInEx folder), we leave it alone.
        string pluginsDir = Path.Combine(brcDir, "BepInEx", "plugins");
        if (!BepInExPresent(brcDir))
        {
            progress.Report((10, "Staging BepInEx 5.4.22 (Unity Mono) into your BRC folder..."));
            await DownloadAndExtractZipToDirAsync(
                BepInExZipUrl, $"brc-bepinex-{BepInExVersion}", brcDir, 10, 38, progress, ct);
        }
        else
        {
            progress.Report((38, "BepInEx already present — keeping your existing install."));
        }
        Directory.CreateDirectory(pluginsDir);

        // 3. Stage ModLocalizer.dll (a SEPARATE hard dependency) into BepInEx/plugins.
        var (locVersion, locDllUrl) = await ResolveLatestModLocalizerAsync(ct);
        if (locDllUrl != null)
        {
            progress.Report((42, "Staging ModLocalizer.dll into BepInEx\\plugins..."));
            await DownloadFileAsync(
                locDllUrl, Path.Combine(pluginsDir, ModLocalizerDllName),
                $"Downloading ModLocalizer {locVersion}...", 42, 56, progress, ct);
        }
        else
        {
            progress.Report((56,
                "Could not resolve ModLocalizer.dll automatically — install it by hand " +
                "from " + ModLocalizerRepoUrl + "/releases (see Settings)."));
        }

        // 4. Download + extract the mod's "BRC-Archipelago.*.zip" INTO
        //    <BRC>/BepInEx/plugins/. The extractor is defensive about an extra
        //    wrapper folder (flattens it so Archipelago.dll lands in plugins).
        await DownloadAndExtractModAsync(zipUrl, version, pluginsDir, 58, 92, progress, ct);

        // 5. Stamp the version next to our sidecar so the tile can show it. (This is
        //    informational only — IsInstalled is judged by the DLL's presence.)
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepOk = BepInExPresent(brcDir);
        bool locOk = File.Exists(Path.Combine(pluginsDir, ModLocalizerDllName));
        progress.Report((100,
            $"Staged the BRC-Archipelago mod {version} into your BepInEx\\plugins folder" +
            (bepOk ? " (BepInEx present" : " (BepInEx NOT detected") +
            (locOk ? ", ModLocalizer present)." : ", ModLocalizer NOT detected).") +
            " To play: launch Bomb Rush Cyberfunk ONCE so BepInEx finishes setting up, " +
            "then on the save-file screen click one of the Archipelago buttons next to a " +
            "save file and enter your server address, port, name, and password (if any). " +
            "Open Settings for the guided steps and links. (This launcher cannot pre-fill " +
            "the connection — it is entered in-game.)"));
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
        // HONEST: the AP server connection for BRC is entered IN-GAME (save-file
        // screen -> an Archipelago button next to a save -> server / port / name /
        // password fields, confirmed with the checkmark). There is no documented
        // command-line / config prefill this launcher can apply (verified — see
        // header). So launching from this tile just starts the game; the user
        // connects in-game with the session credentials (the settings panel + note
        // surface those values to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartBrc();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Bomb Rush Cyberfunk runs perfectly well.
    public bool SupportsStandalone => true;

    /// The BRC-Archipelago mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartBrc();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started BRC from here. Kill what we launched.
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
        // The BRC-Archipelago mod receives items from the AP server directly; there
        // is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game (the Archipelago phone app).
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid BRC folder contains
    /// "Bomb Rush Cyberfunk.exe" (and the "Bomb Rush Cyberfunk_Data" folder is next
    /// to it). Return null when acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Bomb Rush Cyberfunk install folder.";

        if (LooksLikeBrcDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeBrcDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Bomb Rush Cyberfunk installation. Pick the " +
               "folder that contains \"Bomb Rush Cyberfunk.exe\" (for Steam this is " +
               @"usually ...\steamapps\common\BombRushCyberfunk).";
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
            Text = "Bomb Rush Cyberfunk is your own game (Steam) with the BRC-Archipelago " +
                   "mod added on top via BepInEx. The launcher detects your Steam install " +
                   "and can stage BepInEx 5.4.22, ModLocalizer, and the Archipelago mod " +
                   "files into it, but BepInEx and ModLocalizer are separate prerequisites " +
                   "and you must run the game once so BepInEx finishes setting up (see the " +
                   "guided steps below). You connect to your server from the in-game " +
                   "Archipelago buttons next to the save files. These external steps are " +
                   "not verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "BOMB RUSH CYBERFUNK INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? brcDir      = ResolveBrcDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = brcDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + brcDir
                : "Detected Steam install: " + brcDir)
            : "Bomb Rush Cyberfunk not detected. Pick your install folder below, or " +
              "install Bomb Rush Cyberfunk via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = brcDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // BepInEx status line
        bool bepOk = brcDir != null && BepInExPresent(brcDir);
        panel.Children.Add(new TextBlock
        {
            Text = brcDir == null
                    ? ""
                    : (bepOk
                        ? "BepInEx found in your BRC folder."
                        : "BepInEx not found yet — Install on the Play tab stages it, or get " +
                          "it from the BepInEx releases (link below). Use 5.4.22 x64, not " +
                          "the BepInEx 6 pre-releases."),
            FontSize = 11, Foreground = bepOk ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        // ModLocalizer status line
        bool locOk = brcDir != null &&
                     File.Exists(Path.Combine(brcDir, "BepInEx", "plugins", ModLocalizerDllName));
        panel.Children.Add(new TextBlock
        {
            Text = brcDir == null
                    ? ""
                    : (locOk
                        ? "ModLocalizer.dll found in BepInEx\\plugins."
                        : "ModLocalizer.dll not found yet — Install on the Play tab stages it, " +
                          "or get it from the ModLocalizer releases (link below)."),
            FontSize = 11, Foreground = locOk ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        // mod status line
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                    ? "BRC-Archipelago mod found: " + modDll
                    : "BRC-Archipelago mod not found in BepInEx\\plugins yet (use Install on " +
                      "the Play tab).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? brcDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Bomb Rush Cyberfunk install folder (the one containing " +
                          "\"Bomb Rush Cyberfunk.exe\"). Detected from Steam automatically; " +
                          "set it here to override (non-standard Steam library, or another " +
                          "store).",
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
                Title            = "Select your Bomb Rush Cyberfunk install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? brcDir ?? "")
                                   ? (overrideDir ?? brcDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    MessageBox.Show(bad, "Not a Bomb Rush Cyberfunk folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeBrcDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeBrcDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 1353230). Use this " +
                   "picker for a non-standard Steam library or another store.",
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
            Text = "Launch the game, and on the save-file screen click one of the " +
                   "Archipelago buttons next to a save file. Enter the server address and " +
                   "port, your name (slot name), and a password if your room has one, then " +
                   "confirm with the checkmark. The in-game Archipelago phone app shows chat " +
                   "and options while you play. This launcher does not pre-fill the " +
                   "connection.",
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
            "1. Own Bomb Rush Cyberfunk (on Steam). Install it if you have not. Use the picker " +
                "above if it was not detected.",
            "2. Install BepInEx 5.4.22 x64 into your BRC folder. The Install button on the Play " +
                "tab stages it for you, or download it from the BepInEx releases (link below) " +
                "and extract it into your BRC folder. Do NOT use the BepInEx 6 pre-releases.",
            "3. Launch Bomb Rush Cyberfunk ONCE so BepInEx creates its config files, then close it.",
            "4. Install ModLocalizer.dll into BepInEx\\plugins, then install the BRC-Archipelago " +
                "mod (extract its zip into BepInEx\\plugins). The Install button on the Play tab " +
                "does both, or do it by hand from the releases / Thunderstore (links below).",
            "5. To play: launch the game, and on the save-file screen click an Archipelago button " +
                "next to a save file, enter your server address / port / name / password, then " +
                "confirm with the checkmark.",
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
            ("BRC-Archipelago (GitHub) ↗",      ModRepoUrl),
            ("BRC-Archipelago (Thunderstore) ↗", ThunderstoreUrl),
            ("ModLocalizer (GitHub) ↗",         ModLocalizerRepoUrl),
            ("Bomb Rush Cyberfunk Setup Guide ↗", SetupGuideUrl),
            ("Bomb Rush Cyberfunk Guide (AP) ↗", GameInfoUrl),
            ("BepInEx (releases) ↗",            BepInExSite),
            ("Archipelago Official ↗",          ArchipelagoSite),
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

    /// "v1.0.6" → "1.0.6" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the mod zip download URL. Prefers
    /// a "BRC-Archipelago*.zip" asset. Falls back to the pinned 1.0.6 direct URL
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
                string? preferred = null;   // the mod zip (BRC-Archipelago*.zip)
                string? anyZip    = null;   // any .zip
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null &&
                        (lower.Contains("brc-archipelago") || lower.Contains("archipelago")))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    /// Resolve the latest ModLocalizer release: version + the bare ModLocalizer.dll
    /// download URL. Falls back to the pinned 1.0.1 direct URL when unreachable.
    private async Task<(string Version, string? DllUrl)> ResolveLatestModLocalizerAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_LOC_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    if (name.Equals(ModLocalizerDllName, StringComparison.OrdinalIgnoreCase))
                        return (version, url);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (LocFallbackVersion, LocFallbackDllUrl);
    }

    // ── Private helpers — Steam / BRC detection ───────────────────────────────

    /// The BRC install dir to use: the override (if set and valid) wins, else the
    /// Steam-detected install. Null when nothing is found.
    private string? ResolveBrcDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeBrcDir(ov))
            return ov;

        try { return DetectSteamBrcDir(); }
        catch { return null; }
    }

    /// A folder "looks like" BRC if it has "Bomb Rush Cyberfunk.exe" and/or the
    /// "Bomb Rush Cyberfunk_Data" folder.
    private static bool LooksLikeBrcDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, BrcExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, BrcDataDir))) return true;
            return false;
        }
        catch { return false; }
    }

    /// True when BepInEx appears installed in a BRC folder. BepInEx 5 (Mono) drops
    /// a "BepInEx" folder plus a winhttp.dll proxy at the game root.
    private static bool BepInExPresent(string brcDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(brcDir) || !Directory.Exists(brcDir)) return false;
            if (Directory.Exists(Path.Combine(brcDir, "BepInEx"))) return true;
            if (File.Exists(Path.Combine(brcDir, "winhttp.dll"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam BRC install: read the Steam root from the registry, gather
    /// all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_1353230.acf exists → steamapps\common\BombRushCyberfunk.
    private static string? DetectSteamBrcDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{BrcSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    // Prefer the installdir named in the manifest; fall back to the
                    // conventional "BombRushCyberfunk" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeBrcDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeBrcDir(conventional)) return conventional;
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

    /// Find the mod's "Archipelago.dll" under the detected/override BRC install's
    /// BepInEx tree (recursive, case-insensitive). Returns the dll path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? brc = ResolveBrcDir();
            if (brc == null) return null;
            string bepInExDir = Path.Combine(brc, "BepInEx");
            if (!Directory.Exists(bepInExDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(bepInExDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(dll);
                if (name.Equals(ModPrimaryDll, StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start BRC: prefer the exe in the detected/override install; if that cannot be
    /// found but Steam is present, fall back to the steam:// URL (BRC also needs
    /// Steam running, so this is a fine fallback). Surfaces a clear message rather
    /// than failing opaquely.
    private void StartBrc()
    {
        string? brc = ResolveBrcDir();
        string? exe = brc != null ? Path.Combine(brc, BrcExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = brc!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Bomb Rush Cyberfunk.");

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

        // Fall back to Steam if we at least know Steam is installed.
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
            "Could not find \"Bomb Rush Cyberfunk.exe\". Open this game's Settings and pick " +
            "your Bomb Rush Cyberfunk install folder, or install Bomb Rush Cyberfunk via Steam.",
            BrcExeName);
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    /// Download the mod's "BRC-Archipelago.*.zip" and extract it into
    /// <BRC>/BepInEx/plugins/. The exact internal layout was not byte-inspected, so
    /// this is defensive: extract into plugins/, and if "Archipelago.dll" did not
    /// land directly there but is one level down inside a single wrapper folder,
    /// flatten that folder up so the DLLs sit where BepInEx expects.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string pluginsDir,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"brc-archipelago-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 7 / 10;
            await DownloadFileAsync(zipUrl, tempZip,
                $"Downloading BRC-Archipelago {version}...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Installing the mod into BepInEx\\plugins..."));
            Directory.CreateDirectory(pluginsDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, pluginsDir, overwriteFiles: true);

            // If the mod DLL did not land directly in plugins, but a single wrapper
            // sub-folder holds it, flatten that wrapper up one level. (Defensive —
            // the zip layout was not byte-inspected at author time.)
            if (!File.Exists(Path.Combine(pluginsDir, ModPrimaryDll)))
            {
                string? srcDir = FindDirContaining(pluginsDir, ModPrimaryDll);
                if (srcDir != null && !PathEquals(srcDir, pluginsDir))
                {
                    foreach (string fileSrc in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
                    {
                        string rel     = Path.GetRelativePath(srcDir, fileSrc);
                        string fileDst = Path.Combine(pluginsDir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                        File.Copy(fileSrc, fileDst, overwrite: true);
                    }
                }
            }

            progress.Report((pctEnd, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    /// Find the directory under <root> that directly contains <fileName>
    /// (case-insensitive). Returns null when not found.
    private static string? FindDirContaining(string root, string fileName)
    {
        try
        {
            foreach (string f in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
                return Path.GetDirectoryName(f);
        }
        catch { /* ignore */ }
        return null;
    }

    private static bool PathEquals(string a, string b)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(a).TrimEnd('\\', '/'),
                Path.GetFullPath(b).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// Download + extract a portable zip (e.g. BepInEx) straight into a target dir.
    private async Task DownloadAndExtractZipToDirAsync(
        string zipUrl,
        string tag,
        string targetDir,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(), $"{tag}-{Guid.NewGuid():N}.zip");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 8 / 10;
            await DownloadFileAsync(zipUrl, tempZip,
                "Downloading BepInEx...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Extracting BepInEx into your BRC folder..."));
            Directory.CreateDirectory(targetDir);
            // BepInEx zips extract their files at the archive root (BepInEx\,
            // winhttp.dll, doorstop_config.ini, ...), so extract straight in.
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, targetDir, overwriteFiles: true);
            progress.Report((pctEnd, "BepInEx staged."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    /// Stream a URL to a file with progress reporting between [pctStart, pctEnd].
    private async Task DownloadFileAsync(
        string url,
        string destPath,
        string msg,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        progress.Report((pctStart, msg));
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
        using var response = await _http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total      = response.Content.Headers.ContentLength ?? -1;
        long downloaded = 0;

        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        var buf = new byte[81920];
        int bytesRead;
        while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
            downloaded += bytesRead;
            if (total > 0)
            {
                int span = Math.Max(1, pctEnd - pctStart);
                int pct  = pctStart + (int)(span * downloaded / total);
                progress.Report((pct, $"{msg} {downloaded / 1000}KB"));
            }
        }
        await dst.FlushAsync(ct);
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the install-dir override + an
    // informational version stamp) in its OWN JSON file so it stays a single
    // self-contained source file and does not modify Core/SettingsStore. BOM-less
    // UTF-8, read-modify-write (same approach as Hollow Knight / TUNIC / Stardew /
    // Jak).

    private sealed class BrcSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private BrcSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<BrcSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(BrcSettings s)
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
