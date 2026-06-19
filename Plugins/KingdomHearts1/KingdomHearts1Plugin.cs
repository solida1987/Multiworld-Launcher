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

// NOTE on type qualification (BUILD GOTCHA — CS0104 / CS1537):
// The real launcher project sets BOTH <UseWPF>true</UseWPF> and
// <UseWindowsForms>true</UseWindowsForms>. That makes a long list of simple type
// names ambiguous between WPF and WinForms (Thickness, FontWeights, TextWrapping,
// StackPanel, TextBlock, DockPanel, SolidColorBrush, Dock, UIElement, …). The
// project's GlobalUsings.cs already aliases the short colliding names it cares about
// (Button, TextBox, MessageBox, Color, Brush(es), …) to their WPF types, so we must
// NOT add any file-level `using X = System.Windows...;` alias (that would be CS1537,
// a duplicate alias). To stay bulletproof, this file does NOT `using
// System.Windows.Controls;` / `using System.Windows.Media;` either — every WPF UI
// type below is written FULLY QUALIFIED (System.Windows.*, System.Windows.Controls.*,
// System.Windows.Media.*). Same approach as the shipped Subnautica / Undertale
// plugins.

namespace LauncherV2.Plugins.KingdomHearts1;

// ═══════════════════════════════════════════════════════════════════════════════
// KingdomHearts1Plugin — detect / install-guide / launch for "Kingdom Hearts"
// (KH1, i.e. KINGDOM HEARTS FINAL MIX) played through its OFFICIAL Archipelago
// integration. NATIVE "ConnectsItself".
//
// WHAT KIND OF INTEGRATION IS THIS? (verified online 2026-06-14)
// ─────────────────────────────────────────────────────────────────────────────
// KH1's Archipelago support is part of AP-MAIN itself (worlds/kh1). The pieces a
// player needs are:
//
//   * THE BASE GAME — the player's OWN, paid copy of "KINGDOM HEARTS -HD 1.5+2.5
//     ReMIX-" on PC (Steam appid 2552430, or the Epic Games Store). KH1 is the
//     "KINGDOM HEARTS FINAL MIX.exe" inside that collection. This is paid
//     software — the launcher detects it, it NEVER ships or downloads it (§11).
//
//   * OpenKH (OpenKH/OpenKh) — the modding toolkit. Its "OpenKh.Tools.
//     ModsManager.exe" is what installs the AP mod into the game and patches it.
//     This step is interactive (pick game edition / platform / install folder,
//     then add the mod from GitHub) and is NOT something the launcher can drive
//     headlessly — so it is GUIDED, not faked.
//
//   * THE KH1FM RANDOMIZER SOFTWARE (gaithern/KH1FM-RANDOMIZER) — the randomizer
//     app (its site is kh1fmrando.com). Its release is a single Windows zip
//     ("kh1rando_v<ver>.zip"). The launcher CAN download this for the user (a
//     real install action). Latest verified live: tag 0.11.0, asset
//     "kh1rando_v0.11.0.zip" (~39 MB).
//
//   * THE "KH1 Client" — a Python client that ships INSIDE the Archipelago
//     release the player already has (KH1 is an AP-main world, like Undertale's
//     client). It OWNS the AP slot connection. You open it from Archipelago's
//     "ArchipelagoLauncher.exe" and pick "KH1 Client".
//
// HOW IT CONNECTS (VERIFIED against the official AP "Kingdom Hearts" setup guide,
// https://archipelago.gg/tutorial/Kingdom%20Hearts/kh1_en):
//   The connection is made IN THE "KH1 Client" GUI — NOT in-game, NOT via a
//   command-line arg on the game exe, and NOT via a config file the launcher can
//   pre-write. The documented flow, quoted from the guide:
//     "Once your game is being hosted, open ArchipelagoLauncher.exe. Find KH1
//      Client and open it. At the top, in the Server: bar, type in the host
//      address and port. Click the Connect button in the top right."
//   then the player types their SLOT NAME in the client's command bar and presses
//   Enter. The guide also states: "The game and client communicate via a game
//   communication path set up in your %AppData% folder, and therefore don't need
//   to establish a socket connection." — i.e. the KH1 Client ↔ game bridge is a
//   FILE relay (exactly like Undertale's %localappdata% relay), and the AP slot is
//   held by the KH1 Client. So:
//     * This plugin does NOT attempt a connection prefill — it GUIDES the user and
//       surfaces the session's host:port / slot so they can copy them in.
//     * The launcher's own ApClient must NOT also sit on the slot while the KH1
//       Client is connected (one connection per slot — they would kick each other
//       off). Hence ConnectsItself = true.
//
// WHAT THIS PLUGIN HONESTLY DOES:
//   1. DETECT the base game:
//        * Steam appid 2552430 via the registry → libraryfolders.vdf →
//          appmanifest_2552430.acf → steamapps\common\KINGDOM HEARTS -HD 1.5+2.5
//          ReMIX- pipeline (same as the Witness/Subnautica plugins).
//        * Epic Games Store via %ProgramData%\Epic\EpicGamesLauncher\Data\
//          Manifests\*.item (JSON; match DisplayName/InstallLocation). READ-ONLY —
//          the launcher never writes to the Epic manifests.
//        * A manual install-dir OVERRIDE (Settings folder picker) takes precedence,
//          is validated, and is persisted in this plugin's OWN sidecar
//          (Games/ROMs/kh1/kh1_launcher.json). Core/SettingsStore is NOT modified.
//   2. INSTALL/UPDATE = download the KH1FM Randomizer software zip from its GitHub
//      release into the launcher's own folder (Games/KH1FMRandomizer/) and stamp
//      the version. Best effort + a honest, numbered guided-steps note for the
//      irreducible parts (OpenKH Mods Manager mod install, the in-client connect).
//      Also fetches the kh1.apworld (best effort) for users who want to drop it in
//      Archipelago's custom_worlds — although AP-main already bundles the world +
//      KH1 Client.
//   3. LAUNCH (AP) = best effort: open the KH 1.5+2.5 game (Steam protocol
//      steam://rungameid/2552430 when Steam is present, else the detected exe).
//      The connection itself is entered in the KH1 Client (see header) — no
//      prefill. ConnectsItself = true. SupportsStandalone = true (the game runs
//      fine without AP). Inert ReceiveItems / OnApStateChanged.
//   4. GUIDE the irreducible steps + links (official setup guide, OpenKH, the
//      randomizer software + its site, the KH1FM-AP apworld repo, Steam page,
//      archipelago.gg) in Settings.
//
// THE AP WORLD game string:
//   game = "Kingdom Hearts"  — VERIFIED character-for-character against AP-main
//   worlds/kh1/__init__.py (class KH1World: `game = "Kingdom Hearts"`; WebWorld
//   KH1Web, tutorial kh1/en "kh1_en.md"). NOTE: the launcher's bundled catalog
//   lists this entry's ap_world_name as "KINGDOM HEARTS FINAL MIX", which does NOT
//   match the AP source — the DataPackage lookup key the server uses is "Kingdom
//   Hearts", so this plugin uses that. (Flagged for the catalog to be corrected;
//   this plugin does not edit the catalog.)
//
// DEFENSIVE / UNVERIFIED DETAILS (flagged, "verify at build time"):
//   * RANDOMIZER ASSET: latest release verified live = tag 0.11.0, single asset
//     "kh1rando_v0.11.0.zip". ResolveLatestRandomizerAsync picks the first .zip
//     asset; the pinned fallback URL targets that exact name at 0.11.0. Re-verify
//     on a bump.
//   * "Installed" is judged by the presence of our downloaded randomizer software
//     in the launcher's folder (or a stamped version). The base game being present
//     does NOT by itself make this game "installed" here — the randomizer software
//     is the AP-specific piece this launcher manages, and the rest is guided.
//   * Steam library parsing + Epic manifest parsing are tolerant scans; any failure
//     degrades to "not detected" rather than throwing.
//   * No plaintext AP password is ever written by this plugin (connection is
//     entered in the KH1 Client GUI), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class KingdomHearts1Plugin : IGamePlugin
{
    // ── Constants — the KH1FM Randomizer software (real repo, verified) ─────────
    private const string RANDO_OWNER = "gaithern";
    private const string RANDO_REPO  = "KH1FM-RANDOMIZER";
    private const string RandoRepoUrl = $"https://github.com/{RANDO_OWNER}/{RANDO_REPO}";
    private const string GH_RANDO_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{RANDO_OWNER}/{RANDO_REPO}/releases/latest";
    private const string GH_RANDO_RELEASES_URL =
        $"https://api.github.com/repos/{RANDO_OWNER}/{RANDO_REPO}/releases";
    private const string RandoWebsite = "https://www.kh1fmrando.com/";

    // The KH1FM-AP component (the apworld; KH1 is also bundled in AP-main).
    private const string AP_OWNER = "gaithernOrg";
    private const string AP_REPO  = "KH1FM-AP";
    private const string ApRepoUrl = $"https://github.com/{AP_OWNER}/{AP_REPO}";
    private const string GH_AP_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{AP_OWNER}/{AP_REPO}/releases/latest";

    // OpenKH modding toolkit (its ModsManager installs the AP mod into the game).
    private const string OpenKhRepoUrl  = "https://github.com/OpenKH/OpenKh";
    private const string OpenKhReleases = "https://github.com/OpenKH/OpenKh/releases/latest";

    /// Official Archipelago "Kingdom Hearts" (KH1) setup guide.
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Kingdom%20Hearts/kh1_en";

    /// Archipelago releases — the "KH1 Client" ships inside this download.
    private const string ApReleasesUrl =
        "https://github.com/ArchipelagoMW/Archipelago/releases";

    private const string ArchipelagoSite = "https://archipelago.gg";

    // ── Base game — KINGDOM HEARTS -HD 1.5+2.5 ReMIX- ──────────────────────────

    /// Steam application id for KINGDOM HEARTS -HD 1.5+2.5 ReMIX- (VERIFIED —
    /// store.steampowered.com/app/2552430).
    private const string SteamAppId = "2552430";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";
    private const string SteamStoreUrl =
        "https://store.steampowered.com/app/2552430";

    /// Standard Steam install sub-folder name (steamapps\common\<this>).
    private const string SteamCommonFolderName = "KINGDOM HEARTS -HD 1.5+2.5 ReMIX-";

    /// KH1 (Final Mix) exe inside the 1.5+2.5 collection.
    private const string Kh1ExeName = "KINGDOM HEARTS FINAL MIX.exe";

    /// Substrings used to recognise the collection's display name (Epic manifest)
    /// and to fuzzy-match its install folder.
    private static readonly string[] CollectionNameHints =
        { "1.5", "2.5", "remix", "kingdom hearts" };

    /// Pinned fallback for the randomizer software when the GitHub API is
    /// unreachable. Tag 0.11.0 verified live 2026-06-14; single asset below.
    private const string FallbackVersion = "0.11.0";
    private const string FallbackZipName = "kh1rando_v0.11.0.zip";
    private static readonly string FallbackZipUrl =
        $"{RandoRepoUrl}/releases/download/{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp, written after a successful randomizer download.
    private const string VersionFileName = "kh1_rando_version.dat";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "kh1";
    public string DisplayName => "Kingdom Hearts";
    public string Subtitle    => "Native PC · Archipelago";

    /// EXACT AP game string — VERIFIED against worlds/kh1/__init__.py
    /// (class KH1World: game = "Kingdom Hearts").
    public string ApWorldName => "Kingdom Hearts";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "kh1.png");

    public string ThemeAccentColor => "#2B6CB0";   // Kingdom Hearts ocean blue

    public string[] GameBadges => new[] { "Requires KH 1.5+2.5 ReMIX" };

    public string Description =>
        "Kingdom Hearts (KINGDOM HEARTS FINAL MIX) is the action-RPG that started " +
        "Sora's journey across Disney and Final Fantasy worlds. This is the official " +
        "Archipelago integration: weapons, abilities, summons, magic, key story " +
        "unlocks and more are shuffled into the multiworld. You bring your own copy " +
        "of KINGDOM HEARTS -HD 1.5+2.5 ReMIX- on PC (Steam or the Epic Games Store); " +
        "the game is modded with OpenKH's Mods Manager plus the KH1FM Randomizer " +
        "software, and Archipelago's bundled \"KH1 Client\" connects to the " +
        "multiworld while you play. The launcher detects your install, downloads the " +
        "randomizer software, and guides the one-time mod setup. You connect to your " +
        "server in the KH1 Client's own window.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means we have the KH1FM Randomizer software in our own folder.
    /// (The base game alone is not enough — the randomizer software is the AP piece
    /// this launcher manages; OpenKH mod install + the KH1 Client connect are
    /// guided.)
    public bool IsInstalled => HasRandomizerSoftware();

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the KH1FM Randomizer software zip is extracted. (The base
    /// game lives in its own Steam/Epic install, which we only detect.)
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "KH1FMRandomizer");

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// Where the KH1 apworld is saved for the user to copy into Archipelago's
    /// custom_worlds folder (AP-main already bundles the stable world; this is a
    /// best-effort convenience for the gaithernOrg release).
    private string ApWorldLocalPath => Path.Combine(GameDirectory, "kh1.apworld");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained source file — same approach as the
    /// Subnautica / Witness / Undertale plugins). BOM-less UTF-8.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "kh1_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The bundled "KH1 Client" reports checks/items/goal to the AP server itself
    // (via the %AppData% game-communication path). The launcher relays nothing.
    // These exist only for interface compatibility (ConnectsItself = true).
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
            InstalledVersion = HasRandomizerSoftware()
                ? (File.Exists(VersionFilePath)
                    ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim()
                    : "installed")
                : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        try
        {
            var (version, _, _) = await ResolveLatestRandomizerAsync(ct);
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
        // 1. Resolve the latest randomizer-software release (pinned fallback offline).
        progress.Report((3, "Checking the latest KH1FM Randomizer software release..."));
        var (version, zipUrl, apworldUrl) = await ResolveLatestRandomizerAsync(ct);
        AvailableVersion = version;

        // 2. Already current? (idempotent fast path)
        if (HasRandomizerSoftware()
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"KH1FM Randomizer software {version} is up to date. " +
                "See Settings for the OpenKH mod-install and KH1 Client connect steps."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the KH1FM Randomizer software download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                RandoRepoUrl + "/releases/latest (or " + RandoWebsite + "). Remember you " +
                "also need your own copy of KINGDOM HEARTS -HD 1.5+2.5 ReMIX- on Steam " +
                "(appid " + SteamAppId + ") or the Epic Games Store.");

        // 3. Download + extract the randomizer software into our own folder.
        await DownloadAndExtractRandomizerAsync(zipUrl, version, progress, ct);

        // 4. Fetch the kh1 apworld next to the install (best effort).
        if (apworldUrl != null)
        {
            try
            {
                progress.Report((88, "Downloading the kh1 apworld (optional)..."));
                byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                await File.WriteAllBytesAsync(ApWorldLocalPath, apworld, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch { /* AP-main already bundles the world — optional */ }
        }

        // 5. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        string? baseDir  = ResolveBaseGameDir();
        string baseLine  = baseDir != null
            ? $"Detected your KINGDOM HEARTS -HD 1.5+2.5 ReMIX- install at \"{baseDir}\". "
            : "Reminder: you need your own copy of KINGDOM HEARTS -HD 1.5+2.5 ReMIX- " +
              "(Steam appid " + SteamAppId + ", or the Epic Games Store). ";

        progress.Report((100,
            $"KH1FM Randomizer software {version} downloaded. " + baseLine +
            "To finish setup (one time): install the latest OpenKH, open " +
            "OpenKh.Tools.ModsManager.exe, choose Kingdom Hearts 1, and install the " +
            "AP mod, then run the randomizer software to generate a seed. To play: " +
            "launch the game, open Archipelago's ArchipelagoLauncher.exe, pick \"KH1 " +
            "Client\", type your server host:port in the Server bar and click Connect, " +
            "then enter your slot name. The launcher cannot pre-fill the connection — " +
            "it is entered in the KH1 Client. See Settings for the full guided steps."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── AutoMod-style validation of a user-picked base-game folder ────────────

    /// Used by the Settings folder picker: a valid KH 1.5+2.5 folder contains
    /// "KINGDOM HEARTS FINAL MIX.exe" (or, defensively, any of the collection's
    /// exes). Return null when acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your KINGDOM HEARTS -HD 1.5+2.5 " +
                   "ReMIX- install folder.";

        if (LooksLikeBaseGameDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeBaseGameDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a KINGDOM HEARTS -HD 1.5+2.5 ReMIX- " +
               "installation. Pick the folder that contains \"KINGDOM HEARTS FINAL " +
               "MIX.exe\" (the Steam version is normally in steamapps\\common\\KINGDOM " +
               "HEARTS -HD 1.5+2.5 ReMIX-).";
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: the AP server connection for KH1 is entered in the bundled "KH1
        // Client" GUI (Server: host:port → Connect → slot name) — there is no
        // command-line / config prefill we can apply (verified — see header). So
        // launching from this tile just starts the base game for convenience; the
        // user connects in the KH1 Client with the session credentials (the
        // settings panel + note surface those to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the KH1 Client is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartBaseGame();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) KINGDOM HEARTS -HD 1.5+2.5 ReMIX- runs perfectly well.
    public bool SupportsStandalone => true;

    /// The bundled "KH1 Client" owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartBaseGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started the base game from here (when launched via the exe).
        // Kill what we launched; never touch the user's AP server.
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        _gameProcess = null;
        // No plaintext AP password is ever written to disk by this plugin (the
        // connection is entered in the KH1 Client), so there is nothing to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The bundled KH1 Client receives items from the AP server directly and
        // relays them into the game; there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The KH1 Client renders its own connection state in its window.
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
            Text = "Kingdom Hearts (KH1) is your own game — KINGDOM HEARTS -HD 1.5+2.5 " +
                   "ReMIX- on Steam or the Epic Games Store — with the Archipelago mod " +
                   "added on top. Setup uses OpenKH's Mods Manager plus the KH1FM " +
                   "Randomizer software, and Archipelago's bundled \"KH1 Client\" connects " +
                   "to the multiworld while you play. The launcher detects your install and " +
                   "downloads the randomizer software, but the OpenKH mod install and the " +
                   "connection itself are done outside the launcher (you connect in the KH1 " +
                   "Client's own window). These external steps are not verified by this " +
                   "launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: randomizer software download status ──────────────────
        panel.Children.Add(SectionHeader("KH1FM RANDOMIZER SOFTWARE", muted));
        string? randoDir = ResolveRandomizerDir();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = randoDir != null
                ? "✓ Randomizer software downloaded: " + randoDir
                : "Randomizer software not downloaded yet (use Install on the Play tab, " +
                  "or get it from the randomizer site/repo below).",
            FontSize = 11, Foreground = randoDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: base game (detected / override) ──────────────────────
        panel.Children.Add(SectionHeader("KINGDOM HEARTS -HD 1.5+2.5 REMIX- (BASE GAME)", muted));

        string? steamDir    = DetectSteamInstallDir();
        string? epicDir     = DetectEpicInstallDir();
        string? overrideDir = LoadOverrideDir();
        string? baseDir     = ResolveBaseGameDir();
        string  detectMsg = baseDir != null
            ? (overrideDir != null
                ? "✓ Using your selected folder: " + baseDir
                : (steamDir != null
                    ? "✓ Detected Steam install (appid " + SteamAppId + "): " + baseDir
                    : "✓ Detected Epic Games Store install: " + baseDir))
            : "KINGDOM HEARTS -HD 1.5+2.5 ReMIX- not detected. Pick your install folder " +
              "below, or install it via Steam / Epic first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = baseDir != null ? success : muted,
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
            ToolTip     = "Your KINGDOM HEARTS -HD 1.5+2.5 ReMIX- install folder (the one " +
                          "containing \"KINGDOM HEARTS FINAL MIX.exe\"). Detected from Steam / " +
                          "Epic automatically; set it here to override.",
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
                Title            = "Select your KINGDOM HEARTS -HD 1.5+2.5 ReMIX- install folder",
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
                    System.Windows.MessageBox.Show(bad, "Not a Kingdom Hearts 1.5+2.5 folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeBaseGameDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeBaseGameDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid " + SteamAppId + "); " +
                   "Epic Games Store installs are detected from the Epic manifests. Use this " +
                   "picker only for a non-standard location.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connecting (entered in the KH1 Client) ───────────────
        panel.Children.Add(SectionHeader("CONNECTING (entered in the KH1 Client window)", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Open Archipelago's ArchipelagoLauncher.exe and pick \"KH1 Client\". At " +
                   "the top, in the Server: bar, type your host and port (e.g. " +
                   "archipelago.gg:38281) and click Connect. Then type your slot name in the " +
                   "client's command bar and press Enter. The game and client talk through a " +
                   "communication file in your %AppData% folder, so the KH1 Client — not this " +
                   "launcher — holds the multiworld connection.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(SectionHeader("GUIDED SETUP", muted));
        foreach (string step in new[]
        {
            "1. Own KINGDOM HEARTS -HD 1.5+2.5 ReMIX- (Steam appid " + SteamAppId + ", or the " +
                "Epic Games Store) and launch KH1 to the title screen once. Use \"Select " +
                "folder...\" above if it was not auto-detected.",
            "2. Install the latest OpenKH (link below). Open OpenKh.Tools.ModsManager.exe, " +
                "choose your game edition (PC Release), platform (EGS or Steam) and your " +
                "1.5+2.5 install folder.",
            "3. In OpenKH Mods Manager, switch the top-right dropdown to \"Kingdom Hearts 1\", " +
                "then Mods > Install a New Mod and add the AP mod from GitHub (see the setup " +
                "guide for the exact mod name).",
            "4. Download the KH1FM Randomizer software: use Install on the Play tab (it " +
                "downloads and unpacks the randomizer software for you), or get it from the " +
                "randomizer site below. Run it to generate your seed.",
            "5. Launch the game from this launcher (or normally). Then open Archipelago's " +
                "ArchipelagoLauncher.exe, pick \"KH1 Client\", enter your server host:port in " +
                "the Server bar, click Connect, and type your slot name. (This launcher " +
                "cannot pre-fill the connection — it is entered in the KH1 Client.)",
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
            ("Kingdom Hearts (KH1) Setup Guide ↗",       SetupGuideUrl),
            ("KINGDOM HEARTS 1.5+2.5 on Steam ↗",        SteamStoreUrl),
            ("OpenKH (Mods Manager) ↗",                  OpenKhReleases),
            ("KH1FM Randomizer Software ↗",              RandoWebsite),
            ("KH1FM Randomizer (GitHub) ↗",              RandoRepoUrl),
            ("KH1FM-AP (apworld) ↗",                     ApRepoUrl),
            ("Archipelago Releases (KH1 Client) ↗",      ApReleasesUrl),
            ("Archipelago Official ↗",                   ArchipelagoSite),
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
            Text = text, FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        };

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Randomizer-software releases are the AP-relevant news for this game.
        try
        {
            string json = await _http.GetStringAsync(GH_RANDO_RELEASES_URL, ct);
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

    /// "v0.11.0" → "0.11.0" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest randomizer-software release: version + the .zip download
    /// URL + an optional apworld URL (best effort, may be on a separate repo).
    /// Falls back to the pinned 0.11.0 direct URL when the API is unreachable.
    private async Task<(string Version, string? ZipUrl, string? ApWorldUrl)>
        ResolveLatestRandomizerAsync(CancellationToken ct)
    {
        string? apworldUrl = await TryResolveApWorldUrlAsync(ct);

        try
        {
            string json = await _http.GetStringAsync(GH_RANDO_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;   // a .zip whose name mentions "rando"/"kh1"
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
                        (lower.Contains("rando") || lower.Contains("kh1")))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip, apworldUrl);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl, apworldUrl);
    }

    /// Best-effort: resolve the kh1.apworld URL from the KH1FM-AP repo's latest
    /// release. Returns null on any failure (AP-main already bundles the world, so
    /// this is purely a convenience download).
    private async Task<string?> TryResolveApWorldUrlAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_AP_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("assets", out var assets)
                && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name != null && url != null &&
                        name.EndsWith(".apworld", StringComparison.OrdinalIgnoreCase))
                        return url;
                }
            }
        }
        catch { /* optional */ }
        return null;
    }

    // ── Private helpers — randomizer software detection ───────────────────────

    /// True when the KH1FM Randomizer software is present in our folder (any file,
    /// or a stamped version). The base game alone does NOT count.
    private bool HasRandomizerSoftware() => ResolveRandomizerDir() != null;

    /// The folder our randomizer software lives in, if it has been downloaded
    /// (non-empty GameDirectory, or a version stamp). Null otherwise.
    private string? ResolveRandomizerDir()
    {
        try
        {
            if (!Directory.Exists(GameDirectory)) return null;
            if (File.Exists(VersionFilePath)) return GameDirectory;
            // Any extracted content (exe / dll / many files) means it is installed.
            foreach (var _ in Directory.EnumerateFileSystemEntries(GameDirectory))
                return GameDirectory;
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start KH 1.5+2.5: prefer the KH1 exe in the detected/override install; if
    /// that cannot be found but Steam is present, fall back to the steam:// URL.
    /// Best effort — surfaces a clear message rather than failing opaquely. The KH1
    /// Client can attach to a game the user starts manually too.
    private void StartBaseGame()
    {
        string? dir = ResolveBaseGameDir();
        string? exe = dir != null ? FindKh1ExeIn(dir) : null;

        if (exe != null && File.Exists(exe))
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName         = exe,
                    WorkingDirectory = dir!,
                    UseShellExecute  = true,
                }) ?? throw new InvalidOperationException("Failed to start Kingdom Hearts.");

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
            catch { /* fall through to Steam below */ }
        }

        // Fall back to Steam if we at least know Steam is installed.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // Steam owns the process; we won't track exit
                return;
            }
            catch { /* fall through to the error below */ }
        }

        throw new FileNotFoundException(
            "Could not find \"KINGDOM HEARTS FINAL MIX.exe\". Open this game's Settings " +
            "and pick your KINGDOM HEARTS -HD 1.5+2.5 ReMIX- install folder, or install " +
            "the game via Steam / Epic. You can also launch the game yourself and connect " +
            "from the KH1 Client.",
            Kh1ExeName);
    }

    /// Find KH1's exe in `dir`: prefer "KINGDOM HEARTS FINAL MIX.exe", else a fuzzy
    /// "*final mix*" exe that is the KH1 one (not KH II Final Mix / BBS). Null if none.
    private static string? FindKh1ExeIn(string dir)
    {
        try
        {
            string preferred = Path.Combine(dir, Kh1ExeName);
            if (File.Exists(preferred)) return preferred;

            foreach (string exe in Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToUpperInvariant();
                // KH1 Final Mix, but not "II" / "BIRTH BY SLEEP" / "Re_Chain".
                if (name.Contains("FINAL MIX") && !name.Contains("II") &&
                    !name.Contains("BIRTH") && !name.Contains("CHAIN") && !name.Contains("RE_"))
                    return exe;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — base-game install detection ─────────────────────────

    /// The base-game install dir to use: the override (if set and valid) wins, else
    /// the Steam-detected install, else the Epic-detected install. Null otherwise.
    private string? ResolveBaseGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeBaseGameDir(ov))
            return ov;

        try { string? s = DetectSteamInstallDir(); if (s != null) return s; }
        catch { /* ignore */ }

        try { return DetectEpicInstallDir(); }
        catch { return null; }
    }

    /// A folder "looks like" the collection if it has KH1's exe (preferred) or any
    /// of the collection's "*final mix*" exes.
    private static bool LooksLikeBaseGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, Kh1ExeName))) return true;
            foreach (string exe in Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToUpperInvariant();
                if (name.Contains("FINAL MIX")) return true;
            }
            return false;
        }
        catch { return false; }
    }

    // ── Steam detection (registry → libraryfolders.vdf → appmanifest) ─────────

    /// Detect the Steam KH 1.5+2.5 install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
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
                            if (LooksLikeBaseGameDir(candidate)) return candidate;
                        }
                        string conventional = Path.Combine(common, SteamCommonFolderName);
                        if (LooksLikeBaseGameDir(conventional)) return conventional;
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

    // ── Epic Games Store detection (READ-ONLY manifests) ──────────────────────

    /// Detect the Epic Games Store install by scanning the Epic manifests
    /// (%ProgramData%\Epic\EpicGamesLauncher\Data\Manifests\*.item — JSON). We match
    /// a manifest whose DisplayName looks like the 1.5+2.5 collection and whose
    /// InstallLocation contains KH1's exe. READ-ONLY — the launcher never writes to
    /// the Epic manifests (per the brief). Never throws.
    private static string? DetectEpicInstallDir()
    {
        try
        {
            foreach (string manifestsDir in EpicManifestDirs())
            {
                if (!Directory.Exists(manifestsDir)) continue;
                foreach (string item in Directory.EnumerateFiles(manifestsDir, "*.item", SearchOption.TopDirectoryOnly))
                {
                    try
                    {
                        string json = File.ReadAllText(item);
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;

                        string? install = GetJsonString(root, "InstallLocation");
                        if (string.IsNullOrWhiteSpace(install)) continue;

                        string display = GetJsonString(root, "DisplayName") ?? "";
                        string appName = GetJsonString(root, "AppName") ?? "";
                        string hay     = (display + " " + appName).ToLowerInvariant();

                        bool nameMatches = LooksLikeCollectionName(hay);
                        bool dirMatches  = LooksLikeBaseGameDir(install);

                        // Accept when the folder actually has a KH exe, or when the
                        // name strongly matches and the folder exists.
                        if (dirMatches) return install;
                        if (nameMatches && Directory.Exists(install))
                        {
                            string? sub = FindCollectionSubdir(install);
                            if (sub != null) return sub;
                        }
                    }
                    catch { /* skip a malformed manifest */ }
                }
            }
        }
        catch { /* ProgramData / registry access failed */ }
        return null;
    }

    /// Candidate Epic "Manifests" directories: the conventional ProgramData path,
    /// plus the AppDataPath from the registry (HKLM\...\Epic Games\EpicGamesLauncher).
    private static IEnumerable<string> EpicManifestDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrWhiteSpace(programData))
        {
            string p = Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
            if (seen.Add(p)) yield return p;
        }

        string? appData = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Epic Games\EpicGamesLauncher", "AppDataPath")
            ?? ReadRegistryString(Registry.LocalMachine,
                @"SOFTWARE\Epic Games\EpicGamesLauncher", "AppDataPath");
        if (!string.IsNullOrWhiteSpace(appData))
        {
            string p = Path.Combine(appData!.Replace('/', '\\').TrimEnd('\\'), "Manifests");
            if (seen.Add(p)) yield return p;
        }
    }

    /// True when a lower-cased haystack looks like the 1.5+2.5 collection name.
    private static bool LooksLikeCollectionName(string hayLower)
    {
        if (!hayLower.Contains("kingdom hearts")) return false;
        // Require at least one collection-specific hint to avoid matching KH2/BBS.
        return hayLower.Contains("1.5") || hayLower.Contains("2.5") ||
               hayLower.Contains("remix");
    }

    /// If an Epic InstallLocation is a parent folder, look one level down for the
    /// actual collection folder that contains KH1's exe. Null if none.
    private static string? FindCollectionSubdir(string parent)
    {
        try
        {
            if (LooksLikeBaseGameDir(parent)) return parent;
            foreach (string sub in Directory.EnumerateDirectories(parent))
            {
                if (LooksLikeBaseGameDir(sub)) return sub;
                string name = Path.GetFileName(sub).ToLowerInvariant();
                if (CollectionNameHints.Any(h => name.Contains(h)) && LooksLikeBaseGameDir(sub))
                    return sub;
            }
        }
        catch { /* permission */ }
        return null;
    }

    /// Read a string property (case-insensitive) from a JSON object element.
    private static string? GetJsonString(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) &&
                p.Value.ValueKind == JsonValueKind.String)
                return p.Value.GetString();
        }
        return null;
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

    // ── Private helpers — download / extract the randomizer software ──────────

    /// Download the randomizer software zip and extract it into GameDirectory.
    /// Flattens a single wrapping sub-folder if the zip has one.
    private async Task DownloadAndExtractRandomizerAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"kh1-rando-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((6, $"Downloading KH1FM Randomizer software {version}..."));
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
                        int pct = (int)(6 + 70 * downloaded / total);
                        progress.Report((pct, $"Downloading randomizer software... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((80, "Extracting the randomizer software..."));
            Directory.CreateDirectory(GameDirectory);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, GameDirectory, overwriteFiles: true);

            progress.Report((86, "Randomizer software installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the base-game install-dir
    // override) in its OWN JSON file so it stays a single self-contained source file
    // and does not modify Core/SettingsStore. BOM-less UTF-8, read-modify-write.

    private sealed class Kh1Settings
    {
        public string? InstallOverride { get; set; }
    }

    private Kh1Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Kh1Settings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(Kh1Settings s)
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
