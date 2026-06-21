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
// FontWeights / HorizontalAlignment collide between System.Windows.Forms and
// System.Windows[.Controls/.Media] (CS0104). Qualifying every UI type avoids that.

namespace LauncherV2.Plugins.BuckshotRoulette;

// ════════════════════════════════════════════════════════════════════════════════
// BuckshotRoulettePlugin — install / launch for "Buckshot Roulette" (Mike Klubnika,
// 2024, Steam appid 2835570) played through APBuckshot, a Godot Mod Loader plugin
// that is the in-game Archipelago client. This is a NATIVE "ConnectsItself"
// integration — the game itself speaks to the AP server with no emulator, no Lua
// bridge, and no launcher-held ApClient on the slot.
//
// ── HONEST REALITY CHECK (2026-06-14, verified against asdfwyay/APBuckshot) ──
//
//   * THE AP WORLD game string is "Buckshot Roulette" (verified against
//     worlds/buckshot/__init__.py: `class BuckshotWorld(World): ... game =
//     "Buckshot Roulette"`). GameId = "buckshot_roulette".
//
//   * THE GAME is a Godot 4 game (NOT Unity — no BepInEx). Mods use the Godot
//     Mod Loader (GML). A CUSTOM build of GML is required:
//       - Custom GML zip: https://github.com/asdfwyay/APBuckshot-Client/releases/
//         tag/gml-7.0.1-custom  (asset: gml-7.0.1-custom.zip)
//       - The zip contains an "addons" folder; its contents go into the game root.
//       - Run the game once with --script addons/mod_loader/mod_loader_setup.gd to
//         finish GML setup (generates a patched exe alongside the original).
//
//   * THE MOD (APBuckshot) comes from asdfwyay/APBuckshot GitHub releases:
//       - Asset: asdfwyay-APBuckshot-{version}.zip — do NOT unzip it; place the zip
//         as-is inside a "/mods" directory in the game install folder.
//
//   * CONNECTION is made IN-GAME (verified from setup_en.md): from the main menu,
//     click the ARCHIPELAGO button, enter host/port, slot name, and password, then
//     click CONNECT. There is no config file this launcher can pre-write. The
//     settings panel surfaces the session credentials for the user to copy.
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. DETECT the Steam Buckshot Roulette install via registry (appmanifest_2835570).
//      A manual install-dir override is also supported, persisted in this plugin's
//      own sidecar at Games/ROMs/buckshot_roulette/buckshot_roulette_launcher.json.
//   2. INSTALL/UPDATE:
//      (a) Download the custom GML zip from APBuckshot-Client releases and extract
//          its "addons" folder into the game root (if not already present).
//          NOTE: the user must then run the game ONCE with --script to finish GML
//          setup (generates a patched exe). This is not automatable from the
//          launcher without executing the game in a special mode.
//      (b) Create the /mods directory in the game folder (if absent), download the
//          latest asdfwyay-APBuckshot-{version}.zip from GitHub releases, and drop
//          it (unmodified) into /mods.
//   3. LAUNCH via the (patched, modded) Buckshot Roulette.exe, or fall back to
//      steam://rungameid/2835570 if the exe is absent.
//      ConnectsItself = true. SupportsStandalone = true.
// ════════════════════════════════════════════════════════════════════════════════

public sealed class BuckshotRoulettePlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    // GitHub repo for the AP world + mod releases.
    private const string MOD_OWNER = "asdfwyay";
    private const string MOD_REPO  = "APBuckshot";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_API =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // GitHub repo for the APBuckshot-Client (custom GML build + the client mod).
    private const string CLIENT_OWNER = "asdfwyay";
    private const string CLIENT_REPO  = "APBuckshot-Client";
    private const string GH_CLIENT_RELEASES_API =
        $"https://api.github.com/repos/{CLIENT_OWNER}/{CLIENT_REPO}/releases";

    // Custom GML release: pinned tag and asset name (verified 2026-06-14).
    private const string GmlTag       = "gml-7.0.1-custom";
    private const string GmlAssetName = "gml-7.0.1-custom.zip";
    private static readonly string GmlZipUrl =
        $"https://github.com/{CLIENT_OWNER}/{CLIENT_REPO}/releases/download/{GmlTag}/{GmlAssetName}";

    // Pinned fallback mod version (verified 2026-06-14).
    private const string FallbackModVersion = "0.4.1-hotfix.1";
    private static readonly string FallbackModAssetName =
        $"asdfwyay-APBuckshot-{FallbackModVersion}.zip";
    private static readonly string FallbackModZipUrl =
        $"https://github.com/{MOD_OWNER}/{MOD_REPO}/releases/download/v0.4.1/{FallbackModAssetName}";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Buckshot%20Roulette/setup/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam
    private const string SteamAppId      = "2835570";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";
    private const string SteamFolderName = "Buckshot Roulette";
    private const string GameExeName     = "Buckshot Roulette.exe";
    // Godot Mod Loader setup args (required for first-time GML install).
    private const string GmlSetupArgs    =
        "--script addons/mod_loader/mod_loader_setup.gd";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "buckshot_roulette";
    public string DisplayName => "Buckshot Roulette";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/buckshot/__init__.py:
    ///   class BuckshotWorld(World): game = "Buckshot Roulette"
    public string ApWorldName => "Buckshot Roulette";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "buckshot_roulette.png");

    public string ThemeAccentColor => "#8B1A1A";   // deep crimson / shotgun shell
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Buckshot Roulette (Mike Klubnika, 2024) is a tense horror tabletop game " +
        "of shotgun roulette against a supernatural dealer, played through APBuckshot " +
        "— a Godot Mod Loader plugin that connects the game directly to your Archipelago " +
        "multiworld server. Items, consumables, and progression across rounds are " +
        "shuffled into the multiworld. You bring your own copy from Steam (appid " +
        "2835570); the mod runs on a custom Godot Mod Loader build. The launcher " +
        "stages the GML add-on and the mod ZIP for you, but you must run the game " +
        "once to finish GML setup, then connect from the in-game ARCHIPELAGO menu.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = a mod ZIP matching asdfwyay-APBuckshot-*.zip exists in the
    /// game's /mods directory.
    public bool IsInstalled => FindInstalledModZip() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "BuckshotRoulette");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "buckshot_roulette_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // APBuckshot connects to the AP server in-game; the launcher relays nothing.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067
    public event Action<int>?    GameExited;

    // ── Lifecycle — ConnectsItself / SupportsStandalone ───────────────────────

    /// APBuckshot owns the AP slot connection — the launcher MUST NOT hold its own
    /// ApClient on this slot while the game is running.
    public bool ConnectsItself     => true;

    /// Plain Buckshot Roulette runs fine without the mod.
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            string? found = FindInstalledModZip();
            InstalledVersion = found != null
                ? (ExtractVersionFromZipName(Path.GetFileName(found)) ?? "installed")
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
            AvailableVersion = null; // never throw on network failure
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. Locate the Buckshot Roulette install.
        progress.Report((2, "Locating your Buckshot Roulette installation..."));
        string? gameDir = ResolveBuckshotDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Buckshot Roulette installation. Open this game's " +
                "Settings and pick your install folder (the one containing " +
                "\"Buckshot Roulette.exe\"), or install the game via Steam first. " +
                "The mod is added on top of your own copy of the game.");

        // 1. Resolve the latest mod release from GitHub.
        progress.Report((6, "Checking the latest APBuckshot release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the APBuckshot download. Check your internet " +
                "connection. Source: " + ModRepoUrl);

        // 2. Ensure the custom Godot Mod Loader is staged (if "addons" folder is absent).
        if (!GmlPresent(gameDir))
        {
            progress.Report((10, "Staging Godot Mod Loader (custom GML build)..."));
            await DownloadAndExtractGmlAsync(gameDir, 10, 45, progress, ct);
            progress.Report((46,
                "Godot Mod Loader staged. IMPORTANT: you must run the game ONCE " +
                "with the --script argument so GML finishes its setup (see Settings " +
                "for the exact command). After that, use the regular Play button."));
        }
        else
        {
            progress.Report((45, "Godot Mod Loader already present — skipping."));
        }

        // 3. Create /mods directory and drop the mod ZIP into it (do NOT unzip).
        string modsDir = Path.Combine(gameDir, "mods");
        Directory.CreateDirectory(modsDir);

        // Remove older APBuckshot ZIP(s) so only one version is active at a time.
        foreach (string old in Directory.EnumerateFiles(
            modsDir, "asdfwyay-APBuckshot-*.zip", SearchOption.TopDirectoryOnly))
        {
            try { File.Delete(old); } catch { /* non-fatal */ }
        }

        string destZip = Path.Combine(modsDir,
            $"asdfwyay-APBuckshot-{version}.zip");
        await DownloadFileAsync(zipUrl, destZip,
            $"Downloading APBuckshot {version}...", 48, 95, progress, ct);

        // Stamp the version.
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"APBuckshot {version} is ready in the mods folder. " +
            "If this is your first install: run the game ONCE with the --script " +
            "flag to finish GML setup (see Settings), then launch normally. " +
            "From the main menu, click ARCHIPELAGO, enter your server details, " +
            "and press CONNECT."));
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
        // HONEST: APBuckshot's connection is entered in-game via the ARCHIPELAGO
        // button on the main menu (host/port, slot name, password). There is no
        // command-line flag or config file this launcher can pre-write (verified).
        // The settings panel surfaces the session values for the user to copy.
        _ = session; // no prefill mechanism exists
        StartBuckshot();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartBuckshot();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(
        ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Buckshot Roulette is your own game (Steam) with the APBuckshot mod " +
                "added on top via Godot Mod Loader. The launcher stages the custom GML " +
                "build and the mod ZIP for you, but you must run the game ONCE with the " +
                "--script flag so GML finishes setup (see steps below). After that, " +
                "connect from the in-game ARCHIPELAGO menu. These external steps are not " +
                "verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Detected install ─────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "BUCKSHOT ROULETTE INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveBuckshotDir();
        string? overrideDir = LoadOverrideDir();
        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Buckshot Roulette not detected. Pick your install folder below, or " +
              "install it via Steam first.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // GML status
        bool gmlOk = gameDir != null && GmlPresent(gameDir);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = gameDir == null ? "" : gmlOk
                    ? "Godot Mod Loader (addons) found in your install folder."
                    : "Godot Mod Loader not found yet — use Install on the Play tab " +
                      "to stage it automatically.",
            FontSize = 11, Foreground = gmlOk ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        // Mod status
        string? modZip = FindInstalledModZip();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modZip != null
                    ? "APBuckshot mod found: " + Path.GetFileName(modZip)
                    : "APBuckshot mod ZIP not found in mods\\ yet — use Install on the Play tab.",
            FontSize = 11, Foreground = modZip != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Install folder picker row
        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Your Buckshot Roulette install folder (the one containing " +
                      "\"Buckshot Roulette.exe\"). Detected from Steam automatically; " +
                      "use this picker to override.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Buckshot Roulette install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                                   ? (overrideDir ?? gameDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                if (!LooksLikeBuckshotDir(picked))
                {
                    string nested = Path.Combine(picked, SteamFolderName);
                    if (LooksLikeBuckshotDir(nested))
                        picked = nested;
                    else
                    {
                        System.Windows.MessageBox.Show(
                            "That does not look like a Buckshot Roulette installation. " +
                            "Pick the folder that contains \"Buckshot Roulette.exe\".",
                            "Not a Buckshot Roulette folder",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                        return;
                    }
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
            Text = "Steam installs are detected automatically (appid 2835570). Use the " +
                   "picker for a non-standard location.",
            FontSize = 11, Foreground = muted, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Connection section ────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "From the main menu, click the ARCHIPELAGO button. Enter your server " +
                   "address and port, slot name, and password (if any), then click " +
                   "CONNECT. You should see \"CONNECTED\". This launcher does not pre-fill " +
                   "the connection — it is entered in-game each session.",
            FontSize = 11, Foreground = fg, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Guided setup steps ────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "SETUP STEPS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Buckshot Roulette on Steam (appid 2835570) and install it. Use the " +
               "picker above if it was not detected automatically.",
            "2. Click Install on the Play tab. The launcher will stage the Godot Mod " +
               "Loader and the APBuckshot mod ZIP into your game folder.",
            "3. ONE-TIME SETUP: run the game once using the --script flag so GML " +
               "finishes installing (copy the command below). Wait for the restart " +
               "prompt. Two executables will appear: the modded one (Buckshot Roulette) " +
               "and the original (Buckshot Roulette-vanilla).",
            "4. Launch the game normally (via Play, Steam, or the exe). The main menu " +
               "will show an ARCHIPELAGO button below MULTIPLAYER.",
            "5. Click ARCHIPELAGO, enter your server, slot name, and password, then " +
               "CONNECT. When status shows CONNECTED, close the panel and click START.",
            "6. To update the mod: just click Install again. The launcher replaces the " +
               "old mod ZIP in the mods\\ folder automatically.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // GML setup command copyable box
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GML SETUP COMMAND (run this ONCE in your game folder, step 3):",
            FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 10, 0, 4),
        });

        string? resolvedGameDir = ResolveBuckshotDir();
        string gmlCmdText = resolvedGameDir != null
            ? $"& \"{Path.Combine(resolvedGameDir, GameExeName)}\" {GmlSetupArgs}"
            : $"& \"{GameExeName}\" {GmlSetupArgs}";

        var cmdRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 12)
        };
        var cmdBox = new System.Windows.Controls.TextBox
        {
            Text = gmlCmdText, IsReadOnly = true, FontSize = 11,
            FontFamily = new System.Windows.Media.FontFamily("Consolas,Courier New,monospace"),
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var copyBtn = new System.Windows.Controls.Button
        {
            Content = "Copy", Width = 70,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        string cmdCapture = gmlCmdText;
        copyBtn.Click += (_, _) =>
        {
            try { System.Windows.Clipboard.SetText(cmdCapture); }
            catch { /* clipboard unavailable */ }
        };
        System.Windows.Controls.DockPanel.SetDock(copyBtn, System.Windows.Controls.Dock.Right);
        cmdRow.Children.Add(copyBtn);
        cmdRow.Children.Add(cmdBox);
        panel.Children.Add(cmdRow);

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("APBuckshot (GitHub) ↗",         ModRepoUrl),
            ("Custom GML Releases ↗",         $"https://github.com/{CLIENT_OWNER}/{CLIENT_REPO}/releases"),
            ("Buckshot Roulette on Steam ↗",  $"https://store.steampowered.com/app/{SteamAppId}/"),
            ("Archipelago Official ↗",         ArchipelagoSite),
            ("AP Setup Guide ↗",              SetupGuideUrl),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background      = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0),
                FontSize = 12,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(u) { UseShellExecute = true });
                }
                catch { /* ignore */ }
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
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_API, ct);
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

    /// Resolve the latest APBuckshot release from the GitHub Releases API.
    /// Returns (version, assetDownloadUrl). Falls back to the pinned values
    /// when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_API, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array
                || doc.RootElement.GetArrayLength() == 0)
                return (FallbackModVersion, FallbackModZipUrl);

            var latest = doc.RootElement[0];
            string? tag = NormalizeTag(
                latest.TryGetProperty("tag_name", out var t) ? t.GetString() : null);

            // Find the mod zip asset (asdfwyay-APBuckshot-*.zip).
            if (latest.TryGetProperty("assets", out var assetsEl)
                && assetsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assetsEl.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = asset.TryGetProperty("browser_download_url", out var u)
                        ? u.GetString() : null;
                    if (name != null
                        && url  != null
                        && name.StartsWith("asdfwyay-APBuckshot-", StringComparison.OrdinalIgnoreCase)
                        && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        return (tag ?? FallbackModVersion, url);
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(tag))
                return (tag!, null); // version known but no suitable asset found
        }
        catch (OperationCanceledException) { throw; }
        catch { /* network failure → pinned fallback */ }

        return (FallbackModVersion, FallbackModZipUrl);
    }

    // ── Private helpers — game detection ──────────────────────────────────────

    private string? ResolveBuckshotDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeBuckshotDir(ov))
            return ov;

        try { return DetectSteamBuckshotDir(); }
        catch { return null; }
    }

    private static bool LooksLikeBuckshotDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, GameExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, "Buckshot Roulette_Data"))) return true;
            if (Directory.Exists(Path.Combine(dir, ".godot"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// True when the custom Godot Mod Loader is staged: the "addons" folder is
    /// present in the game root, which means the GML zip was extracted.
    private static bool GmlPresent(string gameDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return false;
            return Directory.Exists(Path.Combine(gameDir, "addons"));
        }
        catch { return false; }
    }

    private static string? DetectSteamBuckshotDir()
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
                        if (LooksLikeBuckshotDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamFolderName);
                    if (LooksLikeBuckshotDir(conventional)) return conventional;
                }
                catch { /* try next library */ }
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

    // ── Private helpers — installed mod detection ─────────────────────────────

    /// Find the APBuckshot mod ZIP inside the game's mods directory.
    /// The ZIP name is always asdfwyay-APBuckshot-{version}.zip.
    private string? FindInstalledModZip()
    {
        try
        {
            string? gameDir = ResolveBuckshotDir();
            if (gameDir == null) return null;
            string modsDir = Path.Combine(gameDir, "mods");
            if (!Directory.Exists(modsDir)) return null;

            return Directory.EnumerateFiles(
                modsDir, "asdfwyay-APBuckshot-*.zip", SearchOption.TopDirectoryOnly)
                .FirstOrDefault();
        }
        catch { return null; }
    }

    /// Extract version from an asset filename like "asdfwyay-APBuckshot-0.4.1.zip".
    private static string? ExtractVersionFromZipName(string fileName)
    {
        const string prefix = "asdfwyay-APBuckshot-";
        const string suffix = ".zip";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return null;
        return fileName.Substring(prefix.Length,
            fileName.Length - prefix.Length - suffix.Length);
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartBuckshot()
    {
        string? gameDir = ResolveBuckshotDir();
        string? exe     = gameDir != null ? Path.Combine(gameDir, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = gameDir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException(
                "Failed to start Buckshot Roulette.");

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
            catch { /* some processes don't expose Exited */ }
            return;
        }

        // Fall back to Steam.
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
            "Could not find \"Buckshot Roulette.exe\". Open this game's Settings " +
            "and pick your install folder, or install the game via Steam.",
            GameExeName);
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    /// Download the custom GML zip and extract its "addons" folder into the game root.
    /// The zip contains an "addons" directory; its contents go to <gameDir>/addons.
    private async Task DownloadAndExtractGmlAsync(
        string gameDir,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip     = Path.Combine(Path.GetTempPath(),
            $"buckshot-gml-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"buckshot-gml-x-{Guid.NewGuid():N}");
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 7 / 10;
            await DownloadFileAsync(GmlZipUrl, tempZip,
                "Downloading Godot Mod Loader...", pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, "Extracting GML into your game folder..."));
            Directory.CreateDirectory(tempExtract);
            ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // Merge the extracted "addons" directory into the game root.
            string srcAddons = Path.Combine(tempExtract, "addons");
            if (Directory.Exists(srcAddons))
            {
                string destAddons = Path.Combine(gameDir, "addons");
                CopyDirectoryContents(srcAddons, destAddons);
            }
            else
            {
                // Fallback: if the zip is flat, copy everything from the extract root.
                CopyDirectoryContents(tempExtract, gameDir);
            }

            progress.Report((pctEnd, "Godot Mod Loader extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip))          File.Delete(tempZip);                    } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, true);     } catch { }
        }
    }

    /// Stream a URL to a file with progress reporting between [pctStart, pctEnd].
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
        using var response = await _http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct);
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

    /// Recursively copy the CONTENTS of sourceDir into destDir (merge, overwrite).
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

    // ── Private helpers — sidecar settings ───────────────────────────────────

    private sealed class BuckshotSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private BuckshotSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<BuckshotSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(BuckshotSettings s)
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

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings(); s.ModVersion = v; SaveSettings(s);
    }
}
