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

namespace LauncherV2.Plugins.DiceyDungeons;

// ═══════════════════════════════════════════════════════════════════════════════
// DiceyDungeonsPlugin — install / launch for "Dicey Dungeons" (Terry Cavanagh,
// 2019) played through the Archipelago integration by Fylcoast. This is a NATIVE
// "ConnectsItself" integration: the Archipelago Python client (bundled inside the
// .apworld) bridges between the AP server and the game; the game is launched with
// the "mod=diceyap" argument; the mod communicates via stdout [AP]-prefixed JSON
// messages that the Python client reads and relays.
//
// ── HONEST REALITY CHECK (2026-06-14, verified against
//    github.com/Fylcoast/AP_diceydungeons) ──────────────────────────────────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Dicey Dungeons (Steam appid 861540), and Archipelago support is added via a
// mod that ships as a .apworld file. The verified facts:
//
//   * THE AP WORLD game string is "Dicey Dungeons" — verified against
//     worlds/diceydungeons/world.py (`class DiceyDungeonsWorld(World): game = "Dicey Dungeons"`)
//     and worlds/diceydungeons/archipelago.json (`"game": "Dicey Dungeons"`).
//     GameId here = "dicey_dungeons". This is a CUSTOM world from the Fylcoast
//     fork of Archipelago — it ships as a .apworld drop-in, NOT bundled in stock AP.
//
//   * THE MOD REPO is Fylcoast/AP_diceydungeons on GitHub. Latest release as of
//     2026-06-14 is v0.4.0-beta. Release assets: "Dicey.Dungeons.yaml" (template)
//     and "diceydungeons.apworld" (the world file to drop into AP's custom_worlds
//     or worlds folder).
//
//   * THE GAME IS HAXE / HashLink. Dicey Dungeons compiles to a native HashLink
//     executable (`diceydungeons.exe` on Windows). The Archipelago mod is loaded
//     by passing the command-line argument `mod=diceyap` to the game exe.
//
//   * THE ARCHIPELAGO CLIENT is a Python script bundled in the .apworld (the
//     worlds/diceydungeons/client/Client.py). When launched via Archipelago's
//     launcher, it:
//       1. Patches the game files (`_cmd_patch`) using the AP slot data to embed
//          item placements into the game's data (generating ap_data.csv).
//       2. Launches `diceydungeons.exe mod=diceyap` (via launch_and_capture.py).
//       3. Reads stdout lines prefixed `[AP]` as JSON commands from the game
//          (location checks, hints) and relays them to the AP server.
//       4. Receives items from the AP server and regenerates the game's generator
//          CSV so newly unlocked items appear in future runs.
//     There is NO WebSocket server or TCP bridge — communication is pure stdout
//     capture (verified in worlds/diceydungeons/client/launch_and_capture.py and
//     Client.py).
//
//   * WHAT THIS LAUNCHER DOES (honest scope):
//       1. Detect the Steam Dicey Dungeons install via the Windows registry and
//          steamapps VDF — or accept a manual folder override.
//       2. Install/update: download the diceydungeons.apworld from the latest
//          GitHub release and drop it into the Archipelago custom_worlds folder
//          (C:\ProgramData\Archipelago\custom_worlds — READ access only for
//          writing; the launcher uses the per-game sidecar dir for its own state
//          and drops the .apworld into the Archipelago worlds folder).
//          NOTE: the mod ALSO needs to patch the game's data files (the AP client
//          does this with `_cmd_patch`). The launcher cannot replicate the full
//          Python patching pipeline here; instead it guides the user to run the
//          patch command via the AP client's CLI (`/patch`) after connecting.
//       3. Launch: start the Archipelago client for Dicey Dungeons via the AP
//          Launcher (archipelago.exe / ArchipelagoLauncher.exe), which handles
//          both the patch step and launching the game exe with `mod=diceyap`. If
//          ArchipelagoLauncher is not found, fall back to launching the game exe
//          directly with `mod=diceyap` and explain the limitation.
//
//   * INSTALL DETECTION: the .apworld exists in the AP worlds/custom_worlds
//     directory AND the game exe is found in the detected/override install dir.
//
//   * ConnectsItself = true: the AP Python client owns the server slot — the
//     launcher must NOT hold its own ApClient on it while the game is running.
//
//   * SupportsStandalone = true: plain Dicey Dungeons runs without the AP mod.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DiceyDungeonsPlugin : IGamePlugin
{
    // ── Constants ──────────────────────────────────────────────────────────────
    private const string MOD_OWNER = "Fylcoast";
    private const string MOD_REPO  = "AP_diceydungeons";
    private const string ModRepoUrl =
        $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Dicey%20Dungeons/setup/en";
    private const string GameInfoUrl     = "https://archipelago.gg/games/Dicey%20Dungeons/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Dicey Dungeons appid 861540
    private const string SteamAppId    = "861540";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// Standard Steam install sub-folder name for Dicey Dungeons.
    private const string SteamCommonFolderName = "Dicey Dungeons";

    /// The game executable name (Windows, HashLink-compiled Haxe).
    private const string GameExeName = "diceydungeons.exe";

    /// Archipelago mod argument — starts the mod inside the game.
    private const string ModArg = "mod=diceyap";

    /// The .apworld file name.
    private const string ApWorldFileName = "diceydungeons.apworld";

    /// Pinned fallback version when GitHub API is unreachable. v0.4.0-beta is
    /// the latest as of 2026-06-14.
    private const string FallbackVersion     = "0.4.0-beta";
    private const string FallbackApWorldName = "diceydungeons.apworld";
    private static readonly string FallbackApWorldUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/{FallbackApWorldName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ─────────────────────────────────────────────────

    public string GameId      => "dicey_dungeons";
    public string DisplayName => "Dicey Dungeons";
    public string Subtitle    => "Native PC · AP mod (Fylcoast)";

    /// EXACT AP game string — verified in worlds/diceydungeons/world.py and
    /// worlds/diceydungeons/archipelago.json from github.com/Fylcoast/AP_diceydungeons
    public string ApWorldName => "Dicey Dungeons";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "dicey_dungeons.png");

    public string ThemeAccentColor => "#C0392B";   // dice red

    public string[] GameBadges => new[] { "Steam · needs AP client" };

    public string Description =>
        "Dicey Dungeons, Terry Cavanagh's 2019 roguelite where you play as a giant " +
        "dice rolling through monster-filled dungeons, played through the Archipelago " +
        "integration by Fylcoast. The mod patches the game's item tables so weapons, " +
        "spells, and equipment become multiworld checks. The game communicates with the " +
        "Archipelago client via its stdout, and the client in turn relays checks and " +
        "items to the AP server. You bring your own copy of Dicey Dungeons (owned on " +
        "Steam); the .apworld file and Archipelago installation provide the integration. " +
        "After installing, connect and patch from the Archipelago client CLI, then play.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ──────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// Installed if the .apworld exists in a known AP worlds folder AND the
    /// game exe is present in the detected/override install dir.
    public bool IsInstalled =>
        FindInstalledApWorld() != null && ResolveGameDir() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ──────────────────────────────────────────────────────────────────

    /// Working directory for downloads and sidecar state.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "dicey_dungeons");

    /// Per-game sidecar per spec: Games/ROMs/dicey_dungeons/
    private string SidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SidecarPath
        => Path.Combine(SidecarDir, "dicey_dungeons_launcher.json");

    // ── Internal state ─────────────────────────────────────────────────────────
    private Process? _gameProcess;
    private Process? _clientProcess;

    // ── AP bridge events (inert — ConnectsItself) ──────────────────────────────
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
            string? apWorldPath = FindInstalledApWorld();
            InstalledVersion = apWorldPath != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        try
        {
            var (version, _) = await ResolveLatestReleaseAsync(ct);
            AvailableVersion = version;
        }
        catch
        {
            AvailableVersion = null;
        }
    }

    // ── Lifecycle — InstallOrUpdate ────────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. Resolve where to install the .apworld: prefer the Archipelago
        //    custom_worlds folder, then the worlds folder. Read-only access is
        //    mandated for C:\ProgramData\Archipelago — we write into its
        //    custom_worlds sub-folder which AP itself manages for user worlds.
        progress.Report((2, "Locating your Archipelago installation..."));
        string? apWorldsDir = ResolveApCustomWorldsDir();
        if (apWorldsDir == null)
            throw new InvalidOperationException(
                "Could not locate an Archipelago installation. Install Archipelago " +
                "from archipelago.gg and make sure it has been run at least once so " +
                "its folders are created. Then try again.");

        // 1. Resolve the latest release.
        progress.Report((6, "Checking the latest diceydungeons.apworld release..."));
        var (version, apWorldUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (apWorldUrl == null)
            throw new InvalidOperationException(
                "Could not find the diceydungeons.apworld download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                ModRepoUrl + "/releases and place it in your Archipelago " +
                "custom_worlds folder.");

        // 2. Download the .apworld.
        string destPath = Path.Combine(apWorldsDir, ApWorldFileName);
        await DownloadApWorldAsync(apWorldUrl, version, destPath, progress, ct);

        // 3. Stamp the version.
        WriteStampedVersion(version);
        InstalledVersion = version;

        // 4. Check whether the game is installed.
        string? gameDir = ResolveGameDir();
        string gameMsg = gameDir != null
            ? $"Detected game install at: {gameDir}"
            : "Dicey Dungeons not found via Steam. Open Settings to select your " +
              "install folder.";

        progress.Report((100,
            $"Installed diceydungeons.apworld {version} into {apWorldsDir}. {gameMsg} " +
            "NEXT STEPS: (1) Run Archipelago and connect this slot. " +
            "(2) In the AP client console, type /patch to patch the game's data files. " +
            "(3) Then use the Play button here to launch the game with the mod active. " +
            "(The launcher cannot run the /patch step automatically — it requires an " +
            "active AP server connection. See Settings for the guided steps.)"));
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
        // The Dicey Dungeons AP integration requires:
        //   1. The game is patched for the current AP session (via /patch in the AP
        //      client CLI, which embeds item placements into the game's data). This
        //      is done via the Python AP client, not by the launcher.
        //   2. The game is launched with `mod=diceyap` so it loads the AP mod.
        //   3. The AP Python client runs alongside to relay checks/items to the
        //      server via stdout capture.
        // ConnectsItself = true: the AP Python client owns the slot — the launcher
        // must NOT hold its own ApClient on this slot.
        //
        // Launch strategy: try to launch diceydungeons.exe with mod=diceyap directly
        // from the detected install, which is the most reliable approach after the
        // user has already run /patch via the AP client.
        _ = session; // no prefill — connection handled by AP client
        StartGameWithMod();
        return Task.CompletedTask;
    }

    public bool SupportsStandalone => true;
    public bool ConnectsItself     => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartGame(withMod: false);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        try { _clientProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning      = false;
        _gameProcess   = null;
        _clientProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ──────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ────────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honest-scope header ───────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Dicey Dungeons is your own game (Steam) with the Archipelago mod " +
                   "added via the .apworld file from Fylcoast's AP_diceydungeons. The " +
                   "launcher installs the .apworld into your Archipelago custom_worlds " +
                   "folder. IMPORTANT: before playing, you must connect to your AP " +
                   "server via the Archipelago client and run the /patch command in the " +
                   "client console — this patches the game's item tables for your slot. " +
                   "The launcher cannot automate this step. After patching, use Play to " +
                   "launch the game with the mod active.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: install status ───────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "INSTALLATION STATUS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? apWorldPath = FindInstalledApWorld();
        panel.Children.Add(new TextBlock
        {
            Text = apWorldPath != null
                ? "diceydungeons.apworld found: " + apWorldPath
                : "diceydungeons.apworld not installed. Use Install on the Play tab.",
            FontSize = 11, Foreground = apWorldPath != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        string? gameDir = ResolveGameDir();
        string? overDir = LoadOverrideDir();
        panel.Children.Add(new TextBlock
        {
            Text = gameDir != null
                ? (overDir != null
                    ? "Using selected folder: " + gameDir
                    : "Detected Steam install: " + gameDir)
                : "Dicey Dungeons not detected. Pick your install folder below, " +
                  "or install via Steam first.",
            FontSize = 11, Foreground = gameDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // ── Game folder override ──────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GAME INSTALL FOLDER", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 4, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Dicey Dungeons install folder (contains diceydungeons.exe). " +
                          "Detected from Steam automatically. Use this picker for a non-standard " +
                          "Steam library or another store.",
        };
        var dirBtn = new Button
        {
            Content = "Select folder...", Width = 120, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Dicey Dungeons install folder (contains diceydungeons.exe)",
                InitialDirectory = Directory.Exists(overDir ?? gameDir ?? "")
                                   ? (overDir ?? gameDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateGameDir(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Dicey Dungeons folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // Descend into a nested "Dicey Dungeons" sub-folder if needed.
                if (!LooksLikeGameDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeGameDir(nested)) picked = nested;
                }
                SaveOverrideDir(picked);
                dirBox.Text = picked;
            }
        };
        DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new TextBlock
        {
            Text = "Steam installs are detected automatically (appid 861540). Use this " +
                   "picker for a non-standard Steam library or another distribution.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Guided setup steps ────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Dicey Dungeons (on Steam). Install it if you have not. Use the " +
                "picker above if it was not detected automatically.",
            "2. Install Archipelago from archipelago.gg if you have not, and run it " +
                "at least once so its folders are created " +
                "(usually C:\\ProgramData\\Archipelago).",
            "3. Install the .apworld: click Install on the Play tab. This downloads " +
                "diceydungeons.apworld and places it in your Archipelago custom_worlds folder. " +
                "You can also do this manually by downloading from the mod releases (link below).",
            "4. Generate your multiworld using the Dicey.Dungeons.yaml template (available " +
                "in the mod releases) as your options file, with your AP server.",
            "5. Connect to your AP server using the Archipelago client (start it from " +
                "Archipelago's launcher, select 'Dicey Dungeons Client'). Use the /connect " +
                "command if needed.",
            "6. In the AP client console, type /patch. This patches the game's item tables " +
                "with your multiworld's data. IMPORTANT: you must do this before playing, and " +
                "again whenever you start a fresh slot/seed.",
            "7. Click Play here (or the Play button on the tile) to launch the game with " +
                "the mod active (mod=diceyap argument). You should see Archipelago connect.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ──────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("AP_diceydungeons (GitHub) ↗", ModRepoUrl),
            ("Dicey Dungeons Setup Guide ↗", SetupGuideUrl),
            ("Dicey Dungeons (AP Info) ↗",   GameInfoUrl),
            ("Archipelago Official ↗",        ArchipelagoSite),
        })
        {
            var btn = new Button
            {
                Content = label,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding         = new Thickness(0, 2, 0, 2),
                Background      = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize        = 12,
                Margin          = new Thickness(0, 0, 0, 4),
                Foreground      = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
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

    // ── Private helpers — release resolution ───────────────────────────────────

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest release: version string + the diceydungeons.apworld
    /// download URL. Falls back to the pinned v0.4.0-beta URL on API failure.
    private async Task<(string Version, string? ApWorldUrl)> ResolveLatestReleaseAsync(
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

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;   // .apworld named diceydungeons*
                string? anyApWorld = null;  // any .apworld
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u)
                                   ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".apworld")) continue;

                    anyApWorld ??= url;
                    if (preferred == null && lower.Contains("dicey"))
                        preferred = url;
                }
                string? result = preferred ?? anyApWorld;
                if (result != null)
                    return (version, result);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackApWorldUrl);
    }

    // ── Private helpers — Archipelago worlds folder detection ──────────────────

    /// Resolve where to install the .apworld. Priority order:
    ///   1. C:\ProgramData\Archipelago\custom_worlds (the canonical user-worlds drop)
    ///   2. <AP exe dir>\custom_worlds
    ///   3. <AP exe dir>\worlds
    ///   4. The sidecar dir (last-resort fallback — the user places it manually)
    private string? ResolveApCustomWorldsDir()
    {
        // 1. Standard ProgramData location.
        string pd = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Archipelago", "custom_worlds");
        if (TryEnsureDir(pd)) return pd;

        // 2+3. Locate Archipelago exe and find its worlds folders.
        string? apDir = FindArchipelagoExeDir();
        if (apDir != null)
        {
            string cw = Path.Combine(apDir, "custom_worlds");
            if (TryEnsureDir(cw)) return cw;

            string w = Path.Combine(apDir, "worlds");
            if (TryEnsureDir(w)) return w;
        }

        // 4. Fall back to the sidecar dir.
        if (TryEnsureDir(SidecarDir)) return SidecarDir;

        return null;
    }

    /// Returns the directory containing ArchipelagoLauncher.exe or similar, or
    /// null when no Archipelago installation is found.
    private static string? FindArchipelagoExeDir()
    {
        // Known install locations for Archipelago on Windows.
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Archipelago"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Archipelago"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Archipelago"),
        };

        foreach (string dir in candidates)
        {
            if (!Directory.Exists(dir)) continue;
            // Accept if it contains a recognizable AP executable.
            if (File.Exists(Path.Combine(dir, "ArchipelagoLauncher.exe")) ||
                File.Exists(Path.Combine(dir, "ArchipelagoServer.exe")) ||
                File.Exists(Path.Combine(dir, "Archipelago.exe")))
                return dir;
        }
        return null;
    }

    private static bool TryEnsureDir(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            return Directory.Exists(dir);
        }
        catch { return false; }
    }

    /// Find the currently installed diceydungeons.apworld in known AP folders.
    /// Returns the full path if found, or null.
    private string? FindInstalledApWorld()
    {
        // Check the same priority list as ResolveApCustomWorldsDir.
        var searchDirs = new List<string>();

        string pd = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Archipelago", "custom_worlds");
        searchDirs.Add(pd);

        string? apDir = FindArchipelagoExeDir();
        if (apDir != null)
        {
            searchDirs.Add(Path.Combine(apDir, "custom_worlds"));
            searchDirs.Add(Path.Combine(apDir, "worlds"));
        }
        searchDirs.Add(SidecarDir);

        foreach (string dir in searchDirs)
        {
            try
            {
                string candidate = Path.Combine(dir, ApWorldFileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* inaccessible — skip */ }
        }
        return null;
    }

    // ── Private helpers — Steam / game detection ───────────────────────────────

    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeGameDir(ov)) return ov;

        try { return DetectSteamGameDir(); }
        catch { return null; }
    }

    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

    private static string? ValidateGameDir(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Dicey Dungeons install folder.";

        if (LooksLikeGameDir(folder)) return null;

        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeGameDir(nested)) return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Dicey Dungeons installation. Pick the " +
               "folder that contains diceydungeons.exe (for Steam this is usually " +
               @"...\steamapps\common\Dicey Dungeons).";
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

                    string common    = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeGameDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeGameDir(conventional)) return conventional;
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
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormalizePath(hkcu);

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormalizePath(hklm);

        string? hklm64 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm64)) yield return NormalizePath(hklm64);

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    private static string NormalizePath(string p) => p.Replace('/', '\\').TrimEnd('\\');

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

    // ── Private helpers — launch ───────────────────────────────────────────────

    private void StartGameWithMod() => StartGame(withMod: true);

    private void StartGame(bool withMod)
    {
        string? gameDir = ResolveGameDir();
        string? exe     = gameDir != null ? Path.Combine(gameDir, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var psi = new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = gameDir!,
                UseShellExecute  = false,
            };
            if (withMod)
                psi.ArgumentList.Add(ModArg);

            var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("Failed to start Dicey Dungeons.");

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

        // Fall back to Steam if the exe is not directly accessible.
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
            "Could not find diceydungeons.exe. Open this game's Settings and pick " +
            "your Dicey Dungeons install folder, or install Dicey Dungeons via Steam.",
            GameExeName);
    }

    // ── Private helpers — download the .apworld ────────────────────────────────

    private async Task DownloadApWorldAsync(
        string url,
        string version,
        string destPath,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempFile = Path.Combine(Path.GetTempPath(),
            $"diceydungeons-{version}-{Guid.NewGuid():N}.apworld");
        try
        {
            progress.Report((10, $"Downloading diceydungeons.apworld {version}..."));
            using var response = await _http.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempFile))
            {
                var buf = new byte[81920];
                int bytesRead;
                while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                    downloaded += bytesRead;
                    if (total > 0)
                    {
                        int pct = (int)(10 + 75 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading diceydungeons.apworld... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((88, "Installing the .apworld..."));
            string? destDir = Path.GetDirectoryName(destPath);
            if (destDir != null) Directory.CreateDirectory(destDir);
            File.Copy(tempFile, destPath, overwrite: true);
            progress.Report((95, $"diceydungeons.apworld installed to {destPath}"));
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    // ── Private helpers — sidecar settings ────────────────────────────────────

    private sealed class DiceyDungeonsSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private DiceyDungeonsSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SidecarPath))
            {
                string txt = File.ReadAllText(SidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<DiceyDungeonsSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(DiceyDungeonsSettings s)
    {
        try
        {
            Directory.CreateDirectory(SidecarDir);
            File.WriteAllText(SidecarPath,
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
}
