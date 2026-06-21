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
// GlobalUsings.cs already aliases the colliding short names project-wide — so this
// file must NOT add any file-level `using X = System.Windows...;` alias (CS1537).

namespace LauncherV2.Plugins.RE3R;

// ═══════════════════════════════════════════════════════════════════════════════
// RE3RPlugin — install / launch for "Resident Evil 3 Remake" (Capcom, 2020) played
// through the RE3R_AP_World mod by TheRealSolidusSnake, which contains the in-game
// Archipelago client. This is a NATIVE "ConnectsItself" integration: the game
// itself speaks to the AP server (no emulator, no Lua bridge, no launcher-held
// ApClient on the slot).
//
// Resident Evil 3 Remake (RE3R) is Capcom's 2020 action-paced reimagining of the
// 1999 classic, following S.T.A.R.S. member Jill Valentine as she battles through
// Raccoon City while being hunted by the terrifying Nemesis bioweapon. The
// Archipelago world by TheRealSolidusSnake shuffles key items, weapons, and
// progression unlocks across the compact-but-intense city areas, turning each
// location into a multiworld check.
//
// ── HONEST REALITY CHECK ──────────────────────────────────────────────────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Resident Evil 3 Remake (Steam appid 952060 — verified against the Steam
// store page), and Archipelago support is delivered as a mod added on top.
// The mod repo is TheRealSolidusSnake/RE3R_AP_World (community AP world).
//
//   * THE AP WORLD game string "Resident Evil 3 Remake" is UNVERIFIED at time
//     of writing — confirm against worlds/__init__.py in the mod repo before
//     shipping to AP server. GameId = "re3r".
//
//   * CONNECTION is made IN-GAME through the mod's connection menu, following
//     the repository's setup instructions. The launcher cannot pre-write
//     connection details; the settings panel surfaces the session's host / port
//     / slot for the user to type in.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam RE3R install via the Windows registry, parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\RESIDENT EVIL 3  BIOHAZARD RE3 via appmanifest_952060.acf.
//      A manual install-dir OVERRIDE (settings folder picker) is also supported
//      and persisted in this plugin's OWN sidecar
//      (Games/ROMs/re3r/re3r_launcher.json).
//   2. INSTALL/UPDATE (best effort) = download the latest release from
//      TheRealSolidusSnake/RE3R_AP_World/releases/latest and extract it into the
//      game directory, following the repository's setup instructions.
//   3. LAUNCH = run re3.exe from the detected/override install; if the exe cannot
//      be found but Steam is present, fall back to steam://rungameid/952060.
//      ConnectsItself = true (the mod owns the slot — the launcher must NOT hold
//      its own ApClient on it). SupportsStandalone = true.
//
// ── DEFENSIVE ("verify at build time") ────────────────────────────────────────
//   * Steam library parsing is defensive: a hand-written tolerant VDF scan that
//     pulls quoted "path" values; any failure degrades to "RE3R not found" rather
//     than throwing.
//   * "Installed" is judged by the presence of re3.exe in the detected/override
//     directory. If no RE3R install is detected, the tile simply reads "not
//     installed".
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class RE3RPlugin : IGamePlugin
{
    // ── Constants — the RE3R_AP_World mod (community repo) ────────────────────
    private const string MOD_OWNER = "TheRealSolidusSnake";
    private const string MOD_REPO  = "RE3R_AP_World";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl   = "https://archipelago.gg/games/Resident%20Evil%203%20Remake/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — RE3R appid 952060 (verified against the Steam store page).
    private const string Re3rAppId = "952060";
    private static readonly string SteamRunUrl = $"steam://rungameid/{Re3rAppId}";

    /// The standard Steam install sub-folder name for RE3R.
    private const string SteamCommonFolderName = "RESIDENT EVIL 3  BIOHAZARD RE3";

    /// The base-game executable name.
    private const string Re3rExeName = "re3.exe";

    /// Pinned fallback version for when the GitHub API is unreachable.
    private const string FallbackVersion = "0.3.0";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackVersion}/RE3R_AP_World.zip";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "re3r";
    public string DisplayName => "Resident Evil 3 Remake";
    public string Subtitle    => "PC · Archipelago mod";

    /// AP world game string — UNVERIFIED; confirm against worlds/__init__.py in
    /// TheRealSolidusSnake/RE3R_AP_World before shipping to AP server.
    public string ApWorldName => "Resident Evil 3 Remake";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "re3r.png");

    public string ThemeAccentColor => "#1A237E";   // Nemesis dark indigo-blue
    public string[] GameBadges     => new[] { "Requires RE3 Remake on Steam" };

    public string Description =>
        "Resident Evil 3 Remake (2020) is Capcom's action-paced reimagining of the " +
        "1999 classic, following S.T.A.R.S. member Jill Valentine as she battles " +
        "through Raccoon City while being hunted by the terrifying Nemesis bioweapon. " +
        "The Archipelago world by TheRealSolidusSnake shuffles key items, weapons, and " +
        "progression unlocks — forcing you to collect checks from across the multiworld " +
        "to assemble the equipment needed to survive and escape. Requires: your own " +
        "legally-owned copy of Resident Evil 3 Remake on Steam. Install the AP mod " +
        "following the repository's setup instructions, then connect to your Archipelago " +
        "server through the in-game connection menu.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means re3.exe is present in the detected/override game dir.
    public bool IsInstalled
    {
        get
        {
            string? dir = ResolveRe3rDir();
            if (dir == null) return false;
            return File.Exists(Path.Combine(dir, Re3rExeName));
        }
    }

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and working files. The actual mod is
    /// extracted INTO the RE3R install directory. Exposed as GameDirectory for the
    /// IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "RE3R");

    /// This plugin's OWN settings sidecar. Lives under Games/ROMs/re3r/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "re3r_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The RE3R_AP_World mod reports checks/items/goal to the AP server itself —
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
        // 0. We need a RE3R install to extract the mod into.
        progress.Report((2, "Locating your Resident Evil 3 Remake installation..."));
        string? re3rDir = ResolveRe3rDir();
        if (re3rDir == null)
            throw new InvalidOperationException(
                "Could not find a Resident Evil 3 Remake installation. Open this game's " +
                "Settings and pick your RE3R folder (the one containing re3.exe), or " +
                "install Resident Evil 3 Remake via Steam first. The Archipelago mod is " +
                "added on top of your own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback).
        progress.Report((6, "Checking the latest RE3R_AP_World release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the RE3R_AP_World mod download on GitHub. " +
                "Check your internet connection, or install the mod manually by " +
                "following the setup instructions at " + ModRepoUrl + ".");

        // 2. Download + extract the mod zip into the RE3R game directory.
        await DownloadAndExtractModAsync(zipUrl, version, re3rDir, progress, ct);

        // 3. Stamp the version in the sidecar so the tile can show it.
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"RE3R_AP_World {version} extracted into your Resident Evil 3 Remake " +
            "folder. Follow the repository's setup instructions at " + ModRepoUrl +
            " to complete the mod installation. To play: launch the game, then " +
            "connect to your Archipelago server through the in-game connection menu. " +
            "This launcher cannot pre-fill the connection — it is entered in-game."));
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
        // HONEST: the AP server connection for RE3R is entered through the in-game
        // connection menu provided by the mod. There is no documented command-line
        // / config prefill this launcher can apply. Launching from this tile just
        // starts the game; the user connects in-game with the session credentials
        // (the settings panel surfaces those values to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartRe3r();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) RE3R runs perfectly well without the mod.
    public bool SupportsStandalone => true;

    /// The RE3R_AP_World mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    /// Checks are not currently implemented in this launcher integration.
    public bool ChecksImplemented => false;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartRe3r();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No plaintext AP password is written to disk by this plugin (the
        // connection is entered in-game), so there is nothing to scrub.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The RE3R_AP_World mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid RE3R folder contains re3.exe.
    /// Return null when acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Resident Evil 3 Remake install folder.";

        if (LooksLikeRe3rDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeRe3rDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Resident Evil 3 Remake installation. Pick the " +
               "folder that contains re3.exe (for Steam this is usually " +
               @"...\steamapps\common\RESIDENT EVIL 3  BIOHAZARD RE3).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Unverified-offline honesty header ─────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Resident Evil 3 Remake is your own game (Steam) with the " +
                   "RE3R_AP_World mod added on top. The launcher detects your Steam " +
                   "install and can extract the mod into your game folder. Follow the " +
                   "repository's setup instructions to complete the installation. You " +
                   "connect to your Archipelago server through the in-game connection " +
                   "menu. The AP world game string is unverified — confirm it against " +
                   "worlds/__init__.py before generating. These external steps are not " +
                   "verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "RESIDENT EVIL 3 REMAKE INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? re3rDir     = ResolveRe3rDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = re3rDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + re3rDir
                : "Detected Steam install: " + re3rDir)
            : "Resident Evil 3 Remake not detected. Pick your install folder below, " +
              "or install the game via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = re3rDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // exe status line
        bool exeFound = re3rDir != null && File.Exists(Path.Combine(re3rDir, Re3rExeName));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = exeFound
                    ? "re3.exe found: " + Path.Combine(re3rDir!, Re3rExeName)
                    : "re3.exe not found in the detected or selected folder. " +
                      "Use the folder picker below to point to your install.",
            FontSize = 11, Foreground = exeFound ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? re3rDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Resident Evil 3 Remake install folder (the one containing " +
                          "re3.exe). Detected from Steam automatically; set it here to " +
                          "override (non-standard Steam library).",
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
                Title            = "Select your Resident Evil 3 Remake install folder (contains re3.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? re3rDir ?? "")
                                   ? (overrideDir ?? re3rDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Resident Evil 3 Remake folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeRe3rDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeRe3rDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 952060). Use this " +
                   "picker for a non-standard Steam library.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (this session) ────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Launch Resident Evil 3 Remake and connect to your Archipelago server " +
                   "through the in-game connection menu provided by the mod. Enter the " +
                   "Server Address, Port, your slot Name, and the Password if the server " +
                   "has one. Follow the repository's setup instructions for details. " +
                   "This launcher does not pre-fill the connection.",
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
            "1. Own Resident Evil 3 Remake on Steam (appid 952060). Install it if you " +
                "have not. Use the folder picker above if it was not detected.",
            "2. Use the Install button on the Play tab to download the latest RE3R_AP_World " +
                "release and extract it into your game folder.",
            "3. Complete the mod setup by following the instructions in the mod repository " +
                "(link below). Some steps may require manual configuration.",
            "4. Launch Resident Evil 3 Remake and connect to your Archipelago server " +
                "through the in-game connection menu. This launcher cannot pre-fill the " +
                "connection — it is entered in-game.",
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
            ("RE3R_AP_World (GitHub) ↗",                  ModRepoUrl),
            ("RE3R_AP_World Releases ↗",                  ModRepoUrl + "/releases"),
            ("Resident Evil 3 Remake Guide (AP) ↗",       SetupGuideUrl),
            ("Archipelago Official ↗",                    ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0), FontSize = 12,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => { try { Process.Start(
                new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
            panel.Children.Add(btn);
        }
        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Mod releases are the AP-relevant news for this game.
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_URL, ct);
            using var doc  = JsonDocument.Parse(json);
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

    /// "v0.3.0" → "0.3.0" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the mod zip download URL.
    /// Prefers the first .zip asset; falls back to the pinned 0.3.0 direct URL
    /// when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(CancellationToken ct)
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
                string? anyZip = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    if (!name.ToLowerInvariant().EndsWith(".zip")) continue;
                    anyZip ??= url;
                }
                if (anyZip != null)
                    return (version, anyZip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — Steam / RE3R detection ──────────────────────────────

    /// The RE3R install dir to use: the override (if set and valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolveRe3rDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeRe3rDir(ov))
            return ov;

        try { return DetectSteamRe3rDir(); }
        catch { return null; }
    }

    /// A folder "looks like" RE3R if it contains re3.exe.
    private static bool LooksLikeRe3rDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, Re3rExeName));
        }
        catch { return false; }
    }

    /// Detect the Steam RE3R install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_952060.acf exists → steamapps\common\RESIDENT EVIL 3  BIOHAZARD RE3.
    private static string? DetectSteamRe3rDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{Re3rAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    // Prefer the installdir named in the manifest; fall back to the
                    // conventional "RESIDENT EVIL 3  BIOHAZARD RE3" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeRe3rDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeRe3rDir(conventional)) return conventional;
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

        string? hklm64 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm64)) yield return NormalizeSteamPath(hklm64);

        // Last-ditch conventional location.
        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    /// Steam stores its SteamPath with forward slashes; normalize for Path APIs.
    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    /// All Steam library roots: the Steam root itself plus every "path" entry in
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

    /// Pull every  "path"   "<value>"  pair out of a libraryfolders.vdf body.
    /// Handles the Steam-VDF escaping of backslashes (\\ → \).
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

    /// Read the "installdir" value from an appmanifest_*.acf (same quoted-pair
    /// format as VDF). Returns null if absent.
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

    /// Safe registry string read; null on any failure (key/value missing, etc.).
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

    /// Start RE3R: prefer the exe in the detected/override install; if that cannot
    /// be found but Steam is present, fall back to the steam:// URL.
    private void StartRe3r()
    {
        string? re3rDir = ResolveRe3rDir();
        string? exe     = re3rDir != null ? Path.Combine(re3rDir, Re3rExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = re3rDir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Resident Evil 3 Remake.");

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

        // Fall back to Steam if we at least know Steam is installed.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process, so we won't track exit
                return;
            }
            catch { /* fall through to the error below */ }
        }

        throw new FileNotFoundException(
            "Could not find re3.exe. Open this game's Settings and pick your " +
            "Resident Evil 3 Remake install folder, or install the game via Steam.",
            Re3rExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod zip and extract it into the RE3R game directory.
    /// Existing files are overwritten but siblings are preserved.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string gameDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"re3r-ap-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"re3r-ap-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading RE3R_AP_World {version}..."));
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
                        int pct = (int)(10 + 60 * downloaded / total);
                        progress.Report((pct, $"Downloading RE3R_AP_World... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Extracting the mod into your game folder..."));
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // If everything is wrapped in a single sub-folder, merge from inside it.
            string mergeRoot = tempExtract;
            {
                string[] subdirs = Directory.GetDirectories(mergeRoot);
                string[] files   = Directory.GetFiles(mergeRoot);
                if (subdirs.Length == 1 && files.Length == 0)
                    mergeRoot = subdirs[0];
            }

            // Merge the extracted tree INTO the existing game folder.
            MergeDirectory(mergeRoot, gameDir);

            progress.Report((90, "Mod files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Recursively copy a directory tree INTO an existing destination, overwriting
    /// individual files but preserving any sibling files already there.
    private static void MergeDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            try { File.Copy(file, dst, overwrite: true); }
            catch { /* a locked file (game open?) — skip; user can retry with game closed */ }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the install-dir override +
    // an informational version stamp) in its OWN JSON file so it stays a single
    // self-contained source file and does not modify Core/SettingsStore.
    // BOM-less UTF-8, read-modify-write.

    private sealed class Re3rSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private Re3rSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Re3rSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(Re3rSettings s)
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
    private void SaveOverrideDir(string p) { var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s); }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    private void WriteStampedVersion(string v) { var s = LoadSettings(); s.ModVersion = v; SaveSettings(s); }
}
