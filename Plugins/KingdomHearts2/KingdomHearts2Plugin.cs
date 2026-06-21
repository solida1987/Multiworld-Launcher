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
// The launcher project sets BOTH <UseWPF>true</UseWPF> and
// <UseWindowsForms>true</UseWindowsForms>. That makes a long list of simple type
// names ambiguous between WPF and WinForms (Button, TextBox, Color, Brush(es),
// MessageBox, FontWeights, HorizontalAlignment, Thickness, Application, Clipboard,
// Cursors, OpenFileDialog, …) → CS0104. To avoid that, this file does NOT do
// `using System.Windows;` / `using System.Windows.Controls;` /
// `using System.Windows.Media;` — every WPF UI type below is written FULLY
// QUALIFIED (System.Windows.*, System.Windows.Controls.*, System.Windows.Media.*).
// GlobalUsings.cs already aliases the short names project-wide, so this file must
// NOT add any `using X = System.Windows...;` of its own (that would be CS1537,
// "duplicate alias").

namespace LauncherV2.Plugins.KingdomHearts2;

// ═══════════════════════════════════════════════════════════════════════════════
// KingdomHearts2Plugin — detect / guide / launch for "Kingdom Hearts II" (Final
// Mix, part of KINGDOM HEARTS HD 1.5+2.5 ReMIX on PC) played through its OFFICIAL
// Archipelago integration.
//
// WHAT KIND OF INTEGRATION IS THIS? (verified online this session — see REALITY
// CHECK) — NATIVE "ConnectsItself", but an honestly GUIDED case in the Undertale /
// Witness family: the AP slot is held by a SEPARATE long-running client that ships
// INSIDE Archipelago itself (not the game process, not the launcher), and the base
// game is the player's own paid copy under a multi-mod stack the launcher cannot
// install for them.
//
// ── HONEST REALITY CHECK (2026-06-14, verified against AP-main + the official
//    KH2 Archipelago setup guide) ────────────────────────────────────────────────
//   * THE AP WORLD game string is "Kingdom Hearts 2" — VERIFIED against
//     worlds/kh2/__init__.py (`game = "Kingdom Hearts 2"`,
//     required_client_version = (0, 4, 4)). Kingdom Hearts 2 is a CORE Archipelago
//     world — it ships inside Archipelago itself, no custom apworld drop needed.
//     GameId here = "kh2".
//
//   * THE CLIENT is the "KH2 Client" — a Python client SHIPPED INSIDE the
//     Archipelago release the player already has. It is registered in
//     worlds/kh2/__init__.py as Component("KH2 Client", "KH2Client") and is launched
//     by running Archipelago's own ArchipelagoLauncher.exe and selecting "KH2
//     Client". It owns the AP slot connection and AUTO-HOOKS the running game once
//     connected ("When you successfully connect to the server the client will
//     automatically hook into the game to send/receive checks"). It is NOT a
//     standalone downloadable client exe. Because it holds the slot, the launcher
//     must NOT also sit an ApClient on that slot — hence ConnectsItself = true.
//
//   * CONNECTION is entered IN THE KH2 CLIENT'S GUI (the AP text-client window):
//     you start a NEW save and enter the Garden of Assemblage, run
//     ArchipelagoLauncher.exe → "KH2 Client", then type the room's host:port into
//     the top box and press Connect, following the prompts for slot name and
//     (optional) password. There is NO connection command-line arg and NO config
//     file on the GAME this launcher can pre-write (verified against the setup
//     guide). So this plugin does NOT attempt a connection prefill — it GUIDES, and
//     surfaces the session host/slot so the player can copy them into the client.
//
//   * THE SEED GENERATOR is tommadness/KH2Randomizer (its release ships a single
//     "KH2.Randomizer.exe", ~155 MB; latest verified live tag v3.2.2, 2026-04-24).
//     IMPORTANT NUANCE the guide is explicit about: you do NOT generate the playable
//     seed with the standalone generator — seeds are generated THROUGH ARCHIPELAGO
//     (the website / ArchipelagoGenerate). KH2.Randomizer.exe is still the tool the
//     guide uses for the one-time Lua Backend setup and is the project's hub, so
//     this plugin offers to download it (a real, honest install of THAT exe) as a
//     convenience, while making the "generate through Archipelago" rule plain. The
//     base game is never downloaded.
//
//   * BASE GAME — the player's own paid copy of KINGDOM HEARTS HD 1.5+2.5 ReMIX
//     (which contains Kingdom Hearts II Final Mix):
//       · Steam: appid 2552430 (KINGDOM HEARTS HD 1.5+2.5 ReMIX), supported build
//         15194255.
//       · Epic Games Store: "Epic Games Version 1.0.0.10_WW".
//     The launcher DETECTS both (Steam via registry→libraryfolders→appmanifest;
//     Epic via the read-only %ProgramData%\Epic\...\Manifests\*.item JSON files) but
//     NEVER modifies either install or the Epic manifests (§11).
//
//   * MOD STACK (the player installs these via OpenKH Mod Manager — the launcher
//     cannot do this for them, and is honest about it): OpenKH Mod Manager
//     (25.03.16.0+) with Panacea, GoA ROM (KH2FM-Mods-Num/GoA-ROM-Edition),
//     APCompanion (JaredWeakStrike/APCompanion), and KH2-ArchipelagoEnablers
//     (TopazTK/KH2-ArchipelagoEnablers), plus the .zip seed Archipelago produced.
//
// ── WHAT THIS PLUGIN HONESTLY DOES ────────────────────────────────────────────
//   1. DETECT the base game:
//        · Steam appid 2552430 via the Windows registry → libraryfolders.vdf →
//          appmanifest_2552430.acf → steamapps\common\<installdir> pipeline (same
//          as the Subnautica / Undertale plugins).
//        · Epic Games Store by scanning the READ-ONLY manifest JSONs at
//          %ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item for a
//          KINGDOM HEARTS HD 1.5+2.5 ReMIX entry and reading its InstallLocation.
//      A manual install-dir OVERRIDE (Settings folder picker) takes precedence and
//      is validated + persisted in this plugin's OWN sidecar
//      (Games/ROMs/kh2/kh2_launcher.json) — Core/SettingsStore is NOT modified.
//   2. INSTALL/UPDATE (optional, best effort) = download the KH2 seed-generator /
//      setup tool (KH2.Randomizer.exe) from tommadness/KH2Randomizer's GitHub
//      release into the launcher's own folder (latest tag, pinned v3.2.2 fallback
//      when the API is unreachable). This is a genuine download of that single exe;
//      it is the project's setup hub (Lua Backend setup). It does NOT install the
//      base game or the OpenKH mod stack — those remain guided. The Install step
//      ALSO emits the exact remaining manual steps so it never fabricates a
//      "ready-to-play" state.
//   3. LAUNCH (AP) = best effort: open the base game so the player gets a one-click
//      launch — Steam protocol (steam://rungameid/2552430) when Steam is present,
//      else the Epic launcher protocol for the detected Epic title, else the
//      detected exe. The CONNECTION itself is entered into the separate KH2 Client
//      (see header), so no prefill is attempted and the launcher does NOT hold an
//      ApClient on the slot. ConnectsItself = true. SupportsStandalone = true (the
//      game plays fine without AP). Inert ReceiveItems / OnApStateChanged.
//   4. GUIDE the irreducible steps + links (official setup guide, KH2Randomizer
//      repo, the mod repos, Steam/Epic store pages, archipelago.gg) in Settings.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", SoH/Undertale/Witness-style) ─
//   * "Installed" here is judged by the presence of the downloaded KH2.Randomizer.exe
//     setup tool in the launcher's own folder (or a stamped version) — i.e. the
//     AP-specific piece this launcher manages. The base game being present (Steam /
//     Epic) is DETECTED and reported, but does not by itself flip "installed",
//     because the launcher does not own that paid copy or its mod stack.
//   * RELEASE ASSET: the latest KH2Randomizer release's sole asset was verified to
//     be "KH2.Randomizer.exe". ResolveLatestApp picks the first Windows .exe asset
//     (fuzzy, excluding setup/helper exes); the pinned fallback URL targets that
//     exact name at v3.2.2. Re-verify on a repo bump.
//   * Steam / Epic detection are tolerant scans; any failure degrades to "not
//     detected" rather than throwing. The Epic manifests are read-only — this plugin
//     never writes to %ProgramData%\Epic.
//   * No plaintext AP password is ever written by this plugin (the connection is
//     entered in the KH2 Client's GUI), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class KingdomHearts2Plugin : IGamePlugin
{
    // ── Constants — the KH2 seed-generator / setup tool (real repo, verified) ──
    private const string APP_OWNER = "tommadness";
    private const string APP_REPO  = "KH2Randomizer";
    private const string AppRepoUrl = $"https://github.com/{APP_OWNER}/{APP_REPO}";
    private const string GH_APP_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{APP_OWNER}/{APP_REPO}/releases/latest";
    private const string GH_APP_RELEASES_URL =
        $"https://api.github.com/repos/{APP_OWNER}/{APP_REPO}/releases";

    /// Official Archipelago "Kingdom Hearts 2" setup guide (the KH2Rando-hosted one
    /// the AP world's setup doc points at).
    private const string SetupGuideUrl =
        "https://tommadness.github.io/KH2Randomizer/setup/Archipelago/";
    /// Mirror of the same guide on archipelago.gg.
    private const string ApSetupGuideUrl =
        "https://archipelago.gg/tutorial/Kingdom%20Hearts%202/setup_en";

    // Base game — KINGDOM HEARTS HD 1.5+2.5 ReMIX.
    private const string SteamAppId = "2552430";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";
    private const string SteamStoreUrl =
        "https://store.steampowered.com/app/2552430/KINGDOM_HEARTS_HD_1525_ReMIX/";
    private const string EpicStoreUrl =
        "https://store.epicgames.com/en-US/p/kingdom-hearts-15-and-25";

    /// Conventional Steam install sub-folder name for the ReMIX collection.
    private const string SteamCommonFolderName = "KINGDOM HEARTS HD 1.5+2.5 ReMIX";

    /// The KH2 Final Mix game exe inside the ReMIX install (used to validate a
    /// picked folder and as a last-resort direct launch). The collection ships
    /// per-title exes; KH2 Final Mix is "KINGDOM HEARTS II FINAL MIX.exe".
    private const string Kh2ExeName = "KINGDOM HEARTS II FINAL MIX.exe";

    /// The mod stack the player installs via OpenKH Mod Manager (guided, not
    /// automated). Surfaced as links so the player can fetch each piece.
    private const string OpenKhSite      = "https://openkh.dev/";
    private const string GoaRomRepo      = "https://github.com/KH2FM-Mods-Num/GoA-ROM-Edition";
    private const string ApCompanionRepo = "https://github.com/JaredWeakStrike/APCompanion";
    private const string EnablersRepo    = "https://github.com/TopazTK/KH2-ArchipelagoEnablers";
    private const string ArchipelagoReleasesUrl =
        "https://github.com/ArchipelagoMW/Archipelago/releases";

    /// Pinned fallback for the seed-generator/setup tool when the GitHub API is
    /// unreachable. Tag v3.2.2 verified live 2026-06-14; the single asset is the
    /// .exe below. The API path is the normal route; this is the safety net.
    private const string FallbackVersion = "3.2.2";
    private const string GeneratorExeName = "KH2.Randomizer.exe";
    private static readonly string FallbackExeUrl =
        $"{AppRepoUrl}/releases/download/v{FallbackVersion}/{GeneratorExeName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30), // the generator exe is ~155 MB
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp for the downloaded setup tool.
    private const string VersionFileName = "kh2_generator_version.dat";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "kh2";
    public string DisplayName => "Kingdom Hearts II";
    public string Subtitle    => "Native PC · Archipelago";

    /// EXACT AP game string — VERIFIED against worlds/kh2/__init__.py
    /// (`game = "Kingdom Hearts 2"`; required_client_version = (0, 4, 4)).
    public string ApWorldName => "Kingdom Hearts 2";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "kh2.png");

    public string ThemeAccentColor => "#1B3A8C";   // Kingdom Key / Sora blue
    public string[] GameBadges     => new[] { "Requires KH 1.5+2.5 ReMIX" };

    public string Description =>
        "Kingdom Hearts II Final Mix, played through its official Archipelago " +
        "integration. Sora's abilities, key items, magic, drive forms, summons and " +
        "world progression are shuffled into the multiworld. You bring your own copy " +
        "of KINGDOM HEARTS HD 1.5+2.5 ReMIX on PC (Epic Games Store or Steam) — which " +
        "contains KH2 Final Mix — and set up the randomizer's mod stack with the " +
        "OpenKH Mod Manager (Panacea, the GoA ROM, and the Archipelago enabler mods). " +
        "Archipelago's bundled \"KH2 Client\" connects to the multiworld and hooks the " +
        "running game automatically. The launcher detects your install, can download " +
        "the KH2 randomizer setup tool, guides the mod setup, and launches the game " +
        "for you; you connect from the KH2 Client window. Seeds are generated through " +
        "Archipelago, not the standalone generator.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means we have the KH2 randomizer setup tool (KH2.Randomizer.exe)
    /// downloaded in our own folder — the AP-specific piece this launcher manages.
    /// The base game (Steam/Epic) being present is detected + reported separately and
    /// does NOT by itself flip this true.
    public bool IsInstalled => ResolveGeneratorExe() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin downloads the KH2 randomizer setup tool. The base game
    /// lives in its own Steam/Epic install, which we only detect. Exposed as
    /// GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "KH2Randomizer");

    /// Preferred setup-tool exe path inside GameDirectory.
    private string PreferredGeneratorExePath
        => Path.Combine(GameDirectory, GeneratorExeName);

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained source file — same approach as the
    /// Witness / Subnautica / Undertale plugins). BOM-less UTF-8.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "kh2_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;   // the base game, if we launched the exe directly
    private bool     _epicDetected;  // set during detection so launch can prefer Epic
    private string?  _epicAppName;   // Epic AppName (for com.epicgames.launcher://apps/<AppName>)

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The bundled KH2 Client owns the AP slot and reports checks/items/goal to the
    // server itself (it hooks the running game). The launcher relays nothing. These
    // exist only for interface compatibility (ConnectsItself = true).
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
    // Best-effort, HONEST. The only thing the launcher can genuinely download here
    // is the KH2 randomizer setup tool (KH2.Randomizer.exe). The base game is the
    // player's paid Steam/Epic copy, and the OpenKH mod stack + the AP "KH2 Client"
    // live elsewhere — so this also reports detection state and the exact remaining
    // manual steps. It never fabricates a ready-to-play install.

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 1. Resolve the latest setup-tool release (pinned fallback when offline).
        progress.Report((3, "Checking the latest KH2 randomizer setup tool release..."));
        var (version, exeUrl, exeName) = await ResolveLatestAppAsync(ct);
        AvailableVersion = version;

        // 2. Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, BuildReadyNote(version)));
            return;
        }

        if (exeUrl == null)
            throw new InvalidOperationException(
                "Could not find the KH2 randomizer setup tool (KH2.Randomizer.exe) " +
                "download on GitHub. Check your internet connection, or get it manually " +
                "from " + AppRepoUrl + "/releases/latest. Remember you also need your own " +
                "copy of KINGDOM HEARTS HD 1.5+2.5 ReMIX (Epic or Steam, appid " +
                SteamAppId + ") and the OpenKH mod stack — see the Setup Guide in Settings.");

        // 3. Download the setup tool exe into our own folder.
        await DownloadGeneratorAsync(exeUrl, exeName ?? GeneratorExeName, version, progress, ct);

        // 4. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        // 5. Honest closing note — base game + mods + connection are not automated.
        progress.Report((100, BuildReadyNote(version)));
    }

    /// The post-install / up-to-date message: factual about what is automated and
    /// what the player still has to do, including the detected base-game location.
    private string BuildReadyNote(string version)
    {
        string baseLine = DescribeDetectedBaseGame();
        return
            $"KH2 randomizer setup tool {version} downloaded. " + baseLine +
            " Next: in the OpenKH Mod Manager add the GoA ROM, APCompanion and the " +
            "KH2-ArchipelagoEnablers mods (plus the .zip seed you generate THROUGH " +
            "Archipelago), and do the one-time Lua Backend setup. To play: start a new " +
            "save and enter the Garden of Assemblage, then run Archipelago's " +
            "ArchipelagoLauncher.exe and pick \"KH2 Client\"; enter your room's host:port, " +
            "slot name and password there and press Connect (it hooks the game " +
            "automatically). See the Setup Guide link in Settings — seeds are generated " +
            "through Archipelago, not the standalone generator.";
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── AutoMod-style validation of a user-picked base-game folder ────────────

    /// Used by the Settings folder picker: a valid KH 1.5+2.5 ReMIX folder contains
    /// the KH2 Final Mix exe (or, defensively, any "*KINGDOM HEARTS II*.exe" /
    /// "*KH2*.exe"). Return null when acceptable, else a short reason so they can
    /// pick again.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your KINGDOM HEARTS HD 1.5+2.5 " +
                   "ReMIX install folder.";

        if (LooksLikeKhRemixDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeKhRemixDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a KINGDOM HEARTS HD 1.5+2.5 ReMIX install. " +
               "Pick the folder that contains the game's executable (the Steam version " +
               "is normally in steamapps\\common\\" + SteamCommonFolderName + ").";
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: the AP server connection is entered into the bundled KH2 Client's
        // GUI (run ArchipelagoLauncher.exe → "KH2 Client", type host:port, slot,
        // password, Connect; it then hooks the game). There is no command-line /
        // config prefill on the GAME we can apply (verified — see header). So this
        // just starts the base game for convenience; the player starts the KH2 Client
        // and connects there with the session credentials (surfaced in Settings).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the KH2 Client is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartBaseGame();
        return Task.CompletedTask;
    }

    /// Kingdom Hearts II is a complete game — plain (non-AP) play is supported.
    public bool SupportsStandalone => true;

    /// The bundled KH2 Client owns the slot connection (see header). The launcher
    /// must NOT connect its own ApClient to the same slot while the client runs.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartBaseGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started the base game from here (the KH2 Client + Steam/Epic
        // own their own processes). Kill what we directly launched.
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        _gameProcess = null;
        // No plaintext AP password is ever written to disk by this plugin (the
        // connection is entered in the KH2 Client), so there is nothing to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The bundled KH2 Client receives items from the AP server directly and hooks
        // them into the running game; there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The KH2 Client renders its own connection state in its window.
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
            Text = "Kingdom Hearts II's Archipelago support uses Archipelago's own " +
                   "bundled \"KH2 Client\" (run ArchipelagoLauncher.exe and pick \"KH2 " +
                   "Client\"); it connects to the multiworld and hooks the running game. " +
                   "You bring your own copy of KINGDOM HEARTS HD 1.5+2.5 ReMIX (Epic Games " +
                   "Store or Steam, appid " + SteamAppId + "), which contains KH2 Final Mix, " +
                   "and set up the randomizer's mod stack with the OpenKH Mod Manager " +
                   "(Panacea, GoA ROM, and the Archipelago enabler mods). The launcher " +
                   "detects your install, can download the KH2 randomizer setup tool, and " +
                   "launches the game — but you connect inside the KH2 Client window, and " +
                   "you generate seeds THROUGH Archipelago (not the standalone generator). " +
                   "These external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: KH2 randomizer setup tool (download status) ──────────
        panel.Children.Add(SectionHeader("KH2 RANDOMIZER SETUP TOOL (KH2.Randomizer.exe)", muted));
        string? genExe = ResolveGeneratorExe();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = genExe != null
                ? "✓ Downloaded: " + genExe
                : "Not downloaded yet (use Install on the Play tab, or get it from the " +
                  "KH2Randomizer repo below). It is the project's setup hub for the one-time " +
                  "Lua Backend setup. (Reminder: generate seeds through Archipelago, not this " +
                  "tool's standalone generator.)",
            FontSize = 11, Foreground = genExe != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: base game (detected / override) ──────────────────────
        panel.Children.Add(SectionHeader("KINGDOM HEARTS HD 1.5+2.5 ReMIX (BASE GAME)", muted));

        string? overrideDir = LoadOverrideDir();
        string? steamDir    = DetectSteamInstallDir();
        string? epicDir     = DetectEpicInstallDir(out string? epicName);
        string? baseDir     = ResolveBaseGameDir();
        string  detectMsg;
        if (baseDir != null && overrideDir != null && string.Equals(baseDir, overrideDir, StringComparison.OrdinalIgnoreCase))
            detectMsg = "✓ Using your selected folder: " + baseDir;
        else if (steamDir != null)
            detectMsg = "✓ Detected Steam install (appid " + SteamAppId + "): " + steamDir;
        else if (epicDir != null)
            detectMsg = "✓ Detected Epic Games install: " + epicDir;
        else
            detectMsg = "Base game not detected. Pick your install folder below, or install " +
                        "KINGDOM HEARTS HD 1.5+2.5 ReMIX via Steam or the Epic Games Store first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = (baseDir != null) ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? steamDir ?? epicDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your KINGDOM HEARTS HD 1.5+2.5 ReMIX install folder (the one " +
                          "containing the KH2 Final Mix executable). Detected from Steam / Epic " +
                          "automatically; set it here to override a non-standard location.",
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
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select your KINGDOM HEARTS HD 1.5+2.5 ReMIX install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? steamDir ?? epicDir ?? "")
                                   ? (overrideDir ?? steamDir ?? epicDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a KH 1.5+2.5 ReMIX folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeKhRemixDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeKhRemixDir(nested)) picked = nested;
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
            Text = "Steam (appid " + SteamAppId + ") and Epic Games installs are detected " +
                   "automatically. Use this picker only for a non-standard location.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: setup + connection steps ─────────────────────────────
        panel.Children.Add(SectionHeader("SETUP & CONNECTION", muted));
        foreach (string step in new[]
        {
            "1. Own and install KINGDOM HEARTS HD 1.5+2.5 ReMIX (Epic Games Store or Steam, " +
                "appid " + SteamAppId + "). It contains Kingdom Hearts II Final Mix.",
            "2. Install the OpenKH Mod Manager (25.03.16.0+) and set up Panacea (see the " +
                "Setup Guide). Use Install on the Play tab to download the KH2 randomizer " +
                "setup tool and do the one-time Lua Backend setup.",
            "3. In the OpenKH Mod Manager add the GoA ROM (KH2FM-Mods-Num/GoA-ROM-Edition), " +
                "APCompanion (JaredWeakStrike/APCompanion — second-highest priority, below " +
                "the seed), and KH2-ArchipelagoEnablers (TopazTK/KH2-ArchipelagoEnablers, or " +
                "the Lite variant). Then build/patch your mods.",
            "4. Generate your seed THROUGH Archipelago (the website or your host), add the " +
                "resulting .zip seed mod at the TOP priority in the Mod Manager, and rebuild.",
            "5. Start a NEW save and enter the Garden of Assemblage (Play here launches the " +
                "game).",
            "6. Run Archipelago's ArchipelagoLauncher.exe and pick \"KH2 Client\". Enter your " +
                "room's host:port in the top box and press Connect, then follow the prompts for " +
                "slot name and password. The client hooks the game automatically. (This " +
                "launcher cannot pre-fill the connection — it is entered in the KH2 Client.)",
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
            ("KH2 Archipelago Setup Guide ↗",        SetupGuideUrl),
            ("KH2 Setup Guide (archipelago.gg) ↗",   ApSetupGuideUrl),
            ("KH2 Randomizer (GitHub) ↗",            AppRepoUrl),
            ("OpenKH Mod Manager ↗",                 OpenKhSite),
            ("GoA ROM mod ↗",                        GoaRomRepo),
            ("APCompanion mod ↗",                    ApCompanionRepo),
            ("KH2-ArchipelagoEnablers mod ↗",        EnablersRepo),
            ("Archipelago Releases ↗",               ArchipelagoReleasesUrl),
            ("KH 1.5+2.5 ReMIX on Steam ↗",          SteamStoreUrl),
            ("KH 1.5+2.5 ReMIX on Epic ↗",           EpicStoreUrl),
            ("Archipelago Official ↗",               "https://archipelago.gg"),
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

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // KH2 randomizer releases are the AP-relevant news for this game.
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

    /// "v3.2.2" → "3.2.2" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest setup-tool release: version + the .exe download URL + the
    /// exe asset name. Falls back to the pinned v3.2.2 direct URL when the API is
    /// unreachable.
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
                string? preferred = null;   // an .exe whose name mentions kh2/rando
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
                        (lower.Contains("kh2") || lower.Contains("rando") ||
                         lower.Contains("kingdom")))
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

        return (FallbackVersion, FallbackExeUrl, GeneratorExeName);
    }

    // ── Private helpers — setup-tool exe resolution ───────────────────────────

    /// The downloaded setup-tool exe: prefer the canonical name, else any
    /// "*kh2*.exe" / "*rando*.exe" in our folder (defensive). Null if absent.
    private string? ResolveGeneratorExe()
    {
        try
        {
            if (File.Exists(PreferredGeneratorExePath)) return PreferredGeneratorExePath;
            if (!Directory.Exists(GameDirectory)) return null;

            foreach (string exe in Directory.EnumerateFiles(GameDirectory, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (IsHelperExe(name)) continue;
                if (name.Contains("kh2") || name.Contains("rando") || name.Contains("kingdom"))
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

    /// Names that are NOT the runnable setup tool (installers, helpers).
    private static bool IsHelperExe(string nameLowerNoExt)
        => nameLowerNoExt.Contains("unins")    ||
           nameLowerNoExt.Contains("setup")    ||
           nameLowerNoExt.Contains("crash")    ||
           nameLowerNoExt.Contains("vcredist") ||
           nameLowerNoExt.Contains("dxsetup");

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start the base game: prefer Steam's protocol launch (so Steam's overlay /
    /// cloud saves engage) when Steam is present; else the Epic launcher protocol
    /// for the detected Epic title; else the detected/override install's exe. Best
    /// effort — surfaces a clear message rather than failing opaquely. Sets
    /// IsRunning so the tile reflects an active session even when Steam/Epic owns
    /// the process.
    private void StartBaseGame()
    {
        // Refresh Epic detection so we have the AppName for the protocol URL.
        DetectEpicInstallDir(out _);

        // Prefer Steam protocol launch when a Steam install is detected for this app.
        string? steamDir = DetectSteamInstallDir();
        if (steamDir != null &&
            SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // Steam owns the process; we won't track exit
                return;
            }
            catch { /* fall through */ }
        }

        // Epic protocol launch when the Epic title is detected.
        if (_epicDetected && !string.IsNullOrWhiteSpace(_epicAppName))
        {
            try
            {
                string epicUrl =
                    $"com.epicgames.launcher://apps/{_epicAppName}?action=launch&silent=true";
                Process.Start(new ProcessStartInfo(epicUrl) { UseShellExecute = true });
                IsRunning = true; // Epic owns the process
                return;
            }
            catch { /* fall through */ }
        }

        // Fall back to launching the detected/override install's KH2 exe directly.
        string? dir = ResolveBaseGameDir();
        string? exe = dir != null ? FindKh2ExeIn(dir) : null;
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
                return;
            }
            catch { /* fall through to the error below */ }
        }

        throw new FileNotFoundException(
            "Could not launch KINGDOM HEARTS HD 1.5+2.5 ReMIX. Open this game's Settings " +
            "and pick your install folder, or install the game via Steam (appid " +
            SteamAppId + ") or the Epic Games Store. Then start the game, run Archipelago's " +
            "ArchipelagoLauncher.exe and pick \"KH2 Client\" to connect.",
            Kh2ExeName);
    }

    // ── Private helpers — base-game detection (Steam + Epic + override) ───────

    /// The base-game install dir to use: the override (if set and valid) wins, else
    /// the Steam-detected install, else the Epic-detected install. Null otherwise.
    private string? ResolveBaseGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeKhRemixDir(ov))
            return ov;

        try
        {
            string? steam = DetectSteamInstallDir();
            if (steam != null) return steam;
        }
        catch { /* ignore */ }

        try { return DetectEpicInstallDir(out _); }
        catch { return null; }
    }

    /// Describe the detected base-game location for status messages.
    private string DescribeDetectedBaseGame()
    {
        string? steam = DetectSteamInstallDir();
        if (steam != null)
            return $"Detected your Steam copy of KH 1.5+2.5 ReMIX at \"{steam}\".";
        string? epic = DetectEpicInstallDir(out _);
        if (epic != null)
            return $"Detected your Epic Games copy of KH 1.5+2.5 ReMIX at \"{epic}\".";
        return "Reminder: you need your own copy of KINGDOM HEARTS HD 1.5+2.5 ReMIX " +
               "(Epic Games Store or Steam, appid " + SteamAppId + ").";
    }

    /// A folder "looks like" the ReMIX collection if it has the KH2 Final Mix exe
    /// (or, defensively, any "*kingdom hearts ii*.exe" / "*kh2*.exe").
    private static bool LooksLikeKhRemixDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, Kh2ExeName))) return true;
            return FindKh2ExeIn(dir) != null;
        }
        catch { return false; }
    }

    /// Find the KH2 Final Mix exe in `dir`: prefer the canonical name, else a fuzzy
    /// match on "kingdom hearts ii" / "kh2". Null if none.
    private static string? FindKh2ExeIn(string dir)
    {
        try
        {
            string preferred = Path.Combine(dir, Kh2ExeName);
            if (File.Exists(preferred)) return preferred;

            foreach (string exe in Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (name.Contains("kingdom hearts ii") || name.Contains("kingdom hearts 2") ||
                    name == "kh2" || name.StartsWith("kh2"))
                    return exe;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    /// Detect the Steam ReMIX install: read the Steam root from the registry, gather
    /// all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_2552430.acf exists → steamapps\common\<installdir>. Never throws.
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
                        string manifest  = Path.Combine(steamapps, $"appmanifest_{SteamAppId}.acf");
                        if (!File.Exists(manifest)) continue;

                        string common = Path.Combine(steamapps, "common");
                        string? installDir = ReadAcfInstallDir(manifest);
                        if (installDir != null)
                        {
                            string candidate = Path.Combine(common, installDir);
                            if (Directory.Exists(candidate)) return candidate;
                        }
                        string conventional = Path.Combine(common, SteamCommonFolderName);
                        if (Directory.Exists(conventional)) return conventional;
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

    /// Read the "installdir" value from an appmanifest_*.acf. Returns null if absent.
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

    /// Detect the Epic Games Store install of KH 1.5+2.5 ReMIX by scanning the
    /// READ-ONLY launcher manifests at
    /// %ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item. Each .item is a
    /// JSON file with "DisplayName", "InstallLocation", and "AppName". We match a
    /// KINGDOM HEARTS 1.5/2.5 ReMIX DisplayName (tolerant) and return its
    /// InstallLocation. NEVER writes to the manifests (§11). Never throws.
    /// `appName` receives the Epic AppName for the launch protocol URL (or null).
    private string? DetectEpicInstallDir(out string? appName)
    {
        appName = null;
        _epicDetected = false;
        _epicAppName  = null;
        try
        {
            string? programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(programData)) return null;

            string manifestsDir = Path.Combine(
                programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (!Directory.Exists(manifestsDir)) return null;

            foreach (string item in Directory.EnumerateFiles(manifestsDir, "*.item", SearchOption.TopDirectoryOnly))
            {
                string text;
                try { text = File.ReadAllText(item); }
                catch { continue; }

                if (string.IsNullOrWhiteSpace(text)) continue;

                string? display;
                string? install;
                string? app;
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    var root = doc.RootElement;
                    display = root.TryGetProperty("DisplayName", out var dn) ? dn.GetString() : null;
                    install = root.TryGetProperty("InstallLocation", out var il) ? il.GetString() : null;
                    app     = root.TryGetProperty("AppName", out var an) ? an.GetString() : null;
                }
                catch { continue; }

                if (string.IsNullOrWhiteSpace(display) || string.IsNullOrWhiteSpace(install))
                    continue;

                if (!LooksLikeKhRemixDisplayName(display!)) continue;

                // Epic stores InstallLocation with forward slashes — normalize.
                string norm = install!.Replace('/', '\\').TrimEnd('\\');
                if (!Directory.Exists(norm)) continue;

                appName       = string.IsNullOrWhiteSpace(app) ? null : app;
                _epicAppName  = appName;
                _epicDetected = true;
                return norm;
            }
        }
        catch { /* manifests unreadable — not detected */ }
        return null;
    }

    /// Tolerant match for the ReMIX collection's Epic DisplayName. Matches strings
    /// that mention KINGDOM HEARTS and the "1.5"/"2.5" ReMIX bundle (covering minor
    /// punctuation variants in the manifest's DisplayName).
    private static bool LooksLikeKhRemixDisplayName(string display)
    {
        string d = display.ToLowerInvariant();
        if (!d.Contains("kingdom hearts")) return false;
        bool remix = d.Contains("remix") || d.Contains("1.5") || d.Contains("2.5");
        return remix;
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

    // ── Private helpers — download the setup-tool exe ─────────────────────────

    /// Download the KH2 randomizer setup tool's single .exe asset directly into
    /// GameDirectory. Normalises the on-disk name to the canonical one so
    /// ResolveGeneratorExe finds it deterministically (and updates overwrite cleanly).
    private async Task DownloadGeneratorAsync(
        string exeUrl,
        string exeName,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(GameDirectory);

        string destExe = Path.Combine(GameDirectory, GeneratorExeName);
        string tempExe = Path.Combine(GameDirectory,
            $".{Path.GetFileNameWithoutExtension(GeneratorExeName)}-{Guid.NewGuid():N}.part");

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
                        progress.Report((pct, $"Downloading the KH2 setup tool... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((95, "Finishing up..."));
            try { if (File.Exists(destExe)) File.Delete(destExe); } catch { }
            File.Move(tempExe, destExe, overwrite: true);
            progress.Report((98, "KH2 setup tool downloaded."));
        }
        finally
        {
            try { if (File.Exists(tempExe)) File.Delete(tempExe); } catch { }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the base-game install-dir
    // override) in its OWN JSON file so it stays a single self-contained source file
    // and does not modify Core/SettingsStore. BOM-less UTF-8, read-modify-write.

    private sealed class Kh2Settings
    {
        public string? InstallOverride { get; set; }
    }

    private Kh2Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Kh2Settings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(Kh2Settings s)
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
