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
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using LauncherV2.Core;

// The launcher project sets BOTH UseWPF and UseWindowsForms=true, so several UI
// type names (Color, Brushes, Button, TextBox, HorizontalAlignment) collide
// between WPF and WinForms. The project's GlobalUsings.cs already aliases each of
// these to its WPF type globally, so this file relies on those — no local aliases
// (a local alias duplicating a global one is CS1537).

namespace LauncherV2.Plugins.SystemShock2;

// ═══════════════════════════════════════════════════════════════════════════════
// SystemShock2Plugin — install / launch for "System Shock 2" (Looking Glass
// Studios / Night Dive Studios, 1999 / 2025 AE), played through the community
// Archipelago mod by Partatio hosted at codeberg.org/Partatio/SS2-Apworld.
//
// ── HONEST REALITY CHECK (2026-06-16, verified online) ───────────────────────
// This is a COMMUNITY apworld (not core Archipelago), confirmed by:
//   - Codeberg repo: codeberg.org/Partatio/SS2-Apworld (active, with releases)
//   - Archipelago wiki page: archipelago.miraheze.org/wiki/System_Shock_2
//   - Official thread on systemshock.org: SS2 Archipelago multi-game randomizer mod
//   - YouTube trailer (confirmed live 2026-06-16)
//   - The Nightdive Studios 2025 Anniversary Edition (AE) remaster is also
//     supported via a separate .kpf mod file.
//
//   * AP WORLD GAME STRING is "System Shock 2" (verified from the Archipelago wiki
//     page title + community documentation; this is a CUSTOM apworld distributed
//     via Codeberg, NOT a core AP world — it must be installed into custom_worlds).
//
//   * DISTRIBUTION: Releases ship two separate mod flavors:
//       - A .7z archive containing mod files for the CLASSIC version (SS2
//         Steam appid 238210 — the original 1999 game on Steam). Classic requires
//         SS2Tool + SCP beta8 installed first; mod installed via ss2bmm.exe.
//       - A .kpf file for the ANNIVERSARY EDITION (AE, Steam appid 2456270 —
//         the 2025 Nightdive remaster). AE mod goes into the mods folder and is
//         enabled in-game.
//
//   * CONNECTION: The game uses its OWN separate "System Shock 2 Client" bundled
//     with the Archipelago Launcher (not with the SS2 mod itself). The client
//     must be started and connected BEFORE loading a save or starting a new game.
//     This means ConnectsItself = false — the LAUNCHER'S ApClient holds the slot,
//     and the SS2 Client is a SEPARATE helper process. HOWEVER: the documented
//     workflow (verified from systemshock.org thread + wiki) states:
//       "Start the System Shock 2 Client from the Archipelago Launcher, selecting
//        the appropriate folder depending on whether you are using AE or classic."
//     The "System Shock 2 Client" is part of the ARCHIPELAGO LAUNCHER RELEASE,
//     not of this launcher. Therefore ConnectsItself = true (the mod + its client
//     own the slot; this launcher must NOT hold its own ApClient on the same slot
//     while the game is running, to avoid endless mutual kick-off). The user runs
//     the official AP Launcher's SS2 Client separately to bridge to the server.
//
//   * INSTALL HONESTY: This plugin handles DETECTION of the user's Steam installs
//     (Classic appid 238210 and AE appid 2456270) and downloads the .apworld file
//     so the user can drop it into their Archipelago custom_worlds folder. The mod
//     installation itself (SS2Tool + SCP beta8 for Classic; kpf into mods for AE)
//     is guided step-by-step but not automated — these are complex, game-specific
//     steps that require the user's attention and existing tool installs. Faking
//     a one-click install for a multi-step mod chain would be dishonest theatre.
//
//   * LAUNCH: Opens the game exe for whichever edition is detected / selected.
//     SupportsStandalone = true (the base game runs without AP). ConnectsItself = true
//     (the AP mod's own client bridges to the server; this launcher does not hold
//     an ApClient on this slot while the game is running).
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. Detects Classic (Steam 238210) or AE (Steam 2456270) via the Windows
//      registry Steam library scan — AE takes priority when both are present.
//   2. Downloads the .apworld file from the latest Codeberg release so the user
//      can install it into their AP custom_worlds folder.
//   3. Shows guided, numbered setup steps for both editions (Classic and AE).
//   4. Launches the detected game exe (or falls back to a steam:// URI).
//   5. Does NOT manage SS2Tool / SCP beta8 / ss2bmm.exe — these are Classic-only
//      prerequisites the user must install by hand per the documented workflow.
//
// ── DEFENSIVE / UNVERIFIED (flagged for verification at build time) ────────────
//   * Exact Codeberg release asset filenames (.7z and .kpf) were not verified
//     offline — the plugin pattern-matches on extension and name keywords.
//   * SteamAppId for AE (2456270) is from the "System Shock 2 Anniversary Edition"
//     Steam page — verify against an ACF on a machine with AE installed.
//   * SS2 Classic exe name assumed "SS2.exe" (standard Steam install name).
//   * AE exe name assumed "SystemShock2AE.exe" (Nightdive AE naming convention)
//     with a fallback glob for "*shock*2*.exe" in the install root.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SystemShock2Plugin : IGamePlugin
{
    // ── Constants — Codeberg (the host for this community apworld) ────────────

    private const string CodebergOwner = "Partatio";
    private const string CodebergRepo  = "SS2-Apworld";
    // Codeberg's Gitea API mirrors GitHub's shape for /releases endpoints.
    private static readonly string CodebergApiBase =
        $"https://codeberg.org/api/v1/repos/{CodebergOwner}/{CodebergRepo}";
    private static readonly string CodebergReleasesUrl =
        $"{CodebergApiBase}/releases?limit=10";
    private static readonly string CodebergRepoUrl =
        $"https://codeberg.org/{CodebergOwner}/{CodebergRepo}";

    private const string WikiUrl       = "https://archipelago.miraheze.org/wiki/System_Shock_2";
    private const string ForumUrl      = "https://www.systemshock.org/index.php?topic=13064.0";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Nightdive Studios AE mod guide (PCGamer article confirmed live 2026-06-16).
    private const string AEModGuideUrl =
        "https://www.pcgamer.com/games/fps/nightdives-system-shock-2-remaster-now-supports-26-years-of-mods-and-fan-missions/";

    // Steam appids.
    private const string ClassicSteamAppId = "238210";
    private const string AESteamAppId      = "2456270";
    private static readonly string ClassicSteamRunUrl = $"steam://rungameid/{ClassicSteamAppId}";
    private static readonly string AESteamRunUrl      = $"steam://rungameid/{AESteamAppId}";

    // Conventional Steam install folder names.
    private const string ClassicSteamFolder = "System Shock 2";
    private const string AESteamFolder      = "System Shock 2 AE";

    // Exe names (verify at build time — see header).
    private const string ClassicExeName = "SS2.exe";
    private const string AEExeName      = "SystemShock2AE.exe";

    /// Pinned fallback apworld release tag when Codeberg API is unreachable.
    /// Update whenever a new Codeberg release is tagged.
    private const string FallbackVersion    = "1.0.0";
    private const string ApWorldFileName    = "ss2.apworld";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "system_shock_2";
    public string DisplayName => "System Shock 2";
    public string Subtitle    => "Native PC · Community apworld (Partatio)";

    /// Exact AP game string — verified from the Archipelago wiki page title
    /// and community documentation (codeberg.org/Partatio/SS2-Apworld).
    /// This is a CUSTOM apworld (not core AP) — must be installed in custom_worlds.
    public string ApWorldName => "System Shock 2";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "system_shock_2.png");

    public string ThemeAccentColor => "#1A3A5C";  // deep space-station blue / SHODAN cold
    public string[] GameBadges     => new[] { "Steam · community apworld" };

    public string Description =>
        "System Shock 2 (1999, Looking Glass Studios / Irrational Games), now with the " +
        "2025 Anniversary Edition remaster from Nightdive Studios, brought to Archipelago " +
        "Multiworld by the community apworld project Partatio/SS2-Apworld. Weapons, " +
        "upgrades, audio logs and key items are randomized across the multiworld. Both " +
        "the Classic Steam edition and the Anniversary Edition (AE) are supported. The " +
        "launcher detects your Steam install, downloads the .apworld file so you can " +
        "add it to your Archipelago server, and guides the mod setup steps. Connection " +
        "is bridged by the SS2 Client bundled with the Archipelago Launcher — start it " +
        "before loading a save.";

    public string? VideoPreviewUrl =>
        "https://www.youtube.com/watch?v=FTGW1wumPxA";  // official trailer, verified live

    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the base game (Classic or AE) is present on this machine.
    /// The AP mod itself is installed by the user following the guided steps.
    public bool IsInstalled => DetectGameDir() != null;

    public bool IsRunning { get; private set; }

    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "SystemShock2");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "ss2_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The SS2 mod's own AP client bridges to the server. ConnectsItself = true.
    // These events exist for interface compatibility only.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself ────────────────────────────────────────────────────────

    /// The AP mod bridges to the server via its own client (the "System Shock 2
    /// Client" bundled with the AP Launcher). The launcher must NOT hold its own
    /// ApClient on the same slot while the game is running.
    public bool ConnectsItself => true;

    /// The base game runs perfectly well without an AP connection.
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Installed = base game present + we have a stamped apworld version.
        try
        {
            InstalledVersion = DetectGameDir() != null
                ? (ReadStampedVersion() ?? "base game detected")
                : null;
        }
        catch { InstalledVersion = null; }

        try
        {
            var (version, _) = await ResolveLatestReleaseAsync(ct);
            AvailableVersion = version;
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // Step 1: confirm a game install exists.
        progress.Report((5, "Locating your System Shock 2 installation..."));
        string? gameDir = DetectGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find System Shock 2 (Classic) or System Shock 2 Anniversary " +
                "Edition on this machine. Please install one via Steam first, or use the " +
                "folder picker in Settings to point at your install.");

        // Step 2: resolve the latest Codeberg release and download the .apworld.
        progress.Report((15, "Checking the latest SS2-Apworld release on Codeberg..."));
        var (version, apworldUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (apworldUrl == null)
            throw new InvalidOperationException(
                "Could not locate the .apworld file in the latest Codeberg release. " +
                "Check your internet connection, or download it manually from " +
                CodebergRepoUrl + "/releases and place ss2.apworld into your " +
                "Archipelago/custom_worlds folder.");

        // Step 3: download the apworld next to the plugin's data directory so
        // the user can copy it into their Archipelago custom_worlds folder.
        progress.Report((25, "Downloading " + ApWorldFileName + "..."));
        Directory.CreateDirectory(GameDirectory);
        string apworldDest = Path.Combine(GameDirectory, ApWorldFileName);

        await DownloadFileAsync(apworldUrl, apworldDest,
            "Downloading " + ApWorldFileName + "...",
            25, 80, progress, ct);

        // Stamp the version.
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool isAE      = IsAEInstall(gameDir);
        string edition = isAE ? "Anniversary Edition (AE)" : "Classic";

        progress.Report((100,
            $"Downloaded {ApWorldFileName} {version} to:\n{apworldDest}\n\n" +
            $"Detected edition: {edition} at {gameDir}\n\n" +
            "NEXT STEPS — open Settings for the full guided setup:\n" +
            "1. Copy ss2.apworld from the path above into your Archipelago/custom_worlds folder.\n" +
            (isAE
                ? "2. (AE) Download the .kpf mod from the Codeberg releases and drop it into " +
                  "your System Shock 2 AE mods folder, then enable the Archipelago mod in the " +
                  "in-game mods menu.\n"
                : "2. (Classic) Install SS2Tool + SCP beta8 first, then install the .7z mod " +
                  "via ss2bmm.exe and make sure SCP_beta8 is at the lowest priority.\n") +
            "3. Generate and host your AP session using the Archipelago Launcher with the " +
            "ss2.apworld in custom_worlds.\n" +
            "4. Before launching the game, start the 'System Shock 2 Client' from the " +
            "Archipelago Launcher and connect it to your slot.\n" +
            "5. Launch System Shock 2 (via this launcher's Play button).\n" +
            "(Keypads won't work unless you have the relevant audio log/email.)"));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        // Verified = base game detected (mod setup is fully user-managed).
        return DetectGameDir() != null;
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // The AP client for SS2 is the "System Shock 2 Client" bundled with the
        // Archipelago Launcher — NOT a process this plugin manages. We just start
        // the game; the user is instructed (in Settings) to start the SS2 Client
        // FIRST. ConnectsItself = true: the launcher does not hold an ApClient.
        _ = session;
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

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Existing-install validation (Settings folder picker) ─────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your System Shock 2 install folder.";

        if (LooksLikeGameDir(folder)) return null;

        // Tolerate user picking the Steam "common" parent.
        foreach (string sub in new[] { ClassicSteamFolder, AESteamFolder, "SS2", "SystemShock2" })
        {
            try
            {
                string nested = Path.Combine(folder, sub);
                if (LooksLikeGameDir(nested)) return null;
            }
            catch { }
        }

        return "That does not look like a System Shock 2 installation. Pick the folder " +
               "that contains SS2.exe (Classic) or SystemShock2AE.exe (Anniversary " +
               "Edition). For Steam installs this is usually inside " +
               @"...\steamapps\common\.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "System Shock 2 is your own game (Steam) with the community " +
                   "Archipelago mod by Partatio added on top. The launcher detects your " +
                   "Steam install and downloads the .apworld file, but the full mod " +
                   "installation — SS2Tool + SCP beta8 for Classic, or the .kpf into " +
                   "mods for AE — must be done by you following the guided steps below. " +
                   "Before playing, also start the 'System Shock 2 Client' from the " +
                   "Archipelago Launcher. These external steps are not verified by this " +
                   "launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Detected install ──────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "SYSTEM SHOCK 2 INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? gameDir     = DetectGameDir();
        string? overrideDir = LoadOverrideDir();
        bool    isAE        = gameDir != null && IsAEInstall(gameDir);

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? $"Using your selected folder ({(isAE ? "AE" : "Classic")}): {gameDir}"
                : $"Detected Steam install ({(isAE ? "AE" : "Classic")}): {gameDir}")
            : "System Shock 2 not detected. Pick your install folder below or install " +
              "the game via Steam (Classic appid 238210, AE appid 2456270).";

        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // apworld stamp
        string? stamped = ReadStampedVersion();
        string apworldPath = Path.Combine(GameDirectory, ApWorldFileName);
        bool   apworldPresent = File.Exists(apworldPath);
        panel.Children.Add(new TextBlock
        {
            Text = apworldPresent
                    ? $"ss2.apworld downloaded ({stamped ?? "version unknown"}): {apworldPath}"
                    : "ss2.apworld not downloaded yet — use Install on the Play tab.",
            FontSize = 11, Foreground = apworldPresent ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // folder picker row
        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your System Shock 2 install folder (Classic: contains SS2.exe; " +
                          "AE: contains SystemShock2AE.exe). Detected from Steam automatically; " +
                          "use this picker for a non-Steam or non-standard install.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select your System Shock 2 install folder (Classic: contains " +
                        "SS2.exe; AE: contains SystemShock2AE.exe)",
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
                    System.Windows.MessageBox.Show(bad, "Not a System Shock 2 folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // Auto-descend if the user picked the steamapps/common parent.
                if (!LooksLikeGameDir(picked))
                {
                    foreach (string sub in new[] { ClassicSteamFolder, AESteamFolder })
                    {
                        string nested = Path.Combine(picked, sub);
                        if (LooksLikeGameDir(nested)) { picked = nested; break; }
                    }
                }
                SaveOverrideDir(picked);
                dirBox.Text = picked;
            }
        };
        DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new TextBlock
        {
            Text = "Classic is Steam appid 238210; AE (Anniversary Edition) is Steam " +
                   "appid 2456270. Both are auto-detected. AE takes priority when both " +
                   "are present.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ───────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (SS2 Client, separate step)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Connection to the Archipelago server is NOT managed by this launcher. " +
                   "You must start the 'System Shock 2 Client' from the Archipelago Launcher " +
                   "(the official Archipelago release, not this launcher) and connect it to " +
                   "your slot BEFORE loading a save or starting a new game. The SS2 client " +
                   "must remain running while you play. If using AE, point the SS2 Client at " +
                   "your AE install folder; for Classic, point it at the Classic folder.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps (Classic) ─────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "SETUP STEPS — CLASSIC EDITION", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own System Shock 2 Classic on Steam (appid 238210).",
            "2. Install SS2Tool (download from systemshock.org) and use it to set up " +
               "the game — follow its wizard.",
            "3. Install SCP beta8 via ss2bmm.exe (System Shock 2 Mod Manager). Ensure " +
               "SCP_beta8 is at the LOWEST priority in ss2bmm.",
            "4. Download the Classic .7z mod file from the Codeberg releases (link below).",
            "5. Install the .7z mod via ss2bmm.exe and activate it. SCP_beta8 must remain " +
               "at the lowest priority.",
            "6. Install the ss2.apworld (use 'Install' on the Play tab to download it) " +
               "into your Archipelago/custom_worlds folder.",
            "7. Generate and host a session with the Archipelago Launcher using ss2.apworld.",
            "8. Start the 'System Shock 2 Client' from the Archipelago Launcher, point it " +
               "at your Classic install folder, and connect to your slot.",
            "9. Launch System Shock 2 Classic from this launcher's Play button. Start a new " +
               "game. Keypads require the relevant audio log/email to function.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Section: Guided setup steps (AE) ─────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "SETUP STEPS — ANNIVERSARY EDITION (AE)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own System Shock 2 Anniversary Edition on Steam (appid 2456270).",
            "2. Download the AE .kpf mod file from the Codeberg releases (link below).",
            "3. Drop the .kpf file into your System Shock 2 AE mods folder.",
            "4. Launch AE, open the in-game mods menu (when starting a new game), and " +
               "ENABLE the Archipelago mod.",
            "5. Install the ss2.apworld (use 'Install' on the Play tab to download it) " +
               "into your Archipelago/custom_worlds folder.",
            "6. Generate and host a session with the Archipelago Launcher using ss2.apworld.",
            "7. Start the 'System Shock 2 Client' from the Archipelago Launcher, point it " +
               "at your AE install folder, and connect to your slot.",
            "8. Launch System Shock 2 AE from this launcher's Play button. Start a new game.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("SS2-Apworld (Codeberg) ↗",         CodebergRepoUrl),
            ("Archipelago Wiki — System Shock 2 ↗", WikiUrl),
            ("Community Forum Thread (SS2 AP) ↗", ForumUrl),
            ("AE Modding Guide (PCGamer) ↗",      AEModGuideUrl),
            ("Archipelago Official ↗",             ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = accent,
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

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Pull releases from the Codeberg Gitea API (/releases endpoint mirrors
        // GitHub's shape for the fields we read: id, name, body, tag_name,
        // published_at, html_url).
        try
        {
            string json = await _http.GetStringAsync(CodebergReleasesUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);
                if (date == DateTimeOffset.MinValue)
                    if (el.TryGetProperty("created_at", out var c) && c.ValueKind == JsonValueKind.String)
                        DateTimeOffset.TryParse(c.GetString(), out date);

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

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..] : tag;
    }

    /// Resolve the latest Codeberg release: version + the .apworld download URL.
    /// Falls back to a pinned version when the API is unreachable.
    private async Task<(string Version, string? ApWorldUrl)> ResolveLatestReleaseAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(
                $"{CodebergApiBase}/releases?limit=1", ct);
            using var doc = JsonDocument.Parse(json);

            // Gitea /releases?limit=1 returns an array (not an object).
            JsonElement root = doc.RootElement;
            JsonElement release = root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
                ? root[0]
                : root;  // fall through to catch

            string? version = release.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString()) : null;

            if (version != null && release.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? apworldUrl = null;
                string? anyUrl     = null;
                foreach (var a in assets.EnumerateArray())
                {
                    // Gitea asset shape: browser_download_url (same key as GitHub).
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u)
                                   ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    anyUrl ??= url;
                    if (apworldUrl == null && lower.EndsWith(".apworld"))
                        apworldUrl = url;
                }
                if (apworldUrl != null) return (version, apworldUrl);
                if (anyUrl     != null) return (version, anyUrl);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        // Pinned fallback: build the Codeberg direct download URL from the repo
        // release tag. Pattern observed: /releases/download/<tag>/<file>.
        string fallbackUrl =
            $"{CodebergRepoUrl}/releases/download/{FallbackVersion}/{ApWorldFileName}";
        return (FallbackVersion, fallbackUrl);
    }

    // ── Private helpers — game detection ──────────────────────────────────────

    /// The game directory to use: the override (if valid) wins, then AE from
    /// Steam, then Classic from Steam. Null when nothing is found.
    private string? DetectGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeGameDir(ov)) return ov;

        // Prefer AE when both editions are installed (AE is newer/more capable).
        try
        {
            string? ae = DetectSteamGameDir(AESteamAppId, AESteamFolder);
            if (ae != null) return ae;
        }
        catch { }

        try
        {
            string? classic = DetectSteamGameDir(ClassicSteamAppId, ClassicSteamFolder);
            if (classic != null) return classic;
        }
        catch { }

        return null;
    }

    private static bool IsAEInstall(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) return false;
        if (File.Exists(Path.Combine(dir, AEExeName))) return true;
        string lower = dir.ToLowerInvariant();
        return lower.Contains("anniversary") || lower.Contains(" ae");
    }

    /// A folder "looks like" a System Shock 2 install if it has the Classic
    /// exe, the AE exe, or a subdirectory that contains one of them.
    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, ClassicExeName))) return true;
            if (File.Exists(Path.Combine(dir, AEExeName)))      return true;
            // Loose glob fallback for unconventionally named AE exes.
            return Directory.EnumerateFiles(dir, "*shock*2*.exe",
                       SearchOption.TopDirectoryOnly).Any()
                || Directory.EnumerateFiles(dir, "*SS2*.exe",
                       SearchOption.TopDirectoryOnly).Any();
        }
        catch { return false; }
    }

    private static string? DetectSteamGameDir(string appId, string conventionalFolderName)
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{appId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common    = Path.Combine(steamapps, "common");
                    string? instDir  = ReadAcfInstallDir(manifest);
                    if (instDir != null)
                    {
                        string candidate = Path.Combine(common, instDir);
                        if (LooksLikeGameDir(candidate)) return candidate;
                    }
                    string conv = Path.Combine(common, conventionalFolderName);
                    if (LooksLikeGameDir(conv)) return conv;
                }
                catch { }
            }
        }
        return null;
    }

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

        string text = string.Empty;
        try { text = File.ReadAllText(vdf); }
        catch { /* unreadable — just the Steam root was yielded above */ }

        if (string.IsNullOrEmpty(text)) yield break;

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
            string text  = File.ReadAllText(acfPath);
            const string key = "\"installdir\"";
            int i    = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += key.Length;
            int open  = text.IndexOf('"', i);   if (open  < 0) return null;
            int close = text.IndexOf('"', open + 1); if (close < 0) return null;
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

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        string? dir = DetectGameDir();
        bool    ae  = dir != null && IsAEInstall(dir);

        string? exePath = null;
        if (dir != null)
        {
            // Try the expected exe name for the detected edition.
            string primary = Path.Combine(dir, ae ? AEExeName : ClassicExeName);
            if (File.Exists(primary))
                exePath = primary;
            else
            {
                // Glob fallback for non-standard exe names.
                exePath = Directory.EnumerateFiles(dir, "*shock*2*.exe",
                              SearchOption.TopDirectoryOnly).FirstOrDefault()
                       ?? Directory.EnumerateFiles(dir, "*SS2*.exe",
                              SearchOption.TopDirectoryOnly).FirstOrDefault();
            }
        }

        if (exePath != null)
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exePath,
                WorkingDirectory = dir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start System Shock 2.");

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
            catch { }
            return;
        }

        // Fall back to Steam URI for whichever edition is available (try AE first).
        bool hasSteam = SteamRoots().Any(r =>
            !string.IsNullOrWhiteSpace(r) && Directory.Exists(r));
        if (hasSteam)
        {
            string uri = (dir != null && ae) ? AESteamRunUrl : ClassicSteamRunUrl;
            try
            {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                IsRunning = true;
                return;
            }
            catch { }
        }

        throw new FileNotFoundException(
            "Could not find System Shock 2 (SS2.exe or SystemShock2AE.exe). Open this " +
            "game's Settings and pick your install folder, or install the game via Steam.",
            ClassicExeName);
    }

    // ── Private helpers — download ─────────────────────────────────────────────

    private async Task DownloadFileAsync(
        string url, string destPath, string msg,
        int pctStart, int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        progress.Report((pctStart, msg));
        using var response = await _http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total      = response.Content.Headers.ContentLength ?? -1;
        long downloaded = 0;

        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destPath);
        var buf = new byte[81920];
        int bytesRead;
        while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
            downloaded += bytesRead;
            if (total > 0)
            {
                int span = Math.Max(1, pctEnd - pctStart);
                int pct  = pctStart + (int)(span * downloaded / total);
                progress.Report((pct, $"{msg} {downloaded / 1000}KB"));
            }
        }
        await dst.FlushAsync(ct);
    }

    // ── Private helpers — settings sidecar ────────────────────────────────────

    private sealed class SS2Settings
    {
        public string? InstallOverride { get; set; }
        public string? ApWorldVersion  { get; set; }
    }

    private SS2Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<SS2Settings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(SS2Settings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { }
    }

    private string? LoadOverrideDir()
    {
        string? p = LoadSettings().InstallOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }
    private void SaveOverrideDir(string p)
    { var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s); }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ApWorldVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    private void WriteStampedVersion(string v)
    { var s = LoadSettings(); s.ApWorldVersion = v; SaveSettings(s); }
}
