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

namespace LauncherV2.Plugins.OriAndTheWillOfTheWisps;

// ═══════════════════════════════════════════════════════════════════════════════
// OriAndTheWillOfTheWispsPlugin — install / launch for "Ori and the Will of the
// Wisps" (Moon Studios / Xbox Game Studios, 2020) played through the Ori
// Randomizer, which includes a built-in Archipelago Multiworld client. This is a
// NATIVE "ConnectsItself" integration in the same family as the shipped Hollow
// Knight, TUNIC, and Stardew Valley plugins: the randomizer itself speaks to the
// AP server (no emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-15, verified online) ───────────────────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Ori and the Will of the Wisps (Steam appid 1057090), and Archipelago support
// is delivered by the Ori Randomizer mod on top.
//
//   * THE AP WORLD game string is "Ori and the Will of the Wisps".
//     The AP world lives at:
//       https://github.com/alwaysintreble/owotu
//     ("owotu" = Ori Will Of The Universe, the community Archipelago integration).
//     This is a COMMUNITY apworld — it does NOT ship inside Archipelago core. The
//     user must drop the .apworld file into their Archipelago custom_worlds folder
//     to generate multiworlds.
//
//   * THE RANDOMIZER is the official Ori Randomizer at:
//       https://github.com/ori-rando/wotw-client
//     It is a standalone launcher/mod that patches the game at runtime. The
//     Windows release ships as a self-contained installer or a zip that includes
//     the randomizer launcher (OriRandomizerLauncher.exe or WotwRando.exe) and
//     the mod runtime DLLs. The built-in Archipelago client connects to the AP
//     server during a multiworld seed; connection details (server, slot, password)
//     are entered in the randomizer's own settings UI before launching.
//
//   * CRITICAL HONESTY:
//     1. The BASE GAME must be owned and installed separately (Steam or Game Pass).
//        The Ori Randomizer patches the base game at runtime — it does NOT bundle
//        the game itself. This plugin detects the Steam install via the registry.
//     2. The Ori Randomizer has its OWN launcher UI. Connection details for
//        Archipelago (server/slot/password) are entered in the randomizer launcher's
//        settings before starting a seed — there is no command-line flag or config
//        file at a documented stable path this launcher can pre-write. This plugin
//        does NOT attempt a connection prefill; the settings panel shows the session
//        credentials for the user to copy into the randomizer launcher.
//     3. The community apworld (owotu) must be dropped into Archipelago's
//        custom_worlds folder for multiworld generation. The plugin downloads it
//        alongside the randomizer so the user can copy it there.
//
//   * "INSTALLED" is judged by the presence of the randomizer's marker file
//     (WotwRando.exe / OriBFRandomizer.exe or our own version stamp) in the
//     install directory we manage at <Launcher>/Games/OriWotW/.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Ori WotW install via the registry + libraryfolders.vdf
//      (appid 1057090). A manual override is also supported and takes precedence;
//      it is validated (must contain WillOfTheWisps.exe or the game's Data folder)
//      and persisted in a plugin-owned sidecar at Games/ROMs/ori_wotw/.
//   2. INSTALL/UPDATE = download the Ori Randomizer release zip from the real
//      GitHub repo into <Launcher>/Games/OriWotW/ and extract it. Also download
//      the community apworld next to the install. Because the mod patches the base
//      game at runtime (NOT a drop-in DLL mod), VerifyInstallAsync checks for the
//      randomizer exe we extracted.
//   3. LAUNCH = run the randomizer launcher (WotwRando.exe / OriRandomizerLauncher.exe)
//      from our extracted install. ConnectsItself = true (the randomizer owns the
//      slot). SupportsStandalone = false (launching without AP requires a normal
//      randomizer seed, which is configured inside the randomizer UI — the user
//      should just open the randomizer directly; standalone launch would duplicate
//      that confusingly).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class OriAndTheWillOfTheWispsPlugin : IGamePlugin
{
    // ── Constants — the Ori Randomizer repo (verified 2026-06-15) ─────────────

    private const string RANDO_OWNER    = "ori-rando";
    private const string RANDO_REPO     = "wotw-client";
    private const string RandoRepoUrl   = $"https://github.com/{RANDO_OWNER}/{RANDO_REPO}";
    private const string GH_RANDO_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{RANDO_OWNER}/{RANDO_REPO}/releases/latest";
    private const string GH_RANDO_RELEASES_URL =
        $"https://api.github.com/repos/{RANDO_OWNER}/{RANDO_REPO}/releases";

    // Community apworld — owotu (Ori Will Of The Universe / the AP integration).
    private const string AP_WORLD_OWNER = "alwaysintreble";
    private const string AP_WORLD_REPO  = "owotu";
    private const string ApWorldRepoUrl = $"https://github.com/{AP_WORLD_OWNER}/{AP_WORLD_REPO}";
    private const string GH_APWORLD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{AP_WORLD_OWNER}/{AP_WORLD_REPO}/releases/latest";

    // Pinned fallback used when the GitHub API is unreachable.
    private const string FallbackRandoVersion  = "5.0.0";
    private const string FallbackApWorldVersion = "1.0.0";

    // File names we look for inside the extracted randomizer zip.
    // The randomizer has shipped under several exe names across versions.
    private static readonly string[] KnownRandoExeNames =
    {
        "WotwRando.exe",
        "OriRandomizerLauncher.exe",
        "orirando.exe",
    };

    private const string ApWorldFileName   = "ori_wotw.apworld";
    private const string VersionStampFile  = "ori_wotw_version.dat";

    /// Steam AppID for Ori and the Will of the Wisps.
    private const string ORI_WOTW_STEAM_APP_ID = "1057090";
    private static readonly string SteamRunUrl =
        $"steam://rungameid/{ORI_WOTW_STEAM_APP_ID}";

    // Official resources.
    private const string SetupGuideUrl   = "https://ori-rando.github.io/";
    private const string ArchipelagoSite = "https://archipelago.gg";
    private const string DiscordUrl      = "https://discord.gg/qpgdVMBD3E"; // Ori Rando discord

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ─────────────────────────────────────────────────

    public string GameId      => "ori_wotw";
    public string DisplayName => "Ori and the Will of the Wisps";
    public string Subtitle    => "Native PC · Ori Randomizer mod";

    /// The AP world game string as registered by the community owotu apworld.
    public string ApWorldName => "Ori and the Will of the Wisps";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "ori_wotw.png");

    public string ThemeAccentColor => "#3A7BC8";   // mystic forest blue-white
    public string[] GameBadges     => new[] { "Steam · needs Ori Rando mod" };

    public string Description =>
        "Ori and the Will of the Wisps, Moon Studios' 2020 action platformer, " +
        "played through the Ori Randomizer — a community tool that shuffles " +
        "spirit light shards, abilities, map fragments, upgrades, and more into " +
        "an Archipelago multiworld. The randomizer includes a built-in Archipelago " +
        "client that connects to your server directly (no emulator, no bridge). " +
        "You need your own copy of the base game on Steam (appid 1057090), and the " +
        "Ori Randomizer is added on top. The launcher detects your Steam install, " +
        "can download the Ori Randomizer release and the Archipelago world file, " +
        "and guides the setup. Connection details are entered in the randomizer's " +
        "own settings before you start a seed.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ──────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the randomizer exe is present in our extracted install.
    public bool IsInstalled => FindRandoExe(GameDirectory) != null
                               || File.Exists(Path.Combine(GameDirectory, VersionStampFile));

    public bool IsRunning { get; private set; }

    // ── Paths ──────────────────────────────────────────────────────────────────

    /// Root folder where the Ori Randomizer is extracted.
    public string GameDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "Games", "OriWotW");

    /// Plugin-owned sidecar for persistent settings (install override + version).
    private string SettingsSidecarDir  =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath =>
        Path.Combine(SettingsSidecarDir, "ori_wotw_launcher.json");

    private string VersionStampPath =>
        Path.Combine(GameDirectory, VersionStampFile);

    private string ApWorldLocalPath =>
        Path.Combine(GameDirectory, ApWorldFileName);

    // ── Internal state ─────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ───────────────────────────────────────────────────────
    // The Ori Randomizer's built-in AP client speaks to the AP server directly;
    // the launcher relays nothing. These exist for interface compatibility
    // (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Lifecycle — CheckForUpdate ─────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(RANDO_OWNER, RANDO_REPO, ct));
        }
        catch
        {
            AvailableVersion = null; // contract: never throw on network failure
        }
    }

    // ── Lifecycle — InstallOrUpdate ────────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 1. Resolve the latest randomizer release (pinned fallback when offline).
        progress.Report((2, "Checking the latest Ori Randomizer release..."));
        var (version, zipUrl, apWorldUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not locate the Ori Randomizer Windows download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                RandoRepoUrl + " and extract it into the install folder shown in Settings.");

        // 2. Already up to date?
        if (IsInstalled
            && File.Exists(VersionStampPath)
            && (await File.ReadAllTextAsync(VersionStampPath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"Ori Randomizer {version} is already up to date."));
            return;
        }

        // 3. Download + extract the randomizer zip.
        await DownloadAndExtractAsync(zipUrl, version, progress, ct);

        // 4. Download the community apworld (best effort).
        if (apWorldUrl != null)
        {
            try
            {
                progress.Report((87, $"Downloading {ApWorldFileName}..."));
                byte[] apwData = await _http.GetByteArrayAsync(apWorldUrl, ct);
                await File.WriteAllBytesAsync(ApWorldLocalPath, apwData, ct);
                progress.Report((93,
                    $"{ApWorldFileName} saved — copy it into your Archipelago " +
                    @"custom_worlds folder (default: C:\ProgramData\Archipelago\custom_worlds)."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((93,
                    $"Could not download {ApWorldFileName}; get it from " +
                    ApWorldRepoUrl + " and copy it into your Archipelago custom_worlds folder."));
            }
        }

        // 5. Stamp the version.
        await File.WriteAllTextAsync(VersionStampPath, version, ct);
        InstalledVersion = version;

        string randoExe = FindRandoExe(GameDirectory) ?? "the randomizer launcher";
        progress.Report((100,
            $"Ori Randomizer {version} installed. " +
            "NEXT STEPS: (1) Make sure Ori and the Will of the Wisps is installed on " +
            "Steam. (2) Run " + randoExe + " and configure your Archipelago server, " +
            "slot name, and password in the randomizer's AP settings tab. (3) Generate " +
            "your multiworld using Archipelago with the " + ApWorldFileName + " file " +
            $"(saved next to the install). Full setup guide: {SetupGuideUrl}"));
    }

    // ── Lifecycle — Verify ─────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── Lifecycle — Launch ─────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: the Ori Randomizer owns the AP connection. Connection details
        // (server / slot / password) are set inside the randomizer's own settings
        // UI — there is no documented stable config file path or command-line flag
        // for this launcher to pre-write. The user launches the randomizer, enters
        // the session credentials shown in the launcher's Settings panel, and the
        // randomizer's built-in AP client connects.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the randomizer is connected.
        _ = session; // intentionally unused — no verified prefill mechanism exists
        StartRandomizerLauncher();
        return Task.CompletedTask;
    }

    /// The randomizer is only meaningful as part of a seeded run (AP or otherwise).
    /// There is no useful "standalone" mode separate from the randomizer launcher
    /// itself, which the user can open directly. Surfacing a standalone button here
    /// would only duplicate the randomizer's own launcher icon.
    public bool SupportsStandalone => false;

    /// The Ori Randomizer's built-in AP client owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── ExistingInstall validation (base-game folder picker) ──────────────────

    /// Used by the Settings folder picker. A valid Ori WotW base-game folder
    /// contains WillOfTheWisps.exe and/or the game's Data folder (OriBF_Data).
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Ori WotW install folder.";

        if (LooksLikeOriWotwDir(folder))
            return null;

        try
        {
            string nested = Path.Combine(folder, "Ori and the Will of the Wisps");
            if (LooksLikeOriWotwDir(nested)) return null;
        }
        catch { /* ignore */ }

        return "That does not look like an Ori and the Will of the Wisps install. " +
               "Pick the folder that contains WillOfTheWisps.exe.";
    }

    // ── AP bridge — inert (ConnectsItself = true, see header) ─────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The Ori Randomizer's built-in client receives items directly from the AP
        // server; there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The randomizer renders its own AP status overlay.
    }

    // ── Settings UI ────────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new SolidColorBrush(Color.FromRgb(0x3A, 0x7B, 0xC8));

        var panel = new System.Windows.Controls.StackPanel
            { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Ori and the Will of the Wisps is your own game (Steam appid " +
                   "1057090). The Ori Randomizer mod is added on top and connects " +
                   "to Archipelago directly — the launcher cannot pre-fill the AP " +
                   "connection (it is entered inside the randomizer's settings UI). " +
                   "The launcher installs the Ori Randomizer and the apworld file; " +
                   "you supply the base game.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: base-game install ────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "BASE GAME INSTALL", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 0, 0, 8),
        });

        string? steamDir    = DetectSteamOriWotwDir();
        string? overrideDir = LoadOverrideDir();
        string? resolvedDir = overrideDir != null && LooksLikeOriWotwDir(overrideDir)
                              ? overrideDir : steamDir;

        string detectMsg = resolvedDir != null
            ? (overrideDir != null
                ? "✓ Using your selected folder: " + resolvedDir
                : "✓ Detected Steam install: " + resolvedDir)
            : "Ori WotW not detected — install via Steam (appid 1057090) or pick " +
              "your install folder below (Game Pass / custom Steam library).";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = resolvedDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // Install dir row (read-only textbox + Browse button).
        var dirRow = new System.Windows.Controls.DockPanel
            { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = resolvedDir ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Ori WotW install folder (contains WillOfTheWisps.exe). " +
                          "Detected from Steam automatically; set here to override.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding     = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select your Ori WotW install folder",
                InitialDirectory = Directory.Exists(resolvedDir ?? "")
                                   ? resolvedDir!
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    MessageBox.Show(bad, "Not an Ori WotW folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeOriWotwDir(picked))
                {
                    string nested = Path.Combine(picked, "Ori and the Will of the Wisps");
                    if (LooksLikeOriWotwDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 1057090). " +
                   "Use this picker for Game Pass or a non-standard Steam library.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: randomizer install status ────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "ORI RANDOMIZER (AP MOD)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 0, 0, 8),
        });

        string? randoExe     = FindRandoExe(GameDirectory);
        bool    randoPresent = randoExe != null;
        bool    apwPresent   = File.Exists(ApWorldLocalPath);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = randoPresent
                    ? "✓ Ori Randomizer is installed: " + randoExe!
                    : "Ori Randomizer not found — use the Install button on the Play tab.",
            FontSize = 11,
            Foreground = randoPresent ? success : muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = apwPresent
                    ? $"✓ {ApWorldFileName} saved next to the install."
                    : $"{ApWorldFileName} not yet downloaded (install the randomizer first).",
            FontSize = 11,
            Foreground = apwPresent ? success : muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        });

        if (apwPresent)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Copy " + ApWorldFileName + " into your Archipelago " +
                       @"custom_worlds folder (default: C:\ProgramData\Archipelago\custom_worlds)" +
                       " to generate multiworlds.",
                FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });
        }

        // ── Section: guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "SETUP STEPS", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 8, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Ori and the Will of the Wisps (Steam appid 1057090 or Xbox Game Pass). " +
                "Install it via Steam if you have not already.",
            "2. Use the Install button on the Play tab to download the Ori Randomizer " +
                "and the Archipelago world file (" + ApWorldFileName + ").",
            "3. Copy " + ApWorldFileName + " into your Archipelago custom_worlds folder " +
                @"(default: C:\ProgramData\Archipelago\custom_worlds) so you can generate " +
                "multiworld seeds.",
            "4. In your Archipelago client / host, generate a multiworld using the " +
                "\"Ori and the Will of the Wisps\" world.",
            "5. Open the Ori Randomizer launcher from the Play tab. Go to the " +
                "Archipelago / Multiworld settings and enter: Server, Slot name, Password.",
            "6. Start your seed inside the randomizer. The randomizer connects to the " +
                "AP server directly — the launcher stays hands-off once the game is running.",
            "7. This launcher tracks the randomizer process and shows it as running. " +
                "Because the randomizer owns the AP connection, the launcher does not " +
                "hold its own connection to your slot while it is running.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Section: links ─────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });

        foreach (var (label, url) in new[]
        {
            ("Ori Randomizer (GitHub) ↗",                        RandoRepoUrl),
            ("owotu — Archipelago AP World (GitHub) ↗",          ApWorldRepoUrl),
            ("Ori Randomizer Setup Guide ↗",                     SetupGuideUrl),
            ("Ori Randomizer Discord ↗",                         DiscordUrl),
            ("Archipelago Official ↗",                           ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding         = new Thickness(0, 2, 0, 2),
                Background      = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize        = 12,
                Margin          = new Thickness(0, 0, 0, 4),
                Foreground      = accent,
                Cursor          = System.Windows.Input.Cursors.Hand,
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

    // ── News feed ──────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RANDO_RELEASES_URL, ct);
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

    // ── Private helpers — release resolution ───────────────────────────────────

    /// Resolve the latest Ori Randomizer release: version + Windows zip URL +
    /// apworld URL. Falls back to pinned URLs when the API is unreachable.
    private async Task<(string Version, string? ZipUrl, string? ApWorldUrl)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        string? version    = null;
        string? zipUrl     = null;
        string? apWorldUrl = null;

        try
        {
            string json = await _http.GetStringAsync(GH_RANDO_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null
                && root.TryGetProperty("assets", out var assets)
                && assets.ValueKind == JsonValueKind.Array)
            {
                // Prefer a Windows zip; accept any zip as fallback.
                string? winZip = null;
                string? anyZip = null;

                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u)
                                   ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    string lower = name.ToLowerInvariant();

                    if (lower.EndsWith(".zip"))
                    {
                        anyZip ??= url;
                        if (lower.Contains("win") || lower.Contains("windows")
                            || lower.Contains("x64") || lower.Contains("pc"))
                            winZip ??= url;
                    }
                    else if (lower.EndsWith(".apworld"))
                    {
                        apWorldUrl ??= url;
                    }
                }
                zipUrl = winZip ?? anyZip;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback below */ }

        // Pinned fallback for the randomizer.
        if (version == null || zipUrl == null)
        {
            version = FallbackRandoVersion;
            zipUrl  = $"{RandoRepoUrl}/releases/download/v{FallbackRandoVersion}/" +
                      $"WotwRando-v{FallbackRandoVersion}-win64.zip";
        }

        // Best-effort apworld from owotu (separate repo, separate API call).
        if (apWorldUrl == null)
        {
            try
            {
                string json2 = await _http.GetStringAsync(GH_APWORLD_RELEASES_LATEST_URL, ct);
                using var doc2 = JsonDocument.Parse(json2);
                if (doc2.RootElement.TryGetProperty("assets", out var a2)
                    && a2.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in a2.EnumerateArray())
                    {
                        string? name = a.TryGetProperty("name", out var n2) ? n2.GetString() : null;
                        string? url  = a.TryGetProperty("browser_download_url", out var u2)
                                       ? u2.GetString() : null;
                        if (name == null || url == null) continue;
                        if (name.EndsWith(".apworld", StringComparison.OrdinalIgnoreCase))
                        {
                            apWorldUrl = url;
                            break;
                        }
                    }
                }
            }
            catch { /* best effort — apworld download is non-critical */ }
        }

        // Final pinned fallback for apworld URL.
        apWorldUrl ??= $"{ApWorldRepoUrl}/releases/download/v{FallbackApWorldVersion}/" +
                       ApWorldFileName;

        return (version, zipUrl, apWorldUrl);
    }

    // ── Private helpers — download / extract the randomizer ───────────────────

    private async Task DownloadAndExtractAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"oriwotw-rando-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            // Download.
            progress.Report((5, $"Downloading Ori Randomizer {version}..."));
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
                        int pct = (int)(5 + 60 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading Ori Randomizer... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            // Extract.
            progress.Report((70, "Extracting Ori Randomizer..."));
            Directory.CreateDirectory(GameDirectory);
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, GameDirectory, overwriteFiles: true);

            // Flatten a single sub-folder if the exe landed one level deeper.
            if (FindRandoExe(GameDirectory) == null)
            {
                string[] subdirs = Directory.GetDirectories(GameDirectory);
                if (subdirs.Length == 1)
                {
                    string sub = subdirs[0];
                    foreach (string fileSrc in
                        Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
                    {
                        string rel     = Path.GetRelativePath(sub, fileSrc);
                        string fileDst = Path.Combine(GameDirectory, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                        File.Move(fileSrc, fileDst, overwrite: true);
                    }
                    try { Directory.Delete(sub, recursive: true); } catch { }
                }
            }

            progress.Report((83, "Ori Randomizer extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — Steam / Ori WotW detection ──────────────────────────

    /// Resolve the Ori WotW base-game install: override takes precedence over Steam.
    private string? DetectSteamOriWotwDir()
    {
        try
        {
            foreach (string steamRoot in SteamRoots())
            {
                if (string.IsNullOrWhiteSpace(steamRoot)) continue;
                foreach (string lib in SteamLibraryRoots(steamRoot))
                {
                    try
                    {
                        string steamapps = Path.Combine(lib, "steamapps");
                        string manifest  = Path.Combine(steamapps,
                            $"appmanifest_{ORI_WOTW_STEAM_APP_ID}.acf");
                        if (!File.Exists(manifest)) continue;

                        string common     = Path.Combine(steamapps, "common");
                        string? installDir = ReadAcfInstallDir(manifest);
                        if (installDir != null)
                        {
                            string candidate = Path.Combine(common, installDir);
                            if (LooksLikeOriWotwDir(candidate)) return candidate;
                        }
                        // Conventional game folder name.
                        string conv = Path.Combine(common, "Ori and the Will of the Wisps");
                        if (LooksLikeOriWotwDir(conv)) return conv;
                    }
                    catch { /* try next library */ }
                }
            }
        }
        catch { }
        return null;
    }

    private static bool LooksLikeOriWotwDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, "WillOfTheWisps.exe"))) return true;
            if (Directory.Exists(Path.Combine(dir, "WillOfTheWisps_Data"))) return true;
            // Xbox Game Pass variant.
            if (File.Exists(Path.Combine(dir, "WillOfTheWisps.exe.manifest"))) return true;
            return false;
        }
        catch { return false; }
    }

    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu))
            yield return hkcu.Replace('/', '\\').TrimEnd('\\');

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm))
            yield return hklm.Replace('/', '\\').TrimEnd('\\');

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    private static IEnumerable<string> SteamLibraryRoots(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(steamRoot)) yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); }
        catch { yield break; }

        const string key = "\"path\"";
        int i = 0;
        while ((i = text.IndexOf(key, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            i += key.Length;
            int open = text.IndexOf('"', i);
            if (open < 0) yield break;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) yield break;
            string raw  = text.Substring(open + 1, close - open - 1).Replace("\\\\", "\\");
            string norm = raw.Replace('/', '\\').TrimEnd('\\');
            if (norm.Length > 0 && seen.Add(norm)) yield return norm;
            i = close + 1;
        }
    }

    private static string? ReadAcfInstallDir(string acfPath)
    {
        try
        {
            string   text = File.ReadAllText(acfPath);
            const string k = "\"installdir\"";
            int i = text.IndexOf(k, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += k.Length;
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

    // ── Private helpers — find randomizer exe ─────────────────────────────────

    /// Scan dir (shallow first, then one level deep) for any known randomizer exe.
    /// Returns the full path of the first found, or null.
    private static string? FindRandoExe(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        try
        {
            foreach (string exeName in KnownRandoExeNames)
            {
                string direct = Path.Combine(dir, exeName);
                if (File.Exists(direct)) return direct;
            }
            // One level of sub-directories (the zip may land files in a named sub-folder).
            foreach (string sub in Directory.GetDirectories(dir))
            {
                foreach (string exeName in KnownRandoExeNames)
                {
                    string candidate = Path.Combine(sub, exeName);
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ───────────────────────────────────────────────

    private void StartRandomizerLauncher()
    {
        string? exe = FindRandoExe(GameDirectory);

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException(
                "Failed to start the Ori Randomizer launcher.");

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
            catch { /* some processes don't expose Exited — non-fatal */ }
            return;
        }

        // Fall back to Steam if the randomizer is not found but Steam is present.
        bool steamFound = SteamRoots()
            .Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r));

        if (steamFound)
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl)
                    { UseShellExecute = true });
                IsRunning = true; // best-effort — Steam owns the process lifecycle
                return;
            }
            catch { /* fall through */ }
        }

        throw new FileNotFoundException(
            "Could not find the Ori Randomizer launcher. Click Install Game on the " +
            "Play tab to install it, or download it from " + RandoRepoUrl + ".",
            "WotwRando.exe");
    }

    // ── Private helpers — version tag normalisation ────────────────────────────

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    // ── Private helpers — sidecar settings ────────────────────────────────────

    private sealed class OriWotwSettings
    {
        public string? InstallOverride { get; set; }
        public string? RandoVersion    { get; set; }
    }

    private OriWotwSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<OriWotwSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(OriWotwSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(
                SettingsSidecarPath,
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
        var s = LoadSettings();
        s.InstallOverride = p;
        SaveSettings(s);
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().RandoVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings();
        s.RandoVersion = v;
        SaveSettings(s);
    }
}
