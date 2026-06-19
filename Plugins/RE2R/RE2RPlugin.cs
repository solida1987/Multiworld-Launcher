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

namespace LauncherV2.Plugins.RE2R;

// ═══════════════════════════════════════════════════════════════════════════════
// RE2RPlugin — install / launch for "Resident Evil 2 Remake" (Capcom, 2019) played
// through the RE2R_AP_World mod by FuzzyGamesOn, which contains the in-game
// Archipelago client. This is a NATIVE "ConnectsItself" integration: the game
// itself speaks to the AP server (no emulator, no Lua bridge, no
// launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK ─────────────────────────────────────────────────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Resident Evil 2 Remake (Steam appid 883710), and Archipelago support is
// delivered as a mod added on top via FuzzyGamesOn's RE2R_AP_World. The honest
// integration ceiling — exactly like the shipped Blasphemous / A Short Hike /
// Hollow Knight plugins — is "automate what is possible, guide the irreducible
// parts." The verified/known facts:
//
//   * THE AP WORLD game string is "Resident Evil 2 Remake" (UNVERIFIED — should
//     be cross-checked against worlds/__init__.py in FuzzyGamesOn/RE2R_AP_World;
//     the launcher uses this string but it may need correction against the real
//     game = "..." line in the apworld source).
//
//   * THE MOD repo is FuzzyGamesOn/RE2R_AP_World (community AP world on GitHub).
//     The AP world randomizes key items, treasure, weapons, and progression checks
//     across Raccoon City Police Department and surrounding areas.
//
//   * CONNECTION is made IN-GAME via the mod's own AP connection interface. There
//     is NO command-line arg and NO config file this launcher can pre-write for
//     the connection. Per an honest stance (same as A Short Hike / Blasphemous),
//     this plugin does NOT write any file or fake a connection prefill — the
//     settings panel surfaces the session's host / port / slot for the user to
//     type into the in-game menu.
//
//   * PINNED FALLBACK version "0.4.0" is UNVERIFIED — it is a reasonable guess
//     at the mod's version history and should be confirmed against the actual
//     GitHub releases list.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam RE2R install via the Windows registry (HKCU SteamPath +
//      HKLM InstallPath), parsing steamapps\libraryfolders.vdf for every library
//      root and locating steamapps\common\RESIDENT EVIL 2  BIOHAZARD RE2 via
//      appmanifest_883710.acf. A manual install-dir OVERRIDE (settings folder
//      picker) is also supported and takes precedence; it is validated (must
//      contain re2.exe) and persisted in this plugin's OWN sidecar
//      (Games/ROMs/re2r/re2r_launcher.json) — Core/SettingsStore is NOT modified.
//   2. INSTALL/UPDATE = download the latest release from
//      FuzzyGamesOn/RE2R_AP_World/releases/latest and extract it to the game dir.
//      Presents clear guided steps + links so the user can complete any remaining
//      setup the installer does not cover.
//   3. LAUNCH = run re2.exe from the detected/override install; if the exe cannot
//      be found but Steam is present, fall back to steam://rungameid/883710.
//      ConnectsItself = true (the mod owns the slot — the launcher must NOT hold
//      its own ApClient on it). SupportsStandalone = true (the base game runs
//      fine without AP). No connection prefill (entered in-game), stated honestly.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class RE2RPlugin : IGamePlugin
{
    // ── Constants — the RE2R_AP_World mod (community repo: FuzzyGamesOn/RE2R_AP_World) ─
    private const string MOD_OWNER = "FuzzyGamesOn";
    private const string MOD_REPO  = "RE2R_AP_World";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl   = "https://archipelago.gg/games/Resident%20Evil%202%20Remake/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — RE2R appid 883710 (verified: standard Steam store fact).
    private const string Re2rAppId = "883710";
    private static readonly string SteamRunUrl = $"steam://rungameid/{Re2rAppId}";

    /// The standard Steam install sub-folder name for RE2R (two spaces between
    /// "RESIDENT EVIL 2" and "BIOHAZARD RE2" is the real Steam folder name).
    private const string SteamCommonFolderName = "RESIDENT EVIL 2  BIOHAZARD RE2";

    /// The base-game executable name.
    private const string Re2rExeName = "re2.exe";

    /// Pinned fallback version for the mod when the GitHub API is unreachable.
    /// "0.4.0" is UNVERIFIED — confirm against the real GitHub releases list.
    private const string FallbackVersion = "0.4.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "re2r";
    public string DisplayName => "Resident Evil 2 Remake";
    public string Subtitle    => "PC · Archipelago mod";

    /// UNVERIFIED AP game string — should be confirmed against worlds/__init__.py
    /// in FuzzyGamesOn/RE2R_AP_World (the `game = "..."` field in the World class).
    public string ApWorldName => "Resident Evil 2 Remake";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "re2r.png");

    public string ThemeAccentColor => "#8B0000";   // dark Raccoon City red
    public string[] GameBadges     => new[] { "Requires RE2 Remake on Steam" };

    public string Description =>
        "Resident Evil 2 Remake (2019) is Capcom's reimagining of the 1998 survival-horror " +
        "classic, following rookie police officer Leon S. Kennedy and college student Claire " +
        "Redfield as they fight through zombie-infested Raccoon City. The Archipelago " +
        "randomizer by FuzzyGamesOn shuffles key items, weapons, treasures, and progression " +
        "unlocks across the multiworld, turning every chest and corpse into a potential check " +
        "from another game. Surviving the police station means hunting items that may have " +
        "been sent to worlds you've never played. Requires: your own legally-owned copy of " +
        "Resident Evil 2 Remake on Steam. Install the AP mod according to the repository's " +
        "setup guide, then connect via the in-game AP connection interface when starting a game.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means re2.exe exists in the detected/override install dir AND
    /// a mod indicator file is present (any .dll or manifest from the AP mod).
    /// Falls back to just re2.exe presence when no mod file can be located.
    public bool IsInstalled => IsRe2rWithModInstalled();

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and working files. The actual mod is
    /// extracted INTO the RE2R install dir, not here. Exposed as GameDirectory
    /// for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "RE2R");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file). Per the brief, lives under
    /// Games/ROMs/re2r/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "re2r_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The RE2R_AP_World mod reports checks/items/goal to the AP server itself —
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
            InstalledVersion = IsRe2rWithModInstalled()
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
        // 0. We need a RE2R install to drop the mod into.
        progress.Report((2, "Locating your Resident Evil 2 Remake installation..."));
        string? re2rDir = ResolveRe2rDir();
        if (re2rDir == null)
            throw new InvalidOperationException(
                "Could not find a Resident Evil 2 Remake installation. Open this game's " +
                "Settings and pick your RE2R folder (the one containing re2.exe), or " +
                "install Resident Evil 2 Remake via Steam first. The Archipelago mod is " +
                "added on top of your own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback).
        progress.Report((6, "Checking the latest RE2R_AP_World release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the RE2R_AP_World mod download on GitHub. " +
                "Check your internet connection, or install the mod manually from " +
                ModRepoUrl + " — see Settings for the guided steps.");

        // 2. Download + extract the mod zip INTO the RE2R game directory.
        await DownloadAndExtractModAsync(zipUrl, version, re2rDir, progress, ct);

        // 3. Stamp the version next to our sidecar so the tile can show it.
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool modOk = IsRe2rWithModInstalled();
        progress.Report((100,
            $"Installed RE2R_AP_World {version} into your Resident Evil 2 Remake folder" +
            (modOk ? "." : " (verify the files landed).") +
            " To play: launch Resident Evil 2 Remake and connect to your Archipelago " +
            "server via the in-game AP connection interface when starting a game. " +
            "(This launcher cannot pre-fill the connection — it is entered in-game.) " +
            "Refer to the mod's setup guide in the repository for full instructions."));
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
        // HONEST: the AP server connection for RE2R is entered via the IN-GAME
        // AP connection interface. There is no documented command-line / config
        // prefill this launcher can apply. So launching from this tile just starts
        // the game; the user connects in-game with the session credentials (the
        // settings panel surfaces those values to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartRe2r();
        return Task.CompletedTask;
    }

    /// Resident Evil 2 Remake runs perfectly well as a standalone game.
    public bool SupportsStandalone => true;

    /// The RE2R_AP_World mod owns the slot connection.
    public bool ConnectsItself => true;

    /// ChecksImplemented = false — the launcher does not track individual
    /// location checks for RE2R; the mod handles them directly with the AP server.
    public bool ChecksImplemented => false;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartRe2r();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No plaintext AP password is ever written to disk by this plugin (the
        // connection is entered in-game), so there is nothing to scrub.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The RE2R_AP_World mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid RE2R folder contains re2.exe.
    /// Return null when acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Resident Evil 2 Remake install folder.";

        if (LooksLikeRe2rDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeRe2rDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Resident Evil 2 Remake installation. Pick the " +
               "folder that contains re2.exe (for Steam this is usually " +
               @"...\steamapps\common\RESIDENT EVIL 2  BIOHAZARD RE2).";
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
            Text = "Resident Evil 2 Remake is your own game (Steam) with the " +
                   "RE2R_AP_World mod by FuzzyGamesOn added on top. The launcher " +
                   "detects your Steam install and can download and extract the mod " +
                   "into your game folder. You connect to your Archipelago server " +
                   "in-game via the mod's own connection interface when starting a game. " +
                   "The AP world name and pinned fallback version are UNVERIFIED — confirm " +
                   "against the repository if the apworld does not generate correctly.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "RESIDENT EVIL 2 REMAKE INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? re2rDir     = ResolveRe2rDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = re2rDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + re2rDir
                : "Detected Steam install: " + re2rDir)
            : "Resident Evil 2 Remake not detected. Pick your install folder below, " +
              "or install it via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = re2rDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // mod status line
        bool modFound = re2rDir != null && FindModIndicatorFile(re2rDir) != null;
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modFound
                    ? "RE2R_AP_World mod files detected in the game folder."
                    : "RE2R_AP_World mod not detected in the game folder yet. Use Install " +
                      "on the Play tab, or follow the setup guide in the mod repository.",
            FontSize = 11, Foreground = modFound ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? re2rDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Resident Evil 2 Remake install folder (the one containing " +
                          "re2.exe). Detected from Steam automatically; set it here to " +
                          "override (e.g. non-standard Steam library).",
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
                Title            = "Select your Resident Evil 2 Remake install folder (contains re2.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? re2rDir ?? "")
                                   ? (overrideDir ?? re2rDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Resident Evil 2 Remake folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeRe2rDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeRe2rDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 883710). Use this " +
                   "picker for non-standard Steam libraries.",
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
            Text = "Launch Resident Evil 2 Remake and connect to your Archipelago server " +
                   "via the in-game AP connection interface provided by the mod. Enter the " +
                   "Server Address and Port (e.g. archipelago.gg and 38281), your Name " +
                   "(slot name), and the Password if the server requires one. Refer to the " +
                   "mod's repository and setup guide for connection details specific to this " +
                   "mod. This launcher does not pre-fill the connection.",
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
            "1. Own Resident Evil 2 Remake on Steam (appid 883710). Install it if you have not. " +
                "Use the picker above if it was not detected automatically.",
            "2. Click Install on the Play tab — the launcher will download the latest " +
                "RE2R_AP_World release from GitHub and extract it into your RE2R game folder.",
            "3. Alternatively, download the mod manually from the GitHub repository (link below) " +
                "and follow the setup guide in the repository's README for extraction and " +
                "configuration steps.",
            "4. Generate your multiworld game on Archipelago using the RE2R apworld. The AP " +
                "world name is 'Resident Evil 2 Remake' (UNVERIFIED — confirm against the " +
                "repository if generation fails).",
            "5. Launch Resident Evil 2 Remake from this tile or from Steam.",
            "6. Connect to your Archipelago server via the in-game AP connection interface. " +
                "Enter your Server Address, Port, Name (slot name), and optional Password. " +
                "(This launcher cannot pre-fill the connection — it is entered in-game.)",
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
            ("RE2R_AP_World (GitHub) ↗",                    ModRepoUrl),
            ("RE2R_AP_World Releases ↗",                    ModRepoUrl + "/releases"),
            ("Resident Evil 2 Remake AP Guide ↗",           SetupGuideUrl),
            ("Archipelago Official ↗",                      ArchipelagoSite),
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

    /// "v0.4.0" → "0.4.0" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the download URL. Prefers any
    /// .zip asset; falls back to the pinned version's release URL when the API
    /// is unreachable.
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
                string? preferred = null;   // any .zip asset
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        preferred ??= url;
                    }
                }
                if (preferred != null)
                    return (version, preferred);

                // If no zip, return the version so we at least know what's available,
                // but note no download URL was found.
                return (version, null);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        // Pinned fallback: construct the URL from the known pattern.
        string fallbackUrl = $"{ModRepoUrl}/releases/download/{FallbackVersion}/RE2R_AP_World_{FallbackVersion}.zip";
        return (FallbackVersion, fallbackUrl);
    }

    // ── Private helpers — Steam / RE2R detection ──────────────────────────────

    /// The RE2R install dir to use: the override (if set and valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolveRe2rDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeRe2rDir(ov))
            return ov;

        try { return DetectSteamRe2rDir(); }
        catch { return null; }
    }

    /// A folder "looks like" RE2R if it contains re2.exe.
    private static bool LooksLikeRe2rDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, Re2rExeName));
        }
        catch { return false; }
    }

    /// "Installed" = re2.exe present + optionally a mod indicator file.
    /// Returns true when the base game is present (mod may or may not be installed;
    /// we surface the mod status separately in the Settings panel).
    private bool IsRe2rWithModInstalled()
    {
        try
        {
            string? dir = ResolveRe2rDir();
            if (dir == null) return false;
            // Base game must be present.
            if (!File.Exists(Path.Combine(dir, Re2rExeName))) return false;
            // Prefer to confirm a mod file is also present.
            return FindModIndicatorFile(dir) != null || ReadStampedVersion() != null;
        }
        catch { return false; }
    }

    /// Look for any file in the game dir that indicates the AP mod has been
    /// extracted there (e.g. any .dll the mod ships, or a known mod config file).
    /// Returns the path of the first found indicator, or null.
    private static string? FindModIndicatorFile(string dir)
    {
        try
        {
            // Look for a BepInEx folder (common modding framework) or any .dll
            // that the AP mod would have placed in a plugins subdirectory.
            string bepInEx = Path.Combine(dir, "BepInEx");
            if (Directory.Exists(bepInEx)) return bepInEx;

            // Look for any obvious AP-mod file names (adjust when the real mod
            // layout is confirmed against the repository).
            foreach (string candidate in new[] { "archipelago.json", "re2r_ap.dll", "AP_RE2R.dll" })
            {
                string p = Path.Combine(dir, candidate);
                if (File.Exists(p)) return p;
            }

            // Recursive scan for any DLL under a "plugins" folder (BepInEx pattern).
            string pluginsDir = Path.Combine(dir, "BepInEx", "plugins");
            if (Directory.Exists(pluginsDir))
            {
                foreach (string dll in Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    /// Detect the Steam RE2R install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_883710.acf exists → steamapps\common\RESIDENT EVIL 2  BIOHAZARD RE2.
    private static string? DetectSteamRe2rDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{Re2rAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeRe2rDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeRe2rDir(conventional)) return conventional;
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

    /// Pull every "path" "<value>" pair out of a libraryfolders.vdf body.
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

    /// Read the "installdir" value from an appmanifest_*.acf.
    /// Returns null if absent.
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

    /// Start RE2R: prefer the exe in the detected/override install; if that
    /// cannot be found but Steam is present, fall back to the steam:// URL.
    private void StartRe2r()
    {
        string? dir = ResolveRe2rDir();
        string? exe = dir != null ? Path.Combine(dir, Re2rExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = dir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Resident Evil 2 Remake.");

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
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { /* fall through to the error below */ }
        }

        throw new FileNotFoundException(
            "Could not find re2.exe. Open this game's Settings and pick your " +
            "Resident Evil 2 Remake install folder, or install it via Steam.",
            Re2rExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod's release zip and extract it into the RE2R game directory.
    /// Existing files are overwritten but siblings are preserved (so other mods
    /// and game files are not disturbed).
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string gameDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"re2r-ap-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"re2r-ap-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading RE2R_AP_World {version}..."));
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
                        progress.Report((pct, $"Downloading RE2R_AP_World... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Extracting mod into your Resident Evil 2 Remake folder..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // If the zip wraps everything in a single sub-folder, flatten it so
            // files land directly in the game directory at the correct depth.
            string mergeRoot = tempExtract;
            string[] topDirs  = Directory.GetDirectories(tempExtract);
            string[] topFiles = Directory.GetFiles(tempExtract);
            if (topDirs.Length == 1 && topFiles.Length == 0)
            {
                // Single top-level folder — use it as the merge root.
                mergeRoot = topDirs[0];
            }

            // Merge the extracted tree INTO the existing game folder (do not wipe
            // the game directory — only overwrite files from the mod).
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
            catch { /* locked file (game open?) — skip; user can retry with game closed */ }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (install-dir override + an
    // informational version stamp) in its OWN JSON file so it stays a single
    // self-contained source file and does not modify Core/SettingsStore. BOM-less
    // UTF-8, read-modify-write (same approach as A Short Hike / Blasphemous / Jak).

    private sealed class Re2rSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private Re2rSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Re2rSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(Re2rSettings s)
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
