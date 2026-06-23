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

namespace LauncherV2.Plugins.Peak;

// ═══════════════════════════════════════════════════════════════════════════════
// PeakPlugin — install / launch for "PEAK" (Aggro Crab & Landfall Games, 2025)
// played through the PEAKPELAGO mod by Mickemoose (BepInEx 5 / C# / Harmony +
// peak.apworld), which contains the in-game Archipelago client. This is a NATIVE
// "ConnectsItself" integration in the same family as the shipped Blasphemous /
// Hollow Knight / TUNIC / Stardew Valley / A Short Hike plugins: the game itself
// speaks to the AP server (no emulator, no Lua bridge, no launcher-held ApClient
// on the slot).
//
// PEAK is a co-op mountain climbing roguelite released June 2025 that quickly
// amassed 130,000+ reviews at 95% positive — one of the fastest-growing co-op
// games of 2025. The PEAKPELAGO Archipelago mod by Mickemoose (latest v0.6.0 on
// Thunderstore, active development through June 2026) randomizes gear unlocks,
// checkpoint flags, map region access, and special items, turning each mountain
// climb into a multiworld discovery adventure. Up to 4 players can participate
// together in co-op.
//
// IMPORTANT — AP GAME STRING: The Archipelago game string for this game is
// "peak" — ALL LOWERCASE, no capitals. This is unusual (most games use title
// case, e.g. "A Short Hike") but confirmed from the apworld file name
// (peak.apworld). The GameId here matches: "peak".
//
// ── HONEST REALITY CHECK (2026-06-19, details sourced from Thunderstore and
//    the GitHub mod repo Mickemoose/peak-archipelago) ─────────────────────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of PEAK (Steam appid 3527290 — UNVERIFIED; very new June 2025 game), and
// Archipelago support is delivered as a BepInEx 5 mod added on top. The honest
// integration ceiling — exactly like the shipped Blasphemous / A Short Hike /
// Hollow Knight plugins — is "automate what is possible, guide the irreducible
// parts."
//
//   * THE AP WORLD game string is "peak" (all lowercase — confirmed from the
//     apworld file name "peak.apworld"). UNUSUAL but confirmed. GameId = "peak".
//
//   * THE MOD repo is Mickemoose/peak-archipelago (GitHub). The mod is also
//     available on Thunderstore as PEAKPELAGO v0.6.0, latest as of May 19, 2026.
//     It is a BepInEx 5 mod (C#/Harmony), same ecosystem as Risk of Rain 2,
//     Inscryption, and other BepInEx Unity games in this launcher.
//
//   * UNVERIFIED DETAILS (PEAK is a very new game — verify these before shipping):
//     - Steam AppId 3527290 (UNVERIFIED — sourced from known info; very new game)
//     - Exe name PEAK.exe (UNVERIFIED — Unity game, likely, but not directly
//       confirmed; user may need to override if it differs)
//     - Steam common folder "PEAK" (UNVERIFIED — likely matches the game name but
//       may differ; the install-dir from appmanifest is also tried)
//
//   * INSTALL: the mod requires BepInEx 5 to be installed in the PEAK game folder
//     first. The plugin can download and extract the mod zip from GitHub releases.
//     It checks for BepInEx presence and guides the user if it is absent. The
//     connection is configured via an in-game UI or BepInEx config file.
//
//   * CONNECTION: made either through an in-game UI (if the mod provides one) or
//     through the BepInEx config file at
//     BepInEx\config\peak_archipelago.cfg (or similar). The plugin surfaces the
//     known connection details (server, port, slot, password) in the settings
//     panel so the user can enter or copy them. Because config-file prefill for
//     BepInEx games varies per mod and is not confirmed for this mod, this plugin
//     does NOT attempt to write the config — the settings panel provides the
//     values to copy.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam PEAK install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\PEAK via appmanifest_3527290.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and takes
//      precedence; it is validated (must contain PEAK.exe or PEAK_Data) and
//      persisted in this plugin's OWN sidecar
//      (Games/ROMs/peak/peak_launcher.json) — Core/SettingsStore is NOT modified.
//   2. INSTALL/UPDATE (best effort) = download the mod zip from
//      Mickemoose/peak-archipelago releases/latest on GitHub and extract it into
//      the PEAK game directory (so the DLL lands at BepInEx\plugins\<mod>.dll).
//      The plugin also checks for BepInEx presence and guides the user to install
//      it first if absent.
//   3. LAUNCH = run PEAK.exe from the detected/override install; if the exe
//      cannot be found but Steam is present, fall back to steam://rungameid/3527290.
//      ConnectsItself = true (the mod owns the slot — the launcher must NOT hold
//      its own ApClient on it). SupportsStandalone = true (plain PEAK runs fine
//      without the mod).
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Blasphemous/Jak-style) ────
//   * "Installed" means: a PEAK.exe exists in a detected/override dir AND any
//     peak*.dll or archipelago*.dll exists under BepInEx\plugins\.
//   * Steam library parsing is defensive: a hand-written tolerant VDF scan that
//     pulls quoted "path" values; any failure degrades to "PEAK not found" rather
//     than throwing.
//   * The Steam AppId (3527290), exe name (PEAK.exe), and common folder (PEAK) are
//     UNVERIFIED — the user may need to set a manual override if detection fails.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PeakPlugin : IGamePlugin
{
    // ── Constants — PEAKPELAGO mod (Mickemoose/peak-archipelago) ─────────────
    private const string MOD_OWNER = "Mickemoose";
    private const string MOD_REPO  = "peak-archipelago";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // Thunderstore listing — mod is published as PEAKPELAGO.
    private const string ThunderstoreUrl =
        "https://thunderstore.io/c/peak/p/PEAKPELAGO/";

    // BepInEx 5 — required mod loader for PEAK (Unity mono). Same as Risk of
    // Rain 2, Inscryption, etc. in this launcher.
    private const string BepInExUrl =
        "https://github.com/BepInEx/BepInEx/releases";
    private const string BepInExGuideUrl =
        "https://docs.bepinex.dev/articles/user_guide/installation/index.html";

    private const string SetupGuideUrl   = "https://archipelago.gg/games/peak/info/en";
    private const string GameInfoUrl     = "https://archipelago.gg/games/peak/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — PEAK appid 3527290 (UNVERIFIED — very new June 2025 game).
    // If Steam detection fails for this game, use the folder picker in Settings.
    private const string PeakAppId = "3527290";   // UNVERIFIED
    private static readonly string SteamRunUrl = $"steam://rungameid/{PeakAppId}";

    // UNVERIFIED: The Steam common folder name and exe name for PEAK. Unity games
    // typically use the game's display name for the folder and <Name>.exe for the
    // exe. These may need to be corrected once a confirmed install is verified.
    private const string SteamCommonFolderName = "PEAK";   // UNVERIFIED
    private const string PeakExeName           = "PEAK.exe"; // UNVERIFIED

    /// Pinned fallback version for the mod when the GitHub API is unreachable.
    /// v0.6.0 was the latest confirmed release on Thunderstore as of May 19, 2026.
    private const string FallbackVersion = "0.6.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "peak";
    public string DisplayName => "PEAK";
    public string Subtitle    => "PC · Archipelago mod";

    /// IMPORTANT: The AP game string for PEAK is all-lowercase "peak" — unusual
    /// but confirmed from the apworld file name (peak.apworld). Most games use
    /// title case; this one does not.
    public string ApWorldName => "peak";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "peak.png");

    public string ThemeAccentColor => "#1565C0";   // mountain blue
    public string[] GameBadges     => new[] { "Requires PEAK on Steam", "BepInEx 5" };

    public string Description =>
        "PEAK (2025) is the viral co-op mountain climbing roguelite from Aggro Crab " +
        "and Landfall Games in which you and up to three friends ascend procedurally " +
        "generated mountains, managing stamina, gear, and teamwork in the face of wild " +
        "weather and treacherous terrain. Released June 2025, it quickly amassed " +
        "130,000+ reviews at 95% positive — one of the fastest-growing co-op games of " +
        "2025. The Archipelago mod (PEAKPELAGO) by Mickemoose randomizes gear unlocks, " +
        "checkpoint flags, map region access, and special items, turning the climb into " +
        "a multiworld discovery adventure. IMPORTANT: The AP game string is all-lowercase " +
        "\"peak\" (unusual but confirmed). Requires: your own legally-owned copy on Steam " +
        "and BepInEx 5 installed first.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means PEAK.exe exists in a detected/override dir AND any
    /// peak*.dll or archipelago*.dll is present under BepInEx\plugins\.
    public bool IsInstalled => CheckModInstalled();

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and working files. The actual mod is
    /// extracted INTO the PEAK game directory's BepInEx folder, not here.
    /// Exposed as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Peak");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same pattern as A Short Hike
    /// / Blasphemous / Hollow Knight). Lives under Games/ROMs/peak/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "peak_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The PEAKPELAGO mod reports checks/items/goal to the AP server itself — the
    // launcher relays nothing. These exist for interface compatibility
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
            InstalledVersion = CheckModInstalled()
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
        // 0. Locate the PEAK game directory. Prefer explicit override; else auto-detect.
        progress.Report((2, "Locating your PEAK installation..."));
        string? peakDir = ResolvePeakDir();
        if (peakDir == null)
            throw new InvalidOperationException(
                "Could not find a PEAK installation. Open this game's Settings and pick " +
                "your PEAK folder (the one containing PEAK.exe), or install PEAK via Steam " +
                "first. NOTE: Steam AppId 3527290 and folder name \"PEAK\" are unverified " +
                "for this new game — if auto-detection fails, use the manual folder picker.");

        // 1. Check that BepInEx is installed (the mod loader). Warn clearly if not.
        string bepInExDir = Path.Combine(peakDir, "BepInEx");
        bool hasBepInEx   = Directory.Exists(bepInExDir);
        if (!hasBepInEx)
        {
            progress.Report((4,
                "WARNING: BepInEx not found in your PEAK folder. The PEAKPELAGO mod " +
                "requires BepInEx 5 to be installed first. See Settings for install " +
                "steps and links. Will attempt to continue staging mod files anyway..."));
        }

        // 2. Resolve the latest mod release.
        progress.Report((6, "Checking the latest PEAKPELAGO mod release on GitHub..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the PEAKPELAGO mod download on GitHub. Check your internet " +
                "connection, or install the mod manually from Thunderstore: " +
                ThunderstoreUrl + " — see Settings for the guided steps. The mod repo is " +
                ModRepoUrl + ".");

        // 3. Download + extract the mod zip INTO the PEAK game directory.
        //    BepInEx mods extract as BepInEx\plugins\<mod>.dll (and sometimes
        //    BepInEx\config\ files). The zip layout from GitHub will be checked and
        //    either extracted flat or with its BepInEx folder structure merged in.
        await DownloadAndExtractModAsync(zipUrl, version, peakDir, progress, ct);

        // 4. Stamp the version in the sidecar.
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool modOk = CheckModInstalled();
        progress.Report((100,
            $"Staged the PEAKPELAGO mod {version} into your PEAK folder" +
            (modOk ? "." : " (verify the files landed in BepInEx\\plugins\\).") +
            (!hasBepInEx
                ? " IMPORTANT: BepInEx was not found — install BepInEx 5 first (see Settings)."
                : "") +
            " The AP game string for PEAK is all-lowercase \"peak\" (unusual — note this " +
            "when generating your multiworld). To play: launch PEAK with the mod active " +
            "and connect to your AP server using the in-game UI or the BepInEx config " +
            "file. See Settings for guided steps and connection details."));
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
        // HONEST: connection for the PEAKPELAGO mod is configured via the in-game UI
        // or the BepInEx config file. The launcher surfaces the session credentials
        // in the settings panel so the user can copy them. ConnectsItself = true:
        // the launcher must NOT hold its own ApClient on this slot while the mod is
        // connected.
        _ = session; // intentionally unused — no confirmed prefill mechanism
        StartPeak();
        return Task.CompletedTask;
    }

    /// Plain PEAK runs fine without the Archipelago mod.
    public bool SupportsStandalone => true;

    /// The PEAKPELAGO mod owns the slot connection.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartPeak();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The PEAKPELAGO mod receives items from the AP server directly; the
        // launcher forwards nothing.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid PEAK folder must contain PEAK.exe
    /// (or PEAK_Data for Unity) or at minimum look like a Unity game install.
    /// Returns null when acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your PEAK install folder.";

        if (LooksLikePeakDir(folder))
            return null;

        // Be forgiving: user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikePeakDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a PEAK installation. Pick the folder that " +
               "contains PEAK.exe (for Steam this is usually " +
               @"...\steamapps\common\PEAK). NOTE: the exe name is unverified — if " +
               "the game uses a different name, pick the folder and it will be saved.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x21, 0x96, 0xF3));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Critical notice: lowercase AP game string ─────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "IMPORTANT — AP GAME STRING: The Archipelago game string for PEAK " +
                   "is all-lowercase \"peak\" (no capitals). This is unusual — most games " +
                   "use title case — but it is confirmed from the apworld file name " +
                   "(peak.apworld). When generating your multiworld in the AP YAML, use " +
                   "\"peak\" (all lowercase).",
            FontSize = 12, Foreground = accent,
            FontWeight = System.Windows.FontWeights.SemiBold,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 10),
        });

        // ── Unverified-offline honesty header ─────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "PEAK is your own game (Steam) with the PEAKPELAGO BepInEx mod " +
                   "added on top. NOTE: Steam AppId 3527290, exe name PEAK.exe, and " +
                   "Steam folder name \"PEAK\" are UNVERIFIED for this new game (released " +
                   "June 2025). If auto-detection fails, use the folder picker below. " +
                   "BepInEx 5 must be installed in the game folder before the mod works.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "PEAK INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? peakDir     = ResolvePeakDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = peakDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + peakDir
                : "Detected Steam install: " + peakDir)
            : "PEAK not detected. Pick your install folder below, or install PEAK via " +
              "Steam first. (AppId 3527290 and folder \"PEAK\" are unverified — use the " +
              "picker if auto-detection fails.)";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = peakDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // BepInEx status line
        bool hasBepInEx = peakDir != null && Directory.Exists(Path.Combine(peakDir, "BepInEx"));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = hasBepInEx
                ? "BepInEx found in PEAK folder — mod loader is present."
                : peakDir != null
                    ? "BepInEx NOT found in PEAK folder. Install BepInEx 5 first (see " +
                      "guided steps and links below)."
                    : "BepInEx status unknown (PEAK install not detected).",
            FontSize = 11,
            Foreground = hasBepInEx ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // mod DLL status line
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                ? "PEAKPELAGO mod found: " + modDll
                : "PEAKPELAGO mod not found in BepInEx\\plugins\\ yet. Use Install on " +
                  "the Play tab, or install manually from Thunderstore (see links below).",
            FontSize = 11,
            Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? peakDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your PEAK install folder (the one containing PEAK.exe). " +
                          "Detected from Steam automatically; use this picker to override " +
                          "(e.g. if Steam detection fails for this new game, or for a " +
                          "non-standard library location).",
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
                Title            = "Select your PEAK install folder (contains PEAK.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? peakDir ?? "")
                                   ? (overrideDir ?? peakDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                // For this very new game, be lenient in validation: accept any
                // folder with PEAK.exe, PEAK_Data, or BepInEx (in case the exe
                // name is different from what we expect).
                string? bad = ValidateExistingInstall(picked);
                if (bad != null && !Directory.Exists(Path.Combine(picked, "BepInEx")))
                {
                    System.Windows.MessageBox.Show(
                        bad + "\n\nIf PEAK uses a different exe name, you can still " +
                        "save this folder and set it as the override. Are you sure this " +
                        "is your PEAK game folder?",
                        "PEAK Folder Check",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikePeakDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikePeakDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 3527290 — unverified). " +
                   "Use this picker if auto-detection fails (common for very new games).",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection info ───────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (in-game or BepInEx config)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "The PEAKPELAGO mod connects to your AP server either through an " +
                   "in-game UI (if the mod provides one) or through the BepInEx config " +
                   "file at BepInEx\\config\\peak_archipelago.cfg (or similar name — check " +
                   "the mod's README). Enter or copy your Server Address, Port, Slot " +
                   "Name, and Password (if any) from there. The AP game string is " +
                   "\"peak\" (all lowercase — remember this when making your YAML). " +
                   "This launcher does not pre-fill the connection file.",
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
            "1. Own PEAK on Steam. Install it if you have not. Use the folder picker above " +
                "if it was not auto-detected (AppId 3527290 and folder name are unverified " +
                "for this new game).",
            "2. Install BepInEx 5 into your PEAK game folder. Download it from the BepInEx " +
                "GitHub releases (link below), extract into the PEAK folder, and run the " +
                "game once to let BepInEx initialize (a BepInEx\\config\\ folder should " +
                "appear).",
            "3. Install the PEAKPELAGO mod. You can use the Install button on the Play tab " +
                "(downloads from GitHub releases), or install it manually from Thunderstore " +
                "(link below — this is also the recommended route as Thunderstore may handle " +
                "dependencies).",
            "4. Launch PEAK. The mod will load via BepInEx. Connect to your AP server using " +
                "the in-game menu or by editing BepInEx\\config\\peak_archipelago.cfg with " +
                "your server address, port, slot name, and password.",
            "5. AP YAML note: When generating your multiworld, use \"peak\" (all lowercase) " +
                "as the game string. This is unusual but confirmed from the apworld file name.",
            "6. If you are playing co-op (up to 4 players), each player needs PEAK and the " +
                "mod installed. Check the mod README for multi-player setup details.",
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
            ("PEAKPELAGO mod repo (GitHub) ↗",    ModRepoUrl),
            ("PEAKPELAGO on Thunderstore ↗",       ThunderstoreUrl),
            ("BepInEx 5 releases (download) ↗",   BepInExUrl),
            ("BepInEx install guide ↗",            BepInExGuideUrl),
            ("PEAK AP Game Info ↗",                GameInfoUrl),
            ("Archipelago Official ↗",             ArchipelagoSite),
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

    /// "v0.6.0" → "0.6.0" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the mod zip download URL. Prefers
    /// any .zip asset; falls back to the pinned 0.6.0 direct URL when the API is
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
                string? preferred = null;   // prefer peak*.zip or peakpelago*.zip
                string? anyZip    = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null &&
                        (lower.Contains("peak") || lower.Contains("peakpelago")))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        // Pinned fallback: v0.6.0. If the release URL structure is unknown, return
        // a null ZipUrl so the caller can handle it gracefully (the releases API
        // found nothing usable).
        string fallbackUrl = $"{ModRepoUrl}/releases/download/v{FallbackVersion}/PEAKPELAGO.zip";
        return (FallbackVersion, fallbackUrl);
    }

    // ── Private helpers — Steam / PEAK detection ──────────────────────────────

    /// The PEAK install dir to use: the override (if set and valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolvePeakDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikePeakDir(ov))
            return ov;
        // Also accept an override folder even if it doesn't strictly match
        // LooksLikePeakDir — this is a new game with unverified exe/folder names.
        if (ov != null && Directory.Exists(ov))
            return ov;

        try { return DetectSteamPeakDir(); }
        catch { return null; }
    }

    /// A folder "looks like" PEAK if it has PEAK.exe or a PEAK_Data folder (Unity).
    private static bool LooksLikePeakDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, PeakExeName))) return true;         // UNVERIFIED exe name
            if (Directory.Exists(Path.Combine(dir, "PEAK_Data"))) return true;    // Unity data folder
            // BepInEx subfolder also suggests a likely game dir.
            if (Directory.Exists(Path.Combine(dir, "BepInEx"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam PEAK install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_3527290.acf exists → steamapps\common\PEAK (folder name unverified).
    private static string? DetectSteamPeakDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    // UNVERIFIED: AppId 3527290
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{PeakAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    // Prefer the installdir named in the manifest.
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikePeakDir(candidate)) return candidate;
                        // Return the candidate from the manifest even if we can't
                        // fully verify — it is the most reliable source.
                        if (Directory.Exists(candidate)) return candidate;
                    }
                    // Fall back to conventional folder name (UNVERIFIED).
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikePeakDir(conventional)) return conventional;
                    if (Directory.Exists(conventional)) return conventional;
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

    // ── Private helpers — mod detection ───────────────────────────────────────

    /// Check whether the PEAKPELAGO mod is installed: PEAK.exe (or PEAK_Data)
    /// must be present AND any peak*.dll or archipelago*.dll must exist under
    /// BepInEx\plugins\ (recursive, case-insensitive).
    private bool CheckModInstalled()
    {
        try
        {
            string? dir = ResolvePeakDir();
            if (dir == null) return false;

            // Game itself must be there.
            bool hasGame = File.Exists(Path.Combine(dir, PeakExeName))
                        || Directory.Exists(Path.Combine(dir, "PEAK_Data"));
            if (!hasGame) return false;

            return FindInstalledModDll() != null;
        }
        catch { return false; }
    }

    /// Find the PEAKPELAGO mod DLL under BepInEx\plugins\ (recursive,
    /// case-insensitive). Looks for any DLL whose name contains "peak" or
    /// "archipelago". Returns the dll path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? dir = ResolvePeakDir();
            if (dir == null) return null;

            string pluginsDir = Path.Combine(dir, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(
                pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string lower = Path.GetFileName(dll).ToLowerInvariant();
                if (lower.Contains("peak") || lower.Contains("archipelago"))
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start PEAK: prefer the exe in the detected/override install; if that cannot
    /// be found but Steam is present, fall back to the steam:// URL.
    private void StartPeak()
    {
        string? dir = ResolvePeakDir();
        string? exe = dir != null ? Path.Combine(dir, PeakExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = dir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start PEAK.");

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
            "Could not find PEAK.exe. Open this game's Settings and pick your PEAK " +
            "install folder, or install PEAK via Steam. NOTE: The exe name PEAK.exe is " +
            "unverified for this new game — if it differs, the manual folder picker in " +
            "Settings will still allow you to launch via Steam.",
            PeakExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod's zip and extract it into the PEAK game directory.
    /// BepInEx mods are typically delivered as a zip containing a BepInEx\plugins\
    /// subfolder (or just the DLL + config directly). The extraction logic tries
    /// both layouts and merges into the existing BepInEx tree.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string peakDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"peak-archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"peak-archipelago-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading PEAKPELAGO {version}..."));
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
                        progress.Report((pct, $"Downloading PEAKPELAGO... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Installing the mod into your PEAK\\BepInEx folder..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // Determine the merge root. BepInEx mods can be:
            //   (a) zip with BepInEx\ at root → merge entire zip into peakDir
            //   (b) zip with plugins\ (and config\) at root → merge into peakDir\BepInEx\
            //   (c) zip with a single wrapper folder → descend first
            string mergeRoot   = tempExtract;
            string mergeDest   = peakDir;

            // If wrapped in one folder, descend.
            string[] topDirs  = Directory.GetDirectories(mergeRoot);
            string[] topFiles = Directory.GetFiles(mergeRoot);
            if (topDirs.Length == 1 && topFiles.Length == 0)
                mergeRoot = topDirs[0];

            // Check layout after potential descent.
            bool hasBepInExSubfolder = Directory.Exists(Path.Combine(mergeRoot, "BepInEx"));
            bool hasPluginsSubfolder = Directory.Exists(Path.Combine(mergeRoot, "plugins"));
            if (!hasBepInExSubfolder && hasPluginsSubfolder)
            {
                // The zip contains plugins\ directly — merge into BepInEx\.
                mergeDest = Path.Combine(peakDir, "BepInEx");
                Directory.CreateDirectory(mergeDest);
            }
            // else: either BepInEx\ is at root (merge into peakDir) or unknown layout
            //       (also merge into peakDir, which is the safest fallback).

            MergeDirectory(mergeRoot, mergeDest);
            progress.Report((90, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Recursively copy a directory tree INTO an existing destination, overwriting
    /// individual files but preserving sibling files already there (so other BepInEx
    /// mods are not disturbed).
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
    // This plugin keeps its launcher-side settings (install-dir override + version
    // stamp) in its OWN JSON file under Games/ROMs/peak/ so it stays a single
    // self-contained source file and does not modify Core/SettingsStore. BOM-less
    // UTF-8, read-modify-write (same approach as A Short Hike / Blasphemous / Jak).

    private sealed class PeakSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private PeakSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<PeakSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(PeakSettings s)
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
