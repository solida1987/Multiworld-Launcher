using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;
using Microsoft.Win32;

// NOTE: WPF UI types are FULLY QUALIFIED throughout this file (System.Windows.*).
// The real launcher project sets <UseWindowsForms>true</UseWindowsForms> alongside
// WPF, so the bare names Button / TextBox / Color / Brushes / MessageBox /
// FontWeights / Orientation / Clipboard collide between System.Windows.Forms and
// System.Windows[.Controls/.Media] (CS0104). Qualifying every UI type avoids that.
// GlobalUsings.cs already aliases the colliding short names project-wide — so this
// file must NOT add any file-level `using X = System.Windows...;` alias (CS1537).

namespace LauncherV2.Plugins.Unbeatable;

// ═══════════════════════════════════════════════════════════════════════════════
// UnbeatablePlugin — install / launch for "UNBEATABLE" (D-CELL GAMES) played
// through the Archipelago integration by AllPoland (GitHub: AllPoland/unbeatAP).
// This is a NATIVE "ConnectsItself" integration in the same family as the shipped
// Blasphemous / Hollow Knight / TUNIC / A Short Hike / AM2R plugins: the BepInEx
// mod speaks to the AP server (no emulator, no Lua bridge, no launcher-held
// ApClient on the slot).
//
// UNBEATABLE is a narrative rhythm game by D-CELL GAMES set in a world where music
// has been made illegal. The game follows a traveling band of music outlaws through
// a story-driven adventure with Arcade Mode as the core gameplay loop.
//
// ── HONEST REALITY CHECK (2026-06-19) ────────────────────────────────────────
//   * THE AP WORLD game string: "unbeatable_arcade" — CONFIRMED. This matches the
//     apworld filename distributed by AllPoland/unbeatAP. Note: the AP string is
//     NOT "UNBEATABLE" — it is "unbeatable_arcade" (Arcade Mode integration only).
//
//   * THE MOD REPO: AllPoland/unbeatAP (GitHub). The integration shuffles tracks,
//     characters, and story unlocks in Arcade Mode. Track completions and score
//     milestones become multiworld location checks. Items from other worlds arrive
//     as new tracks or characters made available. The BepInEx mod shows the
//     Archipelago connection UI on the Arcade Mode title screen.
//
//   * STEAM APP ID: UNKNOWN — UNBEATABLE may be on itch.io, Steam, or both.
//     The AppId is UNVERIFIED and has NOT been confirmed. This plugin uses a
//     manual path picker ONLY (same pattern as AM2R). NO Steam registry lookup.
//
//   * THE EXE NAME: "UNBEATABLE.exe" — UNVERIFIED. This is a reasonable assumption
//     based on the game title. The user is directed to navigate to their actual
//     UNBEATABLE installation and verify. See settings panel notes.
//
//   * BEPINEX DEPENDENCY: The mod runs on BepInEx, which must be installed
//     separately into the UNBEATABLE game folder BEFORE the mod will work.
//     The launcher stages the mod DLL into BepInEx/plugins/ but does NOT install
//     BepInEx itself. The settings panel makes this dependency explicit.
//
//   * CONNECTION: Made in-game via an overlay UI on the Arcade Mode title screen.
//     The launcher surfaces session host/port/slot for the user to copy. No
//     command-line prefill is documented, so this plugin does NOT attempt one.
//
//   * NO STEAM DETECTION: Steam AppId is unknown. Detection is via a user-
//     specified folder only (stored in this plugin's own sidecar JSON). There is
//     no Steam registry lookup.
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. DETECT UNBEATABLE.exe + BepInEx/plugins/unbeatAP*.dll in a user-specified
//      install folder (stored in a sidecar JSON at
//      Games/ROMs/unbeatable/unbeatable_launcher.json). No Steam detection.
//   2. INSTALL/UPDATE — download the latest release zip from
//      AllPoland/unbeatAP/releases/latest, find an unbeatAP*.zip asset, extract
//      the DLL into <game>/BepInEx/plugins/. Provides clear guided steps + links
//      for the manual steps (get the game, install BepInEx, AP guide, mod repo).
//   3. LAUNCH = run UNBEATABLE.exe from the configured directory.
//      ConnectsItself = true (the BepInEx integration speaks to AP itself).
//      SupportsStandalone = true (the unmodified game runs without AP).
//      No connection prefill (entered in-game via overlay UI).
//
// ── DEFENSIVE / UNVERIFIED ───────────────────────────────────────────────────
//   * "Installed" = UNBEATABLE.exe present + unbeatAP*.dll in BepInEx/plugins/.
//   * UNBEATABLE.exe name is UNVERIFIED — users may need to navigate to the exe.
//   * Steam AppId is UNKNOWN — no Steam detection, manual path only.
//   * Fallback version "1.0.0" is a guess; update once actual release tags are
//     known from AllPoland/unbeatAP releases page.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class UnbeatablePlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────
    private const string MOD_OWNER = "AllPoland";
    private const string MOD_REPO  = "unbeatAP";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl    = "https://archipelago.gg/games/unbeatable_arcade/info/en";
    private const string ArchipelagoSite  = "https://archipelago.gg";
    private const string BepInExGuideUrl  = "https://docs.bepinex.dev/articles/user_guide/installation/index.html";

    /// UNBEATABLE.exe — UNVERIFIED. The actual filename may differ.
    /// The user is directed to navigate to their install folder and verify.
    private const string UnbeatableExeName = "UNBEATABLE.exe";

    /// BepInEx plugins folder name, relative to the game directory.
    private const string BepInExPluginsSubdir = @"BepInEx\plugins";

    /// Prefix used to find the mod DLL inside BepInEx/plugins/.
    private const string ModDllPrefix = "unbeatAP";

    /// Pinned fallback when the GitHub API is unreachable. Update once real tags
    /// are confirmed from AllPoland/unbeatAP releases. "1.0.0" is a first guess.
    private const string FallbackVersion = "1.0.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "unbeatable";
    public string DisplayName => "UNBEATABLE";
    public string Subtitle    => "PC · Archipelago mod";

    /// AP world game string — CONFIRMED as "unbeatable_arcade".
    /// This is the apworld filename used by AllPoland/unbeatAP (Arcade Mode).
    /// Do NOT change to "UNBEATABLE" — that is incorrect.
    public string ApWorldName => "unbeatable_arcade";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "unbeatable.png");

    public string ThemeAccentColor => "#FF4081";   // Hot pink — UNBEATABLE's signature color
    public string[] GameBadges     => new[] { "BepInEx required separately", "Arcade Mode" };

    public string Description =>
        "UNBEATABLE (D-CELL GAMES) is a narrative rhythm game about a world where music is " +
        "illegal, following a traveling band of music outlaws. The Archipelago mod by AllPoland " +
        "integrates UNBEATABLE's Arcade Mode: track completions, score milestones, and character " +
        "unlocks become multiworld location checks, and items from other worlds arrive as new " +
        "tracks or characters made available. The mod runs on BepInEx (a prerequisite for many " +
        "Unity game mods) and shows the Archipelago connection UI on the Arcade Mode title screen. " +
        "The launcher stages the mod into your BepInEx/plugins folder; BepInEx itself must be " +
        "installed separately first. You configure your server connection via the in-game UI.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means UNBEATABLE.exe is present in the configured folder
    /// AND the unbeatAP mod DLL is present in BepInEx/plugins/.
    public bool IsInstalled => FindUnbeatableExe() != null && FindModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and working files. The actual game is
    /// in a user-specified folder, not here. Exposed as GameDirectory for the
    /// IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Unbeatable");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore).
    /// Lives under Games/ROMs/unbeatable/ per the brief.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "unbeatable_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The unbeatAP BepInEx mod speaks to the AP server itself — the launcher
    // relays nothing. These exist for interface compatibility (ConnectsItself = true).
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
            string? modDll = FindModDll();
            InstalledVersion = modDll != null
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
            AvailableVersion = null; // contract: never throw on network failure
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. We need a target game folder. Use the configured override if set;
        //    otherwise default to our GameDirectory.
        progress.Report((2, "Locating target UNBEATABLE install folder..."));
        string? installDir = LoadOverrideDir();
        if (string.IsNullOrWhiteSpace(installDir))
            installDir = GameDirectory;

        // 1. Resolve the latest release (pinned fallback when offline).
        progress.Report((6, "Checking the latest unbeatAP release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the unbeatAP download on GitHub. " +
                "Check your internet connection, or download the mod manually " +
                "from " + ModRepoUrl + "/releases and extract it into " +
                "BepInEx/plugins/ inside your UNBEATABLE folder.");

        // 2. Download + extract the release zip — DLLs go into BepInEx/plugins/.
        await DownloadAndExtractAsync(zipUrl, version, installDir, progress, ct);

        // 3. Stamp the version next to our sidecar so the tile can show it.
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool exeOk = FindUnbeatableExe() != null;
        bool dllOk = FindModDll() != null;
        progress.Report((100,
            $"unbeatAP {version} staged into {Path.Combine(installDir, BepInExPluginsSubdir)}" +
            (!exeOk ? " (UNBEATABLE.exe not detected — verify the exe name in Settings)" : "") +
            (!dllOk ? " (unbeatAP DLL not found — BepInEx may not be installed yet)" : ".") +
            " IMPORTANT: BepInEx must be installed separately before the mod will work. " +
            "See the BepInEx installation guide in the Settings panel."));
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
        // HONEST: the AP server connection for UNBEATABLE is entered in an overlay
        // UI that appears on the Arcade Mode title screen. There is no documented
        // command-line / config prefill. Launching from this tile just starts the
        // game; the user connects in-game using the session credentials shown in
        // the settings panel.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the BepInEx mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartUnbeatable();
        return Task.CompletedTask;
    }

    /// Plain UNBEATABLE runs without AP — the Arcade Mode title screen overlay
    /// can simply be skipped (no connection required for normal play).
    public bool SupportsStandalone => true;

    /// The unbeatAP BepInEx mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartUnbeatable();
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
        // The unbeatAP BepInEx mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The BepInEx mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid UNBEATABLE folder contains
    /// UNBEATABLE.exe (UNVERIFIED name — see header). Returns null when
    /// acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your UNBEATABLE install folder.";

        if (LooksLikeUnbeatableDir(folder))
            return null;

        return "That does not look like a UNBEATABLE installation (UNBEATABLE.exe not found). " +
               "Note: the exe name is UNVERIFIED — navigate to the folder containing the " +
               "UNBEATABLE game executable and select it. If the exe has a different name, " +
               "pick the folder anyway and the launcher will attempt to launch it from there.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Honesty / unverified header ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "UNBEATABLE uses BepInEx (a Unity mod framework) for its Archipelago " +
                   "integration. BepInEx must be installed separately into your UNBEATABLE " +
                   "game folder BEFORE the mod will work — the Install button only stages " +
                   "the mod DLL into BepInEx/plugins/. The exe name (UNBEATABLE.exe) is " +
                   "UNVERIFIED — navigate to your actual UNBEATABLE installation when " +
                   "picking the folder. The AP game string is \"unbeatable_arcade\" " +
                   "(confirmed). You connect to AP in-game via an overlay on the Arcade " +
                   "Mode title screen.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: install folder ────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "UNBEATABLE INSTALL FOLDER", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? overrideDir = LoadOverrideDir();
        string? exePath     = FindUnbeatableExe();
        string? modDllPath  = FindModDll();

        string detectMsg;
        System.Windows.Media.Brush detectColor;
        if (exePath != null && modDllPath != null)
        {
            detectMsg   = $"UNBEATABLE.exe found: {exePath}\nunbeatAP mod DLL found: {modDllPath}";
            detectColor = success;
        }
        else if (exePath != null)
        {
            detectMsg   = $"UNBEATABLE.exe found: {exePath}\nMod DLL not found — install the mod " +
                          "or install BepInEx first (see Guided Setup below).";
            detectColor = warn;
        }
        else if (overrideDir != null)
        {
            detectMsg   = "Folder configured but UNBEATABLE.exe not found. " +
                          "Note: the exe name is UNVERIFIED — verify your UNBEATABLE installation. " +
                          "Folder: " + overrideDir;
            detectColor = warn;
        }
        else
        {
            detectMsg   = "No UNBEATABLE install configured. Use the Install button (Play tab) " +
                          "or pick your UNBEATABLE folder below.";
            detectColor = muted;
        }

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = detectColor,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your UNBEATABLE install folder (the one containing the game executable). " +
                          "The mod DLL will be staged into BepInEx/plugins/ inside this folder. " +
                          "Note: UNBEATABLE.exe is the assumed exe name (UNVERIFIED).",
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
                Title            = "Select your UNBEATABLE install folder (contains the game exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? "")
                                   ? overrideDir!
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                // Soft validate — UNBEATABLE.exe may not be found if the exe name
                // differs (UNVERIFIED). We still allow saving the path.
                if (!Directory.Exists(picked))
                {
                    System.Windows.MessageBox.Show(
                        "That folder does not exist. Pick your UNBEATABLE install folder.",
                        "Invalid folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeUnbeatableDir(picked))
                {
                    var result = System.Windows.MessageBox.Show(
                        "UNBEATABLE.exe was not found in that folder.\n\n" +
                        "Note: the exe name is UNVERIFIED — it may have a different name " +
                        "depending on your version or platform. Save this folder anyway?\n\n" +
                        "The launcher will try to launch from this folder.",
                        "Exe not found (UNVERIFIED name)",
                        System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
                    if (result != System.Windows.MessageBoxResult.Yes)
                        return;
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
            Text = "UNBEATABLE's Steam AppId is unknown — there is no automatic Steam " +
                   "detection. Navigate to your UNBEATABLE install folder manually and select it. " +
                   "The exe name (UNBEATABLE.exe) is UNVERIFIED and may differ from your actual " +
                   "install — the launcher will still attempt to launch from this folder.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: BepInEx dependency ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "BEPINEX PREREQUISITE", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "The unbeatAP mod runs on BepInEx — a Unity mod framework. BepInEx must " +
                   "be installed separately into your UNBEATABLE game folder. The Install " +
                   "button only stages the mod DLL into BepInEx/plugins/; it does NOT install " +
                   "BepInEx itself. Install BepInEx first using the official guide (link below), " +
                   "then use the Install button to add the unbeatAP mod.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Connection (entered in-game) ─────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Launch UNBEATABLE and navigate to Arcade Mode. The BepInEx mod shows an " +
                   "Archipelago connection overlay on the Arcade Mode title screen. Enter your " +
                   "Server Address and Port (e.g. archipelago.gg and 38281), your Name (slot name), " +
                   "and the Password if the server has one, then connect. This launcher does not " +
                   "pre-fill the connection.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: AP game string note ──────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "AP GAME STRING", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "The AP game string is \"unbeatable_arcade\" (CONFIRMED — matches the " +
                   "apworld filename from AllPoland/unbeatAP). When generating your multiworld " +
                   "on the Archipelago website, select \"unbeatable_arcade\" from the game list. " +
                   "Do NOT use \"UNBEATABLE\" — that is not the correct game string for this mod.",
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
            "1. Get UNBEATABLE: obtain the game from D-CELL GAMES (itch.io, Steam, or " +
                "wherever it is distributed). The launcher does not provide the base game.",
            "2. Install BepInEx: follow the BepInEx installation guide (link below) to " +
                "install the BepInEx Unity mod framework into your UNBEATABLE game folder. " +
                "This is a prerequisite — the mod will not work without it.",
            "3. Use the Install button on the Play tab to download and stage the latest " +
                "unbeatAP mod DLL from AllPoland/unbeatAP (GitHub) automatically. " +
                "Alternatively, download the mod manually from the releases page (link below) " +
                "and extract the DLL into BepInEx/plugins/ inside your game folder.",
            "4. Point the launcher at your UNBEATABLE folder using the picker above. " +
                "NOTE: the exe name (UNBEATABLE.exe) is UNVERIFIED — navigate to the folder " +
                "containing the actual game executable, even if the name differs.",
            "5. To play: press Launch, navigate to Arcade Mode, and enter your " +
                "Server Address, Port, Name, and optional Password in the Archipelago " +
                "overlay that appears on the Arcade Mode title screen. (This launcher " +
                "cannot pre-fill the connection — it is entered in-game.)",
            "6. For multiworld generation: use the game string \"unbeatable_arcade\" " +
                "on the Archipelago website (confirmed). See the AP setup guide (link below).",
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
            ("unbeatAP Mod (GitHub) ↗",                   ModRepoUrl),
            ("unbeatAP Releases (download) ↗",             ModRepoUrl + "/releases"),
            ("BepInEx Installation Guide ↗",               BepInExGuideUrl),
            ("UNBEATABLE Setup Guide (Archipelago) ↗",     SetupGuideUrl),
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

    /// "v1.0.0" → "1.0.0" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + zip download URL.
    /// Prefers the first asset whose name matches "unbeatAP*.zip"; falls back to
    /// any .zip asset; falls back to the pinned fallback version when offline.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(CancellationToken ct)
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
                string? bestZipUrl  = null;
                string? firstZipUrl = null;

                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        firstZipUrl ??= url;
                        // Prefer a zip whose name starts with "unbeatAP".
                        if (name.StartsWith(ModDllPrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            bestZipUrl = url;
                            break; // found the best candidate
                        }
                    }
                }

                string? zipUrl = bestZipUrl ?? firstZipUrl;
                if (zipUrl != null)
                    return (version, zipUrl);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        // Pinned fallback — "1.0.0" is a guess. The URL pattern follows the standard
        // GitHub release-asset convention. Update once real tags are known.
        string fallbackUrl =
            $"{ModRepoUrl}/releases/download/v{FallbackVersion}/unbeatAP-{FallbackVersion}.zip";
        return (FallbackVersion, fallbackUrl);
    }

    // ── Private helpers — UNBEATABLE detection ────────────────────────────────

    /// Find UNBEATABLE.exe in the configured install folder (override dir, then
    /// default GameDirectory). Returns the full exe path or null.
    /// NOTE: UNBEATABLE.exe name is UNVERIFIED — see header.
    private string? FindUnbeatableExe()
    {
        try
        {
            string? dir = ResolveInstallDir();
            if (dir == null) return null;
            string exe = Path.Combine(dir, UnbeatableExeName);
            return File.Exists(exe) ? exe : null;
        }
        catch { return null; }
    }

    /// Find the unbeatAP mod DLL in BepInEx/plugins/ inside the configured install
    /// folder. Returns the full DLL path or null.
    private string? FindModDll()
    {
        try
        {
            string? dir = ResolveInstallDir();
            if (dir == null) return null;
            string pluginsDir = Path.Combine(dir, BepInExPluginsSubdir);
            if (!Directory.Exists(pluginsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(pluginsDir, "*.dll"))
            {
                if (Path.GetFileName(dll).StartsWith(ModDllPrefix, StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
            return null;
        }
        catch { return null; }
    }

    /// The install directory to use: the override (if set and valid) wins, else
    /// the default GameDirectory (only if UNBEATABLE.exe is actually there).
    private string? ResolveInstallDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && Directory.Exists(ov))
            return ov;

        if (LooksLikeUnbeatableDir(GameDirectory))
            return GameDirectory;

        return null;
    }

    /// A folder "looks like" a UNBEATABLE install if it contains UNBEATABLE.exe.
    /// NOTE: UNBEATABLE.exe is UNVERIFIED — see header.
    private static bool LooksLikeUnbeatableDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, UnbeatableExeName));
        }
        catch { return false; }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start UNBEATABLE from the configured install directory.
    /// NOTE: UNBEATABLE.exe name is UNVERIFIED — see header. If the exe is not
    /// found at the expected path, throws with a message directing the user to
    /// the Settings panel.
    private void StartUnbeatable()
    {
        // Try the confirmed exe name first.
        string? dir = ResolveInstallDir();
        string? exe = FindUnbeatableExe();

        // If the configured directory exists but exe was not found, try to find
        // any plausible game exe in the root (fallback for unknown exe names).
        if (exe == null && dir != null && Directory.Exists(dir))
        {
            // Last resort: look for any .exe that isn't a common non-game binary.
            foreach (string candidate in Directory.EnumerateFiles(dir, "*.exe"))
            {
                string name = Path.GetFileName(candidate).ToLowerInvariant();
                // Skip launchers, uninstallers, and crash reporters.
                if (name.Contains("uninstall") || name.Contains("crashreport") ||
                    name.Contains("setup") || name.Contains("dotnet") ||
                    name.Contains("ue4") || name.Contains("ue5") || name == "ue4game.exe")
                    continue;
                exe = candidate;
                break;
            }
        }

        if (exe == null)
            throw new FileNotFoundException(
                "Could not find the UNBEATABLE game executable. " +
                "Open this game's Settings and pick your UNBEATABLE install folder. " +
                "NOTE: the exe name (UNBEATABLE.exe) is UNVERIFIED — navigate to the " +
                "folder containing the actual game executable.",
                UnbeatableExeName);

        string gameDir = Path.GetDirectoryName(exe)!;
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exe,
            WorkingDirectory = gameDir,
            UseShellExecute  = true,
        }) ?? throw new InvalidOperationException("Failed to start UNBEATABLE.");

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
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod release zip and extract the DLL(s) into the BepInEx
    /// plugins folder inside the target game directory.
    /// Existing files are overwritten; other BepInEx plugins are preserved.
    private async Task DownloadAndExtractAsync(
        string zipUrl,
        string version,
        string gameDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"unbeatap-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"unbeatap-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading unbeatAP {version}..."));
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
                        progress.Report((pct, $"Downloading unbeatAP... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Extracting unbeatAP mod files..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // The mod zip may contain DLLs at root level or inside a sub-folder.
            // Find all DLLs whose name starts with ModDllPrefix and copy them into
            // BepInEx/plugins/ inside the game directory.
            string pluginsDir = Path.Combine(gameDir, BepInExPluginsSubdir);
            Directory.CreateDirectory(pluginsDir);

            bool copiedAny = false;
            foreach (string dll in Directory.EnumerateFiles(
                tempExtract, "*.dll", SearchOption.AllDirectories))
            {
                string dllName = Path.GetFileName(dll);
                if (dllName.StartsWith(ModDllPrefix, StringComparison.OrdinalIgnoreCase)
                    || dllName.EndsWith(ModDllPrefix + ".dll", StringComparison.OrdinalIgnoreCase))
                {
                    string dest = Path.Combine(pluginsDir, dllName);
                    try { File.Copy(dll, dest, overwrite: true); copiedAny = true; }
                    catch { /* locked file (game open?) — non-fatal, user can retry */ }
                }
            }

            // Fallback: if no prefixed DLL found, copy all DLLs from the zip root.
            if (!copiedAny)
            {
                foreach (string dll in Directory.EnumerateFiles(tempExtract, "*.dll"))
                {
                    string dest = Path.Combine(pluginsDir, Path.GetFileName(dll));
                    try { File.Copy(dll, dest, overwrite: true); }
                    catch { }
                }
            }

            progress.Report((90, "Mod DLL staged into BepInEx/plugins/."));
        }
        finally
        {
            try { if (File.Exists(tempZip))          File.Delete(tempZip); }                     catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (install-dir override + an
    // informational version stamp) in its OWN JSON file so it stays a single
    // self-contained source file and does not modify Core/SettingsStore.
    // BOM-less UTF-8, read-modify-write (same approach as AM2R / AShortHike).

    private sealed class UnbeatableSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private UnbeatableSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<UnbeatableSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(UnbeatableSettings s)
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
