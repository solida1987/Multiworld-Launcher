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
// WPF, so the bare names Button / TextBox / Color / Brushes / MessageBox /
// FontWeights / Orientation / Clipboard collide between System.Windows.Forms and
// System.Windows[.Controls/.Media] (CS0104). Qualifying every UI type avoids that.
// GlobalUsings.cs already aliases the colliding short names project-wide — so this
// file must NOT add any file-level `using X = System.Windows...;` alias (CS1537).

namespace LauncherV2.Plugins.OblivionRemastered;

// ═══════════════════════════════════════════════════════════════════════════════
// OblivionRemasteredPlugin — detect / install / launch for "The Elder Scrolls IV:
// Oblivion Remastered" (Bethesda / Virtuos, 2025) played through the two-part
// POD-io Archipelago integration:
//   1. Oblivion-ArchipelagoMod — an in-game mod (client side) that reads and
//      writes JSON files in the save directory for bidirectional communication
//      with the Archipelago server. The LAUNCHER downloads and stages this.
//   2. Oblivion-ArchipelagoWorld — the APWorld (server side). This must be
//      installed separately by the USER in their Archipelago installation. The
//      launcher GUIDES the user but cannot install it into the AP server for them.
//
// This is a NATIVE "ConnectsItself" integration: the in-game mod owns the AP
// communication via a file-based bridge (it reads/writes JSON files in the game's
// save directory). The launcher does NOT hold its own ApClient on this slot.
//
// ── HONEST REALITY CHECK (2026-06-19, verified against the GitHub repos) ──────
//
//   * AP WORLD game string: "Oblivion Remastered" — VERIFIED against the
//     POD-io/Oblivion-ArchipelagoWorld repository (v0.5.0, June 13, 2026).
//     GameId here = "oblivion_remastered". This is a COMMUNITY APWORLD (not a
//     core AP world) — the user must install the .apworld file into their
//     Archipelago installation manually (Custom Worlds directory). The launcher
//     links to the APWorld releases page so the user can download it.
//
//   * CLIENT (mod) repo: POD-io/Oblivion-ArchipelagoMod — latest release
//     downloaded from github.com/POD-io/Oblivion-ArchipelagoMod/releases/latest.
//     The launcher downloads the mod release and extracts it into the game
//     directory. Asset naming is resolved from the GitHub releases API; the
//     launcher looks for any .zip asset and falls back to a pinned v0.5.0 URL.
//
//   * STEAM APPID: 2623190 — UNVERIFIED (taken from the integration brief; not
//     confirmed against the live Steam store page). If game detection fails, use
//     the manual folder picker in Settings.
//
//   * STEAM COMMON FOLDER: "Oblivion Remastered" — UNVERIFIED. Steam often uses
//     the name from the .acf installdir field; the plugin reads that field and
//     also tries "Oblivion Remastered" as a fallback.
//
//   * EXECUTABLE: OblivionRemastered.exe — UNVERIFIED exact location. Unreal
//     Engine 5 titles typically ship their executable inside a
//     <GameName>/Binaries/Win64/ subdirectory (e.g.
//     OblivionRemastered/Binaries/Win64/OblivionRemastered.exe). The plugin
//     tries BOTH the root of the install directory AND the
//     OblivionRemastered/Binaries/Win64/ subdirectory so that either layout
//     works.
//
//   * FILE-BASED BRIDGE: the AP mod reads and writes JSON files in the game's
//     save directory. No TCP port is configured in-game; no launcher-held socket
//     is needed. ConnectsItself = true.
//
//   * TWO-PART INSTALL: the launcher handles PART 1 (the in-game mod). PART 2
//     (the APWorld for the Archipelago server) must be installed by the user —
//     they download the .apworld from the APWorld releases page and drop it into
//     their Archipelago "custom_worlds" folder (or use the AP launcher's install
//     feature). The settings panel explains both parts with numbered steps.
//
//   * CONNECTION: no command-line prefill is documented. Per the file-based
//     bridge design, the connection details (host, port, slot, password) are
//     entered via the in-game mod's own UI. The settings panel surfaces the
//     session values so the user can copy them in-game.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Oblivion Remastered install via the Windows registry,
//      parsing steamapps\libraryfolders.vdf and locating the game via
//      appmanifest_2623190.acf → steamapps\common\<installdir>. A manual
//      override (folder picker) is supported and takes precedence; it is
//      validated (must contain OblivionRemastered.exe at root or in
//      <name>/Binaries/Win64/) and persisted in this plugin's own sidecar at
//      Games/ROMs/oblivion_remastered/oblivion_remastered_launcher.json.
//   2. INSTALL/UPDATE = download the latest release zip from
//      POD-io/Oblivion-ArchipelagoMod/releases/latest and extract it into the
//      game directory. Existing files are overwritten; siblings are preserved.
//      IsInstalled is judged by the presence of AP-related JSON or DLL files in
//      the game's Mods/, Content/, or similar folder — OR by the launcher's own
//      version stamp (written on successful install via this plugin).
//   3. LAUNCH = run OblivionRemastered.exe from the detected/override install;
//      fall back to steam://rungameid/2623190 if the exe cannot be located but
//      Steam is present.
//   4. The settings panel guides the TWO-PART install: (1) use the Install button
//      here to stage the in-game mod, (2) manually install the APWorld in your
//      Archipelago installation (link provided). Connection is entered in-game.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class OblivionRemasteredPlugin : IGamePlugin
{
    // ── Constants — APWorld repo (server side, for links and news) ────────────
    private const string APWORLD_OWNER = "POD-io";
    private const string APWORLD_REPO  = "Oblivion-ArchipelagoWorld";
    private const string ApWorldRepoUrl = $"https://github.com/{APWORLD_OWNER}/{APWORLD_REPO}";
    private const string GH_APWORLD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{APWORLD_OWNER}/{APWORLD_REPO}/releases/latest";
    private const string GH_APWORLD_RELEASES_URL =
        $"https://api.github.com/repos/{APWORLD_OWNER}/{APWORLD_REPO}/releases";

    // ── Constants — in-game mod repo (what the launcher downloads) ───────────
    private const string MOD_OWNER = "POD-io";
    private const string MOD_REPO  = "Oblivion-ArchipelagoMod";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // Pinned fallback when the GitHub API is unreachable. Version 0.5.0 is the
    // latest verified release (June 2026). Asset name UNVERIFIED — the plugin
    // falls back to the first .zip in the release; this URL is a best-effort
    // guess for the zip naming convention.
    private const string FallbackVersion = "0.5.0";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/OblivionArchipelagoMod-{FallbackVersion}.zip";

    private const string SetupGuideUrl   = "https://archipelago.gg/games/Oblivion%20Remastered/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — appid 2623190 (UNVERIFIED — taken from the integration brief).
    private const string ObAppId       = "2623190";
    private static readonly string SteamRunUrl = $"steam://rungameid/{ObAppId}";

    // Steam common folder name (UNVERIFIED — may differ from the acf installdir).
    private const string SteamCommonFolderName = "Oblivion Remastered";

    // Executable name (UNVERIFIED exact location — UE5 games typically ship the
    // exe inside <GameName>/Binaries/Win64/). The plugin tries both layouts.
    private const string ObExeName = "OblivionRemastered.exe";

    // UE5 sub-path tried after the root: <install>\OblivionRemastered\Binaries\Win64\
    private const string UE5ExeSubPath = @"OblivionRemastered\Binaries\Win64\OblivionRemastered.exe";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "oblivion_remastered";
    public string DisplayName => "Oblivion Remastered";
    public string Subtitle    => "PC · Archipelago mod";

    /// EXACT AP game string — verified against POD-io/Oblivion-ArchipelagoWorld v0.5.0.
    public string ApWorldName => "Oblivion Remastered";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "oblivion_remastered.png");

    public string ThemeAccentColor => "#E65100";   // Oblivion gate orange
    public string[] GameBadges     => new[] { "Requires Oblivion Remastered on Steam", "Two-part install" };

    public string Description =>
        "The Elder Scrolls IV: Oblivion Remastered (2025) is Bethesda and Virtuos's " +
        "stunning Unreal Engine 5 remake of the 2006 RPG classic — rebuilding the vast " +
        "land of Cyrodiil with modern visuals, reworked combat, and full voice acting " +
        "while preserving the original's legendary open-world freedom and Daedric " +
        "storylines. The Archipelago mod by POD-io shuffles main quest gates, Daedric " +
        "shrine access, guild questlines, skill books, and key items across the multiworld " +
        "using a file-based communication bridge. This is a two-part installation: the " +
        "launcher stages the in-game mod, and you also need to install the provided " +
        "APWorld in your Archipelago installation separately. Requires: your own " +
        "legally-owned copy of Oblivion Remastered on Steam.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" is judged by a version stamp written on a successful launcher-
    /// driven install, OR by AP-related files (JSON, DLL) found in the game's
    /// mod directories. If no game install is detected, this is false.
    public bool IsInstalled => CheckIsInstalled();

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Working directory for downloads and extracted stage files. The actual mod
    /// is installed INTO the game directory, not here. Exposed as GameDirectory
    /// for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "OblivionRemastered");

    /// This plugin's own settings sidecar (install-dir override + version stamp).
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "oblivion_remastered_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The POD-io AP mod communicates with the AP server via a file-based bridge.
    // The launcher does not relay anything. These exist for interface compatibility
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
            InstalledVersion = CheckIsInstalled()
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
                await GitHubHelper.FetchLatestTagAsync(APWORLD_OWNER, APWORLD_REPO, ct));
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
        // 0. Locate the game install to drop the mod into.
        progress.Report((2, "Locating your Oblivion Remastered installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find an Oblivion Remastered installation. Open this game's " +
                "Settings and pick your Oblivion Remastered folder (the one containing " +
                "OblivionRemastered.exe or the OblivionRemastered/Binaries/Win64/ " +
                "subdirectory), or install Oblivion Remastered via Steam first. The " +
                "Archipelago mod is added on top of your own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback on API failure).
        progress.Report((6, "Checking the latest Oblivion Archipelago Mod release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Oblivion Archipelago Mod download on GitHub. " +
                "Check your internet connection, or download the mod manually from " +
                ModRepoUrl + "/releases and extract it into your game directory. " +
                "See Settings for guided steps.");

        // 2. Download and extract the mod into the game directory.
        //    HONEST: this is the in-game mod (PART 1). The user must ALSO install
        //    the APWorld into their Archipelago server separately (PART 2 — the
        //    settings panel explains this with a direct link).
        await DownloadAndExtractModAsync(zipUrl, version, gameDir, progress, ct);

        // 3. Stamp the version so the tile can show it.
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Oblivion Remastered AP Mod {version} installed into your game directory. " +
            "IMPORTANT — TWO-PART INSTALL: this was Part 1 (the in-game mod). " +
            "For Part 2, you must also install the APWorld (.apworld file) in your " +
            "Archipelago installation — open Settings for the download link and steps. " +
            "To play: launch the game, enter your Archipelago server details " +
            "(host, port, slot name, password) in the mod's in-game UI, and connect. " +
            "Connection is entered in-game — this launcher does not pre-fill it."));
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
        // HONEST: Archipelago connection details for Oblivion Remastered are
        // entered in the in-game mod UI (file-based bridge; no documented
        // command-line / config prefill). Launching from this tile just starts
        // the game; the user connects in-game using the session credentials
        // shown in the settings panel.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartGame();
        return Task.CompletedTask;
    }

    /// Oblivion Remastered can be run without the AP mod (plain game).
    public bool SupportsStandalone => true;

    /// The POD-io AP mod owns the slot connection via file-based bridge.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The AP mod receives items from the AP server via its file-based bridge;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod manages its own connection state in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Oblivion Remastered folder
    /// contains OblivionRemastered.exe (at root or in the UE5 Binaries/Win64
    /// subdirectory). Returns null when acceptable, else a short reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Oblivion Remastered install folder.";

        if (LooksLikeGameDir(folder))
            return null;

        // Forgiving: user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeGameDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like an Oblivion Remastered installation. Pick the " +
               "folder that contains OblivionRemastered.exe (at the root or in " +
               @"OblivionRemastered\Binaries\Win64\). For Steam this is usually " +
               @"...\steamapps\common\Oblivion Remastered.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Two-part install honesty header ───────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "TWO-PART INSTALLATION: (1) The Install button below stages the " +
                   "in-game AP mod into your Oblivion Remastered folder. (2) You must " +
                   "ALSO install the APWorld (.apworld file) in your Archipelago " +
                   "installation separately — see the guided steps below. Both parts " +
                   "are required for multiplayer. Connection details are entered " +
                   "in-game via the mod's own UI; this launcher does not pre-fill them.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "OBLIVION REMASTERED INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Oblivion Remastered not detected. Pick your install folder below, or " +
              "install Oblivion Remastered via Steam first. Note: Steam appid 2623190 " +
              "and the common folder name are UNVERIFIED — if auto-detection fails, " +
              "use the folder picker.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // Exe detection note (UNVERIFIED path, display where we found it)
        string? exePath = gameDir != null ? FindExePath(gameDir) : null;
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = exePath != null
                ? "Executable found: " + exePath
                : "Executable not found (UNVERIFIED path — the launcher tries both " +
                  "the root and OblivionRemastered\\Binaries\\Win64\\ subdirectory). " +
                  "If neither is correct for your install, please report the actual path.",
            FontSize = 11,
            Foreground = exePath != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // Mod installation status line
        bool modInstalled = CheckIsInstalled();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modInstalled
                ? "Oblivion AP Mod appears to be installed (Part 1 complete). " +
                  "Check Part 2 (APWorld) separately."
                : "Oblivion AP Mod not detected in the game directory yet (use the " +
                  "Install button on the Play tab to stage Part 1).",
            FontSize = 11, Foreground = modInstalled ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Oblivion Remastered install folder. Detected from Steam " +
                          "automatically; set it here to override (non-standard Steam " +
                          "library, or if auto-detection failed).",
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
                Title            = "Select your Oblivion Remastered install folder (contains OblivionRemastered.exe or OblivionRemastered\\Binaries\\Win64\\)",
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
                    System.Windows.MessageBox.Show(bad, "Not an Oblivion Remastered folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // Descend into the conventional Steam folder if user picked the parent.
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
            Text = "Steam installs are detected automatically (appid 2623190 — UNVERIFIED). " +
                   "Use the picker if auto-detection fails or for a non-standard Steam library.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (in-game) ─────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game via the mod UI)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "The Oblivion AP mod uses a file-based bridge — no external client " +
                   "process is needed. After both parts of the install are complete and " +
                   "the game is launched, enter your Archipelago server details (host, " +
                   "port, slot name, and password if required) in the mod's in-game " +
                   "connection UI. This launcher does not pre-fill the connection.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP — TWO-PART INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "PART 1 — In-game Mod (the launcher handles this):",
            "1. Own Oblivion Remastered on Steam. Install it if you have not. If auto-detection " +
               "fails, use the folder picker above.",
            "2. Click the Install button on the Play tab. The launcher downloads the latest " +
               "Oblivion AP Mod from POD-io/Oblivion-ArchipelagoMod/releases and extracts it " +
               "into your game directory.",
            "3. Launch Oblivion Remastered (Play button above). Enable the AP mod in the " +
               "game's mod manager if required.",
            "",
            "PART 2 — APWorld (you must install this separately in Archipelago):",
            "4. Download the .apworld file from the APWorld releases page (link below).",
            "5. Install it in your Archipelago application: in the Archipelago Launcher, go to " +
               "Options → Install APWorld, and select the downloaded .apworld file. " +
               "Alternatively, copy it manually into your Archipelago custom_worlds folder.",
            "6. Generate a game in Archipelago using the Oblivion Remastered world options, " +
               "then host or connect to a room as usual.",
            "",
            "CONNECTING (after both parts are installed):",
            "7. Launch Oblivion Remastered and load your save (or start a new game).",
            "8. In the mod's in-game connection UI, enter your Archipelago server address " +
               "(e.g. archipelago.gg), port, slot name, and optional password, then connect.",
        })
        {
            if (string.IsNullOrEmpty(step))
            {
                panel.Children.Add(new System.Windows.Controls.TextBlock
                {
                    Text = "", FontSize = 6,
                    Margin = new System.Windows.Thickness(0, 0, 0, 4),
                });
                continue;
            }

            bool isHeader = step.StartsWith("PART ") || step.StartsWith("CONNECTING");
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11,
                Foreground = isHeader ? warn : fg,
                FontWeight = isHeader
                    ? System.Windows.FontWeights.SemiBold
                    : System.Windows.FontWeights.Normal,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, isHeader ? 4 : 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("APWorld releases (download .apworld for Archipelago) ↗",  GH_APWORLD_RELEASES_URL),
            ("APWorld repo (POD-io/Oblivion-ArchipelagoWorld) ↗",       ApWorldRepoUrl),
            ("In-game Mod repo (POD-io/Oblivion-ArchipelagoMod) ↗",    ModRepoUrl),
            ("Oblivion Remastered on Archipelago ↗",                    SetupGuideUrl),
            ("Archipelago Official ↗",                                  ArchipelagoSite),
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

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Surface APWorld releases as the primary AP-relevant news, with mod
        // releases as secondary. We poll the APWorld repo (where game logic and
        // version numbers are tracked) since that has 6 releases.
        try
        {
            string json = await _http.GetStringAsync(GH_APWORLD_RELEASES_URL, ct);
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

    // ── Private helpers — release resolution ──────────────────────────────────

    /// "v0.5.0" → "0.5.0"; leading 'v' before a digit is stripped; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest MOD release (what the launcher downloads): version +
    /// download URL. Prefers any .zip asset; falls back to the pinned 0.5.0 URL
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
                string? anyZip = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    anyZip ??= url;
                }
                if (anyZip != null)
                    return (version, anyZip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — Steam / game detection ──────────────────────────────

    /// The game install dir to use: the override (if set and valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeGameDir(ov))
            return ov;

        try { return DetectSteamGameDir(); }
        catch { return null; }
    }

    /// A folder "looks like" the Oblivion Remastered install if it contains the
    /// exe at the root OR at the UE5 Binaries/Win64 subdirectory.
    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            // Try root exe (flat layout, less common for UE5 but checked first)
            if (File.Exists(Path.Combine(dir, ObExeName))) return true;
            // Try UE5 Binaries/Win64 subdirectory (most common UE5 layout)
            if (File.Exists(Path.Combine(dir, UE5ExeSubPath))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Return the absolute path to OblivionRemastered.exe within a detected game
    /// directory (checking both the root and the UE5 Binaries/Win64 subdirectory).
    /// Returns null if neither exists.
    private static string? FindExePath(string gameDir)
    {
        try
        {
            string root = Path.Combine(gameDir, ObExeName);
            if (File.Exists(root)) return root;

            string ue5 = Path.Combine(gameDir, UE5ExeSubPath);
            if (File.Exists(ue5)) return ue5;
        }
        catch { /* permission / vanished */ }
        return null;
    }

    /// Detect the Steam Oblivion Remastered install via the Windows registry:
    /// read Steam root, scan libraryfolders.vdf for all library roots, find the
    /// library containing appmanifest_2623190.acf, then resolve the game folder.
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
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{ObAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common      = Path.Combine(steamapps, "common");
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
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath + HKLM InstallPath). Both are tried.
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

    /// Pull every "path" "<value>" pair out of a libraryfolders.vdf body.
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

    // ── Private helpers — IsInstalled check ──────────────────────────────────

    /// Returns true if the game directory exists AND either:
    ///   (a) the launcher previously stamped a version there (successful install
    ///       via this plugin), OR
    ///   (b) AP-related files are found in common mod/content subdirectories
    ///       (JSON files or DLLs that suggest the AP mod was installed by any means).
    private bool CheckIsInstalled()
    {
        try
        {
            string? dir = ResolveGameDir();
            if (dir == null) return false;

            // (a) launcher-written stamp
            if (!string.IsNullOrWhiteSpace(ReadStampedVersion())) return true;

            // (b) look for AP-related mod files: .json or .dll files in Mods/,
            //     Content/, Plugins/, or similar first-level subdirectories that
            //     suggest the AP mod was dropped there.
            foreach (string sub in new[] { "Mods", "Content", "Plugins", "archipelago" })
            {
                string subDir = Path.Combine(dir, sub);
                if (!Directory.Exists(subDir)) continue;

                // Any .json file whose name contains "archipelago" (AP bridge files)
                foreach (string f in Directory.EnumerateFiles(subDir, "*.json", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(f).IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
                // Any .dll whose name contains "archipelago"
                foreach (string f in Directory.EnumerateFiles(subDir, "*.dll", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(f).IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                        return true;
                }
            }
        }
        catch { /* permission / vanished */ }
        return false;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Oblivion Remastered: prefer the exe in the detected/override install;
    /// if not found but Steam is present, fall back to the steam:// URL.
    private void StartGame()
    {
        string? dir = ResolveGameDir();
        string? exe = dir != null ? FindExePath(dir) : null;

        if (exe != null && File.Exists(exe))
        {
            // Working directory is the directory containing the exe (not the
            // game root), so that UE5's relative paths resolve correctly.
            string workDir = Path.GetDirectoryName(exe) ?? dir!;
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = workDir,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Oblivion Remastered.");

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

        // Fall back to Steam if Steam is installed.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { /* fall through to error */ }
        }

        throw new FileNotFoundException(
            "Could not find OblivionRemastered.exe. Open this game's Settings and " +
            "pick your Oblivion Remastered install folder, or install the game via " +
            "Steam. The launcher looks for the exe both at the root and in " +
            "OblivionRemastered\\Binaries\\Win64\\ (UNVERIFIED path — report the " +
            "actual exe location if neither is correct).",
            ObExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod zip and extract it into the game directory. Existing
    /// files are overwritten; siblings are preserved so other mods are not
    /// disturbed. If the zip wraps everything in a single sub-folder, that
    /// wrapper is flattened so the mod's contents land in the game root.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string gameDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"oblivion-ap-mod-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"oblivion-ap-mod-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading Oblivion AP Mod {version}..."));
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
                        int pct = (int)(10 + 60 * downloaded / total);
                        progress.Report((pct, $"Downloading Oblivion AP Mod... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Extracting mod into your game directory..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // If the entire archive is wrapped in a single folder (common for
            // GitHub release zips), descend into that wrapper before merging.
            string mergeRoot = tempExtract;
            {
                string[] subdirs = Directory.GetDirectories(mergeRoot);
                string[] files   = Directory.GetFiles(mergeRoot);
                if (subdirs.Length == 1 && files.Length == 0)
                    mergeRoot = subdirs[0];
            }

            // Merge the extracted tree INTO the existing game directory without
            // wiping other files.
            Directory.CreateDirectory(gameDir);
            MergeDirectory(mergeRoot, gameDir);

            progress.Report((90, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Recursively copy a directory tree INTO an existing destination, overwriting
    /// individual files but preserving any sibling files already there.
    private static void MergeDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            try { File.Copy(file, dst, overwrite: true); }
            catch { /* locked file (game open?); skip — user can retry with game closed */ }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class ObSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private ObSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<ObSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(ObSettings s)
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
