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

namespace LauncherV2.Plugins.ClairObscur;

// ═══════════════════════════════════════════════════════════════════════════════
// ClairObscurPlugin — install / launch for "Clair Obscur: Expedition 33" (2025)
// via the ClairObscur_APWorld mod by Demorck, which contains the in-game
// Archipelago client. This is a NATIVE "ConnectsItself" integration in the same
// family as the shipped AShortHike / Blasphemous / Hollow Knight / TUNIC plugins:
// the game itself speaks to the AP server (no emulator, no Lua bridge, no
// launcher-held ApClient on the slot).
//
// Clair Obscur: Expedition 33 is a 2025 JRPG by Sandfall Interactive — a
// painterly, melancholic turn-based RPG set in a world where a Painter inscribes
// a number each year and everyone of that age perishes. You lead Expedition 33,
// a group of survivors who march to end the Painter. The AP mod by Demorck
// shuffles key items, abilities, and story progression across the multiworld.
//
// ── !! UNVERIFIED DETAILS — THIS IS A VERY NEW GAME (2025) !! ──────────────────
// Several technical details below are UNVERIFIED and may need correction:
//
//   * STEAM APPID: 2925360 — UNVERIFIED. Confirm against the Steam store page
//     for "Clair Obscur: Expedition 33" before releasing.
//
//   * STEAM COMMON FOLDER NAME: "Clair Obscur Expedition 33" — UNVERIFIED. The
//     actual Steam installdir value in the appmanifest_2925360.acf may differ
//     (e.g. "ClairObscur", "Expedition33", or some other variant). Check the
//     actual steamapps/common/ subfolder on a real install.
//
//   * EXECUTABLE NAME: "Expedition33.exe" — UNVERIFIED. This is the best guess
//     based on the project/subtitle naming convention for Unreal Engine titles.
//     The real exe may be "ClairObscur.exe", "ClairObscurExpedition33.exe", or
//     something else entirely. Check a real install.
//
//   * AP WORLD GAME STRING: "Clair Obscur: Expedition 33" — UNVERIFIED. Check
//     against the `game` field in Demorck/ClairObscur_APWorld/__init__.py before
//     generating any multiworld with this integration.
//
//   * CONNECTION METHOD: The exact in-game connection flow is UNVERIFIED. The mod
//     likely provides an in-game connection panel (typical for AP mods of this
//     type), but the precise steps should be confirmed against the mod's README.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Clair Obscur install via the Windows registry (HKCU\
//      Software\Valve\Steam -> SteamPath, and HKLM\...\WOW6432Node\Valve\Steam
//      -> InstallPath), parsing steamapps\libraryfolders.vdf for every library
//      root and locating steamapps\common\<folder> via appmanifest_2925360.acf.
//      A manual install-dir OVERRIDE (settings folder picker) is also supported
//      and takes precedence; it is validated (must contain Expedition33.exe) and
//      persisted in this plugin's OWN sidecar
//      (Games/ROMs/clair_obscur/clair_obscur_launcher.json) — Core/SettingsStore
//      is NOT modified.
//   2. INSTALL/UPDATE (best effort) = download the latest zip from
//      Demorck/ClairObscur_APWorld/releases/latest and extract to the game dir.
//      The plugin presents clear guided steps for the user, since the exact mod
//      layout must be verified against the real mod release.
//   3. LAUNCH = run Expedition33.exe from the detected/override install; if the
//      exe cannot be found but Steam is present, fall back to
//      steam://rungameid/2925360. ConnectsItself = true (the mod owns the slot —
//      the launcher must NOT hold its own ApClient on it). SupportsStandalone =
//      true (the game runs fine without AP). No connection prefill (entered
//      in-game via the mod's connection UI).
//
// ── DEFENSIVE / UNVERIFIED ────────────────────────────────────────────────────
//   * "Installed" is judged by the presence of Expedition33.exe in the detected/
//     override install dir AND any mod-related DLL or marker file under the game
//     dir. Since the exact mod layout is unverified, the detection is best-effort.
//   * Steam library parsing is defensive: a hand-written tolerant VDF scan that
//     pulls quoted "path" values; any failure degrades gracefully.
//   * No plaintext AP password is ever written by this plugin (the connection is
//     entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ClairObscurPlugin : IGamePlugin
{
    // ── Constants — the Clair Obscur AP mod (Demorck/ClairObscur_APWorld) ───────
    private const string MOD_OWNER = "Demorck";
    private const string MOD_REPO  = "ClairObscur_APWorld";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl   =
        "https://archipelago.gg/games/Clair%20Obscur%3A%20Expedition%2033/info/en";
    private const string GameInfoUrl     =
        "https://archipelago.gg/games/Clair%20Obscur%3A%20Expedition%2033/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Clair Obscur: Expedition 33 appid 2925360.
    // UNVERIFIED: confirm this against the Steam store page before releasing.
    private const string CoAppId = "2925360";
    private static readonly string SteamRunUrl = $"steam://rungameid/{CoAppId}";

    /// The standard Steam install sub-folder name for Clair Obscur: Expedition 33.
    /// UNVERIFIED: check the actual steamapps/common/ subfolder on a real install.
    private const string SteamCommonFolderName = "Clair Obscur Expedition 33";

    /// The base-game executable name.
    /// UNVERIFIED: this is the best guess ("Expedition33.exe") based on the
    /// title/subtitle naming convention. The real exe may differ — check a real
    /// install and update this constant accordingly.
    private const string CoExeName = "Expedition33.exe";

    /// Pinned fallback version for the AP mod when the GitHub API is unreachable.
    /// UNVERIFIED: update this to the real latest release tag from
    /// Demorck/ClairObscur_APWorld/releases once the mod is established.
    private const string FallbackVersion = "0.1.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "clair_obscur";
    public string DisplayName => "Clair Obscur: Expedition 33";
    public string Subtitle    => "PC · Archipelago mod";

    /// EXACT AP game string — UNVERIFIED. Check against Demorck/ClairObscur_APWorld
    /// __init__.py: look for `class <...>World(World): ... game = "..."` before
    /// generating any multiworld. This is the best guess based on the repo name.
    public string ApWorldName => "Clair Obscur: Expedition 33";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "clair_obscur.png");

    public string ThemeAccentColor => "#7B1FA2";   // dark violet/purple — the game's painterly art style
    public string[] GameBadges     => new[] { "Requires Clair Obscur: Expedition 33 on Steam" };

    public string Description =>
        "Clair Obscur: Expedition 33 (2025) is the debut RPG from Sandfall Interactive — " +
        "a painterly, melancholic JRPG set in a world where a Painter inscribes a number " +
        "each year, and everyone of that age perishes. You lead Expedition 33, a group of " +
        "survivors who march to end the Painter before she writes the final digit. The " +
        "Archipelago mod by Demorck shuffles key items, abilities, and story progression " +
        "across the multiworld, turning the expedition's discoveries into checks for the " +
        "entire group. Requires: your own legally-owned copy of Clair Obscur: Expedition 33 " +
        "on Steam. Install the AP mod following the repository instructions, then connect " +
        "via the in-game connection UI.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means Expedition33.exe exists in the detected/override install
    /// AND any AP mod-related file is present under the game directory. Since the
    /// exact mod layout is UNVERIFIED, the exe presence alone is the primary gate.
    public bool IsInstalled
    {
        get
        {
            string? dir = ResolveClairObscurDir();
            if (dir == null) return false;
            if (!File.Exists(Path.Combine(dir, CoExeName))) return false;
            // Additional check: look for any AP mod DLL or marker file under the game dir.
            // UNVERIFIED: update this check once the real mod file layout is confirmed.
            return FindInstalledModFile(dir) != null;
        }
    }

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and working files. The actual mod is
    /// extracted INTO the Clair Obscur install dir, not here. Exposed as
    /// GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "ClairObscur");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as AShortHike / Jak /
    /// Blasphemous). Per the brief, lives under Games/ROMs/clair_obscur/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "clair_obscur_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The AP mod reports checks/items/goal to the AP server itself — the launcher
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
            string? dir = ResolveClairObscurDir();
            InstalledVersion = (dir != null && File.Exists(Path.Combine(dir, CoExeName)))
                ? (ReadStampedVersion() ?? (FindInstalledModFile(dir) != null ? "installed" : null))
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
        // 0. We need a Clair Obscur install to drop the mod into. Prefer an explicit
        //    override; else auto-detect the Steam install.
        progress.Report((2, "Locating your Clair Obscur: Expedition 33 installation..."));
        string? coDir = ResolveClairObscurDir();
        if (coDir == null)
            throw new InvalidOperationException(
                "Could not find a Clair Obscur: Expedition 33 installation. Open this " +
                "game's Settings and pick your Clair Obscur folder (the one containing " +
                "Expedition33.exe — note: the exe name is UNVERIFIED and may differ), " +
                "or install the game via Steam first. The Archipelago mod is added on " +
                "top of your own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback).
        progress.Report((6, "Checking the latest Clair Obscur AP mod release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Clair Obscur AP mod download on GitHub. Check your " +
                "internet connection, or install the mod manually following the " +
                "instructions at " + ModRepoUrl + ". See Settings for links.");

        // 2. Download + extract the mod zip INTO the Clair Obscur install directory.
        //    UNVERIFIED: the exact mod layout (where files should be extracted within
        //    the game dir) must be confirmed against the real mod release contents.
        await DownloadAndExtractModAsync(zipUrl, version, coDir, progress, ct);

        // 3. Stamp the version next to our sidecar so the tile can show it.
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Staged Clair Obscur AP mod {version} into your game folder. " +
            "IMPORTANT: The mod installation layout is UNVERIFIED for this new game. " +
            "If the mod does not load, please follow the manual installation " +
            "instructions at " + ModRepoUrl + " and check the mod's README. " +
            "To play: launch Clair Obscur: Expedition 33 and connect to your AP server " +
            "via the in-game connection panel (exact steps — see the mod README). " +
            "This launcher cannot pre-fill the connection."));
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
        // HONEST: the AP server connection for Clair Obscur: Expedition 33 is entered
        // via the in-game connection panel provided by the AP mod. The exact connection
        // flow is UNVERIFIED — check the mod README at Demorck/ClairObscur_APWorld.
        // There is no documented command-line / config prefill this launcher can apply.
        // Launching from this tile just starts the game; the user connects in-game.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartClairObscur();
        return Task.CompletedTask;
    }

    /// Clair Obscur: Expedition 33 runs fine without AP (standalone playthrough).
    public bool SupportsStandalone => true;

    /// The AP mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartClairObscur();
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
        // The AP mod receives items from the AP server directly; there is nothing
        // for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Clair Obscur folder contains
    /// Expedition33.exe. Return null when acceptable, else a short human-readable
    /// reason.
    /// UNVERIFIED: the exe name "Expedition33.exe" is a best guess — update this
    /// once confirmed against a real install.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Clair Obscur: Expedition 33 install folder.";

        if (LooksLikeClairObscurDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeClairObscurDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Clair Obscur: Expedition 33 installation. " +
               "Pick the folder that contains Expedition33.exe (UNVERIFIED exe name — " +
               "may differ on your install). For Steam this is usually " +
               @"...\steamapps\common\Clair Obscur Expedition 33 (folder name UNVERIFIED).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── UNVERIFIED honesty header — prominently displayed ──────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "⚠ NEW GAME — SEVERAL TECHNICAL DETAILS ARE UNVERIFIED ⚠\n" +
                   "Clair Obscur: Expedition 33 (2025) is a very new game and the AP " +
                   "integration is also new. The following details need verification " +
                   "against a real install and the mod repository before this plugin " +
                   "is fully reliable:\n" +
                   "• Steam AppId: 2925360 (UNVERIFIED — check the Steam store page)\n" +
                   "• Steam common folder: \"Clair Obscur Expedition 33\" (UNVERIFIED — " +
                   "check your actual steamapps/common/ subfolder)\n" +
                   "• Executable name: Expedition33.exe (UNVERIFIED — check your install)\n" +
                   "• AP World game string: \"Clair Obscur: Expedition 33\" (UNVERIFIED — " +
                   "check __init__.py in Demorck/ClairObscur_APWorld)\n" +
                   "• In-game connection method: UNVERIFIED — see the mod README\n\n" +
                   "If the launcher cannot find your install, use the folder picker below. " +
                   "Install the mod manually from the GitHub link if auto-install fails.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CLAIR OBSCUR: EXPEDITION 33 INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? coDir       = ResolveClairObscurDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = coDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + coDir
                : "Detected Steam install: " + coDir)
            : "Clair Obscur: Expedition 33 not detected. Pick your install folder below, " +
              "or install the game via Steam first. (Detection uses UNVERIFIED AppId " +
              "2925360 and folder name — may need the manual picker.)";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = coDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // mod status line
        string? modFile = coDir != null ? FindInstalledModFile(coDir) : null;
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modFile != null
                    ? "AP mod file found: " + modFile
                    : "AP mod file not found. Use Install on the Play tab, or install " +
                      "manually from " + ModRepoUrl + " (mod layout UNVERIFIED).",
            FontSize = 11, Foreground = modFile != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? coDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Clair Obscur: Expedition 33 install folder (the one " +
                          "containing Expedition33.exe — UNVERIFIED exe name). Detected " +
                          "from Steam automatically; set it here to override.",
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
                Title            = "Select your Clair Obscur: Expedition 33 install folder (contains Expedition33.exe — UNVERIFIED)",
                InitialDirectory = Directory.Exists(overrideDir ?? coDir ?? "")
                                   ? (overrideDir ?? coDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Clair Obscur folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeClairObscurDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeClairObscurDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (UNVERIFIED AppId 2925360). " +
                   "Use this picker if the game was not detected, or for a non-standard " +
                   "Steam library location.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (entered in-game) ─────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game via the AP mod)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "The exact connection flow is UNVERIFIED — consult the mod README at " +
                   "Demorck/ClairObscur_APWorld for the confirmed steps. Typically for " +
                   "AP mods of this type: launch the game, find the in-game Archipelago " +
                   "connection panel, enter your Server Address, Port (e.g. 38281), Slot " +
                   "Name, and optional Password, then connect. This launcher does not " +
                   "pre-fill the connection.",
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
            "1. Own Clair Obscur: Expedition 33 on Steam. Install it if you have not. " +
                "Use the folder picker above if it was not detected.",
            "2. Download the AP mod from the GitHub releases page (link below) and follow " +
                "the installation instructions in the mod's README. The Install button on " +
                "the Play tab will attempt to do this automatically, but since the mod is " +
                "new, manual verification is recommended.",
            "3. Follow the mod README for any additional setup steps (e.g. BepInEx " +
                "installation or other prerequisites — UNVERIFIED).",
            "4. Launch Clair Obscur: Expedition 33 and confirm the mod loaded (check for " +
                "an AP connection panel or indicator in the game — UNVERIFIED).",
            "5. Enter your Server Address, Port, Slot Name, and optional Password in the " +
                "in-game connection panel, then connect. (This launcher cannot pre-fill " +
                "the connection — it is entered in-game.)",
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
            ("Clair Obscur AP Mod — GitHub ↗",             ModRepoUrl),
            ("Clair Obscur AP Mod — Releases ↗",           ModRepoUrl + "/releases"),
            ("AP Setup Guide (Clair Obscur) ↗",            SetupGuideUrl),
            ("Clair Obscur Game Info (AP) ↗",              GameInfoUrl),
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

    /// "v0.1.0" → "0.1.0" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the mod zip download URL. Prefers
    /// the first .zip asset; falls back to the pinned 0.1.0 fallback when the API
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
                string? anyZip = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        anyZip = url;
                        break;
                    }
                }
                if (anyZip != null)
                    return (version, anyZip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        // Fallback: the pinned version. We cannot construct a reliable direct URL
        // without knowing the real asset name, so return null for ZipUrl to let
        // the caller surface a clear message to the user.
        return (FallbackVersion, null);
    }

    // ── Private helpers — Steam / Clair Obscur detection ──────────────────────

    /// The Clair Obscur install dir to use: the override (if set and valid) wins,
    /// else the Steam-detected install. Null when nothing is found.
    private string? ResolveClairObscurDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeClairObscurDir(ov))
            return ov;

        try { return DetectSteamClairObscurDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Clair Obscur: Expedition 33 if it has the (UNVERIFIED)
    /// Expedition33.exe executable. Update this check once the real exe name is
    /// confirmed against an actual install.
    private static bool LooksLikeClairObscurDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            // UNVERIFIED: "Expedition33.exe" is the best guess. Update when confirmed.
            if (File.Exists(Path.Combine(dir, CoExeName))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Clair Obscur install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_2925360.acf exists → steamapps\common\<installdir>.
    /// UNVERIFIED: AppId 2925360 and the folder name are best guesses.
    private static string? DetectSteamClairObscurDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{CoAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    // Prefer the installdir named in the manifest; fall back to the
                    // conventional folder name (UNVERIFIED).
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeClairObscurDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeClairObscurDir(conventional)) return conventional;
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
            // find the opening quote of the value
            int open = text.IndexOf('"', i);
            if (open < 0) yield break;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) yield break;

            string raw = text.Substring(open + 1, close - open - 1);
            yield return raw.Replace("\\\\", "\\");
            i = close + 1;
        }
    }

    /// Read the "installdir" value from an appmanifest_*.acf (same quoted-pair format
    /// as VDF). Returns null if absent.
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

    /// Find any AP mod-related file under the game directory (best effort).
    /// UNVERIFIED: the exact mod file layout is unknown for this new game. This
    /// searches for common AP mod patterns (BepInEx plugins, .dll files containing
    /// "archipelago" or "expedition" in the name). Update once the real mod layout
    /// is confirmed from Demorck/ClairObscur_APWorld's release contents.
    private static string? FindInstalledModFile(string gameDir)
    {
        if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir))
            return null;

        // UNVERIFIED mod layout: try common patterns for AP mods on Unreal/Unity games.
        // Check BepInEx plugins directory (common for Unreal/Unity AP mods).
        string[] candidateDirs = {
            Path.Combine(gameDir, "BepInEx", "plugins"),
            Path.Combine(gameDir, "Mods"),
            Path.Combine(gameDir, "Archipelago"),
            gameDir,
        };

        foreach (string dir in candidateDirs)
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                foreach (string dll in Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(dll).ToLowerInvariant();
                    if (name.Contains("archipelago") || name.Contains("expedition") ||
                        name.Contains("clairobscur") || name.Contains("clair_obscur"))
                        return dll;
                }
            }
            catch { /* permission / vanished */ }
        }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Clair Obscur: Expedition 33: prefer the exe in the detected/override
    /// install; if that cannot be found but Steam is present, fall back to the
    /// steam:// URL. Surfaces a clear message rather than failing opaquely.
    /// UNVERIFIED: the exe name "Expedition33.exe" may differ — update when confirmed.
    private void StartClairObscur()
    {
        string? coDir = ResolveClairObscurDir();
        string? exe   = coDir != null ? Path.Combine(coDir, CoExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = coDir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Clair Obscur: Expedition 33.");

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
            "Could not find Expedition33.exe (UNVERIFIED exe name — may differ on your " +
            "install). Open this game's Settings and pick your Clair Obscur: Expedition 33 " +
            "install folder, or install the game via Steam.",
            CoExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod zip and extract it into the Clair Obscur install directory.
    /// UNVERIFIED: the exact mod layout (where files should be extracted within the
    /// game dir) must be confirmed against the real mod release contents.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string gameDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"clair-obscur-ap-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"clair-obscur-ap-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading Clair Obscur AP mod {version}..."));
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
                        progress.Report((pct, $"Downloading... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Installing the AP mod into your game folder..."));
            Directory.CreateDirectory(gameDir);
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // UNVERIFIED: The mod layout within the zip is unknown for this new game.
            // Attempt: if the zip wraps everything in a single sub-folder, flatten it;
            // otherwise merge the root directly into the game dir.
            string mergeRoot = tempExtract;
            string[] subdirs = Directory.GetDirectories(mergeRoot);
            string[] files   = Directory.GetFiles(mergeRoot);
            if (subdirs.Length == 1 && files.Length == 0)
            {
                // Single wrapped folder — descend into it.
                mergeRoot = subdirs[0];
            }

            // Merge the extracted tree INTO the game folder (do not wipe existing files).
            MergeDirectory(mergeRoot, gameDir);

            progress.Report((90, "Mod files installed."));
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
    // This plugin keeps its launcher-side settings (the install-dir override + an
    // informational version stamp) in its OWN JSON file so it stays a single
    // self-contained source file and does not modify Core/SettingsStore. BOM-less
    // UTF-8, read-modify-write (same approach as AShortHike / Blasphemous / Jak).

    private sealed class CoSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private CoSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<CoSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(CoSettings s)
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
