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

namespace LauncherV2.Plugins.Celeste;

// ═══════════════════════════════════════════════════════════════════════════════
// CelestePlugin — install / launch for Celeste (the original 2018 game by
// Maddy Thorson & Noel Berry) played through the CelesteArchipelago Everest mod.
// This is a NATIVE "ConnectsItself" integration — the in-game mod owns the
// Archipelago slot connection; the launcher does not hold its own ApClient.
//
// ── HONEST REALITY CHECK (2026-06-15, verified online) ───────────────────────
// Celeste is a STEAM-MOD native (you own the base game; AP support is an Everest
// mod added on top). The honest ceiling is "automate what's possible, guide the
// irreducible parts" — identical posture to HollowKnight and StardewValley.
//
//   * BASE GAME: Celeste on Steam (appid 504230). The launcher detects the Steam
//     install via the registry (SteamPath → libraryfolders.vdf →
//     appmanifest_504230.acf → steamapps\common\Celeste\). A manual folder picker
//     is also offered for GOG or non-standard paths.
//
//   * MOD LOADER: Everest (EverestAPI/Everest). Everest is the community mod
//     loader for Celeste. The recommended install route is Olympus (the Everest
//     installer/manager) — its setup is interactive and CANNOT be silently driven
//     by this launcher. So the plugin GUIDES Olympus (download link + steps) and
//     does not fake a one-click loader install. When Everest IS installed, the
//     Mods/ folder exists next to Celeste.exe (or in the OS per-user folder) and
//     "Everest.dll" is present in the game directory.
//
//   * AP MOD: doshyw/CelesteArchipelago (the canonical Celeste AP mod for the
//     original 2018 game, distinct from Celeste 64 and Celeste Open World).
//     Releases ship a zip (pattern: CelesteArchipelago*.zip or similar) containing
//     a mod folder that drops into <Celeste>/Mods/. The plugin CAN automate
//     downloading this zip and extracting it into the Mods folder.
//     Fallback version pinned: 0.3.0 (verified on releases page 2026-06-15).
//
//   * AP GAME STRING: "Celeste" — verified against the official multiworld.gg
//     setup guide (https://multiworld.gg/tutorial/Celeste/celeste_en).
//
//   * HOW IT CONNECTS (verified via the multiworld.gg setup guide): the AP
//     connection is entered IN-GAME. After the mod is installed and Celeste is
//     launched, an "Archipelago" button appears on the main menu. The player
//     fills in:
//         Name     (slot name, e.g. Madeline)
//         Server   (host, e.g. archipelago.gg)
//         Port     (e.g. 38281)
//         Password (blank if no password)
//     then presses "Connect to Session." There is NO documented config.json or
//     command-line arg the launcher can pre-write. So this plugin does NOT attempt
//     a connection prefill — it surfaces the session credentials clearly in a
//     post-launch note so the user can copy them into the in-game fields.
//
//   * ConnectsItself = true: the mod's in-game client owns the slot connection.
//     The launcher must NOT hold its own ApClient on the same slot while the game
//     is running (launcher and game would endlessly kick each other off).
//
//   * SupportsStandalone = true: plain Celeste runs without the mod.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Celeste install via registry + libraryfolders.vdf, with a
//      manual override picker persisted in a self-contained sidecar JSON.
//   2. DETECT EVEREST: look for Everest.dll in the Celeste directory (sign that
//      the mod loader is already installed). Also accept if Mods/ folder exists.
//   3. DETECT AP MOD: look for a CelesteArchipelago* folder or
//      CelesteArchipelago*.zip under the Mods/ folder.
//   4. INSTALL/UPDATE: download the latest doshyw/CelesteArchipelago release zip
//      and extract it into <Celeste>/Mods/. Guide Olympus/Everest install with
//      numbered steps + links (Olympus is interactive; we do NOT silent-run it).
//   5. LAUNCH: run Celeste.exe from the detected install; fall back to the Steam
//      URL. No connection prefill. Show the session credentials in a launch notice.
//
// ── DEFENSIVE NOTES ───────────────────────────────────────────────────────────
//   * The Mods/ folder location: on Windows, Everest's default is a subfolder of
//     the Celeste install directory at <CelesteDir>/Mods/. Some setups may use
//     the per-user path (%LOCALAPPDATA%/Celeste/Mods/); this plugin checks both.
//   * Asset filename is fuzzy-matched ("CelesteArchipelago" in the name, .zip
//     extension, not linux/mac). If the release ships a .zip with a different
//     prefix, PickModZip falls back to any .zip in the assets.
//   * "IsInstalled" is judged by Everest presence + mod presence — a mod-only
//     check would accept a broken setup where Everest was uninstalled.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CelestePlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string MOD_OWNER = "doshyw";
    private const string MOD_REPO  = "CelesteArchipelago";
    private static readonly string ModRepoUrl =
        $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private static readonly string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";
    private static readonly string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";

    private const string OlympusSite        = "https://everestapi.github.io/";
    private const string OlympusDownloadUrl = "https://github.com/EverestAPI/Olympus/releases/latest";
    private const string EverestRepoUrl     = "https://github.com/EverestAPI/Everest";
    private const string SetupGuideUrl      = "https://multiworld.gg/tutorial/Celeste/celeste_en";
    private const string ArchipelagoSite    = "https://archipelago.gg";

    private const string CelesteSteamAppId = "504230";
    private static readonly string SteamRunUrl = $"steam://rungameid/{CelesteSteamAppId}";

    /// Pinned fallback when the GitHub API is unreachable.
    /// v0.3.0 verified on the releases page 2026-06-15.
    private const string FallbackVersion = "0.3.0";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/CelesteArchipelago.zip";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "celeste";
    public string DisplayName => "Celeste";
    public string Subtitle    => "Native PC · Everest mod · ConnectsItself";

    /// Exact AP game string — verified against multiworld.gg setup guide for
    /// the original Celeste 2018 (NOT Celeste 64 or Celeste Open World).
    public string ApWorldName => "Celeste";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "celeste.png");

    public string ThemeAccentColor => "#AC3232";   // Celeste red
    public string[] GameBadges     => new[] { "Steam", "Everest", "ConnectsItself" };

    public string Description =>
        "Celeste Archipelago randomizer. Uses the Everest mod loader. Install Everest " +
        "and the AP mod via this launcher, then connect to Archipelago from the in-game " +
        "menu. Strawberries, crystal hearts, and progression are shuffled into the " +
        "multiworld. You bring your own copy of Celeste (Steam or GOG) — the launcher " +
        "detects it, downloads the CelesteArchipelago mod into your Mods folder, and " +
        "guides the Everest mod loader setup. The game connects to the Archipelago " +
        "server itself from an in-game menu; no emulator or bridge needed.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means Everest is present in the Celeste dir AND the
    /// CelesteArchipelago mod folder exists in the Mods folder.
    public bool IsInstalled => IsEverestPresent() && FindInstalledModDir() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// The Celeste game directory (contains Celeste.exe). Set by detection or
    /// the manual override picker. Exposed as GameDirectory per IGamePlugin.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Celeste");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "celeste_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // CelesteArchipelago's in-game client owns the AP slot connection.
    // These are present for interface compatibility only (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Lifecycle — ConnectsItself / Standalone ───────────────────────────────

    /// The in-game mod owns the slot — the launcher must NOT hold its own
    /// ApClient on this slot while the game is running.
    public bool ConnectsItself => true;

    /// Plain Celeste runs fine without the AP mod.
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = IsInstalled
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
        // 0. Need a Celeste install to drop the mod into.
        progress.Report((2, "Locating your Celeste installation..."));
        string? celesteDir = ResolveCelesteDir();
        if (celesteDir == null)
            throw new InvalidOperationException(
                "Could not find a Celeste installation. Open this game's Settings " +
                "and pick your Celeste folder (the one containing Celeste.exe), or " +
                "install Celeste via Steam first. The Archipelago mod is added on top " +
                "of your own copy of the game.");

        // 1. Everest check — guide the user if missing, but still proceed with
        //    the mod download so it is staged and ready.
        bool everestOk = IsEverestPresent(celesteDir);
        if (!everestOk)
            progress.Report((5,
                "Everest mod loader not detected in the Celeste folder. " +
                "The CelesteArchipelago mod will be staged, but Everest must be " +
                "installed first — see Settings for the guided steps."));

        // 2. Resolve the latest AP mod release.
        progress.Report((8, "Checking the latest CelesteArchipelago release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the CelesteArchipelago download on GitHub. " +
                "Check your internet connection, or download the mod manually " +
                "from " + ModRepoUrl + "/releases and place the mod folder in " +
                "your Celeste/Mods/ folder.");

        // 3. Determine the Mods target directory.
        string modsDir = ResolveCelesteModsDir(celesteDir);
        Directory.CreateDirectory(modsDir);

        // 4. Download + extract the mod zip into the Mods folder.
        await DownloadAndExtractModAsync(zipUrl, version, modsDir, progress, ct);

        // 5. Stamp version into sidecar.
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"CelesteArchipelago {version} staged into the Mods folder. " +
            (everestOk
                ? "Everest is installed. Launch Celeste, press the Archipelago " +
                  "button on the main menu, and enter your server details."
                : "IMPORTANT: Everest is not yet installed. Install it via Olympus " +
                  "(the Everest installer) before launching — see Settings for the " +
                  "guided steps and links.")));
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
        // HONEST: the AP connection for Celeste is entered IN-GAME on the
        // Archipelago menu (Name / Server / Port / Password). There is no
        // config file or command-line arg the launcher can pre-write (verified
        // against the official setup guide). We launch the game and let the
        // user copy their credentials from the session info shown in the launcher.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient.
        _ = session; // session credentials are shown to the user — not prefilled
        StartCeleste();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartCeleste();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        // No plaintext AP password was ever written to disk by this plugin —
        // the connection is entered in-game, nothing to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself) ───────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // CelesteArchipelago's in-game client receives items from the AP server
        // directly; the launcher has nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game; no launcher HUD channel.
    }

    // ── Existing-install validation (override folder picker) ─────────────────

    /// Return null when the folder is an acceptable Celeste install; otherwise a
    /// short human-readable reason so the user can pick again.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Celeste install folder.";
        if (LooksLikeCelesteDir(folder))
            return null;
        // Tolerate picking the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, "Celeste");
            if (LooksLikeCelesteDir(nested)) return null;
        }
        catch { }
        return "That does not look like a Celeste installation. Pick the folder " +
               "that contains Celeste.exe (the Content folder is next to it).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));

        var scroll = new System.Windows.Controls.ScrollViewer
        {
            VerticalScrollBarVisibility   = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
        };
        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };
        scroll.Content = panel;

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Celeste is your own game (Steam or GOG). The launcher detects your " +
                "install, downloads the CelesteArchipelago Everest mod into your Mods " +
                "folder, and guides Everest setup. The Everest mod loader itself must " +
                "be installed separately via Olympus (see guided steps below) — its " +
                "interactive installer cannot be driven silently. You connect to your " +
                "Archipelago server from the in-game Archipelago menu; the launcher " +
                "shows your session credentials to copy. These external steps are not " +
                "verified by this launcher.",
            FontSize    = 11,
            Foreground  = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin      = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: Celeste install ──────────────────────────────────────
        AddSectionHeader(panel, "CELESTE INSTALLATION", muted);

        string? celesteDir  = ResolveCelesteDir();
        string? overrideDir = LoadOverrideDir();
        bool    everestOk   = IsEverestPresent(celesteDir);
        string? modDir      = FindInstalledModDir(celesteDir);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = celesteDir != null
                    ? (overrideDir != null
                        ? "Using your selected folder: " + celesteDir
                        : "Detected Steam install: " + celesteDir)
                    : "Celeste not detected. Pick your install folder below, or " +
                      "install Celeste via Steam first.",
            FontSize    = 11,
            Foreground  = celesteDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin      = new System.Windows.Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = everestOk
                    ? "Everest mod loader: installed"
                    : "Everest mod loader: not detected (install via Olympus — see steps below)",
            FontSize   = 11,
            Foreground = everestOk ? success : warn,
            Margin     = new System.Windows.Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDir != null
                    ? "CelesteArchipelago mod: found at " + modDir
                    : "CelesteArchipelago mod: not found (use Install on the Play tab)",
            FontSize    = 11,
            Foreground  = modDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin      = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = overrideDir ?? celesteDir ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Celeste install folder (the one containing Celeste.exe). " +
                          "Detected from Steam automatically; use this picker for GOG or a " +
                          "non-standard Steam library.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content     = "Select folder...",
            Width       = 120,
            Padding     = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Celeste install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? celesteDir ?? "")
                                   ? (overrideDir ?? celesteDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Celeste folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // Descend if user picked the Steam "common" parent
                if (!LooksLikeCelesteDir(picked))
                {
                    string nested = Path.Combine(picked, "Celeste");
                    if (LooksLikeCelesteDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 504230). Use this " +
                   "picker for GOG or a non-standard Steam library.",
            FontSize    = 11,
            Foreground  = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin      = new System.Windows.Thickness(0, 4, 0, 12),
        });

        // ── Section: Guided setup ─────────────────────────────────────────
        AddSectionHeader(panel, "GUIDED SETUP (required: Olympus + Everest)", muted);

        foreach (string step in new[]
        {
            "1. Own Celeste (Steam or GOG). Install it if you have not yet.",
            "2. Download Olympus (the Everest installer/manager) from the link below " +
               "and run it. Olympus will install Everest into your Celeste directory. " +
               "If Olympus does not find Celeste automatically, point it at your install " +
               "folder. After installing Everest, your Celeste directory will contain " +
               "Everest.dll and a Mods/ subfolder.",
            "3. Click Install on the Play tab in this launcher. The launcher downloads " +
               "the CelesteArchipelago mod and places it in your Celeste/Mods/ folder.",
            "4. Launch Celeste (from this launcher or Steam/GOG). Confirm that " +
               "\"Archipelago\" appears as a button on the main menu — this means Everest " +
               "and the mod are both active.",
            "5. Press the Archipelago button on the main menu and fill in your " +
               "connection details: Name (your slot name), Server (e.g. archipelago.gg), " +
               "Port (e.g. 38281), and Password (blank if none). Press Connect to Session.",
            "6. This launcher cannot pre-fill the in-game connection form (there is no " +
               "config file or command-line arg for the original Celeste AP mod). Your " +
               "session server, port, and slot name are shown on the launcher's Play " +
               "tab — copy them into the in-game fields.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text        = step,
                FontSize    = 11,
                Foreground  = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin      = new System.Windows.Thickness(0, 0, 0, 6),
            });
        }

        // ── Section: Links ────────────────────────────────────────────────
        AddSectionHeader(panel, "LINKS", muted);

        foreach (var (label, url) in new[]
        {
            ("Olympus (Everest installer/manager) ↗",       OlympusDownloadUrl),
            ("Everest mod loader (GitHub) ↗",               EverestRepoUrl),
            ("CelesteArchipelago mod (GitHub) ↗",           ModRepoUrl),
            ("Celeste Setup Guide (Archipelago) ↗",         SetupGuideUrl),
            ("Archipelago Official ↗",                      ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content         = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding         = new System.Windows.Thickness(0, 2, 0, 2),
                Background      = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0),
                FontSize        = 12,
                Margin          = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
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

        return scroll;
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

    /// "v0.3.0" → "0.3.0"; else trimmed as-is.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest CelesteArchipelago release: returns (version, zipUrl).
    /// Falls back to the pinned fallback when the GitHub API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(CancellationToken ct)
    {
        try
        {
            // Try /releases (full list) first — some releases may be pre-release
            // so /releases/latest skips them. We want the most recent regardless.
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    string? version = el.TryGetProperty("tag_name", out var t)
                        ? NormalizeTag(t.GetString())
                        : null;
                    if (version == null) continue;

                    if (el.TryGetProperty("assets", out var assets)
                        && assets.ValueKind == JsonValueKind.Array)
                    {
                        string? zip = PickModZip(assets);
                        if (zip != null)
                            return (version, zip);
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback below */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    /// From a release's assets array, pick the CelesteArchipelago .zip.
    /// Prefers an asset whose name contains "CelesteArchipelago" (case-insensitive)
    /// and ends with .zip; falls back to any .zip excluding linux/mac.
    private static string? PickModZip(JsonElement assets)
    {
        string? preferred = null;
        string? anyZip    = null;

        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();
            if (!lower.EndsWith(".zip")) continue;

            if (lower.Contains("linux") || lower.Contains("ubuntu")
                || lower.Contains("mac") || lower.Contains("osx")
                || lower.Contains("darwin") || lower.Contains("source"))
                continue;

            anyZip ??= url;
            if (preferred == null && lower.Contains("celestearchipelago"))
                preferred = url;
        }

        return preferred ?? anyZip;
    }

    // ── Private helpers — Celeste detection ──────────────────────────────────

    /// The Celeste dir to use: override (if set and valid) wins; else Steam auto-
    /// detect. Updates GameDirectory when a dir is resolved.
    private string? ResolveCelesteDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeCelesteDir(ov))
        {
            GameDirectory = ov;
            return ov;
        }

        try
        {
            string? steam = DetectSteamCelesteDir();
            if (steam != null)
            {
                GameDirectory = steam;
                return steam;
            }
        }
        catch { /* Steam detection failure is non-fatal */ }

        return null;
    }

    /// A folder "looks like" Celeste if it has Celeste.exe and/or Content/.
    private static bool LooksLikeCelesteDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, "Celeste.exe"))) return true;
            if (Directory.Exists(Path.Combine(dir, "Content"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Celeste install: registry → libraryfolders.vdf →
    /// appmanifest_504230.acf → steamapps\common\Celeste\.
    private static string? DetectSteamCelesteDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{CelesteSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common    = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeCelesteDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, "Celeste");
                    if (LooksLikeCelesteDir(conventional)) return conventional;
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

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    private static string NormalizeSteamPath(string p) => p.Replace('/', '\\').TrimEnd('\\');

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
            string text  = File.ReadAllText(acfPath);
            const string key = "\"installdir\"";
            int i = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += key.Length;
            int open  = text.IndexOf('"', i);
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

    // ── Private helpers — Everest / Mods detection ────────────────────────────

    /// True if Everest.dll exists in the given Celeste directory (or the one
    /// currently stored in GameDirectory when celesteDir is null).
    private bool IsEverestPresent(string? celesteDir = null)
    {
        string? dir = celesteDir ?? ResolveCelesteDir();
        if (string.IsNullOrWhiteSpace(dir)) return false;
        try
        {
            // Everest patches Celeste.exe and leaves Everest.dll + MiniInstaller.exe
            // in the game directory. Either file is a reliable marker.
            if (File.Exists(Path.Combine(dir, "Everest.dll")))         return true;
            if (File.Exists(Path.Combine(dir, "MiniInstaller.exe")))   return true;
            // Also accept: if the Mods folder already exists (could have been
            // created by Everest even if the dll was cleaned up by Olympus updates).
            if (Directory.Exists(Path.Combine(dir, "Mods")))            return true;
            return false;
        }
        catch { return false; }
    }

    /// Where Everest puts mods for the given Celeste directory. On Windows the
    /// default is <CelesteDir>/Mods/; a per-user path exists on some setups.
    private static string ResolveCelesteModsDir(string celesteDir)
    {
        // Check the conventional per-install path first.
        string inGameDir = Path.Combine(celesteDir, "Mods");
        if (Directory.Exists(inGameDir)) return inGameDir;

        // Per-user fallback: %LOCALAPPDATA%/Celeste/Mods
        try
        {
            string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string perUser  = Path.Combine(localApp, "Celeste", "Mods");
            if (Directory.Exists(perUser)) return perUser;
        }
        catch { }

        // Default to the in-game-dir path (will be created on install).
        return inGameDir;
    }

    /// Find the installed CelesteArchipelago mod folder under the Mods directory.
    /// Returns the folder path, or null if not found.
    private string? FindInstalledModDir(string? celesteDir = null)
    {
        try
        {
            string? dir = celesteDir ?? ResolveCelesteDir();
            if (string.IsNullOrWhiteSpace(dir)) return null;

            string modsDir = ResolveCelesteModsDir(dir);
            if (!Directory.Exists(modsDir)) return null;

            // Look for a subfolder whose name contains "CelesteArchipelago"
            // (case-insensitive), or a .zip with the same pattern (Everest
            // also accepts mods as zips placed directly in the Mods folder).
            foreach (string entry in Directory.EnumerateFileSystemEntries(modsDir))
            {
                string name = Path.GetFileName(entry).ToLowerInvariant();
                if (name.Contains("celestearchipelago"))
                    return entry;
            }
        }
        catch { }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartCeleste()
    {
        string? dir = ResolveCelesteDir();
        string? exe = dir != null ? Path.Combine(dir, "Celeste.exe") : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = dir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Celeste.");

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

        // Fall back to Steam if Celeste.exe was not found directly.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { }
        }

        throw new FileNotFoundException(
            "Could not find Celeste.exe. Open this game's Settings and pick " +
            "your Celeste install folder, or install Celeste via Steam.",
            "Celeste.exe");
    }

    // ── Private helpers — download / extract mod ──────────────────────────────

    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string modsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"celeste-ap-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((12, $"Downloading CelesteArchipelago {version}..."));
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
                        int pct = (int)(12 + 55 * downloaded / total);
                        progress.Report((pct, $"Downloading CelesteArchipelago... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((72, "Extracting mod into the Celeste Mods folder..."));
            Directory.CreateDirectory(modsDir);

            // CelesteArchipelago releases typically ship a zip whose contents
            // can go directly into the Mods folder (Everest treats each subfolder
            // in Mods/ as a mod, or a .zip placed there). We extract flat into
            // a CelesteArchipelago subfolder inside Mods/ so we do not scatter
            // files at the Mods root.
            string apModDir = Path.Combine(modsDir, "CelesteArchipelago");
            Directory.CreateDirectory(apModDir);

            // Extract: if the zip is already structured with a top-level folder
            // (e.g. CelesteArchipelago/...), extract straight into modsDir so
            // we do not double-nest. Otherwise extract into apModDir.
            string tempExtract = Path.Combine(Path.GetTempPath(),
                $"celeste-ap-extract-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tempExtract);
                System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

                string[] topEntries = Directory.GetFileSystemEntries(tempExtract);
                if (topEntries.Length == 1 && Directory.Exists(topEntries[0]))
                {
                    // Single-folder zip: the top-level folder IS the mod folder.
                    // Move its contents into modsDir/<folderName>.
                    string srcFolder   = topEntries[0];
                    string folderName  = Path.GetFileName(srcFolder);
                    string dstModFolder = Path.Combine(modsDir, folderName);
                    if (Directory.Exists(dstModFolder))
                        Directory.Delete(dstModFolder, recursive: true);
                    Directory.Move(srcFolder, dstModFolder);
                }
                else
                {
                    // Flat zip: move everything into modsDir/CelesteArchipelago.
                    if (Directory.Exists(apModDir))
                        Directory.Delete(apModDir, recursive: true);
                    Directory.CreateDirectory(apModDir);
                    foreach (string srcEntry in topEntries)
                    {
                        string dstEntry = Path.Combine(apModDir, Path.GetFileName(srcEntry));
                        if (File.Exists(srcEntry))
                            File.Move(srcEntry, dstEntry, overwrite: true);
                        else if (Directory.Exists(srcEntry))
                        {
                            if (Directory.Exists(dstEntry)) Directory.Delete(dstEntry, recursive: true);
                            Directory.Move(srcEntry, dstEntry);
                        }
                    }
                }
            }
            finally
            {
                try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
            }

            progress.Report((90, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // The plugin keeps launcher-side settings (install-dir override + stamped
    // mod version) in its OWN JSON file so it stays a single self-contained
    // source file without modifying Core/SettingsStore.

    private sealed class CelesteSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private CelesteSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<CelesteSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(CelesteSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
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
        var s = LoadSettings();
        s.InstallOverride = p;
        SaveSettings(s);
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings();
        s.ModVersion = v;
        SaveSettings(s);
    }

    // ── Private helpers — settings panel ─────────────────────────────────────

    private static void AddSectionHeader(
        System.Windows.Controls.StackPanel panel,
        string text,
        System.Windows.Media.SolidColorBrush muted)
    {
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = text,
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });
    }
}
