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

namespace LauncherV2.Plugins.Astalon;

// ═══════════════════════════════════════════════════════════════════════════════
// AstalonPlugin — install / launch for "Astalon: Tears of the Earth"
// (GDNom / Yacht Club Games, 2021) played through the Archipelago-Astalon mod
// by drtchops. This is a NATIVE "ConnectsItself" integration — the mod connects
// to the AP server itself with no emulator and no Lua bridge.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Astalon: Tears of the Earth (Steam appid 1046400; verified via the official
// AP setup guide which links store.steampowered.com/app/1046400/), and
// Archipelago is delivered as a BepInEx IL2CPP mod added on top.
//
//   * THE AP WORLD game string is "Astalon" (verified against
//     worlds/astalon/constants.py: `GAME_NAME: Final[str] = "Astalon"`).
//     GameId here = "astalon".
//
//   * THE MOD repo is drtchops/Archipelago-Astalon (GitHub). Latest STABLE
//     release: v0.27.1 (published 2026-03-12). Pre-releases v1.0.0-rc1/rc2/rc3
//     exist as of 2026-05-27 but are marked pre-release. The stable release
//     ships two assets:
//         Archipelago.Astalon-v0.27.0.zip   (~394 KB, the mod itself)
//         astalon.apworld                   (the AP world package)
//     The zip contains BepInEx plugin DLLs that drop into:
//         <GameDir>/BepInEx/plugins/Archipelago.Astalon/
//
//   * CRITICAL HONESTY — BepInEx IL2CPP IS A PREREQUISITE. The mod requires
//     BepInEx IL2CPP v6 x86 (bleeding-edge build 688 verified against the
//     setup guide; x64 does NOT work). This is NOT bundled in the mod zip.
//     Installation order per the official setup guide:
//         1. Install BepInEx IL2CPP v6 x86 → extract into game folder
//         2. Run the game once so BepInEx generates its config
//         3. Extract the mod zip into the game folder
//     The plugin guides the user through these steps honestly and links to the
//     exact BepInEx build. A best-effort install of the mod DLLs is performed
//     but BepInEx itself is NOT auto-installed (it requires running the game
//     first to let BepInEx hook IL2CPP — a step we cannot automate).
//
//   * CONNECTION CONFIG — PREFILL IS POSSIBLE AND IMPLEMENTED:
//     The mod reads its connection credentials from a standard BepInEx CFG file
//     at:  <GameDir>/BepInEx/config/Archipelago.cfg
//     (INI format with section [Archipelago]). Verified against Plugin.cs in the
//     mod source, which binds:
//         [Archipelago] uri     = "archipelago.gg:38281"
//         [Archipelago] slotName = "Player1"
//         [Archipelago] password = ""
//     The mod also supports an "archipelago://" command-line URL for one-shot
//     connection (verified in Plugin.cs: AttemptAutomaticConnection), but the
//     config-file approach is simpler and does not require CLI argument passing
//     through Steam. This plugin writes the CFG file on LaunchAsync and clears
//     the password entry on StopAsync.
//
//   * GAME EXE: Astalon.exe (at <GameDir>/Astalon.exe, verified via
//     [BepInProcess("Astalon.exe")] in the mod's Plugin.cs).
//
//   * IN-GAME UI: When not connected, the mod shows a connection panel in the
//     bottom-right corner of the screen where the user can type credentials and
//     press Connect. The config-file prefill means the user can just launch and
//     click Connect, or let the game use the pre-filled values.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Astalon install via the Windows registry, parsing
//      steamapps/libraryfolders.vdf for every library root, and locating
//      steamapps/common/Astalon Tears of the Earth via appmanifest_1046400.acf.
//      A manual install-dir OVERRIDE is also supported.
//   2. INSTALL/UPDATE = download the Archipelago.Astalon zip from GitHub and
//      extract it into <GameDir>/BepInEx/plugins/Archipelago.Astalon/. Because
//      BepInEx itself is NOT in the zip and requires its own install-then-run
//      step, the plugin presents clear guided steps + links so the user can
//      complete the BepInEx install. Never a fake one-click.
//   3. PREFILL: On LaunchAsync, writes the session host/slot/password into
//      <GameDir>/BepInEx/config/Archipelago.cfg. On StopAsync, scrubs the
//      password entry from the config.
//   4. LAUNCH = run Astalon.exe from the detected/override install; if the exe
//      cannot be found but Steam is present, fall back to steam://rungameid/1046400.
//      ConnectsItself = true. SupportsStandalone = true.
//
// ── BUILD NOTE ────────────────────────────────────────────────────────────────
//   UseWindowsForms=true alongside UseWPF=true → CS0104. Every WPF type that
//   also exists in WinForms is spelled with its FULL namespace below.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class AstalonPlugin : IGamePlugin
{
    // ── Constants — Steam ─────────────────────────────────────────────────────
    private const string SteamAppId          = "1046400";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    // The conventional install folder name Steam uses for the game.
    private const string SteamCommonFolderName = "Astalon Tears of the Earth";
    private const string GameExeName           = "Astalon.exe";

    // ── Constants — Mod ───────────────────────────────────────────────────────
    private const string ModOwner   = "drtchops";
    private const string ModRepo    = "Archipelago-Astalon";
    private const string ModRepoUrl = $"https://github.com/{ModOwner}/{ModRepo}";
    private const string GhReleasesLatestUrl =
        $"https://api.github.com/repos/{ModOwner}/{ModRepo}/releases/latest";
    private const string GhReleasesUrl =
        $"https://api.github.com/repos/{ModOwner}/{ModRepo}/releases";

    // BepInEx IL2CPP v6 x86 (bleeding-edge build 688) — verified against the
    // official AP setup guide as the required build (x64 does NOT work).
    private const string BepInExDirectUrl =
        "https://builds.bepinex.dev/projects/bepinex_be/688/" +
        "BepInEx-Unity.IL2CPP-win-x86-6.0.0-be.688%2B4901521.zip";
    private const string BepInExBuildsPage =
        "https://builds.bepinex.dev/projects/bepinex_be";

    private const string SetupGuideUrl =
        "https://github.com/drtchops/Archipelago/blob/astalon/worlds/astalon/docs/setup_en.md";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Pinned fallback: v0.27.1 stable, verified 2026-03-12.
    private const string FallbackVersion = "0.27.1";
    private const string FallbackZipName = "Archipelago.Astalon-v0.27.0.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/{FallbackZipName}";

    // BepInEx config file path (relative to game dir) and INI section.
    private const string BepInExConfigRelPath =
        @"BepInEx\config\Archipelago.cfg";
    // Mod plugin subfolder inside BepInEx\plugins\.
    private const string PluginFolderName = "Archipelago.Astalon";
    private const string PluginDllName    = "Archipelago.Astalon.dll";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "astalon";
    public string DisplayName => "Astalon: Tears of the Earth";
    public string Subtitle    => "Native PC · BepInEx mod";

    /// EXACT AP game string — verified against worlds/astalon/constants.py
    /// (`GAME_NAME: Final[str] = "Astalon"`).
    public string ApWorldName => "Astalon";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "astalon.png");

    public string ThemeAccentColor => "#4A1A6B";   // deep Stygian violet

    public string[] GameBadges => new[] { "Steam · needs mod", "BepInEx IL2CPP" };

    public string Description =>
        "Astalon: Tears of the Earth is an action platformer Metroidvania where you " +
        "fight through a cursed tower as three unique adventurers — Algus, Arias and " +
        "Kyuli — each with distinct abilities you must combine to progress. The " +
        "Archipelago-Astalon mod by drtchops turns the game into a multiworld " +
        "randomizer: relics, keys, characters, elevator access and more are shuffled " +
        "across the multiworld, and the game connects to the Archipelago server itself " +
        "through the BepInEx mod (no emulator, no Lua bridge). You bring your own copy " +
        "of Astalon: Tears of the Earth (owned on Steam), and the mod is installed on " +
        "top via BepInEx IL2CPP v6 x86. The launcher detects your Steam install, can " +
        "stage the mod files, pre-fills your server / slot / password into the BepInEx " +
        "config, and guides BepInEx setup. You then connect from the in-game AP panel.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Launcher working directory for this plugin (downloads, sidecar).
    /// The actual mod lives in the user's Astalon install tree, not here.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Astalon");

    private string SidecarDir =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SidecarPath =>
        Path.Combine(SidecarDir, "astalon_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The mod connects to the AP server itself; the launcher relays nothing.
    // These exist for interface compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself / SupportsStandalone ───────────────────────────────────

    /// The Archipelago.Astalon mod owns the slot connection; the launcher must
    /// NOT hold its own ApClient on this slot while the mod is running.
    public bool ConnectsItself => true;

    /// Plain Astalon (without the mod active) runs perfectly.
    public bool SupportsStandalone => true;

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
            var (version, _) = await ResolveLatestModAsync(ct);
            AvailableVersion = version;
        }
        catch
        {
            AvailableVersion = null; // never throw on network failure
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((2, "Locating your Astalon: Tears of the Earth installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find an Astalon: Tears of the Earth installation. " +
                "Open this game's Settings and pick your Astalon folder (the one " +
                "containing Astalon.exe), or install the game via Steam first. " +
                "The Archipelago mod is added on top of your own copy of the game.");

        // Check whether BepInEx is present (user must have installed it first).
        string bepInExDir = Path.Combine(gameDir, "BepInEx");
        bool bepInExPresent = Directory.Exists(bepInExDir)
            && File.Exists(Path.Combine(bepInExDir, "core", "BepInEx.Core.dll"));

        progress.Report((6, "Checking the latest Archipelago-Astalon mod release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Archipelago-Astalon mod download on GitHub. " +
                "Check your internet connection, or download the mod manually from " +
                ModRepoUrl + "/releases/latest and extract it into your Astalon folder.");

        // Extract the mod into BepInEx/plugins/Archipelago.Astalon/.
        string pluginsDir   = Path.Combine(bepInExDir, "plugins", PluginFolderName);
        await DownloadAndExtractModAsync(zipUrl, version, pluginsDir, progress, ct);

        WriteStampedVersion(version);
        InstalledVersion = version;

        string bepInExWarning = bepInExPresent
            ? ""
            : " WARNING: BepInEx IL2CPP v6 x86 (build 688) was NOT detected in your " +
              "Astalon folder. You MUST install BepInEx first (see Setup Guide in " +
              "Settings), run the game once to initialize it, and THEN the mod will " +
              "load correctly.";

        progress.Report((100,
            $"Installed Archipelago-Astalon mod {version} into your Astalon folder." +
            bepInExWarning +
            " To play: launch the game, wait for BepInEx to initialize (first launch " +
            "may be slow), then enter your Archipelago server, slot name and password " +
            "in the AP panel at the bottom-right of the screen and press Connect. " +
            "Start a new save file to begin your randomizer run."));
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
        // Prefill the BepInEx config with the session credentials. The mod reads
        // [Archipelago] uri / slotName / password from BepInEx/config/Archipelago.cfg.
        // This means the user can connect without retyping the address in-game.
        TryWriteBepInExConfig(session.ServerUri, session.SlotName, session.Password);
        StartAstalon();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // Plain launch — no AP config prefill.
        StartAstalon();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;

        // Scrub the password from the BepInEx config for security.
        TryScrubBepInExConfigPassword();

        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself) ───────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The Archipelago.Astalon mod receives items from the AP server directly.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Astalon: Tears of the Earth is your own game (Steam) with the " +
                "Archipelago-Astalon mod added on top. The mod requires BepInEx " +
                "IL2CPP v6 x86 (specific build 688 — see links below) installed " +
                "first; after installing BepInEx you must run the game once before " +
                "the mod will load. The launcher can install the mod files for you " +
                "and will pre-fill your AP server, slot name and password into the " +
                "BepInEx config before launch. Connection details are confirmed " +
                "in-game via the AP panel at the bottom-right corner.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "ASTALON INSTALL",
            FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? overrideDir = LoadOverrideDir();
        string? gameDir     = ResolveGameDir();
        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Found (manual override): " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Astalon: Tears of the Earth not detected. Pick your install folder " +
              "below or install the game on Steam first.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // BepInEx status
        bool hasBepInEx = gameDir != null && Directory.Exists(
            Path.Combine(gameDir, "BepInEx", "core"));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = hasBepInEx
                ? "BepInEx detected in the game folder."
                : "BepInEx NOT detected. You must install BepInEx IL2CPP v6 x86 " +
                  "(build 688) and run the game once before the mod will load.",
            FontSize = 11, Foreground = hasBepInEx ? success : warn,
            TextWrapping = TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        // Mod DLL status
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                ? "Archipelago-Astalon mod found: " + modDll
                : "Archipelago-Astalon mod not found (use Install on the Play tab " +
                  "after BepInEx is set up).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Install folder row
        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Astalon: Tears of the Earth install folder (the one " +
                          "containing Astalon.exe). Detected from Steam automatically; " +
                          "set it here to override (non-standard Steam library).",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Astalon: Tears of the Earth install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                                   ? (overrideDir ?? gameDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateAstalonDir(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not an Astalon folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                SaveOverrideDir(picked);
                dirBox.Text = picked;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Steam installs are detected automatically (appid 1046400). " +
                   "Use this picker for non-standard Steam library locations.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP",
            FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Astalon: Tears of the Earth on Steam. Install it if you have not.",
            "2. Download BepInEx IL2CPP v6 x86 build 688 from the link below. IMPORTANT: " +
                "you MUST use this specific build and the x86 version — the x64 version " +
                "does not work with Astalon.",
            "3. Extract the BepInEx zip into your Astalon folder (the same folder as " +
                "Astalon.exe). The BepInEx folder should appear next to Astalon.exe.",
            "4. Run Astalon.exe once and close it. BepInEx needs this run to generate " +
                "its configuration files.",
            "5. Use the Install button on the Play tab to download and install the " +
                "Archipelago-Astalon mod. Or download it manually from the mod repo " +
                "(link below) and extract it into your Astalon folder.",
            "6. Launch from the Play tab. The launcher pre-fills your AP server / slot / " +
                "password into the BepInEx config before starting the game.",
            "7. In-game: an AP connection panel appears in the bottom-right corner. " +
                "Click Connect. Then start a new save file to begin your run.",
            "8. When resuming a save, load the save file — the mod reconnects to the " +
                "same AP server automatically.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS",
            FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("BepInEx IL2CPP v6 x86 build 688 (direct download) ↗", BepInExDirectUrl),
            ("BepInEx bleeding-edge builds page ↗",                  BepInExBuildsPage),
            ("Archipelago-Astalon mod (GitHub) ↗",                   ModRepoUrl),
            ("Mod releases page ↗",                                   ModRepoUrl + "/releases"),
            ("Setup Guide ↗",                                          SetupGuideUrl),
            ("Archipelago Official ↗",                                 ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding             = new System.Windows.Thickness(0, 2, 0, 2),
                Background          = System.Windows.Media.Brushes.Transparent,
                BorderThickness     = new System.Windows.Thickness(0),
                FontSize = 12, Margin = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); }
                catch { }
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
            string json = await _http.GetStringAsync(GhReleasesUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d)
                    && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t)
                             ? NormalizeTag(t.GetString()) ?? "" : "",
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

    /// Resolve the latest STABLE (non-prerelease) mod release. Falls back to the
    /// pinned v0.27.1 URL when the GitHub API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(
        CancellationToken ct)
    {
        try
        {
            // Use the releases list (not /latest) so we can skip pre-releases.
            string json = await _http.GetStringAsync(GhReleasesUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Unexpected API response.");

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                // Skip pre-releases.
                if (el.TryGetProperty("prerelease", out var pr) && pr.GetBoolean())
                    continue;
                if (el.TryGetProperty("draft", out var dr) && dr.GetBoolean())
                    continue;

                string? version = el.TryGetProperty("tag_name", out var t)
                    ? NormalizeTag(t.GetString())
                    : null;

                if (version == null) continue;

                if (el.TryGetProperty("assets", out var assets)
                    && assets.ValueKind == JsonValueKind.Array)
                {
                    string? preferred = null;
                    string? anyZip    = null;
                    foreach (var a in assets.EnumerateArray())
                    {
                        string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        string? url  = a.TryGetProperty("browser_download_url", out var u)
                                       ? u.GetString() : null;
                        if (name == null || url == null) continue;
                        string lower = name.ToLowerInvariant();
                        if (!lower.EndsWith(".zip")) continue;
                        anyZip ??= url;
                        if (preferred == null && lower.Contains("archipelago"))
                            preferred = url;
                    }
                    string? zip = preferred ?? anyZip;
                    if (zip != null) return (version, zip);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — Steam / game detection ──────────────────────────────

    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeAstalonDir(ov)) return ov;
        try { return DetectSteamGameDir(); }
        catch { return null; }
    }

    private static bool LooksLikeAstalonDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, GameExeName))
                || Directory.Exists(Path.Combine(dir, "Astalon_Data"));
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

                    string common     = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeAstalonDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeAstalonDir(conventional)) return conventional;
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

    // ── Private helpers — mod detection ───────────────────────────────────────

    /// Find Archipelago.Astalon.dll under the detected/override game install's
    /// BepInEx\plugins tree (recursive, case-insensitive). Returns the dll path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? dir = ResolveGameDir();
            if (dir == null) return null;
            string pluginsDir = Path.Combine(dir, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(
                pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dll).Equals(
                    PluginDllName, StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { }
        return null;
    }

    // ── Private helpers — BepInEx config prefill ──────────────────────────────

    /// Write the AP credentials into <GameDir>/BepInEx/config/Archipelago.cfg
    /// using the BepInEx INI format the mod reads (section [Archipelago]).
    private void TryWriteBepInExConfig(string serverUri, string slotName, string password)
    {
        try
        {
            string? gameDir = ResolveGameDir();
            if (gameDir == null) return;

            string cfgPath = Path.Combine(gameDir, BepInExConfigRelPath);
            string cfgDir  = Path.GetDirectoryName(cfgPath)!;
            if (!Directory.Exists(cfgDir)) return; // BepInEx not installed yet

            // Read existing cfg if any, merge our keys, then write back.
            var lines = File.Exists(cfgPath)
                ? new List<string>(File.ReadAllLines(cfgPath))
                : new List<string>();

            SetIniValue(lines, "Archipelago", "uri",      serverUri);
            SetIniValue(lines, "Archipelago", "slotName", slotName);
            SetIniValue(lines, "Archipelago", "password", password);

            File.WriteAllLines(cfgPath, lines, new UTF8Encoding(false));
        }
        catch { /* non-fatal — the user can enter credentials in-game */ }
    }

    /// Overwrite the [Archipelago] password key with an empty string for security.
    private void TryScrubBepInExConfigPassword()
    {
        try
        {
            string? gameDir = ResolveGameDir();
            if (gameDir == null) return;
            string cfgPath = Path.Combine(gameDir, BepInExConfigRelPath);
            if (!File.Exists(cfgPath)) return;

            var lines = new List<string>(File.ReadAllLines(cfgPath));
            SetIniValue(lines, "Archipelago", "password", "");
            File.WriteAllLines(cfgPath, lines, new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    /// Set a key=value pair in an INI-style list of lines (BepInEx CFG format).
    /// Creates the section and key if absent; updates the key if present.
    private static void SetIniValue(List<string> lines, string section, string key, string value)
    {
        string sectionHeader = $"[{section}]";
        int sectionLine = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim().Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
            {
                sectionLine = i;
                break;
            }
        }

        if (sectionLine < 0)
        {
            // Section does not exist — append it.
            if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[^1]))
                lines.Add("");
            lines.Add(sectionHeader);
            lines.Add($"{key} = {value}");
            return;
        }

        // Section exists — look for the key within the section.
        for (int i = sectionLine + 1; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim();
            if (trimmed.StartsWith('[')) break; // hit the next section
            if (trimmed.StartsWith(key + " =", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"{key} = {value}";
                return;
            }
        }

        // Key not found in section — insert after the section header.
        lines.Insert(sectionLine + 1, $"{key} = {value}");
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartAstalon()
    {
        string? dir = ResolveGameDir();
        string? exe = dir != null ? Path.Combine(dir, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = dir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Astalon.exe.");

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
            catch { }
            return;
        }

        // Fall back to Steam if we can locate it.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true;
                return;
            }
            catch { }
        }

        throw new FileNotFoundException(
            "Could not find Astalon.exe. Open this game's Settings and pick your " +
            "Astalon: Tears of the Earth install folder, or install the game via Steam.",
            GameExeName);
    }

    // ── Private helpers — download / extract mod ──────────────────────────────

    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string pluginDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"astalon-mod-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((10, $"Downloading Archipelago-Astalon mod {version}..."));
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
                        int pct = (int)(10 + 60 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading mod... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Installing mod into BepInEx plugins folder..."));
            Directory.CreateDirectory(pluginDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, pluginDir, overwriteFiles: true);

            // If the zip wraps everything in one subfolder, flatten it.
            if (!File.Exists(Path.Combine(pluginDir, PluginDllName)))
            {
                string[] subdirs = Directory.GetDirectories(pluginDir);
                if (subdirs.Length == 1)
                {
                    string sub = subdirs[0];
                    foreach (string srcFile in Directory.EnumerateFiles(
                        sub, "*", SearchOption.AllDirectories))
                    {
                        string rel     = Path.GetRelativePath(sub, srcFile);
                        string dstFile = Path.Combine(pluginDir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dstFile)!);
                        File.Move(srcFile, dstFile, overwrite: true);
                    }
                    try { Directory.Delete(sub, recursive: true); } catch { }
                }
            }

            progress.Report((90, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — install validation ──────────────────────────────────

    private static string? ValidateAstalonDir(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist.";
        if (LooksLikeAstalonDir(folder)) return null;
        string nested = Path.Combine(folder, SteamCommonFolderName);
        if (LooksLikeAstalonDir(nested)) return null;
        return "That does not look like an Astalon: Tears of the Earth installation. " +
               "Pick the folder that contains Astalon.exe (the Astalon_Data folder " +
               "should be next to it).";
    }

    // ── Private helpers — self-contained sidecar ──────────────────────────────

    private sealed class AstalonSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private AstalonSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SidecarPath))
            {
                string txt = File.ReadAllText(SidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<AstalonSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(AstalonSettings s)
    {
        try
        {
            Directory.CreateDirectory(SidecarDir);
            File.WriteAllText(SidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { }
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
