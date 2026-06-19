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

namespace LauncherV2.Plugins.GettingOverIt;

// ═══════════════════════════════════════════════════════════════════════════════
// GettingOverItPlugin — install / launch for "Getting Over It with Bennett
// Foddy" (Bennett Foddy, 2017) played through the CheckingOverIt mod by
// BlastSlimey (GitHub: BlastSlimey/CheckingOverIt). The mod is a BepInEx
// plugin that bundles the Archipelago.MultiClient.Net library and connects the
// game to the AP multiworld by itself. This is a NATIVE "ConnectsItself"
// integration — the game speaks to the AP server with no emulator and no Lua
// bridge.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online) ───────────────────────
// This is a STEAM-MOD native: the base game is the user's own Getting Over It
// with Bennett Foddy (Steam appid 240720), and Archipelago support is a BepInEx
// mod added on top. The honest ceiling is "automate what is possible, guide the
// irreducible parts."
//
//   * THE AP WORLD game string is "Getting Over It" (verified against
//     Connection.cs in the mod repo: both TryConnectAndLogin("Getting Over It",
//     ...) and GetLocationIdFromName("Getting Over It", ...) use this exact
//     string). GameId here = "getting_over_it". The .apworld (getting_over_it.
//     apworld) ships alongside the mod zip in every GitHub release.
//
//   * THE MOD is BlastSlimey/CheckingOverIt on GitHub. Latest release: 0.2.3
//     (AP 0.6.2 compatibility update, 2025-07-17). Each release ships TWO
//     assets:
//         BlastSlimey-CheckingOverIt-<ver>.zip  — the BepInEx mod zip
//         getting_over_it.apworld               — the AP world file
//     The mod zip extracts into <game root>/ with the BepInEx subtree inside
//     (i.e. it carries BepInEx/plugins/CheckingOverIt/CheckingOverIt.dll and
//     friends). The mod framework is BepInEx 5 (Unity Mono,
//     netstandard2.0; the .csproj references BepInEx.Core 5.*).
//
//   * CONNECTION CONFIG — KEY ADVANTAGE over most Unity/BepInEx mods: this mod
//     reads its connection settings from the standard BepInEx config file at
//     <game root>/BepInEx/config/CheckingOverIt.cfg. The config has three
//     relevant keys (from Config.cs in the repo):
//
//         [General]
//         ConnectionList = [{"slot":"SlotName","addressPort":"host:38281","password":"pw"}]
//         ActiveSlot = 0          ; index into the array above; -1 disables connecting
//         OfflineItems = ...      ; only used when not connecting
//
//     This means the launcher CAN pre-write the session credentials into the cfg
//     before launching, so the user does not have to type them in manually. The
//     settings panel also reveals the host/port/slot for reference, but the
//     prefill removes that hurdle entirely. This is verified against the actual
//     source — not invented.
//
//   * CRITICAL HONESTY — THE MOD HAS ONE SEPARATE PREREQUISITE THE ZIP DOES NOT
//     BUNDLE: BepInEx 5 (Unity Mono). Getting Over It is a Unity Mono game.
//     BepInEx 5.4.22 x64 portable zip must be installed into the game root
//     first. The mod zip itself carries only the CheckingOverIt plugin. So the
//     install flow is: (1) stage BepInEx (if absent), (2) extract the mod zip,
//     (3) optionally write the connection cfg. The launcher can do all three
//     with clear, honest progress messages.
//
//   * "INSTALLED" is judged by the presence of CheckingOverIt.dll under the
//     game's BepInEx/plugins tree (recursive, case-insensitive). We do NOT gate
//     on our own version stamp, because a manual extraction should also count.
//     If no game install is detected the tile reads "not installed".
//
//   * STEAM LIBRARY PARSING is a hand-written tolerant VDF scan that pulls
//     quoted "path" values; any failure degrades to "game not found" rather
//     than throwing.
//
//   * No raw password is written anywhere except the BepInEx config (which is a
//     local game folder file). On StopAsync the config is cleared so credentials
//     do not linger between sessions.
//
//   * BUILD NOTE: this project sets UseWindowsForms=true alongside UseWPF=true,
//     so WPF UI types that also exist in WinForms are spelled with their FULL
//     namespaces below to avoid CS0104 ambiguity, independent of GlobalUsings.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class GettingOverItPlugin : IGamePlugin
{
    // ── Constants — mod repo (GitHub, verified 2026-06-14) ────────────────────
    private const string MOD_OWNER = "BlastSlimey";
    private const string MOD_REPO  = "CheckingOverIt";
    private const string ModRepoUrl =
        $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_RELEASES_LATEST =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Getting%20Over%20It/setup_en";
    private const string ArchipelagoSite = "https://archipelago.gg";
    private const string BepInExSite =
        "https://github.com/BepInEx/BepInEx/releases";

    // BepInEx 5.4.22 x64 (Unity Mono) — the SEPARATE prerequisite the mod does
    // not bundle. Getting Over It is a 64-bit Unity Mono build.
    private const string BepInExVersion = "5.4.22";
    private const string BepInExZipUrl  =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.22/" +
        "BepInEx_x64_5.4.22.0.zip";

    // Steam — Getting Over It with Bennett Foddy appid 240720.
    private const string GoiSteamAppId = "240720";
    private static readonly string SteamRunUrl =
        $"steam://rungameid/{GoiSteamAppId}";

    // The Steam common sub-folder name for this game.
    private const string SteamCommonFolderName =
        "Getting Over It with Bennett Foddy";

    // The game executable name.
    private const string GoiExeName = "Getting Over It.exe";

    // The mod's primary plugin DLL filename (verified against repo structure).
    private const string ModDllName = "CheckingOverIt.dll";

    // The BepInEx config file that contains the connection settings.
    // Written at LaunchAsync time, cleared at StopAsync time.
    private const string BepInExConfigFileName = "CheckingOverIt.cfg";

    // Pinned fallback for the mod when the GitHub API is unreachable.
    // 0.2.3 verified live 2026-06-14; asset = BlastSlimey-CheckingOverIt-0.2.3.zip.
    private const string FallbackVersion = "0.2.3";
    private const string FallbackZipName = "BlastSlimey-CheckingOverIt-0.2.3.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "getting_over_it";
    public string DisplayName => "Getting Over It";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against Connection.cs:
    /// TryConnectAndLogin("Getting Over It", ...) and
    /// GetLocationIdFromName("Getting Over It", ...).
    public string ApWorldName => "Getting Over It";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "getting_over_it.png");

    public string ThemeAccentColor => "#8B6914";   // cauldron copper

    public string[] GameBadges => new[] { "Steam · needs mod" };

    public string Description =>
        "Getting Over It with Bennett Foddy, Bennett Foddy's 2017 pot-climbing " +
        "meditation, played through the CheckingOverIt Archipelago mod by " +
        "BlastSlimey. The mod is a BepInEx plugin that bundles the Archipelago " +
        "network client, so the game connects to the multiworld itself. Heights " +
        "become checks: progress through the climb sends items to other worlds, " +
        "while Gravity Reduction, Goal Height Reduction and Wind Trap items from " +
        "the pool shape how you ascend. You bring your own copy of Getting Over It " +
        "(owned on Steam); the launcher detects your install, stages BepInEx and " +
        "the Archipelago mod, and can pre-write your session connection so the game " +
        "auto-connects on startup. Climbing was never meant to be easy — now it is " +
        "a multiworld puzzle.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = CheckingOverIt.dll is present in the game's BepInEx/plugins
    /// tree (recursive, case-insensitive).
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ConnectsItself = true: the mod owns the AP slot connection. The launcher
    // must NOT hold its own ApClient on this slot while the game is running.
    public bool ConnectsItself    => true;
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Scratch / download directory for this plugin (not the game install dir).
    /// Exposed as GameDirectory to satisfy the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "GettingOverIt");

    /// Per the brief, sidecar lives under Games/ROMs/getting_over_it/.
    private string SidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SidecarPath
        => Path.Combine(SidecarDir, "getting_over_it_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The CheckingOverIt mod reports checks / items / goal to the AP server
    // directly; the launcher relays nothing. These exist for interface
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
            string? dll = FindInstalledModDll();
            InstalledVersion = dll != null
                ? (ReadStampedVersion() ?? "installed")
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
        progress.Report((2, "Locating your Getting Over It installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Getting Over It installation. Open this game's " +
                "Settings and pick your Getting Over It folder (the one containing " +
                "\"Getting Over It.exe\"), or install it from Steam first. The " +
                "Archipelago mod is added on top of your own copy of the game.");

        // Stage BepInEx first if it is not already present.
        string bepInExCore = Path.Combine(gameDir, "BepInEx", "core");
        if (!Directory.Exists(bepInExCore))
        {
            progress.Report((5, "BepInEx not found — downloading BepInEx 5.4.22 x64..."));
            await DownloadAndExtractZipAsync(
                BepInExZipUrl, gameDir, progress,
                startPct: 5, endPct: 35,
                descriptionPrefix: "BepInEx", ct: ct);
            progress.Report((35, "BepInEx staged."));
        }
        else
        {
            progress.Report((35, "BepInEx already present — skipping."));
        }

        // Resolve the latest mod release.
        progress.Report((38, "Checking the latest CheckingOverIt release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a CheckingOverIt mod download on GitHub. " +
                "Check your internet connection or visit: " + ModRepoUrl);

        // Download + extract the mod zip into the game root. The zip carries the
        // BepInEx/ subtree, so extracting to the game root drops everything
        // (BepInEx/plugins/CheckingOverIt/…) in the right places.
        await DownloadAndExtractZipAsync(
            zipUrl, gameDir, progress,
            startPct: 40, endPct: 95,
            descriptionPrefix: $"CheckingOverIt {version}", ct: ct);

        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"CheckingOverIt {version} installed. BepInEx is staged in your " +
            "Getting Over It folder. To play: press Play (the launcher pre-writes " +
            "your session connection into BepInEx/config/CheckingOverIt.cfg), then " +
            "launch the game — it will auto-connect to the AP server on startup. " +
            "The first launch after a fresh BepInEx install may be slow while " +
            "BepInEx generates its config files; that is normal."));
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
        // KEY ADVANTAGE: the mod reads its connection from the BepInEx config
        // file, so we CAN pre-write the session credentials. The config key
        // "ConnectionList" is a JSON array; "ActiveSlot" (0-based) selects which
        // entry to use. We write entry 0 and set ActiveSlot=0. Verified against
        // Config.cs and Connection.cs in the BlastSlimey/CheckingOverIt repo.
        PrewriteConnectionConfig(session);
        StartGettingOverIt();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // Run the game without AP — set ActiveSlot=-1 in the config to disable
        // the mod's auto-connect, then launch.
        string? gameDir = ResolveGameDir();
        if (gameDir != null)
            WriteDisabledConnectionConfig(gameDir);
        StartGettingOverIt();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;

        // Clear the connection config so credentials do not linger on disk
        // between sessions.
        try
        {
            string? gameDir = ResolveGameDir();
            if (gameDir != null)
                WriteDisabledConnectionConfig(gameDir);
        }
        catch { /* non-fatal */ }

        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(
        ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The CheckingOverIt mod receives items from the AP server directly; the
        // launcher forwards nothing.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status; the launcher does not intervene.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadSidecar().InstallOverride;
        string? modDll      = FindInstalledModDll();
        bool    bepInExOk   = gameDir != null &&
                               Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));

        // ── Overview ──────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Getting Over It with Bennett Foddy is your own game (Steam). The " +
                "CheckingOverIt mod adds Archipelago support via BepInEx (the Unity " +
                "mod loader). The launcher detects your Steam install, stages BepInEx " +
                "and the mod, and pre-writes your session connection into the mod's " +
                "config file so the game auto-connects when you press Play. The first " +
                "launch after a fresh BepInEx install may generate config files and be " +
                "slow — that is normal.",
            FontSize     = 11,
            Foreground   = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Install status ────────────────────────────────────────────────
        AddSectionHeader(panel, "GETTING OVER IT INSTALL", muted);

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Getting Over It not detected. Pick your install folder below, or " +
              "install the game via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text         = detectMsg,
            FontSize     = 11,
            Foreground   = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = bepInExOk
                ? "BepInEx found (BepInEx\\core present)."
                : "BepInEx not found yet — use Install to stage it, or install " +
                  "BepInEx 5.4.22 x64 (Unity Mono) manually into the game folder.",
            FontSize     = 11,
            Foreground   = bepInExOk ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                ? "CheckingOverIt mod found: " + modDll
                : "CheckingOverIt mod not found yet — use Install to stage it.",
            FontSize     = 11,
            Foreground   = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = overrideDir ?? gameDir ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Your Getting Over It install folder (the one containing " +
                      "\"Getting Over It.exe\"). Detected from Steam automatically; " +
                      "set it here to override.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content  = "Select folder...",
            Width    = 120,
            Padding  = new System.Windows.Thickness(0, 6, 0, 6),
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
                Title = "Select your Getting Over It install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                    ? (overrideDir ?? gameDir!)
                    : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                if (!LooksLikeGoiDir(picked))
                {
                    // Accept parent "common" folder: descend if it contains the game.
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeGoiDir(nested))
                        picked = nested;
                    else
                    {
                        System.Windows.MessageBox.Show(
                            "That does not look like a Getting Over It installation. " +
                            "Pick the folder that contains \"Getting Over It.exe\".",
                            "Not a Getting Over It folder",
                            System.Windows.MessageBoxButton.OK,
                            System.Windows.MessageBoxImage.Warning);
                        return;
                    }
                }
                var s = LoadSidecar();
                s.InstallOverride = picked;
                SaveSidecar(s);
                dirBox.Text = picked;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(
            dirBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Steam installs are detected automatically (appid 240720). " +
                   "Use this picker for a non-standard library or GOG install.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Connection note ───────────────────────────────────────────────
        AddSectionHeader(panel, "CONNECTION (PRE-WRITTEN BY THE LAUNCHER)", muted);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "When you press Play, the launcher writes your server address, slot " +
                "name and password into BepInEx/config/CheckingOverIt.cfg. The mod " +
                "reads this file at startup and auto-connects — you do not have to " +
                "type anything in-game. When you stop the session the config is " +
                "cleared. In standalone mode ActiveSlot=-1 disables auto-connect.",
            FontSize     = 11,
            Foreground   = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Setup steps ───────────────────────────────────────────────────
        AddSectionHeader(panel, "SETUP STEPS", muted);
        foreach (string step in new[]
        {
            "1. Own Getting Over It with Bennett Foddy (Steam, appid 240720). " +
                "Use \"Select folder...\" above if it was not auto-detected.",
            "2. Press Install on the Play tab. The launcher stages BepInEx 5.4.22 " +
                "x64 (Unity Mono) and the CheckingOverIt mod into your game folder.",
            "3. Press Play. The launcher pre-writes your AP session into the mod's " +
                "config file and launches the game. The mod auto-connects on startup.",
            "4. The first launch after a fresh BepInEx install generates config files " +
                "— it may be briefly slower than usual. If the game doesn't connect, " +
                "check the BepInEx console (enabled in BepInEx/config/BepInEx.cfg " +
                "under [Logging.Console] Enabled=true).",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text         = step,
                FontSize     = 11,
                Foreground   = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin       = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        AddSectionHeader(panel, "LINKS", muted);
        foreach (var (label, url) in new[]
        {
            ("CheckingOverIt mod (GitHub) ↗",  ModRepoUrl),
            ("AP Setup Guide ↗",               SetupGuideUrl),
            ("BepInEx releases ↗",             BepInExSite),
            ("Archipelago Official ↗",         ArchipelagoSite),
        })
        {
            string u = url;
            var btn = new System.Windows.Controls.Button
            {
                Content  = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding         = new System.Windows.Thickness(0, 2, 0, 2),
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
                try
                {
                    Process.Start(new ProcessStartInfo(u) { UseShellExecute = true });
                }
                catch { }
            };
            panel.Children.Add(btn);
        }

        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Pull the GitHub releases list; each release is a news item.
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
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

                string tag  = el.TryGetProperty("tag_name", out var t)  ? t.GetString() ?? "" : "";
                string name = el.TryGetProperty("name",     out var n)  ? n.GetString() ?? "" : tag;
                string body = el.TryGetProperty("body",     out var b)  ? b.GetString() ?? "" : "";
                string url  = el.TryGetProperty("html_url", out var u)  ? u.GetString() ?? ModRepoUrl : ModRepoUrl;

                items.Add(new NewsItem(
                    Title:   "CheckingOverIt " + name,
                    Body:    body,
                    Version: tag,
                    Date:    date,
                    Url:     url
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? tag = root.TryGetProperty("tag_name", out var t)
                ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) goto fallback;

            // Find the asset whose name matches BlastSlimey-CheckingOverIt-*.zip
            if (root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string? aname = asset.TryGetProperty("name", out var an)
                        ? an.GetString() : null;
                    string? aurl  = asset.TryGetProperty("browser_download_url", out var au)
                        ? au.GetString() : null;
                    if (aname != null && aurl != null &&
                        aname.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                        aname.IndexOf("CheckingOverIt", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return (tag!, aurl);
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* network failure → pinned fallback */ }

        fallback:
        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — Steam / game detection ──────────────────────────────

    private string? ResolveGameDir()
    {
        string? ov = LoadSidecar().InstallOverride;
        if (!string.IsNullOrWhiteSpace(ov) && LooksLikeGoiDir(ov))
            return ov;
        try { return DetectSteamGoiDir(); }
        catch { return null; }
    }

    private static bool LooksLikeGoiDir(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
        try { return File.Exists(Path.Combine(dir, GoiExeName)); }
        catch { return false; }
    }

    private static string? DetectSteamGoiDir()
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
                        $"appmanifest_{GoiSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? idir  = ReadAcfInstallDir(manifest);
                    if (idir != null)
                    {
                        string c = Path.Combine(common, idir);
                        if (LooksLikeGoiDir(c)) return c;
                    }
                    string conv = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeGoiDir(conv)) return conv;
                }
                catch { /* try next library */ }
            }
        }
        return null;
    }

    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadReg(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormSlash(hkcu);

        string? hklm = ReadReg(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormSlash(hklm);

        string? hklm2 = ReadReg(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm2)) yield return NormSlash(hklm2);

        string? pf86 = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(pf86))
            yield return Path.Combine(pf86, "Steam");
    }

    private static string NormSlash(string p)
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

        foreach (string p in ExtractVdfPaths(text))
        {
            string n = p.Replace('/', '\\').TrimEnd('\\');
            if (n.Length > 0 && seen.Add(n)) yield return n;
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

    private static string? ReadAcfInstallDir(string acf)
    {
        try
        {
            string text = File.ReadAllText(acf);
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

    private static string? ReadReg(RegistryKey hive, string sub, string val)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(sub);
            return k?.GetValue(val) as string;
        }
        catch { return null; }
    }

    // ── Private helpers — mod detection ───────────────────────────────────────

    /// Find CheckingOverIt.dll under the game's BepInEx/plugins tree.
    /// Returns the first match or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? game = ResolveGameDir();
            if (game == null) return null;
            string pluginsDir = Path.Combine(game, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            // Direct name match first.
            foreach (string dll in Directory.EnumerateFiles(
                pluginsDir, ModDllName, SearchOption.AllDirectories))
                return dll;

            // Fallback: any DLL whose name mentions "checkingovit".
            foreach (string dll in Directory.EnumerateFiles(
                pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                if (Path.GetFileNameWithoutExtension(dll).IndexOf(
                        "checkingoveri", StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGettingOverIt()
    {
        string? gameDir = ResolveGameDir();
        string? exe     = gameDir != null
            ? Path.Combine(gameDir, GoiExeName)
            : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = gameDir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException(
                "Failed to start Getting Over It.");

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
            catch { /* some Unity processes don't expose Exited — non-fatal */ }
            return;
        }

        // Steam fallback.
        if (SteamRoots().Any(r =>
            !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl)
                {
                    UseShellExecute = true
                });
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { /* fall through */ }
        }

        throw new FileNotFoundException(
            "Could not find \"Getting Over It.exe\". Open this game's Settings " +
            "and pick your Getting Over It install folder, or install the game " +
            "via Steam (appid 240720).",
            GoiExeName);
    }

    // ── Private helpers — connection config ───────────────────────────────────

    /// Write the BepInEx config file with the session credentials so the mod
    /// auto-connects on startup. The file is a standard BepInEx .cfg (INI-like).
    /// Verified against Config.cs / Connection.cs in the mod repo.
    private void PrewriteConnectionConfig(ApSession session)
    {
        string? gameDir = ResolveGameDir();
        if (gameDir == null) return; // can't pre-write without a known install

        string cfgDir  = Path.Combine(gameDir, "BepInEx", "config");
        string cfgPath = Path.Combine(cfgDir, BepInExConfigFileName);

        // Escape double quotes in the JSON values (basic safety; AP strings
        // normally don't contain backslashes or double quotes but be defensive).
        static string EscJson(string s) =>
            s.Replace("\\", "\\\\").Replace("\"", "\\\"");

        string safeAddr = EscJson(session.ServerUri ?? "archipelago.gg:38281");
        string safeSlot = EscJson(session.SlotName ?? "Player1");
        string safePw   = EscJson(session.Password ?? "");

        // ConnectionList is a JSON array of objects; ActiveSlot=0 selects the
        // first entry. OfflineItems is the fallback when not connecting —
        // leave it at the default vanilla values.
        string connectionJson =
            $"[{{\\\"slot\\\":\\\"{safeSlot}\\\"," +
            $"\\\"addressPort\\\":\\\"{safeAddr}\\\"," +
            $"\\\"password\\\":\\\"{safePw}\\\"}}]";

        // BepInEx .cfg format: [Section]\nKey = Value
        // The binding defaults (from Config.cs) list "General" as the section.
        string cfg =
            "[General]\n" +
            $"ConnectionList = {connectionJson}\n" +
            "ActiveSlot = 0\n" +
            "OfflineItems = {\"Gravity Reduction\":2,\"Goal Height Reduction\":0,\"Wind Trap\":0}\n" +
            "PrintHammerCollision = false\n" +
            "PrintGravity = false\n";

        try
        {
            Directory.CreateDirectory(cfgDir);
            File.WriteAllText(cfgPath, cfg, new UTF8Encoding(false));
        }
        catch { /* non-fatal — the user can still type credentials in-game */ }
    }

    /// Write the config with ActiveSlot=-1 to disable the mod's auto-connect.
    private static void WriteDisabledConnectionConfig(string gameDir)
    {
        string cfgDir  = Path.Combine(gameDir, "BepInEx", "config");
        string cfgPath = Path.Combine(cfgDir, BepInExConfigFileName);
        string cfg =
            "[General]\n" +
            "ConnectionList = []\n" +
            "ActiveSlot = -1\n" +
            "OfflineItems = {\"Gravity Reduction\":2,\"Goal Height Reduction\":0,\"Wind Trap\":0}\n" +
            "PrintHammerCollision = false\n" +
            "PrintGravity = false\n";
        try
        {
            Directory.CreateDirectory(cfgDir);
            File.WriteAllText(cfgPath, cfg, new UTF8Encoding(false));
        }
        catch { }
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    /// Download a zip from url and extract it into destDir (overwriting).
    /// Reports progress between startPct and endPct.
    private async Task DownloadAndExtractZipAsync(
        string url,
        string destDir,
        IProgress<(int Pct, string Msg)> progress,
        int    startPct,
        int    endPct,
        string descriptionPrefix,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"goi-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((startPct, $"Downloading {descriptionPrefix}..."));
            using (var resp = await _http.GetAsync(
                url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total      = resp.Content.Headers.ContentLength ?? -1;
                long downloaded = 0;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tempZip);
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    downloaded += n;
                    if (total > 0)
                    {
                        int span = endPct - startPct;
                        int dlPct = startPct + (int)(span * 0.7 * downloaded / total);
                        progress.Report((dlPct,
                            $"Downloading {descriptionPrefix}... " +
                            $"{downloaded / 1024}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            int exPct = startPct + (int)((endPct - startPct) * 0.75);
            progress.Report((exPct, $"Extracting {descriptionPrefix}..."));
            Directory.CreateDirectory(destDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, destDir, overwriteFiles: true);
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — sidecar ─────────────────────────────────────────────

    private sealed class GoiSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private GoiSettings LoadSidecar()
    {
        try
        {
            if (File.Exists(SidecarPath))
            {
                string txt = File.ReadAllText(SidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<GoiSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSidecar(GoiSettings s)
    {
        try
        {
            Directory.CreateDirectory(SidecarDir);
            File.WriteAllText(
                SidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { }
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSidecar().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSidecar();
        s.ModVersion = v;
        SaveSidecar(s);
    }

    // ── Private helpers — UI ──────────────────────────────────────────────────

    private static void AddSectionHeader(
        System.Windows.Controls.StackPanel panel,
        string text,
        System.Windows.Media.SolidColorBrush color)
    {
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = text,
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = color,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });
    }
}
