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

namespace LauncherV2.Plugins.Messenger;

// ═══════════════════════════════════════════════════════════════════════════════
// MessengerPlugin — install / launch for "The Messenger" (Sabotage Studio, 2018)
// played through the TheMessengerRandomizerModAP mod by alwaysintreble, which
// contains the in-game Archipelago Multiworld client. This is a NATIVE
// "ConnectsItself" integration in the same family as the shipped Hollow Knight /
// TUNIC / Stardew Valley / Jak plugins: the game itself speaks to the AP server
// (no emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of The Messenger (Steam appid 764790), and Archipelago support is delivered as
// a mod added on top. The honest integration ceiling — exactly like the shipped
// Hollow Knight / TUNIC / Stardew Valley plugins — is "automate what is possible,
// guide the irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "The Messenger" (verified against
//     worlds/messenger/__init__.py: `class MessengerWorld(World): ... game =
//     "The Messenger"`). GameId here = "messenger". The Messenger is a CORE
//     Archipelago world (it ships inside Archipelago itself — no custom_worlds
//     drop needed to generate). The web setup tutorial (path "setup/en") is by
//     alwaysintreble.
//
//   * THE MOD repo is alwaysintreble/TheMessengerRandomizerModAP (verified live
//     2026-06-14 — it is the AP fork of minous27/TheMessengerRandomizerMod, the
//     single-player randomizer). Its README points install + play instructions at
//     the official AP setup page. The latest release (v0.15.12, 2025-05-25) ships
//     a SINGLE asset:
//         TheMessengerRandomizerAP-0.15.12.zip
//     The official setup guide states plainly: "Extract the zip file to
//     `TheMessenger/Mods/` of your game's install location." That zip is a CLEAN
//     extract (no installer), so this plugin CAN best-effort stage it for you.
//
//   * CRITICAL HONESTY — THE MOD NEEDS THE *COURIER* MOD LOADER, AND COURIER IS A
//     PATCHER, NOT A PORTABLE ZIP. The Messenger does NOT use BepInEx directly:
//     it uses Courier (Brokemia/Courier), a dedicated mod loader for The
//     Messenger. The official setup guide says: "Download and install Courier Mod
//     Loader using the instructions on the release page. Latest release is
//     currently 0.7.1." Courier's OWN install steps are: move the contents of
//     Courier-vX.X.zip into your TheMessenger.exe folder, then RUN MiniInstaller.exe
//     (which PATCHES the game's Assembly so mods load). That MiniInstaller step is
//     an IRREDUCIBLE, side-effecting patch of the user's own game files — so this
//     plugin will STAGE the Courier zip into the install (best effort) but will NOT
//     silently run MiniInstaller.exe for you; it leads you to run it (and confirms
//     Courier afterwards). Faking a fully-automated "ready to play" that cannot
//     honestly exist would be dishonest theatre.
//
//     ALSO: Courier's GitHub releases are all marked "prerelease" (alpha), so the
//     GitHub `/releases/latest` endpoint 404s for it. This plugin resolves Courier
//     from the FULL `/releases` list (newest first) and pins a fallback to the
//     verified Courier-v0.7.1-alpha.zip direct URL.
//
//   * CONNECTION is made IN-GAME (verified against the official AP setup guide):
//     after the mod is installed, in the game go to `Options > Archipelago
//     Options`, enter your connection info with the option buttons, then select
//     "Connect to Archipelago". There is ALSO a config-file fallback the guide
//     documents for slot names that cannot be typed with the in-game keyboard: an
//     APConfig.toml in the game directory (when used, ALL connection info must be
//     in the file). The OFFICIALLY DOCUMENTED primary method is the IN-GAME menu,
//     and per an honest "don't invent an undocumented prefill" stance (same as
//     Hollow Knight / TUNIC / Jak), this plugin does NOT write APConfig.toml or
//     fake a connection prefill — the settings panel + post-install note surface
//     the session's host/port/slot for the user to type into the in-game fields,
//     and point at the APConfig.toml fallback for power users.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam The Messenger install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\The Messenger via appmanifest_764790.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and takes
//      precedence; it is validated (must contain TheMessenger.exe) and persisted
//      in this plugin's OWN sidecar (Games/ROMs/messenger/messenger_launcher.json)
//      — Core/SettingsStore is NOT modified.
//   2. INSTALL/UPDATE (best effort) = (a) if Courier is not present in the
//      detected install, download the pinned Courier zip and extract it into the
//      TheMessenger.exe folder (so MiniInstaller.exe is staged there for the user
//      to run); (b) download the mod's "TheMessengerRandomizerAP-*.zip" from the
//      real release and extract it into <The Messenger>/Mods/. The plugin then
//      presents clear, numbered, Hollow-Knight/TUNIC-style guided steps + links
//      (mod repo, Courier repo, the official AP setup guide, archipelago.gg) so the
//      user can run MiniInstaller.exe to finish the Courier install and connect
//      in-game. Never a fake one-click.
//   3. LAUNCH = run TheMessenger.exe from the detected/override install; if the
//      exe cannot be found but Steam is present, fall back to
//      steam://rungameid/764790. ConnectsItself = true (the mod owns the slot —
//      the launcher must NOT hold its own ApClient on it). SupportsStandalone =
//      true (plain The Messenger runs fine without AP). No connection prefill
//      (entered in-game), stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Hollow-Knight/TUNIC-style) ──
//   * "Installed" is judged by the presence of the AP mod DLL under a detected/
//     override The Messenger install's Mods tree (case-insensitive, recursive) —
//     NOT by an OUR-OWN version stamp, because the user may instead install the
//     mod by hand, which this launcher should honor. The mod's primary DLL name is
//     UNVERIFIED from a definitive source, so detection accepts ANY .dll under
//     Mods\ whose name contains "messenger" (the mod project is
//     TheMessengerRandomizerAP) — a deliberately tolerant heuristic that degrades
//     to "not installed" rather than throwing. If no The Messenger install is
//     detected, the tile simply reads "not installed".
//   * Steam library parsing is defensive: a hand-written tolerant VDF scan that
//     pulls quoted "path" values; any failure degrades to "The Messenger not
//     found" rather than throwing.
//   * No plaintext AP password is ever written by this plugin (the connection is
//     entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MessengerPlugin : IGamePlugin
{
    // ── Constants — the AP Messenger mod (real repo, verified 2026-06-14) ──────
    private const string MOD_OWNER = "alwaysintreble";
    private const string MOD_REPO  = "TheMessengerRandomizerModAP";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl  = "https://archipelago.gg/tutorial/The%20Messenger/setup/en";
    private const string GameInfoUrl    = "https://archipelago.gg/games/The%20Messenger/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Courier — The Messenger's dedicated mod loader (Brokemia/Courier). NOT
    // BepInEx, and NOT a clean portable zip: its install ends with running
    // MiniInstaller.exe to patch the game Assembly. Its releases are all marked
    // "prerelease" (alpha), so /releases/latest 404s — we resolve from the full
    // /releases list and pin a verified fallback.
    private const string COURIER_OWNER = "Brokemia";
    private const string COURIER_REPO  = "Courier";
    private const string CourierRepoUrl = $"https://github.com/{COURIER_OWNER}/{COURIER_REPO}";
    private const string CourierWikiUrl =
        $"https://github.com/{COURIER_OWNER}/{COURIER_REPO}/wiki/Installing-Courier";
    private const string CourierReleasesUrl = $"{CourierRepoUrl}/releases";
    private const string GH_COURIER_RELEASES_URL =
        $"https://api.github.com/repos/{COURIER_OWNER}/{COURIER_REPO}/releases";

    /// Pinned fallback for Courier when the GitHub API is unreachable. v0.7-alpha
    /// (asset Courier-v0.7.1-alpha.zip) verified live 2026-06-14 — the setup guide
    /// names "0.7.1" as current.
    private const string CourierFallbackVersion = "0.7.1-alpha";
    private static readonly string CourierFallbackZipUrl =
        $"{CourierRepoUrl}/releases/download/v0.7-alpha/Courier-v0.7.1-alpha.zip";

    // Steam — The Messenger appid 764790.
    private const string MessengerSteamAppId = "764790";
    private static readonly string SteamRunUrl = $"steam://rungameid/{MessengerSteamAppId}";

    /// The standard Steam install sub-folder name for The Messenger.
    private const string SteamCommonFolderName = "The Messenger";

    /// The base-game executable name (verified against the setup guide / Courier
    /// docs: TheMessenger.exe, with the TheMessenger_Data folder beside it).
    private const string MessengerExeName = "TheMessenger.exe";
    private const string MessengerDataFolder = "TheMessenger_Data";

    /// Where mod zips extract — relative to the game root (verified from the setup
    /// guide: "Extract the zip file to `TheMessenger/Mods/`").
    private const string ModsFolderName = "Mods";

    /// The in-game / file connection touchpoints (verified from the setup guide).
    private const string ApConfigFileName = "APConfig.toml";

    /// Pinned fallback for the AP mod when the GitHub API is unreachable. v0.15.12
    /// verified live 2026-06-14; the single asset is
    /// "TheMessengerRandomizerAP-0.15.12.zip".
    private const string FallbackVersion = "0.15.12";
    private const string FallbackZipName = "TheMessengerRandomizerAP-0.15.12.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "messenger";
    public string DisplayName => "The Messenger";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/messenger/__init__.py
    /// (`class MessengerWorld(World): ... game = "The Messenger"`).
    public string ApWorldName => "The Messenger";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "messenger.png");

    public string ThemeAccentColor => "#3A4E8C";   // 8-bit indigo / Ninja blue
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "The Messenger, Sabotage Studio's 2018 retro action-platformer, played " +
        "through the TheMessengerRandomizerModAP mod by alwaysintreble — which " +
        "bundles an in-game Archipelago client, so the game connects to the " +
        "multiworld itself with no emulator and no bridge. Key items, upgrades, " +
        "notes and more are shuffled across the multiworld. You bring your own copy " +
        "of The Messenger (owned on Steam); the integration runs on Courier, the " +
        "game's dedicated mod loader. The launcher detects your Steam install and " +
        "can stage Courier and the Archipelago mod into it, then guides the rest " +
        "(Courier finishes by running its MiniInstaller). You connect to your " +
        "server from the in-game Archipelago Options menu.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the AP Messenger mod DLL is present in a detected/override
    /// install's Mods tree. (We do NOT gate on our own stamp — the user may have
    /// installed the mod by hand, which we honor.)
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads (the mod / Courier zips) and any working
    /// files. The actual mod is extracted INTO the game's Mods folder, not here.
    /// Exposed as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Messenger");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Hollow Knight /
    /// TUNIC / Stardew / Jak). Per the brief, lives under Games/ROMs/messenger/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "messenger_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The TheMessengerRandomizerModAP mod reports checks/items/goal to the AP
    // server itself — the launcher relays nothing. These exist for interface
    // compatibility (ConnectsItself = true).
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
        // 0. We need a The Messenger install to drop Courier + the mod into. Prefer
        //    an explicit override; else auto-detect the Steam install.
        progress.Report((2, "Locating your The Messenger installation..."));
        string? gameDir = ResolveMessengerDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a The Messenger installation. Open this game's " +
                "Settings and pick your The Messenger folder (the one containing " +
                "TheMessenger.exe), or install The Messenger via Steam first. The " +
                "Archipelago mod is added on top of your own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback).
        progress.Report((6, "Checking the latest Messenger Randomizer (AP) release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Messenger Randomizer (AP) mod download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                ModRepoUrl + "/releases — see Settings for the guided steps. The mod " +
                "repo is " + ModRepoUrl + ".");

        // 2. Ensure Courier (the mod loader) is staged. Courier is NOT bundled with
        //    the mod, and its install is a PATCH (MiniInstaller.exe) we do not run
        //    for the user — so if Courier looks absent we stage its zip into the
        //    game root and the guided steps tell the user to run MiniInstaller.exe.
        //    If Courier already appears present, we leave it alone.
        if (!CourierPresent(gameDir))
        {
            progress.Report((12, "Staging Courier (The Messenger mod loader) into your game folder..."));
            var (cVer, cZipUrl) = await ResolveLatestCourierAsync(ct);
            if (cZipUrl != null)
            {
                await DownloadAndExtractZipToDirAsync(
                    cZipUrl, $"messenger-courier-{cVer}", gameDir, 12, 45, progress, ct);
            }
            else
            {
                progress.Report((45,
                    "Could not download Courier automatically — install it by hand from " +
                    "the Courier releases (link in Settings)."));
            }
        }
        else
        {
            progress.Report((45, "Courier already present — keeping your existing install."));
        }

        // 3. Download + extract the mod INTO <The Messenger>/Mods/. The setup guide
        //    says to extract the zip to TheMessenger/Mods/.
        string modsDir = Path.Combine(gameDir, ModsFolderName);
        await DownloadAndExtractModAsync(zipUrl, version, modsDir, 48, 92, progress, ct);

        // 4. Stamp the version next to our sidecar so the tile can show it. (This is
        //    informational only — IsInstalled is judged by the DLL's presence.)
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool courierOk = CourierPresent(gameDir);
        progress.Report((100,
            $"Staged the Messenger Randomizer (AP) mod {version} into your Mods folder" +
            (courierOk ? " (Courier appears present)." : ".") +
            " IMPORTANT: to finish the Courier mod loader you must run MiniInstaller.exe " +
            "in your The Messenger folder ONCE (it patches the game so mods load) — this " +
            "launcher does not run it for you. Then launch The Messenger, go to Options > " +
            "Archipelago Options, enter your server details, and select \"Connect to " +
            "Archipelago\". Open Settings for the guided steps and links. (This launcher " +
            "cannot pre-fill the connection — it is entered in-game.)"));
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
        // HONEST: the AP server connection for The Messenger is entered in the
        // IN-GAME Archipelago menu (Options > Archipelago Options -> connection
        // info -> Connect to Archipelago). There is a documented APConfig.toml
        // fallback for un-typable names, but the OFFICIAL primary method is the
        // in-game menu, and per the "don't invent an undocumented prefill" stance
        // this launcher does not write that file. So launching from this tile just
        // starts the game; the user connects in-game with the session credentials
        // (the settings panel + note surface those values to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism is applied
        StartMessenger();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) The Messenger runs perfectly well.
    public bool SupportsStandalone => true;

    /// The TheMessengerRandomizerModAP mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartMessenger();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started The Messenger from here. Kill what we launched.
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
        // The TheMessengerRandomizerModAP mod receives items from the AP server
        // directly; there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid The Messenger folder contains
    /// TheMessenger.exe (and the TheMessenger_Data folder is next to it). Return
    /// null when acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your The Messenger install folder.";

        if (LooksLikeMessengerDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeMessengerDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a The Messenger installation. Pick the folder " +
               "that contains TheMessenger.exe (for Steam this is usually " +
               @"...\steamapps\common\The Messenger).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Unverified-offline honesty header ─────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "The Messenger is your own game (Steam) with the Archipelago mod added " +
                   "on top via Courier, the game's mod loader. The launcher detects your " +
                   "Steam install and can stage Courier and the Archipelago mod files into " +
                   "it, but Courier finishes by running its MiniInstaller.exe (which patches " +
                   "the game), and that step is left to you (see the guided steps below). " +
                   "You connect to your server from the in-game Archipelago Options menu. " +
                   "These external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "THE MESSENGER INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveMessengerDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "The Messenger not detected. Pick your install folder below, or install " +
              "The Messenger via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // Courier status line
        bool courierOk = gameDir != null && CourierPresent(gameDir);
        panel.Children.Add(new TextBlock
        {
            Text = gameDir == null
                    ? ""
                    : (courierOk
                        ? "Courier mod loader found in your The Messenger folder."
                        : "Courier not found yet — Install on the Play tab stages it, then run " +
                          "MiniInstaller.exe in your game folder. Or get Courier from its " +
                          "releases (link below)."),
            FontSize = 11, Foreground = courierOk ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        // mod status line
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                    ? "Archipelago mod found: " + modDll
                    : "Archipelago mod not found in the Mods folder yet (use Install on the " +
                      "Play tab).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your The Messenger install folder (the one containing " +
                          "TheMessenger.exe). Detected from Steam automatically; set it here " +
                          "to override (non-standard Steam library, or another store).",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your The Messenger install folder (contains TheMessenger.exe)",
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
                    System.Windows.MessageBox.Show(bad, "Not a The Messenger folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeMessengerDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeMessengerDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 764790). Use this " +
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
            Text = "In the game, go to Options > Archipelago Options, enter your connection " +
                   "info (server / port, slot name, and password if any) with the option " +
                   "buttons, then select \"Connect to Archipelago\". For a slot name that " +
                   "cannot be typed with the in-game keyboard, edit " + ApConfigFileName +
                   " in your game directory instead (when used, ALL connection info must be " +
                   "in that file). This launcher does not pre-fill the connection.",
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
            "1. Own The Messenger (on Steam). Install it if you have not. Use the picker above " +
                "if it was not detected.",
            "2. Install the Courier mod loader: the Install button on the Play tab stages Courier " +
                "into your The Messenger folder, or download it from the Courier releases (link " +
                "below).",
            "3. Run MiniInstaller.exe in your The Messenger folder ONCE to finish Courier (it " +
                "patches the game so mods load). This launcher does not run it for you.",
            "4. Install the Archipelago mod: Install on the Play tab downloads the mod and extracts " +
                "it into your Mods folder, or do it by hand from the mod releases (link below) — " +
                "extract the zip to TheMessenger\\Mods\\.",
            "5. Launch The Messenger and confirm the mod loaded (the Archipelago Options entry " +
                "appears under Options).",
            "6. To play: go to Options > Archipelago Options, enter your server / slot / password " +
                "with the option buttons, then select \"Connect to Archipelago\".",
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
            ("Messenger Randomizer (AP) (GitHub) ↗", ModRepoUrl),
            ("The Messenger Setup Guide ↗",          SetupGuideUrl),
            ("The Messenger Guide (AP) ↗",           GameInfoUrl),
            ("Courier mod loader (releases) ↗",      CourierReleasesUrl),
            ("Installing Courier (wiki) ↗",          CourierWikiUrl),
            ("Archipelago Official ↗",               ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2), Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
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

    /// "v0.15.12" → "0.15.12" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the mod zip download URL. Prefers
    /// the "TheMessengerRandomizerAP*.zip" asset. Falls back to the pinned 0.15.12
    /// direct URL when the API is unreachable.
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
                string? preferred = null;   // the mod zip (TheMessengerRandomizerAP*.zip)
                string? anyZip    = null;   // any .zip
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null && lower.Contains("messenger"))
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

    /// Resolve the latest Courier release: version + the Courier zip download URL.
    /// Courier's releases are ALL marked "prerelease", so /releases/latest 404s —
    /// we read the FULL /releases list (newest first) and pick the newest release's
    /// newest Courier-*.zip. Falls back to the pinned Courier-v0.7.1-alpha.zip when
    /// the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestCourierAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_COURIER_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    string? version = rel.TryGetProperty("tag_name", out var t)
                        ? NormalizeTag(t.GetString())
                        : null;

                    if (!rel.TryGetProperty("assets", out var assets)
                        || assets.ValueKind != JsonValueKind.Array)
                        continue;

                    // Within this (newest) release, pick the highest Courier-*.zip
                    // by ordinal name compare (…v0.7.1-alpha.zip > …v0.7.0-alpha.zip).
                    string? bestUrl  = null;
                    string  bestName = "";
                    foreach (var a in assets.EnumerateArray())
                    {
                        string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        if (name == null || url == null) continue;

                        string lower = name.ToLowerInvariant();
                        if (!lower.EndsWith(".zip") || !lower.Contains("courier")) continue;

                        if (bestUrl == null ||
                            string.CompareOrdinal(name, bestName) > 0)
                        {
                            bestUrl  = url;
                            bestName = name;
                        }
                    }

                    if (bestUrl != null)
                        return (version ?? CourierFallbackVersion, bestUrl);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (CourierFallbackVersion, CourierFallbackZipUrl);
    }

    // ── Private helpers — Steam / Messenger detection ─────────────────────────

    /// The The Messenger install dir to use: the override (if set and valid) wins,
    /// else the Steam-detected install. Null when nothing is found.
    private string? ResolveMessengerDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeMessengerDir(ov))
            return ov;

        try { return DetectSteamMessengerDir(); }
        catch { return null; }
    }

    /// A folder "looks like" The Messenger if it has TheMessenger.exe and/or the
    /// TheMessenger_Data folder.
    private static bool LooksLikeMessengerDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, MessengerExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, MessengerDataFolder))) return true;
            return false;
        }
        catch { return false; }
    }

    /// True when Courier appears installed in a The Messenger folder. Courier drops
    /// a MiniInstaller.exe at the game root, a Courier.dll (and patcher/mods
    /// scaffolding); the most distinctive marker is MiniInstaller.exe. We also
    /// accept a Courier.dll anywhere under the game root.
    private static bool CourierPresent(string gameDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return false;
            if (File.Exists(Path.Combine(gameDir, "MiniInstaller.exe"))) return true;
            if (File.Exists(Path.Combine(gameDir, "Courier.dll"))) return true;
            // The Mods folder itself is a Courier convention — but on its own it is
            // weak evidence, so only treat MiniInstaller/Courier.dll as definitive.
            string managed = Path.Combine(gameDir, MessengerDataFolder, "Managed");
            if (Directory.Exists(managed) &&
                File.Exists(Path.Combine(managed, "Courier.dll"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam The Messenger install: read the Steam root from the
    /// registry, gather all library roots from libraryfolders.vdf, and find the one
    /// whose appmanifest_764790.acf exists → steamapps\common\The Messenger.
    private static string? DetectSteamMessengerDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{MessengerSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    // Prefer the installdir named in the manifest; fall back to the
                    // conventional "The Messenger" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeMessengerDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeMessengerDir(conventional)) return conventional;
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

    /// Find the AP mod DLL under the detected/override install's Mods tree
    /// (recursive, case-insensitive). The mod's exact primary DLL name is not
    /// definitively documented, so we accept any .dll under Mods\ whose name
    /// contains "messenger" (the project is TheMessengerRandomizerAP). Returns the
    /// dll path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? game = ResolveMessengerDir();
            if (game == null) return null;
            string modsDir = Path.Combine(game, ModsFolderName);
            if (!Directory.Exists(modsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(modsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name.IndexOf("messenger", StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start The Messenger: prefer the exe in the detected/override install; if
    /// that cannot be found but Steam is present, fall back to the steam:// URL.
    /// Surfaces a clear message rather than failing opaquely.
    private void StartMessenger()
    {
        string? game = ResolveMessengerDir();
        string? exe  = game != null ? Path.Combine(game, MessengerExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = game!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start The Messenger.");

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
            "Could not find TheMessenger.exe. Open this game's Settings and pick your " +
            "The Messenger install folder, or install The Messenger via Steam.",
            MessengerExeName);
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    /// Download the mod zip and extract it INTO <The Messenger>/Mods/. The setup
    /// guide says to extract the zip to TheMessenger/Mods/. If the zip nests a
    /// single wrapper folder, we keep its structure (Courier reads the Mods tree
    /// recursively) — we simply extract straight into Mods.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string modsDir,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"messenger-mod-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 8 / 10;
            await DownloadFileAsync(zipUrl, tempZip,
                $"Downloading Messenger Randomizer (AP) {version}...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Installing the mod into your Mods folder..."));
            Directory.CreateDirectory(modsDir);
            // Extract straight into Mods\. The guide instructs extracting the zip
            // to TheMessenger/Mods/, so this matches the documented layout.
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, modsDir, overwriteFiles: true);

            progress.Report((pctEnd, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    /// Download + extract a portable zip (e.g. Courier) straight into a target dir.
    /// NOTE: Courier's zip lands MiniInstaller.exe + scaffolding at the game root;
    /// it still requires the user to RUN MiniInstaller.exe afterwards (we do not).
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
                "Downloading Courier...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Extracting Courier into your The Messenger folder..."));
            Directory.CreateDirectory(targetDir);
            // Courier zips extract their files at the archive root (MiniInstaller.exe,
            // patcher/ scaffolding, ...), so extract straight in.
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, targetDir, overwriteFiles: true);
            progress.Report((pctEnd, "Courier staged (run MiniInstaller.exe to finish)."));
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

    private sealed class MessengerSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private MessengerSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<MessengerSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(MessengerSettings s)
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
