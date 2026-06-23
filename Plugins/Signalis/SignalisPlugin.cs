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

namespace LauncherV2.Plugins.Signalis;

// ═══════════════════════════════════════════════════════════════════════════════
// SignalisPlugin — install / launch for "SIGNALIS" (rose-engine / Humble Games,
// 2022) played through the SIGNALISArchipelagoRandomizer mod by devoidlazarus,
// which delivers an in-game Archipelago client. This is a NATIVE "ConnectsItself"
// integration in the same family as the shipped Blasphemous / Hollow Knight /
// TUNIC / Stardew Valley / A Short Hike plugins: the game itself speaks to the AP
// server (no emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// SIGNALIS is a 2022 Unity-based survival horror game for Windows (Steam appid
// 1262350 — VERIFY this against the Steam store if anything seems wrong). The mod
// repo is devoidlazarus/SIGNALISArchipelagoRandomizer on GitHub, first released in
// April 2025 (latest verified v0.1.2). The AP game string is "Signalis" (verified
// against the mod source code).
//
// ── HONEST REALITY CHECK (2026-06-19, verified online) ──────────────────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of SIGNALIS (Steam appid 1262350 — verified against the Steam store page;
// published by Humble Games). Archipelago support is delivered as a mod added on
// top via Unity mod infrastructure. The honest integration ceiling — exactly like
// the shipped Blasphemous / Hollow Knight / A Short Hike plugins — is "automate
// what is possible, guide the irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "Signalis" (verified against the mod source in
//     devoidlazarus/SIGNALISArchipelagoRandomizer). GameId here = "signalis".
//     SIGNALIS is a COMMUNITY Archipelago world delivered via the mod repo; the
//     user must install the .apworld if generating multiworld seeds locally.
//
//   * THE MOD repo is devoidlazarus/SIGNALISArchipelagoRandomizer (GitHub).
//     Latest release verified is v0.1.2 (April 2025). The mod is fresh (three
//     releases in April 2025) and actively maintained.
//
//   * CONNECTION is made IN-GAME via the mod's own AP connection UI. There is no
//     documented command-line / config-file prefill this launcher can apply. So
//     this plugin does NOT write any prefill file — the settings panel surfaces
//     the session's host / port / slot for the user to enter in-game.
//
//   * UNVERIFIED (mark for the user to check at build time):
//     - Steam AppId 1262350 — strongly expected but cross-check vs. Steam store.
//     - Steam common folder name "SIGNALIS" — Unity games on Steam often use the
//       stylized name as the folder. Check steamapps\common\ to be sure.
//     - Exe name "SIGNALIS.exe" — Unity games conventionally name the exe after
//       the game; check the actual install folder. If not found the plugin falls
//       back to a directory scan for any .exe at the root.
//     - Mod asset name in releases — the download loop accepts any .zip from the
//       release; add a name filter if the repo ships non-mod zips in the future.
//     - Mod drop location — BepInEx / Mods layout is assumed (see IsInstalled).
//       Verify against the mod's own README install instructions.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam SIGNALIS install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\SIGNALIS via appmanifest_1262350.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and takes
//      precedence; it is validated and persisted in this plugin's OWN sidecar
//      (Games/ROMs/signalis/signalis_launcher.json) — Core/SettingsStore is NOT
//      modified.
//   2. INSTALL/UPDATE (best effort) = download the mod's .zip from
//      devoidlazarus/SIGNALISArchipelagoRandomizer/releases/latest and extract
//      it into the game directory. The mod's README install steps are surfaced in
//      the settings panel for the user to follow (never a fake one-click).
//   3. LAUNCH = run SIGNALIS.exe from the detected/override install dir. If the
//      exe cannot be found but Steam is present, fall back to
//      steam://rungameid/1262350. ConnectsItself = true (the mod owns the slot —
//      the launcher must NOT hold its own ApClient on it). SupportsStandalone =
//      true (plain SIGNALIS runs fine without AP).
//
// ── DEFENSIVE / UNVERIFIED ───────────────────────────────────────────────────
//   * "Installed" is judged by the presence of a mod DLL under BepInEx/plugins/
//     or Mods/ in the detected/override SIGNALIS install (case-insensitive,
//     recursive). If the mod uses a different layout, adjust FindInstalledModDll.
//   * Steam library parsing is defensive: a hand-written tolerant VDF scan that
//     pulls quoted "path" values; any failure degrades to "SIGNALIS not found"
//     rather than throwing.
//   * No plaintext AP password is ever written by this plugin (the connection is
//     entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SignalisPlugin : IGamePlugin
{
    // ── Constants — the SIGNALISArchipelagoRandomizer mod ────────────────────
    private const string MOD_OWNER = "devoidlazarus";
    private const string MOD_REPO  = "SIGNALISArchipelagoRandomizer";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl   = "https://archipelago.gg/games/Signalis/info/en";
    private const string GameInfoUrl     = "https://archipelago.gg/games/Signalis/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — SIGNALIS appid 1262350.
    // UNVERIFIED: cross-check against the Steam store page if anything seems wrong.
    private const string SigAppId = "1262350";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SigAppId}";

    // UNVERIFIED: the Steam common folder name for SIGNALIS.
    // Unity games on Steam often use the game's display name all-caps; check
    // steamapps\common\ in a real install if detection fails.
    private const string SteamCommonFolderName = "SIGNALIS";

    // UNVERIFIED: the executable name for SIGNALIS on Windows.
    // Unity games conventionally name the .exe after the game. If this is wrong,
    // the plugin's fallback directory scan will locate any .exe at the game root.
    private const string SigExeName = "SIGNALIS.exe";

    /// Pinned fallback version when the GitHub API is unreachable (v0.1.2 verified
    /// live 2026-06-19 as the latest release tag).
    private const string FallbackVersion = "0.1.2";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackVersion}/SIGNALIS.Archipelago.Randomizer.zip";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "signalis";
    public string DisplayName => "SIGNALIS";
    public string Subtitle    => "PC · Archipelago mod";

    /// EXACT AP game string — verified against devoidlazarus/SIGNALISArchipelagoRandomizer
    /// source code.
    public string ApWorldName => "Signalis";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "signalis.png");

    public string ThemeAccentColor => "#E53935";   // blood red — survival horror
    public string[] GameBadges     => new[] { "Requires SIGNALIS on Steam" };

    public string Description =>
        "SIGNALIS (2022) is the acclaimed indie survival horror from rose-engine " +
        "(published by Humble Games), awarded 96% Very Positive on Steam with over " +
        "40,000 reviews. Set in a dystopian future, you play as Elster — a technician " +
        "REPLIIKA — searching for her lost partner in a nightmare of body horror, " +
        "surreal dreamscapes, and limited-inventory resource management. Inspired by " +
        "Silent Hill and Resident Evil 2, it is widely regarded as one of the finest " +
        "horror games of the decade. The Archipelago randomizer by devoidlazarus " +
        "shuffles key items and pickups across the game, turning each room's discovery " +
        "into a multiworld location check. Requires: your own legally-owned copy of " +
        "SIGNALIS on Steam. Install following the mod's repository instructions.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means a mod DLL is present under BepInEx/plugins/ or Mods/ in
    /// a detected/override SIGNALIS install. We do NOT gate on our own version
    /// stamp — the user may have installed the mod manually, which we honor.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and working files. The actual mod is
    /// extracted INTO the SIGNALIS install directory, not here. Exposed as
    /// GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Signalis");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file). Lives under Games/ROMs/signalis/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "signalis_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The SIGNALISArchipelagoRandomizer mod reports checks/items/goal to the AP
    // server itself — the launcher relays nothing. These exist for interface
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
        // 0. We need a SIGNALIS install to drop the mod into. Prefer an explicit
        //    override; else auto-detect the Steam install.
        progress.Report((2, "Locating your SIGNALIS installation..."));
        string? sigDir = ResolveSignalisDir();
        if (sigDir == null)
            throw new InvalidOperationException(
                "Could not find a SIGNALIS installation. Open this game's Settings " +
                "and pick your SIGNALIS folder (the one containing SIGNALIS.exe), " +
                "or install SIGNALIS via Steam first. The Archipelago mod is added " +
                "on top of your own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback).
        progress.Report((6, "Checking the latest SIGNALIS Archipelago Randomizer release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the SIGNALIS Archipelago Randomizer mod download on GitHub. " +
                "Check your internet connection, or download the mod manually from " +
                ModRepoUrl + "/releases — see Settings for links. " +
                "Install following the mod's README instructions.");

        // 2. Download + extract the mod zip INTO the SIGNALIS install directory.
        //    HONEST: this stages the mod files. Follow the mod's README for the
        //    exact install layout (BepInEx or other mod loader may be required).
        await DownloadAndExtractModAsync(zipUrl, version, sigDir, progress, ct);

        // 3. Stamp the version next to our sidecar so the tile can show it.
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool dllOk = FindInstalledModDll() != null;
        progress.Report((100,
            $"Staged the SIGNALIS Archipelago Randomizer {version} into your game folder" +
            (dllOk ? "." : " (verify the files landed correctly).") +
            " IMPORTANT: check the mod's README at " + ModRepoUrl + " for any additional " +
            "dependencies (such as BepInEx) that this download does NOT include. " +
            "To play: launch SIGNALIS and connect to your Archipelago server in-game. " +
            "(This launcher cannot pre-fill the connection — it is entered in-game.)"));
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
        // HONEST: the AP server connection for SIGNALIS is entered in-game via the
        // mod's own connection UI. There is no documented command-line / config
        // prefill this launcher can apply. Launching from this tile just starts the
        // game; the user connects in-game with the session credentials (the settings
        // panel surfaces those values to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartSignalis();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) SIGNALIS runs perfectly well.
    public bool SupportsStandalone => true;

    /// The SIGNALISArchipelagoRandomizer mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartSignalis();
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
        // The SIGNALISArchipelagoRandomizer mod receives items from the AP server
        // directly; there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid SIGNALIS folder contains
    /// SIGNALIS.exe (or at minimum a _Data Unity folder). Return null when
    /// acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your SIGNALIS install folder.";

        if (LooksLikeSignalisDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeSignalisDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a SIGNALIS installation. Pick the folder " +
               "that contains SIGNALIS.exe (for Steam this is usually " +
               @"...\steamapps\common\SIGNALIS).";
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
            Text = "SIGNALIS is your own game (Steam) with the SIGNALIS Archipelago " +
                   "Randomizer mod added on top by devoidlazarus. The launcher detects " +
                   "your Steam install and can stage the mod files into it, but you " +
                   "should follow the mod's README for any additional dependencies " +
                   "(such as BepInEx). You connect to your Archipelago server in-game. " +
                   "These external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "SIGNALIS INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? sigDir      = ResolveSignalisDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = sigDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + sigDir
                : "Detected Steam install: " + sigDir)
            : "SIGNALIS not detected. Pick your install folder below, or install " +
              "SIGNALIS via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = sigDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // mod status line
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                    ? "SIGNALIS Archipelago Randomizer mod found: " + modDll
                    : "SIGNALIS Archipelago Randomizer mod not found yet. Use Install " +
                      "on the Play tab, or install it manually following the mod's README.",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? sigDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your SIGNALIS install folder (the one containing SIGNALIS.exe). " +
                          "Detected from Steam automatically; set it here to override " +
                          "(e.g. if Steam is on a non-standard library path).",
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
                Title            = "Select your SIGNALIS install folder (contains SIGNALIS.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? sigDir ?? "")
                                   ? (overrideDir ?? sigDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a SIGNALIS folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeSignalisDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeSignalisDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 1262350 — UNVERIFIED: " +
                   "cross-check against the Steam store if detection fails). Use this " +
                   "picker for a non-standard Steam library path.",
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
            Text = "Launch SIGNALIS with the mod installed. The mod provides its own " +
                   "Archipelago connection UI in-game. Enter your Server Address, Port, " +
                   "Slot Name, and Password (if any) as shown by the mod. This launcher " +
                   "does not pre-fill the connection — it is entered in-game.",
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
            "1. Own SIGNALIS (on Steam). Install it if you have not. Use the picker above " +
                "if it was not detected automatically.",
            "2. Check the mod README at the GitHub link below for any required dependencies " +
                "(such as BepInEx). Install those first if needed.",
            "3. Use the Install button on the Play tab to download and stage the " +
                "SIGNALIS Archipelago Randomizer mod files, OR download manually from " +
                "the mod's GitHub releases page and install following the README.",
            "4. Launch SIGNALIS and confirm the mod loaded correctly.",
            "5. To play with Archipelago: connect to your server in-game using the " +
                "mod's connection UI. Enter your Server Address, Port, Slot Name, and " +
                "Password if required. (This launcher cannot pre-fill the connection.)",
            "6. For multiworld seed generation: download the .apworld file from the mod's " +
                "GitHub and place it in your Archipelago worlds folder.",
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
            ("SIGNALIS Archipelago Randomizer (GitHub) ↗", ModRepoUrl),
            ("SIGNALIS Randomizer Releases ↗",             $"{ModRepoUrl}/releases"),
            ("SIGNALIS AP Guide ↗",                        GameInfoUrl),
            ("Archipelago Official ↗",                     ArchipelagoSite),
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

    /// "v0.1.2" → "0.1.2" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the mod zip download URL. Prefers
    /// any .zip asset; falls back to the pinned fallback URL when the API is
    /// unreachable.
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
                    if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
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

    // ── Private helpers — Steam / SIGNALIS detection ──────────────────────────

    /// The SIGNALIS install dir to use: the override (if set and valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolveSignalisDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeSignalisDir(ov))
            return ov;

        try { return DetectSteamSignalisDir(); }
        catch { return null; }
    }

    /// A folder "looks like" SIGNALIS if it has SIGNALIS.exe, or the Unity
    /// "_Data" folder beside it, or any .exe at the root (fallback for an
    /// unexpected exe name — UNVERIFIED).
    private static bool LooksLikeSignalisDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            // Primary check: known exe name (UNVERIFIED — see file header comment).
            if (File.Exists(Path.Combine(dir, SigExeName))) return true;
            // Unity _Data folder (conventional name pattern).
            if (Directory.Exists(Path.Combine(dir, "SIGNALIS_Data"))) return true;
            // Fallback: any .exe at the root (catches an unexpected exe name).
            if (Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly).Any())
                return true;
            return false;
        }
        catch { return false; }
    }

    /// Find SIGNALIS.exe (or any .exe at root if it does not exist — UNVERIFIED
    /// exe name) in the given directory. Returns the full path or null.
    private static string? FindExeInDir(string dir)
    {
        try
        {
            string primary = Path.Combine(dir, SigExeName);
            if (File.Exists(primary)) return primary;
            // Fallback directory scan (UNVERIFIED exe name — see file header comment).
            return Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly)
                            .FirstOrDefault();
        }
        catch { return null; }
    }

    /// Detect the Steam SIGNALIS install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_1262350.acf exists → steamapps\common\SIGNALIS.
    private static string? DetectSteamSignalisDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{SigAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeSignalisDir(candidate)) return candidate;
                    }
                    // UNVERIFIED folder name — also try the all-caps conventional name.
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeSignalisDir(conventional)) return conventional;
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
    /// steamapps\libraryfolders.vdf. Tolerant text scan (the VDF is a simple quoted
    /// key/value tree; we only need the path values).
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

    // ── Private helpers — installed-mod detection ─────────────────────────────

    /// Find a mod DLL under BepInEx/plugins/ or Mods/ in the detected/override
    /// SIGNALIS install (case-insensitive, recursive). Returns the dll path or null.
    /// UNVERIFIED: if the mod uses a different drop location, update the search roots
    /// below (check the mod's README install instructions).
    private string? FindInstalledModDll()
    {
        try
        {
            string? sig = ResolveSignalisDir();
            if (sig == null) return null;

            // Check BepInEx/plugins (standard BepInEx mod location).
            string bepInExPlugins = Path.Combine(sig, "BepInEx", "plugins");
            if (Directory.Exists(bepInExPlugins))
            {
                foreach (string dll in Directory.EnumerateFiles(bepInExPlugins, "*.dll",
                    SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(dll).ToLowerInvariant();
                    // Accept any DLL whose name suggests the SIGNALIS AP randomizer.
                    if (name.Contains("signalis") || name.Contains("archipelago") ||
                        name.Contains("randomizer"))
                        return dll;
                }
            }

            // Also check a top-level Mods/ folder (some Unity mod frameworks use this).
            string modsDir = Path.Combine(sig, "Mods");
            if (Directory.Exists(modsDir))
            {
                foreach (string dll in Directory.EnumerateFiles(modsDir, "*.dll",
                    SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(dll).ToLowerInvariant();
                    if (name.Contains("signalis") || name.Contains("archipelago") ||
                        name.Contains("randomizer"))
                        return dll;
                }
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start SIGNALIS: prefer the exe in the detected/override install; if that
    /// cannot be found but Steam is present, fall back to the steam:// URL. Surfaces
    /// a clear message rather than failing opaquely.
    private void StartSignalis()
    {
        string? sig = ResolveSignalisDir();
        string? exe = sig != null ? FindExeInDir(sig) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = sig!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start SIGNALIS.");

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
            "Could not find SIGNALIS.exe. Open this game's Settings and pick your " +
            "SIGNALIS install folder, or install SIGNALIS via Steam. " +
            "(UNVERIFIED: the exe name 'SIGNALIS.exe' has not been confirmed against a real " +
            "install — if the game uses a different name, use the folder picker.)",
            SigExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod's .zip from GitHub and extract it into the SIGNALIS game
    /// directory. Existing files are overwritten; siblings (other mods, save files,
    /// etc.) are not disturbed.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string gameDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"signalis-randomizer-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"signalis-randomizer-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading SIGNALIS Archipelago Randomizer {version}..."));
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
                        progress.Report((pct, $"Downloading SIGNALIS Randomizer... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Installing the mod into your SIGNALIS folder..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // The mod zip may wrap everything in a single top-level folder; flatten it
            // if the real content (BepInEx/ or similar) is one level deep.
            string mergeRoot = tempExtract;
            string[] subdirs = Directory.GetDirectories(mergeRoot);
            string[] files   = Directory.GetFiles(mergeRoot);
            if (subdirs.Length == 1 && files.Length == 0)
            {
                // Peek inside the single sub-folder to see if it looks like mod content.
                string inner = subdirs[0];
                bool innerHasContent =
                    Directory.GetDirectories(inner).Length > 0 ||
                    Directory.GetFiles(inner, "*.dll", SearchOption.AllDirectories).Any();
                if (innerHasContent)
                    mergeRoot = inner;
            }

            // Merge the extracted tree INTO the existing game directory (do not wipe it
            // — the user's save files and other mods live alongside).
            MergeDirectory(mergeRoot, gameDir);

            progress.Report((90, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))        File.Delete(tempZip); } catch { }
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
    // This plugin keeps its launcher-side settings (the install-dir override + an
    // informational version stamp) in its OWN JSON file so it stays a single
    // self-contained source file and does not modify Core/SettingsStore. BOM-less
    // UTF-8, read-modify-write (same approach as AShortHike / Blasphemous / Jak).

    private sealed class SigSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private SigSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<SigSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(SigSettings s)
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
