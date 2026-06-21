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

namespace LauncherV2.Plugins.RiftOfTheNecroDancer;

// ═══════════════════════════════════════════════════════════════════════════════
// RiftOfTheNecroDancerPlugin — install / launch for "Rift of the NecroDancer"
// (Brace Yourself Games) played through the RiftArchipelago mod by studkid, which
// contains the in-game Archipelago Multiworld client. This is a NATIVE
// "ConnectsItself" integration in the same family as the shipped Hollow Knight /
// TUNIC / Stardew Valley / BombRushCyberfunk plugins: the game itself speaks to
// the AP server (no emulator, no Lua bridge, no launcher-held ApClient on the
// slot).
//
// ── HONEST REALITY CHECK ──────────────────────────────────────────────────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Rift of the NecroDancer (Steam appid 1681570), and Archipelago support is
// delivered as a BepInEx 5 mod added on top. The honest integration ceiling —
// exactly like the shipped BombRushCyberfunk / Noita / RiskOfRain2 plugins — is
// "automate what is possible, guide the irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "Rift of the NecroDancer" (mod repo:
//     studkid/RiftArchipelago). GameId here = "rift_of_the_necrodancer".
//     The mod is a community BepInEx 5 plugin for a Unity game.
//
//   * THE MOD repo is studkid/RiftArchipelago (GitHub releases). The latest
//     release ships a zip containing a BepInEx/ folder tree that is extracted
//     directly into the Steam game directory root. IsInstalled is judged by
//     the presence of any *archipelago* or *rift*archipelago* DLL anywhere
//     under BepInEx\plugins\ (case-insensitive, recursive).
//
//   * CRITICAL HONESTY — THE MOD REQUIRES BepInEx 5 (Unity MONO), which is NOT
//     bundled in the mod zip. The user must have BepInEx 5 installed first.
//     BepInEx 5 is a portable zip extracted into the game root. The plugin
//     surfaces this prerequisite clearly in the guided steps. The Install button
//     stages the mod files only; BepInEx must be present first.
//
//   * CONNECTION is made IN-GAME. After BepInEx and the mod are in place and the
//     game is launched, the mod provides an in-game UI to enter the Archipelago
//     server address, port, slot name, and password. There is NO command-line
//     arg and NO config file this launcher can pre-write to seed the connection.
//     The settings panel surfaces the session's server / slot for the user to
//     copy into the in-game fields. This launcher does NOT attempt a prefill.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Rift of the NecroDancer install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, HKLM\...\WOW6432Node\Valve\Steam
//      -> InstallPath), parsing steamapps\libraryfolders.vdf for every library
//      root and locating steamapps\common\Rift of the NecroDancer via
//      appmanifest_1681570.acf. A manual install-dir OVERRIDE (settings folder
//      picker) is supported and persisted in a plugin-local sidecar JSON.
//   2. INSTALL/UPDATE (best effort) = download the mod zip from GitHub releases
//      and extract the BepInEx/ folder into the Steam game directory, so the
//      plugin DLL lands in BepInEx\plugins\. The plugin flags if BepInEx core is
//      absent. Never a fake "fully ready" one-click for the in-game parts no
//      launcher can perform.
//   3. LAUNCH = try multiple known exe names in the detected/override install;
//      if nothing is found, fall back to steam://rungameid/1681570.
//      ConnectsItself = true (the mod owns the slot). SupportsStandalone = true.
//
// ── BUILD NOTE ────────────────────────────────────────────────────────────────
// This project sets UseWindowsForms=true alongside UseWPF=true, so WPF UI types
// that also exist in WinForms can collide (CS0104). This file imports
// System.Windows.Controls and System.Windows.Media at the top, relying on the
// project's GlobalUsings.cs WPF aliases — no local re-aliases (CS1537).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class RiftOfTheNecroDancerPlugin : IGamePlugin
{
    // ── Constants — mod repo (studkid/RiftArchipelago) ────────────────────────
    private const string MOD_OWNER = "studkid";
    private const string MOD_REPO  = "RiftArchipelago";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_RELEASES_URL        = $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";

    private const string SetupGuideUrl   = $"{ModRepoUrl}#readme";
    private const string ArchipelagoSite = "https://archipelago.gg";
    private const string BepInExUrl      = "https://github.com/BepInEx/BepInEx/releases";

    // Steam — Rift of the NecroDancer appid 1681570.
    private const string SteamAppId   = "1681570";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// Standard Steam common sub-folder name for this game.
    private const string SteamCommonFolderName = "Rift of the NecroDancer";

    /// Known exe names (the exact filename is not officially documented, so we
    /// try both forms).
    private static readonly string[] KnownExeNames = new[]
    {
        "Rift of the NecroDancer.exe",
        "RiftNecroDancer.exe",
        "necrodancer.exe",
    };

    /// Pinned fallback version when the GitHub API is unreachable.
    private const string FallbackVersion = "1.0.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "rift_of_the_necrodancer";
    public string DisplayName => "Rift of the NecroDancer";
    public string Subtitle    => "Native PC · BepInEx mod";

    /// EXACT AP game string used by the studkid/RiftArchipelago world.
    public string ApWorldName => "Rift of the NecroDancer";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "rift_of_the_necrodancer.png");

    public string ThemeAccentColor => "#6A1FBF";   // electric purple (neon rhythm aesthetic)
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Rift of the NecroDancer, the rhythm roguelike sequel by Brace Yourself Games, " +
        "played through the RiftArchipelago mod by studkid — a BepInEx 5 plugin that " +
        "contains an in-game Archipelago client, so the game connects to the multiworld " +
        "itself with no emulator and no Lua bridge. You bring your own copy of Rift of " +
        "the NecroDancer (owned on Steam); BepInEx 5 and the Archipelago mod are added " +
        "on top. The launcher detects your Steam install, can stage the mod files, and " +
        "guides the rest. You connect to your Archipelago server from within the game.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── AP contract — unused-event suppression ────────────────────────────────
    // ConnectsItself = true: the mod reports checks/items/goal to the AP server
    // itself. These events exist only for interface compatibility.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = the mod DLL is present under the detected/override game
    /// install's BepInEx\plugins tree (case-insensitive, recursive scan).
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── ConnectsItself / SupportsStandalone ───────────────────────────────────
    public bool ConnectsItself     => true;
    public bool SupportsStandalone => true;

    /// AP data package checksum — not tracked by this plugin (ConnectsItself).
    public string? BuiltAgainstDataPackageChecksum => null;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Working directory for this plugin's downloads and bookkeeping. The actual
    /// mod is extracted INTO the game install's BepInEx\plugins folder.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "RiftOfTheNecroDancer");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "rift_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────
    private Process? _gameProcess;

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
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(MOD_OWNER, MOD_REPO, ct));
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
        // 0. Locate the game install to drop mod files into.
        progress.Report((2, "Locating your Rift of the NecroDancer installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Rift of the NecroDancer installation. Open this " +
                "game's Settings and pick your install folder (the one containing " +
                "the game exe), or install the game via Steam first. The Archipelago " +
                "mod is added on top of your own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback when offline).
        progress.Report((6, "Checking the latest RiftArchipelago release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the RiftArchipelago mod download on GitHub. Check " +
                "your internet connection, or download the zip manually from " +
                ModRepoUrl + "/releases — see Settings for the guided steps.");

        // 2. Download + extract the mod zip. The zip contains a BepInEx/ folder
        //    tree; we extract it directly into the game root so the plugin DLL
        //    lands in BepInEx\plugins\. HONEST: BepInEx itself is NOT bundled;
        //    the user must have BepInEx 5 already installed (see guided steps).
        await DownloadAndExtractModAsync(zipUrl, version, gameDir, progress, ct);

        // 3. Stamp version (informational — IsInstalled is judged by DLL presence).
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepInExPresent = Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));
        progress.Report((100,
            $"Installed RiftArchipelago {version} into your Rift of the NecroDancer " +
            "BepInEx\\plugins folder. " +
            (bepInExPresent
                ? "BepInEx looks present. "
                : "IMPORTANT: BepInEx 5 (Unity MONO) is NOT included in this download " +
                  "and must be installed first. Download BepInEx 5 from the link in " +
                  "Settings, extract its contents into your game root, and run the game " +
                  "once so BepInEx generates its config. Then re-run Install. ") +
            "To play: launch the game, connect to your server from the in-game " +
            "Archipelago UI, and start your run. Open Settings for the guided steps."));
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
        // HONEST: connection is entered in-game via the mod's UI. No command-line
        // or config prefill exists. ConnectsItself = true: we must NOT hold our
        // own ApClient on this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartGame();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself) ───────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var accent  = new SolidColorBrush(Color.FromRgb(0x6A, 0x1F, 0xBF));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();
        string? modDll      = FindInstalledModDll();
        bool    bepInExOk   = gameDir != null && Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Rift of the NecroDancer is your own game (Steam) with the " +
                   "RiftArchipelago BepInEx mod added on top. The launcher detects " +
                   "your Steam install and can stage the mod files. BepInEx 5 (Unity " +
                   "MONO) must be installed separately first — see the guided steps " +
                   "below. Connection to your Archipelago server is done in-game via " +
                   "the mod's UI; this launcher cannot pre-fill it.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: Game install ─────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "RIFT OF THE NECRODANCER INSTALL", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 0, 0, 8),
        });

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Game not detected. Pick your install folder below, or install " +
              "Rift of the NecroDancer via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // BepInEx status
        panel.Children.Add(new TextBlock
        {
            Text = bepInExOk
                    ? "BepInEx found (BepInEx\\core present)."
                    : "BepInEx not found yet. Install BepInEx 5 (Unity MONO) into your " +
                      "game root first (see guided steps and link below).",
            FontSize = 11, Foreground = bepInExOk ? success : warn,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        // Mod DLL status
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                    ? "RiftArchipelago mod found: " + modDll
                    : "RiftArchipelago mod not found in BepInEx\\plugins yet. " +
                      "Use the Install button on the Play tab, or install it by hand " +
                      "from the mod releases (link below).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Rift of the NecroDancer install folder (contains the " +
                          "game exe). Detected from Steam; set it here to override.",
        };
        var dirBtn = new Button
        {
            Content     = "Select folder...", Width = 120,
            Padding     = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Rift of the NecroDancer install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                                   ? (overrideDir ?? gameDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() != true) return;

            string picked = dlg.FolderName;
            string? bad   = ValidateInstallDir(picked);
            if (bad != null)
            {
                System.Windows.MessageBox.Show(bad, "Not a Rift of the NecroDancer folder",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // Descend into the game sub-folder if the user picked a "common" parent.
            if (!LooksLikeGameDir(picked))
            {
                string nested = Path.Combine(picked, SteamCommonFolderName);
                if (LooksLikeGameDir(nested)) picked = nested;
            }
            SaveOverrideDir(picked);
            dirBox.Text = picked;
        };
        DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new TextBlock
        {
            Text = "Steam installs are detected automatically (appid 1681570). " +
                   "Use this picker for a non-standard Steam library.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connecting ───────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Launch the game with BepInEx and the mod installed. In the game's " +
                   "Archipelago UI, enter your server address, port, slot name, and " +
                   "password (if any). This launcher cannot pre-fill the connection " +
                   "— copy your session details from the Play tab into the in-game " +
                   "fields manually.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Rift of the NecroDancer (Steam). Install it if you have not. " +
                "Use the picker above if it was not detected automatically.",
            "2. Install BepInEx 5 (Unity MONO build — NOT the IL2CPP or pre-release " +
                "BepInEx 6 builds). Download the BepInEx 5 zip from the link below, " +
                "extract its contents (the BepInEx/ folder + doorstop files) directly " +
                "into your Rift of the NecroDancer install root.",
            "3. Launch the game once so BepInEx generates its config folders, then " +
                "close it. You should see a BepInEx\\core\\ folder in your install.",
            "4. Install the RiftArchipelago mod: use the Install button on the Play " +
                "tab (downloads and extracts the BepInEx/ folder from the GitHub " +
                "release into your game root), or download the zip manually from the " +
                "mod releases link below and extract it into your game root.",
            "5. Launch the game. The mod should load via BepInEx. Use the in-game " +
                "Archipelago UI to enter your server, port, slot name, and password, " +
                "then start your run.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("RiftArchipelago (GitHub) ↗",          ModRepoUrl),
            ("RiftArchipelago Releases ↗",          ModRepoUrl + "/releases"),
            ("BepInEx 5 Releases (Unity MONO) ↗",  BepInExUrl),
            ("Archipelago Official ↗",              ArchipelagoSite),
        })
        {
            var btn = new Button
            {
                Content         = label,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding         = new Thickness(0, 2, 0, 2),
                Background      = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                FontSize        = 12,
                Margin          = new Thickness(0, 0, 0, 4),
                Foreground      = new SolidColorBrush(Color.FromRgb(0xAA, 0x60, 0xFF)),
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

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
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

    // ── Private helpers — validation ──────────────────────────────────────────

    /// Return a user-visible error if `folder` is not a valid game install, else null.
    public string? ValidateInstallDir(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Rift of the NecroDancer " +
                   "install folder.";
        if (LooksLikeGameDir(folder)) return null;
        // Be forgiving: maybe the user picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeGameDir(nested)) return null;
        }
        catch { /* ignore */ }
        return "That does not look like a Rift of the NecroDancer installation. " +
               "Pick the folder that contains the game exe (e.g. " +
               @"...\steamapps\common\Rift of the NecroDancer).";
    }

    /// A folder is a valid game install if it contains one of the known exe names,
    /// or a BepInEx sub-folder (robust to exe renaming).
    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            foreach (string exe in KnownExeNames)
                if (File.Exists(Path.Combine(dir, exe))) return true;
            // Secondary: a BepInEx folder is a strong signal for an installed game dir.
            if (Directory.Exists(Path.Combine(dir, "BepInEx"))) return true;
            return false;
        }
        catch { return false; }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// "v1.2.3" → "1.2.3"; also trims whitespace.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release from the GitHub API: version + the first
    /// .zip asset's download URL. Falls back to constructing a pinned URL when
    /// the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = NormalizeTag(
                root.TryGetProperty("tag_name", out var t) ? t.GetString() : null);

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? preferredZip = null;
                string? anyZip       = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferredZip == null &&
                        (lower.Contains("archipelago") || lower.Contains("rift")))
                        preferredZip = url;
                }
                string? zip = preferredZip ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        // Construct a pinned fallback URL from the repo's common tag-naming convention.
        string fallbackUrl = $"{ModRepoUrl}/releases/download/v{FallbackVersion}/RiftArchipelago.zip";
        return (FallbackVersion, fallbackUrl);
    }

    // ── Private helpers — Steam / game detection ──────────────────────────────

    /// The game dir to use: the override (if set and valid) wins, else Steam detection.
    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeGameDir(ov))
            return ov;

        try { return DetectSteamGameDir(); }
        catch { return null; }
    }

    /// Detect the Steam install via the registry + libraryfolders.vdf + appmanifest.
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

                    string common  = Path.Combine(steamapps, "common");
                    string? instDir = ReadAcfInstallDir(manifest);
                    if (instDir != null)
                    {
                        string candidate = Path.Combine(common, instDir);
                        if (LooksLikeGameDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeGameDir(conventional)) return conventional;
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
            int open  = text.IndexOf('"', i);
            if (open  < 0) yield break;
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
            int open  = text.IndexOf('"', i);
            if (open  < 0) return null;
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

    // ── Private helpers — mod DLL detection ──────────────────────────────────

    /// Find the RiftArchipelago mod DLL under the detected/override game install's
    /// BepInEx\plugins tree (recursive, case-insensitive). Accepts any *.dll whose
    /// name mentions "rift" + "archipelago", or just "archipelago" in the name, or
    /// a plugins sub-folder named like "archipelago" that holds a DLL. Returns the
    /// matched path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? game = ResolveGameDir();
            if (game == null) return null;
            string pluginsDir = Path.Combine(game, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            // 1. A DLL whose name mentions both "rift" and "archipelago", or just
            //    "archipelago" (canonical install).
            foreach (string dll in Directory.EnumerateFiles(
                pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string lower = Path.GetFileNameWithoutExtension(dll).ToLowerInvariant();
                if (lower.Contains("archipelago"))
                    return dll;
            }

            // 2. A plugins sub-folder named like "archipelago" that holds a DLL.
            foreach (string sub in Directory.EnumerateDirectories(
                pluginsDir, "*", SearchOption.AllDirectories))
            {
                string folderName = Path.GetFileName(sub).ToLowerInvariant();
                if (!folderName.Contains("archipelago")) continue;
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

    private void StartGame()
    {
        string? game = ResolveGameDir();

        if (game != null)
        {
            foreach (string exeName in KnownExeNames)
            {
                string exePath = Path.Combine(game, exeName);
                if (!File.Exists(exePath)) continue;

                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName         = exePath,
                    WorkingDirectory = game,
                    UseShellExecute  = true,
                }) ?? throw new InvalidOperationException(
                    "Failed to start Rift of the NecroDancer.");

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
        }

        // Fall back to the steam:// URI if a game dir was not found or exe is missing.
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
            "Could not find the Rift of the NecroDancer executable. Open this game's " +
            "Settings and pick your install folder, or install the game via Steam.",
            SteamCommonFolderName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod zip from GitHub and extract its BepInEx/ folder tree into
    /// the game root. This stages the plugin DLL into BepInEx\plugins\. BepInEx
    /// itself must already be present (the user installs it separately — see the
    /// guided steps).
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string gameDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"rift-archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"rift-archipelago-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading RiftArchipelago {version}..."));
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
                        progress.Report((pct,
                            $"Downloading RiftArchipelago... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting mod package..."));
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            progress.Report((85, "Installing mod into the game folder..."));

            // The zip ships a BepInEx/ folder tree. Walk the extracted contents:
            // if there is a BepInEx/ folder inside, copy it into the game root so
            // BepInEx\plugins\<mod>.dll is where BepInEx will load it.
            // If no BepInEx/ folder is found, just copy everything to the game root
            // (best-effort for an unexpected zip layout).
            string bepInExSrc = Path.Combine(tempDir, "BepInEx");
            if (Directory.Exists(bepInExSrc))
            {
                string bepInExDst = Path.Combine(gameDir, "BepInEx");
                CopyDirectoryContents(bepInExSrc, bepInExDst);
            }
            else
            {
                // Fallback: look one level deep for a BepInEx sub-folder.
                bool found = false;
                foreach (string sub in Directory.EnumerateDirectories(tempDir))
                {
                    string nested = Path.Combine(sub, "BepInEx");
                    if (!Directory.Exists(nested)) continue;
                    CopyDirectoryContents(nested, Path.Combine(gameDir, "BepInEx"));
                    found = true;
                    break;
                }
                if (!found)
                {
                    // Last resort: copy the whole extracted tree into the game root.
                    CopyDirectoryContents(tempDir, gameDir);
                }
            }

            progress.Report((95, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))       File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempDir))  Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void CopyDirectoryContents(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(
            sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel  = Path.GetRelativePath(sourceDir, file);
            string dst  = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class RiftSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private RiftSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<RiftSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(RiftSettings s)
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
    private void SaveOverrideDir(string p) { var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s); }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    private void WriteStampedVersion(string v) { var s = LoadSettings(); s.ModVersion = v; SaveSettings(s); }
}
