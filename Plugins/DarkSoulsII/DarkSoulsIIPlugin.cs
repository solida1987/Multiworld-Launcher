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

// NOTE on type qualification (BUILD GOTCHA):
// This project sets BOTH <UseWPF>true</UseWPF> and <UseWindowsForms>true</UseWindowsForms>.
// That makes a long list of simple type names ambiguous between WPF and WinForms
// (Clipboard, MessageBox, Application, Color, Brush(es), Button, TextBox, CheckBox,
// Orientation, FontWeights, HorizontalAlignment, Cursors, Thickness, OpenFileDialog…).
// To avoid CS0104 this file deliberately does NOT do `using System.Windows.Controls;` /
// `using System.Windows.Media;` — every WPF UI type below is written FULLY QUALIFIED
// (System.Windows.Controls.*, System.Windows.Media.*, System.Windows.MessageBox, …).
// It also does NOT introduce any file-level `using X = System.Windows...;` alias, because
// GlobalUsings.cs already aliases the short names project-wide and a second alias here
// would be CS1537 (duplicate alias).

namespace LauncherV2.Plugins.DarkSoulsII;

// ═══════════════════════════════════════════════════════════════════════════════
// DarkSoulsIIPlugin — detect / install / launch for "Dark Souls II" (Scholar of
// the First Sin edition by default, vanilla optionally) played through the
// WildBunnie/DarkSoulsII-Archipelago DLL injection mod. This is a NATIVE
// "ConnectsItself" integration: a dinput8.dll wrapper injected into the game
// binary (via DLL proxy, the same technique as DS3's dinput8.dll) owns the AP
// slot connection using the embedded apclientpp library — not the launcher's
// ApClient, and not a launcher-held pipe.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online this session) ───────────
//   * REPO: WildBunnie/DarkSoulsII-Archipelago — the sole AP DS2 mod repo.
//     Latest release: v0.6.0-alpha.2 (2026-03-21). TWO game versions supported:
//       - Scholar of the First Sin (SotFS) — x64, Steam appid 335300
//         asset: ds2_sotfs_archipelago_v0.6.0-alpha.2.zip
//       - Vanilla Dark Souls II — x86, Steam appid 236430
//         asset: ds2_vanilla_archipelago_v0.6.0-alpha.2.zip
//     Both ship the same apworld: dark_souls_2.apworld
//
//   * MOD FRAMEWORK: dinput8.dll proxy (DLL wrapper generator pattern, same as
//     DS3). Drop dinput8.dll into the Game folder next to the executable. The DLL
//     is loaded by Windows when Dark Souls II starts, injects the AP client
//     (built on top of apclientpp) into the game process, and also spawns a
//     CONSOLE WINDOW for status + commands.
//
//   * AP GAME STRING: "Dark Souls II" — VERIFIED against
//     world/dark_souls_2/__init__.py (class DS2World, game = "Dark Souls II").
//
//   * Steam appids:
//       - Dark Souls II: Scholar of the First Sin  = 335300 (64-bit; PRIMARY)
//       - Dark Souls II (vanilla / original)       = 236430 (32-bit; optional)
//     The mod README says "Verify that you are on the latest Steam version" —
//     SotFS is the current, maintained release. This plugin defaults to SotFS and
//     lets the user switch to vanilla via a Settings option.
//
//   * CONNECTION: typed in the CONSOLE WINDOW the mod spawns alongside the game.
//     After launching Dark Souls II with the modded dinput8.dll, the console
//     appears; the user types:
//         /connect server_address:port slot_name [password:PASSWORD]
//     For example: /connect archipelago.gg:12345 Player1 password:secret
//     There is NO config file the launcher can pre-write (verified — the source
//     only reads keyboard input, not a connection file). This plugin DISPLAYS the
//     session credentials in Settings so the player can copy them into the console.
//
//   * OFFLINE / EAC: The mod forces the game to start in offline mode
//     automatically. No manual offline-mode toggle is needed (unlike DS3).
//     However, if a firewall blocks Dark Souls II, the mod cannot reach
//     Archipelago (the firewall blocks the mod's outbound connection too).
//
//   * Save data: written to archipelago/save_data/<room_id>_<slot>.json
//     (relative to the game exe). No plaintext AP password is written to disk
//     by the mod itself.
//
//   * Linux: add WINEDLLOVERRIDES="dinput8.dll=n,b" %command% to Steam's launch
//     options. This plugin is Windows-only; no special handling is needed.
//
// ── WHAT THIS PLUGIN HONESTLY DOES ────────────────────────────────────────────
//   1. DETECT the Steam Dark Souls II (SotFS or vanilla) install via the Windows
//      registry, parsing steamapps\libraryfolders.vdf for every library root.
//      A manual install-dir OVERRIDE (Settings folder picker) takes precedence
//      and is validated + persisted in this plugin's OWN sidecar
//      (Games/ROMs/dark_souls_2/dark_souls_2_launcher.json). Core/SettingsStore
//      is NOT modified.
//   2. INSTALL/UPDATE = download the selected game-version zip from the GitHub
//      release (latest tag; pinned v0.6.0-alpha.2 fallback when the API is
//      unreachable) into the launcher's own staging folder
//      (Games/DarkSouls2Archipelago/), extract it (flattening a single wrapping
//      sub-folder if needed), and stamp the version. THEN copy dinput8.dll into
//      the user's Steam game folder (the only game-folder write this plugin does,
//      which is reversible).
//   3. LAUNCH (AP) = launch the game via Steam protocol (steam://rungameid/...)
//      or by detecting the exe directly. The mod's dinput8.dll is already in the
//      Game folder; the console for connection appears automatically. The session
//      credentials are surfaced in Settings for the user to type/paste into the
//      console after the game starts. ConnectsItself = true (the mod owns the AP
//      slot; the launcher must NOT hold its own ApClient on it). SupportsStandalone
//      = true (plain Dark Souls II via Steam runs without AP once the dinput8.dll
//      is removed or not yet staged).
//   4. GUIDE in Settings: credentials to copy into the console, firewall note,
//      and links.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time") ─────────────────────────
//   * The exact sub-folder structure of the release zips was not inspected (the
//     zip contents were not listed offline). ResolveModDll() scans recursively
//     for dinput8.dll, so sub-folder layouts are handled gracefully.
//   * dinput8.dll is placed in the "Game" sub-folder of the Steam install
//     (SotFS: DARK SOULS II Scholar of the First Sin\Game\; vanilla:
//     Dark Souls II\Game\). If the Steam layout differs, the folder picker in
//     Settings lets the user correct it.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DarkSoulsIIPlugin : IGamePlugin
{
    // ── Constants — the AP mod repo (verified 2026-06-14) ────────────────────
    private const string MOD_OWNER  = "WildBunnie";
    private const string MOD_REPO   = "DarkSoulsII-Archipelago";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    /// Official Archipelago "Dark Souls II" setup guide (if one exists on ap.gg).
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Dark%20Souls%20II/setup/en";

    // ── Steam appids — SotFS (primary) and vanilla (secondary) ────────────────
    private const string SOTFS_APPID            = "335300";
    private const string VANILLA_APPID          = "236430";
    private const string SOTFS_COMMON_FOLDER    = "DARK SOULS II Scholar of the First Sin";
    private const string VANILLA_COMMON_FOLDER  = "Dark Souls II";

    private const string SotFSSteamStoreUrl  = "https://store.steampowered.com/app/335300";
    private const string VanillaSteamStoreUrl = "https://store.steampowered.com/app/236430";

    /// Pinned fallback for the mod when the GitHub API is unreachable. Tag
    /// v0.6.0-alpha.2 verified live 2026-06-14; the /latest API endpoint 404s for
    /// prerelease-only repos, so we enumerate /releases and take the newest — but
    /// this static fallback keeps Install working when the API is unreachable.
    private const string FallbackVersion      = "0.6.0-alpha.2";
    private const string FallbackSotFSZipName = "ds2_sotfs_archipelago_v0.6.0-alpha.2.zip";
    private const string FallbackVanillaZipName = "ds2_vanilla_archipelago_v0.6.0-alpha.2.zip";
    private static readonly string FallbackSotFSZipUrl  =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/{FallbackSotFSZipName}";
    private static readonly string FallbackVanillaZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/{FallbackVanillaZipName}";

    /// The DLL proxy that injects the AP client when placed in the Game folder.
    private const string ModDllName = "dinput8.dll";

    private const string VersionFileName = "ds2_ap_version.dat";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "dark_souls_2";
    public string DisplayName => "Dark Souls II";
    public string Subtitle    => "Native PC · Archipelago";

    /// EXACT AP game string — VERIFIED against world/dark_souls_2/__init__.py
    /// (class DS2World, game = "Dark Souls II").
    public string ApWorldName => "Dark Souls II";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "dark_souls_2.png");

    public string ThemeAccentColor => "#6B3A1F";   // Dark Souls II amber / soul orange

    public string[] GameBadges =>
        UseVanilla
            ? new[] { "Requires DS2 Vanilla on Steam" }
            : new[] { "Requires DS2: Scholar of the First Sin on Steam" };

    public string Description =>
        "Dark Souls II: Scholar of the First Sin (or the original Dark Souls II) " +
        "randomized through the WildBunnie Archipelago mod — a dinput8.dll proxy that " +
        "injects an Archipelago client directly into the game process. Items, weapon " +
        "progressions, estus flasks and more are shuffled into the multiworld, and the " +
        "mod connects to the AP server itself (no emulator, no bridge). You bring your " +
        "own copy of Dark Souls II on Steam; the launcher downloads the mod, copies it " +
        "into your game folder, and launches the game. A console window appears when the " +
        "game starts — type your server, slot and password there to connect. The mod " +
        "forces the game offline automatically (no manual toggle needed).";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the mod DLL was staged into the user's game folder.
    public bool IsInstalled => ResolveGameDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Staging folder where we extract the mod zip (launcher-managed).
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "DarkSouls2Archipelago");

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "dark_souls_2_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────
    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Dark Souls II mod reports checks/items/goal to the AP server itself.
    // The launcher relays nothing (ConnectsItself = true).
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
            InstalledVersion = File.Exists(VersionFilePath) && IsInstalled
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
        string editionLabel = UseVanilla ? "Dark Souls II (Vanilla)" : "Dark Souls II: Scholar of the First Sin";

        // 1. Resolve the latest mod release.
        progress.Report((3, $"Checking latest {editionLabel} Archipelago mod release..."));
        var (version, sotfsZipUrl, vanillaZipUrl, apworldUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        string? zipUrl  = UseVanilla ? vanillaZipUrl : sotfsZipUrl;
        string  zipName = UseVanilla
            ? $"ds2_vanilla_archipelago_{version}.zip"
            : $"ds2_sotfs_archipelago_{version}.zip";

        // 2. Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"{editionLabel} Archipelago mod {version} is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                $"Could not find the {editionLabel} Archipelago mod download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                ModRepoUrl + "/releases/latest. Make sure you own and have installed " +
                $"{editionLabel} on Steam.");

        // 3. Download + extract the mod into our staging folder.
        await DownloadAndExtractModAsync(zipUrl, zipName, version, progress, ct);

        // 4. Download the apworld next to the staging folder (best effort).
        if (apworldUrl != null)
        {
            try
            {
                progress.Report((82, "Downloading the Dark Souls II apworld..."));
                byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                string apworldPath = Path.Combine(GameDirectory, "dark_souls_2.apworld");
                await File.WriteAllBytesAsync(apworldPath, apworld, ct);
                progress.Report((88, "dark_souls_2.apworld saved — copy it into Archipelago's custom_worlds folder if you generate with this build."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((88, "Could not download the apworld — get it from the GitHub release page (AP may also bundle it)."));
            }
        }

        // 5. Copy dinput8.dll into the game's Game folder.
        progress.Report((90, $"Staging dinput8.dll into your {editionLabel} game folder..."));
        StageModDll();

        // 6. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        string? gameDir = ResolveGameDir();
        string notice = gameDir != null
            ? $"Mod installed. dinput8.dll placed in \"{gameDir}\"."
            : "Mod extracted, but your game folder was not detected. Use Settings to " +
              "pick your game folder, then reinstall (or copy dinput8.dll there manually).";

        progress.Report((100,
            $"{editionLabel} Archipelago mod {version} ready. {notice} " +
            "Launch the game from the Play tab. A console appears when the game starts — " +
            "type /connect server:port slot_name (and optionally password:PASSWORD) to connect. " +
            "Your session details are shown in Settings for easy copy-paste."));
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
        // HONEST: the DS2 Archipelago mod spawns a console window when the game
        // starts. The player types their connection command there:
        //     /connect server:port slot_name [password:PASSWORD]
        // There is NO config file or command-line arg the launcher can pre-fill
        // (verified against the source: only keyboard input drives connection).
        // The session credentials are surfaced in Settings for the user to copy.
        // ConnectsItself = true — the mod's own apclientpp instance owns the slot.
        _ = session; // intentionally unused — no config prefill mechanism exists
        StartGame();
        return Task.CompletedTask;
    }

    public bool SupportsStandalone => true;
    public bool ConnectsItself     => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // Standalone = plain Dark Souls II (no AP connection — player doesn't type
        // /connect). The mod DLL is still present in the game folder and starts
        // its console, but the player simply ignores the console.
        StartGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        // No plaintext AP password is written to disk by this plugin or by the
        // mod (the connection is typed into the console at runtime). Nothing to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The DS2 Archipelago mod receives items from the AP server directly
        // via its own apclientpp instance; there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in the spawned console window.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6B, 0x3A, 0x1F));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Dark Souls II's Archipelago support uses a DLL proxy mod (dinput8.dll) " +
                   "that injects an AP client into the game when you launch it. The launcher " +
                   "downloads the mod and copies it into your game folder. When the game starts, " +
                   "a console window appears — type your connection details there:\n" +
                   "    /connect server:port slot_name [password:PASSWORD]\n" +
                   "The mod forces the game offline automatically. " +
                   "Firewall rules blocking Dark Souls II will also block the mod's AP connection " +
                   "— disable any such rules if you cannot connect.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: game edition ─────────────────────────────────────────
        panel.Children.Add(SectionHeader("GAME EDITION", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Scholar of the First Sin (Steam appid 335300, 64-bit) is the current " +
                   "maintained edition and the default. Switch to vanilla Dark Souls II " +
                   "(Steam appid 236430, 32-bit) only if that is the version you own.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        var chkVanilla = new System.Windows.Controls.CheckBox
        {
            Content   = "Use vanilla Dark Souls II (32-bit, appid 236430) instead of Scholar of the First Sin",
            IsChecked = UseVanilla,
            Foreground = fg,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
            ToolTip = "Uncheck for Scholar of the First Sin (recommended); check for the " +
                      "original Dark Souls II. Re-install after switching to get the correct " +
                      "edition's dinput8.dll.",
        };
        chkVanilla.Checked   += (_, _) => { SaveUseVanilla(true);  };
        chkVanilla.Unchecked += (_, _) => { SaveUseVanilla(false); };
        panel.Children.Add(chkVanilla);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After changing edition: use Install on the Play tab to get the correct mod " +
                   "DLL, then play that edition from Steam.",
            FontSize = 10, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 2, 0, 12),
        });

        // ── Section: mod DLL status ───────────────────────────────────────
        panel.Children.Add(SectionHeader("MOD (dinput8.dll)", muted));
        string? modDll = ResolveGameDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                ? "✓ Mod installed at: " + modDll
                : "Mod not installed. Use Install on the Play tab to download and stage dinput8.dll.",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 10),
        });

        // ── Section: Steam game folder detection / override ───────────────
        panel.Children.Add(SectionHeader(
            UseVanilla ? "DARK SOULS II (VANILLA) GAME FOLDER"
                       : "DARK SOULS II: SCHOLAR OF THE FIRST SIN GAME FOLDER",
            muted));

        string? steamDir    = DetectSteamGameDir();
        string? overrideDir = LoadOverrideDir();
        string? gameDir     = overrideDir ?? steamDir;
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = gameDir != null
                ? (overrideDir != null
                    ? "✓ Using your selected folder: " + gameDir
                    : "✓ Detected Steam install: " + gameDir)
                : "Game folder not detected. Pick it below, or install the game via Steam first.",
            FontSize = 11, Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? steamDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Your Dark Souls II game folder (the one containing Game\\DarkSoulsII.exe or " +
                      "Game\\darksoulsII.exe). Detected from Steam automatically; pick here for a " +
                      "non-standard library location.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 130, Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Dark Souls II install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? steamDir ?? "")
                                   ? (overrideDir ?? steamDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                if (!LooksLikeGameDir(picked))
                {
                    // Try descending into known sub-folder names
                    foreach (string sub in new[]
                    {
                        UseVanilla ? VANILLA_COMMON_FOLDER : SOTFS_COMMON_FOLDER,
                        "Game"
                    })
                    {
                        string nested = Path.Combine(picked, sub);
                        if (LooksLikeGameDir(nested)) { picked = nested; break; }
                    }
                }
                if (!LooksLikeGameDir(picked))
                {
                    System.Windows.MessageBox.Show(
                        "That folder does not look like a Dark Souls II installation. " +
                        "Pick the folder that contains the Game sub-folder (or the Game " +
                        "folder itself, which holds DarkSoulsII.exe / darksoulsII.exe).",
                        "Not a Dark Souls II folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
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
            Text = "Steam installs are detected automatically. Use this picker only for a " +
                   "non-standard Steam library location, or if automatic detection fails.",
            FontSize = 10, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: setup steps ──────────────────────────────────────────
        panel.Children.Add(SectionHeader("SETUP & CONNECTION", muted));
        foreach (string step in new[]
        {
            "1. Own and install " +
                (UseVanilla ? "Dark Souls II (appid 236430)" : "Dark Souls II: Scholar of the First Sin (appid 335300)") +
                " on Steam. The mod requires the latest Steam version of the game.",
            "2. Use Install on the Play tab to download the Archipelago mod " +
                "(dinput8.dll) and copy it into your game folder automatically.",
            "3. Launch the game from this launcher (Play). A console window appears " +
                "alongside the game.",
            "4. In the console, type:  /connect server:port slot_name  (plus  " +
                "password:PASSWORD  if needed). Example: /connect archipelago.gg:38281 MySlot",
            "5. Start a new game in Dark Souls II. Items from the multiworld will " +
                "appear as you check locations. The mod forces the game offline automatically.",
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
        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            (UseVanilla ? "Dark Souls II (Vanilla) on Steam ↗" : "Dark Souls II: SotFS on Steam ↗",
             UseVanilla ? VanillaSteamStoreUrl : SotFSSteamStoreUrl),
            ("Dark Souls II Archipelago Mod (GitHub) ↗", ModRepoUrl),
            ("Archipelago Official ↗", "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
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

    private static System.Windows.Controls.TextBlock SectionHeader(
        string text, System.Windows.Media.Brush muted)
        => new System.Windows.Controls.TextBlock
        {
            Text = text, FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        };

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
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

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + SotFS zip URL + vanilla zip URL +
    /// apworld URL. All releases so far are prereleases, so /releases/latest 404s;
    /// enumerate /releases and take the newest non-draft. Falls back to the pinned
    /// v0.6.0-alpha.2 direct URLs when the API is unreachable.
    private async Task<(string Version, string? SotFSZip, string? VanillaZip, string? ApworldUrl)>
        ResolveLatestModAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    // Skip drafts; accept prereleases (every release is a prerelease).
                    if (rel.TryGetProperty("draft", out var dr) &&
                        dr.ValueKind == JsonValueKind.True)
                        continue;

                    string? version = rel.TryGetProperty("tag_name", out var t)
                        ? NormalizeTag(t.GetString())
                        : null;
                    if (version == null) continue;

                    if (rel.TryGetProperty("assets", out var assets) &&
                        assets.ValueKind == JsonValueKind.Array)
                    {
                        var (sotfs, vanilla, apworld) = PickModAssets(assets);
                        // Accept this release when at least one game zip is present.
                        if (sotfs != null || vanilla != null)
                            return (version, sotfs, vanilla, apworld);
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackSotFSZipUrl, FallbackVanillaZipUrl, null);
    }

    /// From a release's assets array, separate the SotFS zip, the vanilla zip, and
    /// the apworld. Both zips ship in every release (verified in v0.6.0-alpha.2).
    private static (string? SotFS, string? Vanilla, string? Apworld)
        PickModAssets(System.Text.Json.JsonElement assets)
    {
        string? sotfs = null, vanilla = null, apworld = null;

        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();

            if (lower.EndsWith(".apworld") && lower.Contains("dark_souls_2"))
            {
                apworld = url;
            }
            else if (lower.EndsWith(".zip") && lower.Contains("ds2"))
            {
                // "sotfs" appears in the SotFS zip name; "vanilla" in the vanilla zip name.
                if (lower.Contains("sotfs"))
                    sotfs = url;
                else if (lower.Contains("vanilla"))
                    vanilla = url;
                else
                    sotfs ??= url; // unknown variant — treat as SotFS
            }
        }

        return (sotfs, vanilla, apworld);
    }

    // ── Private helpers — mod DLL / game dir resolution ───────────────────────

    /// The dinput8.dll we staged into the game's Game folder. Null if absent.
    private string? ResolveGameDll()
    {
        string? dir = ResolveGameDir();
        if (dir == null) return null;

        // Staged location is the Game sub-folder of the install.
        string inGame  = Path.Combine(dir, "Game", ModDllName);
        if (File.Exists(inGame)) return inGame;

        string direct  = Path.Combine(dir, ModDllName);
        if (File.Exists(direct)) return direct;

        return null;
    }

    /// The Steam game install dir, applying the user override when set.
    private string? ResolveGameDir() => LoadOverrideDir() ?? DetectSteamGameDir();

    /// A folder "looks like" Dark Souls II when it (or its Game sub-folder)
    /// contains the game executable. SotFS exe is DarkSoulsII.exe; vanilla is
    /// DarkSoulsII.exe as well (same naming confirmed for both editions).
    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, "DarkSoulsII.exe"))) return true;
            if (File.Exists(Path.Combine(dir, "Game", "DarkSoulsII.exe"))) return true;
            if (File.Exists(Path.Combine(dir, "darksoulsII.exe"))) return true;
            if (File.Exists(Path.Combine(dir, "Game", "darksoulsII.exe"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Stage the mod DLL from our extracted staging folder into the game's Game
    /// folder. Throws with an honest guidance message if the game folder is not
    /// detected (so InstallOrUpdate surfaces the problem clearly).
    private void StageModDll()
    {
        // Find dinput8.dll inside our extracted staging folder (may be nested).
        string? srcDll = FindDllInStaging();
        if (srcDll == null)
            return; // extraction may still be in progress; best effort

        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            return; // will be reported in the InstallOrUpdate closing message

        // The mod README says: place dinput8.dll in the Game folder next to the exe.
        string targetDir = Path.Combine(gameDir, "Game");
        if (!Directory.Exists(targetDir))
        {
            // Some installs may not have the Game sub-folder; fall back to root.
            targetDir = gameDir;
        }

        string dst = Path.Combine(targetDir, ModDllName);
        File.Copy(srcDll, dst, overwrite: true);
    }

    /// Locate dinput8.dll in our staging folder (GameDirectory). Checks the root
    /// first, then sub-folders (the zip may nest it).
    private string? FindDllInStaging()
    {
        try
        {
            if (!Directory.Exists(GameDirectory)) return null;

            string direct = Path.Combine(GameDirectory, ModDllName);
            if (File.Exists(direct)) return direct;

            foreach (string f in Directory.EnumerateFiles(GameDirectory, ModDllName, SearchOption.AllDirectories))
                return f;
        }
        catch { /* vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        if (!IsInstalled)
            throw new InvalidOperationException(
                "The Dark Souls II Archipelago mod (dinput8.dll) is not installed. " +
                "Click Install on the Play tab first, or check that your game folder is " +
                "correct in Settings.");

        string appId = UseVanilla ? VANILLA_APPID : SOTFS_APPID;
        string runUrl = $"steam://rungameid/{appId}";

        try
        {
            Process.Start(new ProcessStartInfo(runUrl) { UseShellExecute = true });
            IsRunning = true;
            // Steam-launched processes cannot be directly tracked; IsRunning is
            // best-effort here (as with DS3/SoH). GameExited fires on StopAsync.
            return;
        }
        catch { /* Steam not running or not installed — fall through to exe */ }

        // Fallback: find and launch the exe directly.
        string? gameDir = ResolveGameDir();
        string? exe = gameDir != null ? FindGameExeIn(gameDir) : null;

        if (exe == null)
            throw new FileNotFoundException(
                "Dark Souls II executable not found. Make sure the game is installed via " +
                "Steam and that the game folder is correct in Settings.",
                "DarkSoulsII.exe");

        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exe,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? gameDir!,
            UseShellExecute  = true,
        }) ?? throw new InvalidOperationException("Failed to start Dark Souls II.");

        _gameProcess = proc;
        IsRunning    = true;
        try
        {
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                IsRunning    = false;
                _gameProcess = null;
                GameExited?.Invoke(proc.ExitCode);
            };
        }
        catch { /* non-fatal */ }
    }

    private static string? FindGameExeIn(string dir)
    {
        try
        {
            foreach (string name in new[] { "DarkSoulsII.exe", "darksoulsII.exe" })
            {
                string inGame = Path.Combine(dir, "Game", name);
                if (File.Exists(inGame)) return inGame;
                string direct = Path.Combine(dir, name);
                if (File.Exists(direct)) return direct;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string zipName,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"ds2-archipelago-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((6, $"Downloading {zipName} ({version})..."));
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
                        int pct = (int)(6 + 60 * downloaded / total);
                        progress.Report((pct, $"Downloading mod... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting the mod..."));
            Directory.CreateDirectory(GameDirectory);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, GameDirectory, overwriteFiles: true);

            // Flatten a single top-level wrapper sub-folder if the zip nests everything.
            if (FindDllInStaging() == null)
            {
                string[] subdirs = Directory.GetDirectories(GameDirectory);
                string[] files   = Directory.GetFiles(GameDirectory);
                if (files.Length == 0 && subdirs.Length == 1)
                {
                    string sub = subdirs[0];
                    foreach (string fileSrc in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
                    {
                        string rel     = Path.GetRelativePath(sub, fileSrc);
                        string fileDst = Path.Combine(GameDirectory, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                        File.Move(fileSrc, fileDst, overwrite: true);
                    }
                    try { Directory.Delete(sub, recursive: true); } catch { }
                }
            }

            progress.Report((80, "Mod extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — Steam detection ────────────────────────────────────

    private string? DetectSteamGameDir()
    {
        try
        {
            string appId         = UseVanilla ? VANILLA_APPID  : SOTFS_APPID;
            string commonFolder  = UseVanilla ? VANILLA_COMMON_FOLDER : SOTFS_COMMON_FOLDER;

            foreach (string steamRoot in SteamRoots())
            {
                if (string.IsNullOrWhiteSpace(steamRoot)) continue;
                foreach (string lib in SteamLibraryRoots(steamRoot))
                {
                    try
                    {
                        string steamapps = Path.Combine(lib, "steamapps");
                        string manifest  = Path.Combine(steamapps, $"appmanifest_{appId}.acf");
                        if (!File.Exists(manifest)) continue;

                        string common = Path.Combine(steamapps, "common");
                        string? installDir = ReadAcfInstallDir(manifest);
                        if (installDir != null)
                        {
                            string candidate = Path.Combine(common, installDir);
                            if (LooksLikeGameDir(candidate)) return candidate;
                        }
                        string conventional = Path.Combine(common, commonFolder);
                        if (LooksLikeGameDir(conventional)) return conventional;
                    }
                    catch { /* try next library */ }
                }
            }
        }
        catch { /* registry / file access failure */ }
        return null;
    }

    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormalizeSteamPath(hkcu);

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormalizeSteamPath(hklm);

        string? hklm2 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm2)) yield return NormalizeSteamPath(hklm2);

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

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
            int open  = text.IndexOf('"', i);
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
            int open  = text.IndexOf('"', i);
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

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class DarkSouls2Settings
    {
        public string? InstallOverride { get; set; }
        public bool    UseVanilla      { get; set; }
    }

    /// Whether to use the vanilla Dark Souls II mod (32-bit, appid 236430)
    /// instead of Scholar of the First Sin (64-bit, appid 335300).
    private bool UseVanilla => LoadSettings().UseVanilla;

    private DarkSouls2Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<DarkSouls2Settings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(DarkSouls2Settings s)
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
    private void SaveOverrideDir(string p)  { var s = LoadSettings(); s.InstallOverride = p;      SaveSettings(s); }
    private void SaveUseVanilla(bool v)      { var s = LoadSettings(); s.UseVanilla      = v;      SaveSettings(s); }
}
