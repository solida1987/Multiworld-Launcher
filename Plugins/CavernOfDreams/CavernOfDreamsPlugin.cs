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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using LauncherV2.Core;

namespace LauncherV2.Plugins.CavernOfDreams;

// ═══════════════════════════════════════════════════════════════════════════════
// CavernOfDreamsPlugin — install / launch for "Cavern of Dreams" (Bynine Studio,
// 2023) played through the CoDArchipelago mod by wu4, which contains the in-game
// Archipelago Multiworld client. This is a NATIVE "ConnectsItself" integration:
// the game itself speaks to the AP server (no emulator, no Lua bridge, no
// launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified against mod source + GitHub) ───
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Cavern of Dreams (Steam appid 2059660, by Bynine Studio), and Archipelago
// support is delivered as a BepInEx mod added on top. The verified facts:
//
//   * THE AP WORLD game string is "Cavern of Dreams" (verified live against
//     wu4/CavernOfDreamsAP APClient/Client.cs:
//     `session.LoginAsync(game: "Cavern of Dreams", ...)`). GameId = "cavern_of_dreams".
//     This is a COMMUNITY APWorld (not in AP main), hosted at wu4/Archipelago.
//     The apworld file is "cavern_of_dreams.apworld".
//
//   * THE MOD repo is wu4/CavernOfDreamsAP (verified live 2026-06-14). The latest
//     release (v0-beta.10.1, Aug 2024) ships ONE asset per release: "Archipelago.zip".
//     The BepInEx plugin DLL is "CoDArchipelago.dll"
//     (BepInPlugin GUID "cavernofdreams.mod.archipelago"). The mod targets BepInEx
//     5.x (Unity Mono) and net472.
//
//   * BepInEx 5.x (Unity MONO) is a SEPARATE prerequisite. The mod's csproj
//     references BepInEx.Core Version="5.*". BepInEx is a portable zip (no wizard)
//     extracted into the game install root.
//
//   * CONNECTION is made VIA THE IN-GAME MENU (verified against APClient/Menu.cs):
//     after installing the mod, launch Cavern of Dreams; on the main menu the game
//     replaces the "File Select" button with "CONNECT". Press CONNECT, then ADD,
//     fill in "Player Name", "Address", and "Port", and click ADD to save the
//     session. The mod saves sessions to:
//         %LocalAppData%\..\LocalLow\Bynine Studio\Cavern of Dreams\savedSessions.json
//     This is Unity's Application.persistentDataPath on Windows. The launcher CAN
//     pre-write this JSON file to pre-fill the connection, sparing the user from
//     typing server details in-game — which is the meaningful automation this plugin
//     provides. The JSON format (from Menu.cs APSavedSessions) is:
//         {"sessions":[{"playerName":"<slot>","address":"<host>","port":<port>}]}
//     If a sessions file already exists (user set up their own), the launcher
//     APPENDS a new entry rather than overwriting it.
//
//   * The apworld lives in wu4/Archipelago (a separate repo from the game mod),
//     at releases tag v0-beta.10.3 as "cavern_of_dreams.apworld". The user must
//     place it in their Archipelago custom_worlds folder to generate a game.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Cavern of Dreams install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Cavern of Dreams via appmanifest_2059660.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and takes
//      precedence; it is validated (must contain CavernOfDreams.exe or the
//      CavernOfDreams_Data folder) and persisted in this plugin's OWN sidecar
//      (Games/ROMs/cavern_of_dreams/cavern_of_dreams_launcher.json) — Core/
//      SettingsStore is NOT modified.
//   2. INSTALL/UPDATE (best effort) = (a) if BepInEx is not present in the
//      detected install, download BepInEx 5.4.22 x64 (Unity Mono) and extract it
//      into the game root; (b) download the mod's "Archipelago.zip" from the
//      latest wu4/CavernOfDreamsAP release and extract it into the game root
//      (the zip is structured to land BepInEx/plugins/CoDArchipelago.dll where
//      BepInEx expects it). Stamps version in the sidecar. Presents guided steps.
//   3. LAUNCH = run CavernOfDreams.exe from the detected/override install; if the
//      exe cannot be found but Steam is present, fall back to
//      steam://rungameid/2059660. ConnectsItself = true (the mod owns the slot).
//      SupportsStandalone = true. PRE-FILLS the in-game savedSessions.json from the
//      ApSession (host / slot / port fields) for a smooth connection.
//
// ── DEFENSIVE / UNVERIFIED ────────────────────────────────────────────────────
//   * "Installed" = "CoDArchipelago.dll" present under a detected/override install's
//     BepInEx tree (case-insensitive, recursive). Honors hand / mod-manager installs.
//   * Steam library / VDF / ACF parsing is defensive (hand-written tolerant scans).
//   * The savedSessions.json pre-fill writes a NEW entry (prepended); if JSON parse
//     fails it writes fresh (the game itself overwrites this file freely).
//   * No plaintext AP session data is left on disk after stop — scrubbed on StopAsync.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CavernOfDreamsPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string MOD_OWNER  = "wu4";
    private const string MOD_REPO   = "CavernOfDreamsAP";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";

    // The apworld ships in a SEPARATE repo (wu4/Archipelago).
    private const string APWORLD_OWNER   = "wu4";
    private const string APWORLD_REPO    = "Archipelago";
    private const string ApworldRepoUrl  = $"https://github.com/{APWORLD_OWNER}/{APWORLD_REPO}";
    private const string ApworldFileName = "cavern_of_dreams.apworld";

    // Pinned fallbacks when the GitHub API is unreachable.
    private const string FallbackModVersion  = "v0-beta.10.1";
    private const string FallbackModZipName  = "Archipelago.zip";
    private static readonly string FallbackModZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackModVersion}/{FallbackModZipName}";

    private const string FallbackApworldVersion = "v0-beta.10.3";
    private static readonly string FallbackApworldUrl =
        $"{ApworldRepoUrl}/releases/download/{FallbackApworldVersion}/{ApworldFileName}";

    // BepInEx 5.4.22 Unity Mono x64 — portable zip, no wizard.
    private const string BepInExVersion = "5.4.22";
    private static readonly string BepInExZipUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.22/" +
        "BepInEx_x64_5.4.22.0.zip";
    private const string BepInExSite = "https://github.com/BepInEx/BepInEx/releases";

    // Steam — Cavern of Dreams appid 2059660 (verified via SteamDB + Steam store).
    private const string SteamAppId  = "2059660";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    // Unity game: install dir is conventionally the game title (read from ACF at runtime).
    private const string SteamCommonFolderName = "Cavern of Dreams";

    // Game exe / data dir names (standard Unity export naming for this title).
    private const string GameExeName = "CavernOfDreams.exe";
    private const string GameDataDir = "CavernOfDreams_Data";

    // The mod's BepInEx plugin DLL (AssemblyName = CoDArchipelago in the csproj).
    private const string ModPrimaryDll = "CoDArchipelago.dll";

    // Unity Application.persistentDataPath on Windows:
    //   %LocalAppData%\..\LocalLow\<Company>\<Product>
    // Verified: save path is ...\LocalLow\Bynine Studio\Cavern of Dreams\
    // (source: games-manuals.com/cavern-of-dreams/cavern-of-dreams-save-game-location...)
    private static string UnityPersistentDataPath
    {
        get
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string localLow = Path.GetFullPath(Path.Combine(local, "..", "LocalLow"));
            return Path.Combine(localLow, "Bynine Studio", "Cavern of Dreams");
        }
    }

    private static string SavedSessionsJsonPath
        => Path.Combine(UnityPersistentDataPath, "savedSessions.json");

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "cavern_of_dreams";
    public string DisplayName => "Cavern of Dreams";
    public string Subtitle    => "Native PC · BepInEx mod";

    // EXACT AP game string — verified against wu4/CavernOfDreamsAP APClient/Client.cs.
    public string ApWorldName => "Cavern of Dreams";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "cavern_of_dreams.png");

    public string ThemeAccentColor => "#4E9FCC";   // N64 cave sky blue
    public string[] GameBadges     => new[] { "Steam · BepInEx mod" };

    public string Description =>
        "Cavern of Dreams, Bynine Studio's 2023 N64-style 3D platformer where you play " +
        "as Fynn the dragon searching for unhatched siblings, played through the " +
        "CoDArchipelago mod by wu4 — a BepInEx plugin that bundles an in-game Archipelago " +
        "client, so the game connects to the multiworld itself with no emulator and no " +
        "bridge. Items, abilities, and wing feathers are shuffled across the multiworld. " +
        "You bring your own copy of Cavern of Dreams (owned on Steam); the integration " +
        "runs on BepInEx 5.x (Unity Mono). The launcher detects your Steam install, " +
        "stages BepInEx and the Archipelago mod automatically, and pre-fills the in-game " +
        "connection screen so you can start playing in one click from the main menu. " +
        "The apworld (cavern_of_dreams.apworld from wu4/Archipelago) must be placed in " +
        "your Archipelago custom_worlds folder to generate a game.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    // "Installed" = CoDArchipelago.dll present in the BepInEx tree of the detected
    // or override install. Honors hand-installed and mod-manager installs.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "CavernOfDreams");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "cavern_of_dreams_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // Tracks what we pre-wrote into savedSessions.json so StopAsync can scrub it.
    private string? _prefillSlotName;
    private string? _prefillAddress;
    private int     _prefillPort;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The CoDArchipelago mod reports checks/items/goal to the AP server itself.
    // These exist for interface compatibility (ConnectsItself = true).
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
            InstalledVersion = FindInstalledModDll() != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch { InstalledVersion = null; }
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
        // 0. Locate the game install.
        progress.Report((2, "Locating your Cavern of Dreams installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Cavern of Dreams installation. Open this game's Settings " +
                "and pick your install folder (the one containing CavernOfDreams.exe), or " +
                "install Cavern of Dreams via Steam first. The Archipelago mod is added on " +
                "top of your own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback when offline).
        progress.Report((6, "Checking the latest CoDArchipelago release..."));
        var (modVersion, modZipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = modVersion;

        if (modZipUrl == null)
            throw new InvalidOperationException(
                "Could not find the CoDArchipelago mod download on GitHub. Check your " +
                "internet connection, or download Archipelago.zip manually from " +
                ModRepoUrl + "/releases and extract it into your Cavern of Dreams folder.");

        // 2. Stage BepInEx 5.4.22 (Unity Mono x64) if not already present. This is
        //    a SEPARATE portable prerequisite not bundled in the mod zip.
        if (!BepInExPresent(gameDir))
        {
            progress.Report((10, "Staging BepInEx 5.4.22 (Unity Mono x64) into your Cavern of Dreams folder..."));
            await DownloadAndExtractZipAsync(
                BepInExZipUrl, gameDir, "bepinex-5.4.22", 10, 45, progress, ct);
        }
        else
        {
            progress.Report((45, "BepInEx already present — keeping existing install."));
        }

        // 3. Download + extract the mod's "Archipelago.zip" into the game root.
        //    The zip is structured so BepInEx/plugins/CoDArchipelago.dll lands
        //    where BepInEx expects it.
        progress.Report((47, $"Downloading CoDArchipelago {modVersion}..."));
        await DownloadAndExtractZipAsync(
            modZipUrl, gameDir, $"cod-archipelago-{modVersion}", 47, 88, progress, ct);

        // 4. Stamp version in the sidecar.
        WriteStampedVersion(modVersion);
        InstalledVersion = modVersion;

        bool bepOk = BepInExPresent(gameDir);
        bool modOk = FindInstalledModDll() != null;

        progress.Report((100,
            $"Staged CoDArchipelago {modVersion} into your Cavern of Dreams folder" +
            (bepOk ? " (BepInEx OK" : " (BepInEx NOT detected") +
            (modOk ? ", mod DLL present)." : ", mod DLL NOT detected — check the zip layout).") +
            " To play: launch Cavern of Dreams ONCE so BepInEx generates its config files. " +
            "Then click Play — the launcher pre-fills the in-game CONNECT screen with your " +
            "server details so you just select the saved session from the list. " +
            "IMPORTANT: place cavern_of_dreams.apworld from wu4/Archipelago releases into " +
            "your Archipelago custom_worlds folder first so you can generate a game."));
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
        // Pre-fill the in-game savedSessions.json so the user sees their server
        // pre-loaded in the CONNECT → session list when the main menu opens.
        // Verified against APClient/Menu.cs: LoadSavedSessions + APSavedSessions JSON.
        ParseServerUri(session.ServerUri, out string host, out int port);
        PrefillSavedSessions(session.SlotName, host, port);
        StartGame();
        return Task.CompletedTask;
    }

    public bool SupportsStandalone => true;

    // The CoDArchipelago mod owns the slot connection — the launcher must NOT hold
    // its own ApClient on this slot.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;

        // Scrub the launcher-written session entry from savedSessions.json so no
        // plaintext server / slot data lingers on disk.
        if (_prefillSlotName != null)
        {
            ScrubPrefillSession(_prefillSlotName, _prefillAddress, _prefillPort);
            _prefillSlotName = null;
            _prefillAddress  = null;
            _prefillPort     = 0;
        }

        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert ─────────────────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Existing-install validation ───────────────────────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Cavern of Dreams install folder.";

        if (LooksLikeGameDir(folder)) return null;

        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeGameDir(nested)) return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Cavern of Dreams installation. Pick the " +
               "folder that contains CavernOfDreams.exe or the CavernOfDreams_Data " +
               @"folder (for Steam this is usually ...\steamapps\common\Cavern of Dreams).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Info header ───────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Cavern of Dreams uses BepInEx 5.x (Unity Mono) and the CoDArchipelago " +
                   "mod (wu4/CavernOfDreamsAP). The launcher can auto-install BepInEx and " +
                   "the mod. You connect in-game via CONNECT on the main menu — the launcher " +
                   "pre-fills your session so it appears ready to select. " +
                   "You also need cavern_of_dreams.apworld (community world, not in AP main) " +
                   "from wu4/Archipelago placed in your Archipelago custom_worlds folder.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install ─────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CAVERN OF DREAMS INSTALL", FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Cavern of Dreams not detected. Pick your install folder below, or install " +
              "the game via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        string? modDll = FindInstalledModDll();
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                ? "CoDArchipelago mod present: " + modDll
                : "CoDArchipelago mod not detected (use Install on the Play tab to stage it).",
            FontSize = 11,
            Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        bool bepOk = gameDir != null && BepInExPresent(gameDir);
        panel.Children.Add(new TextBlock
        {
            Text = bepOk
                ? "BepInEx present in install folder."
                : "BepInEx not detected (the launcher stages it automatically during Install).",
            FontSize = 11,
            Foreground = bepOk ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Cavern of Dreams install folder (containing CavernOfDreams.exe). " +
                          "Detected from Steam automatically; use the picker to override.",
        };
        var dirBtn = new Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        string? capturedOverride = overrideDir;
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select your Cavern of Dreams install folder (contains CavernOfDreams.exe)",
                InitialDirectory = Directory.Exists(capturedOverride ?? gameDir ?? "")
                    ? (capturedOverride ?? gameDir!)
                    : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    MessageBox.Show(bad, "Not a Cavern of Dreams folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeGameDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeGameDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 2059660). " +
                   "Use the picker for a non-standard library location.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: connection ───────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "When you click Play with an active AP session, the launcher pre-fills " +
                   "the in-game session list with your server address, port, and slot name. " +
                   "Open the game, click CONNECT on the main menu, and select your pre-filled " +
                   "session to start. To connect manually: click CONNECT → ADD, fill in " +
                   "Player Name, Address, and Port, then click ADD. " +
                   "Session file location: " + SavedSessionsJsonPath,
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: apworld ─────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "APWORLD FILE (COMMUNITY — NOT IN AP MAIN)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Cavern of Dreams uses a community apworld (not part of Archipelago main). " +
                   "Download cavern_of_dreams.apworld from wu4/Archipelago releases and place " +
                   "it in your Archipelago custom_worlds folder " +
                   @"(%LocalAppData%\Archipelago\lib\worlds\ or next to ArchipelagoLauncher.exe).",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        string? cwDir = FindArchipelagoCustomWorldsDir();
        panel.Children.Add(new TextBlock
        {
            Text = cwDir != null
                ? "Archipelago custom_worlds folder: " + cwDir
                : "Archipelago installation not detected. Install Archipelago first, then " +
                  "drop cavern_of_dreams.apworld into its custom_worlds folder.",
            FontSize = 11,
            Foreground = cwDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // ── Section: guided steps ─────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "SETUP STEPS", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own and install Cavern of Dreams on Steam. Use the folder picker above if " +
                "it was not auto-detected.",
            "2. Download cavern_of_dreams.apworld from wu4/Archipelago releases (link below) " +
                "and place it in your Archipelago custom_worlds folder. Generate your game " +
                "from the Archipelago web client or Launcher.",
            "3. Click Install on the Play tab — the launcher stages BepInEx 5.4.22 x64 " +
                "(Unity Mono) and the CoDArchipelago mod automatically.",
            "4. Launch Cavern of Dreams ONCE so BepInEx generates its config files. You " +
                "will see a BepInEx console window appear briefly.",
            "5. To play: click Play with an active AP session. Your server details are " +
                "pre-filled in the CONNECT menu. Select your session entry to connect.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5),
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
            ("CoDArchipelago mod (wu4/CavernOfDreamsAP) ↗",  ModRepoUrl + "/releases"),
            ("cavern_of_dreams.apworld (wu4/Archipelago) ↗", ApworldRepoUrl + "/releases"),
            ("BepInEx 5.x releases ↗",                       BepInExSite),
            ("Cavern of Dreams on Steam ↗",
                $"https://store.steampowered.com/app/{SteamAppId}/Cavern_of_Dreams/"),
            ("Archipelago Official ↗", "https://archipelago.gg"),
        })
        {
            var btn = new Button
            {
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = accent,
                Cursor = Cursors.Hand,
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
            string json = await _http.GetStringAsync(
                $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases?per_page=5", ct);
            using var doc = JsonDocument.Parse(json);
            var results = new List<NewsItem>();
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                string tag  = rel.TryGetProperty("tag_name",    out var t) ? t.GetString() ?? "" : "";
                string body = rel.TryGetProperty("body",        out var b) ? b.GetString() ?? "" : "";
                string dt   = rel.TryGetProperty("published_at", out var d) ? d.GetString() ?? "" : "";
                string url  = rel.TryGetProperty("html_url",    out var u) ? u.GetString() ?? "" : "";
                DateTimeOffset date = DateTimeOffset.TryParse(dt, out var parsed)
                    ? parsed : DateTimeOffset.MinValue;
                results.Add(new NewsItem(
                    Title:   $"CoDArchipelago {tag}",
                    Body:    string.IsNullOrWhiteSpace(body)
                                 ? "See the release page for details."
                                 : body.Length > 400 ? body[..400] + "..." : body,
                    Version: tag,
                    Date:    date,
                    Url:     url));
            }
            if (results.Count > 0) return results.ToArray();
        }
        catch { /* fall through to static item */ }

        return new[]
        {
            new NewsItem(
                Title:   "Cavern of Dreams — Archipelago mod (wu4/CavernOfDreamsAP)",
                Body:    "A BepInEx mod for Cavern of Dreams (Steam, appid 2059660) that adds " +
                         "an in-game Archipelago client. Connects via the CONNECT button on the " +
                         "main menu. Download cavern_of_dreams.apworld from wu4/Archipelago " +
                         "releases for the community apworld (not in AP main).",
                Version: FallbackModVersion,
                Date:    DateTimeOffset.MinValue,
                Url:     ModRepoUrl + "/releases"),
        };
    }

    // ── Private helpers — Steam / game detection ──────────────────────────────

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
            if (File.Exists(Path.Combine(dir, GameExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, GameDataDir))) return true;
            return Directory.EnumerateFiles(dir, "*Cavern*Dreams*.exe",
                                            SearchOption.TopDirectoryOnly).Any();
        }
        catch { return false; }
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

                    string common = Path.Combine(steamapps, "common");
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

    private string? FindInstalledModDll()
    {
        string? dir = ResolveGameDir();
        if (dir == null) return null;
        try
        {
            string bepDir = Path.Combine(dir, "BepInEx");
            if (!Directory.Exists(bepDir)) return null;
            return Directory.EnumerateFiles(bepDir, ModPrimaryDll,
                                            SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { return null; }
    }

    private static bool BepInExPresent(string gameDir)
    {
        try
        {
            // BepInEx 5 portable zip extracts winhttp.dll to the game root as the
            // loader patcher; it also creates a BepInEx/ folder.
            return File.Exists(Path.Combine(gameDir, "winhttp.dll")) &&
                   Directory.Exists(Path.Combine(gameDir, "BepInEx"));
        }
        catch { return false; }
    }

    // ── Private helpers — Archipelago custom_worlds detection ─────────────────

    private static string? FindArchipelagoCustomWorldsDir()
    {
        // Standard AP installation: %LocalAppData%\Archipelago\lib\worlds\
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string candidate = Path.Combine(local, "Archipelago", "lib", "worlds");
            if (Directory.Exists(candidate)) return candidate;
        }
        catch { /* ignore */ }

        // Fallback: custom_worlds next to ArchipelagoLauncher.exe (portable install).
        try
        {
            foreach (string prog in new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            })
            {
                if (string.IsNullOrWhiteSpace(prog)) continue;
                string launcherExe = Path.Combine(prog, "Archipelago", "ArchipelagoLauncher.exe");
                if (!File.Exists(launcherExe)) continue;
                string cw = Path.Combine(Path.GetDirectoryName(launcherExe)!, "custom_worlds");
                if (Directory.Exists(cw)) return cw;
            }
        }
        catch { /* ignore */ }

        return null;
    }

    // ── Private helpers — savedSessions.json pre-fill ─────────────────────────

    private void PrefillSavedSessions(string slotName, string address, int port)
    {
        try
        {
            // Load existing sessions or start fresh.
            List<SavedSession> sessions = new();
            if (File.Exists(SavedSessionsJsonPath))
            {
                try
                {
                    string existing = File.ReadAllText(SavedSessionsJsonPath);
                    using var doc = JsonDocument.Parse(existing);
                    if (doc.RootElement.TryGetProperty("sessions", out var arr))
                    {
                        foreach (var el in arr.EnumerateArray())
                        {
                            string pn = el.TryGetProperty("playerName", out var p) ? p.GetString() ?? "" : "";
                            string ad = el.TryGetProperty("address",    out var a) ? a.GetString() ?? "" : "";
                            int    po = el.TryGetProperty("port",       out var o) ? o.GetInt32()      : 38281;
                            // Skip any prior entry for the same slot+address to avoid duplicates.
                            if (string.Equals(pn, slotName, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(ad, address,  StringComparison.OrdinalIgnoreCase) &&
                                po == port)
                                continue;
                            sessions.Add(new SavedSession(pn, ad, po));
                        }
                    }
                }
                catch { /* corrupt — start fresh */ }
            }

            // Prepend the launcher-provided session so it appears first in the CONNECT list.
            sessions.Insert(0, new SavedSession(slotName, address, port));

            Directory.CreateDirectory(UnityPersistentDataPath);

            // JSON format: {"sessions":[{"playerName":"...","address":"...","port":N},...]}
            // Matches the Newtonsoft.Json serialization in APClient/Menu.cs (APSavedSessions).
            var obj = new
            {
                sessions = sessions.Select(s => new
                {
                    playerName = s.PlayerName,
                    address    = s.Address,
                    port       = s.Port,
                }).ToArray()
            };

            File.WriteAllText(SavedSessionsJsonPath,
                JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = false }),
                new UTF8Encoding(false));

            _prefillSlotName = slotName;
            _prefillAddress  = address;
            _prefillPort     = port;
        }
        catch { /* non-fatal: user can still connect manually in-game */ }
    }

    private void ScrubPrefillSession(string slotName, string? address, int port)
    {
        if (!File.Exists(SavedSessionsJsonPath)) return;
        try
        {
            string existing = File.ReadAllText(SavedSessionsJsonPath);
            using var doc = JsonDocument.Parse(existing);
            if (!doc.RootElement.TryGetProperty("sessions", out var arr)) return;

            var keep = new List<SavedSession>();
            foreach (var el in arr.EnumerateArray())
            {
                string pn = el.TryGetProperty("playerName", out var p) ? p.GetString() ?? "" : "";
                string ad = el.TryGetProperty("address",    out var a) ? a.GetString() ?? "" : "";
                int    po = el.TryGetProperty("port",       out var o) ? o.GetInt32()      : 38281;
                // Remove only the entry we wrote.
                if (string.Equals(pn, slotName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(ad, address,  StringComparison.OrdinalIgnoreCase) &&
                    po == port)
                    continue;
                keep.Add(new SavedSession(pn, ad, po));
            }

            var obj = new
            {
                sessions = keep.Select(s => new
                {
                    playerName = s.PlayerName,
                    address    = s.Address,
                    port       = s.Port,
                }).ToArray()
            };

            File.WriteAllText(SavedSessionsJsonPath,
                JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = false }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    private sealed record SavedSession(string PlayerName, string Address, int Port);

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        string? gameDir = ResolveGameDir();
        string? exe     = gameDir != null ? FindGameExe(gameDir) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Cavern of Dreams.");

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

        // Fall back to Steam if detectable.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true;
                return;
            }
            catch { /* fall through */ }
        }

        throw new FileNotFoundException(
            "Could not find CavernOfDreams.exe. Open this game's Settings and pick your " +
            "Cavern of Dreams install folder, or install the game via Steam first.",
            GameExeName);
    }

    private static string? FindGameExe(string gameDir)
    {
        try
        {
            string direct = Path.Combine(gameDir, GameExeName);
            if (File.Exists(direct)) return direct;

            // Defensive: search for any Cavern-of-Dreams-named exe in the game root.
            foreach (string exe in Directory.EnumerateFiles(gameDir, "*Cavern*Dreams*.exe",
                                                            SearchOption.TopDirectoryOnly))
                return exe;

            // Last resort: any exe in the root.
            foreach (string exe in Directory.EnumerateFiles(gameDir, "*.exe",
                                                            SearchOption.TopDirectoryOnly))
                return exe;
        }
        catch { /* permission / vanished */ }
        return null;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* non-fatal */ }
    }

    private static void ParseServerUri(string serverUri, out string host, out int port)
    {
        host = "archipelago.gg";
        port = 38281;
        try
        {
            string uri = serverUri ?? "";
            if (uri.Contains("://")) uri = uri.Split(new[] { "://" }, 2, StringSplitOptions.None)[1];
            int colon = uri.LastIndexOf(':');
            if (colon >= 0 && int.TryParse(uri[(colon + 1)..], out int p))
            {
                host = uri[..colon];
                port = p;
            }
            else
            {
                host = uri;
            }
        }
        catch { /* use defaults */ }
        if (string.IsNullOrWhiteSpace(host)) host = "archipelago.gg";
    }

    // ── Private helpers — GitHub release resolution ───────────────────────────

    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            string tag = doc.RootElement.TryGetProperty("tag_name", out var t)
                ? t.GetString() ?? FallbackModVersion
                : FallbackModVersion;

            string? zipUrl = null;
            if (doc.RootElement.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zipUrl = asset.TryGetProperty("browser_download_url", out var u)
                            ? u.GetString() : null;
                        break;
                    }
                }
            }
            return (tag, zipUrl ?? FallbackModZipUrl);
        }
        catch
        {
            return (FallbackModVersion, FallbackModZipUrl);
        }
    }

    // ── Private helpers — file download / extraction ──────────────────────────

    private async Task DownloadAndExtractZipAsync(
        string url, string destDir, string tempSuffix,
        int pctStart, int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(), $"cod-ap-{tempSuffix}.zip");
        try
        {
            progress.Report((pctStart, $"Downloading {Path.GetFileName(url)}..."));
            using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 0L;
                await using var fs = File.Create(tempZip);
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                byte[] buf = new byte[81920];
                long downloaded = 0;
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await fs.WriteAsync(buf.AsMemory(0, read), ct);
                    downloaded += read;
                    if (total > 0)
                    {
                        int fraction = (int)(downloaded * 100L / total);
                        int pct = pctStart + (pctEnd - pctStart) * fraction / 100;
                        progress.Report((pct, $"Downloading... {downloaded / 1024}KB / {total / 1024}KB"));
                    }
                }
            }
            progress.Report((pctEnd - 2, "Extracting..."));
            Directory.CreateDirectory(destDir);
            ZipFile.ExtractToDirectory(tempZip, destDir, overwriteFiles: true);
            progress.Report((pctEnd, "Extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — Steam registry / VDF / ACF ─────────────────────────

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
            if (norm.Length > 0 && seen.Add(norm)) yield return norm;
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
            string raw = text.Substring(open + 1, close - open - 1);
            yield return raw.Replace("\\\\", "\\");
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

    // ── Private helpers — sidecar ─────────────────────────────────────────────

    private sealed class CodSettings
    {
        public string? InstallOverride { get; set; }
        public string? StampedVersion  { get; set; }
    }

    private CodSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<CodSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(CodSettings s)
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

    private void SaveOverrideDir(string p)
    {
        var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s);
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().StampedVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings(); s.StampedVersion = v; SaveSettings(s);
    }
}
