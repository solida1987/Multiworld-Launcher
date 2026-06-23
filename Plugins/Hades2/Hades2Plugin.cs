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

namespace LauncherV2.Plugins.Hades2;

// ═══════════════════════════════════════════════════════════════════════════════
// Hades2Plugin — install / launch for "Hades II" (Supergiant Games, 2024 Early
// Access) with Archipelago support via the H2_Archipelago mod by JFrog-55.
//
// !! IMPORTANT: This is HADES II (the SEQUEL). The launcher already ships a
// separate Hades plugin (GameId "hades") for the original Hades (2020). This
// plugin uses GameId "hades_2" and is entirely independent of that one. !!
//
// ── HONESTY / UNVERIFIED DETAILS (written 2026-06-19) ───────────────────────
// Hades II was in Early Access when this plugin was written. Several technical
// details COULD NOT BE VERIFIED against a running install and are marked
// UNVERIFIED below. The plugin degrades gracefully when unverified values are
// wrong — it always falls back to manual paths or the steam:// URL.
//
//   STEAM APP ID:       1145350 (RESOLVED) — Hades II is 1145350; Hades 1 is
//                       1145360. The two were originally swapped; corrected so
//                       H2_STEAM_APP_ID = "1145350" (see the constant below).
//                       Folder name / exe / AP game string remain best-effort
//                       defaults with fallbacks (still flagged in the UI notice).
//
//   STEAM COMMON FOLDER: The game may install as "Hades 2" (with space) or
//                        "Hades2" (no space). Both variants are checked.
//
//   EXE NAME:           "Hades2.exe" is assumed; "Hades.exe" is also tried as
//                       a fallback in case Supergiant reused the exe name.
//
//   AP WORLD GAME STRING: "Hades II" (UNVERIFIED — verify against
//                          JFrog-55/H2_Archipelago worlds/__init__.py,
//                          specifically the `game = "..."` line). If the actual
//                          string differs, update ApWorldName below.
//
// ── AP INTEGRATION (JFrog-55/H2_Archipelago) ────────────────────────────────
// The Archipelago mod shuffles boon unlocks, weapon forms, and story progression
// across the multiworld. Players enter connection details in an in-game menu or
// config file after installing the mod. ConnectsItself = true: the mod's in-game
// client owns the server slot — the launcher must NOT hold its own ApClient.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ────────────────────────────────────
//   1. DETECT the Steam Hades II install via the Windows registry + VDF parsing.
//      Checks BOTH "Hades 2" and "Hades2" Steam library folder names. A manual
//      install-dir override (Settings folder picker) takes precedence and is
//      persisted in this plugin's own sidecar
//      (Games/ROMs/hades_2/hades_2_launcher.json).
//   2. INSTALL/UPDATE = download the mod's latest release zip from
//      JFrog-55/H2_Archipelago/releases/latest, extract it into the game dir.
//      Falls back to pinned version 0.1.0 when the GitHub API is unreachable.
//   3. LAUNCH = run Hades2.exe from the detected/override install dir, or fall
//      back to steam://rungameid/H2_STEAM_APP_ID. Connection details entered
//      in-game. SupportsStandalone = true.
//
// ── DEFENSIVE NOTES ─────────────────────────────────────────────────────────
//   * All Steam detection is read-only. Failures degrade to "not found" rather
//     than throwing.
//   * IsInstalled requires BOTH the game exe AND a mod folder/DLL in the install.
//   * No AP password is ever written to disk by this plugin (connection is
//     entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Hades2Plugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string MOD_OWNER = "JFrog-55";
    private const string MOD_REPO  = "H2_Archipelago";

    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl   = "https://archipelago.gg/games/Hades%20II/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Hades II Steam AppId is 1145350 (store.steampowered.com/app/1145350).
    // Hades 1 is 1145360 — the two were swapped; corrected here.
    private const string H2_STEAM_APP_ID = "1145350";
    private static readonly string SteamRunUrl = $"steam://rungameid/{H2_STEAM_APP_ID}";

    // UNVERIFIED — both names are tried; the actual folder is whichever Steam chose.
    private const string SteamCommonFolderWithSpace  = "Hades 2";   // UNVERIFIED
    private const string SteamCommonFolderWithoutSpace = "Hades2";  // UNVERIFIED

    // UNVERIFIED — Hades2.exe is the primary guess; Hades.exe tried as fallback.
    private const string PrimaryExeName  = "Hades2.exe";  // UNVERIFIED
    private const string FallbackExeName = "Hades.exe";   // UNVERIFIED fallback

    // Pinned fallback when the GitHub API is unreachable.
    private const string FallbackVersion = "0.1.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "hades_2";
    public string DisplayName => "Hades II";
    public string Subtitle    => "PC · Archipelago mod";

    // UNVERIFIED — check the `game = "..."` line in JFrog-55/H2_Archipelago
    // worlds/__init__.py to confirm the exact AP game string.
    public string ApWorldName => "Hades II"; // UNVERIFIED

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "hades_2.png");

    /// Deep blue — Hecate / lunar magic aesthetic; distinguishes from Hades 1's
    /// crimson (#8B1A1A) used in HadesPlugin.cs.
    public string ThemeAccentColor => "#1565C0";

    public string[] GameBadges => new[] { "Requires Hades II on Steam", "Early Access" };

    public string Description =>
        "Hades II (2024, Early Access) is the roguelike sequel from Supergiant " +
        "Games, following the witch Melinoë — sister of Zagreus — through the " +
        "Underworld, battling her way toward the Titan Chronos under the guidance " +
        "of Hecate. New weapons, a new cast of Olympian gods, and the expanded " +
        "world of Greek mythology make it a worthy successor to the acclaimed " +
        "original. The Archipelago mod by JFrog-55 shuffles boon unlocks, weapon " +
        "forms, and story progression across the multiworld — turning each escape " +
        "attempt into a hunt for items that may be held in other players' games. " +
        "Requires: your own legally-owned copy of Hades II on Steam. Install the " +
        "mod following the repository's instructions, then connect to your " +
        "Archipelago server via the in-game connection interface.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = the game exe is present AND a mod folder or mod DLL exists.
    public bool IsInstalled
    {
        get
        {
            try
            {
                string? dir = ResolveHades2Dir();
                if (dir == null) return false;
                if (!FindGameExe(dir, out _)) return false;
                return FindModSignature(dir);
            }
            catch { return false; }
        }
    }

    public bool IsRunning { get; private set; }

    // ── ConnectsItself / Standalone ───────────────────────────────────────────

    /// The H2_Archipelago mod connects to the AP server in-game; the launcher
    /// must NOT hold its own slot connection on top of it.
    public bool ConnectsItself => true;

    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Hades2");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "hades_2_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The H2_Archipelago mod handles the slot connection itself; launcher events
    // are interface stubs only (ConnectsItself = true).
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
        // 0. Locate Hades II. The mod must be extracted into the game directory.
        progress.Report((2, "Locating your Hades II installation..."));
        string? gameDir = ResolveHades2Dir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Hades II installation. Open this game's Settings " +
                "and pick your Hades II folder (the one containing Hades2.exe or " +
                "Hades.exe), or install Hades II via Steam first. " +
                "NOTE: Some technical details for Hades II are UNVERIFIED in this " +
                "plugin — see the Settings panel for details.");

        // 1. Resolve the latest mod release.
        progress.Report((6, "Checking the latest H2_Archipelago mod release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the H2_Archipelago mod download on GitHub. " +
                "Check your internet connection, or install the mod manually " +
                "from " + ModRepoUrl + ".");

        // 2. Download and extract the mod zip into the game directory.
        await DownloadAndExtractModAsync(zipUrl, version, gameDir, progress, ct);

        // 3. Stamp the version.
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool modOk = FindModSignature(gameDir);
        progress.Report((100,
            $"H2_Archipelago mod {version} installed into Hades II directory." +
            (modOk ? "" : " (Verify the mod files landed correctly.)") +
            " Connect to your Archipelago server via the in-game connection " +
            "interface once Hades II is running."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── Lifecycle — Launch (AP) ───────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // The H2_Archipelago mod takes connection details in-game (or via a config
        // file in the mod's directory). There is no documented command-line arg or
        // env-var prefill that this launcher can apply. We simply start the game and
        // surface the connection details in the settings panel for the user to enter
        // in-game. ConnectsItself = true: the launcher must NOT open its own slot.
        _ = session; // intentionally unused — no prefill mechanism verified
        StartHades2();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartHades2();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No AP password is written to disk by this plugin (connection is
        // entered in-game), so there is nothing to scrub.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert ────────────────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The H2_Archipelago mod receives items from the AP server directly.
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
            return "That folder does not exist. Pick your Hades II install folder.";

        if (LooksLikeHades2Dir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            foreach (string sub in new[] { SteamCommonFolderWithSpace, SteamCommonFolderWithoutSpace })
            {
                string nested = Path.Combine(folder, sub);
                if (LooksLikeHades2Dir(nested))
                    return null;
            }
        }
        catch { /* ignore */ }

        return "That does not look like a Hades II installation. Pick the folder " +
               "that contains Hades2.exe (or Hades.exe). " +
               "Note: some details about Hades II are UNVERIFIED in this plugin — " +
               "see the Settings panel. For Steam, the folder is usually under " +
               @"...\steamapps\common\Hades 2 (or Hades2).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var linkFg  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));
        var dark    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20));
        var border  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33));
        var btnBg   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30));
        var btnBd   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        // ── IMPORTANT: Hades II identity header ──────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "THIS IS HADES II (THE SEQUEL) — not the original Hades. " +
                   "The launcher ships a separate plugin for the original Hades " +
                   "(GameId: \"hades\"). This plugin uses GameId \"hades_2\" " +
                   "and the H2_Archipelago mod by JFrog-55.",
            FontSize = 11,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 10),
        });

        // ── UNVERIFIED details warning ────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "NOTICE: Several technical details for Hades II were UNVERIFIED " +
                   "when this plugin was written (the game was in Early Access). " +
                   "These unverified values are used as best-effort defaults:\n" +
                   "  • Steam AppId: " + H2_STEAM_APP_ID + " (verified — Hades II " +
                   "is 1145350; Hades 1 is 1145360)\n" +
                   "  • Steam folder name: \"Hades 2\" or \"Hades2\" (both tried)\n" +
                   "  • Exe name: Hades2.exe (Hades.exe tried as fallback)\n" +
                   "  • AP world game string: \"Hades II\" " +
                   "(verify in JFrog-55/H2_Archipelago __init__.py)\n\n" +
                   "If detection fails, use the folder picker below to select your " +
                   "Hades II install manually.",
            FontSize = 11,
            Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: install detection ────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "HADES II INSTALL",
            FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? gameDir    = ResolveHades2Dir();
        string? overrideDir = LoadOverrideDir();
        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Hades II not detected. Pick your install folder below, or install " +
              "Hades II via Steam. If the folder picker does not help, the Steam " +
              "AppId or folder name in this plugin may be incorrect (see notice above).";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg,
            FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // Exe detection status
        bool exeFound = gameDir != null && FindGameExe(gameDir, out _);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = exeFound
                    ? "Game executable found."
                    : "Game executable not found in the detected folder. " +
                      "The expected name is Hades2.exe (UNVERIFIED). " +
                      "If the file has a different name, the mod may still work " +
                      "but launch-from-exe will fall back to Steam URL.",
            FontSize = 11,
            Foreground = exeFound ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // Mod detection status
        bool modFound = gameDir != null && FindModSignature(gameDir);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modFound
                    ? "H2_Archipelago mod found in game directory."
                    : "H2_Archipelago mod not found yet. " +
                      "Click Install on the Play tab, or install the mod manually " +
                      "from the repository (link below).",
            FontSize = 11,
            Foreground = modFound ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "",
            IsReadOnly = true,
            FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = dark,
            Foreground  = fg,
            BorderBrush = border,
            ToolTip = "Your Hades II install folder (the one containing Hades2.exe). " +
                      "Detected from Steam automatically; set it here to override.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...",
            Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = btnBg,
            Foreground  = fg,
            BorderBrush = btnBd,
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select your Hades II install folder (contains Hades2.exe or Hades.exe)",
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
                    System.Windows.MessageBox.Show(bad, "Not a Hades II folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // Descend into sub-folder if user picked Steam "common" parent.
                if (!LooksLikeHades2Dir(picked))
                {
                    foreach (string sub in new[] { SteamCommonFolderWithSpace, SteamCommonFolderWithoutSpace })
                    {
                        string nested = Path.Combine(picked, sub);
                        if (LooksLikeHades2Dir(nested)) { picked = nested; break; }
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
            Text = "Steam installs are detected automatically (appid " + H2_STEAM_APP_ID + "). " +
                   "Use this picker if detection fails.",
            FontSize = 11,
            Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ───────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (in-game)",
            FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After installing the H2_Archipelago mod, launch Hades II and " +
                   "open the in-game connection interface. Enter your Server Address " +
                   "(e.g. archipelago.gg), Port (e.g. 38281), Slot Name, and Password " +
                   "if required, then connect. The exact in-game UI varies by mod " +
                   "version — see the mod repository's README for current instructions. " +
                   "This launcher cannot pre-fill the connection (it is entered in-game).",
            FontSize = 11,
            Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────────
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
            "1. Own Hades II on Steam (Early Access). Install it if you have not. " +
                "Use the folder picker above if auto-detection fails.",
            "2. Click Install on the Play tab. The launcher downloads and extracts " +
                "the H2_Archipelago mod into your Hades II folder.",
            "3. Alternatively: download the mod manually from the GitHub repository " +
                "(link below) and follow its README instructions.",
            "4. Launch Hades II via the Play button (or Steam directly).",
            "5. Open the in-game connection interface and enter your AP server details " +
                "(Server Address, Port, Slot Name, Password). The exact in-game menu " +
                "location is described in the mod repository's README.",
            "6. Generate your multiworld using a Hades II YAML on your AP server. " +
                "The AP world game string is \"Hades II\" (UNVERIFIED — check the repo).",
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

        // ── Links ─────────────────────────────────────────────────────────────
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
            ("H2_Archipelago mod (GitHub) ↗",    ModRepoUrl),
            ("H2_Archipelago releases ↗",         $"{ModRepoUrl}/releases"),
            ("Hades II AP game info ↗",           SetupGuideUrl),
            ("Hades II on Steam ↗",               $"https://store.steampowered.com/app/{H2_STEAM_APP_ID}/"),
            ("Archipelago Official ↗",             ArchipelagoSite),
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
                Foreground = linkFg,
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

    /// "v0.1.0" → "0.1.0"; trimmed as-is otherwise.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest H2_Archipelago mod release: version + zip download URL.
    /// Prefers any .zip asset; falls back to the pinned 0.1.0 direct URL when the
    /// GitHub API is unreachable.
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
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        return (version, url);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        // Pinned fallback URL — constructed from the known repo + version.
        string fallbackUrl =
            $"{ModRepoUrl}/releases/download/v{FallbackVersion}/H2_Archipelago_{FallbackVersion}.zip";
        return (FallbackVersion, fallbackUrl);
    }

    // ── Private helpers — Steam / Hades II detection ──────────────────────────

    /// The Hades II install dir to use: override (if set and valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolveHades2Dir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeHades2Dir(ov))
            return ov;

        try { return DetectSteamHades2Dir(); }
        catch { return null; }
    }

    /// A folder looks like Hades II if it contains Hades2.exe or Hades.exe.
    private static bool LooksLikeHades2Dir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, PrimaryExeName)))  return true;
            if (File.Exists(Path.Combine(dir, FallbackExeName))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Find the game exe in the given directory; returns true and sets exePath
    /// when found.
    private static bool FindGameExe(string dir, out string exePath)
    {
        string primary  = Path.Combine(dir, PrimaryExeName);
        string fallback = Path.Combine(dir, FallbackExeName);

        if (File.Exists(primary))  { exePath = primary;  return true; }
        if (File.Exists(fallback)) { exePath = fallback; return true; }

        exePath = primary; // report the expected name even if not found
        return false;
    }

    /// Returns true when any recognisable H2_Archipelago mod signature is
    /// present in the game directory (a mod folder, a mod DLL, or a mod config).
    private static bool FindModSignature(string dir)
    {
        try
        {
            // Look for a top-level "mods", "Mods", or "archipelago" folder, or
            // any DLL whose name contains "archipelago" or "h2ap" anywhere in the
            // directory tree (case-insensitive).
            foreach (string sub in new[] { "mods", "Mods", "archipelago", "Archipelago", "H2_Archipelago" })
            {
                if (Directory.Exists(Path.Combine(dir, sub))) return true;
            }
            foreach (string dll in Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll).ToLowerInvariant();
                if (name.Contains("archipelago") || name.Contains("h2ap")) return true;
            }
        }
        catch { /* permission / vanished */ }
        return false;
    }

    /// Detect the Steam Hades II install: read the Steam root from the registry,
    /// scan all library roots via libraryfolders.vdf, and check the appmanifest
    /// for appid H2_STEAM_APP_ID. Checks both "Hades 2" and "Hades2" folders.
    private static string? DetectSteamHades2Dir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{H2_STEAM_APP_ID}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");

                    // Prefer the installdir named in the manifest.
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeHades2Dir(candidate)) return candidate;
                    }

                    // Try both conventional folder names.
                    foreach (string name in new[] { SteamCommonFolderWithSpace, SteamCommonFolderWithoutSpace })
                    {
                        string conventional = Path.Combine(common, name);
                        if (LooksLikeHades2Dir(conventional)) return conventional;
                    }
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath + last-ditch conventional path).
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

    /// All Steam library roots: the Steam root itself plus every "path" entry in
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

    /// Pull every  "path"  "<value>"  pair from a libraryfolders.vdf body.
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

    /// Start Hades II: prefer the exe in the detected/override dir; fall back
    /// to the steam:// URL when the exe cannot be found.
    private void StartHades2()
    {
        string? dir = ResolveHades2Dir();
        bool hasExe = dir != null && FindGameExe(dir, out string exePath);

        if (hasExe)
        {
            FindGameExe(dir!, out exePath);
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exePath,
                WorkingDirectory = dir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Hades II.");

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

        // Fall back to Steam if we at least have a Steam installation.
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
            "Could not find the Hades II executable. Open this game's Settings " +
            "and pick your Hades II install folder, or install Hades II via Steam. " +
            "If the game exe has a different name than Hades2.exe (UNVERIFIED), " +
            "use the Steam launch fallback instead.",
            PrimaryExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the H2_Archipelago mod zip and extract it into the game directory.
    /// Existing files are overwritten but unrelated siblings are preserved.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string gameDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"h2archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"h2archipelago-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading H2_Archipelago {version}..."));
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
                        progress.Report((pct, $"Downloading H2_Archipelago... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Extracting mod files..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // If the zip wraps everything in a single sub-folder, unwrap it.
            string mergeRoot = tempExtract;
            string[] subdirs = Directory.GetDirectories(mergeRoot);
            string[] files   = Directory.GetFiles(mergeRoot);
            if (subdirs.Length == 1 && files.Length == 0)
                mergeRoot = subdirs[0];

            MergeDirectory(mergeRoot, gameDir);
            progress.Report((90, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))          File.Delete(tempZip); }          catch { }
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
            catch { /* locked file — skip; user can retry with game closed */ }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class H2Settings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private H2Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<H2Settings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(H2Settings s)
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
