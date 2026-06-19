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
// We also do NOT add any file-level `using X = System.Windows...;` aliases — the
// project's GlobalUsings.cs already aliases the short names, and a second local
// alias would be CS1537 (duplicate alias). Bare names or fully-qualified only.

namespace LauncherV2.Plugins.SonicAdventure2Battle;

// ═══════════════════════════════════════════════════════════════════════════════
// SonicAdventure2BattlePlugin — install / launch for "Sonic Adventure 2: Battle"
// (SEGA / Sonic Team, Steam appid 213610) played through the SA2B_Archipelago mod,
// the in-game Archipelago client for SA2B. This is a NATIVE "ConnectsItself"
// integration in the same family as the shipped Hollow Knight / Subnautica / Raft
// / Stardew Valley plugins (and Ship of Harkinian / APDOOM) — the game itself
// speaks to the AP server (no emulator, no Lua bridge, no launcher-held ApClient on
// the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the mod source) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned Sonic
// Adventure 2 (Steam appid 213610; the "Battle" content is the Battle DLC the AP
// world targets), and Archipelago is a MOD loaded through the SA2 Mod Loader / SA
// Mod Manager. The honest integration ceiling — like the shipped HK/Subnautica/Raft
// plugins — is "automate what is possible, guide the irreducible parts." For SA2B
// the automatable surface is LARGER than for those three, because the mod reads a
// flat config.ini that this launcher CAN pre-write (verified — see below). The
// verified facts:
//
//   * THE AP WORLD game string is "Sonic Adventure 2 Battle" (verified against
//     worlds/sa2b/__init__.py: class SA2BWorld, `game = "Sonic Adventure 2 Battle"`).
//     GameId here = "sa2b". Note the DISPLAY name carries a colon ("Sonic
//     Adventure 2: Battle") but the AP game STRING does not — ApWorldName must be
//     the exact AP string.
//
//   * THE MOD repo is PoryGone/SA2B_Archipelago (the exact link the official AP
//     "Sonic Adventure 2 Battle" setup guide points its mod-releases page at —
//     verified live 2026-06-14). Latest release verified live is tag v2.4.3, a
//     SINGLE asset "SA2B_Archipelago_v2.4.3.zip" (~12.2 MB). The release-asset
//     filename follows a deterministic pattern (SA2B_Archipelago_v<tag>.zip), so
//     the offline fallback can build a known-good direct URL.
//
//   * THE MOD LOADER is the X-Hax SA Mod Manager (repo X-Hax/SA-Mod-Manager,
//     latest 1.3.6, assets release_x64.zip / release_x86.zip). Its launcher exe is
//     "SAModManager.exe" (older guides/builds name it "SA2ModManager.exe" — this
//     plugin resolves BOTH). CRITICAL HONESTY — SA2 mods ONLY load when the game is
//     started THROUGH the mod manager (its "Save & Play"); launching SA2 straight
//     from Steam does NOT load SA2B_Archipelago (verified across the AP setup guide
//     + the SA Mod Loader install guide). So this plugin's AP launch prefers to
//     start SAModManager.exe (the user clicks Save & Play, which loads the mod), and
//     only falls back to sonic2app.exe / steam://rungameid/213610 for PLAIN, non-AP
//     play.
//
//   * THE RELEASE ZIP layout is a single top-level folder "SA2B_Archipelago/"
//     containing SA2B_Archipelago.dll, config.ini, configschema.xml, mod.ini,
//     CopyAPCppDLL.bat, APCpp.dll, and gd_PC/OBJECT asset folders (verified by
//     inspecting the v2.4.3 zip). Extracting that folder into <SA2>/mods/ makes
//     "mods/SA2B_Archipelago" a valid path — exactly the documented requirement.
//     After unpacking, the setup guide requires running CopyAPCppDLL.bat once (it
//     stages APCpp.dll where the game loads it); this plugin runs it best-effort.
//
//   * CONNECTION is config-driven (the WIN over HK/Subnautica/Raft, which are
//     in-game-only). VERIFIED against the mod source
//     (Archipelago/ArchipelagoManager.cpp): OnInitFunction loads
//     "<modPath>\config.ini" into an IniFile, and at connect time reads
//         [AP] IP         (e.g. archipelago.gg:38281)
//         [AP] PlayerName (the slot name)
//         [AP] Password   (room password, may be empty)
//     and calls Init(IP, PlayerName, Password). The SA Mod Manager "Configure"
//     button edits this same config.ini via configschema.xml. So this plugin DOES
//     pre-write those three keys into mods/SA2B_Archipelago/config.ini before
//     launch (read-modify-write — it preserves the file's other sections, [General]
//     message settings + any [Chao] keys, which the mod also reads via getInt). The
//     plaintext room password is scrubbed from the file when the session ends. If
//     the prefill cannot be applied for any reason, the user can still type the
//     three fields via the mod manager's Configure dialog (the settings panel and
//     note say so).
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam SA2 install via the Windows registry (HKCU SteamPath /
//      HKLM[\WOW6432Node] InstallPath), parsing steamapps\libraryfolders.vdf for
//      every library root and locating steamapps\common\Sonic Adventure 2 via
//      appmanifest_213610.acf. A manual install-dir OVERRIDE (settings folder
//      picker) is also supported and takes precedence; it is validated (must
//      contain sonic2app.exe) and persisted in this plugin's OWN sidecar
//      (Games/ROMs/sa2b/sa2b_launcher.json) — Core/SettingsStore is NOT modified.
//   2. INSTALL/UPDATE (best effort) = download the mod's release zip from the real
//      release and extract it into <SA2>/mods/ (so mods/SA2B_Archipelago is valid),
//      then run CopyAPCppDLL.bat once. Plus clear numbered guided steps + links so
//      the user can install the SA Mod Manager and enable the mod. The mod's own
//      apworld is bundled with Archipelago (core world), so there is no custom
//      apworld for the launcher to fetch.
//   3. LAUNCH = pre-write config.ini [AP] keys, then start SAModManager.exe (so the
//      user does Save & Play and the mod loads). If the mod manager cannot be
//      located, fall back to sonic2app.exe from the detected/override install, then
//      to steam://rungameid/213610 (plain play only — mods will NOT load that way,
//      stated honestly). ConnectsItself = true (the mod owns the slot — the launcher
//      must NOT hold its own ApClient on it). SupportsStandalone = true (plain SA2
//      runs perfectly without AP).
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", HK/Subnautica/Raft-style) ──
//   * "Installed" is judged by the presence of SA2B_Archipelago.dll under a
//     detected/override SA2 install's mods\SA2B_Archipelago folder (case-insensitive,
//     recursive) — NOT by an OUR-OWN version stamp, because the user may unpack the
//     zip by hand or via the mod manager's updater, which this launcher honors. If
//     no SA2 install is detected, the tile reads "not installed".
//   * SA Mod Manager has no reliable registry key we depend on, so the plugin
//     searches common locations for SAModManager.exe / SA2ModManager.exe (and
//     accepts a user override via the settings picker). If it is not found, launch
//     degrades to sonic2app.exe / Steam and the guidance points at the SA Mod
//     Manager download page.
//   * Steam library parsing is defensive: a tolerant VDF scan that pulls quoted
//     "path" values; any failure degrades to "Sonic Adventure 2 not found" rather
//     than throwing.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SonicAdventure2BattlePlugin : IGamePlugin
{
    // ── Constants — the SA2B_Archipelago mod (real repo, verified 2026-06-14) ──
    private const string MOD_OWNER = "PoryGone";
    private const string MOD_REPO  = "SA2B_Archipelago";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";
    private const string ModReleasesPageUrl = $"{ModRepoUrl}/releases";

    // SA Mod Manager (the X-Hax SA2 mod loader) — the RECOMMENDED loader. Mods only
    // load when the game starts through it ("Save & Play").
    private const string ModManagerRepoUrl     = "https://github.com/X-Hax/SA-Mod-Manager";
    private const string ModManagerReleasesUrl = "https://github.com/X-Hax/SA-Mod-Manager/releases";

    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Sonic%20Adventure%202%20Battle/setup_en";
    private const string TrackerUrl = "https://github.com/PoryGone/SA2B_AP_Tracker/releases";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Sonic Adventure 2 appid 213610.
    private const string SteamAppId = "213610";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The standard Steam install sub-folder name for Sonic Adventure 2.
    private const string SteamCommonFolderName = "Sonic Adventure 2";

    /// The SA2 main executable (verified — the mod's mod.ini EXEData targets
    /// sonic2app_data.ini; the Steam exe is sonic2app.exe).
    private const string GameExeName = "sonic2app.exe";

    /// The mod-drop folder name inside the SA2 install (the setup guide uses
    /// lowercase "mods"; we match case-insensitively anyway).
    private const string ModsFolderName = "mods";

    /// The AP mod's own folder name under mods\ and its DLL (verified zip layout).
    private const string ApModFolderName = "SA2B_Archipelago";
    private const string ApModDllName    = "SA2B_Archipelago.dll";

    /// The flat config.ini the mod reads for the AP connection (verified against
    /// ArchipelagoManager.cpp: it loads "<modPath>\config.ini" and reads the
    /// [AP] IP / PlayerName / Password keys at connect time).
    private const string ConfigIniName = "config.ini";

    /// The post-unpack DLL-stage script the setup guide requires running once.
    private const string CopyApCppBat = "CopyAPCppDLL.bat";

    /// SA Mod Manager launcher exe names. The current X-Hax build is
    /// "SAModManager.exe"; older guides/builds named it "SA2ModManager.exe".
    private static readonly string[] ModManagerExeNames =
        { "SAModManager.exe", "SA2ModManager.exe" };

    /// Pinned fallback for the mod when the GitHub API is unreachable. Tag v2.4.3
    /// verified live 2026-06-14; the single asset is "SA2B_Archipelago_v2.4.3.zip".
    /// The asset filename follows a deterministic pattern, so the direct URL is
    /// known-good. The API path is the normal route; this is the offline net.
    private const string FallbackVersion = "2.4.3";
    private const string FallbackZipName = "SA2B_Archipelago_v2.4.3.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "sa2b";
    public string DisplayName => "Sonic Adventure 2: Battle";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/sa2b/__init__.py
    /// (SA2BWorld.game = "Sonic Adventure 2 Battle"). NOTE: no colon — the AP
    /// string differs from the colon-bearing display name.
    public string ApWorldName => "Sonic Adventure 2 Battle";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "sa2b.png");

    public string ThemeAccentColor => "#1438C8";   // Sonic Adventure 2 blue
    public string[] GameBadges     => new[] { "Requires SA2 on Steam" };

    public string Description =>
        "Sonic Adventure 2: Battle, Sonic Team's 2001 classic, played through the " +
        "SA2B_Archipelago mod — an in-game Archipelago client for SA2B. Character " +
        "upgrades, Chao Keys, emblems and more are shuffled into the multiworld, and " +
        "the game connects to the Archipelago server itself (no emulator, no bridge). " +
        "You bring your own copy of Sonic Adventure 2 on Steam, and the Archipelago " +
        "mod is added on top through the SA Mod Manager (the SA2 mod loader). The " +
        "launcher detects your Steam install, unpacks the mod for you, and pre-fills " +
        "your server, slot and password into the mod's config — then you launch " +
        "through the mod manager's Save & Play and create a new save to connect.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the SA2B_Archipelago mod DLL is present in a detected/
    /// override SA2 install's mods\SA2B_Archipelago folder. We do NOT gate on our
    /// own stamp — the user may have unpacked the zip by hand or via the mod
    /// manager, which this launcher honors.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads (the mod zip) and any working files. The
    /// actual mod is extracted INTO the SA2 install's mods folder, not here.
    /// Exposed as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "SonicAdventure2Battle");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Doom/Jak/HK/
    /// Subnautica/Raft). Per the brief, lives under Games/ROMs/sa2b/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "sa2b_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The SA2B_Archipelago mod reports checks/items/goal to the AP server itself —
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
            // if present; otherwise report "installed" when the mod DLL exists.
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
        // 0. We need an SA2 install to unpack the mod into. Prefer an explicit
        //    override; else auto-detect the Steam install.
        progress.Report((2, "Locating your Sonic Adventure 2 installation..."));
        string? saDir = ResolveSa2Dir();
        if (saDir == null)
            throw new InvalidOperationException(
                "Could not find a Sonic Adventure 2 installation. Open this game's " +
                "Settings and pick your Sonic Adventure 2 folder (the one containing " +
                "sonic2app.exe), or install Sonic Adventure 2 via Steam first. The " +
                "Archipelago mod is added on top of your own copy of the game.");

        string modsDir  = FindModsFolder(saDir);
        string apModDir = Path.Combine(modsDir, ApModFolderName);

        // 1. Resolve the latest mod release (pinned fallback when offline).
        progress.Report((6, "Checking the latest SA2B Archipelago mod release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the SA2B Archipelago mod download on GitHub. Check " +
                "your internet connection, or download the mod zip manually from " +
                ModReleasesPageUrl + " and unpack it into your Sonic Adventure 2 " +
                "\"mods\" folder so that mods\\SA2B_Archipelago is a valid path. See " +
                "Settings for the guided steps. The mod repo is " + ModRepoUrl + ".");

        // 2. Download + extract the mod zip INTO <SA2>/mods/. The zip's single
        //    top-level folder is "SA2B_Archipelago", so extracting it into mods\
        //    yields mods\SA2B_Archipelago (the documented, correct layout).
        await DownloadAndExtractModAsync(zipUrl, version, modsDir, progress, ct);

        // 3. Run CopyAPCppDLL.bat once (the setup guide requires it — it stages
        //    APCpp.dll where the game loads it). Best effort; never blocks install.
        progress.Report((93, "Staging the Archipelago client DLL (CopyAPCppDLL.bat)..."));
        TryRunCopyApCppBat(apModDir);

        // 4. Stamp the version next to our sidecar so the tile can show it. (This
        //    is informational only — IsInstalled is judged by the DLL's presence.)
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Installed the SA2B Archipelago mod {version} into your Sonic Adventure 2 " +
            "\"mods\" folder. To play: install the SA Mod Manager if you have not " +
            "(see Settings → Links), make sure SA2B_Archipelago is enabled in it, then " +
            "press Play here (it opens the SA Mod Manager so you can Save & Play). The " +
            "launcher pre-fills your server, slot and password into the mod's config; " +
            "create a NEW save in game to connect."));
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
        // VERIFIED prefill: the mod reads [AP] IP / PlayerName / Password from
        // mods\SA2B_Archipelago\config.ini at connect time. Pre-write those three
        // keys (read-modify-write — preserve every other section/key). Best effort:
        // if it fails, the player can still type them via the mod manager's
        // Configure dialog. Never blocks the launch.
        try { WriteApConnectionConfig(session); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected. Launch through the SA Mod Manager
        // so the mod loads (the user clicks Save & Play); fall back to the game exe
        // / Steam for plain play.
        StartSa2(preferModManager: true);
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Sonic Adventure 2 runs perfectly well.
    public bool SupportsStandalone => true;

    /// The SA2B_Archipelago mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // Plain play: still prefer the mod manager (the user may simply not create
        // an Archipelago save), but do NOT pre-write any AP connection.
        StartSa2(preferModManager: true);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started SA2 / the mod manager from here. Kill what we
        // launched.
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // The plaintext room password was written into config.ini for the session —
        // blank it now so it does not outlive the session on disk. Best effort.
        ScrubApConnectionPassword();
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The SA2B_Archipelago mod receives items from the AP server directly; there
        // is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid SA2 folder contains
    /// sonic2app.exe. Return null when acceptable, else a short human-readable
    /// reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Sonic Adventure 2 install folder.";

        if (LooksLikeSa2Dir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeSa2Dir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Sonic Adventure 2 installation. Pick the " +
               "folder that contains sonic2app.exe. For Steam this is usually " +
               @"...\steamapps\common\Sonic Adventure 2.";
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
            Text = "Sonic Adventure 2 is your own game on Steam with the SA2B_Archipelago " +
                   "mod added on top. The mod loads through the SA Mod Manager (the SA2 mod " +
                   "loader) — the launcher detects your Steam install and unpacks the mod " +
                   "for you, but you install the SA Mod Manager yourself (link below) and " +
                   "make sure the mod is enabled in it. Mods only load when you start the " +
                   "game THROUGH the mod manager (Save & Play), not straight from Steam. " +
                   "The launcher pre-fills your server, slot and password into the mod's " +
                   "config.ini for you. These external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "SONIC ADVENTURE 2 INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? saDir       = ResolveSa2Dir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = saDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + saDir
                : "Detected Steam install: " + saDir)
            : "Sonic Adventure 2 not detected. Pick your install folder below, or " +
              "install Sonic Adventure 2 via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = saDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // mod status line
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                    ? "SA2B_Archipelago mod found: " + modDll
                    : "SA2B_Archipelago not found in mods\\SA2B_Archipelago yet (use Install " +
                      "on the Play tab, or unpack the mod zip into your \"mods\" folder).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? saDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Sonic Adventure 2 install folder (the one containing " +
                          "sonic2app.exe). Detected from Steam automatically; set it here " +
                          "to override a non-standard Steam library.",
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
                Title            = "Select your Sonic Adventure 2 install folder (contains sonic2app.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? saDir ?? "")
                                   ? (overrideDir ?? saDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Sonic Adventure 2 folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeSa2Dir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeSa2Dir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 213610). Use this " +
                   "picker for a non-standard Steam library.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: SA Mod Manager ───────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "SA MOD MANAGER (the SA2 mod loader)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });

        string? mgrPath     = ResolveModManager();
        string? mgrOverride = LoadModManagerOverride();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = mgrPath != null
                    ? (mgrOverride != null
                        ? "Using your selected SA Mod Manager: " + mgrPath
                        : "SA Mod Manager found: " + mgrPath)
                    : "SA Mod Manager (SAModManager.exe) not found. Install it from its " +
                      "releases page (link below), then optionally point the launcher at " +
                      "it here.",
            FontSize = 11, Foreground = mgrPath != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        var mgrRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var mgrBox = new System.Windows.Controls.TextBox
        {
            Text = mgrOverride ?? mgrPath ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "SAModManager.exe — the SA Mod Manager. Launching the game through " +
                          "it (Save & Play) is what makes the SA2B_Archipelago mod load. Set " +
                          "it here if the launcher cannot find it automatically.",
        };
        var mgrBtn = new System.Windows.Controls.Button
        {
            Content = "Select manager...", Width = 130, Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        mgrBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select the SA Mod Manager (SAModManager.exe)",
                Filter = "SA Mod Manager (SAModManager.exe;SA2ModManager.exe)|SAModManager.exe;SA2ModManager.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*",
                InitialDirectory = Directory.Exists(Path.GetDirectoryName(mgrOverride ?? mgrPath ?? "") ?? "")
                                   ? Path.GetDirectoryName(mgrOverride ?? mgrPath!)!
                                   : (saDir ?? AppContext.BaseDirectory),
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FileName;
                if (!File.Exists(picked))
                {
                    System.Windows.MessageBox.Show(
                        "That file does not exist. Pick SAModManager.exe.",
                        "Not found", System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                SaveModManagerOverride(picked);
                mgrBox.Text = picked;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(mgrBtn, System.Windows.Controls.Dock.Right);
        mgrRow.Children.Add(mgrBtn);
        mgrRow.Children.Add(mgrBox);
        panel.Children.Add(mgrRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Important: launch through the SA Mod Manager (this launcher opens it for " +
                   "you when it is found — click Save & Play there). Launching straight from " +
                   "Steam will NOT load SA2B_Archipelago.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (this session) ────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (pre-filled into config.ini; editable via Configure)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "When you press Play, the launcher writes your Server IP (host:port, e.g. " +
                   "archipelago.gg:38281), PlayerName (your slot name) and Password into the " +
                   "mod's config.ini ([AP] section). You can also edit these in the SA Mod " +
                   "Manager: select SA2B_Archipelago, click Configure, and fill the AP " +
                   "Settings fields. In game, create a NEW save to connect — a \"Connected to " +
                   "Archipelago\" message appears on success.",
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
            "1. Own Sonic Adventure 2 on Steam, and launch it once normally (without mods) " +
                "so it finishes first-run setup. Use \"Select folder...\" above if it was not " +
                "auto-detected.",
            "2. Install the SA Mod Manager from its releases page (link below) — pick the " +
                "x64 build on a 64-bit PC. Run it once so it sets up the \"mods\" folder.",
            "3. Install the mod: use Install on the Play tab (it downloads the mod zip, " +
                "unpacks it into mods\\SA2B_Archipelago, and runs CopyAPCppDLL.bat for you). " +
                "Or unpack the zip into your \"mods\" folder by hand and run CopyAPCppDLL.bat.",
            "4. In the SA Mod Manager, make sure SA2B_Archipelago is listed and ENABLED " +
                "(tick it). Optional: install the SA2B AP Tracker (link below) for an " +
                "external item/location tracker.",
            "5. Press Play here (it opens the SA Mod Manager). The launcher has already " +
                "written your server / slot / password into the mod's config — or click " +
                "Configure in the manager to review them. Then click Save, then Save & Play.",
            "6. In game, create a NEW save to connect to the multiworld. A \"Connected to " +
                "Archipelago\" message appears on success.",
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
            ("SA Mod Manager (download) ↗",            ModManagerReleasesUrl),
            ("SA2B Archipelago mod (releases) ↗",      ModReleasesPageUrl),
            ("SA2B Archipelago Tracker (releases) ↗",  TrackerUrl),
            ("SA2B Setup Guide ↗",                     SetupGuideUrl),
            ("Archipelago Official ↗",                 ArchipelagoSite),
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

    // ── Private helpers — small utilities ─────────────────────────────────────

    /// "v2.4.3" → "2.4.3" when a leading 'v' decorates a digit; else trimmed.
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

    // ── Private helpers — release resolution ──────────────────────────────────

    /// Resolve the latest mod release: version + the mod zip download URL. Prefers
    /// an asset matching "SA2B_Archipelago" and ".zip"; falls back to the first .zip
    /// asset; falls back to the pinned v2.4.3 direct URL when the API is unreachable.
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
                string? preferred = null;   // the .zip named like SA2B_Archipelago*.zip
                string? anyZip    = null;   // any .zip
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null && lower.Contains("sa2b") && lower.Contains("archipelago"))
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

    // ── Private helpers — Steam / SA2 detection ───────────────────────────────

    /// The SA2 install dir to use: the override (if set and valid) wins, else the
    /// Steam-detected install. Null when nothing is found.
    private string? ResolveSa2Dir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeSa2Dir(ov))
            return ov;

        try { return DetectSteamSa2Dir(); }
        catch { return null; }
    }

    /// A folder "looks like" SA2 if it has sonic2app.exe.
    private static bool LooksLikeSa2Dir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

    /// Detect the Steam SA2 install: read the Steam root from the registry, gather
    /// all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_213610.acf exists → steamapps\common\Sonic Adventure 2.
    private static string? DetectSteamSa2Dir()
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
                    // conventional "Sonic Adventure 2" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeSa2Dir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeSa2Dir(conventional)) return conventional;
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

    // ── Private helpers — SA Mod Manager detection ────────────────────────────

    /// Locate the SA Mod Manager launcher exe: the user override (if set + exists)
    /// wins; else search common install locations + the SA2 folder. Null if not
    /// found.
    private string? ResolveModManager()
    {
        string? ov = LoadModManagerOverride();
        if (ov != null && File.Exists(ov)) return ov;

        foreach (string candidate in ModManagerCandidatePaths())
        {
            try { if (File.Exists(candidate)) return candidate; }
            catch { /* skip unreadable candidate */ }
        }
        return null;
    }

    /// Common locations SAModManager.exe (or legacy SA2ModManager.exe) is installed
    /// to. The SA Mod Manager has no reliable registry key, so we probe the SA2
    /// install folder itself (a common spot — the manager is often unpacked next to
    /// the game), %LOCALAPPDATA%/%APPDATA%/%PROGRAMFILES%\SAManager, and a sibling
    /// "SAManager" folder. Each is yielded as a full path to one of the manager
    /// exe names.
    private IEnumerable<string> ModManagerCandidatePaths()
    {
        // The SA2 install folder + a "SAManager" subfolder of it.
        string? sa = ResolveSa2Dir();
        if (sa != null)
        {
            foreach (string exe in ModManagerExeNames)
            {
                yield return Path.Combine(sa, exe);
                yield return Path.Combine(sa, "SAManager", exe);
            }
            string? parent = Path.GetDirectoryName(sa);
            if (!string.IsNullOrWhiteSpace(parent))
                foreach (string exe in ModManagerExeNames)
                    yield return Path.Combine(parent!, "SAManager", exe);
        }

        string?[] bases =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SAManager"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),      "SAManager"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),         "SAManager"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),      "SAManager"),
        };
        foreach (string? b in bases)
            if (!string.IsNullOrWhiteSpace(b))
                foreach (string exe in ModManagerExeNames)
                    yield return Path.Combine(b!, exe);
    }

    // ── Private helpers — mods folder + installed-mod detection ───────────────

    /// Resolve the "mods" folder inside an SA2 install, matching the folder name
    /// case-insensitively (the guide uses lowercase "mods", but be tolerant).
    /// Returns the conventional lowercase path if no folder exists yet.
    private static string FindModsFolder(string sa2Dir)
    {
        try
        {
            foreach (string sub in Directory.EnumerateDirectories(sa2Dir))
            {
                if (string.Equals(Path.GetFileName(sub), ModsFolderName, StringComparison.OrdinalIgnoreCase))
                    return sub;
            }
        }
        catch { /* fall through to the conventional path */ }
        return Path.Combine(sa2Dir, ModsFolderName);
    }

    /// Find SA2B_Archipelago.dll under the detected/override SA2 install's
    /// mods\SA2B_Archipelago folder (recursive, case-insensitive). Returns the dll
    /// path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? sa = ResolveSa2Dir();
            if (sa == null) return null;

            string modsDir = FindModsFolder(sa);
            if (!Directory.Exists(modsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(modsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(dll);
                if (name.Equals(ApModDllName, StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    /// The mod's own folder under mods\ (whether or not it exists yet). Used for
    /// the config.ini prefill + CopyAPCppDLL.bat.
    private string? ResolveApModDir()
    {
        string? sa = ResolveSa2Dir();
        if (sa == null) return null;
        return Path.Combine(FindModsFolder(sa), ApModFolderName);
    }

    // ── Private helpers — config.ini prefill (verified — see header) ──────────

    /// Pre-write the AP connection into mods\SA2B_Archipelago\config.ini. VERIFIED:
    /// the mod reads [AP] IP / PlayerName / Password from this file at connect time
    /// (ArchipelagoManager.cpp). This is a SECTION-AWARE read-modify-write that
    /// preserves every other section/key (the [General] message settings and any
    /// [Chao] keys the mod also reads), so we never break the rest of the config.
    /// BOM-less, CRLF — a plain Windows INI the mod's IniFile parser + the SA Mod
    /// Manager Configure dialog both accept. Best effort.
    private void WriteApConnectionConfig(ApSession session)
    {
        string? modDir = ResolveApModDir();
        if (modDir == null) return;                 // no SA2 install → nothing to do
        if (!Directory.Exists(modDir)) return;      // mod not unpacked yet → nothing to do

        var (host, port) = ParseServerHostPort(session.ServerUri);
        string ip = $"{host}:{port}";

        string path = Path.Combine(modDir, ConfigIniName);
        var ini = IniDocument.Load(path);           // empty doc if file absent
        ini.Set("AP", "IP", ip);
        ini.Set("AP", "PlayerName", session.SlotName);
        ini.Set("AP", "Password", session.Password ?? "");
        ini.Save(path);
    }

    /// Blank the [AP] Password in config.ini once the session ends — the plaintext
    /// room password should not outlive the session on disk. Best effort; the next
    /// AP launch rewrites it anyway.
    private void ScrubApConnectionPassword()
    {
        try
        {
            string? modDir = ResolveApModDir();
            if (modDir == null) return;
            string path = Path.Combine(modDir, ConfigIniName);
            if (!File.Exists(path)) return;

            var ini = IniDocument.Load(path);
            if (string.IsNullOrEmpty(ini.Get("AP", "Password"))) return; // nothing to scrub
            ini.Set("AP", "Password", "");
            ini.Save(path);
        }
        catch { /* best effort — the next AP launch overwrites the file anyway */ }
    }

    /// Accepts "archipelago.gg:38281", "ws://host:port", "wss://host:port", a bare
    /// hostname, and IPv6 literals (bracketed "[::1]:38281" or bare "::1"). Default
    /// AP port is 38281.
    private static (string Host, int Port) ParseServerHostPort(string serverUri)
    {
        string s = serverUri.Trim();
        foreach (string prefix in new[] { "wss://", "ws://", "archipelago://" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..];
                break;
            }
        }

        int slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];

        string host = s;
        int    port = 38281;

        int colonCount = 0;
        foreach (char c in s) if (c == ':') colonCount++;

        if (s.StartsWith('['))
        {
            int close = s.IndexOf(']');
            if (close > 0)
            {
                host = s[1..close];
                string rest = s[(close + 1)..];
                if (rest.StartsWith(':') &&
                    int.TryParse(rest[1..], out int p6) && p6 > 0 && p6 <= 65535)
                    port = p6;
            }
        }
        else if (colonCount > 1)
        {
            host = s; // bare IPv6 literal — no port can be carried this way
        }
        else
        {
            int colon = s.LastIndexOf(':');
            if (colon > 0 && int.TryParse(s[(colon + 1)..], out int p) && p > 0 && p <= 65535)
            {
                host = s[..colon];
                port = p;
            }
        }

        if (host.Length == 0) host = "archipelago.gg";
        return (host, port);
    }

    // ── Private helpers — CopyAPCppDLL.bat ────────────────────────────────────

    /// Run the mod's CopyAPCppDLL.bat once after unpacking (the setup guide requires
    /// it — it stages APCpp.dll where the game loads it). Best effort: never throws,
    /// and uses a short timeout so a misbehaving script cannot hang an install.
    private static void TryRunCopyApCppBat(string apModDir)
    {
        try
        {
            string bat = Path.Combine(apModDir, CopyApCppBat);
            if (!File.Exists(bat)) return;

            var psi = new ProcessStartInfo
            {
                FileName         = "cmd.exe",
                Arguments        = $"/c \"\"{bat}\"\"",
                WorkingDirectory = apModDir,
                UseShellExecute  = false,
                CreateNoWindow   = true,
            };
            using var proc = Process.Start(psi);
            // The script is a quick copy; wait briefly so APCpp.dll is in place
            // before we report success, but never block install for long.
            proc?.WaitForExit(15000);
        }
        catch { /* convenience — the user can re-run the .bat by hand if needed */ }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start SA2 so the mod can load: when preferModManager, start the SA Mod
    /// Manager (the user clicks Save & Play, which loads SA2B_Archipelago); else / if
    /// the manager is missing, start sonic2app.exe from the detected/override install
    /// (plain play — mods will NOT load this way); else fall back to the steam:// URL
    /// (plain play). Surfaces a clear message rather than failing opaquely.
    private void StartSa2(bool preferModManager)
    {
        // 1. Preferred: the SA Mod Manager so SA2B_Archipelago loads on Save & Play.
        if (preferModManager)
        {
            string? mgr = ResolveModManager();
            if (mgr != null && File.Exists(mgr))
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName         = mgr,
                    WorkingDirectory = Path.GetDirectoryName(mgr)!,
                    UseShellExecute  = true,
                });
                if (proc != null)
                {
                    TrackProcess(proc);
                    return;
                }
                // If the manager failed to start, fall through to the game / Steam.
            }
        }

        // 2. Fall back to sonic2app.exe directly. NOTE: this does NOT load mods — it
        //    is a last resort so the tile still launches the game. (Plain play.)
        string? sa  = ResolveSa2Dir();
        string? exe = sa != null ? Path.Combine(sa, GameExeName) : null;
        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = sa!,
                UseShellExecute  = true,
            });
            if (proc != null)
            {
                TrackProcess(proc);
                return;
            }
        }

        // 3. Last resort: Steam URL (plain play; mods will not load this way).
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
            "Could not start Sonic Adventure 2. Install the SA Mod Manager (so mods " +
            "load) and/or open this game's Settings to pick your Sonic Adventure 2 " +
            "folder, or install Sonic Adventure 2 via Steam. See Settings for the " +
            "guided steps.",
            GameExeName);
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
                ScrubApConnectionPassword();   // session over — blank the password
                GameExited?.Invoke(proc.ExitCode);
            };
        }
        catch { /* some processes don't expose Exited — non-fatal */ }
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod's release zip and extract it INTO the SA2 "mods" folder. The
    /// zip's single top-level folder is "SA2B_Archipelago", so a straight extract
    /// into mods\ yields mods\SA2B_Archipelago (the documented layout). We extract to
    /// a temp staging folder first, then merge (overwriting files) so an update lands
    /// cleanly without deleting unrelated files; if the zip is somehow not wrapped in
    /// the SA2B_Archipelago folder, we still place its contents under
    /// mods\SA2B_Archipelago.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string modsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"sa2b-archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"sa2b-archipelago-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading SA2B Archipelago mod {version}..."));
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
                        progress.Report((pct, $"Downloading SA2B Archipelago mod... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Unpacking the mod..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // The documented layout: the zip wraps everything in one "SA2B_Archipelago"
            // folder. Merge so the result is mods\SA2B_Archipelago. If the zip's top
            // folder is that wrapper, merge the wrapper INTO mods\SA2B_Archipelago;
            // otherwise place the loose contents there directly.
            Directory.CreateDirectory(modsDir);
            string apModDir = Path.Combine(modsDir, ApModFolderName);

            string? wrapper = FindSingleNamedWrapper(tempExtract, ApModFolderName);
            progress.Report((82, "Installing the mod into your \"mods\" folder..."));
            if (wrapper != null)
                MergeDirectory(wrapper, apModDir);
            else
                MergeDirectory(tempExtract, apModDir);

            progress.Report((90, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// If <root> contains exactly one entry and it is a sub-folder named <name>
    /// (case-insensitive), return that sub-folder; else null. Used to detect the
    /// zip's single "SA2B_Archipelago" wrapper folder.
    private static string? FindSingleNamedWrapper(string root, string name)
    {
        try
        {
            string[] dirs  = Directory.GetDirectories(root);
            string[] files = Directory.GetFiles(root);
            if (files.Length == 0 && dirs.Length == 1 &&
                string.Equals(Path.GetFileName(dirs[0]), name, StringComparison.OrdinalIgnoreCase))
                return dirs[0];
        }
        catch { /* ignore */ }
        return null;
    }

    /// Recursively copy everything under <src> into <dst>, overwriting files and
    /// creating directories as needed. Never deletes anything in <dst> that is not
    /// being overwritten — so an existing config.ini the user customised via the SA
    /// Mod Manager would be overwritten by the zip's default (acceptable on
    /// install/update; the launch-time prefill re-applies the [AP] keys anyway).
    private static void MergeDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, dir);
            Directory.CreateDirectory(Path.Combine(dst, rel));
        }
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel    = Path.GetRelativePath(src, file);
            string target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the SA2 install-dir override +
    // the SA Mod Manager exe override + an informational version stamp) in its OWN
    // JSON file so it stays a single self-contained source file and does not modify
    // Core/SettingsStore. BOM-less UTF-8, read-modify-write (same approach as
    // Doom/Jak/HK/Subnautica/Raft).

    private sealed class Sa2bSettings
    {
        public string? InstallOverride    { get; set; }
        public string? ModManagerOverride { get; set; }
        public string? ModVersion         { get; set; }
    }

    private Sa2bSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Sa2bSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(Sa2bSettings s)
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

    private string? LoadModManagerOverride()
    {
        string? p = LoadSettings().ModManagerOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }
    private void SaveModManagerOverride(string p) { var s = LoadSettings(); s.ModManagerOverride = p; SaveSettings(s); }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    private void WriteStampedVersion(string v) { var s = LoadSettings(); s.ModVersion = v; SaveSettings(s); }

    // ── Private helpers — tiny section-aware INI read-modify-write ────────────
    // The mod's config.ini is a flat Windows INI ([Section] then key=value). We
    // need to Set [AP] keys WITHOUT disturbing the file's other sections/keys (the
    // mod also reads [General] and [Chao]). This minimal in-memory model preserves
    // section order, key order, and unknown/blank lines as faithfully as is needed,
    // and appends a missing section/key at the right place.

    private sealed class IniDocument
    {
        // Ordered list of sections; each section keeps its ordered key list.
        private readonly List<Section> _sections = new();

        private sealed class Section
        {
            public string Name = "";                 // "" = the implicit pre-header section
            public readonly List<KeyValuePair<string, string>> Pairs = new();
        }

        public static IniDocument Load(string path)
        {
            var doc = new IniDocument();
            Section current = new();                  // implicit top section
            doc._sections.Add(current);
            try
            {
                if (File.Exists(path))
                {
                    foreach (string raw in File.ReadAllLines(path))
                    {
                        string line = raw.Trim();
                        if (line.Length == 0) continue;
                        if (line.StartsWith(';') || line.StartsWith('#')) continue; // comment
                        if (line.StartsWith('[') && line.EndsWith(']'))
                        {
                            string name = line[1..^1].Trim();
                            current = new Section { Name = name };
                            doc._sections.Add(current);
                            continue;
                        }
                        int eq = line.IndexOf('=');
                        if (eq < 0) continue;          // skip malformed line
                        string key = line[..eq].Trim();
                        string val = line[(eq + 1)..].Trim();
                        if (key.Length == 0) continue;
                        current.Pairs.Add(new KeyValuePair<string, string>(key, val));
                    }
                }
            }
            catch { /* unreadable → behave like an empty doc */ }
            return doc;
        }

        public string? Get(string section, string key)
        {
            var sec = _sections.Find(s =>
                string.Equals(s.Name, section, StringComparison.OrdinalIgnoreCase));
            if (sec == null) return null;
            foreach (var kv in sec.Pairs)
                if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            return null;
        }

        public void Set(string section, string key, string value)
        {
            var sec = _sections.Find(s =>
                string.Equals(s.Name, section, StringComparison.OrdinalIgnoreCase));
            if (sec == null)
            {
                sec = new Section { Name = section };
                _sections.Add(sec);
            }
            for (int i = 0; i < sec.Pairs.Count; i++)
            {
                if (string.Equals(sec.Pairs[i].Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    sec.Pairs[i] = new KeyValuePair<string, string>(sec.Pairs[i].Key, value);
                    return;
                }
            }
            sec.Pairs.Add(new KeyValuePair<string, string>(key, value));
        }

        public void Save(string path)
        {
            var sb = new StringBuilder();
            bool first = true;
            foreach (var sec in _sections)
            {
                // Skip a totally empty implicit top section.
                if (sec.Name.Length == 0 && sec.Pairs.Count == 0) continue;

                if (sec.Name.Length > 0)
                {
                    if (!first) sb.Append("\r\n");
                    sb.Append('[').Append(sec.Name).Append(']').Append("\r\n");
                }
                foreach (var kv in sec.Pairs)
                    sb.Append(kv.Key).Append('=').Append(kv.Value).Append("\r\n");
                first = false;
            }

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
