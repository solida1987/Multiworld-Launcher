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

namespace LauncherV2.Plugins.FlipWitch;

// ═══════════════════════════════════════════════════════════════════════════════
// FlipWitchPlugin — install / launch for "FlipWitch - Forbidden Sex Hex" (2023,
// MomoGames / Critical Bliss) played through the FlipwitchAPClient BepInEx mod
// by Witchybun, which contains the in-game Archipelago client. This is a NATIVE
// "ConnectsItself" integration: the BepInEx mod speaks directly to the AP server
// — no emulator, no Lua bridge, no launcher-held ApClient on the slot.
//
// FlipWitch is a side-scrolling Metroidvania published on Steam (appid 1748620
// — UNVERIFIED, verify against the Steam store page). The AP mod (FlipwitchAPClient)
// is one of the most actively maintained community mods, with 57+ releases; the
// latest verified release as of 2026-06-19 is v1.1.7 (May 2026).
//
// NOTE: This game contains adult content (R18+). The launcher surfaces this
// information in the settings panel. The base game is the user's own legally-owned
// copy of FlipWitch on Steam.
//
// ── HONEST REALITY CHECK (2026-06-19, research session) ─────────────────────
//
//   * THE AP WORLD game string is (UNVERIFIED — inferred from the apworld file):
//     "FlipWitch - Forbidden Sex Hex". GameId here = "flipwitch". If the actual
//     apworld uses a different string, update ApWorldName below before shipping.
//
//   * THE MOD repo is Witchybun/FlipwitchAPClient (verified active on GitHub).
//     It is a BepInEx C# mod. The mod connects to the AP server in-game via its
//     own UI; the launcher cannot pre-fill connection details. Latest release
//     v1.1.7 (May 2026), 57+ releases, highly active.
//
//   * STEAM APPID 1748620 — UNVERIFIED. Verify against the Steam store page for
//     "FlipWitch - Forbidden Sex Hex" before shipping.
//
//   * STEAM COMMON FOLDER "FlipWitch" — UNVERIFIED. The actual steamapps\common
//     folder name must be confirmed against a real install's appmanifest.
//
//   * EXE NAME "FlipWitch.exe" — UNVERIFIED. Unity games sometimes use a different
//     name. Confirm against a real install.
//
//   * BEPINEX REQUIREMENT: BepInEx must be installed into the FlipWitch game
//     directory BEFORE the AP mod will work. The launcher says this clearly and
//     cannot automate BepInEx installation (it modifies the game's boot scripts).
//     The mod's own BepInEx plugins DLL is staged by InstallOrUpdateAsync.
//
//   * CONNECTION is made IN-GAME via the mod's own connection UI. The launcher
//     cannot pre-write a connection config (no documented path for this mod).
//     The settings panel surfaces the session host/port/slot for the user to copy.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam FlipWitch install via the Windows registry (HKCU SteamPath
//      + HKLM WOW6432Node InstallPath), parsing steamapps\libraryfolders.vdf and
//      locating steamapps\common\FlipWitch via appmanifest_1748620.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and takes
//      precedence; validated (must contain FlipWitch.exe and/or BepInEx/) and
//      persisted in this plugin's OWN sidecar
//      (Games/ROMs/flipwitch/flipwitch_launcher.json).
//   2. INSTALL/UPDATE (best effort) = download the FlipwitchAPClient release zip
//      from Witchybun/FlipwitchAPClient/releases/latest, find the .zip asset, and
//      extract it into <FlipWitch>/BepInEx/plugins/. BepInEx itself must already be
//      installed by the user. The plugin says this clearly. Never a fake one-click.
//   3. LAUNCH = run FlipWitch.exe from the detected/override install; if the exe
//      cannot be found but Steam is present, fall back to steam://rungameid/1748620.
//      ConnectsItself = true (the BepInEx mod owns the slot — the launcher must NOT
//      hold its own ApClient on it). SupportsStandalone = true (plain FlipWitch runs
//      fine without AP). No connection prefill (entered in-game).
//
// ── DEFENSIVE / UNVERIFIED ────────────────────────────────────────────────────
//   * "Installed" is judged by the presence of any Flipwitch*.dll under the
//     detected/override game's BepInEx/plugins/ tree (recursive, case-insensitive).
//   * Steam library parsing uses the same tolerant VDF scan as all other plugins.
//   * No plaintext AP password is ever written by this plugin.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class FlipWitchPlugin : IGamePlugin
{
    // ── Constants — the FlipwitchAPClient mod (Witchybun, GitHub) ─────────────
    private const string MOD_OWNER = "Witchybun";
    private const string MOD_REPO  = "FlipwitchAPClient";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl =
        "https://archipelago.gg/games/FlipWitch%20-%20Forbidden%20Sex%20Hex/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";
    private const string BepInExUrl      = "https://github.com/BepInEx/BepInEx/releases";

    // Steam — appid 1748620 (UNVERIFIED — verify against the Steam store page).
    private const string FwAppId     = "1748620";   // UNVERIFIED
    private static readonly string SteamRunUrl = $"steam://rungameid/{FwAppId}";

    /// Standard Steam common folder name for FlipWitch (UNVERIFIED).
    private const string SteamCommonFolderName = "FlipWitch";   // UNVERIFIED

    /// Base-game executable name (Unity, UNVERIFIED — confirm against a real install).
    private const string FwExeName = "FlipWitch.exe";   // UNVERIFIED

    /// Glob pattern used to detect the mod DLL under BepInEx/plugins.
    private const string ModDllPattern = "Flipwitch*.dll";

    /// Pinned fallback version when the GitHub API is unreachable.
    private const string FallbackVersion = "1.1.7";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "flipwitch";
    public string DisplayName => "FlipWitch";
    public string Subtitle    => "PC · Archipelago mod";

    /// UNVERIFIED — inferred from the apworld filename. If the actual apworld uses
    /// a different game string, update this before shipping.
    public string ApWorldName => "FlipWitch - Forbidden Sex Hex";   // UNVERIFIED

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "flipwitch.png");

    public string ThemeAccentColor => "#9C27B0";   // witch purple
    public string[] GameBadges     => new[] { "R18+ Adult Content", "Requires FlipWitch on Steam" };

    public string Description =>
        "FlipWitch - Forbidden Sex Hex (2023) is a side-scrolling Metroidvania platformer " +
        "(MomoGames/Critical Bliss) in which the witch Jack battles through monster-filled " +
        "dungeons, collecting abilities and expanding the map. The Archipelago mod by Witchybun " +
        "is one of the most actively maintained community mods, with 57+ releases and full " +
        "randomization of collectibles, boss encounters, shop items, and quest progression. " +
        "Note: this game contains adult content and is rated R18+. Requires: your own " +
        "legally-owned copy on Steam. Install BepInEx into your game directory first, then " +
        "use the launcher to stage the AP mod files. Connect via the in-game connection UI.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means a Flipwitch*.dll is present under the detected/override
    /// game's BepInEx/plugins/ tree. We do NOT gate on our own stamp — the user
    /// may have installed the mod manually.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "FlipWitch");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "flipwitch_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The FlipwitchAPClient mod reports checks/items/goal to the AP server itself
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
        // 0. We need a FlipWitch install to drop the mod into.
        progress.Report((2, "Locating your FlipWitch installation..."));
        string? fwDir = ResolveFlipWitchDir();
        if (fwDir == null)
            throw new InvalidOperationException(
                "Could not find a FlipWitch installation. Open this game's Settings and " +
                "pick your FlipWitch folder (the one containing FlipWitch.exe), or install " +
                "FlipWitch via Steam first. The Archipelago mod is added on top of your own " +
                "copy of the game.");

        // 1. Resolve the latest mod release.
        progress.Report((6, "Checking the latest FlipwitchAPClient release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the FlipwitchAPClient mod download on GitHub. " +
                "Check your internet connection, or download the mod manually from " +
                ModRepoUrl + "/releases. " +
                "Extract the contents into your FlipWitch/BepInEx/plugins/ folder.");

        // 2. Download + extract the mod zip INTO <FlipWitch>/BepInEx/plugins/.
        //    HONEST: BepInEx itself must already be installed by the user. This only
        //    stages the AP mod files.
        string pluginsDir = Path.Combine(fwDir, "BepInEx", "plugins");
        await DownloadAndExtractModAsync(zipUrl, version, pluginsDir, progress, ct);

        // 3. Stamp the version.
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool dllOk = FindInstalledModDll() != null;
        progress.Report((100,
            $"Staged the FlipwitchAPClient mod {version} into your BepInEx/plugins/ folder" +
            (dllOk ? "." : " (verify the files landed).") +
            " IMPORTANT: BepInEx must already be installed in your FlipWitch directory for " +
            "the mod to load. If you have not installed BepInEx yet, download it from " +
            BepInExUrl + " and follow the standard BepInEx install instructions. " +
            "To play: launch FlipWitch and use the in-game connection UI to connect to " +
            "your Archipelago server."));
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
        // HONEST: the AP server connection for FlipWitch is entered via the mod's
        // own in-game connection UI. There is no documented command-line / config
        // prefill. The settings panel surfaces the session host/port/slot for the
        // user to copy into the in-game UI.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartFlipWitch();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) FlipWitch runs perfectly well.
    public bool SupportsStandalone => true;

    /// The FlipwitchAPClient BepInEx mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartFlipWitch();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The FlipwitchAPClient mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid FlipWitch folder contains
    /// FlipWitch.exe and/or a BepInEx directory (Unity BepInEx install). Returns
    /// null when acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your FlipWitch install folder.";

        if (LooksLikeFlipWitchDir(folder))
            return null;

        // Forgiving: user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeFlipWitchDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a FlipWitch installation. Pick the folder that " +
               "contains FlipWitch.exe (for Steam this is usually " +
               @"...\steamapps\common\FlipWitch). Note: the exe name is UNVERIFIED — " +
               "if your install uses a different exe name, check the Settings panel.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Adult content / honesty header ────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "ADULT CONTENT NOTICE: FlipWitch - Forbidden Sex Hex is an R18+ adult " +
                   "game. By using this plugin you confirm you are of legal age in your " +
                   "jurisdiction to access adult content. This launcher does not verify age.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 10),
            FontWeight = System.Windows.FontWeights.SemiBold,
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "FlipWitch is your own game (Steam) with the FlipwitchAPClient BepInEx " +
                   "mod added on top. The launcher detects your Steam install and can stage " +
                   "the AP mod files, but BepInEx itself must be installed by you first (see " +
                   "guided steps below). You connect to your server via the mod's in-game " +
                   "connection UI. These external steps are not verified by this launcher. " +
                   "Note: Steam appid, common folder name, and exe name are UNVERIFIED — " +
                   "verify these against a real install if detection fails.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "FLIPWITCH INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? fwDir       = ResolveFlipWitchDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = fwDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + fwDir
                : "Detected Steam install: " + fwDir)
            : "FlipWitch not detected. Pick your install folder below, or install " +
              "FlipWitch via Steam first. (Steam appid 1748620 — UNVERIFIED.)";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = fwDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // BepInEx and mod status lines
        bool bepinexOk = fwDir != null && Directory.Exists(Path.Combine(fwDir, "BepInEx"));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = bepinexOk
                    ? "BepInEx found in your FlipWitch directory."
                    : "BepInEx NOT found. Install BepInEx into your FlipWitch directory " +
                      "before installing the AP mod — see the guided steps below.",
            FontSize = 11, Foreground = bepinexOk ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        string? modDll = FindInstalledModDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                    ? "FlipwitchAPClient mod found: " + modDll
                    : "FlipwitchAPClient mod not found in BepInEx/plugins/ yet (use " +
                      "Install on the Play tab after installing BepInEx).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? fwDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your FlipWitch install folder (the one containing FlipWitch.exe). " +
                          "Detected from Steam automatically; set it here to override.",
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
                Title            = "Select your FlipWitch install folder (contains FlipWitch.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? fwDir ?? "")
                                   ? (overrideDir ?? fwDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a FlipWitch folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeFlipWitchDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeFlipWitchDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 1748620 — UNVERIFIED). " +
                   "Use this picker if detection fails or if you use a non-standard Steam library.",
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
            Text = "Launch FlipWitch — the FlipwitchAPClient mod provides an in-game " +
                   "connection UI. Enter your Server Address, Port, Slot Name, and Password " +
                   "(if any) there to connect to your Archipelago server. This launcher " +
                   "cannot pre-fill the connection details.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own FlipWitch - Forbidden Sex Hex on Steam (appid 1748620 — UNVERIFIED). " +
                "Install the game if you have not already. Use the folder picker above if " +
                "your install was not detected automatically.",
            "2. Install BepInEx into your FlipWitch game directory. Download BepInEx " +
                "(link below), extract it so that BepInEx/ is a folder directly inside your " +
                "FlipWitch install folder (alongside FlipWitch.exe), and run the game once " +
                "to let BepInEx initialize. This is required before the AP mod will load.",
            "3. Use the Install button on the Play tab to stage the FlipwitchAPClient mod " +
                "files into BepInEx/plugins/. Alternatively, download the release zip from " +
                "the mod repo (link below) and extract it into BepInEx/plugins/ yourself.",
            "4. Launch FlipWitch from the Play tab. The FlipwitchAPClient mod will load " +
                "automatically via BepInEx.",
            "5. Use the in-game connection UI to enter your Server Address, Port, Slot Name, " +
                "and Password (if any), then connect. (This launcher cannot pre-fill the " +
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
            ("FlipwitchAPClient (GitHub mod repo) ↗", ModRepoUrl),
            ("FlipwitchAPClient releases (download mod) ↗", ModRepoUrl + "/releases"),
            ("BepInEx releases (download BepInEx) ↗", BepInExUrl),
            ("FlipWitch AP game info ↗", SetupGuideUrl),
            ("Archipelago Official ↗", ArchipelagoSite),
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

    /// Resolve the latest mod release: version + the mod zip download URL. Prefers
    /// any .zip asset; falls back to the pinned 1.1.7 direct URL when the API is
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
                string? preferred = null;   // Flipwitch*.zip
                string? anyZip    = null;   // any .zip
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null && lower.Contains("flipwitch"))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        // Pinned fallback — construct the direct URL if the GitHub API is down.
        // The actual asset name for v1.1.7 is not verified; adjust if needed.
        string fallbackUrl =
            $"{ModRepoUrl}/releases/download/v{FallbackVersion}/FlipwitchAPClient.zip";
        return (FallbackVersion, fallbackUrl);
    }

    // ── Private helpers — Steam / FlipWitch detection ─────────────────────────

    private string? ResolveFlipWitchDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeFlipWitchDir(ov))
            return ov;

        try { return DetectSteamFlipWitchDir(); }
        catch { return null; }
    }

    /// A folder "looks like" FlipWitch if it has FlipWitch.exe (UNVERIFIED name)
    /// or a BepInEx subdirectory (a reasonable proxy for a mod-enabled Unity game).
    private static bool LooksLikeFlipWitchDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, FwExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, "BepInEx"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam FlipWitch install via registry + libraryfolders.vdf.
    private static string? DetectSteamFlipWitchDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{FwAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeFlipWitchDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeFlipWitchDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
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

    // ── Private helpers — installed-mod detection ─────────────────────────────

    /// Find any Flipwitch*.dll under the detected/override game's BepInEx/plugins/
    /// tree (recursive, case-insensitive). Returns the dll path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? fw = ResolveFlipWitchDir();
            if (fw == null) return null;
            string pluginsDir = Path.Combine(fw, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(dll);
                if (name.StartsWith("Flipwitch", StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartFlipWitch()
    {
        string? fw  = ResolveFlipWitchDir();
        string? exe = fw != null ? Path.Combine(fw, FwExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = fw!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start FlipWitch.");

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
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { /* fall through to the error below */ }
        }

        throw new FileNotFoundException(
            "Could not find FlipWitch.exe (UNVERIFIED exe name). Open this game's Settings " +
            "and pick your FlipWitch install folder, or install FlipWitch via Steam. " +
            "If your exe has a different name, the folder picker will still work as long as " +
            "the BepInEx/ folder is present.",
            FwExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the FlipwitchAPClient zip and extract it into <FlipWitch>/BepInEx/plugins/.
    /// Existing files are overwritten; sibling files are preserved.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string pluginsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"flipwitch-ap-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"flipwitch-ap-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading FlipwitchAPClient {version}..."));
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
                        progress.Report((pct, $"Downloading FlipwitchAPClient... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Installing the mod into your BepInEx/plugins/ folder..."));
            Directory.CreateDirectory(pluginsDir);
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // If the zip wraps everything in a single top-level folder, flatten it.
            string mergeRoot = tempExtract;
            string[] subdirs = Directory.GetDirectories(mergeRoot);
            string[] files   = Directory.GetFiles(mergeRoot);
            if (subdirs.Length == 1 && files.Length == 0)
            {
                // Check if the single subfolder contains DLLs (the actual mod content)
                // or if it is itself the plugins/ folder wrapper.
                string sub = subdirs[0];
                string subName = Path.GetFileName(sub).ToLowerInvariant();
                if (subName == "plugins" || subName == "bepinex")
                {
                    // Extract from the BepInEx wrapper: merge into the game BepInEx/ dir
                    // (one level up from pluginsDir).
                    mergeRoot = Path.Combine(Path.GetDirectoryName(pluginsDir)!, "..");
                    MergeDirectory(tempExtract, mergeRoot);
                }
                else
                {
                    mergeRoot = sub;
                    MergeDirectory(mergeRoot, pluginsDir);
                }
            }
            else
            {
                // Flat zip: extract directly into plugins/.
                MergeDirectory(mergeRoot, pluginsDir);
            }

            progress.Report((90, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    private static void MergeDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            try { File.Copy(file, dst, overwrite: true); }
            catch { /* locked file (game open?) — skip; retry with game closed */ }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class FwSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private FwSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<FwSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(FwSettings s)
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
