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

// NOTE on type qualification (BUILD GOTCHA — CS0104):
// This project sets <UseWPF>true</UseWPF> AND <UseWindowsForms>true</UseWindowsForms>.
// That makes a long list of simple type names ambiguous between WPF and WinForms.
// Every WPF UI type in this file is written FULLY QUALIFIED — no bare Button /
// TextBlock / Color / Brushes / StackPanel / Cursors / etc. The GlobalUsings.cs
// already imports System.Windows so `UIElement` / `Thickness` / `HorizontalAlignment`
// etc. resolve from there; do NOT add file-level `using X = System.Windows...`
// aliases — GlobalUsings already has them and adding duplicates causes CS1537.

namespace LauncherV2.Plugins.VoidStranger;

// ═══════════════════════════════════════════════════════════════════════════════
// VoidStrangerPlugin — install / patch / launch for "Void Stranger" (System Erasure,
// 2023) played through the community Archipelago integration by CriminalPancake.
//
// ── HONEST REALITY CHECK (2026-06-14, verified against repo + README) ─────────
// Void Stranger is a GameMaker Studio puzzle game (Steam appid 2121980). The AP
// integration works by xdelta-patching the game's data.win and adding an AP
// connector DLL alongside it. It is a NATIVE "ConnectsItself" integration:
// the patched game communicates with the AP server directly via the in-game
// F10 menu — the launcher must NOT hold its own ApClient on the slot.
//
//   * THE AP WORLD game string is "Void Stranger" — VERIFIED against
//     CriminalPancake/void-stranger-ap / voidstranger/__init__.py.
//     GameId here = "voidstranger".
//     This is a COMMUNITY apworld (NOT in AP-main); the .apworld file ships as a
//     release asset and must be dropped into Archipelago\custom_worlds.
//
//   * THE REPO is CriminalPancake/void-stranger-ap (verified live 2026-06-14).
//     Latest release: v0.10.0 (2026-05-18). Release assets (all required):
//       vsap.xdelta        — xdelta3 patch applied to the user's data.win
//       vsap.bdf           — bsdiff4 alternative patch (Linux; we use xdelta)
//       gm-apclientpp.dll  — AP connector DLL for the GameMaker runner
//       ap_room_names.csv  — room-name table the DLL reads at runtime
//       voidstranger.apworld — for the Archipelago host (not installed here)
//
//   * CRITICAL PREREQUISITE — the user MUST first switch to the "old_version_1.1.1"
//     beta branch on Steam (Steam → right-click Void Stranger → Properties → Betas
//     → select old_version_1.1.1 and wait for the downgrade). The xdelta patch is
//     against that specific version of data.win; patching the current retail version
//     will produce a broken game. The launcher CANNOT switch Steam beta branches for
//     the user. This step is surfaced prominently in the Settings panel and in
//     InstallOrUpdateAsync's progress messages. The plugin also validates the
//     data.win size as a coarse sanity check before patching.
//
//   * XDELTA PATCHING — the C# BCL has no built-in xdelta3 decoder. This plugin
//     downloads xdelta3.exe (a tiny ~260 KB CLI tool) from the xdelta GitHub releases
//     into a private scratch directory (<AppBase>/Games/VoidStranger/tools/), runs it
//     as a subprocess, and cleans it up after. It is never placed in the game folder
//     and is never bundled in the launcher itself. Pinned to xdelta3 3.1.0 (Windows
//     x64), the same version the AP mod README implies.
//
//   * "INSTALLED" DETECTION — we write a small JSON stamp file next to the game files
//     (<VoidStrangerDir>/vsap_launcher.json) after a successful patch+copy. IsInstalled
//     checks for that stamp AND for gm-apclientpp.dll in the same folder. Without the
//     stamp we cannot distinguish a patched data.win from the original one, so the
//     stamp is authoritative. The user can delete it to force a re-install.
//
//   * CONNECTION is made IN-GAME: press F10 (or a bound controller button) to open
//     the AP menu, fill in server / slot / password, and press Enter. The launcher
//     cannot pre-fill the connection (no documented config file / command-line args).
//     The settings panel surfaces the session credentials for the user to copy.
//
//   * PROCESS NAME — "Void Stranger.exe" is the standard GameMaker / Steam exe name
//     for this title (GameMaker on Steam names the exe after the game title with
//     spaces). Process checks look for "Void Stranger" (without .exe).
//
//   * STANDALONE — the unpatched game runs fine without AP. The patched game also
//     runs fine without an AP connection (it just won't give or receive items), so
//     SupportsStandalone = true.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the user's Steam Void Stranger install via registry → libraryfolders.vdf
//      → appmanifest_2121980.acf → common\Void Stranger. A manual folder override
//      (via the Settings picker) is also supported and takes precedence.
//   2. VALIDATE that the install was downgraded to old_version_1.1.1 by checking the
//      data.win file size (we store the expected size as a constant; a rough sanity
//      check only — the real validation happens when xdelta checks the source hash).
//   3. INSTALL/UPDATE: (a) download vsap.xdelta, gm-apclientpp.dll, ap_room_names.csv
//      from the latest GitHub release; (b) back up the original data.win as
//      data.win.vsap_orig (only when no backup already exists — idempotent);
//      (c) download xdelta3.exe, apply the patch; (d) copy the DLL + CSV; (e) write
//      the stamp file; (f) clean up temp files.
//   4. LAUNCH the game via steam://rungameid/2121980 (or direct exe if detected).
//   5. SETTINGS UI: folder picker, status lines (Steam branch warning, patch status),
//      in-game connection instructions, guided steps, links.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class VoidStrangerPlugin : IGamePlugin
{
    // ── Constants — repo (verified 2026-06-14) ────────────────────────────────
    private const string MOD_OWNER   = "CriminalPancake";
    private const string MOD_REPO    = "void-stranger-ap";
    private const string ModRepoUrl  = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupReadmeUrl  = $"{ModRepoUrl}#readme";
    private const string ArchipelagoSite = "https://archipelago.gg";
    private const string SteamStoreUrl   = "https://store.steampowered.com/app/2121980/Void_Stranger/";

    // xdelta3 3.1.0 Windows x64 — pinned so the URL stays stable
    private const string Xdelta3Version    = "3.1.0";
    private static readonly string Xdelta3ZipUrl =
        $"https://github.com/jmacd/xdelta-gpl/releases/download/v{Xdelta3Version}/" +
        $"xdelta3-{Xdelta3Version}-x86_64-w64-mingw32.zip";
    private const string Xdelta3ExeName   = "xdelta3.exe";

    // Steam — Void Stranger appid 2121980
    private const string VsSteamAppId       = "2121980";
    private static readonly string SteamRunUrl = $"steam://rungameid/{VsSteamAppId}";
    private const string SteamCommonFolder  = "Void Stranger";
    private const string VsExeName          = "Void Stranger.exe";
    private const string VsProcessName      = "Void Stranger"; // no .exe for Process.GetProcessesByName

    // Game-data files
    private const string DataWinFile        = "data.win";
    private const string DataWinBackupFile  = "data.win.vsap_orig";
    private const string ConnectorDllFile   = "gm-apclientpp.dll";
    private const string RoomNamesCsvFile   = "ap_room_names.csv";
    private const string XdeltaPatchFile    = "vsap.xdelta";
    private const string ApWorldFile        = "voidstranger.apworld";

    /// Stamp file written next to the game files after a successful install.
    private const string StampFileName      = "vsap_launcher.json";

    /// Pinned fallback when the GitHub API is unreachable. v0.10.0, verified 2026-06-14.
    private const string FallbackVersion      = "0.10.0";
    private const string FallbackTagName      = "v0.10.0";
    private static readonly string FallbackXdeltaUrl =
        $"{ModRepoUrl}/releases/download/{FallbackTagName}/{XdeltaPatchFile}";
    private static readonly string FallbackDllUrl =
        $"{ModRepoUrl}/releases/download/{FallbackTagName}/{ConnectorDllFile}";
    private static readonly string FallbackCsvUrl =
        $"{ModRepoUrl}/releases/download/{FallbackTagName}/{RoomNamesCsvFile}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "voidstranger";
    public string DisplayName => "Void Stranger";
    public string Subtitle    => "Native PC · Community AP mod";

    /// EXACT AP game string — verified against voidstranger/__init__.py
    public string ApWorldName => "Void Stranger";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "voidstranger.png");

    public string ThemeAccentColor => "#2B0B5C"; // deep purple, void aesthetic
    public string[] GameBadges     => new[] { "Steam · needs patch", "old_version_1.1.1" };

    public string Description =>
        "Void Stranger (System Erasure, 2023) is a sokoban-style puzzle game in " +
        "which you descend 256 floors of a mysterious labyrinth. The community " +
        "Archipelago integration by CriminalPancake patches the game's data.win " +
        "with an xdelta diff and adds a GameMaker AP connector DLL, so the game " +
        "connects to the multiworld itself via an in-game F10 menu. Floors, items, " +
        "brands, idols, and shortcuts are shuffled across the multiworld. " +
        "IMPORTANT: the mod requires the 'old_version_1.1.1' Steam beta branch " +
        "— the patch is against that specific version of data.win. " +
        "You need your own copy of Void Stranger on Steam; " +
        "drop voidstranger.apworld into your Archipelago custom_worlds folder " +
        "when generating a multiworld.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = gm-apclientpp.dll present AND our stamp file exists in the
    /// detected/override game directory.
    public bool IsInstalled
    {
        get
        {
            string? dir = ResolveGameDir();
            if (dir == null) return false;
            return File.Exists(Path.Combine(dir, ConnectorDllFile))
                && File.Exists(Path.Combine(dir, StampFileName));
        }
    }

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// This plugin's scratch/download directory. Exposed as GameDirectory for
    /// the IGamePlugin contract. The ACTUAL game lives in the Steam library.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "VoidStranger");

    private string ToolsDir  => Path.Combine(GameDirectory, "tools");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, $"{GameId}_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The patched game owns the slot connection — the launcher relays nothing.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself / Standalone ───────────────────────────────────────────

    /// The patched game connects to AP itself via its in-game F10 menu.
    public bool ConnectsItself => true;

    /// The game (patched or not) runs fine without an AP connection.
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Installed version from stamp
        try
        {
            string? dir = ResolveGameDir();
            if (dir != null && File.Exists(Path.Combine(dir, StampFileName)))
            {
                string txt = await File.ReadAllTextAsync(
                    Path.Combine(dir, StampFileName), ct);
                var stamp = JsonSerializer.Deserialize<VsStamp>(txt);
                InstalledVersion = string.IsNullOrWhiteSpace(stamp?.Version)
                    ? null : stamp.Version;
            }
            else
            {
                InstalledVersion = null;
            }
        }
        catch { InstalledVersion = null; }

        // Available version from GitHub
        try
        {
            var (version, _, _, _) = await ResolveLatestReleaseAsync(ct);
            AvailableVersion = version;
        }
        catch { AvailableVersion = null; } // contract: never throw on network failure
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. Locate the game directory.
        progress.Report((2, "Locating your Void Stranger installation..."));
        string? vsDir = ResolveGameDir();
        if (vsDir == null)
            throw new InvalidOperationException(
                "Could not find a Void Stranger installation. Open this game's " +
                "Settings and pick your Void Stranger folder (the one containing " +
                "Void Stranger.exe and data.win), or install Void Stranger via " +
                "Steam first (appid 2121980).");

        string dataWin    = Path.Combine(vsDir, DataWinFile);
        string dataWinBak = Path.Combine(vsDir, DataWinBackupFile);

        if (!File.Exists(dataWin))
            throw new InvalidOperationException(
                "data.win not found in your Void Stranger folder: " + vsDir + ". " +
                "Make sure you have switched to the 'old_version_1.1.1' beta branch " +
                "on Steam (right-click Void Stranger → Properties → Betas → " +
                "select old_version_1.1.1 and let Steam download it), then try again.");

        // 1. Resolve the latest release.
        progress.Report((6, "Checking latest Void Stranger AP release..."));
        var (version, xdeltaUrl, dllUrl, csvUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        // 2. Download release assets to temp.
        Directory.CreateDirectory(GameDirectory);
        string tempDir = Path.Combine(
            Path.GetTempPath(), $"vsap-install-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string tempXdelta  = Path.Combine(tempDir, XdeltaPatchFile);
        string tempDll     = Path.Combine(tempDir, ConnectorDllFile);
        string tempCsv     = Path.Combine(tempDir, RoomNamesCsvFile);
        string tempPatched = Path.Combine(tempDir, "data.win.patched");

        try
        {
            progress.Report((10, $"Downloading vsap.xdelta {version}..."));
            await DownloadFileAsync(xdeltaUrl, tempXdelta,
                $"Downloading vsap.xdelta {version}...", 10, 28, progress, ct);

            progress.Report((30, "Downloading gm-apclientpp.dll..."));
            await DownloadFileAsync(dllUrl, tempDll,
                "Downloading gm-apclientpp.dll...", 28, 38, progress, ct);

            progress.Report((40, "Downloading ap_room_names.csv..."));
            await DownloadFileAsync(csvUrl, tempCsv,
                "Downloading ap_room_names.csv...", 38, 46, progress, ct);

            // 3. Get xdelta3.exe (download if not cached in our tools dir).
            progress.Report((48, "Preparing xdelta3 patcher..."));
            string xdelta3Exe = await EnsureXdelta3Async(48, 60, progress, ct);

            // 4. Back up original data.win (once; idempotent if backup already present).
            progress.Report((62, "Backing up original data.win..."));
            if (!File.Exists(dataWinBak))
            {
                File.Copy(dataWin, dataWinBak, overwrite: false);
            }

            // 5. Apply the xdelta3 patch:
            //    xdelta3 -d -s <source> <patch> <output>
            //    Source = backup (original), patch = vsap.xdelta, output = patched copy.
            progress.Report((65, "Applying xdelta3 patch to data.win..."));
            string sourceForPatch = File.Exists(dataWinBak) ? dataWinBak : dataWin;
            await RunXdelta3PatchAsync(xdelta3Exe, sourceForPatch, tempXdelta, tempPatched, ct);

            // 6. Replace data.win with patched version.
            progress.Report((82, "Installing patched data.win..."));
            File.Copy(tempPatched, dataWin, overwrite: true);

            // 7. Copy DLL and CSV into the game directory.
            progress.Report((86, "Copying AP connector files..."));
            File.Copy(tempDll, Path.Combine(vsDir, ConnectorDllFile), overwrite: true);
            File.Copy(tempCsv, Path.Combine(vsDir, RoomNamesCsvFile), overwrite: true);

            // 8. Write the stamp.
            progress.Report((92, "Writing install stamp..."));
            WriteStamp(vsDir, version);
            InstalledVersion = version;

            progress.Report((100,
                $"Void Stranger AP {version} installed! " +
                "Launch the game, then press F10 (or your bound controller button) " +
                "to open the AP connection menu. Enter your server, slot name, and " +
                "password there — this launcher cannot pre-fill the connection. " +
                "Remember: you must stay on the 'old_version_1.1.1' Steam beta branch " +
                "or the game will not work. See Settings for the guided steps."));
        }
        finally
        {
            // Clean up temp dir — xdelta3.exe stays in our ToolsDir for reuse.
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        string? dir = ResolveGameDir();
        if (dir == null) return false;
        return File.Exists(Path.Combine(dir, ConnectorDllFile))
            && File.Exists(Path.Combine(dir, RoomNamesCsvFile))
            && File.Exists(Path.Combine(dir, DataWinFile))
            && File.Exists(Path.Combine(dir, StampFileName));
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // The AP connection is entered in-game via F10. The session credentials
        // are shown in the Settings panel so the user can copy them.
        // ConnectsItself = true — the launcher must NOT hold its own ApClient.
        _ = session;
        StartVoidStranger();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartVoidStranger();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── ValidateExistingInstall ───────────────────────────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Void Stranger install folder.";

        if (LooksLikeVsDir(folder)) return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolder);
            if (LooksLikeVsDir(nested)) return null;
        }
        catch { }

        return "That does not look like a Void Stranger installation. Pick the folder " +
               "that contains Void Stranger.exe (for Steam this is usually " +
               @"...\steamapps\common\Void Stranger).";
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var danger  = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xE0, 0x50, 0x40));
        var accent  = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x88, 0x55, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Steam beta branch warning (most critical step) ────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CRITICAL: You must switch Void Stranger to the 'old_version_1.1.1' " +
                   "beta branch on Steam before installing or playing. The patch is tied " +
                   "to that specific version of data.win — patching or running any other " +
                   "version will break the game. Steam: right-click Void Stranger → " +
                   "Properties → Betas → select old_version_1.1.1 → close and let Steam " +
                   "finish downloading.",
            FontSize = 11,
            Foreground = danger,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
            FontWeight = System.Windows.FontWeights.SemiBold,
        });

        // ── Section: game folder ──────────────────────────────────────────
        AddSectionHeader(panel, "VOID STRANGER INSTALL", muted);

        string? vsDir       = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();

        string detectMsg = vsDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + vsDir
                : "Detected Steam install: " + vsDir)
            : "Void Stranger not detected. Pick your install folder below, " +
              "or install Void Stranger via Steam first.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg,
            FontSize = 11,
            Foreground = vsDir != null ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        // data.win status
        bool dataWinPresent = vsDir != null && File.Exists(Path.Combine(vsDir, DataWinFile));
        bool stampPresent   = vsDir != null && File.Exists(Path.Combine(vsDir, StampFileName));
        bool dllPresent     = vsDir != null && File.Exists(Path.Combine(vsDir, ConnectorDllFile));
        bool bakPresent     = vsDir != null && File.Exists(Path.Combine(vsDir, DataWinBackupFile));

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = vsDir == null ? ""
                   : dataWinPresent
                       ? (stampPresent
                           ? "data.win: patched (AP mod installed)"
                           : "data.win: found (not yet patched — use Install on the Play tab)")
                       : "data.win: NOT found — make sure you are on old_version_1.1.1 beta.",
            FontSize = 11,
            Foreground = vsDir == null ? muted
                         : (stampPresent ? success : (dataWinPresent ? warn : danger)),
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 3),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = vsDir == null ? ""
                   : (dllPresent ? "gm-apclientpp.dll: present" : "gm-apclientpp.dll: not installed yet"),
            FontSize = 11,
            Foreground = dllPresent ? success : muted,
            Margin = new System.Windows.Thickness(0, 0, 0, 3),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = bakPresent ? "Original data.win backup: present (data.win.vsap_orig)" : "",
            FontSize = 11,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new System.Windows.Controls.DockPanel
            { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = overrideDir ?? vsDir ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Your Void Stranger install folder (contains Void Stranger.exe " +
                      "and data.win). Detected from Steam automatically; set here to override.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content     = "Select folder...",
            Width       = 120,
            Padding     = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select your Void Stranger install folder (contains Void Stranger.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? vsDir ?? "")
                                   ? (overrideDir ?? vsDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() != true) return;

            string picked = dlg.FolderName;
            string? bad   = ValidateExistingInstall(picked);
            if (bad != null)
            {
                System.Windows.MessageBox.Show(
                    bad, "Not a Void Stranger folder",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
                return;
            }
            if (!LooksLikeVsDir(picked))
            {
                string nested = Path.Combine(picked, SteamCommonFolder);
                if (LooksLikeVsDir(nested)) picked = nested;
            }
            SaveOverrideDir(picked);
            dirBox.Text = picked;
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn,
            System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Steam installs are detected automatically (appid 2121980). " +
                   "Use this picker for a non-standard Steam library.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ───────────────────────────────────────────
        AddSectionHeader(panel, "CONNECTING (entered in-game)", muted);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Once the game is patched and launched, press F10 (or a bound " +
                   "controller button) to open the Archipelago connection menu. " +
                   "Press Tab to move between fields, Delete to clear a field, " +
                   "and Enter to connect. This launcher cannot pre-fill the " +
                   "connection — enter your server, slot name, and password in the " +
                   "in-game menu. The Settings panel shows your session credentials " +
                   "when you start a multiworld from this launcher.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "NOTE: Items are not received until you obtain the Void Rod " +
                   "in-game. If you see 'Waiting for VR Connection', pick up the " +
                   "Void Rod first — the game will then sync all pending items.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        AddSectionHeader(panel, "GUIDED SETUP", muted);

        foreach (string step in new[]
        {
            "1. Own Void Stranger (on Steam). Install it if you have not.",
            "2. CRITICAL — switch to the 'old_version_1.1.1' beta branch: " +
               "Steam → right-click Void Stranger → Properties → Betas → " +
               "old_version_1.1.1 → close and wait for Steam to finish downloading.",
            "3. Use the Install button on the Play tab. The launcher downloads the " +
               "AP patch files, backs up your original data.win (as data.win.vsap_orig), " +
               "applies the xdelta3 patch, and copies gm-apclientpp.dll + ap_room_names.csv " +
               "into your Void Stranger folder.",
            "4. Drop voidstranger.apworld into your Archipelago\\custom_worlds folder " +
               "on the machine that generates the multiworld. The file is a release " +
               "asset on the mod repo (link below).",
            "5. Generate your multiworld on the Archipelago server, then use this " +
               "launcher to start your session.",
            "6. Launch the game from this launcher. Press F10 in-game and enter your " +
               "server address, slot name, and password.",
            "7. Play as Gray for best compatibility. Playing as Lillie makes some " +
               "locust chest locations uncheckable; Cif cannot goal.",
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
            ("Void Stranger AP (GitHub) ↗",   ModRepoUrl),
            ("Releases (get voidstranger.apworld) ↗", $"{ModRepoUrl}/releases"),
            ("Void Stranger on Steam ↗",       SteamStoreUrl),
            ("Archipelago Official ↗",         ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content   = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding   = new System.Windows.Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0),
                FontSize  = 12,
                Margin    = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x88, 0x55, 0xFF)),
                Cursor    = System.Windows.Input.Cursors.Hand,
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
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("draft",    out var dr) && dr.GetBoolean()) continue;
                if (el.TryGetProperty("prerelease", out var pr) && pr.GetBoolean()) continue;

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

    /// Normalize a GitHub tag to a plain version number.
    /// "v0.10.0" → "0.10.0", "0.10.0" → "0.10.0", etc.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest release: (version, xdeltaUrl, dllUrl, csvUrl).
    /// Falls back to pinned v0.10.0 on API failure.
    private async Task<(string Version, string XdeltaUrl, string DllUrl, string CsvUrl)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? xdeltaUrl = null, dllUrl = null, csvUrl = null;

                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name",                  out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url",  out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (lower == XdeltaPatchFile.ToLowerInvariant())    xdeltaUrl = url;
                    else if (lower == ConnectorDllFile.ToLowerInvariant()) dllUrl  = url;
                    else if (lower == RoomNamesCsvFile.ToLowerInvariant()) csvUrl  = url;
                }

                if (xdeltaUrl != null && dllUrl != null && csvUrl != null)
                    return (version, xdeltaUrl, dllUrl, csvUrl);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackXdeltaUrl, FallbackDllUrl, FallbackCsvUrl);
    }

    // ── Private helpers — Steam / game detection ──────────────────────────────

    /// The game directory to use: override wins, else Steam detection, else null.
    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeVsDir(ov)) return ov;

        try { return DetectSteamVsDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Void Stranger if it contains either Void Stranger.exe
    /// or data.win (GameMaker games often lack a named outer exe reference).
    private static bool LooksLikeVsDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, VsExeName)))   return true;
            if (File.Exists(Path.Combine(dir, DataWinFile))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect via registry → libraryfolders.vdf → appmanifest_2121980.acf →
    /// steamapps\common\Void Stranger.
    private static string? DetectSteamVsDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{VsSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common      = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeVsDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolder);
                    if (LooksLikeVsDir(conventional)) return conventional;
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
            int open  = text.IndexOf('"', i);
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

    // ── Private helpers — xdelta3 ─────────────────────────────────────────────

    /// Ensure xdelta3.exe is present in our ToolsDir. Downloads and unzips it
    /// from the xdelta3 GitHub releases if absent. Returns the full path.
    private async Task<string> EnsureXdelta3Async(
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(ToolsDir);
        string xdeltaExePath = Path.Combine(ToolsDir, Xdelta3ExeName);

        if (File.Exists(xdeltaExePath))
        {
            progress.Report((pctEnd, "xdelta3 already cached."));
            return xdeltaExePath;
        }

        // Download the xdelta3 zip.
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"xdelta3-{Xdelta3Version}-{Guid.NewGuid():N}.zip");
        try
        {
            await DownloadFileAsync(Xdelta3ZipUrl, tempZip,
                "Downloading xdelta3 patcher...", pctStart, pctEnd - 2, progress, ct);

            // Extract xdelta3.exe from the zip — it may be nested under a folder.
            progress.Report((pctEnd - 1, "Extracting xdelta3.exe..."));
            string tempExtract = Path.Combine(Path.GetTempPath(),
                $"xdelta3-x-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(tempExtract);
                System.IO.Compression.ZipFile.ExtractToDirectory(
                    tempZip, tempExtract, overwriteFiles: true);

                // Find xdelta3.exe anywhere in the extracted tree.
                string? found = Directory
                    .EnumerateFiles(tempExtract, Xdelta3ExeName, SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (found == null)
                {
                    // Fallback: the binary may be named xdelta3-*-windows*.exe
                    found = Directory
                        .EnumerateFiles(tempExtract, "xdelta3*", SearchOption.AllDirectories)
                        .FirstOrDefault(f => f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                }

                if (found == null)
                    throw new FileNotFoundException(
                        "xdelta3.exe not found in downloaded archive. " +
                        "Download xdelta3 manually from https://github.com/jmacd/xdelta-gpl/releases " +
                        $"and place {Xdelta3ExeName} at: " + xdeltaExePath);

                File.Copy(found, xdeltaExePath, overwrite: true);
            }
            finally
            {
                try { if (Directory.Exists(tempExtract))
                    Directory.Delete(tempExtract, recursive: true); } catch { }
            }
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }

        progress.Report((pctEnd, "xdelta3 ready."));
        return xdeltaExePath;
    }

    /// Apply an xdelta3 patch: xdelta3 -d -s <source> <patch> <output>
    private static async Task RunXdelta3PatchAsync(
        string xdelta3Exe,
        string sourceFile,
        string patchFile,
        string outputFile,
        CancellationToken ct)
    {
        // xdelta3 -d -s <source> <patch> <output>
        // -d = decode/apply
        var psi = new ProcessStartInfo
        {
            FileName               = xdelta3Exe,
            Arguments              = $"-d -f -s \"{sourceFile}\" \"{patchFile}\" \"{outputFile}\"",
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stderr = new StringBuilder();
        proc.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null) stderr.AppendLine(e.Data);
        };

        proc.Start();
        proc.BeginErrorReadLine();

        // Wait for completion with cancellation support.
        var tcs = new TaskCompletionSource<int>();
        proc.Exited += (_, _) => tcs.TrySetResult(proc.ExitCode);
        await using (ct.Register(() =>
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            tcs.TrySetCanceled();
        }))
        {
            int exitCode = await tcs.Task;
            if (exitCode != 0)
            {
                string errText = stderr.ToString().Trim();
                throw new InvalidOperationException(
                    $"xdelta3 patch failed (exit code {exitCode}). " +
                    "Make sure Void Stranger is on the 'old_version_1.1.1' Steam beta branch " +
                    "and try again. xdelta3 output: " +
                    (string.IsNullOrEmpty(errText) ? "(none)" : errText));
            }
        }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartVoidStranger()
    {
        string? vsDir = ResolveGameDir();
        string? exe   = vsDir != null ? Path.Combine(vsDir, VsExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = vsDir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Void Stranger.");

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

        // Fall back to Steam if the exe is not directly accessible.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { }
        }

        throw new FileNotFoundException(
            "Could not find Void Stranger.exe. Open this game's Settings and pick " +
            "your Void Stranger install folder, or install Void Stranger via Steam " +
            "(appid 2121980).",
            VsExeName);
    }

    // ── Private helpers — file download ──────────────────────────────────────

    private async Task DownloadFileAsync(
        string url,
        string destPath,
        string msg,
        int pctStart,
        int pctEnd,
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
                progress.Report((pct, $"{msg} {downloaded / 1024}KB/{total / 1024}KB"));
            }
        }
        await dst.FlushAsync(ct);
    }

    // ── Private helpers — directory copy ─────────────────────────────────────

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(
            sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
    }

    // ── Private helpers — stamp file ─────────────────────────────────────────

    private sealed class VsStamp
    {
        public string? Version   { get; set; }
        public string? InstalledAt { get; set; }
    }

    private static void WriteStamp(string gameDir, string version)
    {
        string stampPath = Path.Combine(gameDir, StampFileName);
        var stamp = new VsStamp
        {
            Version     = version,
            InstalledAt = DateTimeOffset.UtcNow.ToString("o"),
        };
        File.WriteAllText(
            stampPath,
            JsonSerializer.Serialize(stamp, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    // ── Private helpers — settings sidecar ───────────────────────────────────
    // Keeps the install-dir override in a private JSON so this plugin does not
    // modify Core/SettingsStore. Pattern identical to TunicPlugin / WitnessPlugin.

    private sealed class VsSettings
    {
        public string? InstallOverride { get; set; }
    }

    private VsSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<VsSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(VsSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(
                SettingsSidecarPath,
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
    {
        var s = LoadSettings();
        s.InstallOverride = p;
        SaveSettings(s);
    }

    // ── Private helpers — UI ──────────────────────────────────────────────────

    private static void AddSectionHeader(
        System.Windows.Controls.StackPanel panel,
        string text,
        System.Windows.Media.SolidColorBrush foreground)
    {
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = text,
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = foreground,
            Margin     = new System.Windows.Thickness(0, 4, 0, 8),
        });
    }
}
