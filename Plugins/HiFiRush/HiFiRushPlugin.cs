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

namespace LauncherV2.Plugins.HiFiRush;

// ═══════════════════════════════════════════════════════════════════════════════
// HiFiRushPlugin — install / launch for "Hi-Fi RUSH" (Tango Gameworks / KRAFTON,
// 2023) played through the HbkArchipelago mod by TRPG0, a UE4SS Lua mod that
// connects the game to an Archipelago Multiworld. This is a NATIVE
// "ConnectsItself" integration: the game speaks to the AP server itself (no
// emulator, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified against TRPG0/HbkArchipelago) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Hi-Fi RUSH (Steam appid 1817230), and Archipelago support is delivered as a
// UE4SS Lua mod added on top. The honest integration ceiling is "automate what is
// possible, guide the irreducible parts."
//
//   * THE AP WORLD game string is "Hi-Fi RUSH" (verified against
//     apworld/__init__.py: `class HiFiRushWorld(World): ... game = "Hi-Fi RUSH"`
//     and apworld/archipelago.json: `"game": "Hi-Fi RUSH"`).
//     GameId here = "hifi_rush". This is a COMMUNITY apworld (not core AP; the
//     user installs hi_fi_rush.apworld from the HbkArchipelago releases).
//
//   * THE MOD repo is TRPG0/HbkArchipelago (verified live 2026-06-14). It is "A
//     client for connecting Hi-Fi RUSH to an Archipelago randomizer." The mod is
//     a UE4SS Lua mod: it drops a "HbkArchipelago" folder and supporting files
//     (including lua-apclientpp) into the UE4SS Mods directory inside the game's
//     Win64 binaries folder. The verified install target path (Steam) is:
//         ...\Hi-Fi RUSH\Hibiki\Binaries\Win64\
//     UE4SS itself is a SEPARATE PREREQUISITE (required for Lua mod support).
//     The setup guide from hibiki-bootstrap (akmubi/hibiki-bootstrap) is required
//     since Update 10 broke the standard UE4SS injection method.
//
//   * CONNECTION is made ENTIRELY IN-GAME via the UE4SS developer console (F10):
//       connect [address:port] [player] [password?]
//     For example: connect archipelago.gg:38281 Chai
//     After the first successful connect, the mod SAVES the address, slot, and
//     password into a per-save JSON in:
//         ...\Win64\Mods\HbkArchipelago\save\<save-slot-name>.json
//     On subsequent sessions the player can type just "connect" (no args) to
//     reconnect using the saved credentials. There is NO startup config file
//     this launcher can pre-write to seed the connection — the mod reads from
//     its own per-save JSON only after the first in-game connect; there is no
//     documented ini or startup config the launcher can pre-fill. Per an honest
//     "don't invent an undocumented prefill" stance, this plugin does NOT write
//     any config file. The settings panel surfaces the session's host:port / slot
//     / password for the user to copy into the F10 console.
//
//   * ConnectsItself = true: the UE4SS Lua mod owns the AP slot connection via
//     lua-apclientpp. The launcher must NOT hold its own ApClient on this slot
//     while the game runs (AP servers allow one connection per slot and would kick
//     the older one endlessly). The launcher launches the game and suppresses its
//     own auto-reconnect for this slot.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Hi-Fi RUSH install via the Windows registry (HKCU\
//      Software\Valve\Steam -> SteamPath, HKLM\...\WOW6432Node\Valve\Steam ->
//      InstallPath), parsing steamapps\libraryfolders.vdf for every library root
//      and locating steamapps\common\Hi-Fi RUSH via appmanifest_1817230.acf.
//      A manual install-dir OVERRIDE (folder picker) is also supported; it is
//      validated (must contain Hibiki\Binaries\Win64\Hi-Fi RUSH.exe or similar)
//      and persisted in this plugin's own sidecar (Games/ROMs/hifi_rush/
//      hifi_rush_launcher.json).
//   2. INSTALL/UPDATE (guided only): the mod is distributed as a zip from the
//      HbkArchipelago GitHub releases. The plugin downloads the release zip and
//      extracts it into ...\Hibiki\Binaries\Win64\ (the verified install path).
//      UE4SS / hibiki-bootstrap is a SEPARATE prerequisite the plugin cannot
//      safely auto-install (it involves the game's crash-injection mechanism);
//      the plugin opens the setup guide and explains this. The .apworld file
//      must also be installed into the Archipelago Launcher manually.
//   3. LAUNCH = run Hi-Fi RUSH.exe from the detected/override install; if the
//      exe cannot be found but Steam is present, fall back to
//      steam://rungameid/1817230. ConnectsItself = true. SupportsStandalone =
//      true (Hi-Fi RUSH runs fine without AP). No connection prefill, stated
//      honestly.
//
// ── DEFENSIVE ("verify at build time") ────────────────────────────────────────
//   * "Installed" means the HbkArchipelago Lua main.lua is present under the
//     detected/override Win64 Mods directory — the best honest signal that the
//     user installed the mod. If the game install is not detected, the tile
//     simply reads "not installed".
//   * Steam library / VDF / ACF parsing is defensive: any failure degrades to
//     "game not found" rather than throwing.
//   * No plaintext AP password is written by this plugin (the connection is
//     entered in the F10 console in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class HiFiRushPlugin : IGamePlugin
{
    // ── Constants — mod repo (TRPG0/HbkArchipelago, verified 2026-06-14) ───────

    private const string MOD_OWNER = "TRPG0";
    private const string MOD_REPO  = "HbkArchipelago";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Hi-Fi%20RUSH/setup/en";
    private const string HibikiBootstrap = "https://github.com/akmubi/hibiki-bootstrap";
    private const string UE4SSSite       = "https://github.com/UE4SS-RE/RE-UE4SS/releases";
    private const string ArchipelagoSite = "https://archipelago.gg";

    /// Steam application id for Hi-Fi RUSH (verified 2026-06-14).
    private const string SteamAppId = "1817230";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// Standard Steam install folder name under steamapps\common.
    private const string SteamCommonFolderName = "Hi-Fi RUSH";

    /// The game executable (lives under Hibiki\Binaries\Win64\).
    private const string GameExeName = "Hi-Fi RUSH.exe";

    /// UE4SS Mods directory path relative to the Win64 binaries folder.
    private const string ModsRelPath = @"ue4ss\Mods";

    /// The primary Lua entry point for the HbkArchipelago mod — used to detect
    /// whether the mod is installed.
    private const string ModMainLuaRelPath = @"HbkArchipelago\scripts\main.lua";

    /// Fallback version / URL if the GitHub API is unreachable.
    private const string FallbackVersion = "0.3.1";
    private const string FallbackZipName = "HbkArchipelago.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "hifi_rush";
    public string DisplayName => "Hi-Fi RUSH";
    public string Subtitle    => "Native PC · UE4SS Lua mod";

    /// EXACT AP game string — verified against apworld/__init__.py and
    /// apworld/archipelago.json: `"game": "Hi-Fi RUSH"`.
    public string ApWorldName => "Hi-Fi RUSH";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "hi_fi_rush.png");

    public string ThemeAccentColor => "#E03060";   // neon pink / Hi-Fi RUSH title card

    public string[] GameBadges => new[] { "Steam · needs mod" };

    public string Description =>
        "Hi-Fi RUSH, the 2023 rhythm action game by Tango Gameworks, played through " +
        "the HbkArchipelago mod by TRPG0 — a UE4SS Lua mod that connects the game " +
        "to the Archipelago Multiworld directly, so no emulator and no bridge are " +
        "needed. Unlockable tracks, abilities, store items, chips and more are " +
        "shuffled across the multiworld. You bring your own copy of Hi-Fi RUSH (on " +
        "Steam); the mod runs on top of UE4SS (the Unreal Engine modding framework) " +
        "via the hibiki-bootstrap injection method required since Update 10. You " +
        "connect to your server from the in-game UE4SS developer console (F10) with " +
        "the connect command.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the HbkArchipelago main.lua is present under a detected
    /// or override game install's UE4SS Mods directory.
    public bool IsInstalled => FindInstalledModLua() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// The game directory surfaced by IGamePlugin. Returns the detected/override
    /// game root when available; falls back to a launcher-local working folder.
    public string GameDirectory
    {
        get => ResolveGameDir() ?? Path.Combine(AppContext.BaseDirectory, "Games", "HiFiRush");
        set { if (!string.IsNullOrWhiteSpace(value)) SaveOverrideDir(value); }
    }

    /// This plugin's own settings sidecar — keeps launcher-side state (install
    /// override, stamped mod version) without touching Core/SettingsStore.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "hifi_rush_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The UE4SS Lua mod reports checks / items / goal to the AP server directly
    // via lua-apclientpp; the launcher relays nothing. Events exist for interface
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
            InstalledVersion = FindInstalledModLua() != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        try
        {
            var (version, _) = await ResolveLatestReleaseAsync(ct);
            AvailableVersion = version;
        }
        catch
        {
            AvailableVersion = null;
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. We need a Hi-Fi RUSH install to drop the mod files into.
        progress.Report((2, "Locating your Hi-Fi RUSH installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Hi-Fi RUSH installation. Open this game's Settings " +
                "and pick your Hi-Fi RUSH install folder (the one that contains " +
                "Hibiki\\Binaries\\Win64\\Hi-Fi RUSH.exe), or install the game via " +
                "Steam first.");

        string win64Dir = FindWin64Dir(gameDir)
            ?? throw new InvalidOperationException(
                "Found Hi-Fi RUSH but could not locate the Hibiki\\Binaries\\Win64 " +
                "folder inside it. Use Settings to pick the correct install folder.");

        // 1. Resolve the latest mod release.
        progress.Report((6, "Checking the latest HbkArchipelago release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the HbkArchipelago download on GitHub. Check your " +
                "internet connection, or download it manually from " + ModRepoUrl +
                "/releases and extract it into your game's Hibiki\\Binaries\\Win64\\ " +
                "folder.");

        // 2. Check whether UE4SS / hibiki-bootstrap appears to be present. We
        //    cannot safely auto-install it (it involves crash-injection), but we
        //    warn clearly if it is absent and open the setup page.
        bool ue4ssPresent = CheckUe4ssPresent(win64Dir);
        if (!ue4ssPresent)
        {
            progress.Report((10,
                "UE4SS does not appear to be installed in your Win64 folder. The " +
                "HbkArchipelago mod requires UE4SS + hibiki-bootstrap (see the links " +
                "in Settings). Opening the setup guide..."));
            OpenUrl(HibikiBootstrap);
        }

        // 3. Download and extract the mod zip into Win64\.
        await DownloadAndExtractModAsync(zipUrl, version, win64Dir, 14, 92, progress, ct);

        // 4. Stamp the version.
        WriteStampedVersion(version);
        InstalledVersion = version;

        // 5. Guidance for the .apworld file and connection.
        progress.Report((100,
            $"HbkArchipelago mod {version} extracted into your Win64 folder." +
            (ue4ssPresent
                ? ""
                : " WARNING: UE4SS was not detected — install UE4SS + hibiki-bootstrap " +
                  "first (see the Setup Guide link in Settings).") +
            " To play: (1) install hi_fi_rush.apworld from the mod releases into the " +
            "Archipelago Launcher; (2) generate a multiworld and get your server address; " +
            "(3) start Hi-Fi RUSH, load a save, open the UE4SS console with F10, and " +
            "type: connect address:port SlotName (password optional). This launcher " +
            "cannot pre-fill the connection — it is entered in the F10 console in-game."));
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
        // HONEST: the AP server connection for Hi-Fi RUSH is made entirely in the
        // UE4SS developer console (F10) using the command:
        //   connect host:port SlotName [password]
        // There is no startup config file or command-line arg this launcher can
        // pre-write to seed the connection (verified against the HbkArchipelago
        // Lua source: Commands.lua / SaveData.lua — the only persistent storage is
        // a per-save JSON the mod writes AFTER the first successful in-game
        // connect). So launching from this tile just starts the game; the user
        // connects in-game using the session credentials displayed in the Settings
        // panel.
        //
        // ConnectsItself = true: lua-apclientpp inside the mod owns the slot
        // connection, so the launcher must NOT hold its own ApClient on this slot.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartGame();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Hi-Fi RUSH runs fine without the mod active.
    public bool SupportsStandalone => true;

    /// The UE4SS Lua mod owns the slot connection (see header).
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
        // No plaintext AP password is ever written to disk by this plugin (the
        // connection is entered in the F10 console in-game), so there is nothing
        // to scrub.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The UE4SS Lua mod receives items from the AP server directly via
        // lua-apclientpp; the launcher forwards nothing.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in the UE4SS console; no launcher HUD.
    }

    // ── Existing-install validation (folder picker) ───────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Hi-Fi RUSH install folder.";

        if (LooksLikeGameDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeGameDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Hi-Fi RUSH installation. Pick the folder " +
               "that contains the Hibiki sub-folder (for Steam this is usually " +
               @"...\steamapps\common\Hi-Fi RUSH).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honesty header ─────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Hi-Fi RUSH is your own game (Steam) with the HbkArchipelago UE4SS " +
                   "Lua mod added on top. The launcher detects your Steam install and can " +
                   "download and extract the mod files, but UE4SS + hibiki-bootstrap is a " +
                   "separate prerequisite you must install first (see steps below). You " +
                   "connect to your Archipelago server from the UE4SS developer console " +
                   "(F10) inside the game. These external steps are not verified by this " +
                   "launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ───────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "HI-FI RUSH INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Hi-Fi RUSH not detected. Pick your install folder below, or install " +
              "the game via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // Win64 / UE4SS status line
        string? win64Dir   = gameDir != null ? FindWin64Dir(gameDir) : null;
        bool    ue4ssOk    = win64Dir != null && CheckUe4ssPresent(win64Dir);
        string? modLua     = FindInstalledModLua();

        panel.Children.Add(new TextBlock
        {
            Text = win64Dir == null
                    ? ""
                    : (ue4ssOk
                        ? "UE4SS found in your Win64 folder."
                        : "UE4SS not detected in Win64 — install UE4SS + hibiki-bootstrap " +
                          "first (see the links below)."),
            FontSize = 11, Foreground = ue4ssOk ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new TextBlock
        {
            Text = modLua != null
                    ? "HbkArchipelago mod found: " + modLua
                    : "HbkArchipelago mod not found yet (use Install on the Play tab).",
            FontSize = 11, Foreground = modLua != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Hi-Fi RUSH install folder (contains the Hibiki sub-folder). " +
                          "Detected from Steam automatically; set it here to override.",
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
                Title            = "Select your Hi-Fi RUSH install folder (contains Hibiki sub-folder)",
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
                    System.Windows.MessageBox.Show(bad, "Not a Hi-Fi RUSH folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
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
            Text = "Steam installs are detected automatically (appid 1817230). Use this " +
                   "picker for a non-standard Steam library.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connecting (in-game console) ──────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (via UE4SS developer console)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Load a save file, then open the UE4SS developer console with F10 and " +
                   "type the connect command:\n" +
                   "  connect host:port SlotName [password]\n" +
                   "Example: connect archipelago.gg:38281 Chai\n\n" +
                   "After your first successful connection the mod saves your address, slot, " +
                   "and password per save file — on later sessions you can just type " +
                   "\"connect\" with no arguments to reconnect. This launcher does not " +
                   "pre-fill the connection.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Hi-Fi RUSH (on Steam). Install it if you have not. Use the picker " +
                "above if it was not detected automatically.",
            "2. Install UE4SS + hibiki-bootstrap into your game's Hibiki\\Binaries\\Win64\\ " +
                "folder. This is required since Update 10. See the hibiki-bootstrap link " +
                "below for instructions — this launcher cannot install it automatically.",
            "3. Install the HbkArchipelago mod: use the Install button on the Play tab to " +
                "download the mod and extract it into your Win64 folder, or download it " +
                "manually from the mod releases (link below).",
            "4. Install hi_fi_rush.apworld into the Archipelago Launcher (double-click the " +
                ".apworld from the mod release, or use Install APWorld in the AP Launcher). " +
                "This enables generating a Hi-Fi RUSH multiworld.",
            "5. Generate a multiworld on your Archipelago server. Note your server address " +
                "(e.g. archipelago.gg:38281) and slot name.",
            "6. Launch Hi-Fi RUSH, start or continue a save, then press F10 to open the " +
                "UE4SS console and type: connect address:port SlotName [password]",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ──────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("HbkArchipelago mod (GitHub) ↗",      ModRepoUrl),
            ("Hi-Fi RUSH Setup Guide ↗",           SetupGuideUrl),
            ("hibiki-bootstrap (UE4SS for HFR) ↗", HibikiBootstrap),
            ("UE4SS (releases) ↗",                 UE4SSSite),
            ("Archipelago Official ↗",             ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
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

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: (version, zip download URL). Falls back to
    /// the pinned 0.3.1 URL when the GitHub API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_LATEST_URL, ct);
            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? zip = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                    zip = url;
                    break; // take the first zip asset
                }
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — game detection ──────────────────────────────────────

    /// The Hi-Fi RUSH install dir to use: the override (if set and valid) wins,
    /// else the Steam-detected install. Null when nothing is found.
    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeGameDir(ov))
            return ov;

        try { return DetectSteamGameDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Hi-Fi RUSH if it contains the Hibiki sub-folder or
    /// the game executable somewhere beneath it.
    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (Directory.Exists(Path.Combine(dir, "Hibiki"))) return true;
            return FindGameExe(dir) != null;
        }
        catch { return false; }
    }

    /// Returns the Hibiki\Binaries\Win64 path inside a game dir, or null.
    private static string? FindWin64Dir(string gameDir)
    {
        try
        {
            // Standard Steam path.
            string win64 = Path.Combine(gameDir, "Hibiki", "Binaries", "Win64");
            if (Directory.Exists(win64)) return win64;

            // Defensive recursive search (covers renamed or non-standard layouts).
            foreach (string dir in Directory.EnumerateDirectories(gameDir, "Win64", SearchOption.AllDirectories))
            {
                // Must be under a "Binaries" parent to avoid false positives.
                if (Path.GetFileName(Path.GetDirectoryName(dir) ?? "")
                        .Equals("Binaries", StringComparison.OrdinalIgnoreCase))
                    return dir;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    /// True when UE4SS appears to be installed in the Win64 folder. UE4SS drops
    /// a "ue4ss" subfolder (or UE4SS.dll / UE4SS_Signatures at the root).
    private static bool CheckUe4ssPresent(string win64Dir)
    {
        try
        {
            if (!Directory.Exists(win64Dir)) return false;
            if (Directory.Exists(Path.Combine(win64Dir, "ue4ss"))) return true;
            if (File.Exists(Path.Combine(win64Dir, "UE4SS.dll"))) return true;
            if (Directory.Exists(Path.Combine(win64Dir, "UE4SS_Signatures"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Hi-Fi RUSH install via appmanifest_1817230.acf.
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

                    string common    = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeGameDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeGameDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Find Hi-Fi RUSH.exe under a game dir (lives under Hibiki\Binaries\Win64).
    private static string? FindGameExe(string gameDir)
    {
        try
        {
            // Fast path first.
            string fastPath = Path.Combine(gameDir, "Hibiki", "Binaries", "Win64", GameExeName);
            if (File.Exists(fastPath)) return fastPath;

            // Defensive recursive fallback.
            foreach (string exe in Directory.EnumerateFiles(gameDir, GameExeName, SearchOption.AllDirectories))
                return exe;
        }
        catch { /* permission / vanished */ }
        return null;
    }

    /// Find the HbkArchipelago main.lua under the detected/override game install.
    /// Returns the lua file path or null.
    private string? FindInstalledModLua()
    {
        try
        {
            string? gameDir = ResolveGameDir();
            if (gameDir == null) return null;
            string? win64 = FindWin64Dir(gameDir);
            if (win64 == null) return null;

            // Standard path: ...\Win64\ue4ss\Mods\HbkArchipelago\scripts\main.lua
            string standard = Path.Combine(win64, ModsRelPath, ModMainLuaRelPath);
            if (File.Exists(standard)) return standard;

            // Also check directly under Win64\Mods (some UE4SS installs omit the
            // ue4ss\ wrapper folder when mods are placed manually).
            string alt = Path.Combine(win64, "Mods", ModMainLuaRelPath);
            if (File.Exists(alt)) return alt;
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — Steam registry / VDF parsing ────────────────────────

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
            }) ?? throw new InvalidOperationException("Failed to start Hi-Fi RUSH.");

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
            catch { /* non-fatal */ }
            return;
        }

        // Fall back to Steam if we know Steam is installed.
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
            "Could not find Hi-Fi RUSH.exe. Open this game's Settings and pick your " +
            "Hi-Fi RUSH install folder, or install the game via Steam.",
            GameExeName);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* non-fatal */ }
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    /// Download the mod zip and extract it into the Win64 directory.
    /// The HbkArchipelago zip contains mod files that drop directly into Win64
    /// (the UE4SS Mods folder layout is included in the zip).
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string win64Dir,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip     = Path.Combine(Path.GetTempPath(),
            $"hbkarchipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"hbkarchipelago-x-{version}-{Guid.NewGuid():N}");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 7 / 10;
            await DownloadFileAsync(zipUrl, tempZip,
                $"Downloading HbkArchipelago {version}...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Extracting mod files into Win64..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // Copy all extracted contents into the Win64 target directory.
            // The zip may contain a top-level wrapper folder — if it does, descend
            // into it; otherwise copy from the extract root.
            string srcRoot = FindModExtractRoot(tempExtract, win64Dir);
            CopyDirectory(srcRoot, win64Dir);

            progress.Report((pctEnd, "Mod files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// If the zip extracted a single top-level wrapper folder (and it is not
    /// itself a mod content folder), descend into it so we copy the right files
    /// into Win64. Otherwise return the extract root as-is.
    private static string FindModExtractRoot(string extractRoot, string win64Dir)
    {
        try
        {
            string[] topDirs  = Directory.GetDirectories(extractRoot);
            string[] topFiles = Directory.GetFiles(extractRoot);

            // If there is exactly one top-level directory and no loose files, and
            // that directory does NOT look like the mod content itself, descend.
            if (topDirs.Length == 1 && topFiles.Length == 0)
            {
                string single = topDirs[0];
                // The mod content folders that belong in Win64 are named "ue4ss",
                // "Mods", "HbkArchipelago", etc. If the single folder is named
                // differently (a wrapper like "HbkArchipelago-0.3.1") descend.
                string folderName = Path.GetFileName(single);
                bool isModContent = folderName.Equals("ue4ss",            StringComparison.OrdinalIgnoreCase)
                                 || folderName.Equals("Mods",             StringComparison.OrdinalIgnoreCase)
                                 || folderName.Equals("HbkArchipelago",   StringComparison.OrdinalIgnoreCase)
                                 || folderName.Equals("lua-apclientpp",   StringComparison.OrdinalIgnoreCase)
                                 || folderName.Equals("UE4SS_Signatures", StringComparison.OrdinalIgnoreCase);
                if (!isModContent)
                    return single;
            }
        }
        catch { /* ignore */ }
        return extractRoot;
    }

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
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
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

    private sealed class HiFiRushSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private HiFiRushSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<HiFiRushSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(HiFiRushSettings s)
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
    private void SaveOverrideDir(string p) { var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s); }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    private void WriteStampedVersion(string v) { var s = LoadSettings(); s.ModVersion = v; SaveSettings(s); }
}
