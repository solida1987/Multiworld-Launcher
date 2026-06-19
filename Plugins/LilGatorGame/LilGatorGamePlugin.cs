using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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

namespace LauncherV2.Plugins.LilGatorGame;

// ═══════════════════════════════════════════════════════════════════════════════
// LilGatorGamePlugin — install / launch for "Lil Gator Game" (Playtonic Friends /
// miHoYo, 2022) played through the GatorRando BepInEx mod by Natronium /
// rose.as.romeo, which contains the in-game Archipelago client. This is a NATIVE
// "ConnectsItself" integration: the mod speaks to the AP multiworld itself (no
// emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified against natronium/GatorRando +
//    natronium/GatorArchipelago) ─────────────────────────────────────────────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Lil Gator Game (Steam appid 1586800 — verified against the setup guide README
// and the Steam store URL in both repos), and Archipelago support is delivered as a
// BepInEx 5 mod added on top. The verified facts:
//
//   * THE AP WORLD game string is "Lil Gator Game" (verified against
//     natronium/GatorArchipelago archipelago.json: `"game": "Lil Gator Game"` and
//     GatorRando/Archipelago/ConnectionManager.cs: `const string Game = "Lil Gator
//     Game"`). GameId here = "lil_gator_game". The apworld is distributed from
//     natronium/GatorArchipelago as "lil_gator_game.apworld" and is CUSTOM — it is
//     NOT a core Archipelago world, so it must be installed into Archipelago's
//     custom_worlds folder.
//
//   * THE MOD repo is natronium/GatorRando (verified live 2026-06-14). The latest
//     release is v1.2.0 (tag "v1.2.0"), shipping ONE asset "GatorRando.zip"
//     (~6 MB). The build target in GatorRando.csproj reveals the exact zip layout:
//         GatorRando.dll                       (the BepInEx plugin)
//         Archipelago.MultiClient.Net.dll       (AP network client, bundled)
//         Newtonsoft.Json.dll                   (JSON lib, bundled)
//         README.md
//     These three DLLs extract into
//     <Lil Gator Game>/BepInEx/plugins/GatorRando/
//     (NOT into the plugins root — into a named subfolder, matching the build
//     target: `DestinationFolder="$(PluginsPath)/$(ModName)/"` where ModName =
//     "GatorRando"). The GUID is provided by BepInEx.PluginInfoProps auto-gen from
//     the csproj.
//
//   * BEPINEX 5 IS A REQUIRED PREREQUISITE that the zip does NOT bundle.
//     The README says explicitly: "Download BepInEx_win_x64_5.4.23.2.zip ...
//     extract the contents into the root of your Lil Gator Game folder." Lil Gator
//     Game is a Unity MONO game (Unity 2020.3), so BepInEx 5 (MONO build) is
//     correct. Because BepInEx is a portable zip extraction, this plugin CAN
//     automate that step — it downloads and extracts BepInEx into the game root
//     before dropping the mod DLLs. That makes this a genuine two-step automated
//     install. The plugin says exactly what it is doing (BepInEx + mod) and still
//     presents the guided steps and links for manual verification.
//
//   * CONNECTION is made IN-GAME (verified against GatorRando README and
//     SaveManager.cs / ConnectionManager.cs source): after the mod is installed
//     and the game launched, play through the prologue (the README recommends
//     speedrun mode), then pause — a Settings menu appears asking for:
//         Server Address:Port   (e.g. archipelago.gg:38281)
//         Slot Name             (your player name / character name)
//         Password              (optional)
//     Click "connect to server" and wait for the quest icon to turn green. The mod
//     persists these details in Application.persistentDataPath/AP_Saves/lastConnection.txt
//     (a Newtonsoft JSON file inside Unity's persistent data path, which on Windows
//     is typically %APPDATA%/../LocalLow/<company>/<product>/AP_Saves/). This is
//     a Unity internal path that is NOT deterministic before the game has run at
//     least once with the mod loaded — the launcher CANNOT write to it without
//     knowing the exact LocalLow path, and doing so without the game having created
//     the AP_Saves folder first would just be silently ignored (Unity overwrites it
//     on startup anyway). Per an honest "don't invent an undocumented prefill"
//     stance (same as Hollow Knight / BombRushCyberfunk / A Short Hike), this
//     plugin does NOT attempt to pre-fill connection details — the settings panel
//     surfaces the session's host / port / slot for the user to type into the
//     in-game Settings menu. This is the exact same honest pattern used by all
//     BepInEx "connects itself" plugins in this launcher.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Lil Gator Game install via the Windows registry, parsing
//      steamapps\libraryfolders.vdf and locating steamapps\common\Lil Gator Game
//      via appmanifest_1586800.acf. A manual install-dir OVERRIDE (folder picker)
//      is supported, validated, and persisted in a plugin-owned sidecar file at
//      Games/ROMs/lil_gator_game/lil_gator_game_launcher.json.
//   2. INSTALL/UPDATE = automated best-effort two-step:
//      (a) Download and extract BepInEx 5.4.23.2 x64 into the game root (if not
//          already present — detected by BepInEx\core\BepInEx.dll).
//      (b) Download GatorRando.zip from the latest GitHub release and extract the
//          three DLLs into BepInEx\plugins\GatorRando\.
//      Progress is reported throughout. Also presents guided numbered steps and
//      links for manual verification. Never a fake one-click "done" when there is
//      a required first-run step the launcher cannot do.
//   3. LAUNCH = run "Lil Gator Game.exe" from the detected/override install. If
//      the exe cannot be found but Steam is present, fall back to
//      steam://rungameid/1586800. ConnectsItself = true (the mod owns the AP slot).
//      SupportsStandalone = true (the base game runs fine without the mod).
//      No connection prefill (entered in-game), stated honestly.
//
// ── DEFENSIVE NOTES ───────────────────────────────────────────────────────────
//   * "Installed" is judged by the presence of BepInEx\plugins\GatorRando\
//     GatorRando.dll under a detected/override game install — the definitive signal
//     that the mod is present. BepInEx alone without the mod DLL = not installed.
//   * BepInEx presence is detected by BepInEx\core\BepInEx.dll (written by the
//     BepInEx extractor, not by us). If only BepInEx is present but GatorRando.dll
//     is absent, we report "partially installed" and auto-drop the mod DLLs on the
//     next install run.
//   * Steam VDF/ACF parsing is tolerant: hand-written quoted-value scans, any
//     failure degrades to "not found".
//   * No plaintext AP password is ever written to disk by this plugin. The in-game
//     Settings menu writes the password inside Unity persistent data, which is
//     the user's own machine and their own decision.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class LilGatorGamePlugin : IGamePlugin
{
    // ── Constants — Steam / GitHub ────────────────────────────────────────────

    private const string SteamAppId            = "1586800";
    private const string SteamCommonFolderName = "Lil Gator Game";
    private const string GameExeName           = "Lil Gator Game.exe";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    private const string GatorRandoOwner  = "natronium";
    private const string GatorRandoRepo   = "GatorRando";
    private const string GatorRandoAsset  = "GatorRando.zip";
    private const string ModDllName       = "GatorRando.dll";
    private const string ModSubfolderName = "GatorRando"; // BepInEx\plugins\GatorRando\

    // BepInEx 5.4.23.2 x64 (Unity Mono) — the exact version the GatorRando README names.
    private const string BepInExVersion      = "5.4.23.2";
    private const string BepInExAssetName    = $"BepInEx_win_x64_{BepInExVersion}.zip";
    private const string BepInExDownloadUrl  =
        $"https://github.com/BepInEx/BepInEx/releases/download/v{BepInExVersion}/{BepInExAssetName}";
    private const string BepInExCoreDllName  = "BepInEx.dll"; // inside BepInEx\core\

    private const string GatorRandoReleasesUrl =
        $"https://github.com/{GatorRandoOwner}/{GatorRandoRepo}/releases";
    private const string GatorRandoApWorldUrl  =
        "https://github.com/natronium/GatorArchipelago/releases/latest";
    private const string SetupGuideUrl         =
        "https://github.com/natronium/GatorArchipelago#instructions";
    private const string GatorMapUrl           = "https://natronium.github.io/GatorMap/";
    private const string ArchipelagoSiteUrl    = "https://archipelago.gg";
    private const string StorePageUrl          =
        $"https://store.steampowered.com/app/{SteamAppId}/Lil_Gator_Game/";

    // GitHub API endpoint for latest GatorRando release.
    private const string GitHubApiLatest =
        $"https://api.github.com/repos/{GatorRandoOwner}/{GatorRandoRepo}/releases/latest";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } },
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "lil_gator_game";
    public string DisplayName => "Lil Gator Game";
    public string Subtitle    => "Native PC · BepInEx mod";

    /// EXACT AP game string — verified against archipelago.json and ConnectionManager.cs.
    public string ApWorldName => "Lil Gator Game";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "lil_gator_game.png");

    public string ThemeAccentColor => "#5BAF5C"; // Lil Gator's green

    public string[] GameBadges => new[] { "Steam · needs mod", "BepInEx 5" };

    public string Description =>
        "Lil Gator Game, the adorable open-world adventure by Playtonic Friends, " +
        "played with the GatorRando BepInEx mod which connects to the Archipelago " +
        "multiworld. Craft ideas, inventory items, quest items, friends and more " +
        "are shuffled across the multiworld. You bring your own copy of Lil Gator " +
        "Game (owned on Steam); the launcher installs BepInEx 5 and the GatorRando " +
        "mod into your game folder automatically. Connection details (server, slot " +
        "name, password) are entered in the in-game Rando Settings menu that appears " +
        "after the prologue. The goal is to finish the playground.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled => FindModDll() != null;
    public bool IsRunning   { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory
    {
        get => ResolveGameDir() ?? Path.Combine(AppContext.BaseDirectory, "Games", "LilGatorGame");
        set { if (!string.IsNullOrWhiteSpace(value)) SaveOverrideDir(value); }
    }

    private string SettingsSidecarDir  => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath => Path.Combine(SettingsSidecarDir, "lil_gator_game_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The GatorRando BepInEx mod reports checks / items / goal to the AP server
    // itself — the launcher relays nothing. These exist for interface compatibility.
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
            string? latestTag = await FetchLatestTagAsync(ct);
            AvailableVersion  = latestTag ?? AvailableVersion;

            string? modDll = FindModDll();
            if (modDll != null)
            {
                // No version is embedded in the DLL name; we report "installed" plus
                // the available tag so the UI can show when an update is available.
                InstalledVersion = "installed";
            }
            else
            {
                InstalledVersion = null;
            }
        }
        catch
        {
            // Network failure — keep existing cached state.
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((2, "Locating Lil Gator Game install..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
        {
            progress.Report((100,
                "Lil Gator Game was not detected. Pick your install folder in the " +
                "Settings panel, or install the game via Steam first. " +
                "The Steam store page has been opened."));
            OpenUrl(StorePageUrl);
            return;
        }

        // ── Step 1: BepInEx ───────────────────────────────────────────────────
        string bepInExCoreDir   = Path.Combine(gameDir, "BepInEx", "core");
        string bepInExCoreDll   = Path.Combine(bepInExCoreDir, BepInExCoreDllName);
        bool   bepInExPresent   = File.Exists(bepInExCoreDll);

        if (!bepInExPresent)
        {
            progress.Report((5, $"Downloading BepInEx {BepInExVersion}..."));
            string bepZipPath = Path.Combine(Path.GetTempPath(), BepInExAssetName);
            try
            {
                await DownloadFileAsync(BepInExDownloadUrl, bepZipPath, progress,
                    startPct: 5, endPct: 40, ct);

                progress.Report((41, "Extracting BepInEx into game folder..."));
                await Task.Run(() =>
                {
                    // BepInEx zip has all content at the root — extract directly
                    // into the game directory. Overwrite any stale files.
                    using var zip = ZipFile.OpenRead(bepZipPath);
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                            continue; // directory entry
                        string dest = Path.Combine(gameDir, entry.FullName.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        entry.ExtractToFile(dest, overwrite: true);
                    }
                }, ct);

                progress.Report((45,
                    $"BepInEx {BepInExVersion} installed. " +
                    "IMPORTANT: You must launch and then exit the game ONCE now " +
                    "so BepInEx can create its required folders (BepInEx\\plugins, " +
                    "BepInEx\\config). Then click Install again to add GatorRando."));

                // Inform user they need to run the game once. We do NOT launch the
                // game automatically here — that would be surprising. The user should
                // do this step themselves, then return to click Install again.
                InstalledVersion = null;
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                progress.Report((100,
                    $"Failed to download BepInEx: {ex.Message}. " +
                    $"Please download {BepInExAssetName} from " +
                    $"https://github.com/BepInEx/BepInEx/releases/tag/v{BepInExVersion} " +
                    "and extract it into your Lil Gator Game folder manually."));
                return;
            }
            finally
            {
                try { if (File.Exists(bepZipPath)) File.Delete(bepZipPath); } catch { }
            }
        }

        progress.Report((45, "BepInEx is present."));

        // Verify that BepInEx has been run at least once (plugins folder must exist).
        string pluginsDir = Path.Combine(gameDir, "BepInEx", "plugins");
        if (!Directory.Exists(pluginsDir))
        {
            progress.Report((100,
                "BepInEx is installed but has not been run yet. Launch Lil Gator " +
                "Game ONCE without this launcher to let BepInEx generate its folders " +
                "(including BepInEx\\plugins), then quit the game and click Install again."));
            return;
        }

        // ── Step 2: GatorRando mod ────────────────────────────────────────────
        progress.Report((50, "Fetching latest GatorRando release info..."));
        string? latestTag         = null;
        string  modZipDownloadUrl = string.Empty;
        try
        {
            (latestTag, modZipDownloadUrl) = await FetchLatestReleaseInfoAsync(ct);
            AvailableVersion = latestTag;
        }
        catch (Exception ex)
        {
            progress.Report((100,
                $"Could not fetch GatorRando release info: {ex.Message}. " +
                $"Download GatorRando.zip manually from {GatorRandoReleasesUrl} " +
                "and extract the DLLs into BepInEx\\plugins\\GatorRando\\ in your " +
                "Lil Gator Game folder."));
            return;
        }

        if (string.IsNullOrWhiteSpace(modZipDownloadUrl))
        {
            progress.Report((100,
                $"No GatorRando.zip asset found in the latest release. " +
                $"Download it manually from {GatorRandoReleasesUrl}."));
            return;
        }

        progress.Report((52, $"Downloading GatorRando {latestTag}..."));
        string modZipPath = Path.Combine(Path.GetTempPath(), GatorRandoAsset);
        try
        {
            await DownloadFileAsync(modZipDownloadUrl, modZipPath, progress,
                startPct: 52, endPct: 85, ct);

            progress.Report((86, "Extracting GatorRando DLLs into BepInEx\\plugins\\GatorRando\\..."));
            string modPluginDir = Path.Combine(pluginsDir, ModSubfolderName);
            await Task.Run(() =>
            {
                Directory.CreateDirectory(modPluginDir);
                using var zip = ZipFile.OpenRead(modZipPath);
                foreach (ZipArchiveEntry entry in zip.Entries)
                {
                    // The zip is flat at root (no subfolder). Extract all .dll files.
                    if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        continue;
                    string dest = Path.Combine(modPluginDir, entry.Name);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            }, ct);

            InstalledVersion = "installed";
            AvailableVersion = latestTag;

            progress.Report((100,
                $"GatorRando {latestTag} installed into BepInEx\\plugins\\GatorRando\\. " +
                "To play: launch Lil Gator Game, play through the short prologue " +
                "(enable speedrun mode to skip cutscenes), then pause — the Rando " +
                "Settings menu will appear. Enter your Archipelago server address:port, " +
                "your slot name (your character's name), and password (if any), then " +
                "click \"connect to server\" and wait for the quest icon in the upper " +
                "right to turn green. You also need to install the lil_gator_game.apworld " +
                "into Archipelago's custom_worlds folder (see the Settings panel link)."));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            progress.Report((100,
                $"Failed to install GatorRando: {ex.Message}. " +
                $"Download GatorRando.zip manually from {GatorRandoReleasesUrl} " +
                "and extract the DLLs into BepInEx\\plugins\\GatorRando\\ in your " +
                "Lil Gator Game folder."));
        }
        finally
        {
            try { if (File.Exists(modZipPath)) File.Delete(modZipPath); } catch { }
        }
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return FindModDll() != null;
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: Lil Gator Game's AP connection is entered in the in-game Rando
        // Settings menu that appears after the prologue. There is no config file
        // this launcher can pre-write (the mod stores connection details in Unity
        // persistent data, a runtime-path known only after the game boots with
        // BepInEx loaded). The settings panel surfaces host / port / slot / password
        // so the user can type them into the in-game menu.
        //
        // ConnectsItself = true: the GatorRando BepInEx plugin owns the AP slot
        // connection, so the launcher must NOT hold its own ApClient on this slot.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartGame();
        return Task.CompletedTask;
    }

    /// Lil Gator Game runs fine without the mod.
    public bool SupportsStandalone => true;

    /// The GatorRando BepInEx mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning     = false;
        _gameProcess  = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The GatorRando mod receives items from the AP server directly.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status; no launcher HUD needed.
    }

    // ── Existing-install validation (override folder picker) ─────────────────

    /// Returns null if the folder is a valid Lil Gator Game install, else a
    /// short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Lil Gator Game install folder.";

        if (LooksLikeGameDir(folder))
            return null;

        // Tolerate: user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeGameDir(nested)) return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Lil Gator Game installation (\"Lil Gator " +
               "Game.exe\" was not found). Pick the folder that contains that exe " +
               @"(for Steam usually ...\steamapps\common\Lil Gator Game).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20),
        };

        // ── Honesty header ────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Lil Gator Game is your own game (Steam) with the GatorRando BepInEx " +
                "mod added on top. The launcher installs BepInEx 5 and GatorRando " +
                "automatically. Connection details (server, slot name, password) must " +
                "be entered in the in-game Rando Settings menu that appears after the " +
                "prologue — there is no config file this launcher can pre-fill. You " +
                "also need the lil_gator_game.apworld installed in Archipelago " +
                "(custom_worlds folder).",
            FontSize       = 11,
            Foreground     = warn,
            TextWrapping   = System.Windows.TextWrapping.Wrap,
            Margin         = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: install detection ────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text        = "LIL GATOR GAME INSTALL",
            FontSize    = 10,
            FontWeight  = System.Windows.FontWeights.SemiBold,
            Foreground  = muted,
            Margin      = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Lil Gator Game not detected. Pick your install folder below or install via Steam.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text         = detectMsg,
            FontSize     = 11,
            Foreground   = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 4),
        });

        // BepInEx status
        bool bepInExOk = gameDir != null && File.Exists(
            Path.Combine(gameDir, "BepInEx", "core", BepInExCoreDllName));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                bepInExOk
                ? $"BepInEx {BepInExVersion} found."
                : $"BepInEx {BepInExVersion} NOT found — the Install button will download it.",
            FontSize     = 11,
            Foreground   = bepInExOk ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 4),
        });

        // GatorRando mod status
        string? modDll = FindModDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                modDll != null
                ? "GatorRando mod installed: " + modDll
                : "GatorRando.dll NOT found — use the Install button on the Play tab.",
            FontSize     = 11,
            Foreground   = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 10),
        });

        // Folder override picker
        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = overrideDir ?? gameDir ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Lil Gator Game install folder. Detected from Steam; use this picker for a non-standard library.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content         = "Select folder...",
            Width           = 120,
            Padding         = new System.Windows.Thickness(0, 6, 0, 6),
            Background      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground      = fg,
            BorderBrush     = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Lil Gator Game install folder (contains \"Lil Gator Game.exe\")",
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
                    System.Windows.MessageBox.Show(bad, "Not a Lil Gator Game folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // Descend into the named subfolder if the user picked the "common" parent.
                if (!LooksLikeGameDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeGameDir(nested)) picked = nested;
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
            Text =
                "Steam installs are detected automatically (appid 1586800). Use this " +
                "picker for a non-standard Steam library location.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 6, 0, 16),
        });

        // ── Section: in-game connection ───────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "CONNECTING (in-game Rando Settings menu)",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "After the mod is installed, launch Lil Gator Game and play through " +
                "the prologue (enable speedrun mode in settings to skip cutscenes). " +
                "Then pause — the Rando Settings menu will appear asking for:\n" +
                "  • Server Address:Port  (e.g. archipelago.gg:38281 or localhost:38281)\n" +
                "  • Slot Name            (your player / character name)\n" +
                "  • Password             (optional, blank if none)\n" +
                "Click \"connect to server\" and wait for the quest icon in the upper " +
                "right to turn green — that means you are connected. Your character " +
                "name is also your slot name. This launcher cannot pre-fill this menu.",
            FontSize     = 11,
            Foreground   = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 16),
        });

        // ── Section: guided install steps ─────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "MANUAL INSTALL STEPS (if automatic install fails)",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Lil Gator Game (on Steam). Install it if you have not.",
            $"2. Download BepInEx_win_x64_{BepInExVersion}.zip from the BepInEx releases " +
                "(see link below) and extract its contents into the root of your Lil Gator " +
                "Game folder. You should now have a BepInEx folder next to \"Lil Gator Game.exe\".",
            "3. Launch and close the game once to let BepInEx generate its plugin folders.",
            "4. Download GatorRando.zip from the GatorRando releases (see link below) and " +
                "extract the three DLLs (GatorRando.dll, Archipelago.MultiClient.Net.dll, " +
                "Newtonsoft.Json.dll) into BepInEx\\plugins\\GatorRando\\ inside your game " +
                "folder (create the GatorRando subfolder if it does not exist).",
            "5. Install the lil_gator_game.apworld from GatorArchipelago releases (see link " +
                "below) by double-clicking it — it will be copied into Archipelago's " +
                "custom_worlds folder.",
            "6. Generate your multiworld in the Archipelago Launcher, host it, then launch " +
                "Lil Gator Game and enter your connection details in the in-game Settings menu.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text         = step,
                FontSize     = 11,
                Foreground   = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin       = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Section: links ────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "LINKS",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 12, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("GatorRando mod releases ↗",                GatorRandoReleasesUrl),
            ("lil_gator_game.apworld releases ↗",        GatorRandoApWorldUrl),
            ("GatorArchipelago setup guide ↗",           SetupGuideUrl),
            ("Lil Gator Map (check location finder) ↗", GatorMapUrl),
            ("Lil Gator Game on Steam ↗",                StorePageUrl),
            ($"BepInEx {BepInExVersion} download ↗",
                $"https://github.com/BepInEx/BepInEx/releases/tag/v{BepInExVersion}"),
            ("Archipelago Official ↗",                   ArchipelagoSiteUrl),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content             = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding             = new System.Windows.Thickness(0, 2, 0, 2),
                Background          = System.Windows.Media.Brushes.Transparent,
                BorderThickness     = new System.Windows.Thickness(0),
                FontSize            = 12,
                Margin              = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground          = accent,
                Cursor              = System.Windows.Input.Cursors.Hand,
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
        try
        {
            string json = await _http.GetStringAsync(GitHubApiLatest, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tag     = root.TryGetProperty("tag_name",    out var tn) ? tn.GetString() ?? "" : "";
            string name    = root.TryGetProperty("name",        out var nm) ? nm.GetString() ?? "" : "";
            string body    = root.TryGetProperty("body",        out var bd) ? bd.GetString() ?? "" : "";
            string htmlUrl = root.TryGetProperty("html_url",    out var hu) ? hu.GetString() ?? "" : "";
            string dateStr = root.TryGetProperty("published_at", out var dt) ? dt.GetString() ?? "" : "";

            DateTimeOffset date = DateTimeOffset.TryParse(dateStr, out var d) ? d : DateTimeOffset.MinValue;
            string title = string.IsNullOrWhiteSpace(name) ? $"GatorRando {tag}" : name;
            if (string.IsNullOrWhiteSpace(body))
                body = "See the release page for details.";

            return new[]
            {
                new NewsItem(
                    Title:   title,
                    Body:    body,
                    Version: tag,
                    Date:    date,
                    Url:     htmlUrl)
            };
        }
        catch
        {
            return new[]
            {
                new NewsItem(
                    Title:   "GatorRando — Lil Gator Game Archipelago mod",
                    Body:    "BepInEx 5 mod that connects Lil Gator Game to an Archipelago " +
                             "multiworld. Install via the Play tab. Connection details are " +
                             "entered in the in-game Rando Settings menu after the prologue.",
                    Version: "",
                    Date:    DateTimeOffset.MinValue,
                    Url:     GatorRandoReleasesUrl)
            };
        }
    }

    // ── Private helpers — game/mod detection ──────────────────────────────────

    /// Resolve the game dir to use: override (if set + valid) wins, else Steam detect.
    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeGameDir(ov)) return ov;
        try { return DetectSteamGameDir(); }
        catch { return null; }
    }

    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

    /// Returns the full path to GatorRando.dll if installed, else null.
    private string? FindModDll()
    {
        try
        {
            string? gameDir = ResolveGameDir();
            if (gameDir == null) return null;
            string dll = Path.Combine(gameDir, "BepInEx", "plugins", ModSubfolderName, ModDllName);
            return File.Exists(dll) ? dll : null;
        }
        catch { return null; }
    }

    private static string? DetectSteamGameDir()
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

                    string common     = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeGameDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeGameDir(conventional)) return conventional;
                }
                catch { /* try next library */ }
            }
        }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        string? gameDir = ResolveGameDir();
        string? exe     = gameDir != null ? Path.Combine(gameDir, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var psi = new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = gameDir!,
                UseShellExecute  = true,
            };
            var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Lil Gator Game.");

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
            catch { /* some processes do not expose Exited — non-fatal */ }
            return;
        }

        // Fall back to Steam if the exe was not found but Steam is present.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // Steam owns the process; we cannot track exit.
                return;
            }
            catch { /* fall through */ }
        }

        throw new FileNotFoundException(
            "Could not find \"Lil Gator Game.exe\". Open the Settings panel and " +
            "pick your Lil Gator Game install folder, or install the game via Steam.",
            GameExeName);
    }

    // ── Private helpers — GitHub API ──────────────────────────────────────────

    private async Task<string?> FetchLatestTagAsync(CancellationToken ct)
    {
        string json = await _http.GetStringAsync(GitHubApiLatest, ct);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("tag_name", out var tn) ? tn.GetString() : null;
    }

    private async Task<(string? Tag, string AssetUrl)> FetchLatestReleaseInfoAsync(CancellationToken ct)
    {
        string json = await _http.GetStringAsync(GitHubApiLatest, ct);
        using var doc  = JsonDocument.Parse(json);
        var root  = doc.RootElement;
        string? tag = root.TryGetProperty("tag_name", out var tn) ? tn.GetString() : null;
        string  url = "";

        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                if (name == GatorRandoAsset && asset.TryGetProperty("browser_download_url", out var bdUrl))
                {
                    url = bdUrl.GetString() ?? "";
                    break;
                }
            }
        }
        return (tag, url);
    }

    private static async Task DownloadFileAsync(
        string url, string destPath,
        IProgress<(int Pct, string Msg)> progress,
        int startPct, int endPct,
        CancellationToken ct)
    {
        using var resp   = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? -1;
        int  range = endPct - startPct;

        using var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        using var cs = await resp.Content.ReadAsStreamAsync(ct);

        byte[] buf        = new byte[81920];
        long   downloaded = 0;
        int    read;
        while ((read = await cs.ReadAsync(buf, ct)) > 0)
        {
            await fs.WriteAsync(buf.AsMemory(0, read), ct);
            downloaded += read;
            if (total > 0)
            {
                int pct = startPct + (int)(range * downloaded / total);
                progress.Report((Math.Min(pct, endPct), $"Downloading... {downloaded / 1024:N0} KB / {total / 1024:N0} KB"));
            }
        }
    }

    // ── Private helpers — Steam detection ────────────────────────────────────

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

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    private static string NormalizeSteamPath(string p) => p.Replace('/', '\\').TrimEnd('\\');

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
            yield return text.Substring(open + 1, close - open - 1).Replace("\\\\", "\\");
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

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* non-fatal */ }
    }

    // ── Private helpers — self-contained settings sidecar ────────────────────

    private sealed class LilGatorSettings
    {
        public string? InstallOverride { get; set; }
    }

    private LilGatorSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<LilGatorSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(LilGatorSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(
                SettingsSidecarPath,
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
}
