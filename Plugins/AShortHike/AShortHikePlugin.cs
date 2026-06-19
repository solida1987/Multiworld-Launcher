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

namespace LauncherV2.Plugins.AShortHike;

// ═══════════════════════════════════════════════════════════════════════════════
// AShortHikePlugin — install / launch for "A Short Hike" (adamgryu, 2019) played
// through the AShortHike.Randomizer mod by BrandenEK, which contains the in-game
// Archipelago client. This is a NATIVE "ConnectsItself" integration in the same
// family as the shipped Blasphemous / Hollow Knight / TUNIC / Stardew Valley / Jak
// plugins: the game itself speaks to the AP server (no emulator, no Lua bridge, no
// launcher-held ApClient on the slot).
//
// A Short Hike runs on BrandenEK's "Modding API" — the SAME modding ecosystem as
// the shipped Blasphemous plugin (the Blasphemous Modding API). So this plugin is
// modelled directly on BlasphemousPlugin.cs: identical "Modding" folder layout
// (Modding\plugins\<mod>.dll + Modding\data\...), identical Mod-Installer-driven
// dependency install, identical in-game connection prompt. The differences are only
// the names: the game (A Short Hike, Steam appid 1055540, exe AShortHike.exe), the
// mod (AShortHike.Randomizer → Randomizer.zip → Modding\plugins\Randomizer.dll),
// the installer (Short Hike Mod Installer → ShortHikeModInstaller.exe), and that
// the Modding API is the ONLY required dependency.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy of
// A Short Hike (Steam appid 1055540 — verified against the Steam store page; also on
// itch.io), and Archipelago support is delivered as a mod added on top via
// BrandenEK's Modding API. The honest integration ceiling — exactly like the shipped
// Blasphemous / Hollow Knight plugins — is "automate what is possible, guide the
// irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "A Short Hike" (verified live 2026-06-14 against
//     worlds/shorthike/__init__.py: `class ShortHikeWorld(World): ...
//     game = "A Short Hike"`; required_client_version = (0, 4, 4)). GameId here =
//     "shorthike". A Short Hike is a CORE Archipelago world (Core-verified, added in
//     AP 0.4.5 — it ships inside Archipelago itself, no custom_worlds drop needed to
//     generate).
//
//   * THE MOD repo is BrandenEK/AShortHike.Randomizer (verified live 2026-06-14
//     against the official AP "A Short Hike" setup guide + the repo's docs/README).
//     The README states the required dependency is the "Modding API"
//     (BrandenEK/AShortHike.ModdingAPI). The latest release verified live is tag
//     1.5.1, a SINGLE asset "Randomizer.zip" (~88 KB). Its contents were inspected
//     this session and are laid out per the Modding-API convention:
//         plugins/Randomizer.dll                       (the mod)
//         data/Archipelago.MultiClient.Net.dll         (AP network client, bundled)
//         data/Randomizer/locations.json, ap-item.png  (mod data)
//     These extract into the game's <A Short Hike>/Modding/ directory — giving the
//     verified mod DLL path  <A Short Hike>/Modding/plugins/Randomizer.dll.
//
//   * CRITICAL HONESTY — THE ZIP IS NOT SELF-SUFFICIENT, AND THE MODDING API IS A
//     SEPARATE PREREQUISITE. The Randomizer release zip contains ONLY the Randomizer
//     mod itself (plus the bundled AP network DLL). It does NOT contain the A Short
//     Hike Modding API (the loader that makes any mod load at all). Therefore the
//     OFFICIAL and RECOMMENDED install route is BrandenEK's "Short Hike Mod
//     Installer" (latest v1.8.0, asset ShortHikeModInstaller.zip → run
//     ShortHikeModInstaller.exe) — a small Windows tool that, after you point it at
//     your AShortHike.exe, installs the Modding API + the Randomizer mod for you.
//     The direct-zip drop this plugin can perform is a PARTIAL, best-effort fallback
//     that still needs the Modding API present. The plugin says exactly this and
//     leads the user to the Mod Installer first — faking a one-click "fully
//     installed" that cannot exist would be dishonest theatre.
//
//   * CONNECTION is made IN-GAME (verified against the official AP setup guide and
//     the mod README): after the mods are installed, launch the game and START A NEW
//     GAME (or continue a previous one) — "you will be prompted to enter the
//     archipelago connection details" in a popup menu: Server Address, Port, Name,
//     and an optional Password, then hit connect. There is NO command-line arg and
//     NO config file this launcher can pre-write for the connection (the connection
//     menu is driven from the in-game new-game flow). Per an honest "don't invent an
//     undocumented prefill" stance (same as Blasphemous / Hollow Knight / Jak), this
//     plugin does NOT write any file or fake a connection prefill — the settings
//     panel + post-install note surface the session's host / port / slot for the
//     user to type into the in-game menu.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam A Short Hike install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\A Short Hike via appmanifest_1055540.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and takes
//      precedence; it is validated (must contain AShortHike.exe / A Short Hike_Data)
//      and persisted in this plugin's OWN sidecar
//      (Games/ROMs/shorthike/shorthike_launcher.json) — Core/SettingsStore is NOT
//      modified. (itch.io / other stores work via the manual picker.)
//   2. INSTALL/UPDATE (best effort) = download the mod's "Randomizer.zip" from the
//      real release and extract it into <A Short Hike>/Modding/ (so the DLL lands at
//      Modding/plugins/Randomizer.dll). Because the zip does NOT carry the Modding
//      API, the plugin ALSO presents clear, numbered, Blasphemous-style guided steps
//      + links (the Short Hike Mod Installer — the recommended route, the mod repo,
//      the Modding API, the official AP setup guide, the PopTracker, archipelago.gg)
//      so the user can complete the Modding-API install via the Mod Installer. Never
//      a fake one-click.
//   3. LAUNCH = run AShortHike.exe from the detected/override install; if the exe
//      cannot be found but Steam is present, fall back to steam://rungameid/1055540.
//      ConnectsItself = true (the mod owns the slot — the launcher must NOT hold its
//      own ApClient on it). SupportsStandalone = true (plain A Short Hike runs fine
//      without AP). No connection prefill (entered in-game), stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Blasphemous/Jak-style) ────
//   * "Installed" is judged by the presence of Randomizer.dll under a detected/
//     override A Short Hike install's Modding tree (case-insensitive, recursive) —
//     NOT by an OUR-OWN version stamp, because the user may instead install the mod
//     via the Mod Installer (the recommended route), which this launcher should
//     honor. If no A Short Hike install is detected, the tile simply reads "not
//     installed".
//   * Steam library parsing is defensive: a hand-written tolerant VDF scan that
//     pulls quoted "path" values; any failure degrades to "A Short Hike not found"
//     rather than throwing.
//   * No plaintext AP password is ever written by this plugin (the connection is
//     entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class AShortHikePlugin : IGamePlugin
{
    // ── Constants — the AShortHike.Randomizer mod (real repo, verified 2026-06-14) ─
    private const string MOD_OWNER = "BrandenEK";
    private const string MOD_REPO  = "AShortHike.Randomizer";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // The Short Hike Mod Installer — the RECOMMENDED installer. After the user points
    // it at their AShortHike.exe it installs the Modding API + the Randomizer mod
    // (the dependency the release zip does not carry). Latest release 1.8.0, asset
    // ShortHikeModInstaller.zip → run ShortHikeModInstaller.exe.
    private const string ModInstallerUrl =
        "https://github.com/BrandenEK/AShortHike.Modding.Installer";
    private const string ModInstallerReleasesUrl =
        "https://github.com/BrandenEK/AShortHike.Modding.Installer/releases";

    // The Modding API — the loader every A Short Hike mod needs.
    private const string ModdingApiUrl = "https://github.com/BrandenEK/AShortHike.ModdingAPI";

    // Optional in-game tracker (PopTracker pack).
    private const string PopTrackerUrl =
        "https://github.com/chandler05/shorthike-archipelago-poptracker/releases";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/A%20Short%20Hike/setup_en";
    private const string GameInfoUrl     = "https://archipelago.gg/games/A%20Short%20Hike/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — A Short Hike appid 1055540 (verified against the Steam store page).
    private const string ShAppId = "1055540";
    private static readonly string SteamRunUrl = $"steam://rungameid/{ShAppId}";

    /// The standard Steam install sub-folder name for A Short Hike.
    private const string SteamCommonFolderName = "A Short Hike";

    /// The base-game executable name (Unity; verified — the Windows exe is
    /// AShortHike.exe, with the "A Short Hike_Data" folder beside it).
    private const string ShExeName = "AShortHike.exe";

    /// The mod's primary DLL, placed under <A Short Hike>/Modding/plugins (verified
    /// against the 1.5.1 release-zip contents: plugins/Randomizer.dll).
    private const string ModPrimaryDll = "Randomizer.dll";

    /// Pinned fallback for the mod when the GitHub API is unreachable. 1.5.1 verified
    /// live 2026-06-14; the single asset is "Randomizer.zip".
    private const string FallbackVersion = "1.5.1";
    private const string FallbackZipName = "Randomizer.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "shorthike";
    public string DisplayName => "A Short Hike";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/shorthike/__init__.py
    /// (`class ShortHikeWorld(World): ... game = "A Short Hike"`;
    /// required_client_version = (0, 4, 4)).
    public string ApWorldName => "A Short Hike";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "shorthike.png");

    public string ThemeAccentColor => "#3FB6C9";   // Hawk Peak sky-teal
    public string[] GameBadges     => new[] { "Requires A Short Hike on Steam" };

    public string Description =>
        "A Short Hike, adamgryu's 2019 cosy exploration game, played through the " +
        "AShortHike.Randomizer mod by BrandenEK — which bundles an in-game " +
        "Archipelago client, so the game connects to the multiworld itself with no " +
        "emulator and no bridge. Items from chests, the ground and NPCs are shuffled " +
        "across the multiworld, replaced by chests that may hold items from other " +
        "worlds (an Archipelago logo appears next to anything you receive). You bring " +
        "your own copy of A Short Hike (owned on Steam, or itch.io); the integration " +
        "runs on BrandenEK's Modding API. The launcher detects your Steam install and " +
        "can stage the Randomizer mod files into it, but the Modding API is best " +
        "installed in one step with the Short Hike Mod Installer, and the launcher " +
        "guides the rest. You connect to your server in-game when you start a new game.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the Randomizer mod DLL is present in a detected/override
    /// A Short Hike install's Modding tree. (We do NOT gate on our own stamp — the
    /// user may have installed the mod via the Mod Installer, the recommended route,
    /// which we honor.)
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads (the mod zip) and any working files. The
    /// actual mod is extracted INTO the A Short Hike install's Modding folder, not
    /// here. Exposed as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "AShortHike");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Blasphemous / Hollow
    /// Knight / TUNIC / Jak). Per the brief, lives under Games/ROMs/shorthike/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "shorthike_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The AShortHike.Randomizer mod reports checks/items/goal to the AP server
    // itself — the launcher relays nothing. These exist for interface compatibility
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
        // 0. We need an A Short Hike install to drop the mod into. Prefer an explicit
        //    override; else auto-detect the Steam install.
        progress.Report((2, "Locating your A Short Hike installation..."));
        string? shDir = ResolveShortHikeDir();
        if (shDir == null)
            throw new InvalidOperationException(
                "Could not find an A Short Hike installation. Open this game's Settings " +
                "and pick your A Short Hike folder (the one containing AShortHike.exe), " +
                "or install A Short Hike via Steam first. The Archipelago mod is added " +
                "on top of your own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback).
        progress.Report((6, "Checking the latest A Short Hike Randomizer release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the A Short Hike Randomizer mod download on GitHub. " +
                "Check your internet connection, or install the mod with the Short Hike " +
                "Mod Installer (recommended) from " + ModInstallerUrl + " — see Settings " +
                "for the guided steps. The mod repo is " + ModRepoUrl + ".");

        // 2. Download + extract the mod zip INTO <A Short Hike>/Modding/.
        //    HONEST: this stages the Randomizer mod only. The A Short Hike Modding API
        //    (the loader) is NOT in this zip and must be provided by the Short Hike
        //    Mod Installer.
        string moddingDir = Path.Combine(shDir, "Modding");
        await DownloadAndExtractModAsync(zipUrl, version, moddingDir, progress, ct);

        // 3. Stamp the version next to our sidecar so the tile can show it. (This is
        //    informational only — IsInstalled is judged by the DLL's presence.)
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool dllOk = FindInstalledModDll() != null;
        progress.Report((100,
            $"Staged the A Short Hike Randomizer mod {version} into your Modding\\plugins folder" +
            (dllOk ? "." : " (verify the files landed).") +
            " IMPORTANT: this mod needs the A Short Hike Modding API (the mod loader) " +
            "that this download does NOT include. The recommended way to finish is the " +
            "Short Hike Mod Installer (point it at your AShortHike.exe and it installs " +
            "the Modding API + the Randomizer mod) — open Settings for the guided steps " +
            "and links. To play: launch A Short Hike, START A NEW GAME, and enter your " +
            "server details (Server Address, Port, Name, optional Password) in the " +
            "in-game popup, then connect. (This launcher cannot pre-fill the connection " +
            "— it is entered in-game.)"));
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
        // HONEST: the AP server connection for A Short Hike is entered in the IN-GAME
        // popup that opens when you start a NEW game (Server Address, Port, Name, plus
        // an optional Password). There is no documented command-line / config prefill
        // this launcher can apply (verified — see header). So launching from this tile
        // just starts the game; the user connects in-game with the session
        // credentials (the settings panel + note surface those values to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartShortHike();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) A Short Hike runs perfectly well.
    public bool SupportsStandalone => true;

    /// The AShortHike.Randomizer mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartShortHike();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started A Short Hike from here. Kill what we launched.
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
        // The AShortHike.Randomizer mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid A Short Hike folder contains
    /// AShortHike.exe (and the "A Short Hike_Data" folder is next to it). Return null
    /// when acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your A Short Hike install folder.";

        if (LooksLikeShortHikeDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeShortHikeDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like an A Short Hike installation. Pick the folder " +
               "that contains AShortHike.exe (for Steam this is usually " +
               @"...\steamapps\common\A Short Hike).";
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
            Text = "A Short Hike is your own game (Steam / itch.io) with the " +
                   "AShortHike.Randomizer mod added on top via BrandenEK's Modding API. " +
                   "The launcher detects your Steam install and can stage the Randomizer " +
                   "mod files into it, but the Modding API (the mod loader) is best " +
                   "installed in one step with the Short Hike Mod Installer (see the " +
                   "guided steps below). You connect to your server in-game when you " +
                   "start a new game. These external steps are not verified by this " +
                   "launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "A SHORT HIKE INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? shDir       = ResolveShortHikeDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = shDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + shDir
                : "Detected Steam install: " + shDir)
            : "A Short Hike not detected. Pick your install folder below, or install " +
              "A Short Hike via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = shDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // mod status line
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                    ? "A Short Hike Randomizer mod found: " + modDll
                    : "A Short Hike Randomizer mod not found in Modding\\plugins yet (use " +
                      "Install on the Play tab, or install it via the Mod Installer — " +
                      "recommended).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? shDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your A Short Hike install folder (the one containing " +
                          "AShortHike.exe). Detected from Steam automatically; set it " +
                          "here to override (itch.io, or a non-standard Steam library).",
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
                Title            = "Select your A Short Hike install folder (contains AShortHike.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? shDir ?? "")
                                   ? (overrideDir ?? shDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not an A Short Hike folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeShortHikeDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeShortHikeDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 1055540). Use this " +
                   "picker for itch.io, or a non-standard Steam library.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
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
            Text = "Launch A Short Hike and START A NEW GAME (or continue a previous one) " +
                   "— a popup opens for your connection details. Enter the Server Address " +
                   "and Port (e.g. archipelago.gg and 38281), your Name (slot name), and " +
                   "the Password if the server has one, then hit connect. Press G " +
                   "(keyboard) or LB/RB + X (controller) in-game to see your current " +
                   "objective. This launcher does not pre-fill the connection.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP (recommended: Short Hike Mod Installer)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own A Short Hike (on Steam, or itch.io). Install it if you have not. Use the picker " +
                "above if it was not detected.",
            "2. Download the Short Hike Mod Installer (link below), unzip it, and run " +
                "ShortHikeModInstaller.exe. Point it at your AShortHike.exe (or game folder) when " +
                "prompted.",
            "3. In the Mod Installer, install \"Randomizer\" — it pulls in the Modding API (the mod " +
                "loader) automatically. (Optional: also install the PopTracker pack as an in-game " +
                "tracker — link below.)",
            "4. Alternative (advanced): the Install button on the Play tab stages the Randomizer " +
                "mod's own files into your Modding\\plugins folder, but it does NOT include the " +
                "Modding API — you would still need that from the Mod Installer.",
            "5. Launch A Short Hike and confirm the mods loaded.",
            "6. To play: START A NEW GAME, then enter your Server Address, Port, Name, and optional " +
                "Password into the in-game popup and connect. (This launcher cannot pre-fill the " +
                "connection — it is entered in-game.)",
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
            ("Short Hike Mod Installer (download) ↗", ModInstallerReleasesUrl),
            ("A Short Hike Randomizer (GitHub) ↗",    ModRepoUrl),
            ("A Short Hike Modding API (GitHub) ↗",   ModdingApiUrl),
            ("PopTracker pack (tracker) ↗",           PopTrackerUrl),
            ("A Short Hike Setup Guide ↗",            SetupGuideUrl),
            ("A Short Hike Guide (AP) ↗",             GameInfoUrl),
            ("Archipelago Official ↗",                ArchipelagoSite),
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

    /// "v1.5.1" → "1.5.1" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the mod zip download URL. Prefers the
    /// "Randomizer.zip" asset; falls back to the first .zip; falls back to the pinned
    /// 1.5.1 direct URL when the API is unreachable.
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
                string? preferred = null;   // the mod zip (Randomizer*.zip)
                string? anyZip    = null;   // any .zip
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null && lower.Contains("randomizer"))
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

    // ── Private helpers — Steam / A Short Hike detection ──────────────────────

    /// The A Short Hike install dir to use: the override (if set and valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolveShortHikeDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeShortHikeDir(ov))
            return ov;

        try { return DetectSteamShortHikeDir(); }
        catch { return null; }
    }

    /// A folder "looks like" A Short Hike if it has AShortHike.exe and/or the
    /// "A Short Hike_Data" folder (the Unity data folder).
    private static bool LooksLikeShortHikeDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, ShExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, "A Short Hike_Data"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam A Short Hike install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_1055540.acf exists → steamapps\common\A Short Hike.
    private static string? DetectSteamShortHikeDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{ShAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    // Prefer the installdir named in the manifest; fall back to the
                    // conventional "A Short Hike" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeShortHikeDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeShortHikeDir(conventional)) return conventional;
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

    /// Read the "installdir" value from an appmanifest_*.acf (same quoted-pair format
    /// as VDF). Returns null if absent.
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

    /// Find Randomizer.dll under the detected/override A Short Hike install's Modding
    /// tree (recursive, case-insensitive). Returns the dll path or null. Matched only
    /// within Modding\ so an unrelated "Randomizer.dll" elsewhere is never counted.
    private string? FindInstalledModDll()
    {
        try
        {
            string? sh = ResolveShortHikeDir();
            if (sh == null) return null;
            string moddingDir = Path.Combine(sh, "Modding");
            if (!Directory.Exists(moddingDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(moddingDir, "*.dll", SearchOption.AllDirectories))
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

    /// Start A Short Hike: prefer the exe in the detected/override install; if that
    /// cannot be found but Steam is present, fall back to the steam:// URL. Surfaces a
    /// clear message rather than failing opaquely.
    private void StartShortHike()
    {
        string? sh  = ResolveShortHikeDir();
        string? exe = sh != null ? Path.Combine(sh, ShExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = sh!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start A Short Hike.");

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
            "Could not find AShortHike.exe. Open this game's Settings and pick your " +
            "A Short Hike install folder, or install A Short Hike via Steam.",
            ShExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod's Randomizer.zip and extract it into <A Short Hike>/Modding/.
    /// The zip is laid out per the Modding-API convention (top-level "plugins" +
    /// "data" folders — verified against the 1.5.1 contents), so it extracts straight
    /// into the Modding directory — landing the DLL at Modding\plugins\Randomizer.dll.
    /// If the zip turns out to wrap everything in a single sub-folder, that wrapper is
    /// flattened. Existing files are overwritten but siblings are preserved (so other
    /// mods under Modding\... installed by the Mod Installer are not disturbed).
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string moddingDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"shorthike-randomizer-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"shorthike-randomizer-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading A Short Hike Randomizer {version}..."));
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
                        progress.Report((pct, $"Downloading A Short Hike Randomizer... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Installing the mod into your Modding folder..."));
            Directory.CreateDirectory(moddingDir);
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // The expected layout has "plugins" (and "data") at the extract root. If
            // instead the whole thing is wrapped in a single folder, descend into it
            // so we merge the real "plugins" folder.
            string mergeRoot = tempExtract;
            if (!Directory.Exists(Path.Combine(mergeRoot, "plugins")))
            {
                string[] subdirs = Directory.GetDirectories(mergeRoot);
                string[] files   = Directory.GetFiles(mergeRoot);
                if (subdirs.Length == 1 && files.Length == 0 &&
                    Directory.Exists(Path.Combine(subdirs[0], "plugins")))
                {
                    mergeRoot = subdirs[0];
                }
            }

            // Merge the extracted tree INTO the existing Modding folder (do not wipe it
            // — the Mod Installer's other mods live alongside under the same plugins/
            // data folders).
            MergeDirectory(mergeRoot, moddingDir);

            progress.Report((90, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Recursively copy a directory tree INTO an existing destination, overwriting
    /// individual files but preserving any sibling files already there (so other mods
    /// under Modding\plugins\... are not disturbed).
    private static void MergeDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            try { File.Copy(file, dst, overwrite: true); }
            catch { /* a locked file (game open?) — skip; user can retry with game closed */ }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the install-dir override + an
    // informational version stamp) in its OWN JSON file so it stays a single
    // self-contained source file and does not modify Core/SettingsStore. BOM-less
    // UTF-8, read-modify-write (same approach as Blasphemous / Hollow Knight / Jak).

    private sealed class ShSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private ShSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<ShSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(ShSettings s)
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
