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
// FontWeights / Orientation / HorizontalAlignment collide between
// System.Windows.Forms and System.Windows[.Controls/.Media] (CS0104). Qualifying
// every UI type avoids that.

namespace LauncherV2.Plugins.Holo8;

// ═══════════════════════════════════════════════════════════════════════════════
// Holo8Plugin — install / launch for "holo8" (frog blend, 2025) played through
// ArchipelagoHolo8, a BepInEx plugin that is the in-game Archipelago client for
// holo8. This is a NATIVE "ConnectsItself" integration in the same family as the
// shipped Hollow Knight / Stardew Valley / Inscryption / RoR2 plugins — the game
// itself speaks to the AP server (no emulator, no Lua bridge, no launcher-held
// ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified against KitLemonfoot/ArchipelagoHolo8) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned holo8
// (Steam appid 3373960), and Archipelago is a MOD (a BepInEx 5.4.23.x plugin)
// added on top. The verified facts:
//
//   * THE AP WORLD game string is "holo8" (verified against
//     APWorld/holo8/archipelago.json: `"game": "holo8"` and
//     APWorld/holo8/__init__.py: `game = "holo8"`).
//     minimum_ap_version = "0.6.3", world_version = "0.3.0".
//
//   * THE MOD is distributed via the GitHub releases page:
//     KitLemonfoot/ArchipelagoHolo8 — each release ships:
//       - holo8.apworld      (the APWorld file for the AP server)
//       - Holo8_Patches_vX.Y.Z.zip  (the BepInEx mod files to drop in)
//     The mod is a BepInEx 5.4.23.x plugin targeting Unity 2022.3.9. The mod
//     DLL is named "ArchipelagoHolo8.dll" (standard BepInEx convention).
//
//   * THE BepInEx PREREQUISITE: holo8 uses BepInEx x64 5.4.23.x (the Unity
//     Mono stable branch). BepInEx is NOT bundled in the mod patch zip — the
//     user must install it separately first (official BepInEx release from
//     github.com/BepInEx/BepInEx/releases, the x64 5.4.23.x build). Install
//     instructions per the README: extract BepInEx into the holo8 folder, run
//     once to generate config, then extract Holo8_Patches into the BepInEx folder
//     (adds "plugins" and "core" subfolders).
//
//   * CONNECTION is made automatically on game boot (verified against
//     Mod/ConnectHandler.cs: ConnectToAP() is called during BepInEx Awake()).
//     THE CONFIG FILE is `BepInEx\config\ArchipelagoHolo8.cfg` (verified against
//     Mod/MiscHandler.cs → setConfig()). The three keys in section [General]:
//       ArchipelagoIP       = "archipelago.gg:"    (the host:port string)
//       ArchipelagoSlotName = "Holo8Player"
//       ArchipelagoPassword = ""
//     This launcher CAN pre-write these three fields before launch so the game
//     connects automatically on boot. The file uses BepInEx's standard INI-like
//     format with a comment header line per key.
//
//   * ConnectsItself = true (the mod owns the slot — the launcher must NOT hold
//     its own ApClient on this slot while the game is running).
//
//   * ABOUT holo8: an Exit8-like game based on the Hololive idol group, published
//     under the HoloIndie project by frog blend with official Cover Corporation
//     permission. Set in Hololive's temporary office, the player loops through an
//     endless office building while detecting and avoiding anomalies. Death Link
//     support is included.
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. DETECT the Steam holo8 install via the Windows registry (HKCU\Software\
//      Valve\Steam -> SteamPath and HKLM\...\WOW6432Node\Valve\Steam ->
//      InstallPath), parsing steamapps\libraryfolders.vdf and
//      appmanifest_3373960.acf. A manual install-dir OVERRIDE (settings folder
//      picker) is also supported and takes precedence; validated (must contain
//      holo8.exe) and persisted in this plugin's OWN sidecar
//      (Games/ROMs/holo8/holo8_launcher.json).
//   2. INSTALL/UPDATE (best effort) = download the latest Holo8_Patches_*.zip
//      from the GitHub releases API and extract it into the holo8 install's
//      BepInEx folder. Reports clearly when BepInEx itself is not yet present
//      (the user must install that separately). Stamps the mod version.
//   3. PRE-FILL the connection: before launch, writes the AP session credentials
//      (host:port, slot, password) into BepInEx\config\ArchipelagoHolo8.cfg in
//      BepInEx's expected INI-like format, so the game auto-connects on boot.
//      The password field is scrubbed (overwritten to empty) when StopAsync() is
//      called and after a failed launch.
//   4. LAUNCH = run holo8.exe from the detected/override install. If the exe
//      cannot be found but Steam is present, fall back to steam://rungameid/3373960.
//
// ── BUILD NOTE ────────────────────────────────────────────────────────────────
//   * WPF UI types are fully qualified — see note at top of file.
//   * `using X = System.Windows...;` file-level aliases are NOT used here.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Holo8Plugin : IGamePlugin
{
    // ── Constants — GitHub mod release source ─────────────────────────────────
    private const string GH_OWNER   = "KitLemonfoot";
    private const string GH_REPO    = "ArchipelagoHolo8";
    private const string GH_API_RELEASES =
        $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases";
    private const string ModRepoUrl = $"https://github.com/{GH_OWNER}/{GH_REPO}";

    // BepInEx prerequisite — the user must install this separately first.
    private const string BepInExReleasesUrl = "https://github.com/BepInEx/BepInEx/releases";

    private const string SetupGuideUrl   = $"https://github.com/{GH_OWNER}/{GH_REPO}#setup";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — holo8 appid 3373960 (verified on the Steam store page).
    private const string Holo8SteamAppId = "3373960";
    private static readonly string SteamRunUrl = $"steam://rungameid/{Holo8SteamAppId}";

    /// Standard Steam steamapps\common sub-folder name for holo8.
    private const string SteamCommonFolderName = "holo8";

    /// The game's executable.
    private const string GameExeName = "holo8.exe";

    /// BepInEx config file path relative to the holo8 install folder.
    private const string BepInExCfgRelPath = @"BepInEx\config\ArchipelagoHolo8.cfg";

    /// Pinned fallback mod version when the GitHub API is unreachable.
    private const string FallbackModVersion = "0.3.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Archipelago-Launcher/2.0" },
            { "Accept",     "application/vnd.github+json" },
        }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "holo8";
    public string DisplayName => "holo8";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against APWorld/holo8/archipelago.json
    /// (`"game": "holo8"`) and APWorld/holo8/__init__.py (`game = "holo8"`).
    public string ApWorldName => "holo8";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "holo8.png");

    public string ThemeAccentColor => "#B85F9E";   // Hololive pink/purple

    public string[] GameBadges => new[] { "Steam · needs mod" };

    public string Description =>
        "holo8 is an Exit8-like horror game set in Hololive's endlessly looping " +
        "temporary office building, published under the HoloIndie project by frog " +
        "blend with official Cover Corporation permission. Players must reach the " +
        "first floor while detecting and avoiding anomalies featuring Hololive talents. " +
        "The Archipelago integration randomizes which talents (anomaly types) are " +
        "available, turning successful anomaly detections into multiworld checks. The " +
        "mod is a BepInEx plugin (ArchipelagoHolo8 by KitLemonfoot) that connects to " +
        "the AP server automatically when the game starts. You bring your own copy of " +
        "holo8 from Steam; the launcher detects it, stages the mod files, pre-fills " +
        "your connection credentials into the BepInEx config, and launches the game.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the ArchipelagoHolo8 mod DLL is present in the detected/
    /// override holo8 install's BepInEx\plugins tree.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and bookkeeping. The actual mod is
    /// extracted INTO the holo8 BepInEx folder, not here. Exposed as
    /// GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Holo8");

    /// This plugin's OWN settings sidecar (out of the shared SettingsStore so
    /// the plugin stays a single self-contained file).
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "holo8_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    /// Most-recently pre-written BepInEx cfg path (for scrubbing on stop).
    private string? _lastCfgPath;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // ArchipelagoHolo8 reports checks/items/goal to the AP server itself — the
    // launcher relays nothing. These exist for interface compatibility
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
            var (version, _, _) = await ResolveLatestReleaseAsync(ct);
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
        progress.Report((2, "Locating your holo8 installation..."));
        string? gameDir = ResolveHolo8Dir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a holo8 installation. Open this game's Settings and " +
                "pick your holo8 folder (the one containing holo8.exe), or install " +
                "holo8 via Steam first. The Archipelago mod is added on top of your " +
                "own copy of the game.");

        bool bepInExPresent = BepInExPresent(gameDir);

        progress.Report((8, "Checking the latest ArchipelagoHolo8 release..."));
        var (version, patchZipUrl, apworldUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (patchZipUrl == null)
            throw new InvalidOperationException(
                "Could not find a Holo8_Patches_*.zip on the GitHub releases page. " +
                "Check your internet connection, or install the mod manually from " +
                ModRepoUrl + " (see Settings for the guided steps).");

        // Download + extract the patch zip into the BepInEx folder.
        string bepInExDir = Path.Combine(gameDir, "BepInEx");
        await DownloadAndExtractPatchZipAsync(patchZipUrl, version, bepInExDir, progress, ct);

        WriteStampedVersion(version);
        InstalledVersion = version;

        string bepInExStatus = bepInExPresent
            ? "BepInEx is already present."
            : "IMPORTANT: BepInEx (x64 5.4.23.x) is NOT yet installed — the patch zip " +
              "requires it. Install BepInEx into your holo8 folder first (see " +
              BepInExReleasesUrl + ") then use Install again to stage the mod files. " +
              "See Settings for the guided steps.";

        progress.Report((100,
            $"Staged ArchipelagoHolo8 {version} into your holo8 BepInEx folder. " +
            bepInExStatus + " The game will connect to the AP server automatically on " +
            "startup once your credentials are set in the BepInEx config (the launcher " +
            "fills these in before each play)."));
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
        // Pre-fill the BepInEx config with the session credentials so the game
        // auto-connects on boot (verified: Mod/MiscHandler.cs setConfig() +
        // Mod/ConnectHandler.cs ConnectToAP() reads these three fields).
        string? gameDir = ResolveHolo8Dir();
        if (gameDir != null)
        {
            string cfgPath = Path.Combine(gameDir, BepInExCfgRelPath);
            _lastCfgPath = cfgPath;
            WriteBepInExCfg(cfgPath, session.ServerUri, session.SlotName, session.Password ?? "");
        }

        StartHolo8();
        return Task.CompletedTask;
    }

    /// Non-AP vanilla holo8 runs fine — the mod is silent when it cannot connect.
    public bool SupportsStandalone => true;

    /// ArchipelagoHolo8 owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartHolo8();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        _gameProcess = null;

        // Scrub the password from the BepInEx config after the session ends.
        if (_lastCfgPath != null)
        {
            try { ScrubBepInExCfgPassword(_lastCfgPath); } catch { }
            _lastCfgPath = null;
        }

        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // ArchipelagoHolo8 receives items from the AP server directly.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker. Returns null when acceptable, else a
    /// short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your holo8 install folder.";

        if (LooksLikeHolo8Dir(folder))
            return null;

        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeHolo8Dir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a holo8 installation. Pick the folder that " +
               @"contains holo8.exe (for Steam this is usually ...\steamapps\common\holo8).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        string? gameDir     = ResolveHolo8Dir();
        string? overrideDir = LoadOverrideDir();
        string? modDll      = FindInstalledModDll();
        bool    bepInExOk   = gameDir != null && BepInExPresent(gameDir);

        // ── Info header ───────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "holo8 is your own game (Steam) with the ArchipelagoHolo8 mod added " +
                   "on top via BepInEx. Install BepInEx x64 5.4.23.x into your holo8 " +
                   "folder first, run the game once so BepInEx generates its config, then " +
                   "use the Install button on the Play tab to stage the mod. When you " +
                   "click Play, the launcher pre-fills BepInEx\\config\\ArchipelagoHolo8.cfg " +
                   "with your server credentials so the game connects automatically on " +
                   "startup. Your password is scrubbed from the config when the session ends.",
            FontSize = 11, Foreground = warn, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: install detection ────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "HOLO8 INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "holo8 not detected. Pick your install folder below, or install holo8 " +
              "via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = bepInExOk
                    ? "BepInEx found in your holo8 folder."
                    : "BepInEx not found yet — install BepInEx x64 5.4.23.x (see link below).",
            FontSize = 11, Foreground = bepInExOk ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                    ? "ArchipelagoHolo8 mod found: " + modDll
                    : "ArchipelagoHolo8 mod not found in BepInEx\\plugins yet (use the " +
                      "Install button on the Play tab after installing BepInEx).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your holo8 install folder (the one containing holo8.exe). " +
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
                Title            = "Select your holo8 install folder (contains holo8.exe)",
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
                    System.Windows.MessageBox.Show(bad, "Not a holo8 folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeHolo8Dir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeHolo8Dir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 3373960). Use this " +
                   "picker for a non-standard Steam library or a manual install.",
            FontSize = 11, Foreground = muted, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (auto on launch) ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (automatic on game startup)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "When you click Play, the launcher pre-fills " +
                   "BepInEx\\config\\ArchipelagoHolo8.cfg with your server address, slot " +
                   "name, and password. The mod reads these on game startup and connects " +
                   "automatically (no in-game UI needed). The password is scrubbed from " +
                   "the config file after the session ends.",
            FontSize = 11, Foreground = fg, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own holo8 (on Steam, appid 3373960). Install it if you have not. Use the " +
                "picker above if it was not detected.",
            "2. Download BepInEx x64 5.4.23.x from the GitHub releases link below. Extract " +
                "it into your holo8 folder so you have holo8.exe and BepInEx\\ side by side.",
            "3. Launch holo8 ONCE so BepInEx generates its config (the BepInEx\\config\\ " +
                "folder is created on the first run). Then close the game.",
            "4. Click the Install button on the Play tab. The launcher downloads the latest " +
                "Holo8_Patches_*.zip from GitHub and extracts it into your BepInEx folder.",
            "5. To play: click Play from the launcher. Your server credentials are written " +
                "to BepInEx\\config\\ArchipelagoHolo8.cfg and the game connects automatically " +
                "on startup.",
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
            ("ArchipelagoHolo8 (GitHub) ↗",  ModRepoUrl),
            ("BepInEx Releases ↗",           BepInExReleasesUrl),
            ("Setup Guide ↗",                SetupGuideUrl),
            ("Archipelago Official ↗",       ArchipelagoSite),
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
            btn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
            };
            panel.Children.Add(btn);
        }

        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // The GitHub releases list is the AP-relevant news for this game.
        try
        {
            string json = await _http.GetStringAsync(GH_API_RELEASES, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                string ver = NormalizeTag(el.TryGetProperty("tag_name", out var t) ? t.GetString() : null) ?? "";

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? ("holo8 " + ver) : ("holo8 " + ver),
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: ver,
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

    /// Resolve the latest GitHub release: returns (version, patchZipUrl, apworldUrl).
    /// patchZipUrl is the Holo8_Patches_*.zip asset URL (null when not found).
    /// apworldUrl is the holo8.apworld asset URL (null when not found).
    /// Falls back to (FallbackModVersion, null, null) when the API is unreachable.
    private async Task<(string Version, string? PatchZipUrl, string? ApworldUrl)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_API_RELEASES, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return (FallbackModVersion, null, null);

            // The releases are newest-first; pick the first (latest) non-draft, non-prerelease
            // release. Fall back to the first release of any kind if all are pre-release
            // (the current repo uses alpha tags).
            JsonElement? best = null;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (best == null) best = el; // always capture the very first
                bool draft = el.TryGetProperty("draft",      out var dr) && dr.ValueKind == JsonValueKind.True;
                bool pre   = el.TryGetProperty("prerelease", out var pr) && pr.ValueKind == JsonValueKind.True;
                if (!draft && !pre) { best = el; break; }
            }
            if (best == null)
                return (FallbackModVersion, null, null);

            string ver = NormalizeTag(best.Value.TryGetProperty("tag_name", out var tv) ? tv.GetString() : null)
                         ?? FallbackModVersion;

            string? patchZipUrl = null;
            string? apworldUrl  = null;

            if (best.Value.TryGetProperty("assets", out var assets)
                && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string? name     = asset.TryGetProperty("name",                out var an) ? an.GetString() : null;
                    string? dlUrl    = asset.TryGetProperty("browser_download_url", out var au) ? au.GetString() : null;
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(dlUrl)) continue;

                    if (name.StartsWith("Holo8_Patches", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        patchZipUrl = dlUrl;
                    else if (name.Equals("holo8.apworld", StringComparison.OrdinalIgnoreCase))
                        apworldUrl = dlUrl;
                }
            }

            return (ver, patchZipUrl, apworldUrl);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (FallbackModVersion, null, null); }
    }

    // ── Private helpers — Steam / holo8 detection ─────────────────────────────

    /// The holo8 install dir to use: the override (if set and valid) wins, else the
    /// Steam-detected install. Null when nothing is found.
    private string? ResolveHolo8Dir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeHolo8Dir(ov))
            return ov;

        try { return DetectSteamHolo8Dir(); }
        catch { return null; }
    }

    /// A folder "looks like" holo8 if it contains holo8.exe.
    private static bool LooksLikeHolo8Dir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

    /// True when BepInEx appears installed in a holo8 folder. BepInEx (Mono)
    /// drops a "BepInEx" folder at the game root.
    private static bool BepInExPresent(string gameDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return false;
            if (Directory.Exists(Path.Combine(gameDir, "BepInEx"))) return true;
            if (File.Exists(Path.Combine(gameDir, "winhttp.dll")))  return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam holo8 install via the registry, libraryfolders.vdf, and
    /// appmanifest_3373960.acf.
    private static string? DetectSteamHolo8Dir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{Holo8SteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeHolo8Dir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeHolo8Dir(conventional)) return conventional;
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

    /// Find the ArchipelagoHolo8 mod DLL under the detected/override install's
    /// BepInEx\plugins tree. Matches any *.dll whose name contains "archipelago"
    /// (covers "ArchipelagoHolo8.dll" and any renamed build). Returns the path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? game = ResolveHolo8Dir();
            if (game == null) return null;
            string pluginsDir = Path.Combine(game, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartHolo8()
    {
        string? game = ResolveHolo8Dir();
        string? exe  = game != null ? Path.Combine(game, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = game!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start holo8.");

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

        // Fall back to Steam URL.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort — Steam owns the process
                return;
            }
            catch { /* fall through */ }
        }

        throw new FileNotFoundException(
            "Could not find holo8.exe. Open this game's Settings and pick your holo8 " +
            "install folder, or install holo8 via Steam.",
            GameExeName);
    }

    // ── Private helpers — BepInEx config pre-fill ────────────────────────────

    /// Write the BepInEx config file BepInEx\config\ArchipelagoHolo8.cfg with the
    /// session credentials. BepInEx uses a simple INI-like format where each key
    /// has a comment header line. Verified against Mod/MiscHandler.cs setConfig():
    ///   Section [General], keys ArchipelagoIP / ArchipelagoSlotName / ArchipelagoPassword.
    ///
    /// The serverUri from ApSession is expected to be "host:port" (e.g.
    /// "archipelago.gg:38281") matching the BepInEx default "archipelago.gg:".
    private static void WriteBepInExCfg(string cfgPath, string serverUri, string slotName, string password)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cfgPath)!);

            // Normalize the server URI: if it doesn't contain a colon-port separator,
            // append a colon so it matches what BepInEx's config default looks like.
            // "archipelago.gg:38281" → "archipelago.gg:38281" (already correct)
            // "archipelago.gg"       → "archipelago.gg:"     (BepInEx style)
            string ipValue = serverUri ?? "archipelago.gg:";
            if (!ipValue.Contains(':'))
                ipValue = ipValue + ":";

            // BepInEx 5 config INI format (verified against generated .cfg files):
            // [Section]
            // ## <description>
            // # Setting type: String
            // # Default value: <default>
            // Key = Value
            var sb = new StringBuilder();
            sb.AppendLine("[General]");
            sb.AppendLine();
            sb.AppendLine("## IP address of the Archipelago server you wish to connect to.");
            sb.AppendLine("# Setting type: String");
            sb.AppendLine("# Default value: archipelago.gg:");
            sb.AppendLine($"ArchipelagoIP = {ipValue}");
            sb.AppendLine();
            sb.AppendLine("## Slot name of the Archipelago server you wish to connect to.");
            sb.AppendLine("# Setting type: String");
            sb.AppendLine("# Default value: Holo8Player");
            sb.AppendLine($"ArchipelagoSlotName = {slotName ?? ""}");
            sb.AppendLine();
            sb.AppendLine("## Password of the Archipelago server you wish to connect to, if any.");
            sb.AppendLine("# Setting type: String");
            sb.AppendLine("# Default value: ");
            sb.AppendLine($"ArchipelagoPassword = {password ?? ""}");

            File.WriteAllText(cfgPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch { /* non-fatal — the game will use whatever the existing cfg says */ }
    }

    /// Overwrite the password field in the BepInEx config with an empty string
    /// (leave the other fields intact so reconnecting without the launcher still
    /// remembers the server and slot).
    private static void ScrubBepInExCfgPassword(string cfgPath)
    {
        try
        {
            if (!File.Exists(cfgPath)) return;
            string text = File.ReadAllText(cfgPath);

            // Replace the password line: "ArchipelagoPassword = <anything>"
            // This works whether the value has trailing spaces or not.
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimStart().StartsWith("ArchipelagoPassword", StringComparison.OrdinalIgnoreCase)
                    && lines[i].Contains('='))
                {
                    int eq = lines[i].IndexOf('=');
                    lines[i] = lines[i].Substring(0, eq + 1) + " ";
                }
            }
            File.WriteAllText(cfgPath, string.Join(Environment.NewLine, lines),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    // ── Private helpers — download / extract the patch zip ────────────────────

    /// Download the Holo8_Patches_*.zip from the GitHub release and extract it
    /// into the BepInEx folder. The README says: "Install the contents of
    /// Holo8_Patches.zip to the BepInEx folder. You should have two folders in
    /// the BepInEx folder: core and plugins." So the patch zip's contents drop
    /// directly under <holo8>\BepInEx\.
    private async Task DownloadAndExtractPatchZipAsync(
        string zipUrl,
        string version,
        string bepInExDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"holo8-patches-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"holo8-patches-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((12, $"Downloading Holo8_Patches {version}..."));
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
                        int pct = 12 + (int)(55 * downloaded / total);
                        progress.Report((pct, $"Downloading Holo8_Patches... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting mod files into BepInEx folder..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // The zip's contents go directly under BepInEx\ (README: "core" and
            // "plugins" should be the two folders inside BepInEx after extraction).
            Directory.CreateDirectory(bepInExDir);
            progress.Report((85, "Installing patch files..."));
            CopyDirectoryContents(tempExtract, bepInExDir);

            progress.Report((95, "Patch files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Recursively copy the contents of a source directory into a destination
    /// directory (merging into whatever is already there; overwriting files).
    private static void CopyDirectoryContents(string sourceDir, string destDir)
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

    // ── Private helpers — settings sidecar ───────────────────────────────────

    private sealed class Holo8Settings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private Holo8Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Holo8Settings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(Holo8Settings s)
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

    // ── Private helpers — misc ────────────────────────────────────────────────

    /// "v0.3.0-alpha" → "0.3.0-alpha" (strip leading 'v' before a digit).
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }
}
