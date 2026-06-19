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

namespace LauncherV2.Plugins.AgainstTheStorm;

// ═══════════════════════════════════════════════════════════════════════════════
// AgainstTheStormPlugin — install / launch for "Against the Storm" (Eremite Games,
// 2023), played through the Archipelago BepInEx mod (Thunderstore:
// ATS_for_AP_Team/Against_The_Storm_for_Archipelago). This is a NATIVE
// "ConnectsItself" integration — the game mod speaks to the AP server directly.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// BASE GAME is the user's own legally-owned copy of Against the Storm (Steam
// appid 1379290). Archipelago is a BepInEx mod added on top.
//
//   * THE AP WORLD game string is "Against the Storm" (verified against
//     worlds/against_the_storm/__init__.py: `game = "Against the Storm"`).
//     GameId = "against_the_storm". apworld version 1.2.0.
//
//   * THE MOD is the Thunderstore package "ATS_for_AP_Team/
//     Against_The_Storm_for_Archipelago" v1.2.0 (verified live 2026-06-14).
//     Mod DLL: Ryguy9999.ATS.ATSForAP.dll (extracted from the Thunderstore zip).
//     Dependencies (NOT bundled in the mod zip — must be installed separately
//     via Thunderstore Mod Manager or r2modman):
//         BepInEx-BepInExPack-5.4.2304   (the mod loader)
//         ATS_API_Devs-API-3.6.1          (the ATS modding API)
//
//   * CRITICAL HONESTY — THE THUNDERSTORE ZIP IS NOT SELF-SUFFICIENT. The mod
//     package contains only the Archipelago plugin DLL plus supporting
//     Archipelago client libraries (Archipelago.MultiClient.Net.dll,
//     Archipelago.Gifting.Net.dll). It does NOT bundle BepInEx or ATS_API.
//     The OFFICIAL and RECOMMENDED install route is the Thunderstore Mod Manager
//     or r2modman, which resolves every dependency in one step. This plugin
//     stages what it CAN from the mod zip; the user needs BepInEx + ATS_API
//     for the mod to run. This is stated clearly — never a fake one-click.
//
//   * CONNECTION is made IN-GAME via the developer console (default backtick key
//     `). The verified command is:
//         ap.connect <url>:<port> "<slotName>" [password]
//     Example: ap.connect archipelago.gg:38281 "MyATSPlayer"
//     There is also ap.c (shorthand using last-used url:port + slotName) and
//     ap.connectForce (override for different slot/url). The mod maintains
//     per-profile connection memory. There is NO config file the launcher can
//     pre-write to seed the connection — the settings panel surfaces the
//     session's server/slot for the user to copy into the in-game console.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Against the Storm install via Windows registry (Steam
//      path + libraryfolders.vdf scan + appmanifest_1379290.acf). A manual
//      install-dir override is also supported, validated, and persisted in this
//      plugin's OWN sidecar (Games/ROMs/against_the_storm/
//      against_the_storm_launcher.json).
//   2. INSTALL/UPDATE (best effort) = download the Thunderstore mod zip and
//      extract the plugin DLL into <ATS>\BepInEx\plugins\ArchipelagoATS\. The
//      plugin ALSO presents clear guided steps + links so the user can install
//      everything via the Thunderstore Mod Manager — the recommended route.
//   3. LAUNCH = run "Against the Storm.exe" from the detected/override install;
//      if the exe cannot be found but Steam is present, fall back to
//      steam://rungameid/1379290. ConnectsItself = true (the mod owns the slot).
//      SupportsStandalone = true (game runs perfectly without AP).
//
// ── BUILD NOTE ────────────────────────────────────────────────────────────────
//   UseWindowsForms=true alongside UseWPF=true → all WPF UI types that also
//   exist in WinForms are spelled with FULL namespaces below (CS0104).
//   No file-level "using X = System.Windows...;" aliases (CS1537).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class AgainstTheStormPlugin : IGamePlugin
{
    // ── Constants — Thunderstore package (verified 2026-06-14) ───────────────
    private const string TS_NAMESPACE = "ATS_for_AP_Team";
    private const string TS_NAME      = "Against_The_Storm_for_Archipelago";

    /// Thunderstore community + package landing page.
    private const string ModPackageUrl =
        $"https://thunderstore.io/c/against-the-storm/p/{TS_NAMESPACE}/{TS_NAME}/";

    /// Thunderstore "experimental" package API — returns the package's version
    /// history (latest first) with each version's download_url.
    private const string TS_PACKAGE_API_URL =
        $"https://thunderstore.io/api/experimental/package/{TS_NAMESPACE}/{TS_NAME}/";

    private const string ThunderstoreModManagerSite =
        "https://www.overwolf.com/app/thunderstore-thunderstore_mod_manager";
    private const string SetupGuideUrl =
        "https://github.com/RyanCirincione/ArchipelagoATS/blob/main/worlds/against_the_storm/docs/setup_en.md";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Against the Storm appid 1379290.
    private const string AtsSteamAppId       = "1379290";
    private static readonly string SteamRunUrl = $"steam://rungameid/{AtsSteamAppId}";

    private const string SteamCommonFolderName = "Against the Storm";
    private const string GameExeName           = "Against the Storm.exe";

    /// Pinned fallback version when the Thunderstore API is unreachable.
    private const string FallbackVersion = "1.2.0";
    private static readonly string FallbackZipUrl =
        $"https://thunderstore.io/package/download/{TS_NAMESPACE}/{TS_NAME}/{FallbackVersion}/";

    /// Expected DLL name inside the mod zip (verified by extracting 1.2.0).
    private const string ModDllName = "Ryguy9999.ATS.ATSForAP.dll";

    /// BepInEx core sub-folder used to detect BepInEx presence in the game dir.
    private const string BepInExCoreSubDir = "BepInEx";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "against_the_storm";
    public string DisplayName => "Against the Storm";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/against_the_storm/__init__.py
    /// (`game = "Against the Storm"`).
    public string ApWorldName => "Against the Storm";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "against_the_storm.png");

    public string ThemeAccentColor => "#C87A3A";   // amber-ember tone for ATS
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Against the Storm, the city-builder roguelite by Eremite Games, played " +
        "through the Archipelago mod — a BepInEx plugin that acts as an in-game " +
        "Archipelago client, so the game connects to the multiworld itself with no " +
        "emulator and no Lua bridge. Buildings, resources, biomes and progression " +
        "are shuffled across the multiworld. You bring your own copy of Against the " +
        "Storm (owned on Steam), and the Archipelago mod is added on top via the " +
        "Thunderstore Mod Manager, which also installs the mods it depends on " +
        "(BepInEx and ATS_API). The launcher detects your Steam install, can stage " +
        "the Archipelago mod files, and guides the rest. You connect to your server " +
        "from the in-game developer console (backtick key).";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the AP ATS mod DLL is present in a detected/override ATS
    /// install's BepInEx\plugins tree. (We do NOT gate on our own stamp — the user
    /// may have installed via the Thunderstore Mod Manager, which we honor.)
    public bool IsInstalled => FindInstalledModPlugin() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "AgainstTheStorm");

    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "against_the_storm_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Archipelago ATS mod reports checks/items/goal to the AP server itself —
    // the launcher relays nothing. These exist for interface compatibility
    // (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Lifecycle — ConnectsItself / SupportsStandalone ───────────────────────

    /// The Archipelago ATS mod owns the slot connection (see header).
    public bool ConnectsItself    => true;
    /// Against the Storm runs fine without the Archipelago mod.
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = FindInstalledModPlugin() != null
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
        // 0. Find the Against the Storm install. Prefer explicit override; else
        //    auto-detect the Steam install.
        progress.Report((2, "Locating your Against the Storm installation..."));
        string? gameDir = ResolveAtsDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find an Against the Storm installation. Open this game's " +
                "Settings and pick your Against the Storm folder (the one containing " +
                "\"Against the Storm.exe\"), or install Against the Storm via Steam " +
                "first. The Archipelago mod is added on top of your own copy.");

        string pluginsDir = Path.Combine(gameDir, "BepInEx", "plugins");
        string apModDir   = Path.Combine(pluginsDir, "ArchipelagoATS");

        // 1. Resolve the latest mod release (pinned fallback when offline).
        progress.Report((6, "Checking the latest Archipelago mod release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Archipelago Against the Storm mod download on " +
                "Thunderstore. Check your internet connection, or install the mod " +
                "with the Thunderstore Mod Manager (recommended) — see Settings. " +
                "The mod package is at: " + ModPackageUrl);

        // 2. Download + extract the mod zip into <ATS>\BepInEx\plugins\ArchipelagoATS\.
        //    HONEST: this stages the Archipelago plugin + its bundled AP client libs
        //    only. BepInEx (BepInExPack 5.4.2304) and ATS_API (3.6.1) are NOT in
        //    this zip and must be provided via the Thunderstore Mod Manager.
        await DownloadAndExtractModAsync(zipUrl, version, apModDir, progress, ct);

        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepInExPresent = Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));
        progress.Report((100,
            $"Staged the Archipelago mod {version} into your Against the Storm " +
            "BepInEx\\plugins folder. " +
            (bepInExPresent
                ? "BepInEx looks present. "
                : "IMPORTANT: this download does NOT include BepInEx or ATS_API, " +
                  "which the Archipelago mod requires. The recommended way to " +
                  "install everything is the Thunderstore Mod Manager (one step " +
                  "installs the mod + all dependencies) — open Settings for guided " +
                  "steps and links. ") +
            "To play: launch the game, open the developer console (backtick key), " +
            "and run: ap.connect <url>:<port> \"<slotName>\" [password]"));
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
        // HONEST: AP connection for ATS is entered IN-GAME via the developer
        // console (backtick → `ap.connect <url>:<port> "<slotName>" [password]`).
        // There is no command-line / config prefill available (verified — see header).
        // Launching from this tile starts the (modded) game; the user connects
        // in-game using the session credentials shown in the Settings panel.
        // ConnectsItself = true: the launcher must NOT hold its own ApClient.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartAgainstTheStorm();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartAgainstTheStorm();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        // No AP credentials are ever written to disk by this plugin (connection
        // is entered in-game), so there is nothing to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (mod owns the AP connection, see header) ────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var amber   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC8, 0x7A, 0x3A));

        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        string? gameDir     = ResolveAtsDir();
        string? overrideDir = LoadOverrideDir();
        string? modPlugin   = FindInstalledModPlugin();
        bool    bepInExOk   = gameDir != null && Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Against the Storm is your own game (Steam) with the Archipelago " +
                   "mod added on top via BepInEx. The mod needs BepInEx (5.4.2304) and " +
                   "ATS_API (3.6.1), which it does not bundle — the recommended way to " +
                   "install everything in one step is the Thunderstore Mod Manager " +
                   "(see guided steps below). You connect to your AP server from the " +
                   "in-game developer console (backtick key).",
            FontSize = 11, Foreground = warn, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: install detection / override ─────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "AGAINST THE STORM INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Against the Storm not detected. Pick your install folder below, or " +
              "install it via Steam first.";
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
                    ? "BepInEx found (BepInEx\\core present)."
                    : "BepInEx not found yet — install it via the Thunderstore Mod Manager.",
            FontSize = 11, Foreground = bepInExOk ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modPlugin != null
                    ? "Archipelago mod found: " + modPlugin
                    : "Archipelago mod not found yet in BepInEx\\plugins (use Install on " +
                      "the Play tab, or install it via the Thunderstore Mod Manager).",
            FontSize = 11, Foreground = modPlugin != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Against the Storm install folder (the one containing " +
                          "\"Against the Storm.exe\"). Detected from Steam automatically; " +
                          "set it here to override.",
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
                Title            = "Select your Against the Storm install folder",
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
                    System.Windows.MessageBox.Show(bad, "Not an Against the Storm folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If user picked the Steam "common" parent, descend into game folder.
                if (!LooksLikeAtsDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeAtsDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 1379290). Use " +
                   "this picker for a non-standard Steam library or manual install.",
            FontSize = 11, Foreground = muted, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connecting (in-game) ─────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (via in-game developer console)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Launch the game (modded via BepInEx). Press the backtick key (`) " +
                   "to open the developer console and run:",
            FontSize = 11, Foreground = fg, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "  ap.connect <server>:<port> \"<SlotName>\" [password]",
            FontSize = 11, Foreground = amber,
            FontFamily = new System.Windows.Media.FontFamily("Consolas, Courier New, monospace"),
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Example: ap.connect archipelago.gg:38281 \"ATSPlayer\"\n" +
                   "Shorthand (reuses last url/port/slot): ap.c\n" +
                   "This launcher cannot pre-fill the connection — enter it in-game.",
            FontSize = 11, Foreground = fg, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP (recommended: Thunderstore Mod Manager)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Against the Storm (Steam). Install it if you have not. Use " +
                "\"Select folder...\" above if it was not detected.",
            "2. Install the Thunderstore Mod Manager from the link below and select " +
                "Against the Storm as the game.",
            "3. In the Thunderstore Mod Manager, search for \"Against the Storm for " +
                "Archipelago\" (by ATS_for_AP_Team) and install it. Its dependencies " +
                "(BepInEx 5.4.2304 and ATS_API 3.6.1) are installed automatically. " +
                "Then launch the game modded.",
            "4. Alternative (advanced): the Install button on the Play tab stages the " +
                "mod's plugin DLL into your BepInEx\\plugins\\ArchipelagoATS folder, " +
                "but does NOT include BepInEx or ATS_API — you still need those from " +
                "the Thunderstore Mod Manager.",
            "5. To play: launch the game (modded). Press the backtick key to open the " +
                "developer console and run: ap.connect <server>:<port> \"<SlotName>\" " +
                "[password]. Your YAML must have a matching slot name.",
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
            ("Thunderstore Mod Manager ↗",                ThunderstoreModManagerSite),
            ("Archipelago ATS mod (Thunderstore) ↗",      ModPackageUrl),
            ("Against the Storm Setup Guide ↗",           SetupGuideUrl),
            ("Archipelago Official ↗",                    ArchipelagoSite),
        })
        {
            string u = url;
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
        try
        {
            string json = await _http.GetStringAsync(TS_PACKAGE_API_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("versions", out var versions) ||
                versions.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in versions.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("date_created", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                string ver = el.TryGetProperty("version_number", out var v) ? v.GetString() ?? "" : "";

                items.Add(new NewsItem(
                    Title:   "Archipelago ATS " + ver,
                    Body:    el.TryGetProperty("description", out var b) ? b.GetString() ?? "" : "",
                    Version: ver,
                    Date:    date,
                    Url:     el.TryGetProperty("download_url", out var u) ? u.GetString() : ModPackageUrl
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(TS_PACKAGE_API_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Shape A: { latest: { version_number, download_url } }
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("latest", out var latest) &&
                latest.ValueKind == JsonValueKind.Object)
            {
                string? ver = latest.TryGetProperty("version_number", out var lv) ? lv.GetString() : null;
                string? url = latest.TryGetProperty("download_url",   out var lu) ? lu.GetString() : null;
                if (!string.IsNullOrWhiteSpace(ver) && !string.IsNullOrWhiteSpace(url))
                    return (ver!, url);
            }

            // Shape B: { versions: [ { version_number, download_url }, ... ] }
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("versions", out var versions) &&
                versions.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in versions.EnumerateArray())
                {
                    string? ver = el.TryGetProperty("version_number", out var ev) ? ev.GetString() : null;
                    string? url = el.TryGetProperty("download_url",   out var eu) ? eu.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(ver) && !string.IsNullOrWhiteSpace(url))
                        return (ver!, url);
                    break; // only first (latest) entry
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — Steam / ATS detection ───────────────────────────────

    private string? ResolveAtsDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeAtsDir(ov))
            return ov;

        try { return DetectSteamAtsDir(); }
        catch { return null; }
    }

    private static bool LooksLikeAtsDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

    private static string? DetectSteamAtsDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{AtsSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common    = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeAtsDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeAtsDir(conventional)) return conventional;
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

    private string? FindInstalledModPlugin()
    {
        try
        {
            string? game = ResolveAtsDir();
            if (game == null) return null;
            string pluginsDir = Path.Combine(game, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            // 1. The known DLL name (Ryguy9999.ATS.ATSForAP.dll) anywhere under plugins.
            foreach (string dll in Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name.IndexOf("ATSForAP", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("ArchipelagoATS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }

            // 2. A plugins sub-folder named like Archipelago or ATS that holds a DLL
            //    (handles Thunderstore Mod Manager profile layouts).
            foreach (string sub in Directory.EnumerateDirectories(pluginsDir, "*", SearchOption.AllDirectories))
            {
                string folder = Path.GetFileName(sub);
                if (folder.IndexOf("Archipelago", StringComparison.OrdinalIgnoreCase) < 0 &&
                    folder.IndexOf("ATSForAP",    StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                try
                {
                    if (Directory.EnumerateFiles(sub, "*.dll", SearchOption.AllDirectories).Any())
                        return sub;
                }
                catch { /* permission — keep scanning */ }
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — install validation ──────────────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Against the Storm install folder.";

        if (LooksLikeAtsDir(folder))
            return null;

        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeAtsDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like an Against the Storm installation. Pick the " +
               "folder that contains \"Against the Storm.exe\" — for Steam this is " +
               @"usually ...\steamapps\common\Against the Storm.";
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartAgainstTheStorm()
    {
        string? game = ResolveAtsDir();
        string? exe  = game != null ? Path.Combine(game, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = game!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Against the Storm.");

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

        // Fall back to Steam if Steam is installed.
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
            "Could not find \"Against the Storm.exe\". Open this game's Settings " +
            "and pick your Against the Storm install folder, or install it via Steam.",
            GameExeName);
    }

    // ── Private helpers — download / extract mod ──────────────────────────────

    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string apModDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"ats-archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"ats-archipelago-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading Archipelago ATS mod {version}..."));
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
                        int pct = (int)(10 + 55 * downloaded / total);
                        progress.Report((pct, $"Downloading... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting the mod package..."));
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            progress.Report((85, "Installing mod into the Against the Storm plugins folder..."));
            Directory.CreateDirectory(apModDir);

            // The ATS mod's Thunderstore zip places the DLLs at the zip root
            // (manifest.json, icon.png, Ryguy9999.ATS.ATSForAP.dll, and the two
            // Archipelago.*.dll at the same level). Copy everything to apModDir.
            string payloadRoot = ResolvePluginPayloadRoot(tempDir);
            CopyDirectoryContents(payloadRoot, apModDir);

            progress.Report((95, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))   File.Delete(tempZip); }            catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static string ResolvePluginPayloadRoot(string extractedRoot)
    {
        try
        {
            // 1. A nested BepInEx/plugins folder.
            foreach (string dir in Directory.EnumerateDirectories(
                         extractedRoot, "plugins", SearchOption.AllDirectories))
            {
                string parent = Path.GetFileName(Path.GetDirectoryName(dir) ?? "");
                if (parent.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) &&
                    DirectoryHasDll(dir))
                    return dir;
            }

            // 2. A top-level "plugins" folder.
            string topPlugins = Path.Combine(extractedRoot, "plugins");
            if (Directory.Exists(topPlugins) && DirectoryHasDll(topPlugins))
                return topPlugins;
        }
        catch { /* fall through */ }

        // 3. Zip root (ATS Thunderstore zip layout — DLLs next to manifest.json).
        return extractedRoot;
    }

    private static bool DirectoryHasDll(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }

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

    private sealed class AtsSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private AtsSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<AtsSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(AtsSettings s)
    {
        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
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
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings(); s.ModVersion = v; SaveSettings(s);
    }
}
