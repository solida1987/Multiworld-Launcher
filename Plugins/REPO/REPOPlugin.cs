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

namespace LauncherV2.Plugins.REPO;

// ═══════════════════════════════════════════════════════════════════════════════
// REPOPlugin — install / launch for "R.E.P.O." (semiwork, 2025)
// played through the Archipelago mod by Automagic00 — a BepInEx 5 plugin that
// is the in-game Archipelago client. This is a NATIVE "ConnectsItself"
// integration: the game itself speaks to the AP server (no emulator, no Lua
// bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online) ───────────────────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned
// R.E.P.O. (Steam appid 3241660), and Archipelago is a BepInEx 5 MOD added on
// top. The honest integration ceiling — exactly like Hollow Knight / Stardew /
// RiskOfRain2 / Subnautica — is "automate what is possible, guide the rest."
//
//   * THE AP WORLD game string is "R.E.P.O." (from
//     Automagic00/R.E.P.O.-Archipelago-Client-Mod).
//     GameId = "repo".
//
//   * THE MOD is a GitHub release at
//     Automagic00/R.E.P.O.-Archipelago-Client-Mod. Latest releases supply a
//     zip containing the BepInEx/ folder (plugins + possibly the doorstop/core).
//     Install: extract the BepInEx/ folder from the zip into the Steam game dir
//     so that <R.E.P.O.>\BepInEx\plugins\ holds the mod DLL(s).
//
//   * DETECTION — IsInstalled: BepInEx\plugins\ under the game dir contains a
//     DLL whose name matches "*repo*archipelago*" (case-insensitive), or any
//     sub-folder whose name matches similarly and holds a DLL. This honors both
//     a direct-zip drop and a BepInEx profile manager install layout.
//
//   * CONNECTION is made FULLY IN-GAME. The BepInEx mod has a built-in AP
//     client. No command-line arg and no config file that the launcher can
//     pre-write have been documented. Launching from this tile just starts the
//     game; the user connects in-game.
//
//   * "R.E.P.O." is an online co-op horror/physics game (no single-player mode
//     in the traditional sense), though the game exe launches normally.
//     ConnectsItself = true.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam R.E.P.O. install via registry (HKCU SteamPath +
//      HKLM WOW6432Node InstallPath), parsing steamapps\libraryfolders.vdf, and
//      locating steamapps\common\REPO (or "R.E.P.O.") via
//      appmanifest_3241660.acf. A manual install-dir OVERRIDE (folder picker)
//      is also supported, validated (must hold the game exe), and persisted in
//      this plugin's OWN sidecar (Games/ROMs/repo/repo_launcher.json).
//   2. INSTALL/UPDATE = download the GitHub release zip and extract the
//      BepInEx/ folder into the R.E.P.O. install root. Plus clear guided steps
//      so the user knows what happened. Never a fake one-click where BepInEx
//      is not already present — the plugin checks and tells the user.
//   3. LAUNCH = run the game exe (REPO.exe / "R.E.P.O..exe" / Unity fallback)
//      from the detected/override install; fallback to steam://rungameid/3241660.
//
// ── BUILD NOTE ────────────────────────────────────────────────────────────────
// UseWindowsForms=true → all WPF types FULLY QUALIFIED (CS0104 avoidance).
// No file-level `using X = System.Windows...;` aliases (CS1537 in this proj).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class REPOPlugin : IGamePlugin
{
    // ── Constants — the AP R.E.P.O. mod (GitHub, Automagic00) ────────────────
    private const string MOD_OWNER = "Automagic00";
    private const string MOD_REPO  = "R.E.P.O.-Archipelago-Client-Mod";

    private const string ModRepoUrl          = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string ModReleasesPageUrl  = $"{ModRepoUrl}/releases";
    private const string GH_API_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";
    private const string GH_API_LATEST_URL  =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";

    private const string BepInExSite      = "https://github.com/BepInEx/BepInEx";
    private const string SetupGuideUrl    = "https://archipelago.gg";    // no dedicated guide page known at time of writing
    private const string ArchipelagoSite  = "https://archipelago.gg";

    // Steam — R.E.P.O. appid 3241660.
    private const string SteamAppId    = "3241660";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    // Candidate exe names (the game used both during early access).
    private static readonly string[] CandidateExeNames = new[]
    {
        "REPO.exe",
        "R.E.P.O..exe",     // note: trailing dot before extension
        "repo.exe",
    };

    // Candidate Steam "common" folder names for R.E.P.O.
    private static readonly string[] CandidateCommonFolders = new[]
    {
        "REPO",
        "R.E.P.O.",
    };

    // Pinned fallback when the GitHub API is unreachable.
    private const string FallbackVersion = "1.0.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "repo";
    public string DisplayName => "R.E.P.O.";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — from Automagic00/R.E.P.O.-Archipelago-Client-Mod.
    public string ApWorldName => "R.E.P.O.";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "repo.png");

    public string ThemeAccentColor => "#0D7A7A";   // dark cyan/teal — sci-fi horror aesthetic
    public string[] GameBadges     => new[] { "Steam · needs mod", "Co-op" };

    public string Description =>
        "R.E.P.O., semiwork's online co-op horror/physics game, played through the " +
        "Archipelago mod by Automagic00 — a BepInEx 5 plugin that is a fully in-game " +
        "Archipelago client. Items, upgrades, and objectives are shuffled into the " +
        "multiworld, and the game connects to the AP server itself with no emulator " +
        "and no external bridge. You bring your own copy of R.E.P.O. (owned on Steam), " +
        "and the Archipelago mod is added on top via BepInEx. The launcher detects your " +
        "Steam install, downloads and extracts the mod files, and guides the rest. " +
        "You connect to your AP server from inside the game.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the AP mod DLL is present in the detected/override
    /// R.E.P.O. install's BepInEx\plugins tree. We do NOT gate on our own stamp
    /// alone — a manual BepInEx drop is honored too.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and bookkeeping. The actual mod is
    /// extracted INTO the R.E.P.O. Steam install's BepInEx folder, not here.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "REPO");

    /// This plugin's OWN settings sidecar (kept out of shared SettingsStore).
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "repo_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Archipelago R.E.P.O. mod reports checks/items/goal to the AP server
    // itself — the launcher relays nothing. These exist for interface
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
            AvailableVersion = null; // contract: never throw on network failure
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. Locate the R.E.P.O. install to extract the mod into.
        progress.Report((2, "Locating your R.E.P.O. installation..."));
        string? gameDir = ResolveREPODir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a R.E.P.O. installation. Open this game's Settings " +
                "and pick your R.E.P.O. folder (the one containing REPO.exe or " +
                "R.E.P.O..exe), or install R.E.P.O. via Steam first. The Archipelago " +
                "mod is added on top of your own copy of the game.");

        // 1. Resolve the latest mod release.
        progress.Report((6, "Checking the latest Archipelago mod release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Archipelago R.E.P.O. mod download on GitHub. " +
                "Check your internet connection, or download the mod zip manually " +
                "from " + ModReleasesPageUrl + " and extract the BepInEx/ folder " +
                "into your R.E.P.O. install directory. See Settings for the guided " +
                "steps. The mod repo is " + ModRepoUrl + ".");

        // 2. Download + extract BepInEx/ folder into the R.E.P.O. install root.
        await DownloadAndExtractModAsync(zipUrl, version, gameDir, progress, ct);

        // 3. Stamp the version (informational only).
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepInExPresent = Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"))
                           || Directory.Exists(Path.Combine(gameDir, "BepInEx", "plugins"));
        progress.Report((100,
            $"Installed the Archipelago R.E.P.O. mod {version} into your game folder. " +
            (bepInExPresent
                ? "BepInEx looks present. "
                : "NOTE: If BepInEx is not yet set up, the mod will not load — see " +
                  "Settings for the guided steps. ") +
            "To play: launch the game (it must load through BepInEx). You connect " +
            "to your AP server from inside the game."));
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
        // HONEST: R.E.P.O. Archipelago connection is entered IN-GAME via the
        // BepInEx mod's built-in AP client. No command-line / config prefill
        // documented — launching just starts the game; the user connects in-game
        // with their session credentials.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism
        StartREPO();
        return Task.CompletedTask;
    }

    /// R.E.P.O. is an online co-op game — standalone (non-AP) play is supported.
    public bool SupportsStandalone => true;

    /// The Archipelago R.E.P.O. mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public string? BuiltAgainstDataPackageChecksum => null;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartREPO();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No plaintext AP password is ever written to disk by this plugin
        // (connection is entered in-game), so there is nothing to scrub.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The Archipelago R.E.P.O. mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid R.E.P.O. folder contains
    /// REPO.exe or R.E.P.O..exe. Return null when acceptable, else a reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your R.E.P.O. install folder.";

        if (LooksLikeREPODir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        foreach (string candidate in CandidateCommonFolders)
        {
            try
            {
                string nested = Path.Combine(folder, candidate);
                if (LooksLikeREPODir(nested)) return null;
            }
            catch { /* ignore */ }
        }

        return "That does not look like a R.E.P.O. installation. Pick the folder " +
               "that contains REPO.exe or R.E.P.O..exe. For Steam this is usually " +
               @"...\steamapps\common\REPO.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0D, 0x7A, 0x7A));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        string? gameDir     = ResolveREPODir();
        string? overrideDir = LoadOverrideDir();
        string? modDll      = FindInstalledModDll();
        bool    bepInExOk   = gameDir != null &&
                              (Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"))
                            || Directory.Exists(Path.Combine(gameDir, "BepInEx", "plugins")));

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "R.E.P.O. is your own game (Steam) with the Archipelago mod added " +
                   "on top via BepInEx 5. The launcher detects your Steam install, " +
                   "downloads and extracts the mod, and guides the rest. You connect " +
                   "to your Archipelago server from inside the game — there is no " +
                   "connection config the launcher can pre-fill. These external steps " +
                   "are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "R.E.P.O. INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "R.E.P.O. not detected. Pick your install folder below, or install " +
              "R.E.P.O. via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // BepInEx + mod status lines
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = bepInExOk
                    ? "BepInEx found (BepInEx folder present)."
                    : "BepInEx not found yet — it will be extracted by the Install step, " +
                      "or can be set up manually (see steps below).",
            FontSize = 11, Foreground = bepInExOk ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                    ? "Archipelago mod found: " + modDll
                    : "Archipelago mod not found in BepInEx\\plugins yet. Use Install on " +
                      "the Play tab, or extract the mod zip manually into your R.E.P.O. folder.",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Install-dir picker row
        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your R.E.P.O. install folder (containing REPO.exe or R.E.P.O..exe). " +
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
                Title            = "Select your R.E.P.O. install folder (contains REPO.exe)",
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
                    System.Windows.MessageBox.Show(bad, "Not a R.E.P.O. folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeREPODir(picked))
                {
                    foreach (string candidate in CandidateCommonFolders)
                    {
                        string nested = Path.Combine(picked, candidate);
                        if (LooksLikeREPODir(nested)) { picked = nested; break; }
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
            Text = "Steam installs are detected automatically (appid 3241660). Use this " +
                   "picker for a non-standard Steam library or manual install.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ───────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After installing the mod, launch R.E.P.O. and connect to your " +
                   "Archipelago server from within the game using the mod's built-in " +
                   "AP client. The launcher cannot pre-fill the connection — enter your " +
                   "server address, slot name, and password in-game.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
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
            "1. Own R.E.P.O. on Steam (appid 3241660). Install it if you have not. " +
                "Use \"Select folder...\" above if it was not auto-detected.",
            "2. Click Install on the Play tab. The launcher will download the " +
                "Archipelago mod zip from GitHub and extract the BepInEx/ folder " +
                "into your R.E.P.O. install directory.",
            "3. Alternative (manual): download the mod zip from the mod releases " +
                "page (link below) and extract the BepInEx/ folder into your " +
                "R.E.P.O. install folder, so that <R.E.P.O.>\\BepInEx\\plugins\\ " +
                "contains the mod DLL(s).",
            "4. Launch R.E.P.O. from this launcher (or via Steam). The first launch " +
                "with BepInEx may take a moment longer while it initialises.",
            "5. In-game, use the mod's built-in Archipelago client to enter your " +
                "server, slot name, and password, then connect. (This launcher cannot " +
                "pre-fill the connection — it is entered in-game.)",
            "6. For co-op: every player who wants to participate needs the mod " +
                "installed in their own copy of R.E.P.O..",
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
            ("Archipelago R.E.P.O. Mod (releases) ↗", ModReleasesPageUrl),
            ("Mod GitHub Repository ↗",                ModRepoUrl),
            ("BepInEx (mod loader) ↗",                 BepInExSite),
            ("Archipelago Official ↗",                  ArchipelagoSite),
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
                Margin   = new System.Windows.Thickness(0, 0, 0, 4),
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
        // Pull mod releases from GitHub as the AP-relevant news for R.E.P.O.
        try
        {
            string json = await _http.GetStringAsync(GH_API_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
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
                    Version: NormalizeTag(el.TryGetProperty("tag_name", out var t) ? t.GetString() : null) ?? "",
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

    /// "v1.2.3" → "1.2.3" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release from GitHub: version + zip download URL.
    /// Falls back to the pinned fallback version when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_API_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;   // a .zip whose name mentions "archipelago"
                string? anyZip    = null;   // any .zip asset
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name",                 out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null && lower.Contains("archipelago"))
                        preferred = url;
                    if (preferred == null && lower.Contains("repo"))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        // Pinned fallback: we don't have a stable direct URL so return null for
        // the zip URL — the install will inform the user to check manually.
        return (FallbackVersion, null);
    }

    // ── Private helpers — Steam / R.E.P.O. detection ─────────────────────────

    /// The R.E.P.O. install dir to use: override (if set and valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolveREPODir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeREPODir(ov))
            return ov;

        try { return DetectSteamREPODir(); }
        catch { return null; }
    }

    /// A folder "looks like" R.E.P.O. if it contains any of the candidate exes.
    private static bool LooksLikeREPODir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            foreach (string exe in CandidateExeNames)
            {
                if (File.Exists(Path.Combine(dir, exe))) return true;
            }
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam R.E.P.O. install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_3241660.acf exists → steamapps\common\REPO (or R.E.P.O.).
    private static string? DetectSteamREPODir()
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

                    // Prefer installdir from the ACF manifest.
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeREPODir(candidate)) return candidate;
                    }

                    // Fall back to our list of conventional folder names.
                    foreach (string name in CandidateCommonFolders)
                    {
                        string conventional = Path.Combine(common, name);
                        if (LooksLikeREPODir(conventional)) return conventional;
                    }
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots (HKCU + HKLM + conventional path).
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

        // Last-ditch conventional location.
        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    /// All Steam library roots: the Steam root plus every "path" entry in
    /// steamapps\libraryfolders.vdf.
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

    /// Pull every "path" "<value>" pair from a libraryfolders.vdf body.
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

    /// Read the "installdir" value from an appmanifest_*.acf.
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

    /// Safe registry string read; null on any failure.
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

    /// Find the AP R.E.P.O. mod DLL under the detected/override install's
    /// BepInEx\plugins tree (recursive, case-insensitive). Accepts any *.dll
    /// whose name contains both "repo" and "archipelago" (case-insensitive), or
    /// any sub-folder whose name contains one of those keywords that holds a DLL.
    /// Returns the matched path (file or folder) or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? game = ResolveREPODir();
            if (game == null) return null;
            string pluginsDir = Path.Combine(game, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            // 1. A DLL whose name mentions both "repo" and "archipelago".
            foreach (string dll in Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll).ToLowerInvariant();
                if (name.Contains("repo") && name.Contains("archipelago"))
                    return dll;
            }

            // 2. A DLL whose name mentions "archipelago" alone (may be the mod
            //    shipped without "repo" in the DLL name).
            foreach (string dll in Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll).ToLowerInvariant();
                if (name.Contains("archipelago"))
                    return dll;
            }

            // 3. A plugins sub-folder whose name mentions "archipelago" or "repo"
            //    and contains a DLL (profile-manager / nested layout).
            foreach (string sub in Directory.EnumerateDirectories(pluginsDir, "*", SearchOption.AllDirectories))
            {
                string folderName = Path.GetFileName(sub).ToLowerInvariant();
                if (!folderName.Contains("archipelago") && !folderName.Contains("repo")) continue;
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

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartREPO()
    {
        string? game = ResolveREPODir();

        if (game != null)
        {
            // Try each candidate exe in order.
            foreach (string exeName in CandidateExeNames)
            {
                string exePath = Path.Combine(game, exeName);
                if (!File.Exists(exePath)) continue;

                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName         = exePath,
                    WorkingDirectory = game,
                    UseShellExecute  = true,
                });
                if (proc == null) continue;

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
        }

        // Fall back to Steam if Steam is present.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { /* fall through */ }
        }

        throw new FileNotFoundException(
            "Could not find REPO.exe or R.E.P.O..exe. Open this game's Settings and " +
            "pick your R.E.P.O. install folder, or install R.E.P.O. via Steam.",
            "REPO.exe");
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod's GitHub release zip and extract the BepInEx/ folder
    /// into the R.E.P.O. install root. The zip from Automagic00's releases
    /// carries a BepInEx/ subtree; we merge that into the install root so that
    /// <R.E.P.O.>\BepInEx\plugins\ holds the mod DLL(s).
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string gameDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"repo-archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"repo-archipelago-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading Archipelago mod {version}..."));
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

            progress.Report((70, "Unpacking mod zip..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // The zip is expected to carry a BepInEx/ subtree (and possibly a
            // winhttp.dll doorstop). Find the merge root:
            //   - if the zip root contains BepInEx/ directly → use the zip root
            //   - if it is wrapped in a single sub-folder → descend into it
            progress.Report((82, "Installing mod files into R.E.P.O. folder..."));
            string mergeRoot = ResolveMergeRoot(tempExtract, gameDir);
            MergeDirectory(mergeRoot, gameDir);

            progress.Report((95, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))        File.Delete(tempZip); }            catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Decide which extracted subfolder to merge into the game root.
    /// Prefers the first level that contains a "BepInEx" subfolder;
    /// falls back to the extraction root.
    private static string ResolveMergeRoot(string extractedRoot, string gameDir)
    {
        // If the extraction root itself holds a BepInEx folder, merge from there.
        if (Directory.Exists(Path.Combine(extractedRoot, "BepInEx")))
            return extractedRoot;

        // If there is a single wrapping sub-folder that holds BepInEx, descend.
        try
        {
            string[] subdirs = Directory.GetDirectories(extractedRoot);
            string[] files   = Directory.GetFiles(extractedRoot);
            if (files.Length == 0 && subdirs.Length == 1)
            {
                if (Directory.Exists(Path.Combine(subdirs[0], "BepInEx")))
                    return subdirs[0];
            }
        }
        catch { /* ignore */ }

        return extractedRoot;
    }

    /// Recursively merge <src> into <dst>, overwriting files, never deleting
    /// existing user files that are not being overwritten.
    private static void MergeDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, dir);
            Directory.CreateDirectory(Path.Combine(dst, rel));
        }
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel    = Path.GetRelativePath(src, file);
            string target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class REPOSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private REPOSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<REPOSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(REPOSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — setting just won't persist */ }
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
