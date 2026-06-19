using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Overcooked2;

// ═══════════════════════════════════════════════════════════════════════════════
// Overcooked2Plugin — detect / install / launch for "Overcooked! 2" (Team17 /
// Ghost Town Games) played through OC2-Modding, a BepInEx/Harmony runtime mod that
// doubles as the Archipelago MultiWorld client.
//
// This is a NATIVE "ConnectsItself" integration in the SAME FAMILY as the shipped
// Stardew Valley plugin (Steam base game + an Archipelago mod installed into the
// game folder; the modded game speaks to the AP server itself via an in-game
// client — no emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified against the apworld + setup guide) ─
// Overcooked! 2 is a STEAM-MOD native (you own the base game; the AP support is a
// runtime mod). The honest ceiling is "automate what is possible, guide the
// irreducible parts," exactly like Stardew Valley / Jak. The verified facts:
//
//   * THE AP WORLD game string is "Overcooked! 2" — VERIFIED against
//     worlds/overcooked2/__init__.py (`game = "Overcooked! 2"`,
//     required_client_version = (0, 3, 8)). Overcooked! 2 is a CORE Archipelago
//     world (ships inside Archipelago itself — there is NO separate apworld to drop
//     into custom_worlds; only the OC2-Modding client is installed game-side).
//     World id = "overcooked2". Catalog id = "overcooked_2".
//
//   * THE CLIENT is OC2-Modding by toasterparty — repo
//     https://github.com/toasterparty/oc2-modding (the apworld's own
//     bug_report_page and setup-guide author). Per the official AP "Overcooked! 2"
//     setup guide (worlds/overcooked2/docs/setup_en.md), OC2-Modding is "a general
//     purpose modding framework which doubles as an Archipelago MultiWorld Client.
//     It works by using Harmony to inject custom code into the game at runtime, so
//     none of the original game files need to be modified in any way." It is a
//     BepInEx mod (it writes a BepInEx\ tree + BepInEx\config\OC2Modding.cfg into
//     the game folder, with a DisableAllMods toggle to return to vanilla). It is
//     NOT a process-memory injector like The Witness and NOT bundled inside
//     Archipelago like Undertale.
//
//   * DISTRIBUTION: a GitHub release ZIP
//     (https://github.com/toasterparty/oc2-modding/releases) that the user extracts
//     anywhere, then runs "oc2-modding-install.bat", which installs the BepInEx +
//     mod tree INTO the Overcooked! 2 game folder. Uninstall via
//     "oc2-modding-uninstall.bat" in the game folder. The launcher CAN honestly
//     download + extract that release zip into its OWN folder; the actual install
//     into the game folder is performed by the bundled .bat, which is INTERACTIVE
//     (it prompts for the game location), so — exactly like the Stardew plugin
//     guides SMAPI's wizard installer — this plugin GUIDES the .bat step (download +
//     a one-click "Open installer folder" + the detected game path to paste) rather
//     than faking a silent install. (We never silent-run an interactive installer.)
//
//   * HOW IT CONNECTS (verified via the setup guide): the AP server connection is
//     entered IN-GAME on a sign-in screen. From the guide: "When attempting to
//     enter the main menu from the title screen, the game will freeze and prompt
//     you to sign in… Sign-in with server address, username and password of the
//     corresponding room… Otherwise… press 'Continue without Archipelago'." So the
//     modded game OWNS the slot connection; there is NO documented config.json /
//     command-line arg the launcher can pre-write. This plugin therefore does NOT
//     attempt a connection prefill (doing so would be dishonest theatre — same
//     stance as Stardew/Jak). The settings panel + post-install note surface the
//     session's host:port / slot so the user can type them into that screen.
//     ConnectsItself = true (the launcher must NOT hold its own ApClient on the
//     same slot while the modded game is connected). SupportsStandalone = true
//     (plain Overcooked! 2 runs fine; the mod even has a "Continue without
//     Archipelago" button and a DisableAllMods config toggle).
//
//   * Steam appid 728880 (verified — store.steampowered.com/app/728880). The
//     launcher DETECTS the Steam install (registry → libraryfolders.vdf →
//     appmanifest_728880.acf → steamapps\common\Overcooked! 2) and never modifies
//     it (§11) — OC2-Modding's own installer writes the BepInEx tree; the launcher
//     only reads the folder to locate the exe and report mod presence. The setup
//     guide also lists Epic / Steam-beta as supported (and GOG "at your own risk");
//     a manual folder override covers those non-Steam stores.
//
// ── WHAT THIS PLUGIN HONESTLY DOES ────────────────────────────────────────────
//   1. DETECT the Steam Overcooked! 2 install (appid 728880) via the same
//      registry → libraryfolders.vdf → ACF pipeline used by the Stardew/Undertale
//      plugins, with a manual override folder picker persisted in this plugin's OWN
//      JSON sidecar (Games/ROMs/overcooked_2/overcooked2_launcher.json — Core/
//      SettingsStore is NOT modified).
//   2. INSTALL/UPDATE = best-effort download of the OC2-Modding release zip
//      (latest tag, pinned fallback when the API is unreachable) into the launcher's
//      OWN folder (Games/OC2Modding/), then GUIDE the user to run the bundled
//      "oc2-modding-install.bat" and point it at the detected game folder. (We open
//      the extracted folder for them and report the game path to paste.) The mod's
//      install into the game folder is the .bat's job — we do not fake it.
//   3. GUIDED STEPS + LINKS (OC2-Modding GitHub, the official AP setup guide,
//      the Steam store page, archipelago.gg) in a Stardew-style honest settings
//      panel + note.
//   4. LAUNCH = run the game so BepInEx auto-loads OC2-Modding (BepInEx injects via
//      its doorstop next to the exe, so launching the game exe IS the modded entry
//      point). Preference: the detected/override install's Overcooked2.exe, then
//      steam://rungameid/728880. NO connection prefill (none exists — the user
//      signs in on the in-game screen). The note shows the session's server/slot to
//      copy. ConnectsItself = true. SupportsStandalone = true.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Stardew/Jak/SoH-style) ─────
//   * RELEASE ASSET: OC2-Modding ships its release as a .zip containing
//     oc2-modding-install.bat. ResolveLatestModAsync picks the first/most-likely
//     Windows .zip asset (excluding source archives); the pinned fallback targets
//     the repo's /releases page. The exact asset filename was NOT verified
//     byte-for-byte offline, so resolution is fuzzy and the Settings panel links the
//     releases page as the authoritative source.
//   * "Installed" (the tile gate) means OC2-Modding is present IN THE GAME FOLDER —
//     i.e. a BepInEx tree with an OC2Modding config/plugin. Having only the Steam
//     base game (unmodded) does NOT flip it true, because that copy is not set up
//     for AP. The downloaded-but-not-yet-.bat-installed zip in our own folder is
//     reported separately ("downloaded, run the installer next").
//   * The game exe name is assumed "Overcooked2.exe" (the Steam build's exe);
//     resolution falls back to a fuzzy "*overcook*.exe" in the install root.
//   * The launcher cannot verify the user actually ran oc2-modding-install.bat
//     beyond "is there a BepInEx/OC2Modding tree in the folder you pointed me at";
//     the Settings panel is explicit about the remaining manual step.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Overcooked2Plugin : IGamePlugin
{
    // ── Constants — the OC2-Modding client (real repo, verified via the apworld) ─

    private const string MOD_OWNER = "toasterparty";
    private const string MOD_REPO  = "oc2-modding";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";
    private const string GH_MOD_RELEASES_LATEST_URL = $"{GH_MOD_RELEASES_URL}/latest";
    private const string ModReleasesPageUrl = $"{ModRepoUrl}/releases";

    /// Official Archipelago "Overcooked! 2" setup guide.
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Overcooked!%202/setup/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    /// Overcooked! 2 on Steam (the base game the player must own; appid 728880).
    private const string SteamAppId = "728880";
    private const string SteamStoreUrl =
        "https://store.steampowered.com/app/728880/Overcooked_2/";
    private static readonly string SteamRunUrl =
        $"steam://rungameid/{SteamAppId}";

    /// Standard Steam install sub-folder name (steamapps\common\Overcooked! 2).
    private const string SteamCommonFolderName = "Overcooked! 2";

    /// The Overcooked! 2 game exe (the Steam build). Launching it loads the mod
    /// because OC2-Modding installs BepInEx's doorstop next to it.
    private const string PreferredExeName = "Overcooked2.exe";

    /// The bundled installer/uninstaller scripts dropped by an OC2-Modding release.
    private const string InstallBatName   = "oc2-modding-install.bat";
    private const string UninstallBatName = "oc2-modding-uninstall.bat";

    /// Pinned fallback for the OC2-Modding download when the GitHub API is
    /// unreachable. The exact release tag/asset was not verified offline, so the
    /// fallback simply routes the user to the releases page (the API path is the
    /// normal route; this is only the safety net).
    private const string FallbackModVersion = "latest";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(15),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-(download)-version stamp for the zip we fetched into our own folder.
    private const string VersionFileName = "oc2_modding_version.dat";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    /// Matches the catalog entry id so the store row and this plugin line up.
    public string GameId      => "overcooked_2";
    public string DisplayName => "Overcooked! 2";
    public string Subtitle    => "Native PC · Archipelago";

    /// EXACT AP game string — VERIFIED against worlds/overcooked2/__init__.py
    /// (`game = "Overcooked! 2"`). required_client_version = (0, 3, 8).
    public string ApWorldName => "Overcooked! 2";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "overcooked_2.png");

    public string ThemeAccentColor => "#E8542B";   // Onion Kingdom orange
    public string[] GameBadges     => new[] { "Requires Overcooked! 2 on Steam" };

    public string Description =>
        "Overcooked! 2 is the frantically paced co-op cooking game by Team17 and " +
        "Ghost Town Games — race the clock to chop, cook and serve while the kitchen " +
        "falls apart around you. This is the Archipelago integration, which turns the " +
        "Story campaign into a metroidvania: many of the chefs' abilities are removed " +
        "and shuffled into the multiworld, and completing a level for the first time " +
        "sends an item, with stars unlocking your way to the final 6-6 boss kitchen. " +
        "You bring your own copy of Overcooked! 2 (owned on Steam); the integration " +
        "runs as OC2-Modding, a BepInEx mod that doubles as the in-game Archipelago " +
        "client, so the game connects to the multiworld itself — no emulator, no " +
        "bridge. Play solo (one player controlling two chefs) or with up to four " +
        "friends locally or online. The launcher detects your install, downloads the " +
        "mod, and guides the one-time setup; you sign in to your server on the " +
        "in-game Archipelago screen.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means OC2-Modding is present IN THE GAME FOLDER (a BepInEx tree
    /// with an OC2Modding config/plugin). The Steam base game alone is NOT enough —
    /// the mod is the AP gate. (The downloaded-but-not-yet-installed zip in our own
    /// folder is reported separately in the settings panel / install note.)
    public bool IsInstalled => IsModInstalledInGame();

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// The resolved Overcooked! 2 install directory (the folder that contains
    /// Overcooked2.exe). Settable so the launcher core's GameDirectory contract is
    /// honored; backed by detection + the sidecar override. Setting it persists the
    /// override.
    public string GameDirectory
    {
        get => ResolveInstallDir() ?? "";
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
                SaveOverrideInstallDir(value);
        }
    }

    /// This plugin's OWN settings sidecar + bookkeeping tree (kept out of the shared
    /// SettingsStore so the plugin stays a single self-contained file — same as the
    /// Stardew / Undertale plugins). Lives under the ROM-library tree by GameId.
    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "overcooked2_launcher.json");

    /// Where the OC2-Modding release zip is extracted (the launcher's own copy of
    /// the client; the .bat then installs it into the game folder).
    private string ModDownloadDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "OC2Modding");

    /// Where we stamp the downloaded mod version (inside the ROM-library tree so we
    /// never write into the user's game folder for bookkeeping).
    private string VersionFilePath
        => Path.Combine(RomLibraryDirectory, VersionFileName);

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    /// Filename of the mod zip asset seen on the resolved release (so the saved
    /// download keeps the upstream name). null until a release is resolved.
    private string? _modZipFileName;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // OC2-Modding's in-game client reports checks/items/goal to the AP server
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
            InstalledVersion = (IsInstalled && File.Exists(VersionFilePath))
                ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim()
                : (IsInstalled ? "installed" : null);
        }
        catch
        {
            InstalledVersion = null;
        }

        try
        {
            var (version, _, _) = await ResolveLatestModAsync(ct);
            AvailableVersion = version == FallbackModVersion ? null : version;
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
        // 1. Resolve the latest OC2-Modding release (routes to releases page when
        //    offline / asset not resolvable).
        progress.Report((3, "Checking the latest OC2-Modding release..."));
        var (version, zipUrl, zipName) = await ResolveLatestModAsync(ct);
        AvailableVersion = version == FallbackModVersion ? null : version;
        _modZipFileName  = zipName;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the OC2-Modding download on GitHub automatically. " +
                "Download the latest release manually from " + ModReleasesPageUrl +
                ", extract it anywhere, and run \"" + InstallBatName + "\" — point it " +
                "at your Overcooked! 2 game folder. (You also need your own copy of " +
                "Overcooked! 2 on Steam, appid " + SteamAppId + ".)");

        // 2. Download + extract OC2-Modding into the launcher's OWN folder. (The
        //    install INTO the game folder is the bundled .bat's job — interactive,
        //    so we guide it rather than silent-run it.)
        await DownloadAndExtractModAsync(zipUrl, version, progress, ct);

        // 3. Stamp the downloaded version.
        Directory.CreateDirectory(RomLibraryDirectory);
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = IsInstalled ? version : InstalledVersion;

        // 4. Honest closing note — the .bat + connection are not automated.
        string? gameDir   = ResolveInstallDir();
        string? installBat = FindInstallerBat();
        string  where = installBat != null
            ? $"Run \"{InstallBatName}\" in \"{Path.GetDirectoryName(installBat)}\" "
            : $"Extract the download and run \"{InstallBatName}\" ";
        string  target = gameDir != null
            ? $"and point it at your Overcooked! 2 folder (\"{gameDir}\"). "
            : "and point it at your Overcooked! 2 game folder. ";

        progress.Report((100,
            $"OC2-Modding {(version == FallbackModVersion ? "" : version + " ")}downloaded. " +
            where + target +
            "Then launch the game and, on the in-game sign-in screen, enter your " +
            "server (host:port), slot name and password. The launcher cannot pre-fill " +
            "the connection — it is entered in-game. See the Setup Guide in Settings."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── AutoMod-style validation of a user-picked Overcooked! 2 folder (§10) ──

    /// Accept a folder that looks like an Overcooked! 2 install: it must contain the
    /// game exe (Overcooked2.exe, or a fuzzy "*overcook*.exe"). We do NOT require the
    /// mod here — the user may be pointing us at a fresh install before running the
    /// OC2-Modding installer. Returns null when acceptable, else a short reason.
    public string? ValidateExistingInstall(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return "That folder does not exist. Pick your Overcooked! 2 folder.";

            if (FindGameExeIn(folder) != null)
                return null;

            return "That folder does not look like an Overcooked! 2 install (no " +
                   "\"Overcooked2.exe\"). Pick the folder that contains the game exe — " +
                   @"for Steam this is usually ...\steamapps\common\Overcooked! 2.";
        }
        catch
        {
            return "Could not read that folder. Pick a different one and try again.";
        }
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: the AP server connection for this game is entered IN-GAME on the
        // sign-in screen that appears when leaving the title screen (Server address,
        // username/slot, password, or "Continue without Archipelago"). There is NO
        // documented config / CLI prefill we can apply here (verified — see header),
        // so launching from this tile means starting the modded game (BepInEx loads
        // OC2-Modding via its doorstop); the user signs in with the session's
        // server/slot (the settings panel + note surface those values to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while OC2-Modding's in-game client is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartGame();
        return Task.CompletedTask;
    }

    /// Plain Overcooked! 2 runs fine (the mod even has a "Continue without
    /// Archipelago" button and a DisableAllMods config toggle).
    public bool SupportsStandalone => true;

    /// OC2-Modding's in-game client owns the slot connection (see header). The
    /// launcher must NOT connect its own ApClient to the same slot while it runs.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started the game (via the exe / steam) from here. Kill what we
        // launched; never touch any AP client. No plaintext AP password is ever
        // written to disk by this plugin (the connection is entered in-game), so
        // there is nothing sensitive to scrub — clear our handle defensively.
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // OC2-Modding's in-game client receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // OC2-Modding renders its own AP status via an in-game console; no launcher
        // HUD channel into the game.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        string? installDir = ResolveInstallDir();
        bool    modInGame  = IsModInstalledInGame();
        bool    downloaded = FindInstallerBat() != null;

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Overcooked! 2 runs the OC2-Modding client — a BepInEx mod that " +
                   "doubles as the in-game Archipelago client. You own the base game " +
                   "(Steam, appid " + SteamAppId + "); the launcher detects your install " +
                   "and downloads OC2-Modding. OC2-Modding has its OWN installer " +
                   "(\"" + InstallBatName + "\") that you run once and point at your game " +
                   "folder — the launcher does not silently install it. You connect to " +
                   "your server IN-GAME on the sign-in screen — there is no connection " +
                   "file to pre-fill. These external steps are not verified by this " +
                   "launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: Overcooked! 2 install ────────────────────────────────
        panel.Children.Add(SectionHeader("OVERCOOKED! 2 INSTALL", muted));

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = installDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "The folder that contains \"Overcooked2.exe\". Auto-detected " +
                          "from Steam (appid " + SteamAppId + "); override here for " +
                          "Epic / GOG / non-default installs.",
        };
        var dirBtn = new Button
        {
            Content = "Locate install...", Width = 130, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select your Overcooked! 2 install folder " +
                                   "(contains Overcooked2.exe)",
                InitialDirectory = Directory.Exists(installDir) ? installDir!
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string? bad = ValidateExistingInstall(dlg.FolderName);
                if (bad != null)
                {
                    MessageBox.Show(bad, "Not an Overcooked! 2 folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                SaveOverrideInstallDir(dlg.FolderName);
                dirBox.Text = dlg.FolderName;
            }
        };
        DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new TextBlock
        {
            Text     = installDir != null
                        ? (DetectSteamInstallDir() == installDir
                            ? "✓ Detected via Steam (appid " + SteamAppId + ")."
                            : "Using your selected install folder.")
                        : "Overcooked! 2 not found automatically — use \"Locate install...\".",
            FontSize = 11, Foreground = installDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap,
            Margin   = new Thickness(0, 6, 0, 12),
        });

        // ── Section: OC2-Modding status ───────────────────────────────────
        panel.Children.Add(SectionHeader("STATUS", muted));
        panel.Children.Add(new TextBlock
        {
            Text     = downloaded
                        ? "✓ OC2-Modding downloaded (run \"" + InstallBatName + "\" next)."
                        : "✗ OC2-Modding not downloaded — click Install on the Play tab.",
            FontSize = 11, Foreground = downloaded ? success : warn,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text     = modInGame
                        ? "✓ OC2-Modding installed in the game folder (BepInEx present) — " +
                          "ready for Archipelago."
                        : "✗ OC2-Modding not detected in the game folder yet — run \"" +
                          InstallBatName + "\" and point it at your Overcooked! 2 folder.",
            FontSize = 11, Foreground = modInGame ? success : warn,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // Convenience: open the extracted OC2-Modding folder (where the .bat lives).
        if (downloaded)
        {
            var openBtn = new Button
            {
                Content = "Open OC2-Modding folder (run " + InstallBatName + ")",
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 0, 12),
                Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
                Foreground  = fg,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            openBtn.Click += (_, _) =>
            {
                try
                {
                    string? bat = FindInstallerBat();
                    string dir = bat != null
                        ? (Path.GetDirectoryName(bat) ?? ModDownloadDirectory)
                        : ModDownloadDirectory;
                    if (Directory.Exists(dir))
                        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
                }
                catch { /* best effort */ }
            };
            panel.Children.Add(openBtn);
        }

        // ── Section: Connection (this session) ────────────────────────────
        panel.Children.Add(SectionHeader("CONNECTING (entered in-game on the sign-in screen)", muted));
        panel.Children.Add(new TextBlock
        {
            Text = "Launch the game and, when you leave the title screen, OC2-Modding " +
                   "shows a sign-in screen. Enter your Server address (host:port, e.g. " +
                   "archipelago.gg:38281), your slot name as the username, and the " +
                   "password if your room has one. (Press \"Continue without " +
                   "Archipelago\" to play plain Overcooked! 2.) A mod file for the room " +
                   "is downloaded automatically on connect; your original save is never " +
                   "overwritten — the randomizer uses a temporary save directory.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(SectionHeader("GUIDED SETUP", muted));
        foreach (string step in new[]
        {
            "1. Make sure Overcooked! 2 is installed (owned on Steam, appid " + SteamAppId +
                "). Use \"Locate install...\" above if it was not detected. (Epic / Steam-beta " +
                "are supported; GOG is unofficial.)",
            "2. Click Install on the Play tab to download OC2-Modding (or get it from the " +
                "OC2-Modding releases page below).",
            "3. Run \"" + InstallBatName + "\" from the downloaded folder (use the \"Open " +
                "OC2-Modding folder\" button above) and follow its prompts to point it at " +
                "your Overcooked! 2 game folder. It installs BepInEx into the game; no " +
                "original game files are changed.",
            "4. Launch from this launcher (it starts the game; BepInEx auto-loads " +
                "OC2-Modding). To play vanilla again, set DisableAllMods = true in " +
                "...\\Overcooked! 2\\BepInEx\\config\\OC2Modding.cfg (or run \"" + UninstallBatName +
                "\" to remove the mod).",
            "5. On the in-game sign-in screen, enter your Server (host:port), slot name, and " +
                "optional password, then play. To play co-op, every player needs the same " +
                "OC2-Modding version and must sign in with the same connection info.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Overcooked! 2 on Steam ↗",     SteamStoreUrl),
            ("Overcooked! 2 Setup Guide ↗",  SetupGuideUrl),
            ("OC2-Modding (GitHub) ↗",       ModRepoUrl),
            ("OC2-Modding Releases ↗",       ModReleasesPageUrl),
            ("Archipelago Official ↗",       ArchipelagoSite),
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
            btn.Click += (_, _) => { try { System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
            panel.Children.Add(btn);
        }
        return panel;
    }

    private static TextBlock SectionHeader(string text, System.Windows.Media.Brush muted)
        => new TextBlock
        {
            Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        };

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // OC2-Modding releases are the AP-relevant news for this game.
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

    // ── Private helpers — install-dir resolution ──────────────────────────────

    /// Resolve the Overcooked! 2 install dir: the user's saved override (if it still
    /// looks valid) first, else Steam auto-detection. null when neither resolves.
    private string? ResolveInstallDir()
    {
        string? overrideDir = LoadOverrideInstallDir();
        if (!string.IsNullOrWhiteSpace(overrideDir) &&
            ValidateExistingInstall(overrideDir!) == null)
            return overrideDir;

        return DetectSteamInstallDir();
    }

    /// True when OC2-Modding is installed IN THE GAME FOLDER. The best on-disk signal
    /// is a BepInEx tree alongside an OC2Modding config/plugin (the .bat writes
    /// BepInEx\ + BepInEx\config\OC2Modding.cfg). Returns false when the game (or its
    /// folder) is not resolvable.
    private bool IsModInstalledInGame()
    {
        try
        {
            string? install = ResolveInstallDir();
            if (install == null || !Directory.Exists(install)) return false;

            string bepInEx = Path.Combine(install, "BepInEx");
            if (!Directory.Exists(bepInEx)) return false;

            // Strong signal: the OC2Modding config the installer drops.
            if (File.Exists(Path.Combine(bepInEx, "config", "OC2Modding.cfg")))
                return true;

            // Fallback signal: an OC2Modding plugin dll under BepInEx\plugins, or any
            // file in the BepInEx tree whose name mentions OC2Modding.
            string plugins = Path.Combine(bepInEx, "plugins");
            if (Directory.Exists(plugins))
            {
                foreach (string f in Directory.EnumerateFiles(plugins, "*", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(f).ToLowerInvariant();
                    if (name.Contains("oc2modding") || name.Contains("oc2-modding"))
                        return true;
                }
            }
        }
        catch { /* permission / vanished — treat as not installed */ }
        return false;
    }

    /// Find the game exe in `dir`: prefer Overcooked2.exe, else a fuzzy
    /// "*overcook*.exe" (excluding obvious helpers). Null if none.
    private static string? FindGameExeIn(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;

            string preferred = Path.Combine(dir, PreferredExeName);
            if (File.Exists(preferred)) return preferred;

            foreach (string exe in Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (IsHelperExe(name)) continue;
                if (name.Contains("overcook")) return exe;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    /// Names that are NOT the runnable game (uninstaller, helpers, the Steam shim).
    private static bool IsHelperExe(string nameLowerNoExt)
        => nameLowerNoExt.Contains("unins")    ||
           nameLowerNoExt.Contains("setup")    ||
           nameLowerNoExt.Contains("crash")    ||
           nameLowerNoExt.Contains("steam")    ||
           nameLowerNoExt.Contains("vcredist") ||
           nameLowerNoExt.Contains("dxsetup")  ||
           nameLowerNoExt.Contains("doorstop");

    /// Locate the bundled "oc2-modding-install.bat" inside our download folder (top
    /// level, or nested one folder deep if the zip wrapped its contents). Null if
    /// the mod has not been downloaded/extracted yet.
    private string? FindInstallerBat()
    {
        try
        {
            if (!Directory.Exists(ModDownloadDirectory)) return null;

            string top = Path.Combine(ModDownloadDirectory, InstallBatName);
            if (File.Exists(top)) return top;

            // Search a couple of levels deep (release zips sometimes nest a folder).
            foreach (string f in Directory.EnumerateFiles(
                         ModDownloadDirectory, InstallBatName, SearchOption.AllDirectories))
                return f;
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — Steam detection (adapted from the Stardew plugin) ────

    /// Best-effort Steam auto-detection of the Overcooked! 2 install:
    ///   1. Steam root from registry (HKCU SteamPath, then HKLM InstallPath).
    ///   2. Library roots from steamapps\libraryfolders.vdf (+ the Steam root).
    ///   3. appmanifest_728880.acf → "installdir" → steamapps\common\<installdir>.
    ///   4. Fall back to steamapps\common\Overcooked! 2.
    /// Returns the install dir (validated to contain the game exe) or null. Never
    /// throws — any failure yields null and the manual picker is used.
    private static string? DetectSteamInstallDir()
    {
        try
        {
            foreach (string steamRoot in EnumerateSteamRoots())
            {
                foreach (string lib in EnumerateSteamLibraries(steamRoot))
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    if (!Directory.Exists(steamapps)) continue;

                    string acf = Path.Combine(steamapps, $"appmanifest_{SteamAppId}.acf");
                    if (File.Exists(acf))
                    {
                        string? installDirName = ReadVdfValue(acf, "installdir");
                        if (!string.IsNullOrWhiteSpace(installDirName))
                        {
                            string candidate = Path.Combine(steamapps, "common", installDirName!);
                            if (LooksLikeOvercooked(candidate)) return candidate;
                        }
                    }

                    string std = Path.Combine(steamapps, "common", SteamCommonFolderName);
                    if (LooksLikeOvercooked(std)) return std;
                }
            }
        }
        catch { /* registry/file access failed — fall through to null */ }
        return null;
    }

    /// Candidate Steam root folders from the registry (HKCU first, then HKLM
    /// WOW6432Node, then HKLM), de-duplicated. Empty if none / non-Windows.
    private static IEnumerable<string> EnumerateSteamRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string? root in new[]
        {
            ReadRegistryString(Microsoft.Win32.Registry.CurrentUser,
                               @"Software\Valve\Steam", "SteamPath"),
            ReadRegistryString(Microsoft.Win32.Registry.LocalMachine,
                               @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            ReadRegistryString(Microsoft.Win32.Registry.LocalMachine,
                               @"SOFTWARE\Valve\Steam", "InstallPath"),
        })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string norm = root!.Replace('/', '\\');
            if (Directory.Exists(norm) && seen.Add(norm))
                yield return norm;
        }
    }

    /// All Steam library roots: the Steam root itself plus every "path" entry in
    /// steamapps\libraryfolders.vdf. De-duplicated.
    private static IEnumerable<string> EnumerateSteamLibraries(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (seen.Add(steamRoot))
            yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string[] lines;
        try { lines = File.ReadAllLines(vdf); }
        catch { yield break; }

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            string? val = TryReadQuotedKeyValue(line, "path");
            if (val == null)
                val = TryReadLastQuotedAbsolutePath(line);
            if (string.IsNullOrWhiteSpace(val)) continue;

            string norm = val!.Replace(@"\\", @"\").Replace('/', '\\');
            if (Directory.Exists(norm) && seen.Add(norm))
                yield return norm;
        }
    }

    /// True when a folder looks like an Overcooked! 2 install (has the game exe).
    private static bool LooksLikeOvercooked(string dir)
    {
        try { return FindGameExeIn(dir) != null; }
        catch { return false; }
    }

    /// Read a string value from the registry, swallowing all failures (missing
    /// key, access denied, non-Windows). Returns null when unavailable.
    private static string? ReadRegistryString(
        Microsoft.Win32.RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    /// Read a top-level quoted value for `key` from a Valve KeyValues text file
    /// (e.g. appmanifest .acf: `"installdir"  "Overcooked! 2"`). First match or null.
    private static string? ReadVdfValue(string path, string key)
    {
        try
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string? val = TryReadQuotedKeyValue(raw.Trim(), key);
                if (val != null) return val;
            }
        }
        catch { /* unreadable — null */ }
        return null;
    }

    /// Parse a single `"key"   "value"` KeyValues line. Returns the value when the
    /// line's key matches `key` (case-insensitive), else null.
    private static string? TryReadQuotedKeyValue(string line, string key)
    {
        int k0 = line.IndexOf('"');
        if (k0 < 0) return null;
        int k1 = line.IndexOf('"', k0 + 1);
        if (k1 < 0) return null;
        string foundKey = line.Substring(k0 + 1, k1 - k0 - 1);
        if (!string.Equals(foundKey, key, StringComparison.OrdinalIgnoreCase))
            return null;

        int v0 = line.IndexOf('"', k1 + 1);
        if (v0 < 0) return null;
        int v1 = line.IndexOf('"', v0 + 1);
        if (v1 < 0) return null;
        return line.Substring(v0 + 1, v1 - v0 - 1);
    }

    /// For older libraryfolders.vdf shapes, pull the last quoted token from a line
    /// and return it only if it looks like an absolute path. Null otherwise.
    private static string? TryReadLastQuotedAbsolutePath(string line)
    {
        int end = line.LastIndexOf('"');
        if (end <= 0) return null;
        int start = line.LastIndexOf('"', end - 1);
        if (start < 0) return null;
        string token = line.Substring(start + 1, end - start - 1);
        bool looksAbsolute =
            (token.Length >= 3 && token[1] == ':' && (token[2] == '\\' || token[2] == '/')) ||
            token.StartsWith('/') || token.Contains(@":\\");
        return looksAbsolute ? token : null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Launch the game so BepInEx auto-loads OC2-Modding. Preference order:
    ///   1. Overcooked2.exe (fuzzy) in the detected/override install dir — the
    ///      doorstop next to it injects the mod, so this IS the modded entry point.
    ///   2. steam://rungameid/728880 (Steam launches the configured exe).
    /// If neither resolves, surface a clear, honest message rather than failing
    /// opaquely.
    private void StartGame()
    {
        string? installDir = ResolveInstallDir();

        // 1: a concrete exe in the install dir.
        if (installDir != null)
        {
            string? exe = FindGameExeIn(installDir);
            if (exe != null)
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName         = exe,
                    WorkingDirectory = installDir,
                    UseShellExecute  = false,
                }) ?? throw new InvalidOperationException("Failed to start Overcooked! 2.");

                TrackProcess(proc);
                return;
            }
        }

        // 2: Steam protocol fallback (no install dir resolved, or no exe found).
        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName        = SteamRunUrl,
                UseShellExecute = true,
            });
            if (proc != null) TrackProcess(proc);
            IsRunning = true; // steam:// may return a transient/no process handle
        }
        catch (Exception ex)
        {
            throw new FileNotFoundException(
                "Could not launch Overcooked! 2. Set your install folder in Settings " +
                "and make sure OC2-Modding is installed (run \"" + InstallBatName + "\"), " +
                "then try again. (You can also start the game through Steam yourself.)\n\n" +
                ex.Message,
                PreferredExeName);
        }
    }

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
                GameExited?.Invoke(proc.ExitCode);
            };
        }
        catch { /* some shell-launched processes don't expose Exited — non-fatal */ }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// "v1.2.3" → "1.2.3" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest OC2-Modding release: version + the .zip download URL + its
    /// filename. Prefers a Windows .zip asset (excluding source archives); falls back
    /// to the first .zip; falls back to (FallbackModVersion, null, null) when the API
    /// is unreachable / no asset resolves — the caller then routes the user to the
    /// releases page.
    private async Task<(string Version, string? ZipUrl, string? ZipName)>
        ResolveLatestModAsync(CancellationToken ct)
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
                string? preferred = null, preferredName = null;  // a windows/oc2 .zip
                string? anyZip    = null, anyZipName    = null;  // any non-source .zip
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;
                    if (lower.Contains("source")) continue;

                    if (anyZip == null) { anyZip = url; anyZipName = name; }
                    if (preferred == null &&
                        (lower.Contains("oc2") || lower.Contains("modding") ||
                         lower.Contains("win")))
                    {
                        preferred = url; preferredName = name;
                    }
                }
                string? zip     = preferred ?? anyZip;
                string? zipName = preferredName ?? anyZipName;
                if (zip != null)
                    return (version, zip, zipName);

                // A release with no usable zip asset — report the version but no URL.
                return (version, null, null);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → fallback below */ }

        return (FallbackModVersion, null, null);
    }

    // ── Private helpers — download + extract the mod (into our OWN folder) ─────

    /// Download the OC2-Modding zip and extract it into the launcher's OWN
    /// ModDownloadDirectory (a clean replace). The user then runs the bundled
    /// "oc2-modding-install.bat" to install it into the game folder (interactive —
    /// we guide it, never silent-run it).
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"oc2-modding-{(version == FallbackModVersion ? "latest" : version)}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading OC2-Modding {(version == FallbackModVersion ? "" : version)}..."));
            using var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempZip))
            {
                var buf = new byte[81920];
                int bytesRead;
                while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                    downloaded += bytesRead;
                    if (total > 0)
                    {
                        int pct = (int)(5 + 80 * downloaded / total);
                        progress.Report((pct, $"Downloading OC2-Modding... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((88, "Extracting OC2-Modding..."));

            // Clean replace of the download folder so an update is tidy.
            if (Directory.Exists(ModDownloadDirectory))
            {
                try { Directory.Delete(ModDownloadDirectory, recursive: true); }
                catch { /* in-use — overwrite below */ }
            }
            Directory.CreateDirectory(ModDownloadDirectory);

            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, ModDownloadDirectory, overwriteFiles: true);

            progress.Report((95, "OC2-Modding extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // The one launcher-side setting (the manual install-dir override) is kept in
    // this plugin's OWN JSON file so it stays a single self-contained source file
    // and does NOT modify Core/SettingsStore. BOM-less UTF-8, read-modify-write
    // (same approach as the Stardew / Undertale plugins).

    private sealed class Overcooked2Settings
    {
        public string? InstallDirOverride { get; set; }
    }

    private Overcooked2Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Overcooked2Settings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(Overcooked2Settings s)
    {
        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — the setting just won't persist this time */ }
    }

    private string? LoadOverrideInstallDir()
    {
        string? p = LoadSettings().InstallDirOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }

    private void SaveOverrideInstallDir(string dir)
    {
        var s = LoadSettings();
        s.InstallDirOverride = dir;
        SaveSettings(s);
    }
}
