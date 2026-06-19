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
// Orientation, FontWeights, HorizontalAlignment, Cursors, Thickness, …). To avoid
// CS0104 this file deliberately does NOT do `using System.Windows.Controls;` /
// `using System.Windows.Media;` — every WPF UI type below is written FULLY QUALIFIED
// (System.Windows.Controls.*, System.Windows.Media.*, System.Windows.MessageBox, …).
// It also does NOT declare any file-level `using X = System.Windows...;` alias
// (CS1537 — GlobalUsings.cs already aliases the short names; a local alias would
// conflict). Bare names from GlobalUsings, or full qualification, only.

namespace LauncherV2.Plugins.Wargroove;

// ═══════════════════════════════════════════════════════════════════════════════
// WargroovePlugin — detect / guide / launch for "Wargroove" (Chucklefish, 2019)
// played through its OFFICIAL Archipelago integration. This is a NATIVE
// "ConnectsItself" integration in the same family as the shipped Undertale and Jak
// and Daxter plugins: an EXTERNAL bundled Archipelago client owns the AP slot and
// relays to the running game — the launcher must NOT hold its own ApClient on the
// slot, and there is no in-game / command-line connection prefill it can apply.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against AP-main) ──────
// Wargroove's Archipelago support is part of AP-MAIN itself (worlds/wargroove),
// NOT a third-party one-click mod. The pieces are:
//
//   * THE BASE GAME is the user's own legally-owned Wargroove (Steam appid 607050,
//     Chucklefish). Paid software — the launcher must not, and does not, ship or
//     recreate it.
//
//   * THE "Wargroove Client" is a PYTHON client SHIPPED INSIDE the Archipelago
//     release the player already has. It is registered in worlds/wargroove/__init__.py
//     as  Component("Wargroove Client", game_name="Wargroove", func=launch_client,
//     component_type=Type.CLIENT)  and launched as component name "WargrooveClient"
//     from the Archipelago Launcher. VERIFIED.
//
//   * THE MOD + CAMPAIGN ARE INSTALLED BY THAT CLIENT, into a WRITABLE %AppData%
//     folder (NOT ProgramData). On start, WargrooveContext.__init__ extracts five
//     embedded resources from the worlds.wargroove package into the user's profile:
//         %APPDATA%\Chucklefish\Wargroove\mods\ArchipelagoMod\maps.dat
//         %APPDATA%\Chucklefish\Wargroove\mods\ArchipelagoMod\mod.dat
//         %APPDATA%\Chucklefish\Wargroove\mods\ArchipelagoMod\modAssets.dat
//         %APPDATA%\Chucklefish\Wargroove\save\campaign-…c483d.cmp(.bak)
//     i.e. the 3 .dat files + the Archipelago campaign save. VERIFIED against
//     Client.py's `resources`/`file_paths` lists. So the launcher does NOT try to
//     reproduce this — exactly as the Undertale plugin does not reproduce
//     /auto_patch and SoH does not reproduce OTR generation. Starting the client
//     is what installs the mod.
//
//   * CONNECTION IS CLIENT-RELAY, NOT in-game and NOT launcher-relay. The
//     WargrooveClient inherits from CommonContext (it holds the AP server
//     connection) and communicates with the running game through FILE IPC — it
//     writes JSON/item files into an "AP" folder under the Wargroove ROOT directory
//     (the Steam install dir), which the in-game ArchipelagoMod reads to receive
//     items and report checks. The player connects by entering their server +
//     username INTO THE WARGROOVE CLIENT — there is no connection arg on the game
//     and no config file this launcher can pre-write. Hence ConnectsItself = true:
//     the launcher must not also sit on the slot or the two would kick each other.
//
// ── WHAT THIS PLUGIN HONESTLY DOES (honest scope) ─────────────────────────────
//   1. DETECT the Steam Wargroove install (appid 607050) via the standard
//      registry → libraryfolders.vdf → appmanifest_607050.acf → common pipeline
//      (the Wargroove ROOT directory the client needs, default
//      ...\steamapps\common\Wargroove). A manual root-dir OVERRIDE (folder picker)
//      is supported and takes precedence; it is persisted in this plugin's OWN
//      sidecar (Games/ROMs/wargroove/wargroove_launcher.json) — Core/SettingsStore
//      is NOT modified. (Epic/other stores work via the picker.)
//   2. DETECT the %AppData% Wargroove save folder (Chucklefish\Wargroove) and
//      whether the ArchipelagoMod (its 3 .dat files) is present there yet — so the
//      tile/Settings can say "ready" honestly. (This folder lives under %AppData%,
//      NOT ProgramData, so detection-only is all that is needed; the bundled client
//      writes it.)
//   3. LOCATE the bundled client in the user's Archipelago install — READ-ONLY.
//      ***CRITICAL SECURITY: C:\ProgramData\Archipelago is treated as READ-ONLY.
//      This plugin only ever READS it to find ArchipelagoLauncher.exe /
//      ArchipelagoWargrooveClient.exe. It NEVER writes anything under ProgramData.***
//      A manual AP-install/client-path OVERRIDE is also supported (sidecar).
//   4. LAUNCH (best effort, never blocks): start the bundled Wargroove Client (it
//      owns the AP connection + installs the mod into %AppData%), and also launch
//      Wargroove itself (steam://rungameid/607050, or the detected Wargroove.exe).
//      ConnectsItself = true (no prefill — the user types server+username into the
//      client). SupportsStandalone = true (plain Wargroove runs without AP).
//   5. NEVER claim an install exists when it does not; never modify the Steam copy
//      (§11); keep its settings in its OWN sidecar.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Undertale/Jak-style) ──────
//   * The launcher cannot launch a Python component by name the way the Archipelago
//     Launcher does. Instead it best-effort runs a bundled Windows EXE: it prefers
//     a "*wargroove*client*.exe", then ArchipelagoLauncher.exe (passed the
//     "Wargroove Client" component name as an argument, which is how the AP launcher
//     dispatches components), found under the detected/override Archipelago install
//     root. The exact bundled-client EXE name was not inspected byte-for-byte
//     offline, so resolution is pattern-based with the AP launcher as the documented
//     fallback. If neither is found, Launch surfaces honest guidance ("start the
//     Wargroove Client from the Archipelago Launcher") and still tries to open the
//     game so the user gets one click.
//   * "Mod installed" is judged by the presence of the ArchipelagoMod .dat files in
//     the %AppData% save folder — written by the client, not by this launcher.
//   * No plaintext AP password is ever written by this plugin (the connection is
//     entered in the client), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class WargroovePlugin : IGamePlugin
{
    // ── Constants — Steam / AP facts (verified 2026-06-14) ─────────────────────

    /// Wargroove's Steam application id (VERIFIED — store.steampowered.com/app/607050,
    /// steamdb.info/app/607050; the world's default root_directory points at
    /// ...\steamapps\common\Wargroove).
    private const string SteamAppId = "607050";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The standard Steam install sub-folder name for Wargroove.
    private const string SteamCommonFolderName = "Wargroove";

    /// The game exe inside the Steam install (used for a direct launch fallback).
    private const string PreferredGameExeName = "Wargroove.exe";

    /// %AppData% sub-path Chucklefish uses for Wargroove (mods + save live here).
    /// VERIFIED — the world's SaveDirectory docstring and Client.py both reference
    /// "/Chucklefish/Wargroove/".
    private const string AppDataVendorFolder = "Chucklefish";
    private const string AppDataGameFolder   = "Wargroove";

    /// The mod folder the Wargroove Client populates, and its three .dat files
    /// (VERIFIED against Client.py: maps.dat / mod.dat / modAssets.dat under
    /// mods\ArchipelagoMod). Presence of these = "mod installed".
    private const string ModFolderName = "ArchipelagoMod";
    private static readonly string[] ModDatFiles = { "mod.dat", "maps.dat", "modAssets.dat" };

    /// Default Archipelago install root on Windows (READ-ONLY — see header).
    private const string DefaultArchipelagoRoot = @"C:\ProgramData\Archipelago";

    /// The AP launcher exe (dispatches components by name) + the verified component
    /// display name to ask it to run.
    private const string ArchipelagoLauncherExe = "ArchipelagoLauncher.exe";
    private const string WargrooveComponentName = "Wargroove Client";

    /// Official Archipelago "Wargroove" setup guide.
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Wargroove/wargroove/en";

    /// Wargroove on Steam (the base game the player must own).
    private const string SteamStoreUrl =
        "https://store.steampowered.com/app/607050/Wargroove/";

    /// Archipelago releases — the Wargroove Client ships inside this download.
    private const string ApReleasesUrl =
        "https://github.com/ArchipelagoMW/Archipelago/releases";

    /// GitHub releases API for the AP repo (news feed source).
    private const string ApReleasesApiUrl =
        "https://api.github.com/repos/ArchipelagoMW/Archipelago/releases";

    private const string ArchipelagoSite = "https://archipelago.gg";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "wargroove";
    public string DisplayName => "Wargroove";
    public string Subtitle    => "Native PC · Archipelago";

    /// EXACT AP game string — VERIFIED against worlds/wargroove/__init__.py
    /// (WargrooveWorld.game = "Wargroove") and the Component registration
    /// (game_name="Wargroove"). World id "wargroove".
    public string ApWorldName => "Wargroove";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "wargroove.png");

    public string ThemeAccentColor => "#3E7CB1";   // commander-banner blue
    public string[] GameBadges     => new[] { "Requires Wargroove on Steam" };

    public string Description =>
        "Wargroove is Chucklefish's turn-based strategy game: choose your commander " +
        "and wage war across vibrant pixel-art battlefields. This is the official " +
        "Archipelago integration, played as a custom campaign. You bring your own " +
        "copy of Wargroove (owned on Steam); Archipelago's bundled \"Wargroove " +
        "Client\" installs the Archipelago mod and campaign into your game's save " +
        "folder for you, holds the multiworld connection, and relays your unlocks " +
        "and victories to the running game. The launcher detects your install, can " +
        "start the client and the game for you, and guides the rest.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    // Wargroove's AP integration is versioned by the Archipelago release the user
    // runs (the mod + client ship inside it), not by a standalone mod tag. There is
    // no independent version stamp this plugin can author honestly, so these stay
    // null (the news feed surfaces AP release versions instead).
    public string? InstalledVersion => null;
    public string? AvailableVersion => null;

    /// "Installed" == the ArchipelagoMod has been written into the %AppData% save
    /// folder (by the bundled client). Owning only the Steam base game does NOT flip
    /// it true — that copy has no AP mod until the client runs.
    public bool IsInstalled => IsModInstalled();
    public bool IsRunning   { get; private set; }

    /// Reports the detected/override Wargroove ROOT (Steam) directory when known,
    /// else "" (interface contract).
    public string GameDirectory => ResolveWargrooveRootDir() ?? "";

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained source file — same approach as the
    /// Undertale / Subnautica / Doom plugins). BOM-less UTF-8, under Games/ROMs/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "wargroove_launcher.json");

    /// %AppData%\Chucklefish\Wargroove — where the mod + save live. The .NET
    /// ApplicationData special folder maps to %AppData% (Roaming).
    private static string? AppDataWargrooveDir
    {
        get
        {
            try
            {
                string appData = Environment.GetFolderPath(
                    Environment.SpecialFolder.ApplicationData);
                if (string.IsNullOrEmpty(appData)) return null;
                return Path.Combine(appData, AppDataVendorFolder, AppDataGameFolder);
            }
            catch { return null; }
        }
    }

    /// %AppData%\Chucklefish\Wargroove\mods\ArchipelagoMod.
    private static string? ModInstallDir
    {
        get
        {
            string? root = AppDataWargrooveDir;
            return root == null ? null : Path.Combine(root, "mods", ModFolderName);
        }
    }

    // ── Internal override state (restored from sidecar) ────────────────────────

    /// User-set override of the Wargroove ROOT (Steam) folder, when auto-detect
    /// misses (Epic / non-standard Steam library). Optional.
    private string? _overrideRootDir;

    /// User-set override of the Archipelago install root (where the bundled client
    /// lives). READ-ONLY target — used only to LOCATE the client, never written.
    private string? _overrideApDir;

    private Process? _gameProcess;     // Wargroove.exe, if we launched it directly
    private Process? _clientProcess;   // the bundled Wargroove Client, if we launched it

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The external Wargroove Client owns the AP slot and relays checks/items/goal to
    // the server itself (via file IPC into the game's AP folder). The launcher relays
    // nothing. These exist only for interface compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Constructor — restore overrides ───────────────────────────────────────

    public WargroovePlugin()
    {
        try
        {
            var s = LoadSettings();
            if (!string.IsNullOrWhiteSpace(s.RootDirOverride) && Directory.Exists(s.RootDirOverride))
                _overrideRootDir = s.RootDirOverride;
            if (!string.IsNullOrWhiteSpace(s.ApInstallOverride) && Directory.Exists(s.ApInstallOverride))
                _overrideApDir = s.ApInstallOverride;
        }
        catch { /* fall back to detection only */ }
    }

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────
    // No independent version to compare (the mod + client ship inside the player's
    // AP install). Contract: never throw on network failure.

    public Task CheckForUpdateAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────
    // HONEST guided setup. There is nothing for the launcher to download here (the
    // mod + client live inside the player's Archipelago install, and the base game
    // is the player's paid Steam copy). The Wargroove Client itself installs the mod
    // when it starts. So this reports the current detection state and the exact
    // remaining steps — it never fabricates an install, and it never writes to the
    // read-only Archipelago install.

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((10, "Checking for your Steam copy of Wargroove..."));

        string? steamDir = ResolveWargrooveRootDir();
        bool    modReady = IsModInstalled();
        string? client   = ResolveBundledClient();

        if (modReady)
        {
            progress.Report((100,
                "The Archipelago Wargroove mod is installed (found in your " +
                @"%AppData%\Chucklefish\Wargroove\mods\ArchipelagoMod). To play: start " +
                "the Wargroove Client and connect (enter your server and username), " +
                "then launch Wargroove and open Story → Campaign → Custom → Archipelago. " +
                "Press Play here to start the client and the game. See the Setup Guide " +
                "link in Settings."));
            return Task.CompletedTask;
        }

        // Mod not installed yet — the bundled client installs it on start.
        var sb = new StringBuilder();
        sb.Append("Wargroove's Archipelago mod is installed by the bundled \"Wargroove ");
        sb.Append("Client\" the first time it starts — not by this launcher. ");

        if (steamDir != null)
            sb.Append("Your Steam Wargroove was detected at \"").Append(steamDir).Append("\". ");
        else
            sb.Append("Install Wargroove on Steam (appid ").Append(SteamAppId)
              .Append("), or set its folder in Settings. ");

        if (client != null)
            sb.Append("Press Play (or Install) to start the Wargroove Client — it will ")
              .Append("create the mod under %AppData%\\Chucklefish\\Wargroove\\mods. ");
        else
            sb.Append("Open the Archipelago Launcher and run \"Wargroove Client\" once ")
              .Append("to install the mod (the launcher could not locate your Archipelago ")
              .Append("install automatically — set it in Settings if needed). ");

        sb.Append("Then in Wargroove open Story → Campaign → Custom → Archipelago. ");
        sb.Append("See the Setup Guide link in Settings.");

        // Best effort: actually start the client so it installs the mod for the user.
        if (client != null)
        {
            progress.Report((60, "Starting the Wargroove Client to install the mod..."));
            try { StartBundledClient(client); }
            catch { /* surfaced via the guidance below — never fail the install */ }
        }

        progress.Report((100, sb.ToString()));
        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public Task<bool> VerifyInstallAsync(CancellationToken ct = default)
        => Task.FromResult(IsInstalled);

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker for the Wargroove ROOT folder: a valid
    /// folder contains Wargroove.exe. Return null when acceptable, else a short
    /// human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Wargroove install folder " +
                   "(the one containing Wargroove.exe).";

        if (LooksLikeWargrooveRoot(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeWargrooveRoot(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Wargroove installation. Pick the folder " +
               "that contains Wargroove.exe (for Steam this is usually " +
               @"...\steamapps\common\Wargroove).";
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────
    // Best effort, never blocks. Start the bundled Wargroove Client (it owns the AP
    // connection AND installs/refreshes the mod into %AppData%) and also open
    // Wargroove. The connection itself is entered into the client (see header), so we
    // do NOT pass connection args and we do NOT hold an ApClient on the slot
    // (ConnectsItself = true).

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        _ = session; // connection is made in the Wargroove Client, not via args here

        // 1) Start the bundled client if we can find it (read-only locate). This is
        //    what installs the mod + holds the slot. Best effort.
        string? client = ResolveBundledClient();
        if (client != null)
        {
            try { StartBundledClient(client); } catch { /* non-fatal */ }
        }

        // 2) Open Wargroove itself so the user gets one click. Best effort.
        try { StartWargroove(); } catch { /* non-fatal — the client can also be used */ }

        return Task.CompletedTask;
    }

    /// Wargroove is a complete game — plain (non-AP) play is fully supported.
    public bool SupportsStandalone => true;

    /// The external Wargroove Client owns the AP slot connection (see header). The
    /// launcher must NOT connect its own ApClient to the same slot while the client
    /// runs, or they would kick each other off.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // Plain Wargroove — do not start the AP client.
        StartWargroove();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // Kill ONLY what this launcher started: the game we opened and the bundled
        // client we launched. We never touch the user's AP server.
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        try { _clientProcess?.Kill(entireProcessTree: true); } catch { }
        _gameProcess = null;
        _clientProcess = null;
        IsRunning = false;
        // No plaintext AP password is written by this plugin (the connection is
        // entered in the Wargroove Client), so there is nothing to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The Wargroove Client receives items from the AP server directly and relays
        // them into the game via file IPC; there is nothing for the launcher to
        // forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The Wargroove Client renders its own connection state; no launcher HUD
        // channel into the game.
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
            Text = "Wargroove's Archipelago support is part of Archipelago itself, not a " +
                   "one-click mod. You bring your own Steam copy of Wargroove; Archipelago's " +
                   "bundled \"Wargroove Client\" installs the Archipelago mod and campaign " +
                   "into your %AppData%\\Chucklefish\\Wargroove folder when it starts, holds " +
                   "the multiworld connection, and relays to the running game. The launcher " +
                   "detects your install, can start the client and the game, and guides the " +
                   "rest — but you connect (server + username) inside the Wargroove Client, " +
                   "not here. Your Archipelago install is only ever READ to find the client; " +
                   "nothing is written there. Some details were verified against the AP " +
                   "client and setup guide, not byte-for-byte offline.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: Steam base game (root dir) ───────────────────────────
        panel.Children.Add(SectionHeader("WARGROOVE INSTALL (ROOT DIRECTORY)", muted));

        string? rootDir = ResolveWargrooveRootDir();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = rootDir != null
                ? "✓ Detected (appid " + SteamAppId + "):\n" + rootDir
                : "Not detected via Steam. Install Wargroove on Steam (appid " +
                  SteamAppId + "), or set the folder below (Epic / non-standard Steam " +
                  "library). The Wargroove Client needs this path to send files to the game.",
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
            ToolTip     = "Your Wargroove install folder (contains Wargroove.exe). Detected " +
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
                Title            = "Select your Wargroove install folder (contains Wargroove.exe)",
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
                    System.Windows.MessageBox.Show(bad, "Not a Wargroove folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeWargrooveRoot(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeWargrooveRoot(nested)) picked = nested;
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

        // ── Section: Mod status (%AppData%) ────────────────────────────────
        panel.Children.Add(SectionHeader("ARCHIPELAGO MOD (INSTALLED BY THE CLIENT)", muted));
        bool modReady = IsModInstalled();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modReady
                ? "✓ Mod found: " + (ModInstallDir ?? "(AppData)")
                : "Not installed yet. Start the Wargroove Client (Play, or from the " +
                  "Archipelago Launcher) — it creates the 3 .dat files under " +
                  @"%AppData%\Chucklefish\Wargroove\mods\ArchipelagoMod and the Archipelago " +
                  "campaign save.",
            FontSize = 11, Foreground = modReady ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Archipelago install / bundled client (READ-ONLY) ─────
        panel.Children.Add(SectionHeader("ARCHIPELAGO INSTALL (READ-ONLY — TO FIND THE CLIENT)", muted));

        string? apRoot = ResolveArchipelagoRoot();
        string? client = ResolveBundledClient();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = client != null
                ? "✓ Bundled client located:\n" + client
                : (apRoot != null
                    ? "Archipelago install found at \"" + apRoot + "\", but the Wargroove " +
                      "Client launcher was not located inside it. You can still run it from " +
                      "the Archipelago Launcher."
                    : "Archipelago install not found automatically. Set its folder below, " +
                      "or just run the Wargroove Client from the Archipelago Launcher."),
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
                          "). READ-ONLY — only used to locate the bundled Wargroove Client.",
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
                Title            = "Select your Archipelago install folder (read-only — to find the Wargroove Client)",
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
            Text = "This folder is never modified — the launcher only reads it to find " +
                   "the Wargroove Client launcher. If left unset, run the client from the " +
                   "Archipelago Launcher yourself.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: setup + connection steps ─────────────────────────────
        panel.Children.Add(SectionHeader("SETUP & CONNECTION", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "1) Own and install Wargroove on Steam (appid " + SteamAppId + ").\n" +
                "2) Start the \"Wargroove Client\" (press Play here, or run it from the " +
                "Archipelago Launcher). The first start installs the Archipelago mod and " +
                "campaign into %AppData%\\Chucklefish\\Wargroove for you.\n" +
                "3) In the Wargroove Client, connect to your server and enter your username " +
                "(from your options/YAML). The client holds the multiworld connection and " +
                "relays to the game.\n" +
                "4) Start Wargroove, then open Story → Campaign → Custom → Archipelago and " +
                "click Play.\n\n" +
                "(The connection is entered in the Wargroove Client — the launcher does not " +
                "connect to your slot itself. Pressing Play here starts the client and the " +
                "game; you still connect in the client.)",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Wargroove on Steam ↗",        SteamStoreUrl),
            ("Wargroove Setup Guide ↗",     SetupGuideUrl),
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
    // Wargroove's integration ships with the Archipelago release, so the most honest
    // "news" is the AP release stream (the Wargroove Client + mod come from there).
    // Never throws — empty on failure.

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(ApReleasesApiUrl, ct);
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

    // ── Private helpers — mod / AppData detection ─────────────────────────────

    /// True when the Archipelago Wargroove mod has been installed into %AppData%
    /// (the bundled client writes its 3 .dat files there). We require at least
    /// mod.dat present in mods\ArchipelagoMod (the canonical marker), tolerating
    /// the others being absent on a partial/in-progress install.
    private static bool IsModInstalled()
    {
        try
        {
            string? dir = ModInstallDir;
            if (dir == null || !Directory.Exists(dir)) return false;
            // Canonical marker: mod.dat. (All three are written together, but
            // mod.dat is the primary mod definition.)
            if (File.Exists(Path.Combine(dir, "mod.dat"))) return true;
            // Defensive: accept any of the known .dat files if mod.dat was renamed.
            foreach (string dat in ModDatFiles)
                if (File.Exists(Path.Combine(dir, dat))) return true;
            return false;
        }
        catch { return false; }
    }

    // ── Private helpers — Wargroove ROOT (Steam) detection ────────────────────

    /// The Wargroove ROOT (install) dir to use: the override (if set and valid)
    /// wins, else the Steam-detected install. Null when nothing is found.
    private string? ResolveWargrooveRootDir()
    {
        if (!string.IsNullOrWhiteSpace(_overrideRootDir) && LooksLikeWargrooveRoot(_overrideRootDir))
            return _overrideRootDir;

        try { return DetectSteamWargrooveDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Wargroove if it has Wargroove.exe.
    private static bool LooksLikeWargrooveRoot(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, PreferredGameExeName));
        }
        catch { return false; }
    }

    /// Detect the Steam Wargroove install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_607050.acf exists → steamapps\common\Wargroove.
    private static string? DetectSteamWargrooveDir()
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
                        if (LooksLikeWargrooveRoot(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeWargrooveRoot(conventional)) return conventional;
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

    // ── Private helpers — Archipelago install + bundled client (READ-ONLY) ────

    /// The Archipelago install root to read: the override (if set) wins, else the
    /// default C:\ProgramData\Archipelago when it exists, else common alternates
    /// (per-user installs). Null when nothing is found. READ-ONLY — never written.
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

    /// Locate the bundled client launcher inside the (read-only) Archipelago install:
    /// prefer a "*wargroove*client*.exe" if one exists, else ArchipelagoLauncher.exe
    /// (which dispatches the "Wargroove Client" component by name). Returns the exe
    /// path or null. Only READS the install tree — never writes.
    private string? ResolveBundledClient()
    {
        try
        {
            string? root = ResolveArchipelagoRoot();
            if (root == null || !Directory.Exists(root)) return null;

            // 1) A dedicated Wargroove client exe, if the install ships one.
            //    Search the root (top-level) first — AP keeps its component exes
            //    next to ArchipelagoLauncher.exe.
            string? wargrooveClient = FindExe(root,
                name => name.Contains("wargroove") && name.Contains("client"));
            if (wargrooveClient != null) return wargrooveClient;

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

    /// True when the resolved client exe is the GENERIC AP launcher (so we must pass
    /// it the component name), as opposed to a dedicated Wargroove client exe.
    private static bool IsGenericApLauncher(string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath).ToLowerInvariant();
        return name.Contains("launcher") && !name.Contains("wargroove");
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

    /// Start the bundled Wargroove Client. If it is the generic AP launcher, pass the
    /// "Wargroove Client" component name so it dispatches the right component. Tracks
    /// the process so StopAsync can end it (we never touch the user's AP server).
    private void StartBundledClient(string clientExe)
    {
        var psi = new ProcessStartInfo
        {
            FileName         = clientExe,
            WorkingDirectory = Path.GetDirectoryName(clientExe) ?? "",
            UseShellExecute  = false,
        };
        if (IsGenericApLauncher(clientExe))
            psi.Arguments = Quote(WargrooveComponentName); // ArchipelagoLauncher "Wargroove Client"

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

    /// Start Wargroove: prefer Wargroove.exe in the detected/override root; if that
    /// cannot be found but Steam is present, fall back to steam://rungameid/607050.
    /// Surfaces a clear message rather than failing opaquely.
    private void StartWargroove()
    {
        string? root = ResolveWargrooveRootDir();
        string? exe  = root != null ? Path.Combine(root, PreferredGameExeName) : null;

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

        // Fall back to Steam if we at least know Steam is installed.
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
            "Could not find Wargroove.exe. Open this game's Settings and pick your " +
            "Wargroove install folder, or install Wargroove via Steam (appid " +
            SteamAppId + ").",
            PreferredGameExeName);
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

    /// Quote an argument for a Windows command line (wrap in double quotes and
    /// escape embedded quotes). Plain tokens are returned unquoted.
    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        bool needs = value.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0;
        if (!needs) return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // Kept in this plugin's OWN JSON file (does not modify Core/SettingsStore).
    // BOM-less UTF-8, read-modify-write (same approach as Undertale / Subnautica /
    // Doom). Stores the Wargroove root override and the (read-only) Archipelago
    // install override.

    private sealed class WargrooveSettings
    {
        /// The Wargroove install (root) folder the user pointed us at.
        public string? RootDirOverride { get; set; }

        /// The Archipelago install folder the user pointed us at — READ-ONLY,
        /// used only to locate the bundled Wargroove Client.
        public string? ApInstallOverride { get; set; }
    }

    private WargrooveSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<WargrooveSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(WargrooveSettings s)
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

    private void SaveApInstallOverride(string dir)
    {
        var s = LoadSettings();
        s.ApInstallOverride = dir;
        SaveSettings(s);
    }
}
