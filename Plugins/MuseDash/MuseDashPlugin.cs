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

namespace LauncherV2.Plugins.MuseDash;

// ═══════════════════════════════════════════════════════════════════════════════
// MuseDashPlugin — install / launch for "Muse Dash" (PeroPeroGames / hasuhasu,
// 2018) played through the ArchipelagoMuseDash mod by DeamonHunter, which contains
// the in-game Archipelago client for Muse Dash. This is a NATIVE "ConnectsItself"
// integration in the same family as the shipped Hollow Knight / TUNIC / Stardew
// Valley / Jak plugins: the game itself speaks to the AP server (no emulator, no
// Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Muse Dash (Steam appid 774171), and Archipelago support is delivered as a
// MelonLoader mod added on top. The honest integration ceiling — exactly like the
// shipped Hollow Knight / TUNIC plugins — is "automate what is possible, guide the
// irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "Muse Dash" (verified against
//     worlds/musedash/__init__.py: `class MuseDashWorld(World): ... game =
//     "Muse Dash"`). GameId here = "musedash". Muse Dash is a CORE Archipelago
//     world (it ships inside Archipelago itself — no custom_worlds drop is needed
//     to generate).
//
//   * THE MOD repo is DeamonHunter/ArchipelagoMuseDash (verified live 2026-06-14).
//     The latest release (v1.5.33) ships TWO assets:
//         ArchipelagoMuseDash.zip   (the mod — extract into <MuseDash>/Mods/)
//         musedash.apworld          (the AP world, for the AP host — NOT used here;
//                                    Muse Dash is already core, so the launcher
//                                    ignores it, same as the TUNIC plugin ignores
//                                    its tunic.apworld asset)
//     The mod zip's contents (incl. ArchipelagoMuseDash.dll) drop FLAT into the
//     game's Mods folder — the official setup guide states plainly: "All files
//     must be under the /Mods/ folder and not within a sub folder inside of
//     /Mods/."
//
//   * CRITICAL HONESTY — MelonLoader IS A SEPARATE PREREQUISITE, NOT BUNDLED. Muse
//     Dash is a Unity IL2CPP game, so the mod needs MelonLoader (v0.6.1+, per the
//     mod README + setup guide), downloaded SEPARATELY from the LavaGang/MelonLoader
//     releases. The mod release zip does NOT contain MelonLoader. MelonLoader ships
//     an INSTALLER (MelonLoader.Installer.exe) that you point at the game — there is
//     no clean portable "extract into the folder" drop the way BepInEx has, so this
//     plugin does NOT silently auto-install MelonLoader (faking that would be
//     dishonest theatre). Instead it best-effort downloads the MelonLoader installer
//     for the user to run, downloads + stages the AP mod into the Mods folder when
//     MelonLoader is present, and otherwise guides the irreducible steps with links.
//     The mod ALSO needs .NET Framework 4.8 + .NET Desktop Runtime 6.0.x (per the
//     setup guide) — surfaced as guided links, not auto-installed.
//
//   * MELONLOADER MUST RUN ONCE FIRST. MelonLoader only creates the game's /Mods/
//     folder after the game has been launched once with MelonLoader installed. So
//     the honest order is: install MelonLoader -> launch Muse Dash once (let it
//     reach the title screen) -> THEN the /Mods/ folder exists for the AP mod. This
//     plugin creates the Mods folder itself when staging (so a manual MelonLoader
//     install still lands the mod), but the guided steps state the run-once
//     requirement explicitly.
//
//   * CONNECTION is made IN-GAME (verified against the official AP setup guide):
//     after the mod is installed, launch the game and "Click on the button in the
//     bottom right. Enter in the details for the archipelago game, such as the
//     server address with port (e.g. archipelago.gg:38381), username and password."
//     There is NO command-line arg and NO config file this launcher can pre-write
//     (verified against the setup guide). So this plugin does NOT attempt a
//     connection prefill — the post-install note + settings panel surface the
//     session's host/port/slot for the user to type into the in-game popup.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Muse Dash install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Muse Dash via appmanifest_774171.acf. A manual install-dir
//      OVERRIDE (settings folder picker) is also supported and takes precedence;
//      it is validated (must contain MuseDash.exe) and persisted in this plugin's
//      OWN sidecar (Games/ROMs/musedash/musedash_launcher.json) — Core/SettingsStore
//      is NOT modified.
//   2. INSTALL/UPDATE (best effort) = (a) if MelonLoader is not present in the
//      detected install, download the MelonLoader installer (LavaGang) into this
//      plugin's working folder for the user to run; (b) download the mod's
//      "ArchipelagoMuseDash.zip" from the real release and extract it FLAT into
//      <MuseDash>/Mods/. The plugin then presents clear, numbered, TUNIC-style
//      guided steps + links (mod repo, the official AP setup guide, MelonLoader,
//      .NET requirements, archipelago.gg) so the user can install/run MelonLoader
//      once and connect in-game. Never a fake one-click.
//   3. LAUNCH = run MuseDash.exe from the detected/override install; if the exe
//      cannot be found but Steam is present, fall back to steam://rungameid/774171.
//      ConnectsItself = true (the mod owns the slot — the launcher must NOT hold its
//      own ApClient on it). SupportsStandalone = true (plain Muse Dash runs fine
//      without AP). No connection prefill (entered in-game), stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Hollow-Knight/TUNIC-style) ──
//   * "Installed" is judged by the presence of ArchipelagoMuseDash.dll under a
//     detected/override Muse Dash install's Mods tree (case-insensitive, recursive)
//     — NOT by an OUR-OWN version stamp, because the user may instead install the
//     mod by hand (or via a mod manager), which this launcher should honor. If no
//     Muse Dash install is detected, the tile simply reads "not installed".
//   * Steam library parsing is defensive: a hand-written tolerant VDF scan that
//     pulls quoted "path" values; any failure degrades to "Muse Dash not found"
//     rather than throwing.
//   * No plaintext AP password is ever written by this plugin (the connection is
//     entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class MuseDashPlugin : IGamePlugin
{
    // ── Constants — the ArchipelagoMuseDash mod (real repo, verified 2026-06-14) ──
    private const string MOD_OWNER = "DeamonHunter";
    private const string MOD_REPO  = "ArchipelagoMuseDash";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Muse%20Dash/setup/en";
    private const string GameInfoUrl     = "https://archipelago.gg/games/Muse%20Dash/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // MelonLoader — the SEPARATE mod-loader prerequisite (Unity IL2CPP). Unlike
    // BepInEx, MelonLoader ships an INSTALLER (a wizard you point at the game), so
    // the plugin downloads the installer for the user to run rather than faking a
    // portable extract. Pinned to the build the setup guide names (0.6.1) for the
    // direct-download fallback; the page link always points at the latest.
    private const string MelonLoaderSite = "https://github.com/LavaGang/MelonLoader/releases/latest";
    private const string MelonLoaderVersion = "0.6.1";
    private const string MelonLoaderInstallerName = "MelonLoader.Installer.exe";
    private static readonly string MelonLoaderInstallerUrl =
        "https://github.com/LavaGang/MelonLoader.Installer/releases/latest/download/" +
        MelonLoaderInstallerName;

    // .NET prerequisites named by the setup guide (links only — not auto-installed).
    private const string DotNetFramework48Url =
        "https://dotnet.microsoft.com/download/dotnet-framework/net48";
    private const string DotNetRuntime6Url =
        "https://dotnet.microsoft.com/download/dotnet/6.0";

    // Steam — Muse Dash appid 774171.
    private const string MuseDashSteamAppId = "774171";
    private static readonly string SteamRunUrl = $"steam://rungameid/{MuseDashSteamAppId}";

    /// The standard Steam install sub-folder name for Muse Dash.
    private const string SteamCommonFolderName = "Muse Dash";

    /// The base-game executable name.
    private const string MuseDashExeName = "MuseDash.exe";

    /// The mod's primary DLL placed (FLAT) under <MuseDash>/Mods/ (verified).
    private const string ModPrimaryDll = "ArchipelagoMuseDash.dll";

    /// Pinned fallback for the mod when the GitHub API is unreachable. v1.5.33
    /// verified live 2026-06-14; the mod asset is "ArchipelagoMuseDash.zip".
    private const string FallbackVersion = "1.5.33";
    private const string FallbackZipName = "ArchipelagoMuseDash.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "musedash";
    public string DisplayName => "Muse Dash";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/musedash/__init__.py
    /// (`class MuseDashWorld(World): ... game = "Muse Dash"`).
    public string ApWorldName => "Muse Dash";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "musedash.png");

    public string ThemeAccentColor => "#E05A9A";   // Marija/Buro rhythm-game pink
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Muse Dash, the 2018 anime rhythm game by PeroPeroGames, played through the " +
        "ArchipelagoMuseDash mod by DeamonHunter — which bundles an in-game " +
        "Archipelago client, so the game connects to the multiworld itself with no " +
        "emulator and no bridge. Songs are locked behind the multiworld and unlocked " +
        "as you collect Music Sheets, with your goal a target number of completed " +
        "tracks. You bring your own copy of Muse Dash (owned on Steam); the " +
        "integration runs on MelonLoader, the Unity mod loader. The launcher detects " +
        "your Steam install, can fetch MelonLoader and stage the Archipelago mod into " +
        "your Mods folder, and guides the rest. You connect to your server from the " +
        "in-game Archipelago menu (the button at the bottom-right).";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the ArchipelagoMuseDash mod DLL is present in a detected/
    /// override Muse Dash install's Mods tree. (We do NOT gate on our own stamp —
    /// the user may have installed the mod by hand, which we honor.)
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads (the mod zip / the MelonLoader installer)
    /// and any working files. The actual mod is extracted INTO the Muse Dash
    /// install's Mods folder, not here. Exposed as GameDirectory for the
    /// IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "MuseDash");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Hollow Knight /
    /// TUNIC / Stardew / Jak). Per the brief, lives under Games/ROMs/musedash/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "musedash_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The ArchipelagoMuseDash mod reports checks/items/goal to the AP server itself
    // — the launcher relays nothing. These exist for interface compatibility
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
        // 0. We need a Muse Dash install to drop the mod into. Prefer an explicit
        //    override; else auto-detect the Steam install.
        progress.Report((2, "Locating your Muse Dash installation..."));
        string? museDir = ResolveMuseDashDir();
        if (museDir == null)
            throw new InvalidOperationException(
                "Could not find a Muse Dash installation. Open this game's Settings " +
                "and pick your Muse Dash folder (the one containing MuseDash.exe), or " +
                "install Muse Dash via Steam first. The Archipelago mod is added on " +
                "top of your own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback).
        progress.Report((6, "Checking the latest ArchipelagoMuseDash release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the ArchipelagoMuseDash mod download on GitHub. Check " +
                "your internet connection, or download it manually from " +
                ModRepoUrl + "/releases — see Settings for the guided steps. The mod " +
                "repo is " + ModRepoUrl + ".");

        // 2. Ensure MelonLoader is present — it is a SEPARATE prerequisite the mod
        //    zip does NOT bundle, and it ships as an installer (not a portable
        //    drop). If it is not already there, fetch the installer into our working
        //    folder for the user to run (HONEST: we cannot silently install it).
        bool melonOk = MelonLoaderPresent(museDir);
        string? installerPath = null;
        if (!melonOk)
        {
            progress.Report((12, "Downloading the MelonLoader installer (you will run it)..."));
            installerPath = await TryDownloadMelonLoaderInstallerAsync(20, 45, progress, ct);
        }
        else
        {
            progress.Report((45, "MelonLoader already present — keeping your existing install."));
        }

        // 3. Download + extract the mod's contents FLAT into <MuseDash>/Mods/.
        //    The setup guide requires the files to sit directly under /Mods/ and NOT
        //    inside a sub-folder, so we flatten any single wrapper directory the zip
        //    may contain.
        string modsDir = Path.Combine(museDir, "Mods");
        await DownloadAndExtractModFlatAsync(zipUrl, version, modsDir, 48, 92, progress, ct);

        // 4. Stamp the version next to our sidecar so the tile can show it. (This is
        //    informational only — IsInstalled is judged by the DLL's presence.)
        WriteStampedVersion(version);
        InstalledVersion = version;

        // 5. Honest completion note: state what is staged vs. what the user must do.
        string melonNote = melonOk
            ? "MelonLoader is already installed."
            : (installerPath != null
                ? "MelonLoader is NOT installed yet — the installer was downloaded to \"" +
                  installerPath + "\". Run it, point it at your Muse Dash folder, install, " +
                  "then launch Muse Dash ONCE so the Mods folder is finalised."
                : "MelonLoader is NOT installed yet and the installer could not be " +
                  "downloaded automatically — get it from " + MelonLoaderSite + " and run it " +
                  "against your Muse Dash folder.");

        progress.Report((100,
            $"Staged the ArchipelagoMuseDash mod {version} into your Muse Dash\\Mods folder. " +
            melonNote +
            " The mod also needs .NET Framework 4.8 and .NET Desktop Runtime 6.0.x (links in " +
            "Settings). To play: launch Muse Dash, then click the button at the bottom-right " +
            "and enter your server address:port, username, and password. Open Settings for " +
            "the guided steps and links. (This launcher cannot pre-fill the connection — it " +
            "is entered in-game.)"));
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
        // HONEST: the AP server connection for Muse Dash is entered in the IN-GAME
        // Archipelago menu (the button at the bottom-right -> server address:port,
        // username, password). There is no documented command-line / config prefill
        // this launcher can apply (verified — see header). So launching from this
        // tile just starts the game; the user connects in-game with the session
        // credentials (the settings panel + note surface those values to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartMuseDash();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Muse Dash runs perfectly well.
    public bool SupportsStandalone => true;

    /// The ArchipelagoMuseDash mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartMuseDash();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started Muse Dash from here. Kill what we launched.
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
        // The ArchipelagoMuseDash mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Muse Dash folder contains
    /// MuseDash.exe (and the MuseDash_Data folder is next to it). Return null when
    /// acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Muse Dash install folder.";

        if (LooksLikeMuseDashDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeMuseDashDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Muse Dash installation. Pick the folder that " +
               "contains MuseDash.exe (for Steam this is usually " +
               @"...\steamapps\common\Muse Dash).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Unverified-offline honesty header ─────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Muse Dash is your own game (Steam) with the ArchipelagoMuseDash mod " +
                   "added on top via MelonLoader. The launcher detects your Steam install, " +
                   "can fetch the MelonLoader installer and stage the Archipelago mod into " +
                   "your Mods folder, but MelonLoader is a separate installer you run " +
                   "yourself, and you must launch the game once so the Mods folder is " +
                   "created (see the guided steps below). You connect to your server from " +
                   "the in-game Archipelago menu. These external steps are not verified by " +
                   "this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "MUSE DASH INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? museDir     = ResolveMuseDashDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = museDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + museDir
                : "Detected Steam install: " + museDir)
            : "Muse Dash not detected. Pick your install folder below, or install Muse " +
              "Dash via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = museDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // MelonLoader status line
        bool melonOk = museDir != null && MelonLoaderPresent(museDir);
        panel.Children.Add(new TextBlock
        {
            Text = museDir == null
                    ? ""
                    : (melonOk
                        ? "MelonLoader found in your Muse Dash folder."
                        : "MelonLoader not found yet — Install on the Play tab downloads its " +
                          "installer for you to run, or get it from the MelonLoader releases " +
                          "(link below)."),
            FontSize = 11, Foreground = melonOk ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        // mod status line
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                    ? "ArchipelagoMuseDash mod found: " + modDll
                    : "ArchipelagoMuseDash mod not found in the Mods folder yet (use Install " +
                      "on the Play tab).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? museDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Muse Dash install folder (the one containing MuseDash.exe). " +
                          "Detected from Steam automatically; set it here to override " +
                          "(non-standard Steam library, or another store).",
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
                Title            = "Select your Muse Dash install folder (contains MuseDash.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? museDir ?? "")
                                   ? (overrideDir ?? museDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    MessageBox.Show(bad, "Not a Muse Dash folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeMuseDashDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeMuseDashDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 774171). Use this " +
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
            Text = "Launch Muse Dash, then click the button at the bottom-right of the " +
                   "screen. In the pop-up, enter the server address with port (e.g. " +
                   "archipelago.gg:38281), your username (slot name), and the password " +
                   "(if any). This launcher does not pre-fill the connection — it is " +
                   "entered in-game.",
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
            "1. Own Muse Dash (on Steam). Install it if you have not. Use the picker above if it " +
                "was not detected.",
            "2. Install the prerequisites: .NET Framework 4.8 and .NET Desktop Runtime 6.0.x " +
                "(links below) if you do not already have them.",
            "3. Install MelonLoader: the Install button on the Play tab downloads the MelonLoader " +
                "installer for you — run it, point it at your Muse Dash folder, and install. (Or " +
                "get the installer from the MelonLoader releases link below.)",
            "4. Launch Muse Dash ONCE with MelonLoader installed and wait until you reach the " +
                "title screen, then exit. This creates the /Mods/ folder.",
            "5. Install the ArchipelagoMuseDash mod: Install on the Play tab downloads the mod " +
                "and extracts it directly into your Mods folder (all files must be directly under " +
                "/Mods/, not in a sub-folder). Or do it by hand from the mod releases (link below).",
            "6. To play: launch Muse Dash, click the button at the bottom-right, and enter your " +
                "server address:port, username, and password.",
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
            ("ArchipelagoMuseDash (GitHub) ↗", ModRepoUrl),
            ("Muse Dash Setup Guide ↗",        SetupGuideUrl),
            ("Muse Dash Guide (AP) ↗",         GameInfoUrl),
            ("MelonLoader (releases) ↗",       MelonLoaderSite),
            (".NET Framework 4.8 ↗",           DotNetFramework48Url),
            (".NET Desktop Runtime 6.0 ↗",     DotNetRuntime6Url),
            ("Archipelago Official ↗",         ArchipelagoSite),
        })
        {
            var btn = new Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
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

    /// "v1.5.33" → "1.5.33" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the mod zip download URL. Prefers
    /// the "ArchipelagoMuseDash.zip" asset (the mod), NOT the musedash.apworld
    /// sidecar asset. Falls back to the pinned 1.5.33 direct URL when the API is
    /// unreachable.
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
                string? preferred = null;   // the mod zip (ArchipelagoMuseDash*.zip)
                string? anyZip    = null;   // any .zip
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;     // skip .apworld

                    anyZip ??= url;
                    if (preferred == null && lower.Contains("archipelago") && lower.Contains("musedash"))
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

    // ── Private helpers — Steam / Muse Dash detection ─────────────────────────

    /// The Muse Dash install dir to use: the override (if set and valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolveMuseDashDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeMuseDashDir(ov))
            return ov;

        try { return DetectSteamMuseDashDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Muse Dash if it has MuseDash.exe and/or the
    /// MuseDash_Data folder.
    private static bool LooksLikeMuseDashDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, MuseDashExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, "MuseDash_Data"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// True when MelonLoader appears installed in a Muse Dash folder. MelonLoader
    /// (IL2CPP) drops a "MelonLoader" folder plus a version.dll proxy at the game
    /// root; the /Mods/ folder appears after the first modded launch.
    private static bool MelonLoaderPresent(string museDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(museDir) || !Directory.Exists(museDir)) return false;
            if (Directory.Exists(Path.Combine(museDir, "MelonLoader"))) return true;
            if (File.Exists(Path.Combine(museDir, "version.dll"))) return true;
            // MelonLoader 0.6 ships a Game executable proxy via dobby/dInput8 too.
            if (File.Exists(Path.Combine(museDir, "dobby.dll"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Muse Dash install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_774171.acf exists → steamapps\common\Muse Dash.
    private static string? DetectSteamMuseDashDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{MuseDashSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    // Prefer the installdir named in the manifest; fall back to the
                    // conventional "Muse Dash" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeMuseDashDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeMuseDashDir(conventional)) return conventional;
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

    /// Find ArchipelagoMuseDash.dll under the detected/override Muse Dash install's
    /// Mods tree (recursive, case-insensitive). Returns the dll path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? muse = ResolveMuseDashDir();
            if (muse == null) return null;
            string modsDir = Path.Combine(muse, "Mods");
            if (!Directory.Exists(modsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(modsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(dll);
                if (name.Equals(ModPrimaryDll, StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Muse Dash: prefer the exe in the detected/override install; if that
    /// cannot be found but Steam is present, fall back to the steam:// URL. Surfaces
    /// a clear message rather than failing opaquely.
    private void StartMuseDash()
    {
        string? muse = ResolveMuseDashDir();
        string? exe  = muse != null ? Path.Combine(muse, MuseDashExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = muse!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Muse Dash.");

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
            "Could not find MuseDash.exe. Open this game's Settings and pick your Muse " +
            "Dash install folder, or install Muse Dash via Steam.",
            MuseDashExeName);
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    /// Download the mod's ArchipelagoMuseDash.zip and extract its contents FLAT into
    /// <MuseDash>/Mods/. The setup guide requires the files to sit directly under
    /// /Mods/ (NOT in a sub-folder), so if the zip nests everything inside a single
    /// wrapper directory we flatten it. A stale copy of the mod DLL is overwritten,
    /// so an update is clean.
    private async Task DownloadAndExtractModFlatAsync(
        string zipUrl,
        string version,
        string modsDir,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"musedash-archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"musedash-archipelago-x-{version}-{Guid.NewGuid():N}");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 7 / 10;
            await DownloadFileAsync(zipUrl, tempZip,
                $"Downloading ArchipelagoMuseDash {version}...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Installing the mod into your Muse Dash\\Mods folder..."));
            Directory.CreateDirectory(modsDir);
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // Determine the source root: the folder that directly contains the mod
            // DLL (handles a zip that wraps everything in a single sub-folder). If
            // the DLL is not found by name, fall back to the extract root so loose
            // files still land under /Mods/.
            string srcRoot = FindFlatSourceRoot(tempExtract) ?? tempExtract;

            // Copy every file from the source root FLAT-PRESERVING into /Mods/. We
            // preserve any intentional sub-structure relative to srcRoot (the AP mod
            // is flat, so in practice files land directly under /Mods/), per the
            // guide's "all files under /Mods/" requirement.
            CopyDirectory(srcRoot, modsDir);

            progress.Report((pctEnd, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Find the directory inside an extracted tree that directly contains the mod's
    /// primary DLL (so a single-wrapper-folder zip is flattened correctly). Returns
    /// null when the DLL is not present by name.
    private static string? FindFlatSourceRoot(string root)
    {
        try
        {
            foreach (string dll in Directory.EnumerateFiles(root, ModPrimaryDll, SearchOption.AllDirectories))
                return Path.GetDirectoryName(dll);
        }
        catch { /* ignore */ }
        return null;
    }

    /// Best-effort download of the MelonLoader installer into this plugin's working
    /// folder. Returns the saved path, or null if the download failed (the caller
    /// then guides the user to the MelonLoader releases page). MelonLoader is NOT
    /// silently installed — it ships a wizard the user runs against their game.
    private async Task<string?> TryDownloadMelonLoaderInstallerAsync(
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        try
        {
            Directory.CreateDirectory(GameDirectory);
            string dest = Path.Combine(GameDirectory, MelonLoaderInstallerName);
            await DownloadFileAsync(MelonLoaderInstallerUrl, dest,
                $"Downloading MelonLoader installer {MelonLoaderVersion}...", pctStart, pctEnd, progress, ct);
            return File.Exists(dest) ? dest : null;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Non-fatal: the mod can still be staged; the user installs MelonLoader
            // from the releases page (link surfaced in the completion note + panel).
            return null;
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

    /// Recursively copy a directory tree (creating the destination).
    private static void CopyDirectory(string sourceDir, string destDir)
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
    // UTF-8, read-modify-write (same approach as Hollow Knight / TUNIC / Stardew /
    // Jak).

    private sealed class MuseDashSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private MuseDashSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<MuseDashSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(MuseDashSettings s)
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
