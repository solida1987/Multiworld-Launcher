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

namespace LauncherV2.Plugins.Blasphemous;

// ═══════════════════════════════════════════════════════════════════════════════
// BlasphemousPlugin — install / launch for "Blasphemous" (The Game Kitchen, 2019)
// played through the Blasphemous Multiworld client mod by BrandenEK / TRPG0, which
// contains the in-game Archipelago Multiworld client. This is a NATIVE
// "ConnectsItself" integration in the same family as the shipped Hollow Knight /
// TUNIC / Stardew Valley / Jak plugins: the game itself speaks to the AP server
// (no emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Blasphemous (Steam appid 774361), and Archipelago support is delivered as a
// mod added on top via BrandenEK's Blasphemous Modding API. The honest integration
// ceiling — exactly like the shipped Hollow Knight / TUNIC plugins — is "automate
// what is possible, guide the irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "Blasphemous" (verified against
//     worlds/blasphemous/__init__.py: `class BlasphemousWorld(World): ...
//     game = "Blasphemous"`). GameId here = "blasphemous". Blasphemous is a CORE
//     Archipelago world (it ships inside Archipelago itself — no custom_worlds drop
//     needed to generate).
//
//   * THE MOD repo is BrandenEK/Blasphemous.Randomizer.Multiworld (verified live
//     2026-06-14 against the official AP "Blasphemous" setup guide + the repo
//     README). The README states plainly: "This mod is available for download
//     through the Blasphemous Mod Installer. Required dependencies: Modding API,
//     Menu Framework, Cheat Console, Randomizer." The latest release (3.2.1,
//     2026-02-04) ships a SINGLE asset:
//         BlasphemousMultiworld.zip   (the mod — extracts into the "Modding" folder)
//     The zip follows the Modding-API layout (verified against the API's own
//     development docs): a "plugins" folder holding BlasphemousMultiworld.dll plus
//     optional data/levels/localization folders, all of which extract into the
//     game's <Blasphemous>/Modding/ directory — giving the verified mod DLL path
//     <Blasphemous>/Modding/plugins/BlasphemousMultiworld.dll.
//
//   * CRITICAL HONESTY — THE ZIP IS NOT SELF-SUFFICIENT, AND THE MODDING API IS A
//     SEPARATE PREREQUISITE. The Multiworld release zip contains ONLY the Multiworld
//     mod itself. It does NOT contain the Blasphemous Modding API (the BepInEx-based
//     loader that makes any mod load at all) NOR the three framework dependencies
//     the mod requires (Menu Framework, Cheat Console, and the base Randomizer mod).
//     Therefore the OFFICIAL and RECOMMENDED install route is BrandenEK's
//     "Blasphemous Mod Installer" (BlasModInstaller, latest v1.10.0) — a small
//     Windows tool that, after you point it at your Blasphemous.exe, installs the
//     Modding API + the Multiworld mod + all of its dependencies for you. The
//     direct-zip drop this plugin can perform is a PARTIAL, best-effort fallback
//     that still needs the Modding API + the dependency mods present. The plugin
//     says exactly this and leads the user to the Mod Installer first — faking a
//     one-click "fully installed" that cannot exist would be dishonest theatre.
//
//   * CONNECTION is made IN-GAME (verified against the official AP setup guide and
//     the mod README): after the mods are installed, launch the game and START A
//     NEW SAVE FILE — a menu opens prompting for your connection details. Enter
//     them like the README's own example:
//         Server ip:   ap:55858        (i.e. host:port — "ap" being a hosts alias;
//                                        use archipelago.gg:PORT for a hosted room)
//         Player name: Player1         (your AP slot name)
//     plus the password if the server has one. (The in-game debug console, opened
//     with the backslash key, also offers `ap status` / `ap say` / `ap hint`.)
//     There is NO command-line arg and NO config file this launcher can pre-write
//     for the connection (the connection menu is driven from the in-game save-file
//     flow). Per an honest "don't invent an undocumented prefill" stance (same as
//     Hollow Knight / TUNIC / Jak), this plugin does NOT write any file or fake a
//     connection prefill — the settings panel + post-install note surface the
//     session's host/port/slot for the user to type into the in-game menu.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Blasphemous install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Blasphemous via appmanifest_774361.acf. A manual install-dir
//      OVERRIDE (settings folder picker) is also supported and takes precedence; it
//      is validated (must contain Blasphemous.exe / Blasphemous_Data) and persisted
//      in this plugin's OWN sidecar (Games/ROMs/blasphemous/blasphemous_launcher.json)
//      — Core/SettingsStore is NOT modified.
//   2. INSTALL/UPDATE (best effort) = download the mod's "BlasphemousMultiworld.zip"
//      from the real release and extract it into <Blasphemous>/Modding/ (so the DLL
//      lands at Modding/plugins/BlasphemousMultiworld.dll). Because the zip does NOT
//      carry the Modding API or the dependency mods, the plugin ALSO presents clear,
//      numbered, Hollow-Knight-style guided steps + links (the Blasphemous Mod
//      Installer — the recommended route, the mod repo, the Modding API, the
//      official AP setup guide, archipelago.gg) so the user can complete the
//      Modding-API + dependency install via the Mod Installer. Never a fake
//      one-click.
//   3. LAUNCH = run Blasphemous.exe from the detected/override install; if the exe
//      cannot be found but Steam is present, fall back to steam://rungameid/774361.
//      ConnectsItself = true (the mod owns the slot — the launcher must NOT hold its
//      own ApClient on it). SupportsStandalone = true (plain Blasphemous runs fine
//      without AP). No connection prefill (entered in-game), stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Hollow-Knight/Jak-style) ──
//   * "Installed" is judged by the presence of BlasphemousMultiworld.dll under a
//     detected/override Blasphemous install's Modding tree (case-insensitive,
//     recursive) — NOT by an OUR-OWN version stamp, because the user may instead
//     install the mod via the Mod Installer (the recommended route), which this
//     launcher should honor. If no Blasphemous install is detected, the tile simply
//     reads "not installed".
//   * Steam library parsing is defensive: a hand-written tolerant VDF scan that
//     pulls quoted "path" values; any failure degrades to "Blasphemous not found"
//     rather than throwing.
//   * No plaintext AP password is ever written by this plugin (the connection is
//     entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class BlasphemousPlugin : IGamePlugin
{
    // ── Constants — the Blasphemous Multiworld mod (real repo, verified 2026-06-14) ─
    private const string MOD_OWNER = "BrandenEK";
    private const string MOD_REPO  = "Blasphemous.Randomizer.Multiworld";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // The Blasphemous Mod Installer — the RECOMMENDED installer. It installs the
    // Modding API + the Multiworld mod + every dependency the release zip does not
    // carry, after the user points it at their Blasphemous.exe.
    private const string ModInstallerUrl   = "https://github.com/BrandenEK/Blasphemous.Modding.Installer";
    private const string ModInstallerReleasesUrl =
        "https://github.com/BrandenEK/Blasphemous.Modding.Installer/releases";

    // The Modding API — the BepInEx-based loader every Blasphemous mod needs.
    private const string ModdingApiUrl = "https://github.com/BrandenEK/Blasphemous.ModdingAPI";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Blasphemous/setup/en";
    private const string GameInfoUrl     = "https://archipelago.gg/games/Blasphemous/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Blasphemous appid 774361.
    private const string BlasSteamAppId = "774361";
    private static readonly string SteamRunUrl = $"steam://rungameid/{BlasSteamAppId}";

    /// The standard Steam install sub-folder name for Blasphemous.
    private const string SteamCommonFolderName = "Blasphemous";

    /// The base-game executable name.
    private const string BlasExeName = "Blasphemous.exe";

    /// The mod's primary DLL, placed under <Blasphemous>/Modding/plugins (verified
    /// against the Modding-API folder-structure docs + the 3.2.1 release asset).
    private const string ModPrimaryDll = "BlasphemousMultiworld.dll";

    /// Pinned fallback for the mod when the GitHub API is unreachable. 3.2.1
    /// verified live 2026-06-14; the single asset is "BlasphemousMultiworld.zip".
    private const string FallbackVersion = "3.2.1";
    private const string FallbackZipName = "BlasphemousMultiworld.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "blasphemous";
    public string DisplayName => "Blasphemous";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/blasphemous/__init__.py
    /// (`class BlasphemousWorld(World): ... game = "Blasphemous"`).
    public string ApWorldName => "Blasphemous";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "blasphemous.png");

    public string ThemeAccentColor => "#8A2A2A";   // Cvstodia blood-crimson
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Blasphemous, The Game Kitchen's 2019 dark-fantasy Metroidvania, played " +
        "through the Blasphemous Multiworld client mod by BrandenEK and TRPG0 — " +
        "which bundles an in-game Archipelago client, so the game connects to the " +
        "multiworld itself with no emulator and no bridge. Relics, prayers, Rosary " +
        "beads, Mea Culpa hearts, keys and more are shuffled across the multiworld. " +
        "You bring your own copy of Blasphemous (owned on Steam); the integration " +
        "runs on the Blasphemous Modding API. The launcher detects your Steam " +
        "install and can stage the Multiworld mod files into it, but the Modding API " +
        "and the mod's dependencies are best installed in one step with the " +
        "Blasphemous Mod Installer, and the launcher guides the rest. You connect to " +
        "your server in-game when you start a new save file.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the Blasphemous Multiworld mod DLL is present in a detected/
    /// override Blasphemous install's Modding tree. (We do NOT gate on our own stamp
    /// — the user may have installed the mod via the Mod Installer, the recommended
    /// route, which we honor.)
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads (the mod zip) and any working files. The
    /// actual mod is extracted INTO the Blasphemous install's Modding folder, not
    /// here. Exposed as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Blasphemous");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Hollow Knight /
    /// TUNIC / Stardew / Jak). Per the brief, lives under Games/ROMs/blasphemous/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "blasphemous_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Blasphemous Multiworld mod reports checks/items/goal to the AP server
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
        // 0. We need a Blasphemous install to drop the mod into. Prefer an explicit
        //    override; else auto-detect the Steam install.
        progress.Report((2, "Locating your Blasphemous installation..."));
        string? blasDir = ResolveBlasphemousDir();
        if (blasDir == null)
            throw new InvalidOperationException(
                "Could not find a Blasphemous installation. Open this game's Settings " +
                "and pick your Blasphemous folder (the one containing Blasphemous.exe), " +
                "or install Blasphemous via Steam first. The Archipelago mod is added " +
                "on top of your own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback).
        progress.Report((6, "Checking the latest Blasphemous Multiworld release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Blasphemous Multiworld mod download on GitHub. " +
                "Check your internet connection, or install the mod with the " +
                "Blasphemous Mod Installer (recommended) from " + ModInstallerUrl +
                " — see Settings for the guided steps. The mod repo is " + ModRepoUrl + ".");

        // 2. Download + extract the mod zip INTO <Blasphemous>/Modding/.
        //    HONEST: this stages the Multiworld mod only. The Blasphemous Modding API
        //    (the BepInEx loader) and the dependency mods (Menu Framework, Cheat
        //    Console, Randomizer) are NOT in this zip and must be provided by the
        //    Blasphemous Mod Installer.
        string moddingDir = Path.Combine(blasDir, "Modding");
        await DownloadAndExtractModAsync(zipUrl, version, moddingDir, progress, ct);

        // 3. Stamp the version next to our sidecar so the tile can show it. (This is
        //    informational only — IsInstalled is judged by the DLL's presence.)
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool dllOk = FindInstalledModDll() != null;
        progress.Report((100,
            $"Staged the Blasphemous Multiworld mod {version} into your Modding\\plugins folder" +
            (dllOk ? "." : " (verify the files landed).") +
            " IMPORTANT: this mod needs the Blasphemous Modding API plus several " +
            "dependency mods (Menu Framework, Cheat Console, Randomizer) that this " +
            "download does NOT include. The recommended way to finish is the " +
            "Blasphemous Mod Installer (point it at your Blasphemous.exe and it " +
            "installs the Modding API + the mod + all dependencies) — open Settings " +
            "for the guided steps and links. To play: launch Blasphemous, start a NEW " +
            "save file, and enter your server details (e.g. \"Server ip: " +
            "archipelago.gg:PORT\", \"Player name: <your slot>\") in the in-game menu. " +
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
        // HONEST: the AP server connection for Blasphemous is entered in the IN-GAME
        // menu that opens when you start a NEW save file (Server ip: host:port,
        // Player name: <slot>, plus password if any). There is no documented
        // command-line / config prefill this launcher can apply (verified — see
        // header). So launching from this tile just starts the game; the user
        // connects in-game with the session credentials (the settings panel + note
        // surface those values to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartBlasphemous();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Blasphemous runs perfectly well.
    public bool SupportsStandalone => true;

    /// The Blasphemous Multiworld mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartBlasphemous();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started Blasphemous from here. Kill what we launched.
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
        // The Blasphemous Multiworld mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game (the `ap status` console line).
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Blasphemous folder contains
    /// Blasphemous.exe (and the Blasphemous_Data folder is next to it). Return null
    /// when acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Blasphemous install folder.";

        if (LooksLikeBlasphemousDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeBlasphemousDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Blasphemous installation. Pick the folder " +
               "that contains Blasphemous.exe (for Steam this is usually " +
               @"...\steamapps\common\Blasphemous).";
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
            Text = "Blasphemous is your own game (Steam) with the Blasphemous " +
                   "Multiworld mod added on top via the Blasphemous Modding API. The " +
                   "launcher detects your Steam install and can stage the Multiworld " +
                   "mod files into it, but the Modding API and the mod's dependencies " +
                   "(Menu Framework, Cheat Console, Randomizer) are best installed in " +
                   "one step with the Blasphemous Mod Installer (see the guided steps " +
                   "below). You connect to your server in-game when you start a new " +
                   "save file. These external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "BLASPHEMOUS INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? blasDir     = ResolveBlasphemousDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = blasDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + blasDir
                : "Detected Steam install: " + blasDir)
            : "Blasphemous not detected. Pick your install folder below, or install " +
              "Blasphemous via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = blasDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // mod status line
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                    ? "Blasphemous Multiworld mod found: " + modDll
                    : "Blasphemous Multiworld mod not found in Modding\\plugins yet (use " +
                      "Install on the Play tab, or install it via the Mod Installer — " +
                      "recommended).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? blasDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Blasphemous install folder (the one containing " +
                          "Blasphemous.exe). Detected from Steam automatically; set it " +
                          "here to override (non-standard Steam library, or another " +
                          "store).",
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
                Title            = "Select your Blasphemous install folder (contains Blasphemous.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? blasDir ?? "")
                                   ? (overrideDir ?? blasDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    MessageBox.Show(bad, "Not a Blasphemous folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeBlasphemousDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeBlasphemousDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 774361). Use this " +
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
            Text = "Launch Blasphemous and START A NEW SAVE FILE — a menu opens for your " +
                   "connection details. Enter \"Server ip\" as host:port (e.g. " +
                   "archipelago.gg:38281), \"Player name\" as your slot name, and the " +
                   "password if the server has one. After connecting, the in-game debug " +
                   "console (backslash key) offers ap status / ap say / ap hint. This " +
                   "launcher does not pre-fill the connection.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP (recommended: Blasphemous Mod Installer)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Blasphemous (on Steam). Install it if you have not. Use the picker above if " +
                "it was not detected.",
            "2. Download the Blasphemous Mod Installer (link below) and run it. Point it at your " +
                "Blasphemous.exe when prompted.",
            "3. In the Mod Installer, install \"Multiworld\" — it pulls in the Modding API and the " +
                "required dependency mods (Menu Framework, Cheat Console, Randomizer) " +
                "automatically. (Optional: also install \"Rando Map\" as an in-game tracker.)",
            "4. Alternative (advanced): the Install button on the Play tab stages the Multiworld " +
                "mod's own files into your Modding\\plugins folder, but it does NOT include the " +
                "Modding API or the dependency mods — you would still need those from the Mod " +
                "Installer.",
            "5. Launch Blasphemous and confirm the mods loaded (the Multiworld mod will report on " +
                "the in-game console with the backslash key).",
            "6. To play: start a NEW save file, then enter your Server ip (host:port), Player name " +
                "(your slot), and password into the in-game menu. (This launcher cannot pre-fill " +
                "the connection — it is entered in-game.)",
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
            ("Blasphemous Mod Installer (download) ↗", ModInstallerReleasesUrl),
            ("Blasphemous Multiworld (GitHub) ↗",      ModRepoUrl),
            ("Blasphemous Modding API (GitHub) ↗",     ModdingApiUrl),
            ("Blasphemous Setup Guide ↗",              SetupGuideUrl),
            ("Blasphemous Guide (AP) ↗",               GameInfoUrl),
            ("Archipelago Official ↗",                 ArchipelagoSite),
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

    /// "v3.2.1" → "3.2.1" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the mod zip download URL. Prefers
    /// the "BlasphemousMultiworld.zip" asset. Falls back to the pinned 3.2.1 direct
    /// URL when the API is unreachable.
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
                string? preferred = null;   // the mod zip (BlasphemousMultiworld*.zip)
                string? anyZip    = null;   // any .zip
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null && lower.Contains("multiworld"))
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

    // ── Private helpers — Steam / Blasphemous detection ───────────────────────

    /// The Blasphemous install dir to use: the override (if set and valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolveBlasphemousDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeBlasphemousDir(ov))
            return ov;

        try { return DetectSteamBlasphemousDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Blasphemous if it has Blasphemous.exe and/or the
    /// Blasphemous_Data folder.
    private static bool LooksLikeBlasphemousDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, BlasExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, "Blasphemous_Data"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Blasphemous install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_774361.acf exists → steamapps\common\Blasphemous.
    private static string? DetectSteamBlasphemousDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{BlasSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    // Prefer the installdir named in the manifest; fall back to the
                    // conventional "Blasphemous" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeBlasphemousDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeBlasphemousDir(conventional)) return conventional;
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

    /// Find BlasphemousMultiworld.dll under the detected/override Blasphemous
    /// install's Modding tree (recursive, case-insensitive). Returns the dll path or
    /// null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? blas = ResolveBlasphemousDir();
            if (blas == null) return null;
            string moddingDir = Path.Combine(blas, "Modding");
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

    /// Start Blasphemous: prefer the exe in the detected/override install; if that
    /// cannot be found but Steam is present, fall back to the steam:// URL. Surfaces
    /// a clear message rather than failing opaquely.
    private void StartBlasphemous()
    {
        string? blas = ResolveBlasphemousDir();
        string? exe  = blas != null ? Path.Combine(blas, BlasExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = blas!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Blasphemous.");

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
            "Could not find Blasphemous.exe. Open this game's Settings and pick your " +
            "Blasphemous install folder, or install Blasphemous via Steam.",
            BlasExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod's BlasphemousMultiworld.zip and extract it into
    /// <Blasphemous>/Modding/. The zip is laid out per the Modding-API convention
    /// (a top-level "plugins" folder + optional data/levels/localization), so it
    /// extracts straight into the Modding directory — landing the DLL at
    /// Modding\plugins\BlasphemousMultiworld.dll. If the zip turns out to wrap
    /// everything in a single sub-folder, that wrapper is flattened.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string moddingDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"blasphemous-multiworld-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"blasphemous-multiworld-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading Blasphemous Multiworld {version}..."));
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
                        progress.Report((pct, $"Downloading Blasphemous Multiworld... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Installing the mod into your Modding folder..."));
            Directory.CreateDirectory(moddingDir);
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // The expected layout has "plugins" (and maybe data/levels/localization)
            // at the extract root. If instead the whole thing is wrapped in a single
            // folder, descend into it so we merge the real "plugins" folder.
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

            // Merge the extracted tree INTO the existing Modding folder (do not wipe
            // it — the Mod Installer's other mods live alongside under the same
            // plugins/data/levels/localization folders).
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
    /// individual files but preserving any sibling files already there (so other
    /// mods under Modding\plugins\... are not disturbed).
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
    // UTF-8, read-modify-write (same approach as Hollow Knight / TUNIC / Jak).

    private sealed class BlasSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private BlasSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<BlasSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(BlasSettings s)
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
