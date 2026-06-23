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

// The launcher project sets BOTH UseWPF and UseWindowsForms=true, so several UI
// type names (Color, Brushes, Button, TextBox, HorizontalAlignment) collide
// between WPF and WinForms. The project's GlobalUsings.cs already aliases each of
// these to its WPF type globally, so this file relies on those — no local aliases
// (a local alias duplicating a global one is CS1537).

namespace LauncherV2.Plugins.Spelunky2;

// ═══════════════════════════════════════════════════════════════════════════════
// Spelunky2Plugin — install / launch for "Spelunky 2" (Mossmouth, 2020) played
// through the Spelunky2-Archipelago mod by DDR-Khat, which uses the Playlunky
// mod loader. This is a NATIVE "ConnectsItself" integration: the mod itself
// contains the in-game AP client and connects to the Archipelago server directly.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Spelunky 2 (Steam appid 418530), and Archipelago support is delivered as a
// mod added on top via the Playlunky mod loader. The verified facts:
//
//   * THE AP WORLD game string is "Spelunky 2" (AP world repo:
//     DDR-Khat/Spelunky2-Archipelago). GameId here = "spelunky2".
//
//   * THE MOD repo is DDR-Khat/Spelunky2-Archipelago (verified 2026-06-14).
//     The mod requires Playlunky as its loader — Playlunky is a mod framework
//     for Spelunky 2 that injects mods at game startup.
//
//   * PLAYLUNKY REQUIREMENT: The Spelunky 2 AP mod is loaded by Playlunky.
//     Playlunky must be installed SEPARATELY (separate GitHub repo:
//     spelunky-fyi/Playlunky). The mod's files are extracted into the
//     Playlunky mods folder (typically <Spelunky2>/Mods/Packs/).
//
//   * CONNECTION is entered IN-GAME via the mod's in-game connection dialog.
//     There is NO command-line argument and NO external config file this
//     launcher can pre-write. The game connects to the AP server itself
//     (ConnectsItself = true) — the launcher must NOT hold its own ApClient
//     on this slot while the mod is running.
//
//   * NO SEPARATE EXTERNAL CLIENT. The mod handles all AP communication
//     internally. This launcher neither downloads nor launches any additional
//     client executable.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Spelunky 2 install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Spelunky 2 via appmanifest_418530.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and
//      takes precedence; validated and persisted in this plugin's own sidecar
//      (Games/ROMs/spelunky2/spelunky2_launcher.json).
//   2. INSTALL/UPDATE = download the mod's release zip from the DDR-Khat repo
//      and extract into <Spelunky2>/Mods/Packs/. Because Playlunky must be
//      installed separately, the plugin surfaces clear numbered guided steps
//      and links (Playlunky repo, mod repo, AP site). Never a fake one-click.
//   3. LAUNCH = prefer Spelunky2.exe in the detected/override install; fall
//      back to steam://rungameid/418530. ConnectsItself = true (the mod owns
//      the slot connection). SupportsStandalone = true.
//
// ── DEFENSIVE / UNVERIFIED ────────────────────────────────────────────────────
//   * "Installed" is judged by the presence of the mod files in the detected
//     Spelunky 2 install's Mods/Packs tree — NOT by an OUR-OWN version stamp,
//     so a hand-installed mod is honored.
//   * Steam library parsing is a tolerant hand-written VDF scan; any failure
//     degrades to "Spelunky 2 not found" rather than throwing.
//   * IsRunning checks for a process named "Spelunky2".
//   * No plaintext AP password is ever written by this plugin.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Spelunky2Plugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string MOD_OWNER = "DDR-Khat";
    private const string MOD_REPO  = "Spelunky2-Archipelago";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // Playlunky — required mod loader (separate install).
    private const string PlaylunkyRepoUrl =
        "https://github.com/spelunky-fyi/Playlunky";
    private const string PlaylunkyReleasesUrl =
        "https://github.com/spelunky-fyi/Playlunky/releases/latest";

    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Spelunky 2 appid 418530.
    private const string S2SteamAppId = "418530";
    private static readonly string SteamRunUrl = $"steam://rungameid/{S2SteamAppId}";

    /// Standard Steam install sub-folder name for Spelunky 2.
    private const string SteamCommonFolderName = "Spelunky 2";

    /// The base-game executable name.
    private const string S2ExeName = "Spelunky2.exe";

    /// Process name (no extension) to check IsRunning.
    private const string S2ProcessName = "Spelunky2";

    /// Mod files are placed in <Spelunky2>/Mods/Packs/<mod-folder>.
    private const string ModPacksFolderRelative = "Mods/Packs";

    /// Pinned fallback version tag when the GitHub API is unreachable.
    private const string FallbackVersion = "1.0.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "spelunky2";
    public string DisplayName => "Spelunky 2";
    public string Subtitle    => "Native PC · Playlunky mod";

    /// EXACT AP game string — matches the world registered in Archipelago.
    public string ApWorldName => "Spelunky 2";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "spelunky2.png");

    public string ThemeAccentColor => "#D4881E";   // gold/brown cave color

    public string[] GameBadges => new[] { "Steam", "Playlunky", "ConnectsItself" };

    public string Description =>
        "Spelunky 2 Archipelago randomizer. Uses the Playlunky mod loader. The mod " +
        "connects to Archipelago when you enter your server details in-game. Install " +
        "Playlunky separately if prompted.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the AP mod files are present in the Spelunky 2
    /// Mods/Packs folder. We do NOT gate on our own stamp so hand installs
    /// are honored.
    public bool IsInstalled => FindInstalledModFolder() != null;

    /// True while Spelunky2.exe is running (process-name check).
    public bool IsRunning
        => Process.GetProcessesByName(S2ProcessName).Length > 0;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps its working files. The actual mod is extracted
    /// INTO the Spelunky 2 install's Mods/Packs folder, not here.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Spelunky2");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "spelunky2_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The mod reports checks/items/goal to the AP server directly — the
    // launcher relays nothing. These exist for interface compatibility
    // (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Lifecycle — CheckForUpdateAsync ───────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = FindInstalledModFolder() != null
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

    // ── Lifecycle — InstallOrUpdateAsync ──────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. Locate the Spelunky 2 install.
        progress.Report((2, "Locating your Spelunky 2 installation..."));
        string? s2Dir = ResolveSpelunky2Dir();
        if (s2Dir == null)
            throw new InvalidOperationException(
                "Could not find a Spelunky 2 installation. Open this game's Settings " +
                "and pick your Spelunky 2 folder (the one containing Spelunky2.exe), " +
                "or install Spelunky 2 via Steam first. The Archipelago mod is added " +
                "on top of your own copy of the game.");

        // Mods/Packs is the conventional Playlunky mod drop location.
        string packsDir = Path.Combine(s2Dir, "Mods", "Packs");

        // 1. Resolve the latest mod release (pinned fallback if API is down).
        progress.Report((6, "Checking the latest Spelunky2-Archipelago release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Spelunky2-Archipelago mod download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                ModRepoUrl + "/releases. Open Settings for the guided setup steps.");

        // 2. Warn about Playlunky (can't automate its install).
        progress.Report((10, "Downloading the Archipelago mod..."));

        // 3. Download + extract the mod zip into Mods/Packs/.
        await DownloadAndExtractModAsync(zipUrl, version, packsDir, progress, ct);

        // 4. Stamp the version so the tile can display it.
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Installed the Spelunky2-Archipelago mod {version} into {packsDir}. " +
            "IMPORTANT: You must have Playlunky installed for the mod to load. " +
            "If Playlunky is not installed yet, download it from the Playlunky " +
            "GitHub releases and follow its setup instructions. Then launch Spelunky 2 " +
            "via this launcher or Steam (Playlunky injects at startup), and enter your " +
            "Archipelago server details in-game to connect."));
    }

    // ── Lifecycle — VerifyInstallAsync ────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── Lifecycle — LaunchAsync ───────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: connection details for Spelunky 2 are entered IN-GAME via
        // the mod's in-game UI. No command-line / config prefill is available
        // (verified). Launching just starts the game; the user enters their
        // server / slot / password via the in-game mod interface.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient
        // on this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartSpelunky2();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Spelunky 2 runs fine.
    public bool SupportsStandalone => true;

    /// The Spelunky2-Archipelago mod owns the slot connection.
    public bool ConnectsItself => true;

    /// No checksum-coupled integration for this native-mod game.
    public string? BuiltAgainstDataPackageChecksum => null;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartSpelunky2();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        _gameProcess = null;
        // No plaintext AP password is ever written to disk by this plugin.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The mod receives items from the AP server directly.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Install validation (override folder picker) ───────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Spelunky 2 install folder.";

        if (LooksLikeSpelunky2Dir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeSpelunky2Dir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Spelunky 2 installation. Pick the folder " +
               @"that contains Spelunky2.exe (for Steam: ...\steamapps\common\Spelunky 2).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xD4, 0x88, 0x1E));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Spelunky 2 is your own game (Steam) with the Spelunky2-Archipelago " +
                   "mod added on top via the Playlunky mod loader. The launcher detects " +
                   "your Steam install and can download the mod files into the Mods/Packs " +
                   "folder for you. Playlunky itself must be installed separately — it " +
                   "injects mods at game startup and is a one-time setup. You connect to " +
                   "your Archipelago server in-game using the mod's connection dialog. " +
                   "These external steps are not verified by this launcher.",
            FontSize = 11,
            Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: Install detect / override ────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "SPELUNKY 2 INSTALL",
            FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? s2Dir       = ResolveSpelunky2Dir();
        string? overrideDir = LoadOverrideDir();

        string detectMsg = s2Dir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + s2Dir
                : "Detected Steam install: " + s2Dir)
            : "Spelunky 2 not detected. Pick your install folder below, or install " +
              "Spelunky 2 via Steam first.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg,
            FontSize = 11,
            Foreground = s2Dir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // Mod status line.
        string? modFolder = FindInstalledModFolder();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modFolder != null
                    ? "Archipelago mod found: " + modFolder
                    : "Archipelago mod not found in Mods/Packs yet. Use Install on the " +
                      "Play tab, or install it manually from the mod releases (link below).",
            FontSize = 11,
            Foreground = modFolder != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // Playlunky detection.
        bool playlunkyFound = s2Dir != null && LooksLikePlaylunkyPresent(s2Dir);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = playlunkyFound
                    ? "Playlunky detected in your Spelunky 2 folder."
                    : "Playlunky not found. You must install Playlunky separately before " +
                      "the mod can load. See the guided steps and links below.",
            FontSize = 11,
            Foreground = playlunkyFound ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Folder picker row.
        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? s2Dir ?? "",
            IsReadOnly = true,
            FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Your Spelunky 2 install folder (contains Spelunky2.exe). " +
                      "Detected from Steam automatically; set it here to override.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...",
            Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select your Spelunky 2 install folder (contains Spelunky2.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? s2Dir ?? "")
                                   ? (overrideDir ?? s2Dir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Spelunky 2 folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeSpelunky2Dir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeSpelunky2Dir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 418530). Use this " +
                   "picker for a non-standard Steam library or another store.",
            FontSize = 11,
            Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Playlunky note ───────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "ABOUT PLAYLUNKY",
            FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Playlunky is the mod loader required by the Spelunky 2 Archipelago mod. " +
                   "It injects mods at game startup. If it is not already installed, download " +
                   "the latest release from the Playlunky GitHub (link below), extract it into " +
                   "your Spelunky 2 folder (next to Spelunky2.exe), and launch the game once " +
                   "standalone to confirm it works before connecting to Archipelago.",
            FontSize = 11,
            Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Connecting (in-game) ─────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game)",
            FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After Playlunky and the mod are installed, launch Spelunky 2 and use " +
                   "the mod's in-game connection interface to enter your Archipelago server, " +
                   "slot name, and password. The mod connects to the AP server itself — this " +
                   "launcher does not pre-fill the connection.",
            FontSize = 11,
            Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP",
            FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Spelunky 2 on Steam (appid 418530). Install it if you have not. " +
                "Use the folder picker above if your install was not detected automatically.",
            "2. Install Playlunky: download the latest release from the Playlunky GitHub " +
                "(link below), extract it into your Spelunky 2 folder (next to Spelunky2.exe). " +
                "Launch the game once standalone to confirm Playlunky is working.",
            "3. Install the Archipelago mod: use the Install button on the Play tab (the " +
                "launcher downloads the mod and extracts it into Mods/Packs/), or download " +
                "it manually from the mod GitHub releases and extract into Mods/Packs/.",
            "4. Launch Spelunky 2 via this launcher or Steam. Playlunky will inject the mod " +
                "at startup and you should see the Archipelago mod active in-game.",
            "5. Use the mod's in-game connection dialog to enter your Archipelago server " +
                "address, slot name, and password. The mod will connect to the AP server " +
                "directly. (This launcher cannot pre-fill the connection.)",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step,
                FontSize = 11,
                Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS",
            FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Spelunky2-Archipelago (GitHub) ↗", ModRepoUrl),
            ("Playlunky (GitHub) ↗",             PlaylunkyRepoUrl),
            ("Playlunky latest release ↗",       PlaylunkyReleasesUrl),
            ("Archipelago Official ↗",            ArchipelagoSite),
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
                try
                {
                    Process.Start(new ProcessStartInfo(u) { UseShellExecute = true });
                }
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
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_URL, ct);
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

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t) ? NormalizeTag(t.GetString()) ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var hu) ? hu.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch
        {
            return Array.Empty<NewsItem>();
        }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// Normalize "v1.2.3" → "1.2.3"; pass other tags through trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: (version, zipUrl).
    /// Falls back to a pinned version + constructed URL when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null &&
                root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;   // .zip with "archipelago" in name
                string? anyZip    = null;   // any .zip
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u)
                        ? u.GetString()
                        : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null && lower.Contains("archipelago"))
                        preferred = url;
                }

                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        // Pinned fallback: construct the download URL from the known tag pattern.
        string fallbackUrl =
            $"{ModRepoUrl}/releases/download/{FallbackVersion}/spelunky2-archipelago.zip";
        return (FallbackVersion, fallbackUrl);
    }

    // ── Private helpers — Steam / Spelunky 2 detection ───────────────────────

    /// The Spelunky 2 install dir to use: the override (if set and valid) wins,
    /// else the Steam-detected install. Null when nothing is found.
    private string? ResolveSpelunky2Dir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeSpelunky2Dir(ov))
            return ov;

        try { return DetectSteamSpelunky2Dir(); }
        catch { return null; }
    }

    /// A folder "looks like" Spelunky 2 if it has Spelunky2.exe.
    private static bool LooksLikeSpelunky2Dir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, S2ExeName))) return true;
            // Secondary signal: the game ships a Mods folder next to the exe.
            if (Directory.Exists(Path.Combine(dir, "Mods"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// True if Playlunky appears to be present in the given Spelunky 2 directory
    /// (looks for playlunky64.dll or playlunky_launcher.exe next to Spelunky2.exe).
    private static bool LooksLikePlaylunkyPresent(string s2Dir)
    {
        try
        {
            return File.Exists(Path.Combine(s2Dir, "playlunky64.dll")) ||
                   File.Exists(Path.Combine(s2Dir, "playlunky_launcher.exe")) ||
                   File.Exists(Path.Combine(s2Dir, "Playlunky64.dll"));
        }
        catch { return false; }
    }

    /// Detect the Steam Spelunky 2 install via Steam registry + VDF library scan.
    private static string? DetectSteamSpelunky2Dir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{S2SteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeSpelunky2Dir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeSpelunky2Dir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath). Both tried; duplicates harmless.
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

        string? progX86 = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    /// Steam stores SteamPath with forward slashes; normalize for Path APIs.
    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    /// All Steam library roots: the Steam root plus every "path" entry in
    /// steamapps\libraryfolders.vdf. Tolerant text scan.
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

    /// Pull every "path" "<value>" pair out of a libraryfolders.vdf body.
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

    /// Read the "installdir" value from an appmanifest_*.acf. Returns null if absent.
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
    private static string? ReadRegistryString(
        RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(subKey);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    // ── Private helpers — installed-mod detection ─────────────────────────────

    /// Find the AP mod folder under <Spelunky2>/Mods/Packs/. Returns the folder
    /// path if found, null otherwise. Checks for any sub-folder in Mods/Packs
    /// that looks like the Archipelago mod (contains a .lua or .dll file that
    /// references Archipelago, or whose name contains "archipelago").
    private string? FindInstalledModFolder()
    {
        try
        {
            string? s2Dir = ResolveSpelunky2Dir();
            if (s2Dir == null) return null;

            string packsDir = Path.Combine(s2Dir, "Mods", "Packs");
            if (!Directory.Exists(packsDir)) return null;

            foreach (string sub in Directory.EnumerateDirectories(packsDir))
            {
                string name = Path.GetFileName(sub);
                if (name.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return sub;

                // Also accept any pack folder whose main .lua references Archipelago.
                try
                {
                    foreach (string lua in Directory.EnumerateFiles(sub, "*.lua",
                        SearchOption.TopDirectoryOnly))
                    {
                        string content = File.ReadAllText(lua);
                        if (content.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                            return sub;
                    }
                }
                catch { /* unreadable — skip */ }
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Spelunky 2: prefer Spelunky2.exe; fall back to Steam URL.
    private void StartSpelunky2()
    {
        string? s2Dir = ResolveSpelunky2Dir();
        string? exe   = s2Dir != null ? Path.Combine(s2Dir, S2ExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = s2Dir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Spelunky 2.");

            _gameProcess = proc;
            try
            {
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) =>
                {
                    GameExited?.Invoke(proc.ExitCode);
                };
            }
            catch { /* some processes don't expose Exited — non-fatal */ }
            return;
        }

        // Fall back to Steam.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                return;
            }
            catch { /* fall through to error */ }
        }

        throw new FileNotFoundException(
            "Could not find Spelunky2.exe. Open this game's Settings and pick your " +
            "Spelunky 2 install folder, or install Spelunky 2 via Steam.",
            S2ExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod's release zip and extract its contents into
    /// <Spelunky2>/Mods/Packs/. Handles both flat zips and single-subfolder zips.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string packsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"spelunky2-archipelago-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((12, $"Downloading Spelunky2-Archipelago mod {version}..."));

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
                        int pct = (int)(12 + 58 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading mod... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Installing mod into Mods/Packs/..."));
            Directory.CreateDirectory(packsDir);

            // Extract into a temp subfolder first, then place appropriately.
            string tempExtract = Path.Combine(Path.GetTempPath(),
                $"s2ap-extract-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tempExtract);
                System.IO.Compression.ZipFile.ExtractToDirectory(
                    tempZip, tempExtract, overwriteFiles: true);

                // Determine the source root: if the zip contains a single top-level
                // sub-folder, treat that as the mod pack folder; otherwise treat
                // tempExtract itself as the mod content.
                string extractRoot = tempExtract;
                string[] topDirs  = Directory.GetDirectories(tempExtract);
                string[] topFiles = Directory.GetFiles(tempExtract);
                if (topDirs.Length == 1 && topFiles.Length == 0)
                    extractRoot = topDirs[0];

                // The target folder in Mods/Packs is named after the extracted folder
                // (or "Spelunky2-Archipelago" as a safe fallback).
                string destFolderName = Path.GetFileName(extractRoot);
                if (string.IsNullOrWhiteSpace(destFolderName))
                    destFolderName = "Spelunky2-Archipelago";

                string destDir = Path.Combine(packsDir, destFolderName);

                // Remove any stale previous install of this folder.
                if (Directory.Exists(destDir))
                {
                    try { Directory.Delete(destDir, recursive: true); } catch { }
                }
                Directory.CreateDirectory(destDir);

                // Move all content from extractRoot into destDir.
                foreach (string fileSrc in Directory.EnumerateFiles(
                    extractRoot, "*", SearchOption.AllDirectories))
                {
                    string rel     = Path.GetRelativePath(extractRoot, fileSrc);
                    string fileDst = Path.Combine(destDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                    File.Copy(fileSrc, fileDst, overwrite: true);
                }
            }
            finally
            {
                try { Directory.Delete(tempExtract, recursive: true); } catch { }
            }

            progress.Report((92, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the install-dir override + an
    // informational version stamp) in its OWN JSON file, kept out of the shared
    // SettingsStore. BOM-less UTF-8, read-modify-write.

    private sealed class Spelunky2Settings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private Spelunky2Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Spelunky2Settings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(Spelunky2Settings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(
                SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — setting just won't persist this time */ }
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
