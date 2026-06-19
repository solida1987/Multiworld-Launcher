using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using LauncherV2.Core;

// NOTE on type qualification (BUILD GOTCHA — CS0104):
// This project sets BOTH <UseWPF>true</UseWPF> and <UseWindowsForms>true</UseWindowsForms>.
// That makes a long list of simple type names ambiguous between WPF and WinForms
// (Clipboard, MessageBox, Application, Color, Brush(es), Button, TextBox, CheckBox,
// Orientation, FontWeights, HorizontalAlignment, Cursors, Thickness, OpenFileDialog…).
// To avoid CS0104 this file deliberately does NOT do `using System.Windows.Controls;` /
// `using System.Windows.Media;` — every WPF UI type below is written FULLY QUALIFIED
// (System.Windows.Controls.*, System.Windows.Media.*, System.Windows.MessageBox, …).
// It also does NOT declare any file-level `using X = System.Windows...;` alias
// (CS1537 — GlobalUsings.cs already aliases the short names; a local alias would
// conflict). Bare names from GlobalUsings, or full qualification, only.
// (OpenFolderDialog is unambiguous — it lives only in Microsoft.Win32 — so it is
// referenced by its short name with `using Microsoft.Win32;` above.)

namespace LauncherV2.Plugins.Civ6;

// ═══════════════════════════════════════════════════════════════════════════════
// Civ6Plugin — detect / stage-mod / guide / launch for "Sid Meier's Civilization VI"
// (Firaxis / 2K, 2016) played through its OFFICIAL Archipelago integration. This is a
// NATIVE "ConnectsItself" integration in the same family as the shipped Wargroove,
// Undertale and Jak-and-Daxter plugins: a SEPARATE bundled Archipelago client (the
// "Civ6 Client") owns the AP slot and relays to the running game — the launcher must
// NOT hold its own ApClient on the slot, and there is no in-game / command-line
// connection prefill the launcher can apply (the player types their slot name into the
// Civ6 Client).
//
// ── HONEST REALITY CHECK (2026-06-14) — verified against the AP-main civ_6 world ──
// The full worlds/civ_6 source is vendored in this repo (mk64src/worlds/civ_6) and was
// read directly. The pieces are:
//
//   * THE BASE GAME is the user's own legally-owned Civilization VI (Steam appid
//     289070, Firaxis/2K; the Epic version also works per the setup guide). Paid
//     software — the launcher must not, and does not, ship or recreate it. The
//     integration additionally REQUIRES the "Rise & Fall" and "Gathering Storm"
//     expansions to be installed AND enabled (the mod targets the Gathering Storm
//     ruleset). VERIFIED — worlds/civ_6/docs/setup_en.md.
//
//   * THE "Civ6 Client" is a PYTHON client SHIPPED INSIDE the Archipelago release the
//     player already has. It is registered in worlds/civ_6/__init__.py as
//     Component("Civ6 Client", func=run_client, component_type=Type.CLIENT,
//     file_identifier=SuffixIdentifier(".apcivvi")) and launched as subprocess name
//     "Civ6Client". VERIFIED. It holds the AP server connection (CivVIContext extends
//     CommonContext) and relays to the running game through the Civ VI in-game "Tuner"
//     (FireTuner) network interface — CivVIInterface/TunerClient send Lua commands such
//     as IsInGame() to the game. The player connects by entering their slot name INTO
//     THE Civ6 CLIENT — there is no connection arg on the game and no config file this
//     launcher can pre-write. Hence ConnectsItself = true: the launcher must not also
//     sit on the slot or the two would kick each other off.
//
//   * THE MOD IS A SEPARATE GITHUB PROJECT (NOT bundled inside Archipelago, unlike the
//     Wargroove/Undertale mods). It is the "Civ VI AP Mod" at
//     github.com/hesto2/civilization_archipelago_mod/releases/latest. The player
//     unzips it into their Civ VI Mods folder, which on Windows is the USER-WRITABLE
//         %USERPROFILE%\Documents\My Games\Sid Meier's Civilization VI\Mods
//     (NOTE: this is under Documents — NOT ProgramData — and may be redirected into
//     OneDrive\Documents when Known-Folder redirection is on; both are probed). The
//     resulting folder is ...\Mods\civilization_archipelago_mod (contains a .modinfo).
//     Per-room the AP generator also produces a `.apcivvi` file (a zip of five mod
//     files) whose CONTENTS must be copied into that same civilization_archipelago_mod
//     folder before connecting — that per-seed step is the player's (or the Civ6
//     Client's, via file association) job, and this launcher does not fabricate it.
//
// ── WHAT THIS PLUGIN HONESTLY DOES (honest scope) ─────────────────────────────
//   1. DETECT the Steam Civilization VI install (appid 289070) via the standard
//      registry → libraryfolders.vdf → appmanifest_289070.acf → common pipeline (the
//      same one the shipped Wargroove/Subnautica/Stardew plugins use; default
//      ...\steamapps\common\Sid Meier's Civilization VI). A manual root-dir OVERRIDE
//      (folder picker) is supported and takes precedence; it is persisted in this
//      plugin's OWN sidecar (Games/ROMs/civ_6/civ6_launcher.json) — Core/SettingsStore
//      is NOT modified. (Epic / other stores work via the picker.)
//   2. DETECT the user's Civ VI Mods folder under Documents (and the OneDrive-redirected
//      variant) and whether the Archipelago mod (the civilization_archipelago_mod
//      folder) is present there yet, so the tile/Settings can say "ready" honestly. A
//      manual Mods-folder OVERRIDE is also supported (sidecar).
//   3. AUTO-STAGE THE MOD when possible. The Civ VI Mods folder is user-writable
//      (Documents), so — UNLIKE the read-only-ProgramData cases — this plugin CAN help.
//      If a copy of the mod is found bundled next to the launcher
//      (Games/Mods/civilization_archipelago_mod or an unzipped folder the user dropped
//      in), it is COPIED into the Documents Mods folder. Otherwise it GUIDES honestly
//      (download from the mod's GitHub releases, unzip into the Mods folder). It never
//      downloads paid game content and never invents the per-seed `.apcivvi` contents.
//   4. LOCATE the bundled Civ6 Client in the user's Archipelago install — READ-ONLY.
//      ***CRITICAL SECURITY: C:\ProgramData\Archipelago is treated as READ-ONLY. This
//      plugin only ever READS it to find ArchipelagoLauncher.exe / a Civ6 client exe.
//      It NEVER writes anything under ProgramData.*** A manual AP-install/client-path
//      OVERRIDE is also supported (sidecar).
//   5. LAUNCH (best effort, never blocks): start the bundled Civ6 Client (it owns the
//      AP connection + relays to the game via the Tuner), and also launch Civ VI itself
//      (steam://rungameid/289070, or the detected exe). ConnectsItself = true (no
//      prefill — the user types their slot name into the Civ6 Client).
//      SupportsStandalone = true (plain Civ VI runs without AP).
//   6. NEVER claim an install/mod exists when it does not; never modify the Steam copy
//      (§11); keep its settings in its OWN sidecar.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Wargroove/Undertale-style) ──
//   * The launcher cannot launch a Python component by name the way the Archipelago
//     Launcher does. Instead it best-effort runs a bundled Windows EXE: it prefers a
//     "*civ*client*.exe" (or "*civ6*client*.exe"), then ArchipelagoLauncher.exe (passed
//     the "Civ6 Client" component name as an argument, which is how the AP launcher
//     dispatches components), found under the detected/override Archipelago install
//     root. The exact bundled-client EXE name was not inspected byte-for-byte offline,
//     so resolution is pattern-based with the AP launcher as the documented fallback.
//     If neither is found, Launch surfaces honest guidance ("start the Civ6 Client from
//     the Archipelago Launcher") and still tries to open the game so the user gets one
//     click.
//   * The mod's exact runnable-state ("is everything the room needs in place") cannot
//     be verified beyond "does the civilization_archipelago_mod folder exist in the
//     Mods directory"; the per-seed `.apcivvi` contents and the in-game Tuner/Mods
//     toggles are surfaced as explicit manual steps.
//   * The bundled-mod auto-stage source is OPTIONAL — if the user has not placed the
//     mod next to the launcher, staging is a no-op and the plugin guides instead. No
//     game files are ever downloaded by this plugin.
//   * No plaintext AP password is ever written by this plugin (the connection is
//     entered in the Civ6 Client), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Civ6Plugin : IGamePlugin
{
    // ── Constants — Steam / AP facts (verified 2026-06-14) ─────────────────────

    /// Civilization VI's Steam application id (VERIFIED — store.steampowered.com/app/289070,
    /// steamdb.info/app/289070).
    private const string SteamAppId = "289070";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The standard Steam install sub-folder name for Civ VI.
    private const string SteamCommonFolderName = "Sid Meier's Civilization VI";

    /// Candidate game exe names inside the Civ VI install (used for a direct-launch
    /// fallback and to recognise a valid install folder). Civ VI ships separate DX11
    /// and DX12 launchers plus a top-level Civ6.exe depending on version; any one of
    /// these present means "this is a Civ VI install".
    private static readonly string[] GameExeNames =
    {
        "Civ6.exe",        // common top-level / launcher
        "Civ6Sub.exe",     // some installs
        "CivilizationVI.exe",
    };

    /// The Documents sub-path Firaxis uses for Civ VI user data (Mods + saves live
    /// here). VERIFIED — setup guide:
    ///   <Documents>\My Games\Sid Meier's Civilization VI\Mods
    private const string MyGamesVendorFolder = "My Games";
    private const string MyGamesGameFolder   = "Sid Meier's Civilization VI";
    private const string ModsFolderName      = "Mods";

    /// The Archipelago mod's folder name once unzipped into the Mods folder, and the
    /// extension of its descriptor file. VERIFIED — setup guide:
    ///   ...\Mods\civilization_archipelago_mod  (contains a *.modinfo).
    private const string ApModFolderName = "civilization_archipelago_mod";
    private const string ModInfoExtension = ".modinfo";

    /// Default Archipelago install root on Windows (READ-ONLY — see header).
    private const string DefaultArchipelagoRoot = @"C:\ProgramData\Archipelago";

    /// The AP launcher exe (dispatches components by name) + the verified component
    /// display name to ask it to run (VERIFIED — Component("Civ6 Client", …)).
    private const string ArchipelagoLauncherExe = "ArchipelagoLauncher.exe";
    private const string Civ6ComponentName = "Civ6 Client";

    /// Official Archipelago "Civilization VI" setup guide (slug = the AP game string).
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Civilization%20VI/setup/en";

    /// Civ VI on Steam (the base game the player must own; expansions required).
    private const string SteamStoreUrl =
        "https://store.steampowered.com/app/289070/Sid_Meiers_Civilization_VI/";

    /// The Civ VI AP Mod's GitHub releases (the mod is a SEPARATE project — VERIFIED in
    /// the setup guide).
    private const string ModReleasesUrl =
        "https://github.com/hesto2/civilization_archipelago_mod/releases/latest";

    /// GitHub releases API for the mod repo (news feed source — the mod is what is
    /// versioned independently here).
    private const string ModReleasesApiUrl =
        "https://api.github.com/repos/hesto2/civilization_archipelago_mod/releases";

    /// Archipelago releases — the Civ6 Client ships inside this download.
    private const string ApReleasesUrl =
        "https://github.com/ArchipelagoMW/Archipelago/releases";

    private const string ArchipelagoSite = "https://archipelago.gg";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    /// World id = the worlds/civ_6 folder name (stable plugin key).
    public string GameId      => "civ_6";
    public string DisplayName => "Civilization VI";
    public string Subtitle    => "Native PC · Archipelago";

    /// EXACT AP game string — VERIFIED against worlds/civ_6/__init__.py
    /// (CivVIWorld.game = "Civilization VI") AND Civ6Client.py
    /// (CivVIContext.game = "Civilization VI"). World id "civ_6".
    public string ApWorldName => "Civilization VI";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "civ_6.png");

    public string ThemeAccentColor => "#1F6F8B";   // Civ VI ocean / map-UI teal
    public string[] GameBadges     => new[] { "Requires Civilization VI" };

    public string Description =>
        "Sid Meier's Civilization VI is Firaxis's turn-based 4X strategy game: build a " +
        "civilization to stand the test of time across the ages, from a single settler " +
        "to a world power. This is the official Archipelago integration, played on the " +
        "Gathering Storm ruleset. You bring your own copy of Civilization VI (owned on " +
        "Steam or Epic) with the Rise & Fall and Gathering Storm expansions; you install " +
        "the community Civ VI Archipelago mod into your game's Mods folder, and " +
        "Archipelago's bundled \"Civ6 Client\" holds the multiworld connection and relays " +
        "your unlocked techs, civics and boosts to the running game through the in-game " +
        "Tuner. The launcher detects your install, can stage the mod, start the client " +
        "and the game, and guides the rest.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    // The integration has two independently-versioned moving parts: the Civ6 Client
    // (ships inside the player's Archipelago release) and the SEPARATE Civ VI AP mod
    // (its own GitHub releases). There is no single combined version stamp this plugin
    // can author honestly, so these stay null (the news feed surfaces the mod's release
    // versions instead).
    public string? InstalledVersion => null;
    public string? AvailableVersion => null;

    /// "Installed" == the Archipelago mod folder has been placed into the Documents Civ
    /// VI Mods folder. Owning only the Steam base game does NOT flip it true — that copy
    /// has no AP mod until civilization_archipelago_mod is staged there.
    public bool IsInstalled => IsModInstalled();
    public bool IsRunning   { get; private set; }

    /// Reports the detected/override Civ VI ROOT (Steam) directory when known, else ""
    /// (interface contract).
    public string GameDirectory => ResolveCiv6RootDir() ?? "";

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so the
    /// plugin stays a single self-contained source file — same approach as the Wargroove
    /// / Undertale / Subnautica plugins). BOM-less UTF-8, under Games/ROMs/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "civ6_launcher.json");

    /// OPTIONAL bundled-mod staging source next to the launcher. If the user drops the
    /// unzipped mod here (Games/Mods/civilization_archipelago_mod with its .modinfo),
    /// the plugin can COPY it into the Documents Mods folder. Never required.
    private string BundledModSourceDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "Mods", ApModFolderName);

    // ── Internal override state (restored from sidecar) ────────────────────────

    /// User-set override of the Civ VI ROOT (Steam) folder, when auto-detect misses
    /// (Epic / non-standard Steam library). Optional.
    private string? _overrideRootDir;

    /// User-set override of the Documents Civ VI Mods folder (when Documents is
    /// redirected somewhere unusual and auto-probe misses). Optional.
    private string? _overrideModsDir;

    /// User-set override of the Archipelago install root (where the bundled Civ6 Client
    /// lives). READ-ONLY target — used only to LOCATE the client, never written.
    private string? _overrideApDir;

    private Process? _gameProcess;     // Civ6 exe, if we launched it directly
    private Process? _clientProcess;   // the bundled Civ6 Client, if we launched it

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The external Civ6 Client owns the AP slot and relays checks/items/goal to the
    // server itself (via the in-game Tuner); the launcher relays nothing. These exist
    // only for interface compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Constructor — restore overrides ───────────────────────────────────────

    public Civ6Plugin()
    {
        try
        {
            var s = LoadSettings();
            if (!string.IsNullOrWhiteSpace(s.RootDirOverride) && Directory.Exists(s.RootDirOverride))
                _overrideRootDir = s.RootDirOverride;
            if (!string.IsNullOrWhiteSpace(s.ModsDirOverride) && Directory.Exists(s.ModsDirOverride))
                _overrideModsDir = s.ModsDirOverride;
            if (!string.IsNullOrWhiteSpace(s.ApInstallOverride) && Directory.Exists(s.ApInstallOverride))
                _overrideApDir = s.ApInstallOverride;
        }
        catch { /* fall back to detection only */ }
    }

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────
    // No single combined version to compare. Contract: never throw on network failure.

    public Task CheckForUpdateAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────
    // HONEST guided setup with a real assist where it is safe. The Civ VI Mods folder
    // lives under Documents (user-writable) — so unlike the read-only-ProgramData cases,
    // this plugin CAN stage the mod when a bundled copy is present next to the launcher.
    // It never downloads the paid game and never fabricates the per-seed `.apcivvi`
    // contents, and it never writes to the read-only Archipelago install.

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((10, "Checking for your Civilization VI install and Mods folder..."));

        string? steamDir = ResolveCiv6RootDir();
        string? modsDir  = ResolveModsDir();
        bool    modReady = IsModInstalled();
        string? client   = ResolveBundledClient();

        // If the mod is already in place, we are done — explain how to play.
        if (modReady)
        {
            progress.Report((100,
                "The Civilization VI Archipelago mod is installed (found at " +
                (ModInstallDir() ?? "your Mods folder") + "). To play: enable the mod and " +
                "the Tuner in-game (Additional Content → Mods, and Options → Game → " +
                "\"Tuner (disables achievements)\"), start a Single Player game on the " +
                "Gathering Storm ruleset in the Ancient era, then start the Civ6 Client " +
                "and enter your slot name. Per room, also copy the contents of your " +
                ".apcivvi file into the mod folder first. Press Play here to start the " +
                "client and the game. See the Setup Guide link in Settings."));
            return Task.CompletedTask;
        }

        // Mod not installed yet. Try to STAGE it from a bundled copy if the user dropped
        // one next to the launcher; otherwise guide.
        var sb = new StringBuilder();
        bool staged = false;

        if (modsDir != null && Directory.Exists(BundledModSourceDir))
        {
            progress.Report((50, "Staging the Archipelago mod into your Civ VI Mods folder..."));
            try
            {
                staged = TryStageBundledMod(modsDir);
            }
            catch { staged = false; }
        }

        if (staged)
        {
            sb.Append("Staged the Civilization VI Archipelago mod into \"")
              .Append(ModInstallDir() ?? modsDir).Append("\". ");
        }
        else
        {
            sb.Append("Civilization VI's Archipelago mod is a separate download (not shipped ")
              .Append("by this launcher). Get the latest \"Civ VI AP Mod\" from its GitHub ")
              .Append("releases and unzip it into your Civ VI Mods folder so you end up with ")
              .Append("...\\Mods\\").Append(ApModFolderName).Append(" (a folder, not loose files). ");
            if (modsDir != null)
                sb.Append("Your Mods folder is \"").Append(modsDir).Append("\". ");
            else
                sb.Append("Your Mods folder is usually %USERPROFILE%\\Documents\\My Games\\")
                  .Append("Sid Meier's Civilization VI\\Mods (or under OneDrive\\Documents). ");
        }

        if (steamDir != null)
            sb.Append("Your Steam Civ VI was detected at \"").Append(steamDir).Append("\". ");
        else
            sb.Append("Install Civilization VI (appid ").Append(SteamAppId)
              .Append(") with the Rise & Fall and Gathering Storm expansions, or set its ")
              .Append("folder in Settings. ");

        if (client != null)
            sb.Append("The Civ6 Client was located in your Archipelago install. ");
        else
            sb.Append("To connect, run \"Civ6 Client\" from the Archipelago Launcher ")
              .Append("(set your Archipelago folder in Settings if it was not found). ");

        sb.Append("Per room, copy the contents of your .apcivvi file into the mod folder, ")
          .Append("enable the mod + Tuner in-game, then connect with the Civ6 Client. ")
          .Append("See the Setup Guide link in Settings.");

        progress.Report((100, sb.ToString()));
        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public Task<bool> VerifyInstallAsync(CancellationToken ct = default)
        => Task.FromResult(IsInstalled);

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker for the Civ VI ROOT folder: a valid folder
    /// contains one of the known Civ VI exes. Return null when acceptable, else a short
    /// human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Civilization VI install folder " +
                   "(the one containing Civ6.exe).";

        if (LooksLikeCiv6Root(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeCiv6Root(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Civilization VI installation. Pick the folder " +
               "that contains Civ6.exe (for Steam this is usually " +
               @"...\steamapps\common\Sid Meier's Civilization VI).";
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────
    // Best effort, never blocks. Start the bundled Civ6 Client (it owns the AP
    // connection AND relays to the game via the Tuner) and also open Civ VI. The
    // connection itself is entered into the client (see header), so we do NOT pass
    // connection args and we do NOT hold an ApClient on the slot (ConnectsItself = true).

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        _ = session; // connection is made in the Civ6 Client, not via args here

        // 1) Start the bundled client if we can find it (read-only locate). This is what
        //    holds the slot + relays to the game. Best effort.
        string? client = ResolveBundledClient();
        if (client != null)
        {
            try { StartBundledClient(client); } catch { /* non-fatal */ }
        }

        // 2) Open Civ VI itself so the user gets one click. Best effort.
        try { StartCiv6(); } catch { /* non-fatal — the client can also be used */ }

        return Task.CompletedTask;
    }

    /// Civ VI is a complete game — plain (non-AP) play is fully supported.
    public bool SupportsStandalone => true;

    /// The external Civ6 Client owns the AP slot connection (see header). The launcher
    /// must NOT connect its own ApClient to the same slot while the client runs, or they
    /// would kick each other off.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // Plain Civ VI — do not start the AP client.
        StartCiv6();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // Kill ONLY what this launcher started: the game we opened and the bundled client
        // we launched. We never touch the user's AP server.
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        try { _clientProcess?.Kill(entireProcessTree: true); } catch { }
        _gameProcess = null;
        _clientProcess = null;
        IsRunning = false;
        // No plaintext AP password is written by this plugin (the connection is entered
        // in the Civ6 Client), so there is nothing to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The Civ6 Client receives items from the AP server directly and relays them into
        // the game via the Tuner; there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The Civ6 Client renders its own connection state; no launcher HUD channel into
        // the game.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD0, 0xA0, 0x40));

        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Civilization VI's Archipelago support has three parts: your own Civ VI " +
                   "(Steam/Epic) WITH the Rise & Fall and Gathering Storm expansions; the " +
                   "community Civ VI AP mod (a separate GitHub download) placed in your Civ " +
                   "VI Mods folder under Documents; and Archipelago's bundled \"Civ6 Client\", " +
                   "which holds the multiworld connection and relays to the running game via " +
                   "the in-game Tuner. The launcher detects your install, can stage the mod " +
                   "(your Mods folder is writable), and can start the client and the game — " +
                   "but you connect (slot name) inside the Civ6 Client, not here, and per " +
                   "room you copy your .apcivvi contents into the mod folder. Your Archipelago " +
                   "install is only ever READ to find the client; nothing is written there. " +
                   "Some client-exe details were verified against the AP source/setup guide, " +
                   "not byte-for-byte offline.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: Steam base game (root dir) ───────────────────────────
        panel.Children.Add(SectionHeader("CIVILIZATION VI INSTALL (ROOT DIRECTORY)", muted));

        string? rootDir = ResolveCiv6RootDir();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = rootDir != null
                ? "✓ Detected (appid " + SteamAppId + "):\n" + rootDir
                : "Not detected via Steam. Install Civilization VI (appid " + SteamAppId +
                  ") with the Rise & Fall and Gathering Storm expansions, or set the folder " +
                  "below (Epic / non-standard Steam library).",
            FontSize = 11, Foreground = rootDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var rootRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var rootBox = new System.Windows.Controls.TextBox
        {
            Text = _overrideRootDir ?? rootDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Civilization VI install folder (contains Civ6.exe). Detected " +
                          "from Steam automatically; set it here to override.",
        };
        var rootBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120, Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        rootBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Civilization VI install folder (contains Civ6.exe)",
                InitialDirectory = Directory.Exists(_overrideRootDir ?? rootDir ?? "")
                                   ? (_overrideRootDir ?? rootDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Civilization VI folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeCiv6Root(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeCiv6Root(nested)) picked = nested;
                }
                _overrideRootDir = picked;
                rootBox.Text = picked;
                SaveRootDirOverride(picked);
            }
        };
        System.Windows.Controls.DockPanel.SetDock(rootBtn, System.Windows.Controls.Dock.Right);
        rootRow.Children.Add(rootBtn);
        rootRow.Children.Add(rootBox);
        panel.Children.Add(rootRow);

        // ── Section: Mods folder (Documents) + mod status ─────────────────
        panel.Children.Add(SectionHeader("CIV VI MODS FOLDER (DOCUMENTS) & ARCHIPELAGO MOD", muted));

        string? modsDir  = ResolveModsDir();
        bool    modReady = IsModInstalled();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modReady
                ? "✓ Archipelago mod found:\n" + (ModInstallDir() ?? "(Mods folder)")
                : (modsDir != null
                    ? "Mods folder detected:\n" + modsDir + "\nThe Archipelago mod (" +
                      ApModFolderName + ") is not there yet. Use \"Stage / open Mods folder\" " +
                      "below, or unzip the Civ VI AP Mod into it."
                    : "Mods folder not detected. It is usually %USERPROFILE%\\Documents\\My " +
                      "Games\\Sid Meier's Civilization VI\\Mods (or under OneDrive\\Documents). " +
                      "Set it below."),
            FontSize = 11, Foreground = modReady ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var modsRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var modsBox = new System.Windows.Controls.TextBox
        {
            Text = _overrideModsDir ?? modsDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Civ VI Mods folder (Documents\\My Games\\Sid Meier's " +
                          "Civilization VI\\Mods). Detected automatically; set it here to override.",
        };
        var modsBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120, Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        modsBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Civ VI Mods folder (…\\Sid Meier's Civilization VI\\Mods)",
                InitialDirectory = Directory.Exists(_overrideModsDir ?? modsDir ?? "")
                                   ? (_overrideModsDir ?? modsDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                _overrideModsDir = dlg.FolderName;
                modsBox.Text = dlg.FolderName;
                SaveModsDirOverride(dlg.FolderName);
            }
        };
        System.Windows.Controls.DockPanel.SetDock(modsBtn, System.Windows.Controls.Dock.Right);
        modsRow.Children.Add(modsBtn);
        modsRow.Children.Add(modsBox);
        panel.Children.Add(modsRow);

        // Stage / open helpers.
        var modsActions = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new System.Windows.Thickness(0, 2, 0, 8),
        };
        var stageBtn = new System.Windows.Controls.Button
        {
            Content = Directory.Exists(BundledModSourceDir) ? "Stage bundled mod" : "Stage bundled mod (none found)",
            Padding = new System.Windows.Thickness(10, 6, 10, 6),
            Margin  = new System.Windows.Thickness(0, 0, 8, 0),
            IsEnabled = Directory.Exists(BundledModSourceDir),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
            ToolTip = "If you placed the unzipped mod next to the launcher (Games\\Mods\\" +
                      ApModFolderName + "), copy it into your Mods folder.",
        };
        stageBtn.Click += (_, _) =>
        {
            string? md = ResolveModsDir();
            if (md == null)
            {
                System.Windows.MessageBox.Show(
                    "Could not find your Civ VI Mods folder. Set it above first.",
                    "Mods folder not set",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
            }
            try
            {
                bool ok = TryStageBundledMod(md);
                System.Windows.MessageBox.Show(
                    ok ? "Staged the Archipelago mod into:\n" + (ModInstallDir() ?? md)
                       : "No bundled mod copy was found next to the launcher (Games\\Mods\\" +
                         ApModFolderName + "). Download the Civ VI AP Mod and unzip it into " +
                         "your Mods folder instead.",
                    ok ? "Mod staged" : "Nothing to stage",
                    System.Windows.MessageBoxButton.OK,
                    ok ? System.Windows.MessageBoxImage.Information : System.Windows.MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show("Could not stage the mod: " + ex.Message,
                    "Staging failed",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
        };
        var openModsBtn = new System.Windows.Controls.Button
        {
            Content = "Open Mods folder",
            Padding = new System.Windows.Thickness(10, 6, 10, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        openModsBtn.Click += (_, _) =>
        {
            string? md = ResolveModsDir();
            try
            {
                if (md != null)
                {
                    Directory.CreateDirectory(md);
                    Process.Start(new ProcessStartInfo(md) { UseShellExecute = true });
                }
            }
            catch { /* non-fatal */ }
        };
        modsActions.Children.Add(stageBtn);
        modsActions.Children.Add(openModsBtn);
        panel.Children.Add(modsActions);

        // ── Section: Archipelago install / bundled client (READ-ONLY) ─────
        panel.Children.Add(SectionHeader("ARCHIPELAGO INSTALL (READ-ONLY — TO FIND THE CLIENT)", muted));

        string? apRoot = ResolveArchipelagoRoot();
        string? client = ResolveBundledClient();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = client != null
                ? "✓ Bundled Civ6 Client located:\n" + client
                : (apRoot != null
                    ? "Archipelago install found at \"" + apRoot + "\", but the Civ6 Client " +
                      "launcher was not located inside it. You can still run it from the " +
                      "Archipelago Launcher (open \"Civ6 Client\")."
                    : "Archipelago install not found automatically. Set its folder below, or " +
                      "just run the Civ6 Client from the Archipelago Launcher."),
            FontSize = 11, Foreground = client != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        var apRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var apBox = new System.Windows.Controls.TextBox
        {
            Text = _overrideApDir ?? apRoot ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Archipelago install folder (default " + DefaultArchipelagoRoot +
                          "). READ-ONLY — only used to locate the bundled Civ6 Client.",
        };
        var apBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120, Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        apBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Archipelago install folder (read-only — to find the Civ6 Client)",
                InitialDirectory = Directory.Exists(_overrideApDir ?? apRoot ?? "")
                                   ? (_overrideApDir ?? apRoot!)
                                   : (Directory.Exists(DefaultArchipelagoRoot)
                                       ? DefaultArchipelagoRoot
                                       : AppContext.BaseDirectory),
            };
            if (dlg.ShowDialog() == true)
            {
                _overrideApDir = dlg.FolderName;
                apBox.Text = dlg.FolderName;
                SaveApInstallOverride(dlg.FolderName);
            }
        };
        System.Windows.Controls.DockPanel.SetDock(apBtn, System.Windows.Controls.Dock.Right);
        apRow.Children.Add(apBtn);
        apRow.Children.Add(apBox);
        panel.Children.Add(apRow);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "This folder is never modified — the launcher only reads it to find the " +
                   "Civ6 Client launcher. If left unset, run the client from the Archipelago " +
                   "Launcher yourself.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: setup + connection steps ─────────────────────────────
        panel.Children.Add(SectionHeader("SETUP & CONNECTION", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "1) Own and install Civilization VI (appid " + SteamAppId + ") WITH the Rise " +
                "& Fall and Gathering Storm expansions (both must be installed and enabled).\n" +
                "2) Download the Civ VI AP Mod (separate GitHub release) and unzip it into " +
                "your Civ VI Mods folder so you get ...\\Mods\\" + ApModFolderName + " (use " +
                "\"Stage bundled mod\" above if you placed a copy next to the launcher).\n" +
                "3) Per room: open your .apcivvi file (it is a zip) and copy its five files " +
                "into the " + ApModFolderName + " folder (overwrite if asked).\n" +
                "4) In Civ VI: Options → Game → enable \"Tuner (disables achievements)\"; " +
                "Additional Content → Mods → enable the Archipelago mod. Start a Single " +
                "Player game on the Gathering Storm ruleset, Ancient era.\n" +
                "5) Start the \"Civ6 Client\" (press Play here, or run it from the Archipelago " +
                "Launcher), connect to your server, and enter your slot name.\n\n" +
                "(The connection is entered in the Civ6 Client — the launcher does not " +
                "connect to your slot itself. Pressing Play here starts the client and the " +
                "game; you still connect in the client. If items/checks get out of sync, run " +
                "/resync in the Civ6 Client.)",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Civilization VI on Steam ↗",  SteamStoreUrl),
            ("Civ VI Setup Guide ↗",        SetupGuideUrl),
            ("Civ VI AP Mod (download) ↗",  ModReleasesUrl),
            ("Archipelago Releases ↗",      ApReleasesUrl),
            ("Archipelago Official ↗",      ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
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
            Text = text, FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        };

    // ── News feed ─────────────────────────────────────────────────────────────
    // The independently-versioned part of this integration is the Civ VI AP mod, so the
    // most honest "news" is that mod's GitHub release stream. Never throws — empty on
    // failure.

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(ModReleasesApiUrl, ct);
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
                    Version: el.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — mod (Documents Mods folder) detection + staging ──────

    /// The resolved path of the staged Archipelago mod folder, or null when the Mods
    /// folder is unknown.
    private string? ModInstallDir()
    {
        string? mods = ResolveModsDir();
        return mods == null ? null : Path.Combine(mods, ApModFolderName);
    }

    /// True when the Archipelago mod has been placed into the Civ VI Mods folder. We
    /// require the civilization_archipelago_mod folder to exist AND to contain at least
    /// one .modinfo (the canonical marker that it is a real mod folder, not an empty
    /// stub or a stray loose-file drop).
    private bool IsModInstalled()
    {
        try
        {
            string? dir = ModInstallDir();
            if (dir == null || !Directory.Exists(dir)) return false;
            return DirHasModInfo(dir);
        }
        catch { return false; }
    }

    /// True when `dir` contains at least one *.modinfo file (top level).
    private static bool DirHasModInfo(string dir)
    {
        try
        {
            foreach (string _ in Directory.EnumerateFiles(dir, "*" + ModInfoExtension,
                                                          SearchOption.TopDirectoryOnly))
                return true;
        }
        catch { /* unreadable */ }
        return false;
    }

    /// Copy the OPTIONAL bundled mod (Games/Mods/civilization_archipelago_mod next to
    /// the launcher) into the given Mods folder. Returns true if a mod was staged (i.e.
    /// the destination now has a .modinfo), false if there was no bundled source. The
    /// Mods folder is user-writable (Documents) so this is safe — we never write to any
    /// read-only location. Existing files are overwritten so re-staging acts as an
    /// update.
    private bool TryStageBundledMod(string modsDir)
    {
        if (!Directory.Exists(BundledModSourceDir)) return false;
        // Only treat it as a real source if it actually looks like the mod.
        if (!DirHasModInfo(BundledModSourceDir))
        {
            // Maybe the user dropped the zip's inner folder one level down; look for a
            // single nested folder that has a .modinfo.
            string? nested = FindNestedModFolder(BundledModSourceDir);
            if (nested == null) return false;
            string destN = Path.Combine(modsDir, ApModFolderName);
            CopyDirectory(nested, destN);
            return DirHasModInfo(destN);
        }

        string dest = Path.Combine(modsDir, ApModFolderName);
        CopyDirectory(BundledModSourceDir, dest);
        return DirHasModInfo(dest);
    }

    /// Look one level under `root` for a sub-folder that contains a .modinfo (handles a
    /// user dropping the zip's wrapper folder). Null if none.
    private static string? FindNestedModFolder(string root)
    {
        try
        {
            foreach (string sub in Directory.EnumerateDirectories(root))
                if (DirHasModInfo(sub)) return sub;
        }
        catch { /* unreadable */ }
        return null;
    }

    /// Recursive directory copy (overwrite). Used only for staging into the writable
    /// Documents Mods folder.
    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            string target = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
        }
        foreach (string sub in Directory.EnumerateDirectories(sourceDir))
        {
            string target = Path.Combine(destDir, Path.GetFileName(sub));
            CopyDirectory(sub, target);
        }
    }

    /// The Civ VI Mods folder to use: the override (if set and valid) wins, else the
    /// first existing Documents/OneDrive-Documents candidate, else the conventional
    /// Documents path (whether or not it exists yet — so staging can create it). Null
    /// only when no Documents location can be determined at all.
    private string? ResolveModsDir()
    {
        if (!string.IsNullOrWhiteSpace(_overrideModsDir) && Directory.Exists(_overrideModsDir))
            return _overrideModsDir;

        string? firstExisting = null;
        string? firstCandidate = null;
        foreach (string cand in EnumerateModsCandidates())
        {
            firstCandidate ??= cand;
            if (Directory.Exists(cand)) { firstExisting = cand; break; }
        }
        return firstExisting ?? firstCandidate;
    }

    /// Candidate Civ VI Mods folders: under the user's Documents (MyDocuments special
    /// folder, which honours Known-Folder redirection), under %USERPROFILE%\Documents,
    /// and under common OneDrive\Documents layouts. All are
    ///   <docs>\My Games\Sid Meier's Civilization VI\Mods.
    private static IEnumerable<string> EnumerateModsCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string? docs in EnumerateDocumentsRoots())
        {
            if (string.IsNullOrWhiteSpace(docs)) continue;
            string p = Path.Combine(docs, MyGamesVendorFolder, MyGamesGameFolder, ModsFolderName);
            if (seen.Add(p)) yield return p;
        }
    }

    /// Candidate "Documents" roots: the MyDocuments special folder (redirection-aware),
    /// %USERPROFILE%\Documents, %OneDrive%\Documents, %OneDriveConsumer%\Documents,
    /// %OneDriveCommercial%\Documents, and %USERPROFILE%\OneDrive\Documents.
    private static IEnumerable<string> EnumerateDocumentsRoots()
    {
        string? myDocs = null;
        try { myDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments); } catch { }
        if (!string.IsNullOrWhiteSpace(myDocs)) yield return myDocs;

        string? userProfile = null;
        try { userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); } catch { }
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, "Documents");
            yield return Path.Combine(userProfile, "OneDrive", "Documents");
        }

        foreach (string envVar in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            string? od = null;
            try { od = Environment.GetEnvironmentVariable(envVar); } catch { }
            if (!string.IsNullOrWhiteSpace(od))
                yield return Path.Combine(od, "Documents");
        }
    }

    // ── Private helpers — Civ VI ROOT (Steam) detection ───────────────────────

    /// The Civ VI ROOT (install) dir to use: the override (if set and valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolveCiv6RootDir()
    {
        if (!string.IsNullOrWhiteSpace(_overrideRootDir) && LooksLikeCiv6Root(_overrideRootDir))
            return _overrideRootDir;

        try { return DetectSteamCiv6Dir(); }
        catch { return null; }
    }

    /// A folder "looks like" Civ VI if it contains one of the known Civ VI exes.
    private static bool LooksLikeCiv6Root(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            foreach (string exe in GameExeNames)
                if (File.Exists(Path.Combine(dir, exe))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Civ VI install: read the Steam root from the registry, gather all
    /// library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_289070.acf exists → steamapps\common\Sid Meier's Civilization VI.
    private static string? DetectSteamCiv6Dir()
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
                        if (LooksLikeCiv6Root(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeCiv6Root(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node/64-bit InstallPath), plus a conventional fallback.
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

    /// Pull every  "path"   "<value>"  pair out of a libraryfolders.vdf body. Handles
    /// the Steam-VDF escaping of backslashes (\\ → \).
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

    /// Read the "installdir" value from an appmanifest_*.acf (same quoted-pair format as
    /// VDF). Returns null if absent.
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

    // ── Private helpers — Archipelago install + bundled client (READ-ONLY) ────

    /// The Archipelago install root to read: the override (if set) wins, else the default
    /// C:\ProgramData\Archipelago when it exists, else common alternates (per-user
    /// installs). Null when nothing is found. READ-ONLY — never written.
    private string? ResolveArchipelagoRoot()
    {
        if (!string.IsNullOrWhiteSpace(_overrideApDir) && Directory.Exists(_overrideApDir))
            return _overrideApDir;

        try
        {
            if (Directory.Exists(DefaultArchipelagoRoot))
                return DefaultArchipelagoRoot;

            // Per-user installs sometimes land under %LocalAppData%\Archipelago or
            // %ProgramFiles%\Archipelago. Probe a few read-only candidates.
            foreach (string root in EnumerateArchipelagoCandidates())
                if (Directory.Exists(root))
                    return root;
        }
        catch { /* fall through */ }
        return null;
    }

    /// Read-only candidate Archipelago install roots (besides the default and the
    /// override). All are just probed for existence — never written.
    private static IEnumerable<string> EnumerateArchipelagoCandidates()
    {
        foreach (Environment.SpecialFolder sf in new[]
        {
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
        })
        {
            string? baseDir = null;
            try { baseDir = Environment.GetFolderPath(sf); } catch { }
            if (!string.IsNullOrWhiteSpace(baseDir))
                yield return Path.Combine(baseDir, "Archipelago");
        }
    }

    /// Locate the bundled Civ6 Client launcher inside the (read-only) Archipelago
    /// install: prefer a "*civ*client*.exe" if one exists, else ArchipelagoLauncher.exe
    /// (which dispatches the "Civ6 Client" component by name). Returns the exe path or
    /// null. Only READS the install tree — never writes.
    private string? ResolveBundledClient()
    {
        try
        {
            string? root = ResolveArchipelagoRoot();
            if (root == null || !Directory.Exists(root)) return null;

            // 1) A dedicated Civ6 client exe, if the install ships one. Search the root
            //    (top-level) first — AP keeps its component exes next to
            //    ArchipelagoLauncher.exe.
            string? civClient = FindExe(root,
                name => name.Contains("civ") && name.Contains("client"));
            if (civClient != null) return civClient;

            // 2) The generic AP launcher — we pass it the component name to run.
            string launcher = Path.Combine(root, ArchipelagoLauncherExe);
            if (File.Exists(launcher)) return launcher;

            string? anyLauncher = FindExe(root,
                name => name.Contains("archipelagolauncher") ||
                        (name.Contains("archipelago") && name.Contains("launcher")));
            return anyLauncher;
        }
        catch { return null; }
    }

    /// True when the resolved client exe is the GENERIC AP launcher (so we must pass it
    /// the component name), as opposed to a dedicated Civ6 client exe.
    private static bool IsGenericApLauncher(string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        return name.Contains("launcher") && !name.Contains("civ");
    }

    /// Find the first top-level .exe in `dir` whose lower-cased name (no extension)
    /// matches `predicate`. Top-level only (AP component exes sit at the root);
    /// read-only. Null on none / unreadable.
    private static string? FindExe(string dir, Func<string, bool> predicate)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            foreach (string exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (predicate(name)) return exe;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start the bundled Civ6 Client. If it is the generic AP launcher, pass the "Civ6
    /// Client" component name so it dispatches the right component. Tracks the process so
    /// StopAsync can end it (we never touch the user's AP server).
    private void StartBundledClient(string clientExe)
    {
        var psi = new ProcessStartInfo
        {
            FileName         = clientExe,
            WorkingDirectory = Path.GetDirectoryName(clientExe) ?? "",
            UseShellExecute  = false,
        };
        if (IsGenericApLauncher(clientExe))
            psi.Arguments = Quote(Civ6ComponentName); // ArchipelagoLauncher "Civ6 Client"

        var proc = Process.Start(psi);
        if (proc == null) return;

        _clientProcess = proc;
        IsRunning = true;
        try
        {
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                // The client exiting does not necessarily mean the game stopped; only
                // clear IsRunning if the game isn't tracked/alive.
                if (_gameProcess == null || _gameProcess.HasExited)
                    IsRunning = false;
            };
        }
        catch { /* some processes don't expose Exited — non-fatal */ }
    }

    /// Start Civ VI: prefer a known exe in the detected/override root; if none can be
    /// found but Steam is present, fall back to steam://rungameid/289070. Surfaces a
    /// clear message rather than failing opaquely.
    private void StartCiv6()
    {
        string? root = ResolveCiv6RootDir();
        string? exe  = null;
        if (root != null)
        {
            foreach (string name in GameExeNames)
            {
                string cand = Path.Combine(root, name);
                if (File.Exists(cand)) { exe = cand; break; }
            }
        }

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = root!,
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
            return;
        }

        // Fall back to Steam if we at least know Steam is installed (preferred for Civ VI
        // anyway, since the Steam launcher lets the user pick DX11/DX12).
        if (SteamIsInstalled())
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
            "Could not find Civ6.exe. Open this game's Settings and pick your Civilization " +
            "VI install folder, or install Civilization VI via Steam (appid " + SteamAppId +
            ").",
            GameExeNames[0]);
    }

    /// True when any Steam root from the registry/conventional list exists on disk.
    private static bool SteamIsInstalled()
    {
        foreach (string r in SteamRoots())
        {
            try { if (!string.IsNullOrWhiteSpace(r) && Directory.Exists(r)) return true; }
            catch { /* ignore */ }
        }
        return false;
    }

    /// Quote an argument for a Windows command line (wrap in double quotes and escape
    /// embedded quotes). Plain tokens are returned unquoted.
    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        bool needs = value.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0;
        if (!needs) return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // Kept in this plugin's OWN JSON file (does not modify Core/SettingsStore). BOM-less
    // UTF-8, read-modify-write (same approach as Wargroove / Undertale / Subnautica).
    // Stores the Civ VI root override, the Mods-folder override, and the (read-only)
    // Archipelago install override.

    private sealed class Civ6Settings
    {
        /// The Civ VI install (root) folder the user pointed us at.
        public string? RootDirOverride { get; set; }

        /// The Civ VI Mods folder the user pointed us at (Documents redirect cases).
        public string? ModsDirOverride { get; set; }

        /// The Archipelago install folder the user pointed us at — READ-ONLY, used only
        /// to locate the bundled Civ6 Client.
        public string? ApInstallOverride { get; set; }
    }

    private Civ6Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Civ6Settings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(Civ6Settings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — the setting just won't persist this time */ }
    }

    private void SaveRootDirOverride(string dir)
    {
        var s = LoadSettings();
        s.RootDirOverride = dir;
        SaveSettings(s);
    }

    private void SaveModsDirOverride(string dir)
    {
        var s = LoadSettings();
        s.ModsDirOverride = dir;
        SaveSettings(s);
    }

    private void SaveApInstallOverride(string dir)
    {
        var s = LoadSettings();
        s.ApInstallOverride = dir;
        SaveSettings(s);
    }
}
