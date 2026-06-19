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

namespace LauncherV2.Plugins.EnderLilies;

// ═══════════════════════════════════════════════════════════════════════════════
// EnderLiliesPlugin — install / launch for "Ender Lilies: Quietus of the Knights"
// (Binary Haze Interactive, 2021) played through the EnderLilies.Randomizer mod
// by Trexounay, which is a LIVESPLIT COMPONENT that acts as the Archipelago client
// for the game. This is a NATIVE "ConnectsItself" integration: the LiveSplit
// component speaks to the AP server directly (no emulator, no Lua bridge, no
// launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM + LIVESPLIT + RANDOMIZER-COMPONENT native. The BASE GAME is the
// user's own legally-owned copy of Ender Lilies: Quietus of the Knights (Steam
// appid 1322810). Archipelago is delivered by a LiveSplit component
// (EnderLilies.Randomizer) that:
//   1. Reads the game's state via memory scanning (the Randomizer talks to the
//      running Ender Lilies process through a C++ SDK / shared memory layer).
//   2. Connects to the Archipelago server using Archipelago.MultiClient.Net
//      (AP game string = "Ender Lilies", confirmed in ArchipelagoSession.cs).
//   3. Communicates items/locations back to the game via Named Mutexes +
//      MemoryMappedFiles ("EnderLilies.Game.SharedMemory", etc.).
//
// The verified facts:
//
//   * THE AP WORLD game string is "Ender Lilies" (verified 2026-06-14 against
//     worlds/enderlilies/__init__.py: `ENDERLILIES = "Ender Lilies"` + `game =
//     ENDERLILIES`, and ArchipelagoSession.cs: `const string __GAME = "Ender
//     Lilies"`). GameId = "ender_lilies". The apworld filename = "enderlilies.apworld".
//
//   * THE MOD repos (both verified live 2026-06-14):
//       Archipelago world:  Trexounay/EnderLilies.Archipelago  (branch: enderlilies)
//       LiveSplit component: Trexounay/EnderLilies.Randomizer
//     Latest apworld release: v0.8 (2024-12-27), asset: enderlilies.apworld.
//     Latest randomizer release: v2.4.4 (for ENDER LILIES 1.1.6),
//       assets: EnderLilies.Randomizer.zip / EnderLilies.Randomizer_splitted_dlls.zip
//     For AP play, there is a separate AP-tagged randomizer release:
//       v2.3.4.3AP "[WIP] Randomizer Archipelago v0.8.1AP for ENDER LILIES 1.1.6 (fix)"
//     The AP variant ships the same zip filename: EnderLilies.Randomizer.zip.
//
//   * HOW IT WORKS (verified from setup_en.md + ComponentSettings.cs):
//       1. Install Archipelago and LiveSplit.
//       2. Extract EnderLilies.Randomizer.zip INTO the "Components" folder of the
//          LiveSplit installation.
//       3. In LiveSplit: right-click → Edit Layout → (+) → Control →
//          "Randomizer Ender Lilies" → OK → Save Layout.
//       4. In the LiveSplit layout, double-click "Randomizer Ender Lilies" to open
//          its settings.
//       5. In the "Archipelago" sub-tab, fill in Server/Port, Password (if any),
//          Slot name.
//       6. Click Connect in LiveSplit → green status = connected.
//       7. Click "Launch Ender Lilies" in the LiveSplit component (or launch from
//          Steam independently — controller users may need Steam launch).
//
//   * CONNECTION: The Server, Port, Password and Slot name are filled in the
//     LiveSplit component's own UI settings panel (sub-tab "Archipelago"). The
//     launcher CANNOT pre-write these (they live inside LiveSplit's XML layout file
//     in a proprietary format, not a standalone config file this launcher owns).
//     Therefore this plugin does NOT attempt a connection prefill — the Settings
//     panel and guided steps state this honestly. ConnectsItself = true.
//
//   * STEAM APPID: 1322810 (ENDER LILIES: Quietus of the Knights).
//
//   * WHAT THIS PLUGIN DOES:
//       1. Detect Ender Lilies via the Steam registry and libraryfolders.vdf
//          (appmanifest_1322810.acf). A manual override is also supported.
//       2. Detect LiveSplit via its default install path
//          (%LOCALAPPDATA%\Programs\LiveSplit or the user's override).
//       3. Install/Update: download EnderLilies.Randomizer.zip (from the AP-tagged
//          release), extract its contents into <LiveSplit>\Components\. The standard
//          release (v2.4.4) is used as fallback; the AP release (v2.3.4.3AP) is the
//          primary AP-capable asset.
//       4. Launch: start LiveSplit.exe from the detected/override install. Steam is
//          NOT launched from here (the user may launch it separately for controller
//          support, or via the LiveSplit component's own "Launch Ender Lilies" button).
//       5. Standalone: launch Ender Lilies directly from its Steam install or the
//          steam:// URL.
//
//   * "Installed" check: presence of any Randomizer*.dll under
//     <LiveSplit>\Components (case-insensitive). If LiveSplit itself is not found,
//     the tile reads "not detected".
//
//   * No plaintext AP password is written by this plugin (connection is entered in
//     the LiveSplit component's own UI), so there is nothing to scrub on stop.
//
//   * All external steps (LiveSplit layout setup, component settings) are guided
//     with honest numbered steps — no fake one-click "ready to play".
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class EnderLiliesPlugin : IGamePlugin
{
    // ── Constants — AP world (verified 2026-06-14) ────────────────────────────
    private const string AP_GAME_STRING   = "Ender Lilies";

    // ── Constants — randomizer repos ─────────────────────────────────────────
    private const string RANDO_OWNER      = "Trexounay";
    private const string RANDO_REPO       = "EnderLilies.Randomizer";
    private const string AP_WORLD_OWNER   = "Trexounay";
    private const string AP_WORLD_REPO    = "EnderLilies.Archipelago";

    private static readonly string RandoRepoUrl =
        $"https://github.com/{RANDO_OWNER}/{RANDO_REPO}";
    private static readonly string ApWorldRepoUrl =
        $"https://github.com/{AP_WORLD_OWNER}/{AP_WORLD_REPO}";
    private static readonly string SetupGuideUrl =
        $"https://archipelago.gg/tutorial/Ender%20Lilies/setup/en";

    // GitHub API URLs for release resolution
    private static readonly string GH_RANDO_RELEASES_URL =
        $"https://api.github.com/repos/{RANDO_OWNER}/{RANDO_REPO}/releases";
    private static readonly string GH_APWORLD_RELEASES_URL =
        $"https://api.github.com/repos/{AP_WORLD_OWNER}/{AP_WORLD_REPO}/releases";

    // Pinned AP-capable randomizer release (v2.3.4.3AP verified live 2026-06-14).
    // This is the "[WIP] Randomizer Archipelago v0.8.1AP" build.
    private const string FallbackVersion    = "2.3.4.3AP";
    private const string FallbackApTag      = "v2.3.4.3AP";
    private const string FallbackZipName    = "EnderLilies.Randomizer.zip";
    private static readonly string FallbackZipUrl =
        $"https://github.com/{RANDO_OWNER}/{RANDO_REPO}/releases/download/{FallbackApTag}/{FallbackZipName}";

    // Steam AppID: 1322810 — ENDER LILIES: Quietus of the Knights
    private const string ElSteamAppId = "1322810";
    private static readonly string SteamRunUrl = $"steam://rungameid/{ElSteamAppId}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "ender_lilies";
    public string DisplayName => "Ender Lilies";
    public string Subtitle    => "Native PC · LiveSplit component";

    /// EXACT AP game string — verified against worlds/enderlilies/__init__.py and
    /// EnderLilies.Randomizer/Logic/ArchipelagoSession.cs (both use "Ender Lilies").
    public string ApWorldName => AP_GAME_STRING;

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "ender_lilies.png");

    public string ThemeAccentColor => "#2B1A2F";   // deep gothic purple
    public string[] GameBadges     => new[] { "Steam · LiveSplit mod" };

    public string Description =>
        "Ender Lilies: Quietus of the Knights, Binary Haze Interactive's 2021 " +
        "gothic Metroidvania, played through the EnderLilies.Randomizer — a " +
        "LiveSplit component that acts as the Archipelago client. Spirits, trinkets, " +
        "souls, and lore pieces are shuffled into the multiworld, and the LiveSplit " +
        "component connects to the Archipelago server itself (no emulator, no bridge). " +
        "You bring your own copy of Ender Lilies (Steam appid 1322810), install " +
        "LiveSplit, drop the randomizer component into LiveSplit's Components folder, " +
        "add it to your layout, and enter your server details in the component's " +
        "Archipelago sub-tab. The launcher detects your installs and guides the rest.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means an EnderLilies.Randomizer*.dll is present in the
    /// detected/override LiveSplit's Components folder.
    public bool IsInstalled => FindInstalledComponentDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "EnderLilies");

    /// Plugin sidecar: Games/ROMs/ender_lilies/ender_lilies_launcher.json
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "ender_lilies_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────
    private Process? _liveSplitProcess;
    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The EnderLilies.Randomizer component reports checks/items/goal to the AP
    // server itself — the launcher relays nothing. These events exist for
    // interface compatibility (ConnectsItself = true).
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
            // Report the stamped version from our sidecar if the dll is present.
            InstalledVersion = FindInstalledComponentDll() != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch { InstalledVersion = null; }

        try
        {
            var (ver, _) = await ResolveLatestApRandoAsync(ct);
            AvailableVersion = ver;
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. We need LiveSplit to drop the component into.
        progress.Report((2, "Locating your LiveSplit installation..."));
        string? lsDir = ResolveLiveSplitDir();
        if (lsDir == null)
            throw new InvalidOperationException(
                "Could not find a LiveSplit installation. Download LiveSplit from " +
                "https://livesplit.org/downloads/ and install it, then open this game's " +
                "Settings and pick your LiveSplit folder (the one containing LiveSplit.exe). " +
                "LiveSplit is required for the Ender Lilies Archipelago randomizer component.");

        string componentsDir = Path.Combine(lsDir, "Components");

        // 1. Resolve the latest AP-capable randomizer release.
        progress.Report((6, "Checking for the latest Ender Lilies Randomizer release..."));
        var (version, zipUrl) = await ResolveLatestApRandoAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Ender Lilies Randomizer download on GitHub. " +
                "Check your internet connection, or download the component manually from " +
                RandoRepoUrl + " (releases tagged *AP) and extract it into your LiveSplit " +
                "Components folder. See Settings for the guided steps.");

        // 2. Download + extract the randomizer zip INTO <LiveSplit>\Components\.
        await DownloadAndExtractComponentAsync(zipUrl, version, componentsDir, progress, ct);

        // 3. Stamp the version so the tile can show it.
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Ender Lilies Randomizer {version} installed in your LiveSplit Components folder. " +
            "To finish setup: open LiveSplit → Edit Layout → (+) → Control → " +
            "'Randomizer Ender Lilies' → OK → Save Layout. Then double-click the component " +
            "in your layout, go to the 'Archipelago' sub-tab, and enter your server / " +
            "slot / password. Click Connect in LiveSplit before launching the game."));
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
        // HONEST: the AP server connection for Ender Lilies is entered in the
        // LiveSplit component's own "Archipelago" sub-tab (Server/Port, Password,
        // Slot name fields). There is no command-line arg or writable config file
        // for this launcher to pre-fill (LiveSplit stores its component settings
        // in its own XML layout format). So launching from this tile just starts
        // LiveSplit; the user then connects via the component UI in LiveSplit.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the randomizer component is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartLiveSplit();
        return Task.CompletedTask;
    }

    /// Ender Lilies runs fine without the randomizer (plain game).
    public bool SupportsStandalone => true;

    /// The EnderLilies.Randomizer component owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // Standalone: launch the game directly from its Steam install.
        StartEnderLiliesDirectly();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _liveSplitProcess?.Kill(entireProcessTree: true); } catch { }
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        _liveSplitProcess = null;
        _gameProcess      = null;
        // No plaintext AP password is ever written by this plugin.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The EnderLilies.Randomizer component receives and relays items directly.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The component renders its own AP status in the LiveSplit layout.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honest preamble ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Ender Lilies uses a LiveSplit component (EnderLilies.Randomizer) as its " +
                   "Archipelago client. You need your own copy of the game (Steam appid " +
                   "1322810), LiveSplit, and the randomizer component extracted into LiveSplit's " +
                   "Components folder. Connection details are entered inside LiveSplit's own " +
                   "layout editor — this launcher cannot pre-fill them. The guided steps below " +
                   "cover the full setup. External steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: Ender Lilies install ──────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "ENDER LILIES INSTALL",
            FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? elDir = DetectEnderLiliesDir();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = elDir != null
                ? "Detected Steam install: " + elDir
                : "Ender Lilies not detected. Install it via Steam (appid 1322810).",
            FontSize = 11,
            Foreground = elDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        });

        // ── Section: LiveSplit install ─────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LIVESPLIT INSTALL",
            FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? lsDir     = ResolveLiveSplitDir();
        string? lsOverride = LoadOverrideLiveSplitDir();
        string  lsMsg = lsDir != null
            ? (lsOverride != null
                ? "Using your selected folder: " + lsDir
                : "Detected: " + lsDir)
            : "LiveSplit not detected. Download and install it, or pick the folder below.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = lsMsg, FontSize = 11,
            Foreground = lsDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // Randomizer component presence
        string? compDll = FindInstalledComponentDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = compDll != null
                ? "Randomizer component found: " + compDll
                : "Randomizer component not found in LiveSplit Components folder yet " +
                  "(use Install on the Play tab after detecting/selecting LiveSplit).",
            FontSize = 11, Foreground = compDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // LiveSplit folder picker
        var lsRow = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var lsBox = new System.Windows.Controls.TextBox
        {
            Text = lsOverride ?? lsDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your LiveSplit install folder (the one containing LiveSplit.exe).",
        };
        var lsBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding     = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        lsBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select your LiveSplit install folder (the one with LiveSplit.exe)",
                InitialDirectory = Directory.Exists(lsOverride ?? lsDir ?? "")
                    ? (lsOverride ?? lsDir!)
                    : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                if (!File.Exists(Path.Combine(picked, "LiveSplit.exe")))
                {
                    System.Windows.MessageBox.Show(
                        "That folder does not contain LiveSplit.exe. Pick the folder where " +
                        "you installed LiveSplit.",
                        "Not a LiveSplit folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                SaveOverrideLiveSplitDir(picked);
                lsBox.Text = picked;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(lsBtn, Dock.Right);
        lsRow.Children.Add(lsBtn);
        lsRow.Children.Add(lsBox);
        panel.Children.Add(lsRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LiveSplit is detected from its default install path " +
                   "(%LOCALAPPDATA%\\Programs\\LiveSplit). Use the picker above if you " +
                   "installed it elsewhere.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP",
            FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Ender Lilies: Quietus of the Knights via Steam (appid 1322810). Install it if you have not.",
            "2. Download LiveSplit from livesplit.org and install it. Note the folder where LiveSplit.exe lives.",
            "3. Download the Ender Lilies Archipelago Randomizer (an *AP-tagged release from the GitHub link " +
                "in Links below) and extract the zip INTO your LiveSplit 'Components' folder so that " +
                "'EnderLilies.Randomizer.dll' ends up directly inside that Components folder. " +
                "Alternatively, use the Install button on the Play tab to have the launcher do this step.",
            "4. Open LiveSplit. Right-click → 'Edit Layout' → click (+) → 'Control' → " +
                "'Randomizer Ender Lilies' → OK. Then right-click → 'Save Layout'.",
            "5. In the LiveSplit layout, double-click 'Randomizer Ender Lilies' to open its settings. " +
                "Ignore the non-Archipelago sub-tabs — only the 'Archipelago' sub-tab is relevant for " +
                "multiworld. Enter your Server/Port (e.g. archipelago.gg:38281), Password (if any), " +
                "and Slot Name.",
            "6. Click 'Connect' in the LiveSplit component. A green message confirms a successful " +
                "connection. This launcher cannot pre-fill these fields — they are stored in LiveSplit's " +
                "own layout XML.",
            "7. After connecting, click 'Launch Ender Lilies' in the LiveSplit component (or launch it " +
                "via Steam separately — controller users may need the Steam launch for controller input).",
            "8. Load or start a save in Ender Lilies. The component will sync your checks with the server.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("LiveSplit (download) ↗",                       "https://livesplit.org/downloads/"),
            ("EnderLilies.Randomizer (GitHub) ↗",            RandoRepoUrl),
            ("EnderLilies.Archipelago world (GitHub) ↗",     ApWorldRepoUrl),
            ("Ender Lilies on Steam ↗",                      $"https://store.steampowered.com/app/{ElSteamAppId}"),
            ("Archipelago Official ↗",                        "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding         = new Thickness(0, 2, 0, 2),
                Background      = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize        = 12,
                Margin          = new Thickness(0, 0, 0, 4),
                Foreground      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor          = System.Windows.Input.Cursors.Hand,
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
        // Report both the randomizer and the apworld releases interleaved by date.
        try
        {
            var items = new List<NewsItem>();

            // Randomizer releases
            string randoJson = await _http.GetStringAsync(GH_RANDO_RELEASES_URL, ct);
            using (var doc = JsonDocument.Parse(randoJson))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        DateTimeOffset date = DateTimeOffset.MinValue;
                        if (el.TryGetProperty("published_at", out var d) &&
                            d.ValueKind == JsonValueKind.String)
                            DateTimeOffset.TryParse(d.GetString(), out date);

                        items.Add(new NewsItem(
                            Title:   (el.TryGetProperty("name",     out var n) ? n.GetString() : null) ?? "",
                            Body:    (el.TryGetProperty("body",     out var b) ? b.GetString() : null) ?? "",
                            Version: NormalizeTag(el.TryGetProperty("tag_name", out var t) ? t.GetString() : null) ?? "",
                            Date:    date,
                            Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                        ));
                        if (items.Count >= 8) break;
                    }
                }
            }

            // apworld releases
            string apJson = await _http.GetStringAsync(GH_APWORLD_RELEASES_URL, ct);
            using (var doc2 = JsonDocument.Parse(apJson))
            {
                if (doc2.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc2.RootElement.EnumerateArray())
                    {
                        DateTimeOffset date = DateTimeOffset.MinValue;
                        if (el.TryGetProperty("published_at", out var d) &&
                            d.ValueKind == JsonValueKind.String)
                            DateTimeOffset.TryParse(d.GetString(), out date);

                        items.Add(new NewsItem(
                            Title:   (el.TryGetProperty("name",     out var n2) ? n2.GetString() : null) ?? "",
                            Body:    (el.TryGetProperty("body",     out var b2) ? b2.GetString() : null) ?? "",
                            Version: NormalizeTag(el.TryGetProperty("tag_name", out var t2) ? t2.GetString() : null) ?? "",
                            Date:    date,
                            Url:     el.TryGetProperty("html_url", out var u2) ? u2.GetString() : null
                        ));
                        if (items.Count >= 12) break;
                    }
                }
            }

            // Sort newest-first and limit to 10.
            items.Sort((a, b) => b.Date.CompareTo(a.Date));
            return items.Take(10).ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// "v2.3.4.3AP" → "2.3.4.3AP"; "v0.8" → "0.8"; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest AP-capable randomizer release (prefers releases whose
    /// tag or name contains "AP"; falls back to the latest release; then to the
    /// pinned fallback URL). Returns (version, zip download url or null).
    private async Task<(string Version, string? ZipUrl)> ResolveLatestApRandoAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RANDO_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                goto Fallback;

            // First pass: look for a release with "AP" in tag or name.
            // Second pass: any release with the expected zip asset.
            JsonElement? apRelease  = null;
            JsonElement? anyRelease = null;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string? tag  = el.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                string? name = el.TryGetProperty("name",     out var n) ? n.GetString() : null;
                bool hasApTag = (tag  != null && tag .Contains("AP", StringComparison.OrdinalIgnoreCase))
                             || (name != null && name.Contains("AP", StringComparison.OrdinalIgnoreCase));

                bool hasZip = false;
                if (el.TryGetProperty("assets", out var assets) &&
                    assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        string? aName = a.TryGetProperty("name", out var an) ? an.GetString() : null;
                        if (aName != null && aName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            hasZip = true;
                            break;
                        }
                    }
                }

                if (hasZip)
                {
                    anyRelease ??= el;
                    if (hasApTag) { apRelease = el; break; }
                }
            }

            var chosen = apRelease ?? anyRelease;
            if (chosen.HasValue)
            {
                string? ver = NormalizeTag(
                    chosen.Value.TryGetProperty("tag_name", out var tv) ? tv.GetString() : null);
                string? zipUrl = null;

                if (chosen.Value.TryGetProperty("assets", out var chosenAssets) &&
                    chosenAssets.ValueKind == JsonValueKind.Array)
                {
                    string? preferred = null;
                    string? anyZip    = null;
                    foreach (var a in chosenAssets.EnumerateArray())
                    {
                        string? aName = a.TryGetProperty("name", out var an) ? an.GetString() : null;
                        string? url   = a.TryGetProperty("browser_download_url", out var au) ? au.GetString() : null;
                        if (aName == null || url == null) continue;
                        string lower = aName.ToLowerInvariant();
                        if (!lower.EndsWith(".zip")) continue;
                        anyZip ??= url;
                        // Prefer the non-splitted zip
                        if (preferred == null && !lower.Contains("splitted"))
                            preferred = url;
                    }
                    zipUrl = preferred ?? anyZip;
                }

                if (ver != null && zipUrl != null)
                    return (ver, zipUrl);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        Fallback:
        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — Steam / Ender Lilies detection ──────────────────────

    /// Detect the Steam Ender Lilies install via registry + libraryfolders.vdf.
    private static string? DetectEnderLiliesDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{ElSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common    = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeEnderLiliesDir(candidate)) return candidate;
                    }
                    // Conventional folder names
                    foreach (string name in new[] { "ENDER LILIES", "Ender Lilies" })
                    {
                        string conv = Path.Combine(common, name);
                        if (LooksLikeEnderLiliesDir(conv)) return conv;
                    }
                }
                catch { /* try next library */ }
            }
        }
        return null;
    }

    /// A folder looks like an Ender Lilies install if it contains a relevant exe.
    private static bool LooksLikeEnderLiliesDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            // The game exe is "ENDER LILIES.exe" or "EnderLilies.exe" depending on version
            if (File.Exists(Path.Combine(dir, "ENDER LILIES.exe"))) return true;
            if (File.Exists(Path.Combine(dir, "EnderLilies.exe"))) return true;
            // Unity data folder
            if (Directory.Exists(Path.Combine(dir, "ENDER LILIES_Data"))) return true;
            if (Directory.Exists(Path.Combine(dir, "EnderLilies_Data"))) return true;
            return false;
        }
        catch { return false; }
    }

    // ── Private helpers — LiveSplit detection ─────────────────────────────────

    /// Resolve the LiveSplit directory to use: explicit override > auto-detect.
    private string? ResolveLiveSplitDir()
    {
        string? ov = LoadOverrideLiveSplitDir();
        if (ov != null && File.Exists(Path.Combine(ov, "LiveSplit.exe")))
            return ov;
        return DetectLiveSplitDir();
    }

    /// Detect LiveSplit from common install locations.
    private static string? DetectLiveSplitDir()
    {
        // Common default: %LOCALAPPDATA%\Programs\LiveSplit
        try
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string candidate = Path.Combine(local, "Programs", "LiveSplit");
            if (File.Exists(Path.Combine(candidate, "LiveSplit.exe"))) return candidate;
        }
        catch { }

        // Also check %ProgramFiles%\LiveSplit and %ProgramFiles(x86)%\LiveSplit
        foreach (var special in new[]
        {
            Environment.SpecialFolder.ProgramFiles,
            Environment.SpecialFolder.ProgramFilesX86,
        })
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(special), "LiveSplit");
                if (File.Exists(Path.Combine(dir, "LiveSplit.exe"))) return dir;
            }
            catch { }
        }

        return null;
    }

    // ── Private helpers — installed component detection ────────────────────────

    /// Find any Randomizer DLL (case-insensitive, named EnderLilies*Randomizer*.dll
    /// or EnderLilies.Randomizer.dll) inside the detected/override LiveSplit's
    /// Components folder. Returns the dll path or null.
    private string? FindInstalledComponentDll()
    {
        try
        {
            string? lsDir = ResolveLiveSplitDir();
            if (lsDir == null) return null;
            string componentsDir = Path.Combine(lsDir, "Components");
            if (!Directory.Exists(componentsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(
                         componentsDir, "*.dll", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileName(dll);
                if (name.Contains("Randomizer", StringComparison.OrdinalIgnoreCase) &&
                    name.Contains("EnderLilies", StringComparison.OrdinalIgnoreCase))
                    return dll;
                // Also match "LiveSplit.EnderLilies.dll" style
                if (name.Contains("EnderLilies", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { }
        return null;
    }

    // ── Private helpers — Steam registry + VDF ────────────────────────────────

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

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start LiveSplit from the detected/override install.
    private void StartLiveSplit()
    {
        string? lsDir = ResolveLiveSplitDir();
        if (lsDir == null)
            throw new FileNotFoundException(
                "Could not find LiveSplit.exe. Open this game's Settings and pick your " +
                "LiveSplit install folder, or download LiveSplit from livesplit.org.",
                "LiveSplit.exe");

        string exe = Path.Combine(lsDir, "LiveSplit.exe");
        if (!File.Exists(exe))
            throw new FileNotFoundException(
                "LiveSplit.exe was not found in: " + lsDir +
                ". Open this game's Settings and correct the LiveSplit folder.",
                exe);

        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exe,
            WorkingDirectory = lsDir,
            UseShellExecute  = true,
        }) ?? throw new InvalidOperationException("Failed to start LiveSplit.");

        _liveSplitProcess = proc;
        IsRunning         = true;
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
    }

    /// Launch Ender Lilies directly (standalone, without the randomizer / LiveSplit).
    private void StartEnderLiliesDirectly()
    {
        // Try to find the game exe in the Steam install.
        string? elDir = DetectEnderLiliesDir();
        if (elDir != null)
        {
            foreach (string exeName in new[] { "ENDER LILIES.exe", "EnderLilies.exe" })
            {
                string exePath = Path.Combine(elDir, exeName);
                if (!File.Exists(exePath)) continue;
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName         = exePath,
                    WorkingDirectory = elDir,
                    UseShellExecute  = true,
                });
                if (proc != null)
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
                    catch { }
                    return;
                }
            }
        }

        // Fall back to steam:// URL.
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
            "Could not find ENDER LILIES.exe. Install Ender Lilies via Steam " +
            "(appid 1322810), or ensure Steam is installed so the game can be " +
            "launched via steam://rungameid/1322810.",
            "ENDER LILIES.exe");
    }

    // ── Private helpers — download / extract the randomizer component ─────────

    private async Task DownloadAndExtractComponentAsync(
        string zipUrl,
        string version,
        string componentsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"enderlilies-rando-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((10, $"Downloading Ender Lilies Randomizer {version}..."));
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
                        progress.Report((pct, $"Downloading... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Extracting component into LiveSplit Components folder..."));
            Directory.CreateDirectory(componentsDir);

            // The randomizer zip ships DLLs flat or in a single sub-folder.
            // Extract flat to Components\ so LiveSplit can discover the component DLL.
            using var archive = ZipFile.OpenRead(tempZip);
            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name)) continue; // directory entry
                string dest = Path.Combine(componentsDir, entry.Name);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                // Manual extract avoids the ZipFileExtensions extension-method dependency.
                await using var entryStream = entry.Open();
                await using var fileStream  = new FileStream(
                    dest, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 65536, useAsync: true);
                await entryStream.CopyToAsync(fileStream, ct);
            }

            progress.Report((90, "Component files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class ElSettings
    {
        public string? LiveSplitOverride { get; set; }
        public string? ComponentVersion  { get; set; }
    }

    private ElSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<ElSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(ElSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { }
    }

    private string? LoadOverrideLiveSplitDir()
    {
        string? p = LoadSettings().LiveSplitOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }
    private void SaveOverrideLiveSplitDir(string p)
    {
        var s = LoadSettings();
        s.LiveSplitOverride = p;
        SaveSettings(s);
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ComponentVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings();
        s.ComponentVersion = v;
        SaveSettings(s);
    }
}
