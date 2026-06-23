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

namespace LauncherV2.Plugins.StardewValley;

// ═══════════════════════════════════════════════════════════════════════════════
// StardewValleyPlugin — install / launch for "Stardew Valley" played through the
// StardewArchipelago mod. This is a NATIVE "ConnectsItself" integration in the
// same family as Jak (ArchipelaGOAL) and Ship of Harkinian: the game speaks to
// the AP server itself via an in-game client — no emulator, no Lua bridge, and
// no launcher-held ApClient on the slot.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// Stardew Valley is a STEAM-MOD native (you own the base game on Steam; the AP
// support is delivered as a SMAPI mod). The honest ceiling is "automate what's
// possible, guide the irreducible parts," exactly like the shipped Jak plugin.
// The verified facts:
//
//   * THE BASE GAME is bring-your-own: a legally-owned copy of Stardew Valley
//     (Steam appid 413150). The launcher does NOT install or own the game — it
//     DETECTS the Steam install (registry → libraryfolders.vdf →
//     appmanifest_413150.acf → steamapps\common\Stardew Valley\) and also offers
//     a manual folder picker. (GOG/other stores work too via the manual picker.)
//
//   * THE MOD LOADER is SMAPI (https://smapi.io/, repo Pathoschild/SMAPI). SMAPI
//     ships as a WIZARD INSTALLER zip (asset pattern "SMAPI-<ver>-installer.zip";
//     latest verified 4.5.2) that contains an interactive install.exe. It CANNOT
//     be silently dropped into the game folder — it must be run by the user. So
//     this plugin GUIDES SMAPI (download link + numbered steps); it does not fake
//     a one-click. After SMAPI installs, "StardewModdingAPI.exe" lives next to
//     "Stardew Valley.exe" in the install dir and is how the modded game is run.
//
//   * THE AP MOD is StardewArchipelago (repo agilbert1412/StardewArchipelago,
//     also on Nexus mods/16087). It IS a plain GitHub release .zip (asset pattern
//     "StardewArchipelago.<ver>.zip"; latest verified 7.4.20) whose contents go
//     into the game's "Mods" folder, each mod in its own sub-folder (the release
//     zip already nests a top "StardewArchipelago" folder). THIS the launcher
//     CAN automate: download the release zip and extract it into the detected
//     install's Mods folder. (SMAPI must be present first for it to do anything.)
//
//   * HOW IT CONNECTS (verified via the official setup guide + the mod README):
//     the AP server connection is entered IN-GAME on the NEW-CHARACTER CREATION
//     screen — three fields appear there: Server (host:port, e.g.
//     archipelago.gg:38281), Slot name, and an optional Password. The game then
//     connects automatically and AUTO-RECONNECTS when the save is loaded later
//     ("you will never need to enter this information again for this character").
//     There is NO documented config.json / connect.json / command-line arg the
//     launcher can pre-write. So this plugin does NOT attempt a connection
//     prefill — doing so would be dishonest theatre (same stance as Jak). The
//     settings panel + post-install note state exactly this and surface the
//     session's host/port/slot so the user can copy them into those three fields.
//
//   * AP SERVER SIDE: Stardew Valley is a CORE Archipelago world (it ships inside
//     Archipelago itself, no custom_worlds drop needed) — verified against
//     worlds/stardew_valley/__init__.py: StardewValleyWorld, game = "Stardew
//     Valley", required_client_version = (0, 4, 0).
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Stardew install (registry + libraryfolders.vdf + ACF),
//      with a manual override folder picker persisted in this plugin's OWN JSON
//      sidecar (Games/ROMs/stardew_valley/stardew_launcher.json — Core/
//      SettingsStore is NOT modified, same approach as Doom1993Plugin / Jak).
//   2. INSTALL/UPDATE = best-effort download of the StardewArchipelago release
//      .zip and extract it into the detected install's Mods folder. SMAPI itself
//      is GUIDED (download link + steps) — it has its own interactive installer
//      we deliberately do NOT silent-run.
//   3. GUIDED STEPS + LINKS (SMAPI, StardewArchipelago, the official AP setup
//      guide, archipelago.gg) in a Jak-style honest settings panel + note.
//   4. LAUNCH = run StardewModdingAPI.exe from the install dir (the correct modded
//      entry point); fall back to "Stardew Valley.exe", then steam://rungameid/
//      413150 if neither exe path resolves. NO connection prefill (none exists —
//      the user enters the 3 fields on the character-creation screen). The note
//      shows the session's server/slot to copy. ConnectsItself = true (the mod's
//      in-game client owns the slot — the launcher must not hold its own ApClient
//      on the same slot). SupportsStandalone = true (plain Stardew runs fine,
//      modded or not).
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Jak/SoH-style) ────────────
//   * IsInstalled means the StardewArchipelago mod folder is present in a
//     detected install's Mods folder (best signal that AP play is set up). A
//     SMAPI-present check is surfaced in the settings panel for the user, but the
//     mod-present check is the gate for the tile (SMAPI alone is not enough to
//     play AP).
//   * The Steam vdf/acf are simple line-based Valve key-value text; we parse them
//     defensively (quoted "key" "value" pairs) and never throw on a malformed
//     file — detection just yields null and the manual picker is used instead.
//   * The mod release zip's top-level folder name is assumed "StardewArchipelago"
//     (verified on 7.4.20); detection also accepts any Mods sub-folder whose
//     manifest/name mentions "archipelago", so a renamed folder still counts.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class StardewValleyPlugin : IGamePlugin
{
    // ── Constants — repos / links ──────────────────────────────────────────────

    // The AP mod — a plain GitHub release zip the launcher CAN install.
    private const string MOD_OWNER = "agilbert1412";
    private const string MOD_REPO  = "StardewArchipelago";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";
    private const string GH_MOD_RELEASES_LATEST_URL = $"{GH_MOD_RELEASES_URL}/latest";
    private const string ModNexusUrl = "https://www.nexusmods.com/stardewvalley/mods/16087";

    // SMAPI — a wizard installer we GUIDE (never silent-install).
    private const string SMAPI_OWNER = "Pathoschild";
    private const string SMAPI_REPO  = "SMAPI";
    private const string SmapiSite        = "https://smapi.io/";
    private const string SmapiReleasesUrl = $"https://github.com/{SMAPI_OWNER}/{SMAPI_REPO}/releases";
    private const string SmapiInstallWikiUrl =
        "https://stardewvalleywiki.com/Modding:Installing_SMAPI_on_Windows";

    private const string SetupGuideUrl = "https://archipelago.gg/tutorial/Stardew%20Valley/setup/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    /// Stardew Valley Steam application id.
    private const string SteamAppId = "413150";

    /// The standard Steam install sub-folder name for Stardew Valley.
    private const string SteamCommonFolderName = "Stardew Valley";

    /// Pinned fallback for the StardewArchipelago mod when the GitHub API is
    /// unreachable. 7.4.20 verified live 2026-06-14 (asset
    /// "StardewArchipelago.7.4.20.zip"). The API path is the normal route; this is
    /// only the net so an offline Install still has something to fetch.
    private const string FallbackModVersion = "7.4.20";
    private static readonly string FallbackModZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackModVersion}/StardewArchipelago.{FallbackModVersion}.zip";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-mod-version stamp, written after the mod is extracted into Mods.
    private const string VersionFileName = "stardew_ap_mod_version.dat";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "stardew_valley";
    public string DisplayName => "Stardew Valley";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/stardew_valley/__init__.py
    /// (StardewValleyWorld.game). required_client_version = (0, 4, 0).
    public string ApWorldName => "Stardew Valley";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "stardew_valley.png");

    public string ThemeAccentColor => "#6FAE3B";   // Pelican Town spring green
    public string[] GameBadges     => new[] { "Steam · needs SMAPI mod" };

    public string Description =>
        "Stardew Valley, the beloved farming RPG by ConcernedApe, played through " +
        "the StardewArchipelago mod — a native in-game Archipelago client, so the " +
        "game connects to the multiworld itself with no emulator and no Lua " +
        "bridge. Friendships, fish, minerals, crafting recipes, building " +
        "upgrades, festivals and much more become checks shuffled across the " +
        "multiworld. You bring your own copy of Stardew Valley (owned on Steam); " +
        "the integration runs on SMAPI, the Stardew mod loader. The launcher " +
        "detects your Steam install and installs the Archipelago mod into it for " +
        "you, and guides the SMAPI step. You connect to your server in-game on " +
        "the new-character screen.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means: the StardewArchipelago mod folder is present in a
    /// detected Stardew install's Mods folder. (SMAPI alone is not enough to play
    /// AP — the mod is the gate. SMAPI presence is reported separately in the
    /// settings panel.)
    public bool IsInstalled => ResolveInstalledModFolder() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// The resolved Stardew install directory (the folder that contains
    /// "Stardew Valley.exe" / "StardewModdingAPI.exe" and the "Mods" sub-folder).
    /// Settable so the launcher core's GameDirectory contract is honored; backed
    /// by detection + the sidecar override. Setting it persists the override.
    public string GameDirectory
    {
        get => ResolveInstallDir() ?? "";
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
                SaveOverrideInstallDir(value);
        }
    }

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Doom1993Plugin and
    /// JakAndDaxterPlugin). Lives under the ROM-library tree by GameId.
    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "stardew_launcher.json");

    /// Where we stamp the installed mod version (inside the ROM-library tree so we
    /// never write into the user's game folder for bookkeeping).
    private string VersionFilePath
        => Path.Combine(RomLibraryDirectory, VersionFileName);

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    /// Filename of the mod zip asset seen on the resolved release (so the saved
    /// download keeps the upstream name). null until a release is resolved.
    private string? _modZipFileName;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // StardewArchipelago's in-game client reports checks/items/goal to the AP
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
        // 0. We need a detected/overridden Stardew install to drop the mod into.
        string? installDir = ResolveInstallDir();
        if (installDir == null)
            throw new InvalidOperationException(
                "Could not find your Stardew Valley install automatically. Open this " +
                "game's Settings tab and use \"Locate install...\" to pick the folder " +
                "that contains \"Stardew Valley.exe\" (for Steam this is usually " +
                @"...\steamapps\common\Stardew Valley). Then run Install again.");

        // 1. Resolve the latest mod release (pinned fallback when offline).
        progress.Report((2, "Checking the latest StardewArchipelago release..."));
        var (version, zipUrl, zipName) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;
        _modZipFileName  = zipName;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the StardewArchipelago mod download on GitHub. Check " +
                "your internet connection, or download it manually from " +
                ModRepoUrl + "/releases (or " + ModNexusUrl + ").");

        // 2. Download + extract the mod into the install's Mods folder.
        string modsDir = Path.Combine(installDir, "Mods");
        await DownloadAndExtractModAsync(zipUrl, version, modsDir, progress, ct);

        // 3. Stamp the installed mod version.
        Directory.CreateDirectory(RomLibraryDirectory);
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        bool smapiPresent = ResolveSmapiExe(installDir) != null;
        progress.Report((100,
            $"StardewArchipelago {version} installed into your Mods folder. " +
            (smapiPresent
                ? "SMAPI is present — launch the game and connect on the new-character screen."
                : "Next: install SMAPI (the mod loader) from smapi.io — see the guided " +
                  "steps in Settings. Then launch and connect on the new-character screen.")));
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
        // HONEST: the AP server connection for this game is entered IN-GAME on the
        // new-character creation screen (three fields: Server host:port, Slot,
        // optional Password). The mod auto-reconnects on subsequent loads. There
        // is NO documented config/CLI prefill we can apply here (verified — see
        // header). So launching from this tile means starting the modded game via
        // SMAPI; the user types the session's server/slot on the character screen
        // (the settings panel + note surface those values to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the StardewArchipelago client is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartModdedGame();
        return Task.CompletedTask;
    }

    /// Plain (modded or vanilla) Stardew is fully playable without AP.
    public bool SupportsStandalone => true;

    /// StardewArchipelago's in-game client owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartModdedGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started the game (via SMAPI/exe/steam) from here. Kill what
        // we launched; never touch any AP client. No plaintext AP password is ever
        // written to disk by this plugin (the connection is entered in-game), so
        // there is nothing to scrub — clear our handle defensively.
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // StardewArchipelago's in-game client receives items from the AP server
        // directly; there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // StardewArchipelago renders its own AP status in-game; no launcher HUD.
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
        string? modFolder  = ResolveInstalledModFolder();
        bool    smapiOk    = installDir != null && ResolveSmapiExe(installDir) != null;

        // ── Unverified-offline honesty header ─────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Stardew Valley runs the StardewArchipelago mod on SMAPI. You own " +
                   "the base game (Steam); the launcher detects your install and " +
                   "installs the Archipelago mod into its Mods folder. SMAPI itself has " +
                   "its own installer (download + steps below) and is not silently " +
                   "installed by this launcher. You connect to your server in-game, on " +
                   "the new-character creation screen — there is no connection file to " +
                   "pre-fill. These external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: Stardew install ──────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "STARDEW VALLEY INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = installDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "The folder that contains \"Stardew Valley.exe\" and (after SMAPI) " +
                          "\"StardewModdingAPI.exe\". Auto-detected from Steam; override here " +
                          "for GOG / non-default installs.",
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
                Title            = "Select your Stardew Valley install folder " +
                                   "(contains Stardew Valley.exe)",
                InitialDirectory = Directory.Exists(installDir) ? installDir!
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string? bad = ValidateExistingInstall(dlg.FolderName);
                if (bad != null)
                {
                    MessageBox.Show(bad, "Not a Stardew Valley folder",
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
                        : "Stardew Valley not found automatically — use \"Locate install...\".",
            FontSize = 11, Foreground = installDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap,
            Margin   = new Thickness(0, 6, 0, 12),
        });

        // ── Section: SMAPI + mod status ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "STATUS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text     = smapiOk
                        ? "✓ SMAPI found (StardewModdingAPI.exe present)."
                        : "✗ SMAPI not found — install it from smapi.io (guided steps below).",
            FontSize = 11, Foreground = smapiOk ? success : warn,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new TextBlock
        {
            Text     = modFolder != null
                        ? "✓ StardewArchipelago mod installed" +
                          (InstalledVersion != null && InstalledVersion != "installed"
                            ? " (v" + InstalledVersion + ")." : ".")
                        : "✗ StardewArchipelago mod not installed — click Install on the Play tab.",
            FontSize = 11, Foreground = modFolder != null ? success : warn,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Connection (this session) ────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (entered in-game on the new-character screen)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "When you create a new farmer, three fields appear: Server (host:port, " +
                   "e.g. archipelago.gg:38281), Slot name, and an optional Password. The " +
                   "game connects automatically and reconnects whenever you load that save " +
                   "again — you only enter it once per character.",
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
            "1. Make sure Stardew Valley is installed (owned on Steam). Use \"Locate install...\" " +
                "above if it was not detected.",
            "2. Install SMAPI (the mod loader): download \"SMAPI-<version>-installer.zip\" from " +
                "smapi.io, unzip it, run the installer (install on Windows.bat / install.exe), " +
                "and point it at your Stardew Valley folder.",
            "3. Install the StardewArchipelago mod: use Install on the Play tab (it downloads the " +
                "mod and drops it into your Mods folder), or get it from Nexus / GitHub manually.",
            "4. Launch from this launcher (it runs StardewModdingAPI.exe), or launch via SMAPI / " +
                "the modded shortcut yourself. Do NOT launch plain \"Stardew Valley.exe\" for AP — " +
                "the mod only loads through SMAPI.",
            "5. On the title screen, create a NEW farmer. Enter your Server (host:port), Slot " +
                "name, and optional Password in the three Archipelago fields, then play. The game " +
                "connects and reconnects automatically from then on.",
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
            ("SMAPI (smapi.io) ↗",                 SmapiSite),
            ("Installing SMAPI on Windows ↗",      SmapiInstallWikiUrl),
            ("StardewArchipelago (GitHub) ↗",      ModRepoUrl),
            ("StardewArchipelago (Nexus) ↗",       ModNexusUrl),
            ("Stardew Valley Setup Guide ↗",       SetupGuideUrl),
            ("Archipelago Official ↗",             ArchipelagoSite),
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

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // StardewArchipelago mod releases are the AP-relevant news for this game.
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

    // ── AutoMod folder validation (§10) ───────────────────────────────────────

    /// Accept a folder that looks like a Stardew Valley install: it must contain
    /// "Stardew Valley.exe" (the base game exe). We do NOT require SMAPI here —
    /// the user may be pointing us at a fresh install before installing SMAPI.
    public string? ValidateExistingInstall(string folder)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return "That folder does not exist. Pick your Stardew Valley folder.";

            if (File.Exists(Path.Combine(folder, "Stardew Valley.exe")) ||
                File.Exists(Path.Combine(folder, "StardewValley.exe"))  ||
                File.Exists(Path.Combine(folder, "StardewModdingAPI.exe")))
                return null;

            return "That folder does not look like a Stardew Valley install (no " +
                   "\"Stardew Valley.exe\"). Pick the folder that contains the game exe — " +
                   @"for Steam this is usually ...\steamapps\common\Stardew Valley.";
        }
        catch
        {
            return "Could not read that folder. Pick a different one and try again.";
        }
    }

    // ── Private helpers — install-dir resolution ──────────────────────────────

    /// Resolve the Stardew install dir: the user's saved override (if it still
    /// looks valid) first, else Steam auto-detection. null when neither resolves.
    private string? ResolveInstallDir()
    {
        string? overrideDir = LoadOverrideInstallDir();
        if (!string.IsNullOrWhiteSpace(overrideDir) &&
            ValidateExistingInstall(overrideDir!) == null)
            return overrideDir;

        return DetectSteamInstallDir();
    }

    /// Locate the StardewArchipelago mod folder inside the resolved install's Mods
    /// folder. Accepts the canonical "StardewArchipelago" sub-folder, or any Mods
    /// sub-folder whose name or manifest mentions "archipelago" (defensive against
    /// a renamed folder). Returns the folder path, or null when not installed.
    private string? ResolveInstalledModFolder()
    {
        try
        {
            string? install = ResolveInstallDir();
            if (install == null) return null;
            string modsDir = Path.Combine(install, "Mods");
            if (!Directory.Exists(modsDir)) return null;

            // Canonical fast path.
            string canonical = Path.Combine(modsDir, "StardewArchipelago");
            if (Directory.Exists(canonical) &&
                File.Exists(Path.Combine(canonical, "manifest.json")))
                return canonical;

            // Defensive scan: a mod sub-folder named or manifested as Archipelago.
            foreach (string sub in Directory.EnumerateDirectories(modsDir))
            {
                string name = Path.GetFileName(sub).ToLowerInvariant();
                string manifest = Path.Combine(sub, "manifest.json");
                if (!File.Exists(manifest)) continue;

                if (name.Contains("archipelago"))
                    return sub;

                try
                {
                    string mtxt = File.ReadAllText(manifest);
                    if (mtxt.Contains("archipelago", StringComparison.OrdinalIgnoreCase))
                        return sub;
                }
                catch { /* unreadable manifest — skip */ }
            }
        }
        catch { /* Mods folder vanished / permission — treat as not installed */ }
        return null;
    }

    /// Resolve SMAPI's exe in an install dir. Returns the path or null.
    private static string? ResolveSmapiExe(string installDir)
    {
        try
        {
            string p = Path.Combine(installDir, "StardewModdingAPI.exe");
            return File.Exists(p) ? p : null;
        }
        catch { return null; }
    }

    // ── Private helpers — Steam detection ─────────────────────────────────────

    /// Best-effort Steam auto-detection of the Stardew install:
    ///   1. Find the Steam root (HKCU\Software\Valve\Steam SteamPath, then
    ///      HKLM WOW6432Node InstallPath).
    ///   2. Enumerate library folders from steamapps\libraryfolders.vdf (+ the
    ///      Steam root itself).
    ///   3. In each, look for steamapps\appmanifest_413150.acf and read its
    ///      "installdir", resolving steamapps\common\<installdir>.
    ///   4. Fall back to steamapps\common\Stardew Valley if the ACF is absent but
    ///      the folder exists.
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

                    // Preferred: read the app manifest's installdir.
                    string acf = Path.Combine(steamapps, $"appmanifest_{SteamAppId}.acf");
                    if (File.Exists(acf))
                    {
                        string? installDirName = ReadVdfValue(acf, "installdir");
                        if (!string.IsNullOrWhiteSpace(installDirName))
                        {
                            string candidate = Path.Combine(steamapps, "common", installDirName!);
                            if (LooksLikeStardew(candidate)) return candidate;
                        }
                    }

                    // Fallback: the standard common sub-folder.
                    string std = Path.Combine(steamapps, "common", SteamCommonFolderName);
                    if (LooksLikeStardew(std)) return std;
                }
            }
        }
        catch { /* registry/file access failed — fall through to null */ }
        return null;
    }

    /// Candidate Steam root folders from the registry (HKCU first, then HKLM
    /// WOW6432Node), de-duplicated. Empty if none / non-Windows.
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

        // libraryfolders.vdf is a Valve KeyValues text file. Library roots appear
        // as:  "path"  "D:\\SteamLibrary"  (older formats also used a bare
        // "1" "D:\\SteamLibrary" line). Match any quoted value that is an existing
        // directory and is reached via a "path" key — but also accept the older
        // shape by scanning for values that look like absolute paths.
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            // "path"   "X:\\..."
            string? val = TryReadQuotedKeyValue(line, "path");
            if (val == null)
            {
                // Older format: a numbered key mapping straight to a path value.
                // Pull the LAST quoted token and treat it as a candidate path.
                val = TryReadLastQuotedAbsolutePath(line);
            }
            if (string.IsNullOrWhiteSpace(val)) continue;

            string norm = val!.Replace(@"\\", @"\").Replace('/', '\\');
            if (Directory.Exists(norm) && seen.Add(norm))
                yield return norm;
        }
    }

    /// True when a folder contains a Stardew Valley game exe (modded or vanilla).
    private static bool LooksLikeStardew(string dir)
    {
        try
        {
            return Directory.Exists(dir) &&
                   (File.Exists(Path.Combine(dir, "Stardew Valley.exe")) ||
                    File.Exists(Path.Combine(dir, "StardewValley.exe"))  ||
                    File.Exists(Path.Combine(dir, "StardewModdingAPI.exe")));
        }
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
    /// (e.g. appmanifest .acf: `"installdir"  "Stardew Valley"`). Returns the
    /// first match's value, or null. Tolerant of whitespace/tabs; never throws.
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
        // Expect at least two quoted tokens: "<key>" ... "<value>".
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
    /// and return it only if it looks like an absolute path (has a ":\" drive or a
    /// leading slash). Returns null otherwise.
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

    /// Launch the modded game. Preference order:
    ///   1. StardewModdingAPI.exe in the install dir (correct modded entry point).
    ///   2. "Stardew Valley.exe" / "StardewValley.exe" in the install dir.
    ///   3. steam://rungameid/413150 (Steam launches whatever is configured).
    /// If none resolve, surface a clear, honest message rather than failing
    /// opaquely. Never throws into the caller in a way that looks like a crash.
    private void StartModdedGame()
    {
        string? installDir = ResolveInstallDir();

        // 1 + 2: a concrete exe in the install dir.
        if (installDir != null)
        {
            string? exe = ResolveSmapiExe(installDir);
            if (exe == null)
            {
                foreach (string cand in new[] { "Stardew Valley.exe", "StardewValley.exe" })
                {
                    string p = Path.Combine(installDir, cand);
                    if (File.Exists(p)) { exe = p; break; }
                }
            }

            if (exe != null)
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName         = exe,
                    WorkingDirectory = installDir,
                    UseShellExecute  = false,
                }) ?? throw new InvalidOperationException("Failed to start Stardew Valley.");

                TrackProcess(proc);
                return;
            }
        }

        // 3: Steam protocol fallback (no install dir resolved, or no exe found).
        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName        = $"steam://rungameid/{SteamAppId}",
                UseShellExecute = true,
            });
            if (proc != null) TrackProcess(proc);
            IsRunning = true; // steam:// may return a transient/no process handle
        }
        catch (Exception ex)
        {
            throw new FileNotFoundException(
                "Could not launch Stardew Valley. Install SMAPI and the Archipelago " +
                "mod, set your install folder in Settings, then try again. (You can " +
                "also start the game through SMAPI yourself.)\n\n" + ex.Message,
                "StardewModdingAPI.exe");
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

    /// "v7.4.20" → "7.4.20" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest StardewArchipelago mod release: version + the mod zip
    /// asset URL + its filename. Prefers an asset matching "StardewArchipelago"
    /// and ".zip"; falls back to the first .zip asset; falls back to the pinned
    /// 7.4.20 direct URL when the API is unreachable.
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
                string? preferred = null, preferredName = null;  // StardewArchipelago*.zip
                string? anyZip    = null, anyZipName    = null;  // any .zip
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    if (anyZip == null) { anyZip = url; anyZipName = name; }
                    if (preferred == null && lower.Contains("stardewarchipelago"))
                    {
                        preferred = url; preferredName = name;
                    }
                }
                string? zip     = preferred ?? anyZip;
                string? zipName = preferredName ?? anyZipName;
                if (zip != null)
                    return (version, zip, zipName);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackModVersion, FallbackModZipUrl,
                $"StardewArchipelago.{FallbackModVersion}.zip");
    }

    // ── Private helpers — download + extract the mod ──────────────────────────

    /// Download the mod zip and extract it into the install's Mods folder. The
    /// release zip nests a top "StardewArchipelago" folder, so extracting directly
    /// into Mods lands the mod at Mods\StardewArchipelago (each mod in its own
    /// sub-folder, as SMAPI requires). A stale mod folder is replaced cleanly.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string modsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"stardew-ap-mod-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading StardewArchipelago {version}..."));
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
                        int pct = (int)(5 + 70 * downloaded / total);
                        progress.Report((pct, $"Downloading StardewArchipelago... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((80, "Installing the mod into your Mods folder..."));
            Directory.CreateDirectory(modsDir);

            // Replace any existing StardewArchipelago folder so an update is clean.
            string canonical = Path.Combine(modsDir, "StardewArchipelago");
            if (Directory.Exists(canonical))
            {
                try { Directory.Delete(canonical, recursive: true); } catch { /* in-use — overwrite below */ }
            }

            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, modsDir, overwriteFiles: true);

            // Defensive: if the zip did NOT nest a top folder (manifest landed at
            // Mods root), move the loose files into Mods\StardewArchipelago so the
            // mod sits in its own sub-folder as SMAPI requires.
            if (!Directory.Exists(canonical) &&
                File.Exists(Path.Combine(modsDir, "manifest.json")))
            {
                Directory.CreateDirectory(canonical);
                foreach (string entry in Directory.EnumerateFileSystemEntries(modsDir))
                {
                    string name = Path.GetFileName(entry);
                    if (string.Equals(name, "StardewArchipelago", StringComparison.OrdinalIgnoreCase))
                        continue;
                    // Only relocate the freshly-extracted manifest/files, not other mods.
                    // Heuristic: move files at the Mods root (a normal install has
                    // only sub-folders at the Mods root, so loose files are ours).
                    if (File.Exists(entry))
                    {
                        try { File.Move(entry, Path.Combine(canonical, name), overwrite: true); } catch { }
                    }
                }
            }

            progress.Report((95, "Mod files installed."));
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
    // (same approach as Doom1993Plugin / JakAndDaxterPlugin).

    private sealed class StardewSettings
    {
        public string? InstallDirOverride { get; set; }
    }

    private StardewSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<StardewSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(StardewSettings s)
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

    private string? LoadOverrideInstallDir() => LoadSettings().InstallDirOverride;

    private void SaveOverrideInstallDir(string dir)
    {
        var s = LoadSettings();
        s.InstallDirOverride = dir;
        SaveSettings(s);
    }
}
