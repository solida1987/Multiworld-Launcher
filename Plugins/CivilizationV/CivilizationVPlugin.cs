using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using LauncherV2.Core;

// NOTE on type qualification (BUILD GOTCHA — CS0104):
// This project sets BOTH <UseWPF>true</UseWPF> and <UseWindowsForms>true</UseWindowsForms>.
// That makes a long list of simple type names ambiguous between WPF and WinForms
// (Clipboard, MessageBox, Application, Color, Brush(es), Button, TextBox, CheckBox,
// Orientation, FontWeights, HorizontalAlignment, Cursors, Thickness, OpenFileDialog…).
// To avoid CS0104 this file deliberately does NOT do `using System.Windows.Controls;` /
// `using System.Windows.Media;` — every WPF UI type below is written FULLY QUALIFIED
// (System.Windows.Controls.*, System.Windows.Media.*, System.Windows.MessageBox, …).
// It also does NOT declare any file-level `using X = System.Windows...;` alias
// (CS1537 — GlobalUsings.cs already aliases the short names; a local alias would
// conflict). Bare names from GlobalUsings, or full qualification, only.
// (OpenFolderDialog is unambiguous — it lives only in Microsoft.Win32 — so it is
// referenced by its short name with `using Microsoft.Win32;` above.)

namespace LauncherV2.Plugins.CivilizationV;

// ═══════════════════════════════════════════════════════════════════════════════
// CivilizationVPlugin — detect / stage-mod / guide / launch for
// "Sid Meier's Civilization V" (Firaxis / 2K, 2010) played through its
// community Archipelago integration. This is a NATIVE "ConnectsItself"
// integration: the Civ V mod's built-in AP client connects to the slot
// directly from inside the game, without a separate relay process. The
// launcher must NOT hold its own ApClient on the same slot while the game
// runs (it would kick the in-game client off).
//
// ── REALITY CHECK (2026-06-14) — verified against the AP world at
//    github.com/1313e/Civ-V-AP-World ──────────────────────────────────────────
//
//   * THE BASE GAME is the user's own legally-owned Civilization V (Steam
//     appid 8930, Firaxis/2K). Paid software — the launcher does not ship it.
//     The Gods & Kings and Brave New World DLCs are required for the full
//     location pool (the apworld targets Brave New World).
//
//   * THE MOD is the Civ V AP World mod (github.com/1313e/Civ-V-AP-World).
//     It is NOT bundled inside the Archipelago release; it ships as its own
//     GitHub release. The user downloads the latest release, unzips it into
//     the Civ V user Mods folder:
//         %USERPROFILE%\Documents\My Games\Sid Meier's Civilization 5\MODS\
//     so the result is:
//         ...\MODS\<ModFolderName>   (contains a .modinfo file)
//     The mod contains its own built-in Archipelago client that connects from
//     inside the game — no separate relay process is required or expected.
//
//   * CONNECTION is done entirely in-game via the mod's main menu UI:
//     the player enters their AP server address, slot name, and password
//     directly in Civ V before starting a game. There is no config file this
//     launcher can pre-write and no command-line arg the game accepts.
//     Hence ConnectsItself = true.
//
//   * INSTALL & ENABLE: after placing the mod folder, the player must enable
//     it in the Civ V in-game mod manager (Main Menu → Mods → enable the AP
//     mod) before starting a game.
//
// ── WHAT THIS PLUGIN HONESTLY DOES ──────────────────────────────────────────
//   1. DETECT the Steam Civilization V install (appid 8930) via the standard
//      registry → libraryfolders.vdf → appmanifest_8930.acf → common pipeline.
//      A manual root-dir OVERRIDE (folder picker) takes precedence.
//   2. DETECT the user's Civ V Mods folder under Documents (and OneDrive-
//      redirected variants) and whether the AP mod is present there yet.
//      A manual Mods-folder OVERRIDE is also supported.
//   3. AUTO-STAGE THE MOD when possible. If a copy of the mod is found next
//      to the launcher (Games/Mods/<ApModFolderName> with a .modinfo), it is
//      COPIED into the Documents MODS folder. Otherwise the plugin guides the
//      user to download from the GitHub releases page. The user's Documents
//      folder is writable so this is safe.
//   4. LAUNCH: start Civilization V via Steam (or direct exe). The mod's
//      built-in client handles the AP connection entirely in-game.
//      SupportsStandalone = true (plain Civ V runs without AP).
//   5. NEVER modify the Steam install; keep settings in its OWN sidecar.
//
// ── AP CONNECTION NOTE ───────────────────────────────────────────────────────
//   The AP connection is entered in-game via the mod's UI (server, slot,
//   password). The launcher does NOT connect to the slot itself, does NOT
//   pre-write a credentials file, and does NOT start a separate relay client.
//   When LaunchAsync is called with an ApSession, the connection details are
//   written to a JSON "connection hint" file in the mod folder so the mod can
//   pre-fill the fields — if the mod supports reading that file. If it does
//   not, the user simply types the details in-game.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CivilizationVPlugin : IGamePlugin
{
    // ── Constants — Steam / game facts ───────────────────────────────────────

    /// Civilization V's Steam application id (verified: store.steampowered.com/app/8930).
    private const string SteamAppId = "8930";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The standard Steam install sub-folder name for Civ V.
    private const string SteamCommonFolderName = "Sid Meier's Civilization V";

    /// Candidate game exe names inside the Civ V install (used for direct-launch
    /// fallback and install-folder validation).
    private static readonly string[] GameExeNames =
    {
        "CivilizationV_DX11.exe",
        "CivilizationV.exe",
        "Civ5.exe",
    };

    /// Process names to check for IsRunning.
    private static readonly string[] GameProcessNames =
    {
        "CivilizationV_DX11",
        "CivilizationV",
        "Civ5",
    };

    /// The Documents sub-path Firaxis uses for Civ V user data (Mods + saves live here).
    private const string MyGamesVendorFolder = "My Games";
    private const string MyGamesGameFolder   = "Sid Meier's Civilization 5";
    private const string ModsFolderName      = "MODS";

    /// The AP mod's folder name once unzipped into the MODS folder.
    /// The launcher looks for any folder under MODS that contains a .modinfo file
    /// whose content references "Archipelago" — or falls back to the known stable
    /// folder name from the GitHub releases.
    private const string ApModFolderName   = "CivVArchipelago";
    private const string ModInfoExtension  = ".modinfo";

    /// Connection hint file written into the mod folder when LaunchAsync is called
    /// with a real ApSession (so the mod can pre-fill the connection UI if it
    /// supports reading this file).
    private const string ConnectionHintFile = "ap_connection.json";

    /// GitHub API for the AP World repo releases (news + update check).
    private const string GitHubReleasesApiUrl =
        "https://api.github.com/repos/1313e/Civ-V-AP-World/releases/latest";

    private const string GitHubAllReleasesApiUrl =
        "https://api.github.com/repos/1313e/Civ-V-AP-World/releases";

    private const string ModReleasesUrl =
        "https://github.com/1313e/Civ-V-AP-World/releases/latest";

    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Civilization%20V/setup/en";

    private const string SteamStoreUrl =
        "https://store.steampowered.com/app/8930/Sid_Meiers_Civilization_V/";

    private const string ArchipelagoSite = "https://archipelago.gg";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } },
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "civ_5";
    public string DisplayName => "Civilization V";
    public string Subtitle    => "Native PC · Archipelago";

    /// Exact AP game string — verified against the 1313e/Civ-V-AP-World world.
    public string ApWorldName => "Civilization V";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "civ_5.png");

    public string ThemeAccentColor => "#1A6B1A";   // civilization green

    public string[] GameBadges =>
        new[] { "Steam", "CivV Mod", "ConnectsItself" };

    public string Description =>
        "Sid Meier's Civilization V is Firaxis's turn-based 4X strategy game: build a " +
        "civilization to stand the test of time. The Brave New World expansion is " +
        "required for the full Archipelago location pool. Install the community Civ V " +
        "AP Mod via this launcher (it goes into your Documents MODS folder), enable it " +
        "in the in-game mod manager, and connect to Archipelago directly from the mod's " +
        "main menu UI — the mod includes its own built-in AP client. The launcher " +
        "detects your install, stages the mod, and launches the game; you enter your " +
        "server and slot name in-game.";

    public string? VideoPreviewUrl  => null;
    public string[] ScreenshotUrls  => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    /// Installed version = the tag from a version.txt inside the staged mod folder,
    /// or null when the mod is not present.
    public string? InstalledVersion => ReadInstalledModVersion();

    /// Latest available version fetched from the GitHub releases API.
    public string? AvailableVersion { get; private set; }

    /// "Installed" = the AP mod folder has been placed into the Civ V MODS folder
    /// and contains at least one .modinfo file.
    public bool IsInstalled => IsModInstalled();

    /// True while a Civ V process is running (polled via Process.GetProcessesByName).
    public bool IsRunning
    {
        get
        {
            if (_gameProcess != null && !_gameProcess.HasExited) return true;
            foreach (string pname in GameProcessNames)
                if (Process.GetProcessesByName(pname).Length > 0) return true;
            return false;
        }
    }

    /// The detected / override Civ V ROOT (Steam) directory, or "" when unknown.
    public string GameDirectory => ResolveCiv5RootDir() ?? "";

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// This plugin's own settings sidecar — kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained source file.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "civ5_launcher.json");

    /// Optional bundled-mod staging source next to the launcher.
    /// If the user drops the unzipped mod here with a .modinfo, the plugin can copy
    /// it into the Documents MODS folder.
    private string BundledModSourceDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "Mods", ApModFolderName);

    // ── Override state (persisted in sidecar) ─────────────────────────────────

    private string? _overrideRootDir;   // Civ V install root (Steam dir)
    private string? _overrideModsDir;   // Documents MODS folder

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The mod's built-in client owns the AP slot; the launcher relays nothing.
    // These exist only for interface compatibility (ConnectsItself = true).

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
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync("1313e", "Civ-V-AP-World", ct));
        }
        catch
        {
            AvailableVersion = null; // contract: never throw on network failure
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────
    // Stages the mod from a bundled copy next to the launcher when available;
    // otherwise guides the user. Never downloads the paid base game. Never
    // writes to ProgramData or the Steam install directory.

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((10, "Checking for your Civilization V install and MODS folder..."));

        string? rootDir  = ResolveCiv5RootDir();
        string? modsDir  = ResolveModsDir();
        bool    modReady = IsModInstalled();

        if (modReady)
        {
            progress.Report((100,
                "The Civilization V Archipelago mod is already installed at:\n" +
                (ModInstallDir() ?? modsDir ?? "your MODS folder") + "\n\n" +
                "To play: launch Civ V, go to Main Menu → Mods, enable the " +
                "Archipelago mod, then start a Single Player game. After the game " +
                "loads, use the mod's connection menu to enter your Archipelago " +
                "server address, slot name, and password."));
            return Task.CompletedTask;
        }

        var sb = new StringBuilder();
        bool staged = false;

        if (modsDir != null && Directory.Exists(BundledModSourceDir))
        {
            progress.Report((50, "Staging the Archipelago mod into your Civ V MODS folder..."));
            try
            {
                staged = TryStageBundledMod(modsDir);
            }
            catch { staged = false; }
        }

        if (staged)
        {
            sb.Append("Staged the Civilization V Archipelago mod into:\n\"")
              .Append(ModInstallDir() ?? modsDir)
              .Append("\"\n\n");
        }
        else
        {
            sb.Append("The Civilization V Archipelago mod is not yet installed. ")
              .Append("Download the latest release from:\n")
              .Append(ModReleasesUrl)
              .Append("\nUnzip it into your Civ V MODS folder so you end up with:\n")
              .Append("...\\MODS\\").Append(ApModFolderName).Append(" (a folder containing a .modinfo file). ");
            if (modsDir != null)
                sb.Append("Your MODS folder is:\n\"").Append(modsDir).Append("\"\n\n");
            else
                sb.Append("Your MODS folder is usually:\n")
                  .Append("%USERPROFILE%\\Documents\\My Games\\Sid Meier's Civilization 5\\MODS\n")
                  .Append("(or under OneDrive\\Documents if Known Folder redirection is on)\n\n");
        }

        if (rootDir != null)
            sb.Append("Your Steam Civ V was detected at:\n\"").Append(rootDir).Append("\"\n\n");
        else
            sb.Append("Civilization V (Steam appid ").Append(SteamAppId)
              .Append(") was not detected automatically. Install it via Steam with the ")
              .Append("Brave New World expansion, or use the folder picker in Settings.\n\n");

        sb.Append("After placing the mod: in Civ V, go to Main Menu → Mods, enable the ")
          .Append("Archipelago mod, start a Single Player game, then enter your ")
          .Append("Archipelago connection details in the mod's UI.");

        progress.Report((100, sb.ToString()));
        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public Task<bool> VerifyInstallAsync(CancellationToken ct = default)
        => Task.FromResult(IsInstalled);

    // ── Existing-install validation (folder picker) ───────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Civilization V install folder " +
                   "(the one containing CivilizationV_DX11.exe or CivilizationV.exe).";

        if (LooksLikeCiv5Root(folder)) return null;

        // Be forgiving: user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeCiv5Root(nested)) return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Civilization V installation. Pick the folder " +
               "that contains CivilizationV_DX11.exe. For Steam this is usually:\n" +
               @"...\steamapps\common\Sid Meier's Civilization V";
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession? session, CancellationToken ct = default)
    {
        // Write a connection hint file so the mod can pre-fill the connection UI.
        if (session != null)
        {
            try { WriteConnectionHint(session); }
            catch { /* non-fatal */ }
        }

        try { StartCiv5(); }
        catch { /* non-fatal */ }

        return Task.CompletedTask;
    }

    /// Civ V is a complete game — standalone (non-AP) play is fully supported.
    public bool SupportsStandalone => true;

    /// The mod's built-in client owns the AP slot — the launcher must NOT also
    /// connect to the same slot while the game runs.
    public bool ConnectsItself => true;

    /// No datapackage checksum coupling (the mod's AP client handles this internally).
    public string? BuiltAgainstDataPackageChecksum => null;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartCiv5();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert ─────────────────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index,
        CancellationToken ct = default)
    {
        // Items are relayed by the mod's built-in client; nothing to forward here.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod's client renders its own connection state.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x6B, 0x1A));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD0, 0xA0, 0x40));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20),
        };

        // ── Honesty / overview header ─────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Civilization V's Archipelago support works through a community mod " +
                   "(github.com/1313e/Civ-V-AP-World). The mod ships its own built-in AP " +
                   "client — connection to your Archipelago server is done entirely in-game " +
                   "via the mod's main menu UI. The launcher detects your Steam install, " +
                   "can stage the mod into your Documents MODS folder, and launches the " +
                   "game. You then enable the mod in Civ V's mod manager and enter your " +
                   "server / slot name in-game. The Brave New World expansion is required " +
                   "for the full location pool.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: Civ V Steam install ──────────────────────────────────────
        panel.Children.Add(SectionHeader("CIVILIZATION V INSTALL (STEAM ROOT DIRECTORY)", muted));

        string? rootDir = ResolveCiv5RootDir();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = rootDir != null
                ? "Detected (Steam appid " + SteamAppId + "):\n" + rootDir
                : "Not detected via Steam. Install Civilization V (appid " + SteamAppId +
                  ") with the Brave New World expansion, or set the folder below " +
                  "(non-standard Steam library / other platform).",
            FontSize = 11, Foreground = rootDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var rootRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        };
        var rootBox = new System.Windows.Controls.TextBox
        {
            Text = _overrideRootDir ?? rootDir ?? "",
            IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Your Civilization V install folder (contains CivilizationV_DX11.exe). " +
                      "Detected from Steam automatically; use the button to override.",
        };
        var rootBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        rootBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select your Civilization V install folder (contains CivilizationV_DX11.exe)",
                InitialDirectory = Directory.Exists(_overrideRootDir ?? rootDir ?? "")
                                   ? (_overrideRootDir ?? rootDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Civilization V folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeCiv5Root(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeCiv5Root(nested)) picked = nested;
                }
                _overrideRootDir = picked;
                rootBox.Text = picked;
                SaveRootDirOverride(picked);
            }
        };
        System.Windows.Controls.DockPanel.SetDock(rootBtn, System.Windows.Controls.Dock.Right);
        rootRow.Children.Add(rootBtn);
        rootRow.Children.Add(rootBox);
        panel.Children.Add(rootRow);

        // ── Section: MODS folder + mod status ────────────────────────────────
        panel.Children.Add(SectionHeader("CIV V MODS FOLDER (DOCUMENTS) & ARCHIPELAGO MOD", muted));

        string? modsDir  = ResolveModsDir();
        bool    modReady = IsModInstalled();
        string? modDir   = ModInstallDir();

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modReady
                ? "Archipelago mod found:\n" + (modDir ?? modsDir ?? "your MODS folder")
                : (modsDir != null
                    ? "MODS folder detected:\n" + modsDir + "\n\nThe Archipelago mod (" +
                      ApModFolderName + ") is not there yet. Use \"Stage bundled mod\" " +
                      "below, or download the mod and unzip it into the folder above."
                    : "MODS folder not detected. It is usually:\n" +
                      "%USERPROFILE%\\Documents\\My Games\\Sid Meier's Civilization 5\\MODS\n" +
                      "(or under OneDrive\\Documents if Known Folder redirection is on). " +
                      "Set it below."),
            FontSize = 11, Foreground = modReady ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        if (modReady && InstalledVersion != null)
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Installed mod version: " + InstalledVersion +
                       (AvailableVersion != null && AvailableVersion != InstalledVersion
                           ? "  (update available: " + AvailableVersion + ")"
                           : ""),
                FontSize = 11, Foreground = fg,
                Margin = new System.Windows.Thickness(0, 0, 0, 6),
            });
        }

        var modsRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        };
        var modsBox = new System.Windows.Controls.TextBox
        {
            Text = _overrideModsDir ?? modsDir ?? "",
            IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Your Civ V MODS folder (Documents\\My Games\\Sid Meier's Civilization 5\\MODS). " +
                      "Detected automatically; use the button to override.",
        };
        var modsBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        modsBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select your Civ V MODS folder (…\\Sid Meier's Civilization 5\\MODS)",
                InitialDirectory = Directory.Exists(_overrideModsDir ?? modsDir ?? "")
                                   ? (_overrideModsDir ?? modsDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                _overrideModsDir = dlg.FolderName;
                modsBox.Text     = dlg.FolderName;
                SaveModsDirOverride(dlg.FolderName);
            }
        };
        System.Windows.Controls.DockPanel.SetDock(modsBtn, System.Windows.Controls.Dock.Right);
        modsRow.Children.Add(modsBtn);
        modsRow.Children.Add(modsBox);
        panel.Children.Add(modsRow);

        // Mod action buttons: stage + open folder.
        var modsActions = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            Margin = new System.Windows.Thickness(0, 4, 0, 12),
        };

        bool hasBundledMod = Directory.Exists(BundledModSourceDir);
        var stageBtn = new System.Windows.Controls.Button
        {
            Content   = hasBundledMod ? "Stage bundled mod" : "Stage bundled mod (none found)",
            Padding   = new System.Windows.Thickness(10, 6, 10, 6),
            Margin    = new System.Windows.Thickness(0, 0, 8, 0),
            IsEnabled = hasBundledMod,
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
            ToolTip = "If you placed the unzipped mod next to the launcher " +
                      "(Games\\Mods\\" + ApModFolderName + "), copy it into your MODS folder.",
        };
        stageBtn.Click += (_, _) =>
        {
            string? md = ResolveModsDir();
            if (md == null)
            {
                System.Windows.MessageBox.Show(
                    "Could not find your Civ V MODS folder. Set it above first.",
                    "MODS folder not set",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }
            try
            {
                bool ok = TryStageBundledMod(md);
                System.Windows.MessageBox.Show(
                    ok  ? "Staged the Archipelago mod into:\n" + (ModInstallDir() ?? md)
                        : "No bundled mod copy was found next to the launcher " +
                          "(Games\\Mods\\" + ApModFolderName + "). Download the mod " +
                          "from the GitHub releases link in Settings and unzip it into " +
                          "your MODS folder.",
                    ok  ? "Mod staged" : "Nothing to stage",
                    System.Windows.MessageBoxButton.OK,
                    ok  ? System.Windows.MessageBoxImage.Information
                        : System.Windows.MessageBoxImage.Warning);
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show(
                    "Could not stage the mod: " + ex.Message,
                    "Staging failed",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        };

        var openModsBtn = new System.Windows.Controls.Button
        {
            Content = "Open MODS folder",
            Padding = new System.Windows.Thickness(10, 6, 10, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        openModsBtn.Click += (_, _) =>
        {
            string? md = ResolveModsDir();
            try
            {
                if (md != null)
                {
                    Directory.CreateDirectory(md);
                    Process.Start(new ProcessStartInfo(md) { UseShellExecute = true });
                }
            }
            catch { /* non-fatal */ }
        };

        modsActions.Children.Add(stageBtn);
        modsActions.Children.Add(openModsBtn);
        panel.Children.Add(modsActions);

        // ── Section: how to connect ───────────────────────────────────────────
        panel.Children.Add(SectionHeader("SETUP & CONNECTION STEPS", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "1) Own and install Civilization V (Steam appid " + SteamAppId + ") with " +
                "the Brave New World expansion (required for the full location pool).\n\n" +
                "2) Download the latest Civ V AP Mod release from the GitHub link below. " +
                "Unzip it into your Civ V MODS folder so you end up with:\n" +
                "    ...\\MODS\\" + ApModFolderName + "\n" +
                "(a folder containing a .modinfo file) — or use \"Stage bundled mod\" " +
                "above if you placed a copy next to the launcher.\n\n" +
                "3) Launch Civ V (press Play here or launch it directly). Go to:\n" +
                "    Main Menu → Mods\n" +
                "Enable the Archipelago mod, then click \"Next\" to start.\n\n" +
                "4) In the Archipelago mod's menu, enter your server address, slot name, " +
                "and password. The mod connects directly — no separate client needed.\n\n" +
                "5) Start a game on the ruleset required by your multiworld seed. " +
                "The mod will automatically receive items and report checks to your server.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Civilization V AP Mod (download) ↗",  ModReleasesUrl),
            ("Civilization V on Steam ↗",            SteamStoreUrl),
            ("Archipelago Setup Guide ↗",            SetupGuideUrl),
            ("Archipelago Official ↗",               ArchipelagoSite),
        })
        {
            string u = url;
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background      = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0),
                FontSize        = 12,
                Margin          = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground      = new System.Windows.Media.SolidColorBrush(
                                      System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            btn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); }
                catch { /* non-fatal */ }
            };
            panel.Children.Add(btn);
        }

        return panel;
    }

    private static System.Windows.Controls.TextBlock SectionHeader(
        string text, System.Windows.Media.Brush muted)
        => new System.Windows.Controls.TextBlock
        {
            Text       = text,
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        };

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GitHubAllReleasesApiUrl, ct);
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
                    Version: el.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — mod (Documents MODS folder) detection + staging ─────

    /// Full path to the expected AP mod folder inside the MODS directory, or null
    /// when the MODS folder cannot be determined.
    private string? ModInstallDir()
    {
        string? mods = ResolveModsDir();
        return mods == null ? null : Path.Combine(mods, ApModFolderName);
    }

    /// True when the AP mod is present in the Civ V MODS folder (the folder exists
    /// AND contains at least one .modinfo file).
    private bool IsModInstalled()
    {
        try
        {
            string? dir = ModInstallDir();
            if (dir == null || !Directory.Exists(dir)) return false;
            return DirHasModInfo(dir);
        }
        catch { return false; }
    }

    /// Read the installed mod version from a version.txt inside the mod folder.
    /// Returns null when not present or unreadable.
    private string? ReadInstalledModVersion()
    {
        try
        {
            string? dir = ModInstallDir();
            if (dir == null || !Directory.Exists(dir)) return null;

            string versionFile = Path.Combine(dir, "version.txt");
            if (!File.Exists(versionFile)) return null;

            string v = File.ReadAllText(versionFile, Encoding.UTF8).Trim();
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        catch { return null; }
    }

    /// True when `dir` contains at least one *.modinfo file (top-level only).
    private static bool DirHasModInfo(string dir)
    {
        try
        {
            foreach (string _ in Directory.EnumerateFiles(
                dir, "*" + ModInfoExtension, SearchOption.TopDirectoryOnly))
                return true;
        }
        catch { /* unreadable */ }
        return false;
    }

    /// Copy the optional bundled mod (Games/Mods/<ApModFolderName> next to the
    /// launcher) into the given MODS directory. Returns true if a mod was staged
    /// (destination now has a .modinfo), false if there was no valid bundled source.
    /// Existing files are overwritten so re-staging acts as an update.
    private bool TryStageBundledMod(string modsDir)
    {
        if (!Directory.Exists(BundledModSourceDir)) return false;

        if (!DirHasModInfo(BundledModSourceDir))
        {
            // Maybe the user dropped the zip's inner folder one level down.
            string? nested = FindNestedModFolder(BundledModSourceDir);
            if (nested == null) return false;
            string destN = Path.Combine(modsDir, ApModFolderName);
            CopyDirectory(nested, destN);
            return DirHasModInfo(destN);
        }

        string dest = Path.Combine(modsDir, ApModFolderName);
        CopyDirectory(BundledModSourceDir, dest);
        return DirHasModInfo(dest);
    }

    /// Find the first sub-folder of `root` that contains a .modinfo file (handles
    /// the case where the user drops the zip's wrapper folder here). Null if none.
    private static string? FindNestedModFolder(string root)
    {
        try
        {
            foreach (string sub in Directory.EnumerateDirectories(root))
                if (DirHasModInfo(sub)) return sub;
        }
        catch { /* unreadable */ }
        return null;
    }

    /// Recursive directory copy (overwrite). Used only for staging into the writable
    /// Documents MODS folder.
    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            string target = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, target, overwrite: true);
        }
        foreach (string sub in Directory.EnumerateDirectories(sourceDir))
        {
            string target = Path.Combine(destDir, Path.GetFileName(sub));
            CopyDirectory(sub, target);
        }
    }

    /// Write a JSON connection hint file into the mod folder so the mod can
    /// pre-fill the connection UI (if it supports this). Non-fatal if it fails.
    private void WriteConnectionHint(ApSession session)
    {
        string? modDir = ModInstallDir();
        if (modDir == null || !Directory.Exists(modDir)) return;

        string hintPath = Path.Combine(modDir, ConnectionHintFile);
        var hint = new
        {
            server   = session.ServerUri,
            slot     = session.SlotName,
            password = session.Password,
        };
        string json = JsonSerializer.Serialize(hint,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(hintPath, json, new UTF8Encoding(false));
    }

    // ── Private helpers — MODS folder detection ───────────────────────────────

    /// The Civ V MODS folder to use: the override (if set and valid) wins, else the
    /// first existing Documents/OneDrive candidate, else the conventional path (so
    /// staging can create the folder). Null only when no Documents root is found.
    private string? ResolveModsDir()
    {
        if (!string.IsNullOrWhiteSpace(_overrideModsDir) &&
            Directory.Exists(_overrideModsDir))
            return _overrideModsDir;

        string? firstExisting  = null;
        string? firstCandidate = null;
        foreach (string cand in EnumerateModsCandidates())
        {
            firstCandidate ??= cand;
            if (Directory.Exists(cand)) { firstExisting = cand; break; }
        }
        return firstExisting ?? firstCandidate;
    }

    /// Candidate Civ V MODS directories: under the user's Documents special folder
    /// (respects Known Folder redirection), under %USERPROFILE%\Documents, and under
    /// common OneDrive layouts. All of the form:
    ///   <docs>\My Games\Sid Meier's Civilization 5\MODS
    private static IEnumerable<string> EnumerateModsCandidates()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? docs in EnumerateDocumentsRoots())
        {
            if (string.IsNullOrWhiteSpace(docs)) continue;
            string p = Path.Combine(docs, MyGamesVendorFolder,
                                    MyGamesGameFolder, ModsFolderName);
            if (seen.Add(p)) yield return p;
        }
    }

    /// Candidate "Documents" roots (redirection-aware):
    ///   MyDocuments special folder, %USERPROFILE%\Documents,
    ///   %OneDrive%\Documents, %OneDriveConsumer%\Documents,
    ///   %OneDriveCommercial%\Documents, %USERPROFILE%\OneDrive\Documents.
    private static IEnumerable<string> EnumerateDocumentsRoots()
    {
        string? myDocs = null;
        try { myDocs = Environment.GetFolderPath(
                           Environment.SpecialFolder.MyDocuments); } catch { }
        if (!string.IsNullOrWhiteSpace(myDocs)) yield return myDocs;

        string? userProfile = null;
        try { userProfile = Environment.GetFolderPath(
                                Environment.SpecialFolder.UserProfile); } catch { }
        if (!string.IsNullOrWhiteSpace(userProfile))
        {
            yield return Path.Combine(userProfile, "Documents");
            yield return Path.Combine(userProfile, "OneDrive", "Documents");
        }

        foreach (string envVar in new[]
            { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            string? od = null;
            try { od = Environment.GetEnvironmentVariable(envVar); } catch { }
            if (!string.IsNullOrWhiteSpace(od))
                yield return Path.Combine(od, "Documents");
        }
    }

    // ── Private helpers — Civ V ROOT (Steam) detection ───────────────────────

    /// The Civ V ROOT (install) dir to use: the override (if valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolveCiv5RootDir()
    {
        if (!string.IsNullOrWhiteSpace(_overrideRootDir) &&
            LooksLikeCiv5Root(_overrideRootDir))
            return _overrideRootDir;

        try { return DetectSteamCiv5Dir(); }
        catch { return null; }
    }

    /// A folder "looks like" Civ V if it contains one of the known Civ V exes.
    private static bool LooksLikeCiv5Root(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            foreach (string exe in GameExeNames)
                if (File.Exists(Path.Combine(dir, exe))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Civ V install via the standard pipeline:
    ///   Steam registry root → libraryfolders.vdf → appmanifest_8930.acf
    ///   → steamapps\common\<installdir>
    private static string? DetectSteamCiv5Dir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps,
                                           $"appmanifest_{SteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeCiv5Root(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeCiv5Root(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots (registry + conventional fallback).
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
            if (norm.Length > 0 && seen.Add(norm)) yield return norm;
        }
    }

    /// Pull every  "path"  "<value>"  pair out of a libraryfolders.vdf body.
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
            yield return text.Substring(open + 1, close - open - 1)
                             .Replace("\\\\", "\\");
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
            int open  = text.IndexOf('"', i);
            if (open < 0) return null;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) return null;
            return text.Substring(open + 1, close - open - 1).Replace("\\\\", "\\");
        }
        catch { return null; }
    }

    /// Safe registry string read; null on any failure.
    private static string? ReadRegistryString(RegistryKey hive,
        string subKey, string valueName)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(subKey);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Civ V: prefer a known exe in the detected/override root; fall back to
    /// steam://rungameid/8930 when Steam is present but no exe path was resolved.
    private void StartCiv5()
    {
        string? root = ResolveCiv5RootDir();
        string? exe  = null;
        if (root != null)
        {
            foreach (string name in GameExeNames)
            {
                string cand = Path.Combine(root, name);
                if (File.Exists(cand)) { exe = cand; break; }
            }
        }

        if (exe != null)
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = root!,
                UseShellExecute  = true,
            });
            if (proc != null)
            {
                _gameProcess = proc;
                try
                {
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (_, _) =>
                    {
                        _gameProcess = null;
                        GameExited?.Invoke(proc.ExitCode);
                    };
                }
                catch { /* some processes don't expose Exited — non-fatal */ }
            }
            return;
        }

        // Fall back to Steam.
        if (SteamIsInstalled())
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl)
                {
                    UseShellExecute = true,
                });
                // Steam owns the process; we cannot track its exit.
                return;
            }
            catch { /* fall through */ }
        }

        throw new FileNotFoundException(
            "Could not find CivilizationV_DX11.exe. Open Settings and pick your " +
            "Civilization V install folder, or install Civilization V via Steam " +
            "(appid " + SteamAppId + ").",
            GameExeNames[0]);
    }

    private static bool SteamIsInstalled()
    {
        foreach (string r in SteamRoots())
        {
            try { if (!string.IsNullOrWhiteSpace(r) && Directory.Exists(r)) return true; }
            catch { /* ignore */ }
        }
        return false;
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // BOM-less UTF-8 JSON in Games/ROMs/civ_5/. Does NOT touch Core/SettingsStore.

    private sealed class Civ5Settings
    {
        /// User override for the Civ V install (root) folder.
        public string? RootDirOverride { get; set; }

        /// User override for the Civ V Documents MODS folder.
        public string? ModsDirOverride { get; set; }
    }

    private Civ5Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Civ5Settings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(Civ5Settings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s,
                    new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    private void SaveRootDirOverride(string dir)
    {
        var s = LoadSettings();
        s.RootDirOverride = dir;
        SaveSettings(s);
    }

    private void SaveModsDirOverride(string dir)
    {
        var s = LoadSettings();
        s.ModsDirOverride = dir;
        SaveSettings(s);
    }
}
