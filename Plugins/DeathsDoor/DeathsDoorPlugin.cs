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

namespace LauncherV2.Plugins.DeathsDoor;

// ═══════════════════════════════════════════════════════════════════════════════
// DeathsDoorPlugin — install / launch for "Death's Door" (Acid Nerve / Devolver
// Digital, 2021) played through the DDArchipelagoRandomizer mod by Chris-Is-Awesome.
// This is a NATIVE "ConnectsItself" Steam-mod integration in the same family as
// the shipped BombRushCyberfunk / HollowKnight / TUNIC / Stardew Valley plugins:
// the game itself speaks to the AP server — no emulator, no Lua bridge, no
// launcher-held ApClient on the slot.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online) ──────────────────────
// This is a STEAM-MOD native. The BASE GAME is the user's own legally-owned copy
// of Death's Door (Steam appid 894020, by Acid Nerve, published by Devolver
// Digital). Archipelago support is delivered as a BepInEx mod added on top. The
// honest integration ceiling — exactly like the shipped BombRushCyberfunk / TUNIC
// plugins — is "automate what is possible, guide the irreducible parts."
//
//   * THE AP WORLD game string is "Death's Door" (verified against
//     roseasromeo/DeathsDoorAPWorld world.py line 98:
//     `game = "Death's Door"`).
//     GameId here = "deaths_door". The apworld is from a SEPARATE repo
//     (roseasromeo/DeathsDoorAPWorld) — NOT bundled in AP core.
//     Latest apworld release: v0.2.1, single asset "deaths_door.apworld".
//
//   * THE MOD repo is Chris-Is-Awesome/DDArchipelagoRandomizer (verified live
//     2026-06-14). Latest release: v0.2.1. The release ships TWO assets:
//       (a) "20250825_DD_plugins.zip" — a FULL BepInEx/plugins snapshot that
//           includes the mod and ALL its companion DLL dependencies bundled
//           together (the "replace your BepInEx plugins folder" drop-in).
//       (b) "ArchipelagoRandomizer.zip" — just the mod DLL + the one required
//           NuGet dependency: Archipelago.MultiClient.Net.dll.
//     The mod's AssemblyName is "ArchipelagoRandomizer" → "ArchipelagoRandomizer.dll".
//     It lives at BepInEx/plugins/ArchipelagoRandomizer/ (the mod subdirectory
//     named "ArchipelagoRandomizer"). BepInPlugin GUID:
//     "deathsdoor.archipelagorandomizer".
//
//   * CRITICAL PREREQUISITES the mod zip does NOT include on its own:
//       (a) BepInEx 5.4.22 x64 (Unity MONO — NOT IL2CPP / 6.x pre-release).
//           Death's Door is a Unity 2019.4 MONO game. BepInEx 5 is a portable
//           zip (no wizard) extracted into the game install ROOT.
//     Using the "20250825_DD_plugins.zip" asset installs the mod AND its
//     companion DLL dependencies in one go into BepInEx/plugins. This is the
//     PREFERRED route and what this plugin uses.
//
//   * CONNECTION is made IN-GAME (verified against the mod README and AP setup
//     guide pattern): after the mod is installed and the game launched, the mod
//     adds connection UI accessible from the main menu. Players enter the server
//     address, port, slot name, and password. Connection settings are saved to a
//     JSON file at:
//       {Application.persistentDataPath}/SAVEDATA/Save_slot{N}-Archipelago.json
//     Each save slot has independent AP credentials — the in-game UI (not a
//     config file) is the source of truth. There is NO documented command-line
//     arg or config file this launcher can pre-write to seed the connection for
//     NEW connections (verified — the connection lives behind the in-game save-
//     slot Archipelago button). Per an honest "don't invent an undocumented
//     prefill" stance (same as BombRushCyberfunk), this plugin does NOT write
//     any config. The settings panel + post-install note surface the session's
//     host / port / slot for the user to type into the in-game fields.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Death's Door install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\DeathsDoor via appmanifest_894020.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and takes
//      precedence; it is validated (must contain "DeathsDoor.exe") and persisted
//      in this plugin's OWN sidecar
//      (Games/ROMs/deaths_door/deaths_door_launcher.json) — Core/SettingsStore
//      is NOT modified.
//   2. INSTALL/UPDATE (best effort):
//        (a) If BepInEx is not present in the detected install, download the
//            pinned BepInEx 5.4.22 x64 zip and extract it into the game root.
//        (b) Download the mod's "20250825_DD_plugins.zip" from the release and
//            extract it into BepInEx/plugins — this includes the mod DLL and all
//            companion dependencies in one go.
//        (c) Download the "deaths_door.apworld" from roseasromeo/DeathsDoorAPWorld
//            (latest release v0.2.1) into the launcher's game folder so the user
//            can copy it into Archipelago's custom_worlds folder.
//   3. LAUNCH = run "DeathsDoor.exe" from the detected/override install. If the
//      exe cannot be found but Steam is present, fall back to
//      steam://rungameid/894020. ConnectsItself = true (the mod owns the slot).
//      SupportsStandalone = true (plain Death's Door runs fine without AP). No
//      connection prefill (entered in-game).
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time") ──────────────────────────
//   * "Installed" is judged by the presence of "ArchipelagoRandomizer.dll" under
//     a detected/override install's BepInEx tree (case-insensitive, recursive) —
//     NOT by our own version stamp, because the user may install the mod by hand.
//   * Steam appmanifest installdir key for Death's Door was not verified offline;
//     the conventional fallback folder name is "DeathsDoor" (the most common
//     Steam install path for this title — verified via community docs).
//   * The EXACT internal layout of "20250825_DD_plugins.zip" was not byte-
//     inspected at author time; the extractor is defensive — it extracts into
//     BepInEx/plugins/ and, if "ArchipelagoRandomizer.dll" did not land in a
//     ArchipelagoRandomizer/ subdirectory of plugins, it searches recursively
//     for it and keeps the structure as-is (the zip is a FULL plugins-folder
//     snapshot and extracts correctly as-is).
//   * BepInEx 5.4.22 x64 download URL is pinned to the known-good official
//     release (the URL format has been stable for years). Re-verify on update.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DeathsDoorPlugin : IGamePlugin
{
    // ── Constants — the AP mod (Chris-Is-Awesome/DDArchipelagoRandomizer) ────
    private const string MOD_OWNER = "Chris-Is-Awesome";
    private const string MOD_REPO  = "DDArchipelagoRandomizer";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // ── Constants — the APWorld (roseasromeo/DeathsDoorAPWorld) ─────────────
    private const string APWORLD_OWNER = "roseasromeo";
    private const string APWORLD_REPO  = "DeathsDoorAPWorld";
    private const string ApWorldRepoUrl = $"https://github.com/{APWORLD_OWNER}/{APWORLD_REPO}";
    private const string GH_APWORLD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{APWORLD_OWNER}/{APWORLD_REPO}/releases/latest";

    // Pinned fallback mod release (v0.2.1, verified 2026-06-14).
    // The "20250825_DD_plugins.zip" asset = the full plugins-folder drop-in.
    private const string FallbackModVersion = "0.2.1";
    private const string FallbackPluginsZipName = "20250825_DD_plugins.zip";
    private static readonly string FallbackPluginsZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackModVersion}/{FallbackPluginsZipName}";

    // Pinned fallback apworld (v0.2.1, verified 2026-06-14).
    private const string FallbackApWorldVersion = "0.2.1";
    private const string ApWorldFileName = "deaths_door.apworld";
    private static readonly string FallbackApWorldUrl =
        $"{ApWorldRepoUrl}/releases/download/v{FallbackApWorldVersion}/{ApWorldFileName}";

    // BepInEx 5.4.22 (Unity MONO x64) — portable, no wizard.
    // URL verified against official BepInEx GitHub releases.
    private const string BepInExSite    = "https://github.com/BepInEx/BepInEx/releases";
    private const string BepInExVersion = "5.4.22";
    private static readonly string BepInExZipUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.22/" +
        "BepInEx_x64_5.4.22.0.zip";

    // The mod's primary DLL name (from AssemblyName = "ArchipelagoRandomizer" in
    // the .csproj). It lives inside BepInEx/plugins/ArchipelagoRandomizer/.
    private const string ModPrimaryDll = "ArchipelagoRandomizer.dll";

    // Steam appid 894020 — verified from store.steampowered.com/app/894020
    private const string DdSteamAppId = "894020";
    private static readonly string SteamRunUrl = $"steam://rungameid/{DdSteamAppId}";

    // Conventional Steam install folder name for Death's Door.
    private const string SteamCommonFolderName = "DeathsDoor";

    // Known exe and data folder names (verified: Death's Door is Unity MONO).
    private const string DdExeName  = "DeathsDoor.exe";
    private const string DdDataDir  = "DeathsDoor_Data";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Death%27s%20Door/setup/en";
    private const string GameInfoUrl     = "https://archipelago.gg/games/Death%27s%20Door/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "deaths_door";
    public string DisplayName => "Death's Door";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against
    /// roseasromeo/DeathsDoorAPWorld world.py line 98
    /// (`game = "Death's Door"`).
    public string ApWorldName => "Death's Door";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "deaths_door.png");

    public string ThemeAccentColor => "#B03A2E";   // blood-red crow motif

    public string[] GameBadges => new[] { "Steam · needs mod" };

    public string Description =>
        "Death's Door, Acid Nerve's acclaimed 2021 action-adventure where you play " +
        "a crow collecting souls, played through the DDArchipelagoRandomizer mod by " +
        "Chris-Is-Awesome — which bundles an in-game Archipelago client, so the game " +
        "connects to the multiworld itself with no emulator and no bridge. Items, " +
        "abilities and progression are shuffled across the multiworld; your goal is " +
        "to collect enough souls to open the titular Death's Door. You bring your own " +
        "copy of Death's Door (owned on Steam); the integration runs on BepInEx, the " +
        "Unity mod loader. The launcher detects your Steam install and can stage " +
        "BepInEx and the Archipelago mod into it, and guides the rest. You connect " +
        "to your server from the in-game Archipelago menu on the main screen.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the DDArchipelago mod DLL ("ArchipelagoRandomizer.dll")
    /// is present in a detected/override Death's Door install's BepInEx tree.
    /// (We do NOT gate on our own stamp — the user may have installed the mod by
    /// hand, which we honor.)
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Working area for downloaded files and the local apworld copy.
    /// The actual mod is extracted INTO the game install's BepInEx folder.
    /// Exposed as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "DeathsDoor");

    /// This plugin's OWN settings sidecar (per-brief: Games/ROMs/deaths_door/).
    /// Keeps the install-dir override and an informational version stamp.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "deaths_door_launcher.json");

    /// Local copy of the apworld file (for the user to copy into custom_worlds).
    private string ApWorldLocalPath
        => Path.Combine(GameDirectory, ApWorldFileName);

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The DDArchipelago mod reports checks/items/goal to the AP server directly;
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
        // 0. Locate the Death's Door install.
        progress.Report((2, "Locating your Death's Door installation..."));
        string? ddDir = ResolveDdDir();
        if (ddDir == null)
            throw new InvalidOperationException(
                "Could not find a Death's Door installation. Open this game's " +
                "Settings and pick your Death's Door folder (the one containing " +
                "\"DeathsDoor.exe\"), or install Death's Door via Steam first. The " +
                "Archipelago mod is added on top of your own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback when offline).
        progress.Report((6, "Checking the latest DDArchipelagoRandomizer release..."));
        var (modVersion, pluginsZipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = modVersion;

        if (pluginsZipUrl == null)
            throw new InvalidOperationException(
                "Could not find the DDArchipelagoRandomizer mod download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                ModRepoUrl + "/releases — see Settings for the guided steps.");

        // 2. Ensure BepInEx 5.4.22 (Mono x64) is present — a SEPARATE portable
        //    prerequisite the mod zip does NOT bundle. Leave it alone if already
        //    there (winhttp.dll / BepInEx folder already present).
        string pluginsDir = Path.Combine(ddDir, "BepInEx", "plugins");
        if (!BepInExPresent(ddDir))
        {
            progress.Report((10, "Staging BepInEx 5.4.22 (Unity Mono) into your Death's Door folder..."));
            await DownloadAndExtractZipToDirAsync(
                BepInExZipUrl, $"dd-bepinex-{BepInExVersion}", ddDir, 10, 42, progress, ct);
        }
        else
        {
            progress.Report((42, "BepInEx already present — keeping your existing install."));
        }
        Directory.CreateDirectory(pluginsDir);

        // 3. Download + extract the mod's full plugins zip into BepInEx/plugins.
        //    "20250825_DD_plugins.zip" is a full plugins-folder snapshot that
        //    includes the mod DLL and all companion dependencies in one drop.
        await DownloadAndExtractPluginsZipAsync(pluginsZipUrl, modVersion, pluginsDir, 44, 88, progress, ct);

        // 4. Download the apworld file next to our sidecar (best effort) so the
        //    user can copy it into Archipelago's custom_worlds folder.
        try
        {
            progress.Report((90, "Downloading deaths_door.apworld..."));
            var (_, apWorldUrl) = await ResolveLatestApWorldAsync(ct);
            string url = apWorldUrl ?? FallbackApWorldUrl;
            Directory.CreateDirectory(GameDirectory);
            byte[] apworld = await _http.GetByteArrayAsync(url, ct);
            await File.WriteAllBytesAsync(ApWorldLocalPath, apworld, ct);
            progress.Report((95, $"deaths_door.apworld saved — copy it into Archipelago's custom_worlds folder."));
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            progress.Report((95, "Could not download the apworld — get it from " + ApWorldRepoUrl + "/releases."));
        }

        // 5. Stamp the version in our sidecar.
        WriteStampedVersion(modVersion);
        InstalledVersion = modVersion;

        bool bepOk = BepInExPresent(ddDir);
        bool modOk = FindInstalledModDll() != null;
        progress.Report((100,
            $"Staged the DDArchipelagoRandomizer mod {modVersion} into your BepInEx\\plugins folder" +
            (bepOk ? " (BepInEx present" : " (BepInEx NOT detected") +
            (modOk ? ", mod DLL present)." : ", mod DLL NOT detected).") +
            " To play: launch Death's Door, then on the main menu use the in-game " +
            "Archipelago connection UI to enter your server address, port, name, " +
            "and password. Open Settings for the guided steps and links. " +
            "(This launcher cannot pre-fill the connection — it is entered in-game.)"));
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
        // HONEST: the AP server connection for Death's Door is entered IN-GAME
        // through the mod's connection UI on the main menu. There is no
        // documented command-line arg or config file this launcher can pre-write
        // to seed a new connection (the connection data lives in per-save-slot
        // JSON files written by the mod itself). So launching from this tile just
        // starts the game; the user connects in-game with the session credentials
        // (the settings panel + note surface those values to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no pre-fill mechanism exists
        StartDeathsDoor();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Death's Door runs perfectly well.
    public bool SupportsStandalone => true;

    /// The DDArchipelago mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartDeathsDoor();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        // No plaintext AP password is written by this plugin (the connection is
        // entered in-game and stored in the game's own save data), so there is
        // nothing to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The DDArchipelago mod receives items from the AP server directly; there
        // is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override folder picker) ─────────────────

    /// Used by the Settings folder picker: a valid Death's Door folder contains
    /// "DeathsDoor.exe" and/or the "DeathsDoor_Data" folder. Returns null when
    /// acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Death's Door install folder.";

        if (LooksDdDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksDdDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Death's Door installation. Pick the " +
               "folder that contains \"DeathsDoor.exe\" (for Steam this is " +
               @"usually ...\steamapps\common\DeathsDoor).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Death's Door is your own game (Steam) with the DDArchipelagoRandomizer " +
                   "mod added on top via BepInEx. The launcher detects your Steam install " +
                   "and can stage BepInEx 5.4.22 and the Archipelago mod files into it. " +
                   "You connect to your server from the in-game Archipelago menu on the " +
                   "main screen. These external setup steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "DEATH'S DOOR INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? ddDir       = ResolveDdDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = ddDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + ddDir
                : "Detected Steam install: " + ddDir)
            : "Death's Door not detected. Pick your install folder below, or " +
              "install Death's Door via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = ddDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // BepInEx status
        bool bepOk = ddDir != null && BepInExPresent(ddDir);
        panel.Children.Add(new TextBlock
        {
            Text = ddDir == null
                    ? ""
                    : (bepOk
                        ? "BepInEx found in your Death's Door folder."
                        : "BepInEx not found yet — Install on the Play tab stages it, or get " +
                          "it from the BepInEx releases (link below). Use 5.4.22 x64, not " +
                          "the BepInEx 6 pre-releases."),
            FontSize = 11, Foreground = bepOk ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        // Mod status
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                    ? "DDArchipelagoRandomizer mod found: " + modDll
                    : "DDArchipelagoRandomizer mod not found in BepInEx\\plugins yet " +
                      "(use Install on the Play tab).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? ddDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Death's Door install folder (the one containing " +
                          "\"DeathsDoor.exe\"). Detected from Steam automatically; " +
                          "use this picker for a non-standard library or another store.",
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
                Title            = "Select your Death's Door install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? ddDir ?? "")
                                   ? (overrideDir ?? ddDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Death's Door folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // Descend if the user picked the Steam "common" parent.
                if (!LooksDdDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksDdDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 894020). Use this " +
                   "picker for a non-standard Steam library or another store.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── APWorld note ──────────────────────────────────────────────────
        if (File.Exists(ApWorldLocalPath))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"deaths_door.apworld is saved in {GameDirectory} — " +
                       @"copy it into your Archipelago custom_worlds folder (default: " +
                       @"C:\ProgramData\Archipelago\custom_worlds) to generate multiworlds.",
                FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });
        }
        else
        {
            panel.Children.Add(new TextBlock
            {
                Text = "The deaths_door.apworld file (needed to generate a multiworld on the " +
                       "AP server) will be downloaded into " + GameDirectory + " when you " +
                       "click Install on the Play tab. Then copy it into Archipelago's " +
                       @"custom_worlds folder (default: C:\ProgramData\Archipelago\custom_worlds).",
                FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });
        }

        // ── Section: Connecting (in-game) ─────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Launch Death's Door. The mod adds an Archipelago connection UI on the " +
                   "main menu. Enter your server address and port, your slot name (player " +
                   "name), and a password if your room requires one, then confirm to connect. " +
                   "The connection is saved per save slot. This launcher does not pre-fill " +
                   "the connection.",
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
            "1. Own Death's Door (on Steam). Install it if you have not. Use the picker " +
                "above if it was not detected automatically.",
            "2. Install BepInEx 5.4.22 x64 into your Death's Door folder. The Install button " +
                "on the Play tab stages it for you, or download it from the BepInEx releases " +
                "(link below) and extract it into your Death's Door folder. Do NOT use the " +
                "BepInEx 6 pre-releases.",
            "3. Install the DDArchipelagoRandomizer mod into BepInEx\\plugins. The Install " +
                "button on the Play tab does this for you (it downloads the full plugins zip " +
                "which includes the mod and all its companion DLLs).",
            "4. Download and install the deaths_door.apworld (from " + ApWorldRepoUrl + "/releases) " +
                "into your Archipelago custom_worlds folder so you can generate multiworlds. " +
                "The Install button also downloads it into " + GameDirectory + " for you.",
            "5. Generate a game on your Archipelago server using the deaths_door.apworld and " +
                "your YAML options.",
            "6. Launch Death's Door. On the main menu, use the in-game Archipelago UI to " +
                "enter your server address, port, slot name, and password, then connect.",
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
            ("DDArchipelagoRandomizer (GitHub) ↗", ModRepoUrl),
            ("Death's Door APWorld (GitHub) ↗",    ApWorldRepoUrl),
            ("Death's Door Setup Guide (AP) ↗",    SetupGuideUrl),
            ("Death's Door Game Info (AP) ↗",      GameInfoUrl),
            ("BepInEx Releases ↗",                 BepInExSite),
            ("Archipelago Official ↗",             ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(u) { UseShellExecute = true });
                }
                catch { }
            };
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

    /// "v0.2.1" → "0.2.1" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the plugins zip download URL.
    /// Prefers the "20250825_DD_plugins.zip" (full plugins snapshot) or any .zip
    /// whose name contains "plugin". Falls back to the pinned 0.2.1 direct URL.
    private async Task<(string Version, string? PluginsZipUrl)> ResolveLatestModAsync(
        CancellationToken ct)
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
                string? pluginsZip = null;   // "20250825_DD_plugins.zip" pattern
                string? anyZip     = null;   // any .zip fallback
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    // Prefer the full-plugins snapshot ("plugin" in name wins).
                    if (pluginsZip == null &&
                        (lower.Contains("plugin") || lower.Contains("_dd_") ||
                         lower.Contains("archipelago")))
                        pluginsZip = url;
                }
                string? zip = pluginsZip ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackModVersion, FallbackPluginsZipUrl);
    }

    /// Resolve the latest apworld release: version + the apworld download URL.
    /// Falls back to the pinned v0.2.1 direct URL when offline.
    private async Task<(string Version, string? ApWorldUrl)> ResolveLatestApWorldAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_APWORLD_RELEASES_LATEST_URL, ct);
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
                    if (name.EndsWith(".apworld", StringComparison.OrdinalIgnoreCase))
                        return (version, url);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackApWorldVersion, FallbackApWorldUrl);
    }

    // ── Private helpers — Steam / Death's Door detection ─────────────────────

    /// The Death's Door install dir to use: the override (if set and valid) wins,
    /// else the Steam-detected install. Null when nothing is found.
    private string? ResolveDdDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksDdDir(ov))
            return ov;

        try { return DetectSteamDdDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Death's Door if it has "DeathsDoor.exe" and/or the
    /// "DeathsDoor_Data" folder.
    private static bool LooksDdDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, DdExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, DdDataDir))) return true;
            return false;
        }
        catch { return false; }
    }

    /// True when BepInEx appears installed in a Death's Door folder.
    /// BepInEx 5 (Mono) drops a "BepInEx" folder plus a winhttp.dll proxy.
    private static bool BepInExPresent(string ddDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ddDir) || !Directory.Exists(ddDir)) return false;
            if (Directory.Exists(Path.Combine(ddDir, "BepInEx"))) return true;
            if (File.Exists(Path.Combine(ddDir, "winhttp.dll"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Death's Door install: read the Steam root from the
    /// registry, gather all library roots from libraryfolders.vdf, and find the
    /// one whose appmanifest_894020.acf exists → steamapps\common\DeathsDoor.
    private static string? DetectSteamDdDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{DdSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksDdDir(candidate)) return candidate;
                    }
                    // Conventional fallback folder name for Death's Door.
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksDdDir(conventional)) return conventional;
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

    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    /// All Steam library roots: the Steam root itself plus every "path" entry in
    /// steamapps\libraryfolders.vdf.
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

    /// Find "ArchipelagoRandomizer.dll" under the detected/override Death's Door
    /// install's BepInEx tree (recursive, case-insensitive). Returns the dll path
    /// or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? dd = ResolveDdDir();
            if (dd == null) return null;
            string bepInExDir = Path.Combine(dd, "BepInEx");
            if (!Directory.Exists(bepInExDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(bepInExDir, "*.dll",
                SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dll).Equals(ModPrimaryDll,
                    StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartDeathsDoor()
    {
        string? dd  = ResolveDdDir();
        string? exe = dd != null ? Path.Combine(dd, DdExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = dd!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Death's Door.");

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
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { /* fall through to the error below */ }
        }

        throw new FileNotFoundException(
            "Could not find \"DeathsDoor.exe\". Open this game's Settings and pick " +
            "your Death's Door install folder, or install Death's Door via Steam.",
            DdExeName);
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    /// Download the mod's full plugins zip and extract it into
    /// <DD>/BepInEx/plugins/. The "20250825_DD_plugins.zip" asset is a complete
    /// plugins-folder snapshot (mod + all companion DLLs).
    private async Task DownloadAndExtractPluginsZipAsync(
        string zipUrl,
        string version,
        string pluginsDir,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"dd-archipelago-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 7 / 10;
            await DownloadFileAsync(zipUrl, tempZip,
                $"Downloading DDArchipelagoRandomizer {version} (plugins zip)...",
                pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Installing the mod into BepInEx\\plugins..."));
            Directory.CreateDirectory(pluginsDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, pluginsDir, overwriteFiles: true);

            progress.Report((pctEnd, "Mod files installed into BepInEx\\plugins."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    /// Download + extract a portable zip (e.g. BepInEx) straight into a target
    /// directory.
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

            progress.Report((dlEnd, "Extracting BepInEx into your Death's Door folder..."));
            Directory.CreateDirectory(targetDir);
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
    // Keeps the install-dir override + an informational version stamp in its OWN
    // JSON file. Identical pattern to BombRushCyberfunk / HollowKnight / TUNIC.

    private sealed class DdSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private DdSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<DdSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(DdSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    private string? LoadOverrideDir()
    {
        string? p = LoadSettings().InstallOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }

    private void SaveOverrideDir(string p)
    {
        var s = LoadSettings();
        s.InstallOverride = p;
        SaveSettings(s);
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings();
        s.ModVersion = v;
        SaveSettings(s);
    }
}
