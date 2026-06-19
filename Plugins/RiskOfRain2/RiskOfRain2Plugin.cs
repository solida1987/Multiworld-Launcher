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

namespace LauncherV2.Plugins.RiskOfRain2;

// ═══════════════════════════════════════════════════════════════════════════════
// RiskOfRain2Plugin — install / launch for "Risk of Rain 2" (Hopoo Games, 2019)
// played through the Archipelago BepInEx mod (Thunderstore: Sneaki/Archipelago),
// the in-game Archipelago client for RoR2. This is a NATIVE "ConnectsItself"
// integration in the same family as the shipped Hollow Knight and Stardew Valley
// plugins — the game itself speaks to the AP server (no emulator, no Lua bridge,
// no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned Risk
// of Rain 2 (Steam appid 632360), and Archipelago is a BepInEx MOD added on top.
// The honest integration ceiling — exactly like the shipped Hollow Knight plugin —
// is "automate what is possible, guide the irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "Risk of Rain 2" (verified against
//     worlds/ror2/__init__.py: `game = "Risk of Rain 2"`). GameId = "ror2".
//     required_client_version = (0, 6, 4).
//
//   * THE MOD is the Thunderstore package "Sneaki/Archipelago" (verified live
//     2026-06-14 via the official AP setup guide + the Thunderstore experimental
//     package API). Latest version 1.5.3. It is a BepInEx plugin that drops a DLL
//     into <RoR2>\BepInEx\plugins\. Its dependencies are
//         bbepis-BepInExPack       (BepInEx itself, the mod loader)
//         tristanmcpherson-R2API   (the modding API)
//         RiskofThunder-HookGenPatcher
//
//   * CRITICAL HONESTY — THE THUNDERSTORE ZIP IS NOT SELF-SUFFICIENT. The AP mod
//     package contains only the Archipelago plugin (its manifest/icon/readme + the
//     plugin DLL). It does NOT bundle BepInEx, R2API or HookGenPatcher. The
//     OFFICIAL and RECOMMENDED install route is the r2modman / Thunderstore Mod
//     Manager, which installs "Archipelago" AND resolves every dependency in one
//     step. So this plugin leads the user to r2modman first, and the direct-zip
//     drop it can perform is a PARTIAL, best-effort fallback that still needs
//     BepInEx + R2API + HookGenPatcher present. Faking a one-click "fully
//     installed" that cannot exist would be dishonest theatre — the plugin says
//     exactly this in its post-install note and settings panel.
//
//   * CONNECTION is made IN-GAME (verified against the setup guide). After the mod
//     is installed and the game is started modded, an Archipelago lobby UI exposes
//     four fields — Slot Name, Password (blank if none), Server URL (default
//     archipelago.gg) and Server Port (default 38281) — and a "Connect to AP"
//     button. There is also an in-game console command
//         archipelago_connect <url> <port> <slot> [password]
//     (console opened with Ctrl+Alt+`). There is NO config file and NO command-
//     line arg this launcher can pre-write to seed the connection. So this plugin
//     does NOT attempt a connection prefill — the settings panel + note surface
//     the session's server / slot so the user can type them into those fields.
//
//   * TWO PLAY MODES (per the setup guide): "Classic" — send checks by causing
//     items to spawn (chests, bosses, scrappers/printers, lunar pods, terminals);
//     and "Explore" — send checks from actions (chests, shrines, scavengers, radio
//     scanners, newt altars). Which mode a seed uses is decided by the YAML on the
//     AP server, not by this launcher; both run through the same mod.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Risk of Rain 2 install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, HKLM\...\WOW6432Node\Valve\Steam
//      -> InstallPath), parsing steamapps\libraryfolders.vdf for every library
//      root and locating steamapps\common\Risk of Rain 2 via
//      appmanifest_632360.acf. A manual install-dir OVERRIDE (settings folder
//      picker) is also supported and takes precedence; it is validated (must
//      contain "Risk of Rain 2.exe") and persisted in this plugin's OWN sidecar
//      (Games/ROMs/ror2/ror2_launcher.json) — Core/SettingsStore is NOT modified.
//   2. INSTALL/UPDATE (best effort) = download the AP mod's Thunderstore zip and
//      extract the plugin into <RoR2>\BepInEx\plugins\Archipelago\. Because the
//      zip does not carry BepInEx / R2API / HookGenPatcher, the plugin ALSO
//      presents clear, numbered, guided steps + links (r2modman, the Thunderstore
//      package, the official RoR2 setup guide, archipelago.gg) so the user can
//      install everything via r2modman — the recommended route. Never a fake
//      one-click.
//   3. LAUNCH = run "Risk of Rain 2.exe" from the detected/override install; if the
//      exe cannot be found but Steam is present, fall back to
//      steam://rungameid/632360. ConnectsItself = true (the mod owns the slot — the
//      launcher must NOT hold its own ApClient on it). SupportsStandalone = true
//      (plain RoR2 runs perfectly without AP). No prefill (in-game lobby/console),
//      stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Hollow-Knight/Stardew-style) ─
//   * "Installed" is judged by the presence of an Archipelago plugin DLL under a
//     detected/override RoR2 install's BepInEx\plugins tree (case-insensitive,
//     recursive). The mod's exact DLL filename is not published in the setup guide,
//     so detection accepts any *.dll whose name mentions "archipelago", OR any
//     plugins sub-folder whose name mentions "archipelago" that contains a DLL —
//     so an r2modman install (which nests each mod in its own profile folder) still
//     counts. We do NOT gate on an OUR-OWN version stamp, because r2modman is the
//     recommended route and we honor it. If no RoR2 install is detected, the tile
//     simply reads "not installed".
//   * Steam library parsing is defensive: a hand-written tolerant VDF scan that
//     pulls quoted "path" values; any failure degrades to "Risk of Rain 2 not
//     found" rather than throwing.
//   * No plaintext AP password is ever written by this plugin (connection is
//     entered in-game), so there is nothing to scrub on stop.
//
//   * BUILD NOTE: this project sets UseWindowsForms=true alongside UseWPF=true, so
//     WPF UI types that also exist in WinForms (Color, Button, Brushes, MessageBox,
//     FontWeights, Orientation, …) are spelled with their FULL namespaces below to
//     avoid CS0104 ambiguity, independent of the project's GlobalUsings aliases.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class RiskOfRain2Plugin : IGamePlugin
{
    // ── Constants — the AP RoR2 mod (Thunderstore, verified 2026-06-14) ────────
    private const string TS_NAMESPACE = "Sneaki";
    private const string TS_NAME      = "Archipelago";

    /// Thunderstore package landing page (used for links + the manual route).
    private const string ModPackageUrl =
        $"https://thunderstore.io/package/{TS_NAMESPACE}/{TS_NAME}/";

    /// Thunderstore "experimental" package API — returns the package's version
    /// history (latest first) with each version's download_url. Used to resolve the
    /// newest mod zip and to build the news feed.
    private const string TS_PACKAGE_API_URL =
        $"https://thunderstore.io/api/experimental/package/{TS_NAMESPACE}/{TS_NAME}/";

    // r2modman — the RECOMMENDED installer; installs "Archipelago" plus every
    // dependency (BepInEx, R2API, HookGenPatcher) the mod zip does not carry.
    private const string R2ModManSite        = "https://thunderstore.io/package/ebkr/r2modman/";
    private const string ThunderstoreAppSite = "https://www.overwolf.com/app/thunderstore-thunderstore_mod_manager";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Risk%20of%20Rain%202/setup_en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Risk of Rain 2 appid 632360.
    private const string RoR2SteamAppId = "632360";
    private static readonly string SteamRunUrl = $"steam://rungameid/{RoR2SteamAppId}";

    /// The standard Steam install sub-folder name + the game exe.
    private const string SteamCommonFolderName = "Risk of Rain 2";
    private const string GameExeName           = "Risk of Rain 2.exe";

    /// Pinned fallback for the mod when the Thunderstore API is unreachable. 1.5.3
    /// verified live 2026-06-14; the version-pinned download URL serves the zip.
    private const string FallbackVersion = "1.5.3";
    private static readonly string FallbackZipUrl =
        $"https://thunderstore.io/package/download/{TS_NAMESPACE}/{TS_NAME}/{FallbackVersion}/";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-mod-version stamp, written after a direct-zip install (info only).
    private const string VersionFileName = "ror2_ap_mod_version.dat";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "ror2";
    public string DisplayName => "Risk of Rain 2";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/ror2/__init__.py
    /// (`game = "Risk of Rain 2"`). required_client_version = (0, 6, 4).
    public string ApWorldName => "Risk of Rain 2";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "ror2.png");

    public string ThemeAccentColor => "#3A6EA5";   // Petrichor V sky blue
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Risk of Rain 2, the roguelike third-person shooter by Hopoo Games, played " +
        "through the Archipelago mod — a BepInEx plugin that is an in-game " +
        "Archipelago client, so the game connects to the multiworld itself with no " +
        "emulator and no Lua bridge. Opening chests, defeating bosses, using " +
        "scrappers and printers, lunar pods, shrines, scavengers and more become " +
        "checks shuffled across the multiworld (Classic and Explore play modes are " +
        "supported, chosen by your YAML). You bring your own copy of Risk of Rain 2 " +
        "(owned on Steam), and the Archipelago mod is added on top with the r2modman " +
        "mod manager, which also installs the mods Archipelago depends on (BepInEx, " +
        "R2API, HookGenPatcher). The launcher detects your Steam install, can stage " +
        "the Archipelago mod files, and guides the rest. You connect to your server " +
        "from the in-game Archipelago lobby.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the AP RoR2 mod plugin is present in a detected/override
    /// RoR2 install's BepInEx\plugins tree. (We do NOT gate on our own stamp — the
    /// user may have installed via r2modman, the recommended route, which we honor.)
    public bool IsInstalled => FindInstalledModPlugin() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads (the mod zip) and bookkeeping. The actual
    /// mod is extracted INTO the Risk of Rain 2 install's BepInEx\plugins folder,
    /// not here. Exposed as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "RiskOfRain2");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Hollow Knight /
    /// Stardew / Jak). Per the brief, lives under Games/ROMs/ror2/.
    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "ror2_launcher.json");
    private string VersionFilePath
        => Path.Combine(RomLibraryDirectory, VersionFileName);

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Archipelago RoR2 mod reports checks/items/goal to the AP server itself —
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
            // Best-effort: read the version we stamped next to a direct-zip install
            // if present; otherwise report "installed" when the mod plugin exists.
            InstalledVersion = FindInstalledModPlugin() != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        try
        {
            var (version, _) = await ResolveLatestModAsync(ct);
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
        // 0. We need a Risk of Rain 2 install to drop the mod into. Prefer an
        //    explicit override; else auto-detect the Steam install.
        progress.Report((2, "Locating your Risk of Rain 2 installation..."));
        string? gameDir = ResolveRoR2Dir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Risk of Rain 2 installation. Open this game's " +
                "Settings and pick your Risk of Rain 2 folder (the one containing " +
                "\"Risk of Rain 2.exe\"), or install Risk of Rain 2 via Steam first. " +
                "The Archipelago mod is added on top of your own copy of the game.");

        string pluginsDir = Path.Combine(gameDir, "BepInEx", "plugins");
        string apModDir   = Path.Combine(pluginsDir, "Archipelago");

        // 1. Resolve the latest mod release (pinned fallback when offline).
        progress.Report((6, "Checking the latest Archipelago mod release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Archipelago Risk of Rain 2 mod download on " +
                "Thunderstore. Check your internet connection, or install the mod " +
                "with r2modman (recommended) — see Settings for the guided steps. " +
                "The mod package is " + ModPackageUrl + ".");

        // 2. Download + extract the mod zip INTO <RoR2>\BepInEx\plugins\Archipelago\.
        //    HONEST: this stages the Archipelago plugin only — BepInEx, R2API and
        //    HookGenPatcher are NOT in this zip and must be provided by r2modman.
        await DownloadAndExtractModAsync(zipUrl, version, apModDir, progress, ct);

        // 3. Stamp the version (informational only — IsInstalled is judged by the
        //    plugin's presence, not this stamp).
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepInExPresent = Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));
        progress.Report((100,
            $"Staged the Archipelago mod {version} into your Risk of Rain 2 " +
            "BepInEx\\plugins folder. " +
            (bepInExPresent
                ? "BepInEx looks present. "
                : "IMPORTANT: this download does NOT include BepInEx, R2API or " +
                  "HookGenPatcher, which Archipelago needs. The recommended way to " +
                  "install everything is the r2modman mod manager (one step installs " +
                  "Archipelago + all dependencies) — open Settings for the guided " +
                  "steps and links. ") +
            "To play: launch the game (modded), open the Archipelago lobby, and " +
            "enter your server URL, port and slot in the in-game fields."));
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
        // HONEST: the AP server connection for Risk of Rain 2 is entered IN-GAME in
        // the Archipelago lobby (Slot Name / Password / Server URL / Server Port +
        // "Connect to AP"), or via the in-game console command
        // `archipelago_connect <url> <port> <slot> [password]`. There is no
        // command-line / config prefill we can apply (verified — see header). So
        // launching from this tile just starts the (modded) game; the user connects
        // in-game with the session credentials (the settings panel + note surface
        // the session's server/slot to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartRiskOfRain2();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Risk of Rain 2 runs perfectly well.
    public bool SupportsStandalone => true;

    /// The Archipelago RoR2 mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartRiskOfRain2();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started Risk of Rain 2 from here. Kill what we launched.
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
        // The Archipelago RoR2 mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Risk of Rain 2 folder contains
    /// "Risk of Rain 2.exe". Return null when acceptable, else a short reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Risk of Rain 2 install folder.";

        if (LooksLikeRoR2Dir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeRoR2Dir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Risk of Rain 2 installation. Pick the folder " +
               "that contains \"Risk of Rain 2.exe\" — for Steam this is usually " +
               @"...\steamapps\common\Risk of Rain 2.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        string? gameDir     = ResolveRoR2Dir();
        string? overrideDir = LoadOverrideDir();
        string? modPlugin   = FindInstalledModPlugin();
        bool    bepInExOk   = gameDir != null && Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));

        // ── Unverified-offline honesty header ─────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Risk of Rain 2 is your own game (Steam) with the Archipelago mod " +
                   "added on top via BepInEx. The launcher detects your Steam install " +
                   "and can stage the Archipelago mod files, but the mod needs BepInEx, " +
                   "R2API and HookGenPatcher, which it does not bundle — the recommended " +
                   "way to install everything in one step is the r2modman mod manager " +
                   "(see the guided steps below). You connect to your server from the " +
                   "in-game Archipelago lobby. These external steps are not verified by " +
                   "this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "RISK OF RAIN 2 INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Risk of Rain 2 not detected. Pick your install folder below, or install " +
              "Risk of Rain 2 via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // BepInEx + mod status lines
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = bepInExOk
                    ? "BepInEx found (BepInEx\\core present)."
                    : "BepInEx not found yet — install it via r2modman (recommended).",
            FontSize = 11, Foreground = bepInExOk ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modPlugin != null
                    ? "Archipelago mod found: " + modPlugin
                    : "Archipelago mod not found in BepInEx\\plugins yet (use Install on " +
                      "the Play tab, or install it via r2modman — recommended).",
            FontSize = 11, Foreground = modPlugin != null ? success : muted,
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
            ToolTip     = "Your Risk of Rain 2 install folder (the one containing " +
                          "\"Risk of Rain 2.exe\"). Detected from Steam automatically; set " +
                          "it here to override (non-standard Steam library, etc.).",
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
                Title            = "Select your Risk of Rain 2 install folder",
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
                    System.Windows.MessageBox.Show(bad, "Not a Risk of Rain 2 folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeRoR2Dir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeRoR2Dir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 632360). Use this " +
                   "picker for a non-standard Steam library or a manual install.",
            FontSize = 11, Foreground = muted, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (this session) ────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game in the Archipelago lobby)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Launch the game (modded), open the Archipelago lobby, and fill in: " +
                   "Server URL (default archipelago.gg), Server Port (default 38281), " +
                   "Slot Name, and Password (leave blank if none). Then click " +
                   "\"Connect to AP\". You can also use the in-game console (Ctrl+Alt+`): " +
                   "archipelago_connect <url> <port> <slot> [password].",
            FontSize = 11, Foreground = fg, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP (recommended: r2modman mod manager)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Risk of Rain 2 (Steam). Install it if you have not. Use \"Select folder...\" " +
                "above if it was not detected.",
            "2. Install the r2modman mod manager (or the Thunderstore Mod Manager) from the " +
                "links below, and select Risk of Rain 2 as the game.",
            "3. In r2modman, search for and install \"Archipelago\" (by Sneaki). Its " +
                "dependencies (BepInEx, R2API, HookGenPatcher) are installed automatically. " +
                "Then click \"Start modded\".",
            "4. Alternative (advanced): the Install button on the Play tab stages the mod's own " +
                "files into your BepInEx\\plugins\\Archipelago folder, but it does NOT include " +
                "BepInEx / R2API / HookGenPatcher — you would still need those from r2modman.",
            "5. To play: launch the game (modded). On the title screen open the Archipelago " +
                "lobby, enter your Server URL, Port, Slot Name and optional Password, then click " +
                "\"Connect to AP\". (This launcher cannot pre-fill the connection — it is entered " +
                "in-game.) In multiplayer, every player needs the mod.",
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
            Text = "LINKS", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("r2modman (Thunderstore) ↗",          R2ModManSite),
            ("Thunderstore Mod Manager ↗",         ThunderstoreAppSite),
            ("Archipelago mod (Thunderstore) ↗",   ModPackageUrl),
            ("Risk of Rain 2 Setup Guide ↗",       SetupGuideUrl),
            ("Archipelago Official ↗",             ArchipelagoSite),
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
        // The Thunderstore package's version history is the AP-relevant news for
        // this game. The experimental package API returns { versions: [ { name,
        // version_number, description, date_created, ... }, ... ] } newest-first.
        try
        {
            string json = await _http.GetStringAsync(TS_PACKAGE_API_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("versions", out var versions) ||
                versions.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in versions.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("date_created", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                string ver = el.TryGetProperty("version_number", out var v) ? v.GetString() ?? "" : "";

                items.Add(new NewsItem(
                    Title:   "Archipelago " + ver,
                    Body:    el.TryGetProperty("description", out var b) ? b.GetString() ?? "" : "",
                    Version: ver,
                    Date:    date,
                    Url:     el.TryGetProperty("download_url", out var u) ? u.GetString() : ModPackageUrl
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// Resolve the latest mod release from the Thunderstore experimental package
    /// API: version_number + its download_url. Falls back to the pinned 1.5.3
    /// version-download URL when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(TS_PACKAGE_API_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Preferred shape: { latest: { version_number, download_url }, ... }.
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("latest", out var latest) &&
                latest.ValueKind == JsonValueKind.Object)
            {
                string? ver = latest.TryGetProperty("version_number", out var lv) ? lv.GetString() : null;
                string? url = latest.TryGetProperty("download_url", out var lu) ? lu.GetString() : null;
                if (!string.IsNullOrWhiteSpace(ver) && !string.IsNullOrWhiteSpace(url))
                    return (ver!, url);
            }

            // Fallback shape: the first entry of the versions array (newest-first).
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("versions", out var versions) &&
                versions.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in versions.EnumerateArray())
                {
                    string? ver = el.TryGetProperty("version_number", out var ev) ? ev.GetString() : null;
                    string? url = el.TryGetProperty("download_url", out var eu) ? eu.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(ver) && !string.IsNullOrWhiteSpace(url))
                        return (ver!, url);
                    break; // only the first (latest) entry is of interest
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / shape changed → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — Steam / Risk of Rain 2 detection ────────────────────

    /// The RoR2 install dir to use: the override (if set and valid) wins, else the
    /// Steam-detected install. Null when nothing is found.
    private string? ResolveRoR2Dir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeRoR2Dir(ov))
            return ov;

        try { return DetectSteamRoR2Dir(); }
        catch { return null; }
    }

    /// A folder "looks like" Risk of Rain 2 if it contains "Risk of Rain 2.exe".
    private static bool LooksLikeRoR2Dir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

    /// Detect the Steam Risk of Rain 2 install: read the Steam root from the
    /// registry, gather all library roots from libraryfolders.vdf, and find the one
    /// whose appmanifest_632360.acf exists → steamapps\common\Risk of Rain 2.
    private static string? DetectSteamRoR2Dir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{RoR2SteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    // Prefer the installdir named in the manifest; fall back to the
                    // conventional "Risk of Rain 2" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeRoR2Dir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeRoR2Dir(conventional)) return conventional;
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

        string? hklm2 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm2)) yield return NormalizeSteamPath(hklm2);

        // Last-ditch conventional location.
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

    // ── Private helpers — installed-mod detection ─────────────────────────────

    /// Find the AP RoR2 mod plugin under the detected/override install's
    /// BepInEx\plugins tree (recursive, case-insensitive). The exact DLL filename
    /// is not published, so we accept either a *.dll whose name mentions
    /// "archipelago", or a plugins sub-folder whose name mentions "archipelago"
    /// that contains at least one DLL (the r2modman layout). Returns the matched
    /// path or null.
    private string? FindInstalledModPlugin()
    {
        try
        {
            string? game = ResolveRoR2Dir();
            if (game == null) return null;
            string pluginsDir = Path.Combine(game, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            // 1. A DLL named like Archipelago anywhere under plugins.
            foreach (string dll in Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }

            // 2. A plugins sub-folder named like Archipelago that holds a DLL.
            foreach (string sub in Directory.EnumerateDirectories(pluginsDir, "*", SearchOption.AllDirectories))
            {
                string folder = Path.GetFileName(sub);
                if (folder.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) < 0) continue;
                try
                {
                    if (Directory.EnumerateFiles(sub, "*.dll", SearchOption.AllDirectories).Any())
                        return sub;
                }
                catch { /* permission — keep scanning */ }
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Risk of Rain 2: prefer the exe in the detected/override install; if
    /// that cannot be found but Steam is present, fall back to the steam:// URL.
    /// Surfaces a clear message rather than failing opaquely.
    private void StartRiskOfRain2()
    {
        string? game = ResolveRoR2Dir();
        string? exe  = game != null ? Path.Combine(game, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = game!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Risk of Rain 2.");

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
            "Could not find \"Risk of Rain 2.exe\". Open this game's Settings and pick " +
            "your Risk of Rain 2 install folder, or install Risk of Rain 2 via Steam.",
            GameExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod's Thunderstore zip and extract it into
    /// <RoR2>\BepInEx\plugins\Archipelago\. Honest scope: this stages the
    /// Archipelago plugin only; BepInEx / R2API / HookGenPatcher come from r2modman.
    ///
    /// A Thunderstore zip is a "mod package": it contains manifest.json, icon.png,
    /// README.md at the top, plus the plugin payload. For BepInEx mods the payload
    /// is usually the plugin DLL at the zip root or under a "plugins" sub-folder, or
    /// occasionally a nested "BepInEx/plugins/..." tree. We extract to a temp
    /// folder, then copy the plugin payload into the install's plugins\Archipelago
    /// so the DLL ends up where BepInEx will load it regardless of the zip's shape.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string apModDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"ror2-archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"ror2-archipelago-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading Archipelago mod {version}..."));
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
                        progress.Report((pct, $"Downloading Archipelago mod... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting the mod package..."));
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            progress.Report((85, "Installing mod into the Risk of Rain 2 plugins folder..."));
            Directory.CreateDirectory(apModDir);

            // Find the directory that actually holds the plugin DLL(s): a nested
            // "BepInEx/plugins" wins; else a "plugins" sub-folder; else the zip root
            // (the plugin DLL sits alongside manifest.json). Copy that folder's
            // contents into plugins\Archipelago.
            string payloadRoot = ResolvePluginPayloadRoot(tempDir);
            CopyDirectoryContents(payloadRoot, apModDir);

            progress.Report((95, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))  File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// Decide which extracted folder holds the BepInEx plugin payload to install.
    /// Order: a nested "BepInEx/plugins" (use the deepest folder that contains a
    /// DLL within it), then a "plugins" sub-folder, then the extraction root.
    private static string ResolvePluginPayloadRoot(string extractedRoot)
    {
        try
        {
            // 1. .../BepInEx/plugins (the canonical modded layout).
            foreach (string dir in Directory.EnumerateDirectories(extractedRoot, "plugins", SearchOption.AllDirectories))
            {
                string parent = Path.GetFileName(Path.GetDirectoryName(dir) ?? "");
                if (parent.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) &&
                    DirectoryHasDll(dir))
                    return dir;
            }

            // 2. A top-level "plugins" folder with a DLL inside.
            string topPlugins = Path.Combine(extractedRoot, "plugins");
            if (Directory.Exists(topPlugins) && DirectoryHasDll(topPlugins))
                return topPlugins;
        }
        catch { /* fall through to the root */ }

        // 3. The extraction root (Thunderstore puts the DLL next to manifest.json).
        return extractedRoot;
    }

    private static bool DirectoryHasDll(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }

    /// Recursively copy a directory's contents into a destination folder
    /// (overwriting), creating sub-folders as needed.
    private static void CopyDirectoryContents(string sourceDir, string destDir)
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
    // UTF-8, read-modify-write (same approach as Hollow Knight / Stardew / Jak).

    private sealed class RoR2Settings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private RoR2Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<RoR2Settings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(RoR2Settings s)
    {
        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
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
