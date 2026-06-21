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

// NOTE on type qualification (BUILD GOTCHA — CS0104 / CS1537):
// The project sets both <UseWPF>true</UseWPF> and <UseWindowsForms>true</UseWindowsForms>,
// making many short WPF/WinForms names ambiguous (CS0104). GlobalUsings.cs already aliases
// the colliding names — we must NOT add file-level `using X = System.Windows...;` aliases
// (CS1537). Every WPF UI type is written fully qualified below.

namespace LauncherV2.Plugins.FinalFantasyXIITrialMode;

// ═══════════════════════════════════════════════════════════════════════════════
// FinalFantasyXIITrialModePlugin — Archipelago integration for
// "Final Fantasy XII Trial Mode" (gaithernOrg/FFXIITMAPArchipelago).
//
// ── WHAT KIND OF INTEGRATION IS THIS? (verified 2026-06-16) ─────────────────
//
//   * THE AP WORLD: game = "Final Fantasy XII Trial Mode" — VERIFIED against
//     worlds/ffxiitm/__init__.py (FFXIITMWorld.game = "Final Fantasy XII Trial Mode",
//     required_client_version = (0, 3, 5)). Community apworld hosted in the
//     gaithernOrg/FFXIITMAPArchipelago fork. Latest release: 0.0.6 (2024-01-13),
//     single asset "ffxiitm.apworld".
//
//   * THE CLIENT: a bundled Python client (FFXIITMContext in worlds/ffxiitm/Client.py)
//     that communicates via FILES in %LocalAppData%\FFXIITM. The client connects to the
//     AP server and writes item files; a BizHawk Lua script (ffxii_tm_ap.lua, shipped
//     as a release asset) reads those files while the game runs in BizHawk's PS2 core.
//     The Python client holds the AP slot. Hence ConnectsItself = true.
//
//   * BASE GAME: the player's own legally-obtained copy of Final Fantasy XII:
//     The Zodiac Age on PC (Steam appid 595520) OR a PS2 ISO of the original
//     Final Fantasy XII. The Trial Mode is the in-game 100-battle gauntlet that
//     unlocks in the main menu. The launcher never ships or downloads the base game.
//
//   * CONNECTION: the Python client (launched separately from the Archipelago
//     installation) connects to the AP server using credentials entered in the
//     Archipelago client window or via command-line arguments. The launcher
//     cannot pre-fill the connection into the Python client.
//
// ── WHAT THIS PLUGIN HONESTLY DOES ──────────────────────────────────────────
//   1. DOWNLOAD the ffxiitm.apworld (latest GitHub release) for the user to
//      place in their Archipelago custom_worlds folder.
//   2. DOWNLOAD the ffxii_tm_ap.lua BizHawk Lua script (latest GitHub release).
//   3. DETECT the base game install via Steam registry (appid 595520).
//   4. LAUNCH the base game via Steam or detected exe (convenience).
//   5. GUIDE the irreducible steps in Settings.
//      ConnectsItself = true — the launcher does NOT hold its own ApClient on
//      the same slot while the Python client runs.
//
// ── VERIFIED DETAILS ─────────────────────────────────────────────────────────
//   * Repo: github.com/gaithernOrg/FFXIITMAPArchipelago
//   * Latest release tag: "0.0.6" (2024-01-13), asset "ffxiitm.apworld"
//   * Earlier releases also shipped "ffxii_tm_ap.lua" + "FFXII_001" (BizHawk save).
//     The Lua script is the BizHawk bridge; it is the last known release asset
//     for that file (present in 0.0.5 and 0.0.4; not re-published in 0.0.6 —
//     fetch from 0.0.5 as fallback).
//   * The game bridge uses %LocalAppData%\FFXIITM as the shared directory:
//     "send<id>" files = checked locations; "victory" file = goal;
//     "AP_N.item" files = received items (created by the Python client).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class FinalFantasyXIITrialModePlugin : IGamePlugin
{
    // ── GitHub repo (verified 2026-06-16) ─────────────────────────────────────
    private const string GH_OWNER = "gaithernOrg";
    private const string GH_REPO  = "FFXIITMAPArchipelago";
    private static readonly string RepoUrl =
        "https://github.com/" + GH_OWNER + "/" + GH_REPO;
    private static readonly string ReleasesApiUrl =
        "https://api.github.com/repos/" + GH_OWNER + "/" + GH_REPO + "/releases";
    // ── Pinned fallbacks (verified live 2026-06-16) ───────────────────────────

    /// Latest apworld release: tag "0.0.6", single asset "ffxiitm.apworld".
    private const string FallbackVersion      = "0.0.6";
    private const string FallbackApWorldName  = "ffxiitm.apworld";
    private static readonly string FallbackApWorldUrl =
        RepoUrl + "/releases/download/" + FallbackVersion + "/" + FallbackApWorldName;

    /// Lua script last published in tag "0.0.5" (not re-published in 0.0.6).
    private const string FallbackLuaVersion = "0.0.5";
    private const string FallbackLuaName    = "ffxii_tm_ap.lua";
    private static readonly string FallbackLuaUrl =
        RepoUrl + "/releases/download/" + FallbackLuaVersion + "/" + FallbackLuaName;

    // ── Base game (Steam) ─────────────────────────────────────────────────────

    /// Steam appid for Final Fantasy XII: The Zodiac Age (PC).
    private const string SteamAppId = "595520";
    private static readonly string SteamRunUrl   = "steam://rungameid/" + SteamAppId;
    private const string SteamStoreUrl =
        "https://store.steampowered.com/app/595520";
    private const string SteamCommonFolderName =
        "FINAL FANTASY XII THE ZODIAC AGE";
    private const string Ff12ExeName = "x64\\FF12.exe";   // TZA exe inside the install

    // ── Local file layout ─────────────────────────────────────────────────────

    private const string ApWorldFileName  = "ffxiitm.apworld";
    private const string LuaFileName      = "ffxii_tm_ap.lua";
    private const string VersionFileName  = "ffxiitm_version.dat";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } },
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "ffxiitm";
    public string DisplayName => "Final Fantasy XII: Trial Mode";
    public string Subtitle    => "AP Integration · BizHawk Lua";

    /// EXACT AP game string — VERIFIED against gaithernOrg/FFXIITMAPArchipelago
    /// worlds/ffxiitm/__init__.py (FFXIITMWorld.game = "Final Fantasy XII Trial Mode").
    public string ApWorldName => "Final Fantasy XII Trial Mode";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "ffxiitm.png");

    public string ThemeAccentColor => "#4A1A6B";   // FFXII violet/purple

    public string[] GameBadges => new[] { "Requires FFXII TZA or PS2 ISO", "BizHawk + Python Client" };

    public string Description =>
        "Final Fantasy XII: Trial Mode puts you through 100 escalating battles in " +
        "the Trial Mode arena. With the community Archipelago integration " +
        "(gaithernOrg/FFXIITMAPArchipelago), loot and key rewards from each stage " +
        "flow into the multiworld. The integration uses a BizHawk Lua script " +
        "plus a bundled Python client from the AP fork — the launcher downloads both " +
        "for you and guides the setup. You bring your own copy of Final Fantasy XII: " +
        "The Zodiac Age (Steam appid " + SteamAppId + ") or a PS2 ISO.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version / install state ───────────────────────────────────────────────

    public string? InstalledVersion  { get; private set; }
    public string? AvailableVersion  { get; private set; }

    /// "Installed" = the apworld file (and ideally the Lua script) are present
    /// in our client folder, as stamped by VersionFile.
    public bool IsInstalled => File.Exists(ApWorldPath) || File.Exists(VersionFilePath);

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "FFXIITrialMode");

    private string ApWorldPath    => Path.Combine(GameDirectory, ApWorldFileName);
    private string LuaPath        => Path.Combine(GameDirectory, LuaFileName);
    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    private string SettingsSidecarDir  =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath =>
        Path.Combine(SettingsSidecarDir, "ffxiitm_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Python client holds the AP slot and writes checks/items to disk;
    // the BizHawk Lua script reads those files. The launcher relays nothing.
    // Events exist only for interface compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself ────────────────────────────────────────────────────────

    /// The Python client (FFXIITMContext) holds the AP slot connection.
    /// The launcher must NOT also hold an ApClient on the same slot — they would
    /// kick each other off. ConnectsItself = true suppresses the launcher's
    /// own reconnect + "connection lost" toast while the game runs.
    public bool ConnectsItself => true;

    /// FFXII TZA plays fine without AP.
    public bool SupportsStandalone => true;

    // ── CheckForUpdateAsync ───────────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = IsInstalled
                ? (File.Exists(VersionFilePath)
                    ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim()
                    : "installed")
                : null;
        }
        catch { InstalledVersion = null; }

        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(GH_OWNER, GH_REPO, ct));
        }
        catch { AvailableVersion = null; }
    }

    // ── InstallOrUpdateAsync ──────────────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((3, "Checking the latest FFXIITM release..."));
        var (version, apworldUrl, luaUrl) = await ResolveLatestAssetsAsync(ct);
        AvailableVersion = version;

        // Fast path: already current.
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100,
                "FFXIITM assets " + version + " are up to date. " +
                "Place " + ApWorldFileName + " in your Archipelago custom_worlds folder " +
                "and load " + LuaFileName + " in BizHawk. See Settings for the full guide."));
            return;
        }

        Directory.CreateDirectory(GameDirectory);

        // Download the apworld.
        if (apworldUrl != null)
        {
            progress.Report((10, "Downloading " + ApWorldFileName + "..."));
            byte[] apworld = await DownloadWithProgressAsync(
                apworldUrl, progress, 10, 55, ct);
            await File.WriteAllBytesAsync(ApWorldPath, apworld, ct);
            progress.Report((58, ApWorldFileName + " saved."));
        }
        else
        {
            progress.Report((58, "apworld URL not found — skipping (check manually at " + RepoUrl + "/releases)."));
        }

        // Download the Lua script (best effort — may not be in the latest tag).
        if (luaUrl != null)
        {
            progress.Report((60, "Downloading " + LuaFileName + "..."));
            try
            {
                byte[] lua = await DownloadWithProgressAsync(
                    luaUrl, progress, 60, 90, ct);
                await File.WriteAllBytesAsync(LuaPath, lua, ct);
                progress.Report((92, LuaFileName + " saved."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                // Not critical — user can get it from the repo.
                progress.Report((92, LuaFileName + " download failed (best effort — get it from " + RepoUrl + "/releases)."));
            }
        }

        // Stamp installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        string? baseDir  = ResolveBaseGameDir();
        string  baseLine = baseDir != null
            ? "Detected your FFXII TZA install at \"" + baseDir + "\". "
            : "Reminder: you need Final Fantasy XII: The Zodiac Age (Steam appid " + SteamAppId + ") or a PS2 ISO. ";

        progress.Report((100,
            "FFXIITM assets " + version + " downloaded to " + GameDirectory + ". " +
            baseLine +
            "Next steps: (1) Place " + ApWorldFileName + " into your Archipelago " +
            "custom_worlds folder. (2) Generate your seed. (3) Load the game in BizHawk " +
            "(PS2 core). (4) Load " + LuaFileName + " in BizHawk's Lua console. " +
            "(5) Start the Archipelago Python client (FFXIITM Client) and connect " +
            "to your server. See Settings for the full guided steps."));
    }

    // ── VerifyInstallAsync ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── ValidateExistingInstall (TZA folder picker) ───────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Final Fantasy XII: " +
                   "The Zodiac Age install folder.";

        if (LooksLikeTzaDir(folder)) return null;

        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeTzaDir(nested)) return null;
        }
        catch { }

        return "That does not look like a Final Fantasy XII: The Zodiac Age " +
               "installation. Pick the folder that contains the game exe " +
               "(the Steam version is normally in steamapps\\common\\" +
               SteamCommonFolderName + ").";
    }

    // ── LaunchAsync ───────────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: the AP connection for FFXIITM is managed by the Python client
        // (FFXIITMContext). There is no mechanism to pre-fill credentials into
        // it from here. ConnectsItself = true — we just start the base game.
        _ = session;
        StartBaseGame();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartBaseGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge (inert — ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The Python client receives items and writes them to %LocalAppData%\FFXIITM.
        // The BizHawk Lua script reads those files while the game runs.
        // Nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The Python client and BizHawk Lua script handle their own state.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Final Fantasy XII Trial Mode's Archipelago integration is a community " +
                "project (gaithernOrg/FFXIITMAPArchipelago). It uses a BizHawk Lua script " +
                "that bridges the running game to a Python AP client via files in " +
                "%LocalAppData%\\FFXIITM. The Python client (bundled with your Archipelago " +
                "install) holds the AP slot connection — the launcher cannot pre-fill it. " +
                "You need Final Fantasy XII: The Zodiac Age (Steam appid " + SteamAppId + ") " +
                "or a PS2 ISO, and BizHawk with its PS2 core configured. " +
                "These external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Assets section ────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("DOWNLOADED ASSETS", muted));

        bool apworldOk = File.Exists(ApWorldPath);
        bool luaOk     = File.Exists(LuaPath);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = apworldOk
                ? "ffxiitm.apworld downloaded: " + ApWorldPath + "\n" +
                  "Place this in your Archipelago custom_worlds folder."
                : "ffxiitm.apworld not downloaded yet (use Install on the Play tab).",
            FontSize = 11, Foreground = apworldOk ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = luaOk
                ? "ffxii_tm_ap.lua downloaded: " + LuaPath + "\n" +
                  "Load this in BizHawk's Lua Console while the game is running."
                : "ffxii_tm_ap.lua not downloaded yet (use Install on the Play tab).",
            FontSize = 11, Foreground = luaOk ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Open assets folder button ─────────────────────────────────────
        if (apworldOk || luaOk)
        {
            string dir = GameDirectory;
            var openBtn = new System.Windows.Controls.Button
            {
                Content = "Open assets folder",
                Width   = 180,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 6, 0, 6),
                Margin  = new System.Windows.Thickness(0, 0, 0, 12),
                Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
                Foreground  = fg,
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x40, 0x30, 0x60)),
            };
            openBtn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
                catch { }
            };
            panel.Children.Add(openBtn);
        }

        // ── Base game section ─────────────────────────────────────────────
        panel.Children.Add(SectionHeader("FINAL FANTASY XII: THE ZODIAC AGE (BASE GAME)", muted));

        string? steamDir    = DetectSteamInstallDir();
        string? overrideDir = LoadOverrideDir();
        string? baseDir     = overrideDir != null && LooksLikeTzaDir(overrideDir)
            ? overrideDir
            : steamDir;

        string detectMsg;
        if (overrideDir != null && string.Equals(baseDir, overrideDir, StringComparison.OrdinalIgnoreCase))
            detectMsg = "Using your selected folder: " + baseDir;
        else if (steamDir != null)
            detectMsg = "Detected Steam install (appid " + SteamAppId + "): " + steamDir;
        else
            detectMsg = "Base game not detected. Pick your install folder below, or " +
                        "install via Steam (appid " + SteamAppId + "). You may also use a PS2 ISO in BizHawk.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = baseDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? steamDir ?? "",
            IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Your FFXII TZA install folder. Detected from Steam automatically.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        string capturedOverride = overrideDir ?? "";
        string capturedSteam    = steamDir    ?? "";
        dirBtn.Click += (_, _) =>
        {
            string initDir = capturedOverride.Length > 0 && Directory.Exists(capturedOverride)
                ? capturedOverride
                : capturedSteam.Length > 0 && Directory.Exists(capturedSteam)
                    ? capturedSteam
                    : AppContext.BaseDirectory;
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select your FFXII: The Zodiac Age install folder",
                InitialDirectory = initDir,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a valid FFXII TZA folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeTzaDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeTzaDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically. " +
                   "For BizHawk + PS2 ISO setups, no install folder is required here — " +
                   "point BizHawk directly at your ISO.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Connection note ───────────────────────────────────────────────
        panel.Children.Add(SectionHeader("CONNECTING (via Archipelago Python client)", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "The AP connection is managed by the FFXIITM Python client bundled " +
                "with your Archipelago installation (run it from the Archipelago launcher " +
                "as \"FFXIITM Client\"). Start the Python client, enter your server " +
                "host:port, slot name and password, then connect. The client writes check " +
                "and item files to %LocalAppData%\\FFXIITM, which the BizHawk Lua script " +
                "reads while the game runs. The launcher cannot pre-fill these credentials.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Guided setup steps ────────────────────────────────────────────
        panel.Children.Add(SectionHeader("GUIDED SETUP", muted));
        foreach (string step in new[]
        {
            "1. Own Final Fantasy XII: The Zodiac Age (Steam appid " + SteamAppId + ") " +
               "or a PS2 ISO of the original Final Fantasy XII.",
            "2. Install BizHawk (https://tasvideos.org/BizHawk/ReleaseHistory) with the " +
               "PS2 (PCSX2) core and BIOS configured.",
            "3. Use Install on the Play tab to download " + ApWorldFileName + " and " +
               LuaFileName + " into " + GameDirectory + ".",
            "4. Copy " + ApWorldFileName + " from " + GameDirectory +
               " into your Archipelago installation's custom_worlds folder " +
               "(next to ArchipelagoLauncher.exe).",
            "5. Use the Archipelago website or your host to generate a FFXII Trial Mode " +
               "multiworld seed.",
            "6. Load Final Fantasy XII in BizHawk (PS2 core), navigate to Trial Mode " +
               "in-game, then load " + LuaFileName + " in BizHawk Tools → Lua Console.",
            "7. From the Archipelago launcher, start the \"FFXIITM Client\", enter your " +
               "server host:port, slot name and password, and connect. Checks and items " +
               "flow automatically via the %LocalAppData%\\FFXIITM shared folder.",
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
        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new (string, string)[]
        {
            ("FFXII Trial Mode AP repo (GitHub) ↗",     RepoUrl),
            ("Final Fantasy XII: The Zodiac Age on Steam ↗", SteamStoreUrl),
            ("BizHawk releases ↗",                      "https://tasvideos.org/BizHawk/ReleaseHistory"),
            ("Archipelago Official ↗",                   "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0), FontSize = 12,
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

    private static System.Windows.Controls.TextBlock SectionHeader(
        string text, System.Windows.Media.Brush muted)
        => new System.Windows.Controls.TextBlock
        {
            Text = text,
            FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 8, 0, 8),
        };

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(ReleasesApiUrl, ct);
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

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve latest release: (version, apworldUrl, luaUrl).
    /// Falls back to pinned URLs if the API is unreachable.
    private async Task<(string Version, string? ApWorldUrl, string? LuaUrl)>
        ResolveLatestAssetsAsync(CancellationToken ct)
    {
        try
        {
            // Scan the last few releases — the Lua script may not be in the latest tag.
            string json = await _http.GetStringAsync(ReleasesApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return (FallbackVersion, FallbackApWorldUrl, FallbackLuaUrl);

            string? latestVersion = null;
            string? apworldUrl    = null;
            string? luaUrl        = null;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string? tag = el.TryGetProperty("tag_name", out var t)
                    ? NormalizeTag(t.GetString())
                    : null;
                if (tag == null) continue;

                latestVersion ??= tag;   // first = latest

                if (!el.TryGetProperty("assets", out var assets)
                    || assets.ValueKind != JsonValueKind.Array) continue;

                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (apworldUrl == null && lower.EndsWith(".apworld"))
                        apworldUrl = url;
                    if (luaUrl == null && lower.EndsWith(".lua"))
                        luaUrl = url;
                }

                if (latestVersion != null && apworldUrl != null && luaUrl != null)
                    break;   // found all we need
            }

            return (
                latestVersion ?? FallbackVersion,
                apworldUrl    ?? FallbackApWorldUrl,
                luaUrl        ?? FallbackLuaUrl
            );
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            return (FallbackVersion, FallbackApWorldUrl, FallbackLuaUrl);
        }
    }

    // ── Private helpers — download ─────────────────────────────────────────────

    private async Task<byte[]> DownloadWithProgressAsync(
        string url,
        IProgress<(int Pct, string Msg)> progress,
        int pctStart,
        int pctEnd,
        CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total      = response.Content.Headers.ContentLength ?? -1;
        long downloaded = 0;
        int  range      = pctEnd - pctStart;

        await using var src = await response.Content.ReadAsStreamAsync(ct);
        using var ms  = new MemoryStream(total > 0 ? (int)total : 65536);
        var buf = new byte[65536];
        int read;
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, read);
            downloaded += read;
            if (total > 0)
            {
                int pct = pctStart + (int)((long)range * downloaded / total);
                progress.Report((pct, "Downloading... " + downloaded / 1024 + " KB"));
            }
        }
        return ms.ToArray();
    }

    // ── Private helpers — launch ───────────────────────────────────────────────

    private void StartBaseGame()
    {
        // Prefer Steam protocol launch when a Steam install is detected.
        if (DetectSteamInstallDir() != null &&
            SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true;
                return;
            }
            catch { }
        }

        // Fall back to the detected / overridden exe.
        string? dir = ResolveBaseGameDir();
        string? exe = dir != null ? FindTzaExeIn(dir) : null;
        if (exe != null && File.Exists(exe))
        {
            try
            {
                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName         = exe,
                    WorkingDirectory = dir!,
                    UseShellExecute  = true,
                });
                if (proc != null)
                {
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
                }
                return;
            }
            catch { }
        }

        throw new FileNotFoundException(
            "Could not find the game exe. Open Settings and pick your " +
            "Final Fantasy XII: The Zodiac Age install folder, or install via Steam " +
            "(appid " + SteamAppId + "). You can also launch the game yourself (or " +
            "load it in BizHawk as a PS2 ISO).",
            "FF12.exe");
    }

    private static string? FindTzaExeIn(string dir)
    {
        try
        {
            // TZA typical path: <install>\x64\FF12.exe
            string preferred = Path.Combine(dir, "x64", "FF12.exe");
            if (File.Exists(preferred)) return preferred;

            // Check root of install folder too.
            string root = Path.Combine(dir, "FF12.exe");
            if (File.Exists(root)) return root;

            foreach (string exe in Directory.GetFiles(dir, "*.exe", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToUpperInvariant();
                if (name == "FF12" || name.Contains("FFXII") || name.Contains("FINAL FANTASY XII"))
                    return exe;
            }
        }
        catch { }
        return null;
    }

    // ── Private helpers — base-game detection ──────────────────────────────────

    private string? ResolveBaseGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeTzaDir(ov)) return ov;
        try { return DetectSteamInstallDir(); } catch { return null; }
    }

    private static bool LooksLikeTzaDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, "x64", "FF12.exe"))) return true;
            if (File.Exists(Path.Combine(dir, "FF12.exe"))) return true;
            foreach (string exe in Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToUpperInvariant();
                if (name == "FF12") return true;
            }
            return false;
        }
        catch { return false; }
    }

    // ── Steam detection ────────────────────────────────────────────────────────

    private static string? DetectSteamInstallDir()
    {
        try
        {
            foreach (string steamRoot in SteamRoots())
            {
                if (string.IsNullOrWhiteSpace(steamRoot)) continue;
                foreach (string lib in SteamLibraryRoots(steamRoot))
                {
                    try
                    {
                        string steamapps = Path.Combine(lib, "steamapps");
                        string manifest  = Path.Combine(steamapps, "appmanifest_" + SteamAppId + ".acf");
                        if (!File.Exists(manifest)) continue;

                        string common     = Path.Combine(steamapps, "common");
                        string? installDir = ReadAcfInstallDir(manifest);
                        if (installDir != null)
                        {
                            string candidate = Path.Combine(common, installDir);
                            if (LooksLikeTzaDir(candidate)) return candidate;
                        }
                        string conventional = Path.Combine(common, SteamCommonFolderName);
                        if (LooksLikeTzaDir(conventional)) return conventional;
                    }
                    catch { }
                }
            }
        }
        catch { }
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

        string? hklm2 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm2)) yield return NormalizeSteamPath(hklm2);

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

    // ── Private helpers — settings sidecar ────────────────────────────────────

    private sealed class FfxiitmSettings
    {
        public string? InstallOverride { get; set; }
    }

    private FfxiitmSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<FfxiitmSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(FfxiitmSettings s)
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
    {
        var s = LoadSettings();
        s.InstallOverride = p;
        SaveSettings(s);
    }
}
