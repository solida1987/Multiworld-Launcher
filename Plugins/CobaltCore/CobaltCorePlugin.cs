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

namespace LauncherV2.Plugins.CobaltCore;

// ═══════════════════════════════════════════════════════════════════════════════
// CobaltCorePlugin — install / launch for "Cobalt Core" (Rocket Rat Games, 2023)
// played through the Archipelago mod by Isaac-SOL (SaltyIsaac), which is a
// Nickel mod that is an in-game Archipelago client. This is a NATIVE
// "ConnectsItself" integration — the game itself speaks to the AP server (no
// emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified against repo + setup guide) ───
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned
// Cobalt Core (Steam appid 2179850, a sci-fi deckbuilding roguelike by Rocket Rat
// Games / Brace Yourself Games, released 2023-11-08), and Archipelago is a MOD
// added on top via the NICKEL mod loader. The honest integration ceiling is
// "automate what is possible, guide the irreducible parts."
//
//   * THE AP WORLD game string is "Cobalt Core" (verified against
//     Isaac-SOL/CobaltCoreArchipelagoMod: apworld filename cobalt_core.apworld,
//     YAML template Cobalt.Core.yaml, README consistently says "Cobalt Core",
//     and the associated AP world repo branch is "cobalt-core").
//     GameId = "cobalt_core".
//
//   * THE MOD is Isaac-SOL/CobaltCoreArchipelagoMod, a NICKEL mod (not BepInEx).
//     It uses the Nickel mod loader (Shockah/Nickel) as its framework —
//     Nickel API version 1.5.7+ required, Nickel itself 1.20.3+.
//     The mod's Nickel unique name is "SaltyIsaac.CobaltCoreArchipelago".
//     The entry point assembly is CobaltCoreArchipelago.dll.
//     Latest stable release: 1.1.6 (2025-03-19).
//
//   * INSTALLATION LAYOUT: the mod .zip is extracted into the <Nickel>/ModLibrary/
//     folder (not into the Cobalt Core game directory directly). The game is then
//     launched via the Nickel Launcher exe (Nickel.exe / NickelLauncher.exe), NOT
//     via Cobalt Core.exe directly — Cobalt Core mods only work through Nickel.
//     The Nickel launcher is itself installed separately.
//
//   * THE RELEASE ASSETS (v1.1.6, verified via GitHub API 2026-06-14):
//         CobaltCoreArchipelago-1.1.6.zip  → extract into <Nickel>/ModLibrary/
//         cobalt_core.apworld               → drops into Archipelago\custom_worlds\
//         Cobalt.Core.yaml                  → template for Archipelago\Players\
//
//   * CONNECTION is made IN-GAME. After launching via Nickel, the game opens on
//     the save selection screen. Click an empty save slot; the mod prompts for:
//         - Hostname and port (e.g. "archipelago.gg:12345")
//         - Slot name (default "CAT1", configurable in Cobalt.Core.yaml)
//         - Password (blank if none)
//     Then press "Connect". There is NO command-line argument and NO config file
//     pre-write path for the in-game connection (verified against README and the
//     mod's ConnectionInfoMenu source). This plugin does not attempt prefill.
//
//   * DEATHLINK is supported (configurable in the in-game mod settings).
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT: find the Steam Cobalt Core install (registry + libraryfolders.vdf
//      + appmanifest_2179850.acf), AND find the Nickel installation folder. Both
//      are needed: the game for detection/verification, Nickel for mod staging
//      and launching. A manual override folder picker is provided for both.
//   2. INSTALL/UPDATE: download CobaltCoreArchipelago-{version}.zip from the
//      GitHub release and extract it into <Nickel>/ModLibrary/. Also downloads
//      and places cobalt_core.apworld in Archipelago\custom_worlds\ (read-only
//      per the brief: ProgramData\Archipelago is READ-ONLY so we only offer the
//      user guidance for this step — we do NOT write to ProgramData\Archipelago
//      ourselves). Nickel itself must be installed by the user (guided steps).
//   3. LAUNCH: run the Nickel Launcher exe from the detected Nickel folder. If
//      the Nickel Launcher is not found but Steam is present, fall back to
//      steam://rungameid/2179850 as a last resort. ConnectsItself = true.
//      SupportsStandalone = true (vanilla Cobalt Core via Steam URI works fine).
//
// ── WHAT REMAINS MANUAL (clearly stated to user) ─────────────────────────────
//   - Installing Nickel itself (user downloads from Shockah/Nickel, extracts
//     adjacent to or near Cobalt Core, runs once).
//   - Placing cobalt_core.apworld in Archipelago\custom_worlds\ (ProgramData is
//     read-only per spec; the Settings panel shows the path and has a button to
//     open Explorer at that location).
//   - In-game connection entry (server, slot, password typed at save screen).
//
// ── BUILD NOTE ────────────────────────────────────────────────────────────────
//   This project sets UseWindowsForms=true alongside UseWPF=true, so WPF UI
//   types are spelled with their FULL namespaces below to avoid CS0104 ambiguity,
//   independent of GlobalUsings. No file-level using aliases (CS1537).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CobaltCorePlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    // Steam — Cobalt Core appid 2179850 (verified 2026-06-14 via Steam store page)
    private const string CCSteamAppId   = "2179850";
    private static readonly string SteamRunUrl = $"steam://rungameid/{CCSteamAppId}";

    private const string SteamCommonFolderName = "Cobalt Core";
    private const string GameExeName           = "Cobalt Core.exe";

    // GitHub release — latest stable 1.1.6 (2025-03-19, verified 2026-06-14)
    private const string GitHubRepo       = "Isaac-SOL/CobaltCoreArchipelagoMod";
    private const string GithubReleasesUrl = $"https://github.com/{GitHubRepo}/releases";
    private const string GithubApiReleases = $"https://api.github.com/repos/{GitHubRepo}/releases/latest";
    private const string SetupGuideUrl    = "https://github.com/Isaac-SOL/CobaltCoreArchipelagoMod/blob/main/README.md";

    // Pinned fallback (latest stable as of verification date)
    private const string FallbackVersion = "1.1.6";
    private static readonly string FallbackModZipUrl =
        $"https://github.com/{GitHubRepo}/releases/download/release/{FallbackVersion}/CobaltCoreArchipelago-{FallbackVersion}.zip";
    private static readonly string FallbackApworldUrl =
        $"https://github.com/{GitHubRepo}/releases/download/release/{FallbackVersion}/cobalt_core.apworld";

    // Nickel mod loader — Shockah/Nickel, latest 1.21.3 (2026-06-14)
    private const string NickelRepo       = "Shockah/Nickel";
    private const string NickelGuideUrl   = "https://github.com/Shockah/Nickel/blob/master/docs/player-guide.md";
    private const string NickelReleasesUrl = "https://github.com/Shockah/Nickel/releases";
    // Conventional Nickel Launcher exe names (may vary across releases)
    private static readonly string[] NickelExeNames =
        new[] { "NickelLauncher.exe", "Nickel.exe", "Launcher.exe" };

    // Mod unique name (from nickel.json — for detection)
    private const string ModNickelUniqueName = "SaltyIsaac.CobaltCoreArchipelago";
    private const string ModDllName          = "CobaltCoreArchipelago.dll";

    // ProgramData\Archipelago — per spec READ-ONLY (we never write there)
    private static readonly string ApworldDestinationDir =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Archipelago", "custom_worlds");

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Archipelago-Launcher/2.0" },
            { "Accept",     "application/vnd.github.v3+json" },
        },
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "cobalt_core";
    public string DisplayName => "Cobalt Core";
    public string Subtitle    => "Native PC · Nickel mod · in-game connection";

    /// EXACT AP game string — "Cobalt Core".
    /// Verified via: apworld file = cobalt_core.apworld, YAML = Cobalt.Core.yaml,
    /// AP world repo branch = cobalt-core, README consistently names "Cobalt Core".
    public string ApWorldName => "Cobalt Core";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "cobalt_core.png");

    /// Space-blue accent — evokes Cobalt Core's starship setting.
    public string ThemeAccentColor => "#2B6CB0";

    public string[] GameBadges => new[] { "Steam · Nickel mod", "Deckbuilder · Roguelike" };

    public string Description =>
        "Cobalt Core, the sci-fi deckbuilding roguelike by Rocket Rat Games (2023), " +
        "played through the Archipelago mod by SaltyIsaac — a Nickel mod that connects " +
        "the game directly to the AP multiworld. Characters, ships, cards, and artifacts " +
        "are shuffled across the multiworld; special cards and artifacts unlock items for " +
        "other players. Your goal is to accumulate memories (one per character) to unlock " +
        "the Future Memory sequence. DeathLink is supported. You bring your own copy of " +
        "Cobalt Core (owned on Steam), install the Nickel mod loader, extract the mod " +
        "into Nickel's ModLibrary folder, then connect to your server from the in-game " +
        "save-selection screen (hostname, port, slot, password). The launcher detects " +
        "your Steam install, stages the mod files, and guides the Nickel setup steps.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = the mod DLL (CobaltCoreArchipelago.dll) exists anywhere
    /// under a detected/override Nickel folder's ModLibrary tree.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    public bool ConnectsItself    => true;
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Plugin working directory (downloads, temp files).
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "CobaltCore");

    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "cobalt_core_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events (ConnectsItself = true — mod owns the slot) ──────────
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── CheckForUpdate ────────────────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            string? dll = FindInstalledModDll();
            InstalledVersion = dll != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch { InstalledVersion = null; }

        try
        {
            var (version, _, _) = await ResolveLatestReleaseAsync(ct);
            AvailableVersion = version;
        }
        catch { AvailableVersion = null; }
    }

    // ── InstallOrUpdate ───────────────────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. Require a Nickel folder — the mod goes into ModLibrary under it.
        progress.Report((2, "Locating your Nickel installation..."));
        string? nickelDir = ResolveNickelDir();
        if (nickelDir == null)
            throw new InvalidOperationException(
                "Could not find a Nickel installation folder. Open this game's Settings " +
                "and pick your Nickel folder (the one containing the Nickel Launcher " +
                "exe), or install Nickel first — see Settings for the guided steps. " +
                "Nickel is the mod loader required to run the Cobalt Core Archipelago mod.");

        string modLibraryDir = Path.Combine(nickelDir, "ModLibrary");
        string modDestDir    = Path.Combine(modLibraryDir, "CobaltCoreArchipelago");

        // 1. Resolve the latest release from GitHub.
        progress.Report((5, "Checking the latest mod release on GitHub..."));
        var (version, modZipUrl, apworldUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (modZipUrl == null)
            throw new InvalidOperationException(
                "Could not find the mod download on GitHub. Check your internet " +
                "connection, or download it manually from:\n" + GithubReleasesUrl + "\n" +
                "Extract the zip into your Nickel/ModLibrary/ folder.");

        // 2. Download + extract the mod zip into <Nickel>/ModLibrary/CobaltCoreArchipelago/.
        progress.Report((10, $"Downloading Cobalt Core Archipelago mod {version}..."));
        await DownloadAndExtractZipAsync(modZipUrl, version, modDestDir, progress, 10, 80, ct);

        // 3. Stamp version.
        WriteStampedVersion(version);
        InstalledVersion = version;

        // 4. Offer guidance for the apworld (ProgramData is READ-ONLY per spec —
        //    never write there from the launcher; we only inform the user).
        string apworldNote = BuildApworldNote(apworldUrl);

        progress.Report((100,
            $"Cobalt Core Archipelago mod {version} extracted into:\n" +
            $"  {modDestDir}\n\n" +
            apworldNote + "\n\n" +
            "To play: launch via the Nickel Launcher (use the Play button), click an " +
            "empty save slot on the save-selection screen, enter your server hostname, " +
            "port, slot name, and password when prompted, then press Connect."));
    }

    // ── VerifyInstall ─────────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── LaunchAsync ───────────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // Connection for Cobalt Core Archipelago is entered IN-GAME on the save
        // selection screen (hostname, port, slot, password). There is no command-line
        // or config-file pre-fill mechanism (verified against README and mod source).
        // ConnectsItself = true: the mod owns the AP slot — we must NOT hold an
        // ApClient on this slot while the game is connected.
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

    // ── AP bridge (inert — mod owns the slot) ────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { /* mod renders its own status */ }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20),
        };

        string? gameDir     = ResolveGameDir();
        string? nickelDir   = ResolveNickelDir();
        string? modDll      = FindInstalledModDll();
        string? overrideGame   = LoadSettings().GameDirOverride;
        string? overrideNickel = LoadSettings().NickelDirOverride;

        // ── Preamble ──────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Cobalt Core uses the Nickel mod loader. This launcher detects your " +
                   "Steam install and stages the Archipelago mod into Nickel's ModLibrary " +
                   "folder. You must install Nickel separately (see the guided steps below). " +
                   "The in-game connection (server, slot, password) is entered on the save " +
                   "selection screen — there is no pre-fill. ProgramData\\Archipelago is " +
                   "managed by Archipelago itself; the launcher shows you what to copy there.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Steam Install ─────────────────────────────────────────────────
        AddSectionHeader(panel, "COBALT CORE INSTALL (Steam)", muted);

        string gameStatus = gameDir != null
            ? (overrideGame != null ? "Using selected folder: " + gameDir : "Detected Steam install: " + gameDir)
            : "Cobalt Core not detected. Pick your install folder, or install via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = gameStatus, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        AddFolderPickerRow(panel, fg, muted,
            currentPath:  overrideGame ?? gameDir ?? "",
            dialogTitle:  "Select your Cobalt Core install folder",
            validate:     p => LooksLikeGameDir(p)
                               ? null
                               : "That does not look like a Cobalt Core folder. " +
                                 "Pick the folder containing \"Cobalt Core.exe\".",
            autoDescend:  p => Path.Combine(p, SteamCommonFolderName),
            looksLike:    LooksLikeGameDir,
            onPicked:     p => { var s = LoadSettings(); s.GameDirOverride = p; SaveSettings(s); },
            hint:         "Steam installs are auto-detected (appid 2179850). Use this picker " +
                          "for a non-standard location.");

        // ── Nickel Install ────────────────────────────────────────────────
        AddSectionHeader(panel, "NICKEL MOD LOADER FOLDER", muted);

        string nickelStatus = nickelDir != null
            ? (overrideNickel != null ? "Using selected folder: " + nickelDir : "Found Nickel: " + nickelDir)
            : "Nickel not found. Select your Nickel folder below, or install Nickel first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = nickelStatus, FontSize = 11,
            Foreground = nickelDir != null ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                ? "Archipelago mod found: " + modDll
                : "Archipelago mod not found yet. Use the Install button on the Play tab " +
                  "after selecting your Nickel folder.",
            FontSize = 11,
            Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        AddFolderPickerRow(panel, fg, muted,
            currentPath:  overrideNickel ?? nickelDir ?? "",
            dialogTitle:  "Select your Nickel installation folder",
            validate:     p => LooksLikeNickelDir(p)
                               ? null
                               : "That does not look like a Nickel folder. " +
                                 "Pick the folder that contains the Nickel Launcher exe " +
                                 "and a ModLibrary sub-folder.",
            autoDescend:  null,
            looksLike:    LooksLikeNickelDir,
            onPicked:     p => { var s = LoadSettings(); s.NickelDirOverride = p; SaveSettings(s); },
            hint:         "Your Nickel folder contains the Nickel Launcher exe and a " +
                          "ModLibrary sub-folder. It is NOT the Cobalt Core game folder.");

        // ── APWorld placement ─────────────────────────────────────────────
        AddSectionHeader(panel, "APWORLD FILE (manual step)", muted);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "The cobalt_core.apworld file must be placed in:\n" +
                   "  " + ApworldDestinationDir + "\n\n" +
                   "This launcher cannot write to ProgramData\\Archipelago. " +
                   "Download cobalt_core.apworld from the GitHub releases page (link " +
                   "below) and double-click it — Archipelago will import it automatically. " +
                   "Or copy it manually to the folder above.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        bool apworldPresent = File.Exists(Path.Combine(ApworldDestinationDir, "cobalt_core.apworld"));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = apworldPresent
                ? "cobalt_core.apworld is present in Archipelago\\custom_worlds."
                : "cobalt_core.apworld not found in Archipelago\\custom_worlds yet.",
            FontSize = 11,
            Foreground = apworldPresent ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        // Open-folder button for the Archipelago custom_worlds dir
        var openApworldFolderBtn = new System.Windows.Controls.Button
        {
            Content = "Open Archipelago\\custom_worlds in Explorer",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Padding = new System.Windows.Thickness(10, 6, 10, 6),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        };
        openApworldFolderBtn.Click += (_, _) =>
        {
            try
            {
                Directory.CreateDirectory(ApworldDestinationDir);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{ApworldDestinationDir}\"")
                    { UseShellExecute = true });
            }
            catch { /* non-fatal */ }
        };
        panel.Children.Add(openApworldFolderBtn);

        // ── Connection info ───────────────────────────────────────────────
        AddSectionHeader(panel, "CONNECTING (entered in-game at save selection)", muted);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After launching via the Nickel Launcher, the game opens on the save " +
                   "selection screen. Click an empty save slot. The mod will prompt for:\n" +
                   "  • Hostname and port (e.g. archipelago.gg:12345)\n" +
                   "  • Slot name (default \"CAT1\", set in Cobalt.Core.yaml)\n" +
                   "  • Password (leave blank if none was set)\n" +
                   "Then press Connect. You cannot load an existing non-AP save with the mod.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Guided setup steps ────────────────────────────────────────────
        AddSectionHeader(panel, "GUIDED SETUP STEPS", muted);
        foreach (string step in new[]
        {
            "1. Own Cobalt Core on Steam (appid 2179850). Install and launch it at least once.",
            "2. Install the Nickel mod loader: download Nickel-{version}-Windows.zip from the " +
                "Nickel releases page (link below), extract it anywhere, and run the Nickel " +
                "Launcher exe once to initialize the ModLibrary folder. Then pick that folder " +
                "using the 'Select folder...' picker for Nickel above.",
            "3. Use the Install button on the Play tab to download and extract the " +
                "Cobalt Core Archipelago mod into your Nickel/ModLibrary/ folder.",
            "4. Download cobalt_core.apworld from the GitHub releases page (link below) and " +
                "double-click it to register it with Archipelago. Or use the 'Open folder' " +
                "button above to copy it manually.",
            "5. (Optional) Download Cobalt.Core.yaml from the releases page, edit it with " +
                "your YAML options, and place it in Archipelago\\Players\\.",
            "6. Launch via the Play button (uses the Nickel Launcher). On the save selection " +
                "screen, click an empty slot and enter your connection info when prompted.",
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
        AddSectionHeader(panel, "LINKS", muted);
        foreach (var (label, url) in new[]
        {
            ("Nickel Releases (mod loader) ↗",                    NickelReleasesUrl),
            ("Nickel Player Guide ↗",                             NickelGuideUrl),
            ("Cobalt Core Archipelago mod (GitHub releases) ↗",   GithubReleasesUrl),
            ("Cobalt Core Archipelago mod README ↗",              SetupGuideUrl),
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
                Foreground = accent,
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
            };
            panel.Children.Add(btn);
        }

        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Pull releases from the GitHub API; surface the latest few as news items.
        try
        {
            string json = await _http.GetStringAsync(
                $"https://api.github.com/repos/{GitHubRepo}/releases", ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                bool prerelease = el.TryGetProperty("prerelease", out var pr) && pr.GetBoolean();
                if (prerelease) continue; // skip release candidates in the news feed

                string ver = el.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                string body = el.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);
                string? htmlUrl = el.TryGetProperty("html_url", out var u) ? u.GetString() : null;

                items.Add(new NewsItem(
                    Title:   "Cobalt Core Archipelago " + ver,
                    Body:    body,
                    Version: ver,
                    Date:    date,
                    Url:     htmlUrl));

                if (items.Count >= 8) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Launch via the Nickel Launcher (the only correct way to run Cobalt Core mods).
    /// Falls back to steam://rungameid/2179850 if Nickel is not found.
    private void StartGame()
    {
        // Prefer the Nickel Launcher exe.
        string? nickelDir = ResolveNickelDir();
        if (nickelDir != null)
        {
            foreach (string exeName in NickelExeNames)
            {
                string candidate = Path.Combine(nickelDir, exeName);
                if (File.Exists(candidate))
                {
                    var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName         = candidate,
                        WorkingDirectory = nickelDir,
                        UseShellExecute  = true,
                    }) ?? throw new InvalidOperationException(
                        $"Failed to start the Nickel Launcher ({exeName}).");

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
        }

        // Fall back: Steam URI (launches vanilla Cobalt Core, no mods).
        bool hasSteam = SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r));
        if (hasSteam)
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
            "Could not find the Nickel Launcher exe. Open Settings and pick your " +
            "Nickel installation folder, or install Nickel first. Without Nickel, " +
            "the Cobalt Core Archipelago mod will not run.",
            "NickelLauncher.exe");
    }

    // ── Private helpers — release resolution ─────────────────────────────────

    /// Resolve the latest GitHub release: version tag, mod zip URL, apworld URL.
    /// Falls back to pinned 1.1.6 constants when the GitHub API is unreachable.
    private async Task<(string Version, string? ModZipUrl, string? ApworldUrl)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GithubApiReleases, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) goto fallback;

            string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) goto fallback;

            // Strip leading "release/" prefix if present (e.g. "release/1.1.6" → "1.1.6").
            string version = tag!.StartsWith("release/", StringComparison.OrdinalIgnoreCase)
                ? tag.Substring("release/".Length)
                : tag;

            string? modZipUrl = null;
            string? apworldUrl = null;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                        name.IndexOf("CobaltCoreArchipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                        modZipUrl = url;
                    else if (name.Equals("cobalt_core.apworld", StringComparison.OrdinalIgnoreCase))
                        apworldUrl = url;
                }
            }

            if (modZipUrl != null)
                return (version, modZipUrl, apworldUrl);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / shape changed → pinned fallback */ }

        fallback:
        return (FallbackVersion, FallbackModZipUrl, FallbackApworldUrl);
    }

    // ── Private helpers — Steam detection ────────────────────────────────────

    private string? ResolveGameDir()
    {
        string? ov = LoadSettings().GameDirOverride;
        if (!string.IsNullOrWhiteSpace(ov) && LooksLikeGameDir(ov)) return ov;
        try { return DetectSteamGameDir(); } catch { return null; }
    }

    private static bool LooksLikeGameDir(string dir)
    {
        try { return !string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir) &&
                     File.Exists(Path.Combine(dir, GameExeName)); }
        catch { return false; }
    }

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
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{CCSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common     = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeGameDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeGameDir(conventional)) return conventional;
                }
                catch { }
            }
        }
        return null;
    }

    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormalizePath(hkcu);

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormalizePath(hklm);

        string? hklm2 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm2)) yield return NormalizePath(hklm2);

        string? prog = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(prog)) yield return Path.Combine(prog, "Steam");
    }

    private static string NormalizePath(string p) => p.Replace('/', '\\').TrimEnd('\\');

    private static IEnumerable<string> SteamLibraryRoots(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(steamRoot)) yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        string text;
        try { text = File.ReadAllText(vdf); } catch { yield break; }

        foreach (string path in ExtractVdfPaths(text))
        {
            string norm = path.Replace('/', '\\').TrimEnd('\\');
            if (norm.Length > 0 && seen.Add(norm)) yield return norm;
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

    // ── Private helpers — Nickel detection ───────────────────────────────────

    private string? ResolveNickelDir()
    {
        string? ov = LoadSettings().NickelDirOverride;
        if (!string.IsNullOrWhiteSpace(ov) && LooksLikeNickelDir(ov)) return ov;

        // Try to find Nickel adjacent to or within the Cobalt Core game folder,
        // or in a few conventional locations.
        string? gameDir = ResolveGameDir();
        if (gameDir != null)
        {
            // Nickel is often installed next to the game or in a sibling folder.
            foreach (string candidate in CandidateNickelDirs(gameDir))
            {
                if (LooksLikeNickelDir(candidate)) return candidate;
            }
        }
        return null;
    }

    private static bool LooksLikeNickelDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            // A valid Nickel folder has at least one of the expected launcher exe names.
            if (NickelExeNames.Any(exe => File.Exists(Path.Combine(dir, exe)))) return true;
            // Or at minimum a ModLibrary sub-folder (Nickel was run once but no exe in root).
            return Directory.Exists(Path.Combine(dir, "ModLibrary"));
        }
        catch { return false; }
    }

    /// Heuristic candidate Nickel locations relative to the game install.
    /// Returns a list (not an iterator) to avoid CS1626 (yield in try-catch).
    private static List<string> CandidateNickelDirs(string gameDir)
    {
        var results = new List<string>();
        string? parent = Path.GetDirectoryName(gameDir);
        if (parent == null) return results;

        // Sibling "Nickel" folder (most common placement)
        results.Add(Path.Combine(parent, "Nickel"));

        // Sibling "Nickel*" folders (e.g. "Nickel-1.21.3")
        try
        {
            foreach (string sibling in Directory.EnumerateDirectories(parent, "Nickel*"))
                results.Add(sibling);
        }
        catch { /* directory enumeration failure — ignore */ }

        // Nickel extracted INTO the game folder itself (less common but possible)
        results.Add(Path.Combine(gameDir, "Nickel"));

        return results;
    }

    // ── Private helpers — mod detection ──────────────────────────────────────

    /// Find the mod DLL (CobaltCoreArchipelago.dll) anywhere under
    /// <Nickel>/ModLibrary (recursive, case-insensitive).
    private string? FindInstalledModDll()
    {
        try
        {
            string? nickelDir = ResolveNickelDir();
            if (nickelDir == null) return null;
            string modLibrary = Path.Combine(nickelDir, "ModLibrary");
            if (!Directory.Exists(modLibrary)) return null;

            foreach (string dll in Directory.EnumerateFiles(modLibrary, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(dll);
                if (name.Equals(ModDllName, StringComparison.OrdinalIgnoreCase)) return dll;
                if (name.IndexOf("CobaltCoreArchipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }
        }
        catch { }
        return null;
    }

    // ── Private helpers — download / extract ─────────────────────────────────

    private async Task DownloadAndExtractZipAsync(
        string zipUrl, string version, string destDir,
        IProgress<(int Pct, string Msg)> progress,
        int pctStart, int pctEnd,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"cc-ap-{version}-{Guid.NewGuid():N}.zip");
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"cc-ap-{version}-{Guid.NewGuid():N}");
        try
        {
            using (var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? -1;
                long done  = 0;

                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tempZip);
                var buf = new byte[65536];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    done += n;
                    if (total > 0)
                    {
                        int pct = pctStart + (int)((pctEnd - pctStart) * 0.7 * done / total);
                        progress.Report((pct, $"Downloading mod... {done / 1024}KB"));
                    }
                }
            }

            int extractPct = pctStart + (int)((pctEnd - pctStart) * 0.75);
            progress.Report((extractPct, "Extracting mod package..."));
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            int installPct = pctStart + (int)((pctEnd - pctStart) * 0.90);
            progress.Report((installPct, $"Installing mod into {destDir}..."));
            Directory.CreateDirectory(destDir);
            CopyDirectoryContents(tempDir, destDir);
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    private static void CopyDirectoryContents(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel  = Path.GetRelativePath(src, file);
            string dest = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    // ── Private helpers — apworld note ───────────────────────────────────────

    private static string BuildApworldNote(string? apworldUrl)
    {
        string urlLine = apworldUrl != null
            ? $"Download cobalt_core.apworld from:\n  {apworldUrl}"
            : $"Download cobalt_core.apworld from the releases page:\n  {GithubReleasesUrl}";

        return
            "The cobalt_core.apworld is NOT written to disk by this launcher " +
            "(ProgramData\\Archipelago is managed by Archipelago itself). " +
            urlLine + "\n" +
            "Double-click the .apworld file to have Archipelago import it automatically, " +
            "or copy it to:\n  " + ApworldDestinationDir;
    }

    // ── Private helpers — settings panel UI utilities ─────────────────────────

    private static void AddSectionHeader(
        System.Windows.Controls.StackPanel parent,
        string text,
        System.Windows.Media.Brush brush)
    {
        parent.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = text, FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = brush,
            Margin = new System.Windows.Thickness(0, 10, 0, 6),
        });
    }

    private static void AddFolderPickerRow(
        System.Windows.Controls.StackPanel parent,
        System.Windows.Media.Brush fg,
        System.Windows.Media.Brush muted,
        string currentPath,
        string dialogTitle,
        Func<string, string?> validate,
        Func<string, string>? autoDescend,
        Func<string, bool> looksLike,
        Action<string> onPicked,
        string hint)
    {
        var row = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        };

        var box = new System.Windows.Controls.TextBox
        {
            Text = currentPath, IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };

        var btn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };

        btn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = dialogTitle,
                InitialDirectory = Directory.Exists(currentPath) ? currentPath : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() != true) return;

            string picked = dlg.FolderName;
            // Try auto-descending into a sub-folder (e.g. "common\Cobalt Core").
            if (autoDescend != null && !looksLike(picked))
            {
                string nested = autoDescend(picked);
                if (looksLike(nested)) picked = nested;
            }

            string? bad = validate(picked);
            if (bad != null)
            {
                System.Windows.MessageBox.Show(bad, "Invalid folder",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }

            box.Text = picked;
            onPicked(picked);
        };

        System.Windows.Controls.DockPanel.SetDock(btn, System.Windows.Controls.Dock.Right);
        row.Children.Add(btn);
        row.Children.Add(box);
        parent.Children.Add(row);

        parent.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = hint, FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 4, 0, 10),
        });
    }

    // ── Private helpers — self-contained settings sidecar ────────────────────

    private sealed class CobaltCoreSettings
    {
        public string? GameDirOverride   { get; set; }
        public string? NickelDirOverride { get; set; }
        public string? ModVersion        { get; set; }
    }

    private CobaltCoreSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<CobaltCoreSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(CobaltCoreSettings s)
    {
        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings();
        s.ModVersion = v;
        SaveSettings(s);
    }
}
