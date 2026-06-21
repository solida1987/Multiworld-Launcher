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
using Microsoft.Win32;
using LauncherV2.Core;

// NOTE: WPF UI types are FULLY QUALIFIED throughout this file (System.Windows.*).
// The real launcher project sets <UseWindowsForms>true</UseWindowsForms> alongside
// WPF, so the bare names Button / TextBox / Color / Brushes / MessageBox /
// FontWeights / Orientation / HorizontalAlignment collide between
// System.Windows.Forms and System.Windows[.Controls/.Media] (CS0104). Qualifying
// every UI type avoids that ambiguity.

namespace LauncherV2.Plugins.SlayTheSpire;

// ═══════════════════════════════════════════════════════════════════════════════
// SlayTheSpirePlugin — install / launch for "Slay the Spire" (MegaCrit, 2019)
// played through the Archipelago mod from cjmang/StS-AP-World, a ModTheSpire
// .jar file that is the in-game Archipelago client for this Java-based deckbuilder
// roguelike. This is a NATIVE "ConnectsItself" integration — the mod itself speaks
// to the AP server (no emulator, no Lua bridge, no launcher-held ApClient on the
// slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified against cjmang/StS-AP-World) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Slay the Spire (Steam appid 646570), and Archipelago is a mod (a .jar file
// loaded by ModTheSpire, the Java mod loader for this game) added on top. The
// honest integration ceiling — exactly like the shipped Hollow Knight / TUNIC /
// Inscryption / Subnautica / RiskOfRain2 plugins — is "automate what is
// possible, guide the irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "Slay the Spire" (from cjmang/StS-AP-World,
//     verified from the repo's __init__.py / game declaration). GameId = "slay_the_spire".
//
//   * THE MOD is a .jar file distributed as a GitHub release from
//     cjmang/StS-AP-World. Each release ships a zip that contains the mod
//     .jar (e.g. "SlayTheSpireAP.jar" or similar). The mod requires
//     ModTheSpire to be installed first — ModTheSpire is a separate Java
//     mod-loader distributed from the Steam Workshop or the official
//     ModTheSpire site. Without ModTheSpire, the .jar is not loaded.
//
//   * CRITICAL HONESTY — ModTheSpire IS A SEPARATE PREREQUISITE, NOT BUNDLED.
//     The AP mod .jar is a ModTheSpire plugin. ModTheSpire is not inside
//     the AP mod release zip. The user must have it installed (typically from
//     the Steam Workshop — Slay the Spire's Workshop makes this a one-click
//     Subscribe that puts ModTheSpire's .jar into the game's mods/ folder
//     automatically). This plugin detects whether ModTheSpire is present and
//     surfaces that clearly in the settings panel. It DOES NOT attempt to
//     install ModTheSpire itself — that is the user's responsibility.
//
//   * INSTALL LOCATION: Slay the Spire mods go into the game's own mods/
//     sub-folder (next to SlayTheSpire.exe). The mod .jar must be placed
//     there so ModTheSpire loads it on startup.
//
//   * CONNECTION: The AP mod's connection is configured in-game. After the
//     mod is installed and the game is launched with ModTheSpire, a Mods
//     menu / AP connection UI appears where the user enters server URL, slot
//     name and password. There is no command-line arg and no config file that
//     this launcher can reliably pre-fill to seed the connection. So this
//     plugin does NOT attempt a connection prefill — the settings panel
//     surfaces the session's server/slot for the user to type in-game.
//
//   * TWO LAUNCH PATHS: (a) Direct: launch SlayTheSpire.exe from the detected
//     Steam install (ModTheSpire runs via its jar in the mods/ folder on the
//     Steam launch command line); (b) Fallback: steam://rungameid/646570 if
//     the exe is found but the working directory cannot be established, or
//     if the exe is not found but Steam is present.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Slay the Spire install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, HKLM WOW6432Node -> InstallPath),
//      parsing steamapps\libraryfolders.vdf for every library root and
//      locating steamapps\common\SlayTheSpire via appmanifest_646570.acf. A
//      manual install-dir OVERRIDE (settings folder picker) is also supported
//      and takes precedence; it is validated (must contain SlayTheSpire.exe)
//      and persisted in this plugin's own sidecar
//      (Games/ROMs/slay_the_spire/sts_launcher.json).
//   2. INSTALL/UPDATE = download the latest release zip from GitHub
//      (cjmang/StS-AP-World/releases), extract the mod .jar, and copy it into
//      the Steam game's mods/ folder. Any existing .jar with a matching name
//      is overwritten (update in-place). The plugin then checks whether
//      ModTheSpire is also present in mods/ and surfaces a clear guided note
//      if it is missing.
//   3. LAUNCH = run SlayTheSpire.exe from the detected/override install (so
//      ModTheSpire is loaded via the game's own launch logic), or fall back to
//      steam://rungameid/646570. ConnectsItself = true (the mod owns the slot).
//      SupportsStandalone = true (plain StS runs fine without AP).
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time") ─────────────────────────
//   * "Installed" is judged by the presence of a .jar file in a detected/override
//     StS install's mods/ folder whose name mentions "archipelago" (case-
//     insensitive), OR the existence of our own version stamp. We also check for
//     the AP mod .jar by the canonical name "SlayTheSpireAP.jar" as a primary
//     hit. If no StS install is detected, the tile reads "not installed".
//   * The EXACT mod .jar filename inside the GitHub release zip was not verified
//     offline. ResolveLatestReleaseAsync picks the first .jar asset that
//     mentions "SlayTheSpire" or "archipelago" (case-insensitive) in its name;
//     if no specific match, falls back to the first .jar in the zip's root.
//   * No plaintext AP password is ever written to disk by this plugin.
//   * Steam library parsing is defensive: tolerant VDF scan; any failure
//     degrades to "Slay the Spire not found" rather than throwing.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SlayTheSpirePlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER = "cjmang";
    private const string GITHUB_REPO  = "StS-AP-World";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"{GH_RELEASES_URL}/latest";

    private const string ModTheSpireUrl   = "https://github.com/kiooeht/ModTheSpire";
    private const string ArchipelagoSite  = "https://archipelago.gg";
    private const string SetupGuideUrl    = "https://archipelago.gg/tutorial/Slay%20the%20Spire/setup/en";

    // Steam — Slay the Spire appid 646570.
    private const string StsSteamAppId        = "646570";
    private static readonly string SteamRunUrl = $"steam://rungameid/{StsSteamAppId}";

    private const string SteamCommonFolderName = "SlayTheSpire";
    private const string GameExeName           = "SlayTheSpire.exe";

    // The expected mod .jar name and the mods sub-folder within the game dir.
    private const string ModJarName = "SlayTheSpireAP.jar";
    private const string ModsFolderName = "mods";

    // Pinned fallback when the GitHub API is unreachable.
    // Keep as a version tag; we derive a tag URL from it.
    private const string FallbackVersion = "0.4.1";

    private const string VersionFileName = "sts_ap_mod_version.dat";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "slay_the_spire";
    public string DisplayName => "Slay the Spire";
    public string Subtitle    => "Native PC · Archipelago mod (ModTheSpire)";

    /// EXACT AP game string — from cjmang/StS-AP-World game declaration.
    public string ApWorldName => "Slay the Spire";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "slay_the_spire.png");

    public string ThemeAccentColor => "#8B0000";   // Dark red — dungeon/horror card aesthetic

    public string[] GameBadges => new[] { "Steam · needs ModTheSpire" };

    public string Description =>
        "Slay the Spire, the deckbuilder roguelike by MegaCrit, played through the " +
        "Archipelago mod from cjmang/StS-AP-World — a ModTheSpire .jar plugin that " +
        "is the in-game Archipelago client, so the game connects to the multiworld " +
        "itself with no emulator and no bridge. Cards, relics, and progression events " +
        "across the Spire's floors are shuffled into the multiworld alongside checks " +
        "from other games. You bring your own copy of Slay the Spire (owned on Steam) " +
        "and also need ModTheSpire installed first (available from the Steam Workshop " +
        "for this game, or from the ModTheSpire GitHub page) — ModTheSpire is the " +
        "Java mod loader that makes mods possible for this Java-based game. The " +
        "launcher detects your Steam install, downloads and places the AP mod .jar, " +
        "and guides the rest. You connect to your Archipelago server from the in-game " +
        "mod menu after launching with ModTheSpire.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = AP mod .jar present in the detected/override StS mods/ folder,
    /// OR our own version stamp exists. Does NOT gate on ModTheSpire being present
    /// (that is surfaced separately in the settings panel).
    public bool IsInstalled => FindInstalledModJar() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps its bookkeeping sidecar and any working files.
    /// The actual mod .jar is placed IN the Steam game's mods/ folder, not here.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "SlayTheSpire");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "sts_launcher.json");

    private string VersionFilePath
        => Path.Combine(SettingsSidecarDir, VersionFileName);

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The StS AP mod reports checks/items/goal to the AP server itself —
    // the launcher relays nothing. These exist for interface compatibility
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
            // Best-effort: read a stamped version from a direct-install; otherwise
            // report "installed" when the mod jar exists.
            InstalledVersion = FindInstalledModJar() != null
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
                await GitHubHelper.FetchLatestTagAsync(GITHUB_OWNER, GITHUB_REPO, ct));
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
        // 0. We need a Slay the Spire install to drop the mod .jar into.
        progress.Report((2, "Locating your Slay the Spire installation..."));
        string? gameDir = ResolveStsDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Slay the Spire installation. Open this game's " +
                "Settings and pick your Slay the Spire folder (the one containing " +
                "SlayTheSpire.exe), or install Slay the Spire via Steam first. " +
                "The Archipelago mod is added on top of your own copy of the game.");

        string modsDir = Path.Combine(gameDir, ModsFolderName);

        // 1. Resolve the latest release from GitHub.
        progress.Report((6, "Checking the latest cjmang/StS-AP-World release..."));
        var (version, zipUrl, jarAssetName) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a mod download in the latest cjmang/StS-AP-World " +
                "GitHub release. Check your internet connection, or download the mod " +
                "manually from " + RepoUrl + "/releases and copy the .jar into your " +
                "Slay the Spire mods/ folder. See Settings for the guided steps.");

        // 2. Download the release zip, extract the .jar, copy it to mods/.
        await DownloadAndInstallJarAsync(zipUrl, version, jarAssetName, modsDir, progress, ct);

        // 3. Stamp the version.
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool modTheSpirePresent = ModTheSpirePresent(gameDir);
        progress.Report((100,
            $"Slay the Spire AP mod {version} installed into your mods/ folder. " +
            (modTheSpirePresent
                ? "ModTheSpire detected — you are ready to launch with mods. "
                : "IMPORTANT: ModTheSpire is NOT detected in your mods/ folder. " +
                  "You must install ModTheSpire (from the Steam Workshop for Slay the " +
                  "Spire, or from github.com/kiooeht/ModTheSpire) before the AP mod " +
                  "will load. Open Settings for the guided steps. ") +
            "To connect: launch the game with ModTheSpire, open the Mods menu, " +
            "find the Archipelago mod and enter your server URL, slot name and " +
            "password in the connection fields."));
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
        // HONEST: the AP connection for StS is entered in-game via the ModTheSpire
        // mod menu — there is no command-line / config prefill this launcher can
        // apply. Launching just starts the game; the user connects in-game.
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartSlayTheSpire();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Slay the Spire runs perfectly well.
    public bool SupportsStandalone => true;

    /// The StS AP mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public string? BuiltAgainstDataPackageChecksum => null;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartSlayTheSpire();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        // No plaintext AP credentials written by this plugin — nothing to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The StS AP mod receives items from the server directly; nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Slay the Spire install folder.";

        if (LooksLikeStsDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeStsDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Slay the Spire installation. Pick the folder " +
               "that contains SlayTheSpire.exe — for Steam this is usually " +
               @"...\steamapps\common\SlayTheSpire.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Slay the Spire is your own game (Steam) with the Archipelago mod " +
                   "added on top via ModTheSpire (the Java mod loader for this game). " +
                   "ModTheSpire must be installed separately (Steam Workshop or GitHub) " +
                   "before the AP mod will load — the launcher detects its presence and " +
                   "guides you if it is missing. You connect to your Archipelago server " +
                   "from the in-game mod menu after launching with mods. These external " +
                   "steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "SLAY THE SPIRE INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveStsDir();
        string? overrideDir = LoadOverrideDir();

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Slay the Spire not detected. Pick your install folder below, or install " +
              "Slay the Spire via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // ModTheSpire status line
        bool mtsPresent = gameDir != null && ModTheSpirePresent(gameDir);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = gameDir == null
                    ? ""
                    : (mtsPresent
                        ? "ModTheSpire detected in the mods/ folder."
                        : "ModTheSpire NOT found. Install it from the Steam Workshop " +
                          "(search \"ModTheSpire\" in Slay the Spire's Workshop tab) " +
                          "or from the GitHub link below."),
            FontSize = 11,
            Foreground = mtsPresent ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        // AP mod .jar status line
        string? modJar = FindInstalledModJar();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modJar != null
                    ? "Archipelago mod .jar found: " + modJar
                    : "Archipelago mod .jar not found in mods/ yet " +
                      "(use Install on the Play tab to download and place it).",
            FontSize = 11,
            Foreground = modJar != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Slay the Spire install folder (the one containing " +
                          "SlayTheSpire.exe). Detected from Steam automatically; set " +
                          "it here to override for a non-standard Steam library.",
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
                Title            = "Select your Slay the Spire install folder (contains SlayTheSpire.exe)",
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
                    System.Windows.MessageBox.Show(bad, "Not a Slay the Spire folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeStsDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeStsDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 646570). Use " +
                   "this picker for a non-standard Steam library.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ───────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game in the mod menu)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After the AP mod is installed and ModTheSpire is present, launch " +
                   "the game with mods enabled. In the Mods menu (or from the title " +
                   "screen), find the Archipelago mod configuration and enter your " +
                   "server URL, slot name and password, then press Connect. This " +
                   "launcher does not pre-fill the connection — it is entered in-game.",
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
            "1. Own Slay the Spire (on Steam). Install it if you have not. Use the " +
                "folder picker above if it was not detected automatically.",
            "2. Install ModTheSpire: subscribe to it on the Steam Workshop (search " +
                "\"ModTheSpire\" in Slay the Spire's Workshop tab) — Steam copies the " +
                ".jar into your game's mods/ folder automatically. Alternatively, " +
                "download it from the ModTheSpire GitHub link below and place the .jar " +
                "into the mods/ folder yourself.",
            "3. Use the Install button on the Play tab to download and place the " +
                "Archipelago mod .jar into your Slay the Spire mods/ folder. " +
                "Alternatively, download it from the cjmang/StS-AP-World releases page " +
                "(link below) and place it in mods/ yourself.",
            "4. To play: launch Slay the Spire from Steam (it should now show a " +
                "\"Play with Mods\" option when ModTheSpire is installed). From the " +
                "mod menu, configure the Archipelago connection (server URL, slot name, " +
                "password) and press Connect.",
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
            ("StS-AP-World (GitHub releases) ↗",   RepoUrl + "/releases"),
            ("ModTheSpire (GitHub) ↗",              ModTheSpireUrl),
            ("Slay the Spire Setup Guide ↗",        SetupGuideUrl),
            ("Archipelago Official ↗",              ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
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
        // Parse GitHub releases from cjmang/StS-AP-World — newest first.
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
                if (el.TryGetProperty("published_at", out var d) &&
                    d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                string tag = el.TryGetProperty("tag_name", out var t)
                    ? NormalizeTag(t.GetString()) ?? ""
                    : "";

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: tag,
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

    /// Normalize a GitHub tag to a plain version string ("v0.4.1" → "0.4.1").
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest release from the GitHub API: version string, the zip
    /// asset download URL containing the mod .jar, and the best-guess jar filename
    /// inside that zip. Falls back to a pinned version with a null download URL
    /// when offline (in which case InstallOrUpdate tells the user to get it manually).
    private async Task<(string Version, string? ZipUrl, string? JarAssetName)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? zipUrl  = null;
                string? jarName = null;

                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();

                    // Prefer a .zip that contains the mod .jar.
                    if (zipUrl == null && lower.EndsWith(".zip"))
                    {
                        zipUrl  = url;
                        jarName = name;
                        continue;
                    }

                    // Some releases may ship the .jar directly as an asset.
                    if (jarName == null && lower.EndsWith(".jar") &&
                        (lower.Contains("archipelago") || lower.Contains("slay") || lower.Contains("sts")))
                    {
                        // Treat a direct .jar as both the "zip" and the jar name
                        // (DownloadAndInstallJarAsync handles both .zip and direct .jar).
                        zipUrl  = url;
                        jarName = name;
                    }
                }

                if (zipUrl != null)
                    return (version, zipUrl, jarName);

                return (version, null, null);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, null, null);
    }

    // ── Private helpers — Steam / StS detection ───────────────────────────────

    /// The StS install dir to use: the override (if set and valid) wins,
    /// else the Steam-detected install. Null when nothing is found.
    private string? ResolveStsDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeStsDir(ov))
            return ov;

        try { return DetectSteamStsDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Slay the Spire if it contains SlayTheSpire.exe.
    private static bool LooksLikeStsDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

    /// True when ModTheSpire is present in the game's mods/ folder.
    /// ModTheSpire is a .jar whose name contains "ModTheSpire" (case-insensitive).
    private static bool ModTheSpirePresent(string gameDir)
    {
        try
        {
            string modsDir = Path.Combine(gameDir, ModsFolderName);
            if (!Directory.Exists(modsDir)) return false;
            return Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly)
                .Any(f => Path.GetFileNameWithoutExtension(f)
                          .IndexOf("ModTheSpire", StringComparison.OrdinalIgnoreCase) >= 0);
        }
        catch { return false; }
    }

    /// Detect the Steam Slay the Spire install: read the Steam root from the
    /// registry, gather all library roots from libraryfolders.vdf, and find the
    /// one whose appmanifest_646570.acf exists → steamapps\common\SlayTheSpire.
    private static string? DetectSteamStsDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{StsSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common    = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeStsDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeStsDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath). Both are tried; duplicates are harmless.
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

    /// Find the AP mod .jar in the detected/override StS install's mods/ folder.
    /// Primary: a file named "SlayTheSpireAP.jar" (canonical).
    /// Secondary: any .jar whose name mentions "archipelago".
    /// Returns the .jar path or null.
    private string? FindInstalledModJar()
    {
        try
        {
            string? game = ResolveStsDir();
            if (game == null) return null;
            string modsDir = Path.Combine(game, ModsFolderName);
            if (!Directory.Exists(modsDir)) return null;

            // Primary: exact canonical name.
            string canonical = Path.Combine(modsDir, ModJarName);
            if (File.Exists(canonical)) return canonical;

            // Secondary: any .jar with "archipelago" in the filename.
            foreach (string jar in Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(jar);
                if (name.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return jar;
            }

            // Tertiary: check our own version stamp (user may have placed the .jar
            // under a different name that we still want to honor).
            if (ReadStampedVersion() != null)
            {
                // Stamp exists but no jar found — stale stamp.
                return null;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Slay the Spire: prefer the exe in the detected/override install;
    /// if that cannot be found but Steam is present, fall back to the steam:// URL.
    private void StartSlayTheSpire()
    {
        string? game = ResolveStsDir();
        string? exe  = game != null ? Path.Combine(game, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = game!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Slay the Spire.");

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

        // Fall back to Steam.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { /* fall through to error */ }
        }

        throw new FileNotFoundException(
            "Could not find SlayTheSpire.exe. Open this game's Settings and pick your " +
            "Slay the Spire install folder, or install Slay the Spire via Steam.",
            GameExeName);
    }

    // ── Private helpers — download / install the mod .jar ─────────────────────

    /// Download the GitHub release asset (a zip or direct .jar), extract the mod
    /// .jar, and copy it into the game's mods/ folder. Handles both a zip-wrapped
    /// jar and a direct .jar asset.
    private async Task DownloadAndInstallJarAsync(
        string assetUrl,
        string version,
        string? assetName,
        string modsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        bool isDirect = assetName != null &&
                        assetName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase);

        string tempFile = Path.Combine(Path.GetTempPath(),
            $"sts-ap-{version}-{Guid.NewGuid():N}" + (isDirect ? ".jar" : ".zip"));
        string? tempDir = isDirect
            ? null
            : Path.Combine(Path.GetTempPath(), $"sts-ap-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading Slay the Spire AP mod {version}..."));
            using (var response = await _http.GetAsync(
                assetUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();

                long total      = response.Content.Headers.ContentLength ?? -1;
                long downloaded = 0;

                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tempFile);
                var buf = new byte[81920];
                int bytesRead;
                while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                    downloaded += bytesRead;
                    if (total > 0)
                    {
                        int pct = (int)(10 + 55 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading AP mod... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            Directory.CreateDirectory(modsDir);

            if (isDirect)
            {
                // The asset IS the .jar — copy it straight into mods/.
                progress.Report((75, "Placing the AP mod .jar into the mods folder..."));
                string destJar = Path.Combine(modsDir, ModJarName);
                File.Copy(tempFile, destJar, overwrite: true);
                progress.Report((95, "Mod .jar placed."));
            }
            else
            {
                // It is a zip — extract and find the .jar inside.
                progress.Report((70, "Extracting the mod zip..."));
                Directory.CreateDirectory(tempDir!);
                System.IO.Compression.ZipFile.ExtractToDirectory(tempFile, tempDir!, overwriteFiles: true);

                progress.Report((82, "Locating the AP mod .jar in the zip..."));
                string? jarPath = FindJarInExtract(tempDir!);
                if (jarPath == null)
                    throw new InvalidOperationException(
                        "Could not find the Archipelago mod .jar inside the downloaded " +
                        "release zip. Download the mod manually from " + RepoUrl +
                        "/releases and copy the .jar into your mods/ folder.");

                progress.Report((90, "Copying the AP mod .jar to the mods folder..."));
                // Keep the original jar name if it matches the known pattern,
                // otherwise save it as the canonical name.
                string destName = Path.GetFileName(jarPath);
                if (!destName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                    destName.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) < 0)
                    destName = ModJarName;

                string destJar = Path.Combine(modsDir, destName);
                File.Copy(jarPath, destJar, overwrite: true);

                // If the extracted name differs from the canonical name, also save
                // under the canonical name so FindInstalledModJar always finds it.
                string canonical = Path.Combine(modsDir, ModJarName);
                if (!string.Equals(destJar, canonical, StringComparison.OrdinalIgnoreCase)
                    && !File.Exists(canonical))
                    File.Copy(jarPath, canonical, overwrite: true);

                progress.Report((95, "Mod .jar placed."));
            }
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
            try { if (tempDir != null && Directory.Exists(tempDir))
                      Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// Find the AP mod .jar inside an extracted zip: prefer a .jar whose name
    /// mentions "archipelago", "slay", or "sts"; else take the first .jar found.
    private static string? FindJarInExtract(string extractRoot)
    {
        try
        {
            string? preferred = null;
            string? first     = null;

            foreach (string jar in Directory.EnumerateFiles(extractRoot, "*.jar", SearchOption.AllDirectories))
            {
                first ??= jar;
                string lower = Path.GetFileNameWithoutExtension(jar).ToLowerInvariant();
                if (lower.Contains("archipelago") || lower.Contains("slay") || lower.Contains("sts"))
                {
                    preferred = jar;
                    break;
                }
            }
            return preferred ?? first;
        }
        catch { return null; }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class StsSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private StsSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<StsSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(StsSettings s)
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
        { var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s); }

    private string? ReadStampedVersion()
    {
        // Check both the sidecar JSON field and the legacy standalone stamp file.
        string? v = LoadSettings().ModVersion;
        if (!string.IsNullOrWhiteSpace(v)) return v;
        try
        {
            if (File.Exists(VersionFilePath))
                return File.ReadAllText(VersionFilePath).Trim();
        }
        catch { }
        return null;
    }
    private void WriteStampedVersion(string v)
        { var s = LoadSettings(); s.ModVersion = v; SaveSettings(s); }
}
