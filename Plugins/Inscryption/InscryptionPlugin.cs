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
// FontWeights / Orientation / HorizontalAlignment collide between
// System.Windows.Forms and System.Windows[.Controls/.Media] (CS0104). Qualifying
// every UI type avoids that.

namespace LauncherV2.Plugins.Inscryption;

// ═══════════════════════════════════════════════════════════════════════════════
// InscryptionPlugin — install / launch for "Inscryption" (Daniel Mullins Games,
// 2021) played through ArchipelagoMod, a BepInEx plugin that is the in-game
// Archipelago client for Inscryption. This is a NATIVE "ConnectsItself"
// integration in the same family as the shipped Hollow Knight / TUNIC / Subnautica
// / Stardew Valley plugins (and Ship of Harkinian / Jak) — the game itself speaks
// to the AP server (no emulator, no Lua bridge, no launcher-held ApClient on the
// slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Inscryption (Steam appid 1092790), and Archipelago is a MOD (a BepInEx
// plugin) added on top. The honest integration ceiling — exactly like the shipped
// Hollow Knight / TUNIC plugins — is "automate what is possible, guide the
// irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "Inscryption" (verified against
//     worlds/inscryption/__init__.py: `class InscryptionWorld(World): ...
//     game = "Inscryption"`). GameId here = "inscryption". Inscryption is a CORE
//     Archipelago world (it ships inside Archipelago itself — no custom_worlds drop
//     needed to generate).
//
//   * THE MOD is "ArchipelagoMod" by DrBibop. Two distribution channels exist and
//     BOTH were checked live 2026-06-14:
//       - GitHub SOURCE: DrBibop/Archipelago_Inscryption (the apworld's
//         bug_report_page points here). CRITICAL: its GitHub *releases* ship ONLY
//         "inscryption.apworld" + "PlayerSettingsTemplate.yaml" — they do NOT carry
//         the compiled BepInEx mod zip. (Every release beta..beta8 verified: same
//         two assets, no DLL/zip.) So a GitHub-direct mod download is NOT possible
//         (unlike Subnautica, whose GitHub release carries the whole mod).
//       - THUNDERSTORE (the OFFICIAL / RECOMMENDED route per the AP setup guide):
//         package Ballin_Inc/ArchipelagoMod, latest version 1.0.3 (2025-07-13). The
//         package zip contains a "plugins" folder whose contents merge into
//         <Inscryption>/BepInEx/plugins/. This is the channel r2modman /
//         Thunderstore Mod Manager install from, and the ONLY channel that carries
//         the actual mod binary — so this plugin best-effort stages the mod from
//         the Thunderstore CDN direct-download URL.
//
//   * CRITICAL HONESTY — BepInEx IS A SEPARATE PREREQUISITE, NOT BUNDLED IN THE MOD.
//     Inscryption is a Unity Mono game; the mod depends on the BepInExPack for
//     Inscryption (Thunderstore: BepInEx/BepInExPack_Inscryption, version 5.4.1902,
//     the exact dependency string the mod declares: "BepInEx-BepInExPack_Inscryption
//     -5.4.1902"). The mod zip does NOT contain BepInEx. The BepInExPack is itself a
//     PORTABLE Thunderstore zip (no wizard) — it nests a "BepInExPack_Inscryption"
//     folder whose CONTENTS drop into the Inscryption root — so this plugin CAN
//     best-effort stage it AND the mod for you. But it then leaves the
//     run-once-to-generate-config and the in-game connection to you, and says so.
//     Faking a fully-automated "ready to play" would be dishonest theatre. The
//     OFFICIAL and RECOMMENDED route remains r2modman / Thunderstore Mod Manager
//     (one click installs ArchipelagoMod + the BepInExPack dependency), and this
//     plugin leads the user there first.
//
//   * CONNECTION is made IN-GAME (verified against the official AP setup guide and
//     the mod README): after the mod is installed and the game launched, a new
//     save-file menu appears; click "New Game", name the save, and on the next
//     screen "enter the information needed to connect to the MultiWorld server,
//     then press the Connect button" — look for "Connected". The exact in-game
//     field names are NOT officially documented, and there is NO command-line arg
//     and NO config file this launcher can pre-write (verified). Per an honest
//     "don't invent an undocumented prefill" stance (same as Hollow Knight / TUNIC),
//     this plugin does NOT write any file or fake a connection prefill — the
//     settings panel + post-install note surface the session's host/slot for the
//     user to type into the in-game fields.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Inscryption install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Inscryption via appmanifest_1092790.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and takes
//      precedence; it is validated (must contain Inscryption.exe) and persisted in
//      this plugin's OWN sidecar (Games/ROMs/inscryption/inscryption_launcher.json)
//      — Core/SettingsStore is NOT modified.
//   2. INSTALL/UPDATE (best effort) = (a) if BepInEx is not present in the detected
//      install, download the pinned BepInExPack_Inscryption Thunderstore zip and
//      extract its nested "BepInExPack_Inscryption" contents into the Inscryption
//      root; (b) download the ArchipelagoMod Thunderstore zip and merge its
//      "plugins" folder into <Inscryption>/BepInEx/plugins/. Both are portable zips.
//      The plugin then presents clear, numbered, Hollow-Knight-style guided steps +
//      links (r2modman, the mod's Thunderstore page, the official AP setup guide,
//      the mod GitHub, archipelago.gg) so the user can run the game once and connect
//      in-game. Never a fake one-click.
//   3. LAUNCH = run Inscryption.exe from the detected/override install; if the exe
//      cannot be found but Steam is present, fall back to steam://rungameid/1092790.
//      ConnectsItself = true (the mod owns the slot — the launcher must NOT hold its
//      own ApClient on it). SupportsStandalone = true (plain Inscryption runs fine
//      without AP). No connection prefill (entered in-game), stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", HK/TUNIC/Subnautica-style) ──
//   * "Installed" is judged by the presence of an Archipelago-named mod DLL under a
//     detected/override Inscryption install's BepInEx\plugins tree (case-insensitive,
//     recursive) — NOT by an OUR-OWN version stamp, because the user may instead
//     install the mod via r2modman (the recommended route) or by hand, which this
//     launcher should honor. The exact mod DLL filename is not officially documented;
//     by Thunderstore convention it is "ArchipelagoMod.dll", but to be robust this
//     plugin matches ANY *.dll under BepInEx\plugins whose name contains
//     "archipelago" (so a renamed/repackaged build still registers as installed). If
//     no Inscryption install is detected, the tile simply reads "not installed".
//   * Steam library parsing is defensive: a hand-written tolerant VDF scan that
//     pulls quoted "path" values; any failure degrades to "Inscryption not found"
//     rather than throwing.
//   * No plaintext AP password is ever written by this plugin (the connection is
//     entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class InscryptionPlugin : IGamePlugin
{
    // ── Constants — the ArchipelagoMod sources (real, verified 2026-06-14) ─────
    // GitHub source repo (the apworld's bug_report_page). Its releases carry only
    // the apworld + a yaml template — NOT the compiled mod — so we use it for links
    // and news, NOT for the mod download.
    private const string MOD_OWNER = "DrBibop";
    private const string MOD_REPO  = "Archipelago_Inscryption";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // Thunderstore — the channel that actually carries the compiled mod, and the
    // route r2modman / Thunderstore Mod Manager install from. Namespace Ballin_Inc,
    // package ArchipelagoMod. The experimental API gives latest version + the direct
    // CDN download URL + the BepInExPack dependency.
    private const string TS_NS   = "Ballin_Inc";
    private const string TS_PKG  = "ArchipelagoMod";
    private const string ModThunderstorePageUrl =
        $"https://thunderstore.io/c/inscryption/p/{TS_NS}/{TS_PKG}/";
    private const string TS_MOD_API_URL =
        $"https://thunderstore.io/api/experimental/package/{TS_NS}/{TS_PKG}/";

    // BepInExPack for Inscryption — the SEPARATE mod-loader prerequisite the mod
    // depends on (not bundled). Portable Thunderstore zip. Pinned to the build the
    // mod declares (5.4.1902).
    private const string BEPINEX_NS  = "BepInEx";
    private const string BEPINEX_PKG = "BepInExPack_Inscryption";
    private const string BepInExVersion = "5.4.1902";
    private const string BepInExThunderstorePageUrl =
        $"https://thunderstore.io/c/inscryption/p/{BEPINEX_NS}/{BEPINEX_PKG}/";
    private const string TS_BEPINEX_API_URL =
        $"https://thunderstore.io/api/experimental/package/{BEPINEX_NS}/{BEPINEX_PKG}/";
    // Direct CDN download for the pinned BepInExPack (used when the API is
    // unreachable). Thunderstore serves the package zip at this stable path.
    private static readonly string BepInExZipUrl =
        $"https://thunderstore.io/package/download/{BEPINEX_NS}/{BEPINEX_PKG}/{BepInExVersion}/";

    // r2modman / Thunderstore Mod Manager — the OFFICIAL recommended installer
    // (resolves the BepInExPack dependency automatically).
    private const string R2ModManUrl = "https://thunderstore.io/c/inscryption/p/ebkr/r2modman/";

    private const string SetupGuideUrl  = "https://archipelago.gg/tutorial/Inscryption/setup/en";
    private const string GameInfoUrl    = "https://archipelago.gg/games/Inscryption/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Inscryption appid 1092790.
    private const string InscryptionSteamAppId = "1092790";
    private static readonly string SteamRunUrl = $"steam://rungameid/{InscryptionSteamAppId}";

    /// The standard Steam install sub-folder name for Inscryption.
    private const string SteamCommonFolderName = "Inscryption";

    /// The base-game executable name.
    private const string InscryptionExeName = "Inscryption.exe";

    /// Pinned fallback for the mod when the Thunderstore API is unreachable. Version
    /// 1.0.3 verified live 2026-06-14. Thunderstore serves the package zip at this
    /// stable direct path.
    private const string FallbackModVersion = "1.0.3";
    private static readonly string FallbackModZipUrl =
        $"https://thunderstore.io/package/download/{TS_NS}/{TS_PKG}/{FallbackModVersion}/";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "inscryption";
    public string DisplayName => "Inscryption";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/inscryption/__init__.py
    /// (`class InscryptionWorld(World): ... game = "Inscryption"`).
    public string ApWorldName => "Inscryption";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "inscryption.png");

    public string ThemeAccentColor => "#6B3A2A";   // inky cabin firelight / dirt
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Inscryption, Daniel Mullins Games' 2021 deckbuilding roguelike wrapped in " +
        "escape-room puzzles and psychological horror, played through ArchipelagoMod " +
        "by DrBibop — a BepInEx plugin that is the in-game Archipelago client, so the " +
        "game connects to the multiworld itself with no emulator and no bridge. Key " +
        "items, cards, and progression across all three acts are shuffled into the " +
        "multiworld. You bring your own copy of Inscryption (owned on Steam); the " +
        "integration runs on BepInEx, the Unity mod loader, which the mod needs as a " +
        "separate prerequisite. The recommended one-click install is the r2modman / " +
        "Thunderstore mod manager (it pulls the mod plus BepInEx); the launcher can " +
        "also detect your Steam install and stage both for you, and guides the rest. " +
        "You connect to your server from the in-game menu when you start a new save.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means an Archipelago-named mod DLL is present in a detected/
    /// override Inscryption install's BepInEx\plugins tree. (We do NOT gate on our
    /// own stamp — the user may have installed via r2modman, the recommended route,
    /// which we honor.)
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads (the mod / BepInEx zips) and any working
    /// files. The actual mod is extracted INTO the Inscryption install's BepInEx
    /// folder, not here. Exposed as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Inscryption");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Hollow Knight /
    /// TUNIC / Subnautica / Jak). Per the brief, lives under Games/ROMs/inscryption/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "inscryption_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // ArchipelagoMod reports checks/items/goal to the AP server itself — the
    // launcher relays nothing. These exist for interface compatibility
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
        // 0. We need an Inscryption install to drop BepInEx + the mod into. Prefer
        //    an explicit override; else auto-detect the Steam install.
        progress.Report((2, "Locating your Inscryption installation..."));
        string? gameDir = ResolveInscryptionDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find an Inscryption installation. Open this game's Settings " +
                "and pick your Inscryption folder (the one containing Inscryption.exe), " +
                "or install Inscryption via Steam first. The Archipelago mod is added " +
                "on top of your own copy of the game.");

        // 1. Resolve the latest mod release from Thunderstore (pinned fallback).
        progress.Report((6, "Checking the latest ArchipelagoMod release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the ArchipelagoMod download on Thunderstore. Check your " +
                "internet connection, or install the mod with r2modman / Thunderstore " +
                "Mod Manager (recommended) from " + ModThunderstorePageUrl + " — see " +
                "Settings for the guided steps. The mod source is " + ModRepoUrl + ".");

        // 2. Ensure BepInEx is present — it is a SEPARATE, portable prerequisite the
        //    mod zip does NOT bundle. If it is already there (BepInEx folder /
        //    winhttp.dll), we leave it alone.
        if (!BepInExPresent(gameDir))
        {
            progress.Report((12, "Staging BepInEx (Unity Mono) into your Inscryption folder..."));
            await DownloadAndExtractBepInExAsync(
                BepInExZipUrl, $"inscryption-bepinex-{BepInExVersion}", gameDir, 12, 45, progress, ct);
        }
        else
        {
            progress.Report((45, "BepInEx already present — keeping your existing install."));
        }

        // 3. Download + extract the mod and merge its "plugins" folder INTO
        //    <Inscryption>/BepInEx/plugins/.
        string bepInExDir = Path.Combine(gameDir, "BepInEx");
        await DownloadAndExtractModAsync(zipUrl, version, bepInExDir, 48, 92, progress, ct);

        // 4. Stamp the version next to our sidecar so the tile can show it. (This is
        //    informational only — IsInstalled is judged by the DLL's presence.)
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepOk = BepInExPresent(gameDir);
        progress.Report((100,
            $"Staged ArchipelagoMod {version} into your BepInEx\\plugins folder" +
            (bepOk ? " (BepInEx is present)." : ".") +
            " To play: launch Inscryption ONCE so BepInEx finishes setting up, then on " +
            "the save-file menu click New Game, name your save, and enter your server " +
            "details on the next screen and press Connect. Open Settings for the guided " +
            "steps and links — the recommended one-click install is r2modman. (This " +
            "launcher cannot pre-fill the connection — it is entered in-game.)"));
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
        // HONEST: the AP server connection for Inscryption is entered IN-GAME (start
        // a New Game on the save-file menu, then enter the server info and press
        // Connect). There is no documented command-line / config prefill this
        // launcher can apply (verified — see header). So launching from this tile
        // just starts the game; the user connects in-game with the session
        // credentials (the settings panel + note surface those values to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartInscryption();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Inscryption runs perfectly well.
    public bool SupportsStandalone => true;

    /// ArchipelagoMod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartInscryption();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started Inscryption from here. Kill what we launched.
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
        // ArchipelagoMod receives items from the AP server directly; there is
        // nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Inscryption folder contains
    /// Inscryption.exe (and the Inscryption_Data folder is next to it). Return null
    /// when acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Inscryption install folder.";

        if (LooksLikeInscryptionDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeInscryptionDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like an Inscryption installation. Pick the folder " +
               "that contains Inscryption.exe (for Steam this is usually " +
               @"...\steamapps\common\Inscryption).";
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
            Text = "Inscryption is your own game (Steam) with the ArchipelagoMod added " +
                   "on top via BepInEx. The recommended one-click install is the r2modman / " +
                   "Thunderstore mod manager (it pulls the mod plus the BepInEx it needs). " +
                   "The launcher can also detect your Steam install and stage BepInEx and " +
                   "the mod files into it, but BepInEx is a separate prerequisite and you " +
                   "must run the game once so it finishes setting up (see the guided steps " +
                   "below). You connect to your server from the in-game menu when you start " +
                   "a new save. These external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "INSCRYPTION INSTALL", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveInscryptionDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Inscryption not detected. Pick your install folder below, or install " +
              "Inscryption via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // BepInEx status line
        bool bepOk = gameDir != null && BepInExPresent(gameDir);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = gameDir == null
                    ? ""
                    : (bepOk
                        ? "BepInEx found in your Inscryption folder."
                        : "BepInEx not found yet — Install on the Play tab stages it, or get " +
                          "it via r2modman / the BepInExPack page (links below)."),
            FontSize = 11, Foreground = bepOk ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        // mod status line
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                    ? "ArchipelagoMod found: " + modDll
                    : "ArchipelagoMod not found in BepInEx\\plugins yet (use Install on the " +
                      "Play tab, or install it via r2modman — recommended).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Inscryption install folder (the one containing Inscryption.exe). " +
                          "Detected from Steam automatically; set it here to override " +
                          "(non-standard Steam library, or another store).",
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
                Title            = "Select your Inscryption install folder (contains Inscryption.exe)",
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
                    System.Windows.MessageBox.Show(bad, "Not an Inscryption folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeInscryptionDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeInscryptionDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 1092790). Use this " +
                   "picker for a non-standard Steam library or another store.",
            FontSize = 11, Foreground = muted, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (this session) ────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After the mod is installed, launch the game. On the save-file menu, " +
                   "click New Game and name your save; on the next screen enter your server " +
                   "details (host/port, slot name, and password if any) and press Connect. " +
                   "You should see \"Connected\". This launcher does not pre-fill the " +
                   "connection — it is entered in-game.",
            FontSize = 11, Foreground = fg, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP (recommended: r2modman / Thunderstore Mod Manager)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Inscryption (on Steam). Install it if you have not. Use the picker above if it " +
                "was not detected.",
            "2. Recommended: install r2modman (or Thunderstore Mod Manager), pick Inscryption, search " +
                "\"ArchipelagoMod\" in the Online tab, and Download it — the BepInEx dependency is " +
                "installed automatically. Then use Start Modded.",
            "3. Alternative (advanced): the Install button on the Play tab stages BepInEx and " +
                "ArchipelagoMod straight into your Inscryption folder, or do it by hand from the " +
                "Thunderstore pages (links below) — extract BepInEx into the Inscryption folder, then " +
                "merge the mod's \"plugins\" folder into BepInEx\\plugins.",
            "4. Launch Inscryption ONCE so BepInEx finishes setting up.",
            "5. To play: on the save-file menu click New Game, name your save, then on the next " +
                "screen enter your server details and press Connect. You should see \"Connected\".",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg, TextWrapping = System.Windows.TextWrapping.Wrap,
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
            ("r2modman (mod manager) ↗",            R2ModManUrl),
            ("ArchipelagoMod (Thunderstore) ↗",     ModThunderstorePageUrl),
            ("BepInExPack for Inscryption ↗",       BepInExThunderstorePageUrl),
            ("Inscryption Setup Guide ↗",           SetupGuideUrl),
            ("ArchipelagoMod (GitHub source) ↗",    ModRepoUrl),
            ("Archipelago Official ↗",              ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2), Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0), FontSize = 12, Margin = new System.Windows.Thickness(0, 0, 0, 4),
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
        // GitHub source-repo releases are the AP-relevant news for this game (they
        // ship the apworld + yaml template and carry the changelog).
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

    /// "v1.0.3" → "1.0.3" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release from the Thunderstore experimental API:
    /// version + the direct package-zip download URL. Falls back to the pinned 1.0.3
    /// direct CDN URL when the API is unreachable. (Thunderstore is the only channel
    /// that carries the compiled mod — the GitHub releases ship only the apworld.)
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(TS_MOD_API_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // The experimental API shape is { latest: { version_number, download_url, ... }, ... }
            if (root.TryGetProperty("latest", out var latest)
                && latest.ValueKind == JsonValueKind.Object)
            {
                string? version = latest.TryGetProperty("version_number", out var v)
                    ? NormalizeTag(v.GetString())
                    : null;
                string? url = latest.TryGetProperty("download_url", out var u)
                    ? u.GetString()
                    : null;

                if (!string.IsNullOrWhiteSpace(version) && !string.IsNullOrWhiteSpace(url))
                    return (version!, url);

                // Have a version but no explicit URL → build the stable CDN path.
                if (!string.IsNullOrWhiteSpace(version))
                    return (version!,
                        $"https://thunderstore.io/package/download/{TS_NS}/{TS_PKG}/{version}/");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / shape changed → pinned fallback */ }

        return (FallbackModVersion, FallbackModZipUrl);
    }

    /// Resolve the BepInExPack direct download URL from the Thunderstore API; falls
    /// back to the pinned CDN URL. Used only as a freshness improvement over the
    /// pinned constant — the pinned URL is a perfectly valid net.
    private async Task<string> ResolveBepInExZipUrlAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(TS_BEPINEX_API_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("latest", out var latest)
                && latest.ValueKind == JsonValueKind.Object
                && latest.TryGetProperty("download_url", out var u)
                && u.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(u.GetString()))
            {
                return u.GetString()!;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through to the pinned URL */ }
        return BepInExZipUrl;
    }

    // ── Private helpers — Steam / Inscryption detection ───────────────────────

    /// The Inscryption install dir to use: the override (if set and valid) wins,
    /// else the Steam-detected install. Null when nothing is found.
    private string? ResolveInscryptionDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeInscryptionDir(ov))
            return ov;

        try { return DetectSteamInscryptionDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Inscryption if it has Inscryption.exe and/or the
    /// Inscryption_Data folder.
    private static bool LooksLikeInscryptionDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, InscryptionExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, "Inscryption_Data"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// True when BepInEx appears installed in an Inscryption folder. BepInEx (Mono)
    /// drops a "BepInEx" folder plus a winhttp.dll proxy at the game root.
    private static bool BepInExPresent(string gameDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return false;
            if (Directory.Exists(Path.Combine(gameDir, "BepInEx"))) return true;
            if (File.Exists(Path.Combine(gameDir, "winhttp.dll"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Inscryption install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_1092790.acf exists → steamapps\common\Inscryption.
    private static string? DetectSteamInscryptionDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{InscryptionSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    // Prefer the installdir named in the manifest; fall back to the
                    // conventional "Inscryption" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeInscryptionDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeInscryptionDir(conventional)) return conventional;
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

    /// Find the ArchipelagoMod DLL under the detected/override Inscryption install's
    /// BepInEx\plugins tree (recursive, case-insensitive). The exact filename is not
    /// officially documented, so we match ANY *.dll whose name contains "archipelago"
    /// (covers "ArchipelagoMod.dll" and any renamed/repackaged build). Returns the
    /// dll path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? game = ResolveInscryptionDir();
            if (game == null) return null;
            string pluginsDir = Path.Combine(game, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Inscryption: prefer the exe in the detected/override install; if that
    /// cannot be found but Steam is present, fall back to the steam:// URL. Surfaces
    /// a clear message rather than failing opaquely.
    private void StartInscryption()
    {
        string? game = ResolveInscryptionDir();
        string? exe  = game != null ? Path.Combine(game, InscryptionExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = game!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Inscryption.");

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
            "Could not find Inscryption.exe. Open this game's Settings and pick your " +
            "Inscryption install folder, or install Inscryption via Steam.",
            InscryptionExeName);
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    /// Download the ArchipelagoMod Thunderstore zip and merge its "plugins" folder
    /// into <Inscryption>/BepInEx/plugins/. Thunderstore package zips wrap the mod in
    /// a tree that contains a "plugins" folder (per the README's manual-install
    /// instructions). We locate that "plugins" folder and copy its CONTENTS into
    /// BepInEx\plugins so the DLL lands at BepInEx\plugins\... . If no "plugins"
    /// folder is found, we fall back to copying any folder/file that holds an
    /// Archipelago-named DLL.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string bepInExDir,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"inscryption-archipelagomod-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"inscryption-archipelagomod-x-{version}-{Guid.NewGuid():N}");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 7 / 10;
            await DownloadFileAsync(zipUrl, tempZip,
                $"Downloading ArchipelagoMod {version}...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Installing the mod into BepInEx\\plugins..."));
            string pluginsDir = Path.Combine(bepInExDir, "plugins");
            Directory.CreateDirectory(pluginsDir);
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // Prefer merging the contents of the zip's "plugins" folder.
            string? srcPluginsDir = FindPluginsDir(tempExtract);
            if (srcPluginsDir != null)
            {
                CopyDirectoryContents(srcPluginsDir, pluginsDir);
            }
            else
            {
                // Fallback: copy the folder that holds an Archipelago-named DLL
                // (drop it under plugins\ArchipelagoMod so it is clearly grouped).
                string? srcModDir = FindModSourceDir(tempExtract);
                if (srcModDir != null)
                {
                    string dest = Path.Combine(pluginsDir, "ArchipelagoMod");
                    CopyDirectoryContents(srcModDir, dest);
                }
                else
                {
                    // Last resort: dump the whole extract under plugins\ArchipelagoMod
                    // so the DLLs at least land under BepInEx\plugins.
                    string dest = Path.Combine(pluginsDir, "ArchipelagoMod");
                    CopyDirectoryContents(tempExtract, dest);
                }
            }

            progress.Report((pctEnd, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Find a "plugins" directory within an extracted Thunderstore tree (the mod
    /// zip nests one). Returns the first match, or null.
    private static string? FindPluginsDir(string root)
    {
        try
        {
            // A top-level "plugins" is the common case; search the whole tree to be
            // safe about an extra wrapper level.
            foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dir).Equals("plugins", StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    /// Find the folder that directly contains an Archipelago-named DLL inside an
    /// extracted tree. Returns null when none is found.
    private static string? FindModSourceDir(string root)
    {
        try
        {
            foreach (string dll in Directory.EnumerateFiles(root, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return Path.GetDirectoryName(dll);
            }
        }
        catch { /* ignore */ }
        return null;
    }

    /// Download + extract the BepInExPack Thunderstore zip into the Inscryption root.
    /// The package nests a "BepInExPack_Inscryption" folder whose CONTENTS (the
    /// BepInEx tree + winhttp.dll doorstop + config) must drop into the game root, so
    /// we locate that folder and copy its contents; if not found, we extract the zip
    /// straight into the root (covers a flat-packaged variant).
    private async Task DownloadAndExtractBepInExAsync(
        string zipUrl,
        string tag,
        string gameDir,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(), $"{tag}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(), $"{tag}-x-{Guid.NewGuid():N}");
        try
        {
            // Best-effort freshness: prefer the API's current URL over the pinned one.
            string resolvedUrl = await ResolveBepInExZipUrlAsync(ct);

            int dlEnd = pctStart + (pctEnd - pctStart) * 8 / 10;
            await DownloadFileAsync(resolvedUrl, tempZip,
                "Downloading BepInEx...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Extracting BepInEx into your Inscryption folder..."));
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // Thunderstore BepInExPack zips nest the payload in a
            // "BepInExPack_Inscryption" folder; its CONTENTS go to the game root.
            string? payload = FindBepInExPayloadDir(tempExtract);
            if (payload != null)
                CopyDirectoryContents(payload, gameDir);
            else
                CopyDirectoryContents(tempExtract, gameDir);

            progress.Report((pctEnd, "BepInEx staged."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Find the BepInExPack payload folder inside an extracted tree: a folder named
    /// "BepInExPack_Inscryption" (the standard nesting), else the folder that
    /// directly contains a "BepInEx" subfolder or a winhttp.dll. Null when none.
    private static string? FindBepInExPayloadDir(string root)
    {
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dir).Equals("BepInExPack_Inscryption", StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
            // Else: a folder that holds a BepInEx subfolder or the doorstop proxy.
            if (Directory.Exists(Path.Combine(root, "BepInEx")) ||
                File.Exists(Path.Combine(root, "winhttp.dll")))
                return root;
            foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
            {
                if (Directory.Exists(Path.Combine(dir, "BepInEx")) ||
                    File.Exists(Path.Combine(dir, "winhttp.dll")))
                    return dir;
            }
        }
        catch { /* ignore */ }
        return null;
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

    /// Recursively copy the CONTENTS of a source directory into a destination
    /// directory (merging into whatever is already there; overwriting files).
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
    // UTF-8, read-modify-write (same approach as Hollow Knight / TUNIC / Subnautica).

    private sealed class InscryptionSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private InscryptionSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<InscryptionSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(InscryptionSettings s)
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
