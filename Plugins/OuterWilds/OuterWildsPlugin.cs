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
using Microsoft.Win32;
using LauncherV2.Core;

// NOTE: WPF UI types are FULLY QUALIFIED throughout this file (System.Windows.*).
// The real launcher project sets <UseWindowsForms>true</UseWindowsForms> alongside
// WPF, so the bare names Button / TextBox / Color / Brushes / MessageBox /
// FontWeights / Orientation collide between System.Windows.Forms and
// System.Windows[.Controls/.Media] (CS0104). Qualifying every UI type avoids that.

namespace LauncherV2.Plugins.OuterWilds;

// ═══════════════════════════════════════════════════════════════════════════════
// OuterWildsPlugin — install / launch for "Outer Wilds" (Mobius Digital, 2019)
// played through the Archipelago mod for Outer Wilds — a Unity OWML mod that is
// the in-game Archipelago client. This is a NATIVE "ConnectsItself" integration
// in the same family as the shipped Hollow Knight, Subnautica, and Stardew Valley
// plugins — the game itself speaks to the AP server (no emulator, no Lua bridge,
// no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-15, verified against the apworld) ───────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned
// Outer Wilds (Steam appid 753640; Epic Games Store also works), and Archipelago
// is a MOD loaded by the Outer Wilds Mod Loader (OWML). The honest integration
// ceiling is "automate what is possible, guide the irreducible parts."
//
//   * THE AP WORLD game string is "Outer Wilds" (the registered AP game name as
//     used in the community AP world for Outer Wilds). GameId = "outer_wilds".
//
//   * THE MOD — The Archipelago randomizer mod for Outer Wilds is by Ixrec, at
//     https://github.com/Ixrec/OuterWildsArchipelagoRandomizer. It is an OWML
//     mod (OWML uniqueName: "Ixrec.ArchipelagoRandomizer"). Release assets are
//     named "Ixrec.ArchipelagoRandomizer.zip". The mod includes both the OWML
//     mod DLL and the .apworld file for Archipelago server generation. OWML is
//     the standard mod loader for Outer Wilds; mods are placed in
//     %APPDATA%\OuterWilds\Mods\ (each in its own sub-folder with manifest.json).
//
//   * OWML — The mod loader is at https://github.com/ow-mods/owml. The launcher
//     can detect it via its standard install paths and download+install it if
//     absent. OWML itself is extracted into the game folder (its exe, the
//     OWPatcher.exe, and the Mods folder next to it).
//
//   * CONNECTION is made IN-GAME: after the mod is installed and OWML is set up,
//     launching the game via OWML applies the mods automatically. The Archipelago
//     mod provides an in-game menu or start-of-save prompt where the player
//     enters the server / slot / password. There is no command-line arg or static
//     config file the launcher can reliably pre-write (the exact mechanism depends
//     on the mod version), so this plugin does NOT attempt a connection prefill —
//     the settings panel states this and surfaces the session credentials to copy.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Outer Wilds install via the Windows registry, parsing
//      libraryfolders.vdf for every library root and locating appmanifest_753640.
//      A manual install-dir OVERRIDE (settings folder picker) is also supported
//      and takes precedence; validated (must contain OuterWilds.exe) and persisted
//      in this plugin's OWN sidecar (Games/ROMs/outer_wilds/ow_launcher.json).
//   2. CHECK FOR OWML — OWML is expected in the game folder or in the standard
//      install path (%APPDATA%\OuterWilds\). If absent, the launcher offers to
//      download + install it automatically from the official GitHub releases page.
//   3. CHECK FOR THE AP MOD — scans %APPDATA%\OuterWilds\Mods\ for a folder
//      whose manifest.json names "Archipelago" (case-insensitive). Reports the
//      status in the settings panel and guides the user to install/update if absent.
//   4. INSTALL/UPDATE = download OWML from GitHub if absent, then download and
//      extract the Archipelago mod into the OWML mods directory.
//   5. LAUNCH = run OWML.Launcher.exe (which patches and launches Outer Wilds with
//      mods active). Fails gracefully to steam://rungameid/753640. ConnectsItself =
//      true (the mod owns the slot). SupportsStandalone = true (vanilla OW works
//      via the same exe path or steam://). No prefill; stated honestly.
//
// ── DEFENSIVE / UNVERIFIED (verified pattern per existing plugins) ─────────────
//   * "Installed" is judged by the presence of the Archipelago mod manifest under
//      %APPDATA%\OuterWilds\Mods\ — NOT by our own version stamp, because the
//      user may install the mod via OWML's own manager or by hand.
//   * Steam library parsing is defensive (tolerant VDF scan); any failure degrades
//      to "Outer Wilds not found" rather than throwing.
//   * No plaintext AP password is written by this plugin (connection is entered
//      in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class OuterWildsPlugin : IGamePlugin
{
    // ── Constants — OWML (the official mod loader) ────────────────────────────
    private const string OWML_OWNER  = "ow-mods";
    private const string OWML_REPO   = "owml";
    private const string OwmlRepoUrl = $"https://github.com/{OWML_OWNER}/{OWML_REPO}";
    private const string GH_OWML_RELEASES_LATEST =
        $"https://api.github.com/repos/{OWML_OWNER}/{OWML_REPO}/releases/latest";

    // ── Constants — Archipelago mod for Outer Wilds ───────────────────────────
    // The community AP world for Outer Wilds. Verified 2026-06-15: the correct
    // repo is Ixrec/OuterWildsArchipelagoRandomizer (the OWML mod + .apworld).
    // Release assets are named "Ixrec.ArchipelagoRandomizer.zip".
    private const string MOD_OWNER   = "Ixrec";
    private const string MOD_REPO    = "OuterWildsArchipelagoRandomizer";
    private const string ModRepoUrl  = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl  = "https://archipelago.gg/tutorial/Outer%20Wilds/setup/en";
    private const string ArchipelagoSite = "https://archipelago.gg";
    private const string OwmlSite        = "https://outerwildsmods.com/";

    // Steam — Outer Wilds appid 753640.
    private const string OW_STEAM_APP_ID    = "753640";
    private const string SteamCommonFolder  = "Outer Wilds";
    private static readonly string SteamRunUrl = $"steam://rungameid/{OW_STEAM_APP_ID}";

    // OWML standard paths
    private static readonly string OwmlAppDataDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "OuterWilds");
    private static readonly string OwmlModsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                     "OuterWilds", "Mods");

    /// Pinned fallback OWML version when the GitHub API is unreachable.
    private const string OwmlFallbackVersion = "2.1.1";
    private static readonly string OwmlFallbackZipUrl =
        $"{OwmlRepoUrl}/releases/download/v{OwmlFallbackVersion}/OWML.zip";

    /// Pinned fallback AP mod version when the GitHub API is unreachable.
    /// Latest confirmed release as of 2026-06-15: v1.2.1.
    private const string ModFallbackVersion = "1.2.1";
    private static readonly string ModFallbackZipUrl =
        $"{ModRepoUrl}/releases/download/v{ModFallbackVersion}/Ixrec.ArchipelagoRandomizer.zip";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "outer_wilds";
    public string DisplayName => "Outer Wilds";
    public string Subtitle    => "Native PC · OWML Archipelago mod";

    /// EXACT AP game string — "Outer Wilds".
    public string ApWorldName => "Outer Wilds";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "outer_wilds.png");

    public string ThemeAccentColor => "#F5A623";   // warm orange / space aesthetic
    public string[] GameBadges     => new[] { "Steam", "OWML", "ConnectsItself" };

    public string Description =>
        "Outer Wilds Archipelago randomizer. Uses the Outer Wilds Mod Loader (OWML). " +
        "Install OWML and the AP mod via this launcher, then enter your connection " +
        "details when the game loads. Outer Wilds (Mobius Digital, 2019) is a " +
        "mystery-adventure set in a handcrafted solar system. With the Archipelago " +
        "mod active, ship's logs, warp codes, tool upgrades and story beats are " +
        "shuffled into the multiworld — and the game connects to the Archipelago " +
        "server itself. You bring your own copy (Steam or Epic Games Store) and add " +
        "the mod on top via OWML.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the Archipelago mod manifest is present under the OWML
    /// Mods directory. We do NOT gate on our own stamp — the user may install via
    /// OWML's own mod manager (outerwildsmods.com), which we honor.
    public bool IsInstalled => FindInstalledApModDir() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and working files. Exposed as GameDirectory.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "OuterWilds");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same pattern as all other
    /// plugins here). Lives under Games/ROMs/outer_wilds/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "ow_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Archipelago mod for Outer Wilds reports checks/items/goal to the AP
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
            // Best-effort: use our stamped version if present; else "installed"
            // when the mod manifest is detected in the OWML Mods directory.
            InstalledVersion = FindInstalledApModDir() != null
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
        // Step 1: We need a detected Outer Wilds install (for OWML to know the game).
        progress.Report((2, "Locating your Outer Wilds installation..."));
        string? owDir = ResolveOuterWildsDir();
        if (owDir == null)
            throw new InvalidOperationException(
                "Could not find an Outer Wilds installation. Open this game's Settings " +
                "and pick your Outer Wilds folder (the one containing OuterWilds.exe), " +
                "or install Outer Wilds via Steam first. The Archipelago mod is added " +
                "on top of your own copy of the game.");

        // Step 2: Ensure OWML is installed. Download+install it if absent.
        progress.Report((8, "Checking for OWML (Outer Wilds Mod Loader)..."));
        string? owmlExe = FindOwmlExe(owDir);
        if (owmlExe == null)
        {
            progress.Report((12, "OWML not found — downloading..."));
            await DownloadAndInstallOwmlAsync(owDir, progress, ct);
            owmlExe = FindOwmlExe(owDir);
        }

        if (owmlExe == null)
            throw new InvalidOperationException(
                "OWML installation could not be verified after download. Try installing " +
                "OWML manually from " + OwmlRepoUrl + " — download OWML.zip and extract " +
                "it into your Outer Wilds game folder so that OWML.Launcher.exe is next " +
                "to OuterWilds.exe. Then try installing the AP mod again.");

        // Step 3: Resolve latest Archipelago mod release.
        progress.Report((40, "Checking the latest Archipelago mod release..."));
        var (modVersion, modZipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = modVersion;

        if (modZipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Archipelago Outer Wilds mod download on GitHub. " +
                "Check your internet connection, or download the mod manually from " +
                ModRepoUrl + "/releases and place it in the OWML Mods directory: " +
                OwmlModsDir);

        // Step 4: Download + install the AP mod into the OWML Mods directory.
        progress.Report((45, $"Downloading Archipelago mod {modVersion}..."));
        await DownloadAndInstallApModAsync(modZipUrl, modVersion, progress, ct);

        // Step 5: Stamp the version.
        WriteStampedVersion(modVersion);
        InstalledVersion = modVersion;

        progress.Report((100,
            $"Installed the Archipelago mod {modVersion} into the OWML Mods folder. " +
            "To play: launch Outer Wilds via OWML (use the Play button, or run " +
            "OWML.Launcher.exe directly). The Archipelago mod will prompt you for " +
            "your server address, slot name, and optional password when you start a " +
            "new expedition. See Settings for the guided steps and links."));
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
        // HONEST: the AP server connection for Outer Wilds is entered in the
        // in-game Archipelago menu when starting an expedition. There is no
        // command-line / config file we can reliably pre-write. So launching just
        // starts the game via OWML; the user connects in-game.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartOuterWilds();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Outer Wilds runs without any AP connection.
    public bool SupportsStandalone => true;

    /// The Archipelago mod for Outer Wilds owns the slot connection.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartOuterWilds();
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

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The Archipelago mod receives items from the AP server directly; there
        // is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Outer Wilds folder contains
    /// OuterWilds.exe and/or OuterWilds_Data. Return null when acceptable, else a
    /// short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Outer Wilds install folder.";

        if (LooksLikeOuterWildsDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolder);
            if (LooksLikeOuterWildsDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like an Outer Wilds installation. Pick the folder " +
               "that contains OuterWilds.exe (the OuterWilds_Data folder is next to it). " +
               @"For Steam this is usually ...\steamapps\common\Outer Wilds.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF5, 0xA6, 0x23));

        var panel = new System.Windows.Controls.StackPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Outer Wilds is your own game (Steam / Epic Games Store) with the " +
                "Archipelago mod added on top via OWML (Outer Wilds Mod Loader). The " +
                "launcher detects your Steam install, installs OWML and the mod, and " +
                "launches the game via OWML. You enter your server details in-game when " +
                "starting a new expedition — the launcher cannot pre-fill the connection. " +
                "These external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: game install ─────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "OUTER WILDS INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? owDir       = ResolveOuterWildsDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = owDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + owDir
                : "Detected Steam install: " + owDir)
            : "Outer Wilds not detected. Pick your install folder below, or install " +
              "Outer Wilds via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = owDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        var dirRow = new System.Windows.Controls.DockPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? owDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Outer Wilds install folder (the one containing OuterWilds.exe). " +
                          "Detected from Steam automatically; set it here to override " +
                          "(Epic Games Store / non-standard Steam library).",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select your Outer Wilds install folder (contains OuterWilds.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? owDir ?? "")
                                   ? (overrideDir ?? owDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not an Outer Wilds folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the Steam "common" parent, descend.
                if (!LooksLikeOuterWildsDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolder);
                    if (LooksLikeOuterWildsDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 753640). Use this " +
                   "picker for the Epic Games Store or a non-standard Steam library.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: OWML status ──────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "OWML STATUS (OUTER WILDS MOD LOADER)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });

        string? owmlExe    = owDir != null ? FindOwmlExe(owDir) : null;
        string  owmlStatus = owmlExe != null
            ? "OWML found: " + owmlExe
            : owDir != null
                ? "OWML not found in the Outer Wilds folder. Use Install on the Play tab " +
                  "to download and install OWML automatically, or install it manually from " +
                  "outerwildsmods.com."
                : "Outer Wilds not detected — set your install folder above first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = owmlStatus, FontSize = 11,
            Foreground = owmlExe != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 10),
        });

        // ── Section: AP mod status ────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "ARCHIPELAGO MOD STATUS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });

        string? apModDir  = FindInstalledApModDir();
        string  modStatus = apModDir != null
            ? "Archipelago mod found: " + apModDir
            : "Archipelago mod not found in the OWML Mods folder yet.\n" +
              "Mods folder: " + OwmlModsDir + "\n" +
              "Use Install on the Play tab, or install the mod via the Outer Wilds Mod " +
              "Manager at outerwildsmods.com.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modStatus, FontSize = 11,
            Foreground = apModDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 10),
        });

        // ── Section: Connection instructions ──────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Launch Outer Wilds via OWML (use the Play button). When you start a " +
                "new expedition, the Archipelago mod will prompt you for your server " +
                "address (host:port, e.g. archipelago.gg:38281), slot name, and optional " +
                "password. The connection is stored in your save — you only enter it " +
                "once per game file. This launcher cannot pre-fill the connection, so " +
                "copy your details from the AP room page before launching.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Outer Wilds (Steam, or the Epic Games Store). Install it if you have " +
                "not. Use \"Select folder...\" above if it was not auto-detected.",
            "2. Install OWML and the Archipelago mod: use Install on the Play tab. The " +
                "launcher will download OWML (if absent) and the Archipelago mod and " +
                "place them in the correct locations automatically.",
            "3. Alternative: install the mod via the Outer Wilds Mod Manager at " +
                "outerwildsmods.com — search for \"Archipelago\" and click Install. This " +
                "also installs OWML automatically if needed.",
            "4. Launch Outer Wilds via this launcher (Play button). OWML loads the mods " +
                "before the game starts.",
            "5. In-game: start a NEW expedition. The Archipelago mod will ask for your " +
                "server address, slot name, and optional password. Enter them and begin " +
                "your multiworld journey. (This launcher cannot pre-fill these — enter " +
                "them in-game.)",
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
            Text = "LINKS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new (string Label, string Url)[]
        {
            ("Outer Wilds Mod Manager (outerwildsmods.com) ↗", OwmlSite),
            ("OWML on GitHub ↗",                               OwmlRepoUrl),
            ("Ixrec.ArchipelagoRandomizer mod (GitHub) ↗",    ModRepoUrl),
            ("Outer Wilds Setup Guide (AP) ↗",                SetupGuideUrl),
            ("Archipelago Official ↗",                        ArchipelagoSite),
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
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
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

    public Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
        => Task.FromResult(Array.Empty<NewsItem>());

    // ── Private helpers — release resolution (OWML) ───────────────────────────

    /// Resolve the latest OWML release: version + the OWML.zip download URL.
    /// Falls back to pinned version when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestOwmlAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_OWML_RELEASES_LATEST, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;
                string? anyZip    = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;
                    anyZip ??= url;
                    if (preferred == null && lower.Contains("owml"))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        return (OwmlFallbackVersion, OwmlFallbackZipUrl);
    }

    // ── Private helpers — release resolution (AP mod) ─────────────────────────

    /// Resolve the latest AP mod release: version + the mod zip download URL.
    /// Falls back to pinned version when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_LATEST, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;
                string? anyZip    = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;
                    anyZip ??= url;
                    // Prefer the Ixrec.ArchipelagoRandomizer.zip asset by name.
                    if (preferred == null
                        && (lower.Contains("ixrec") || lower.Contains("archipelago")))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        return (ModFallbackVersion, ModFallbackZipUrl);
    }

    // ── Private helpers — Steam / Outer Wilds detection ───────────────────────

    /// The Outer Wilds install dir to use: the override (if set and valid) wins,
    /// else the Steam-detected install. Null when nothing is found.
    private string? ResolveOuterWildsDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeOuterWildsDir(ov))
            return ov;

        try { return DetectSteamOuterWildsDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Outer Wilds if it has OuterWilds.exe and/or the
    /// OuterWilds_Data folder.
    private static bool LooksLikeOuterWildsDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, "OuterWilds.exe"))) return true;
            if (Directory.Exists(Path.Combine(dir, "OuterWilds_Data"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Outer Wilds install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, find appmanifest_753640.
    private static string? DetectSteamOuterWildsDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{OW_STEAM_APP_ID}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common     = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeOuterWildsDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolder);
                    if (LooksLikeOuterWildsDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry.
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
            int open = text.IndexOf('"', i);
            if (open < 0) yield break;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) yield break;
            string raw = text.Substring(open + 1, close - open - 1);
            yield return raw.Replace("\\\\", "\\");
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

    // ── Private helpers — OWML detection ─────────────────────────────────────

    /// Find OWML.Launcher.exe — first look in the game directory (the documented
    /// install location where OWML is extracted next to OuterWilds.exe), then
    /// check the %APPDATA%\OuterWilds directory as a secondary location.
    private static string? FindOwmlExe(string owDir)
    {
        // Primary: OWML extracted into the game folder (standard install)
        string owmlInGame = Path.Combine(owDir, "OWML.Launcher.exe");
        if (File.Exists(owmlInGame)) return owmlInGame;

        // Also check for OWML in a sub-folder named "OWML" within the game dir
        // (some install guides use this layout)
        string owmlSubDir = Path.Combine(owDir, "OWML", "OWML.Launcher.exe");
        if (File.Exists(owmlSubDir)) return owmlSubDir;

        // Secondary: %APPDATA%\OuterWilds (alternative OWML install target)
        string owmlAppData = Path.Combine(OwmlAppDataDir, "OWML.Launcher.exe");
        if (File.Exists(owmlAppData)) return owmlAppData;

        return null;
    }

    // ── Private helpers — AP mod detection ───────────────────────────────────

    /// Find the Archipelago AP mod directory under the OWML Mods folder.
    /// The Ixrec Outer Wilds AP mod has OWML uniqueName "Ixrec.ArchipelagoRandomizer"
    /// and is typically installed in a folder of that name. Returns the folder path
    /// or null if not found.
    private static string? FindInstalledApModDir()
    {
        try
        {
            if (!Directory.Exists(OwmlModsDir)) return null;

            foreach (string modDir in Directory.EnumerateDirectories(OwmlModsDir))
            {
                string manifestPath = Path.Combine(modDir, "manifest.json");
                if (!File.Exists(manifestPath)) continue;

                try
                {
                    string txt = File.ReadAllText(manifestPath);
                    // Primary: uniqueName "Ixrec.ArchipelagoRandomizer".
                    if (txt.IndexOf("Ixrec.ArchipelagoRandomizer", StringComparison.OrdinalIgnoreCase) >= 0)
                        return modDir;
                    // Broader: manifest contains both "Archipelago" and ("OuterWilds" or "Ixrec").
                    if (txt.IndexOf("Archipelago", StringComparison.OrdinalIgnoreCase) >= 0
                        && (txt.IndexOf("OuterWilds", StringComparison.OrdinalIgnoreCase) >= 0
                            || txt.IndexOf("Ixrec", StringComparison.OrdinalIgnoreCase) >= 0))
                        return modDir;

                    // Dir-name check for "ixrec" or "archipelago" as fallback.
                    string dirName = Path.GetFileName(modDir);
                    if (dirName.IndexOf("ixrec", StringComparison.OrdinalIgnoreCase) >= 0
                        || dirName.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                        return modDir;
                }
                catch { /* unreadable manifest — skip */ }
            }

            // Last resort: sub-directory name contains "ixrec" or "archipelago".
            foreach (string modDir in Directory.EnumerateDirectories(OwmlModsDir))
            {
                string dirName = Path.GetFileName(modDir);
                if (dirName.IndexOf("ixrec", StringComparison.OrdinalIgnoreCase) >= 0
                    || dirName.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return modDir;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Outer Wilds: prefer OWML.Launcher.exe in the detected install (which
    /// patches the game and launches it with mods); fall back to OuterWilds.exe
    /// directly for standalone play; fall back to steam:// URL.
    private void StartOuterWilds()
    {
        string? owDir   = ResolveOuterWildsDir();
        string? owmlExe = owDir != null ? FindOwmlExe(owDir) : null;

        // Preferred: launch via OWML (mods active)
        if (owmlExe != null && File.Exists(owmlExe))
        {
            LaunchExe(owmlExe, Path.GetDirectoryName(owmlExe)!);
            return;
        }

        // Fallback: launch OuterWilds.exe directly (no mods — AP mod won't be active)
        if (owDir != null)
        {
            string owExe = Path.Combine(owDir, "OuterWilds.exe");
            if (File.Exists(owExe))
            {
                LaunchExe(owExe, owDir);
                return;
            }
        }

        // Last resort: Steam protocol
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { /* fall through */ }
        }

        throw new FileNotFoundException(
            "Could not find OWML.Launcher.exe or OuterWilds.exe. Open this game's " +
            "Settings and pick your Outer Wilds install folder, or install Outer Wilds " +
            "via Steam. Use Install to set up OWML and the Archipelago mod.",
            "OWML.Launcher.exe");
    }

    private void LaunchExe(string exePath, string workingDir)
    {
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = workingDir,
            UseShellExecute  = true,
        }) ?? throw new InvalidOperationException(
            $"Failed to start process: {exePath}");

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

    // ── Private helpers — download / install OWML ─────────────────────────────

    /// Download OWML from GitHub and extract it into the Outer Wilds game folder
    /// (next to OuterWilds.exe — the documented standard install location).
    private async Task DownloadAndInstallOwmlAsync(
        string owDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        progress.Report((14, "Resolving latest OWML release..."));
        var (version, zipUrl) = await ResolveLatestOwmlAsync(ct);
        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the OWML download on GitHub. Check your internet " +
                "connection, or download OWML manually from " + OwmlRepoUrl +
                " and extract OWML.zip into your Outer Wilds game folder.");

        string tempZip = Path.Combine(Path.GetTempPath(),
            $"owml-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((16, $"Downloading OWML {version}..."));
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
                        int pct = (int)(16 + 14 * downloaded / total);
                        progress.Report((pct, $"Downloading OWML... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((32, "Installing OWML into the Outer Wilds folder..."));
            // OWML.zip extracts with its files at the zip root (OWML.Launcher.exe, etc.)
            // or possibly inside a single wrapping folder. Detect and merge correctly.
            string tempExtract = Path.Combine(Path.GetTempPath(),
                $"owml-{version}-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tempExtract);
                System.IO.Compression.ZipFile.ExtractToDirectory(
                    tempZip, tempExtract, overwriteFiles: true);

                // If the zip wrapped everything in a single sub-folder, unwrap.
                string mergeRoot = tempExtract;
                if (!File.Exists(Path.Combine(mergeRoot, "OWML.Launcher.exe")))
                {
                    string[] subdirs = Directory.GetDirectories(mergeRoot);
                    if (subdirs.Length == 1
                        && File.Exists(Path.Combine(subdirs[0], "OWML.Launcher.exe")))
                        mergeRoot = subdirs[0];
                }

                MergeDirectory(mergeRoot, owDir);
            }
            finally
            {
                try { if (Directory.Exists(tempExtract))
                    Directory.Delete(tempExtract, recursive: true); }
                catch { }
            }

            progress.Report((38, $"OWML {version} installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — download / install AP mod ───────────────────────────

    /// Download the Archipelago mod zip and extract it into the OWML Mods directory
    /// (%APPDATA%\OuterWilds\Mods\). OWML mods are each in their own sub-folder
    /// with a manifest.json at the root of that sub-folder.
    private async Task DownloadAndInstallApModAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(OwmlModsDir);

        string tempZip = Path.Combine(Path.GetTempPath(),
            $"ow-archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"ow-archipelago-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((48, $"Downloading Archipelago mod {version}..."));
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
                        int pct = (int)(48 + 32 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading Archipelago mod... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((82, "Extracting the Archipelago mod..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, tempExtract, overwriteFiles: true);

            // An OWML mod zip can be laid out in two ways:
            //   (a) The manifest.json is at the zip root → we merge to a new
            //       named sub-folder under Mods\.
            //   (b) The zip contains a single sub-folder that IS the mod folder
            //       (already named correctly) → move that folder into Mods\.
            progress.Report((88, "Installing the mod into OWML Mods folder..."));
            InstallOwmlMod(tempExtract, version);

            progress.Report((95, "Archipelago mod installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract))
                Directory.Delete(tempExtract, recursive: true); }
            catch { }
        }
    }

    /// Place the extracted mod files into the OWML Mods directory under the correct
    /// sub-folder name. Handles both zip layouts (manifest at root, or single
    /// wrapping sub-folder).
    private static void InstallOwmlMod(string tempExtract, string version)
    {
        // Determine the manifest path to decide on layout.
        string manifestAtRoot = Path.Combine(tempExtract, "manifest.json");
        string modFolderName;
        string sourceDir;

        if (File.Exists(manifestAtRoot))
        {
            // Layout (a): manifest is at the zip root. Derive the folder name from
            // the manifest's "uniqueName" field, or fall back to a sensible default.
            // The OWML uniqueName for this mod is "Ixrec.ArchipelagoRandomizer".
            modFolderName = DeriveModFolderName(manifestAtRoot) ?? "Ixrec.ArchipelagoRandomizer";
            sourceDir = tempExtract;
        }
        else
        {
            // Layout (b): look for a single sub-folder that contains manifest.json.
            string[] subdirs = Directory.GetDirectories(tempExtract);
            string? found    = null;
            foreach (string sub in subdirs)
            {
                if (File.Exists(Path.Combine(sub, "manifest.json")))
                { found = sub; break; }
            }

            if (found != null)
            {
                modFolderName = DeriveModFolderName(Path.Combine(found, "manifest.json"))
                                ?? Path.GetFileName(found)
                                ?? "Ixrec.ArchipelagoRandomizer";
                sourceDir = found;
            }
            else
            {
                // No manifest found: copy as-is into a best-guess folder name.
                modFolderName = "Ixrec.ArchipelagoRandomizer";
                sourceDir = tempExtract;
            }
        }

        string destDir = Path.Combine(OwmlModsDir, modFolderName);
        if (Directory.Exists(destDir))
        {
            try { Directory.Delete(destDir, recursive: true); } catch { }
        }
        MergeDirectory(sourceDir, destDir);
    }

    /// Read the "uniqueName" field from an OWML manifest.json file to use as the
    /// mod folder name. Returns null on any failure.
    private static string? DeriveModFolderName(string manifestPath)
    {
        try
        {
            string json = File.ReadAllText(manifestPath);
            using var doc = JsonDocument.Parse(json);
            // OWML manifests use "uniqueName" for the folder identifier.
            if (doc.RootElement.TryGetProperty("uniqueName", out var uname))
            {
                string? name = uname.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                    return SanitizeFolderName(name);
            }
            // Some manifests only have "name".
            if (doc.RootElement.TryGetProperty("name", out var nname))
            {
                string? name = nname.GetString();
                if (!string.IsNullOrWhiteSpace(name))
                    return SanitizeFolderName(name);
            }
        }
        catch { }
        return null;
    }

    /// Strip path-invalid characters from a proposed folder name.
    private static string SanitizeFolderName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (char c in name)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.ToString();
    }

    /// Recursively copy everything under <src> into <dst>, overwriting files and
    /// creating directories as needed. Never deletes anything in <dst> that is not
    /// being overwritten.
    private static void MergeDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, dir);
            Directory.CreateDirectory(Path.Combine(dst, rel));
        }
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel    = Path.GetRelativePath(src, file);
            string target = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // ── Private helpers — tag normalization ───────────────────────────────────

    /// "v1.2.3" → "1.2.3"; else trimmed as-is.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class OwSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private OwSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<OwSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(OwSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(
                SettingsSidecarPath,
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
        var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s);
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings(); s.ModVersion = v; SaveSettings(s);
    }
}
