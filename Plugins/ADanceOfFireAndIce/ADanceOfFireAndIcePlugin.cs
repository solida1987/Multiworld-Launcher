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

namespace LauncherV2.Plugins.ADanceOfFireAndIce;

// ═══════════════════════════════════════════════════════════════════════════════
// ADanceOfFireAndIcePlugin — install / launch for "A Dance of Fire and Ice"
// (7th Beat Games, 2019) played through the ADOFAI_AP-Mod by ClaudeChibout,
// which contains the in-game Archipelago client. This is a NATIVE
// "ConnectsItself" integration: the game itself speaks to the AP server (no
// emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified against the mod source) ──────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned
// copy of A Dance of Fire and Ice (Steam appid 977950 — verified from the
// README which links to store.steampowered.com/app/977950). Archipelago support
// is delivered as a BepInEx 5.x mod added on top. The verified facts:
//
//   * THE AP WORLD game string is "A Dance of Fire and Ice" (verified directly
//     against CLIENT_AP.cs in ClaudeChibout/ADOFAI_AP-Mod, line:
//     `session.TryConnectAndLogin("A Dance of Fire and Ice", slot, ...)` — this
//     is the EXACT string the mod passes to the AP server). GameId here =
//     "adofai". ADOFAI is a community world (an .apworld is bundled in the
//     release — but the launcher only uses the mod zip for install, NOT the
//     .apworld, since hosting is the user's AP server's concern).
//
//   * THE MOD repo is ClaudeChibout/ADOFAI_AP-Mod (verified live 2026-06-14).
//     The latest STABLE release is v1.0.7 (v1.0.8-pre is pre-release and skipped
//     by the default/latest API endpoint). The release ships ONE asset:
//     "ADOFAI_AP-Mod.zip" — a zip whose contents are extracted DIRECTLY INTO the
//     ADOFAI installation directory (the folder containing the game executable).
//     The README states explicitly: "extracting it directly into your ADOFAI
//     installation directory (the folder containing the game's executable)." The
//     zip therefore carries a BepInEx/ subtree that merges with any existing
//     BepInEx install.
//
//   * CRITICAL HONESTY — BepInEx 5.x IS A SEPARATE PREREQUISITE the mod zip does
//     NOT bundle. ADOFAI is a Unity MONO game; the README states "This mod uses
//     BepInEx to run." BepInEx 5 ships as a portable zip (no wizard), extracted
//     into the game root — the plugin CAN automate this step, making install
//     fully automated for the user. The plugin stages BepInEx first, then the mod
//     on top (extracting the mod zip into the game root merges with the BepInEx
//     tree, which is the correct installation method per the README).
//
//   * CONNECTION can be PARTIALLY PRE-FILLED (verified from source). The mod uses
//     BepInEx's Config.Bind system to store the last-used server IP, port, and
//     slot name in the BepInEx config file:
//       <game>\BepInEx\config\com.shotal.ADOFAI_AP.cfg
//     with section [ConnectionForm] and keys IP, Port, pseudo (slot name).
//     This launcher CAN write that cfg file before launching so the in-game
//     connection menu is pre-filled with the session's server / port / slot.
//     The PASSWORD is entered in-game via the menu (the mod has no config key
//     for it — verified from source). The user presses 'M' in-game to open the
//     Archipelago menu, sees the pre-filled fields, can type the password if any,
//     and clicks Connect. This is the BEST honest prefill achievable.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam ADOFAI install via the Windows registry (HKCU SteamPath +
//      HKLM WOW6432Node\Valve\Steam InstallPath), parsing steamapps\
//      libraryfolders.vdf for every library root and locating steamapps\common\
//      "A Dance of Fire and Ice" via appmanifest_977950.acf. A manual install-dir
//      OVERRIDE (settings folder picker) is also supported and takes precedence;
//      it is validated (must contain the ADOFAI exe) and persisted in this
//      plugin's OWN sidecar (Games/ROMs/adofai/adofai_launcher.json) — Core/
//      SettingsStore is NOT modified.
//   2. INSTALL/UPDATE (fully automated + portable) = (a) if BepInEx 5.x is not
//      present in the detected install, download the pinned BepInEx 5.4.22 x64
//      portable zip and extract it into the ADOFAI game root; (b) download the
//      latest "ADOFAI_AP-Mod.zip" from ClaudeChibout/ADOFAI_AP-Mod releases and
//      extract it into the game root (merging with the BepInEx tree). A version
//      stamp is saved in the sidecar. Guided steps + links cover the in-game
//      steps (press M, enter password, connect).
//   3. LAUNCH = write the BepInEx cfg prefill (IP, port, slot), then run the
//      ADOFAI exe (or steam://rungameid/977950 as fallback).
//      ConnectsItself = true (the mod owns the slot). SupportsStandalone = true
//      (plain ADOFAI runs fine without AP).
//
// ── DEFENSIVE / UNVERIFIED ────────────────────────────────────────────────────
//   * "Installed" is judged by the presence of "ADOFAI_AP.dll" anywhere under the
//     detected/override ADOFAI install's BepInEx/plugins tree (case-insensitive,
//     recursive). This matches the BepInPlugin GUID source ("com.shotal.ADOFAI_AP",
//     assembly "ADOFAI_AP") — the output DLL is ADOFAI_AP.dll. The user may also
//     have installed the mod by hand, which this launcher should honor.
//   * Steam library parsing is defensive: a tolerant VDF scan; any failure
//     degrades to "ADOFAI not found" rather than throwing.
//   * The cfg prefill is best-effort (silently skipped if BepInEx/config/ does
//     not exist yet — it is created on the first BepInEx launch; this plugin
//     creates the directory itself to handle the first-launch case).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ADanceOfFireAndIcePlugin : IGamePlugin
{
    // ── Constants — the ADOFAI_AP-Mod (real repo, verified 2026-06-14) ─────────
    private const string MOD_OWNER   = "ClaudeChibout";
    private const string MOD_REPO    = "ADOFAI_AP-Mod";
    private const string ModRepoUrl  = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl    = "https://archipelago.gg/tutorial/A%20Dance%20of%20Fire%20and%20Ice/setup/en";
    private const string GameInfoUrl      = "https://archipelago.gg/games/A%20Dance%20of%20Fire%20and%20Ice/info/en";
    private const string ArchipelagoSite  = "https://archipelago.gg";

    // BepInEx 5.4.22 (Unity MONO x64) — the SEPARATE portable mod-loader prerequisite.
    // ADOFAI is a Unity MONO game; the mod README states "This mod uses BepInEx to run."
    // BepInEx 5 ships as a portable zip (extracted into the game root, no installer wizard).
    private const string BepInExSite      = "https://github.com/BepInEx/BepInEx/releases";
    private const string BepInExVersion   = "5.4.22";
    private static readonly string BepInExZipUrl =
        "https://github.com/BepInEx/BepInEx/releases/download/v5.4.22/" +
        "BepInEx_x64_5.4.22.0.zip";

    // Steam — A Dance of Fire and Ice appid 977950 (verified from mod README link).
    private const string AdofaiSteamAppId       = "977950";
    private static readonly string SteamRunUrl  = $"steam://rungameid/{AdofaiSteamAppId}";

    /// Standard Steam install subfolder name for ADOFAI.
    private const string SteamCommonFolderName  = "A Dance of Fire and Ice";

    /// The base-game executable name.
    private const string AdofaiExeName          = "A Dance of Fire and Ice.exe";

    /// The mod's primary plugin DLL found under BepInEx/plugins (recursive).
    /// Matches the BepInPlugin assembly name "ADOFAI_AP" in ADOFAI_AP.cs.
    private const string ModPrimaryDll          = "ADOFAI_AP.dll";

    // BepInEx config path for the AP mod (relative to the game root).
    // Created by writing BepInEx\config\com.shotal.ADOFAI_AP.cfg.
    private const string BepInExCfgRelPath =
        @"BepInEx\config\com.shotal.ADOFAI_AP.cfg";

    /// Pinned fallback for the mod when the GitHub API is unreachable.
    /// v1.0.7 verified live 2026-06-14; v1.0.8-pre is pre-release (skipped).
    private const string FallbackVersion  = "1.0.7";
    private const string FallbackZipName  = "ADOFAI_AP-Mod.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "adofai";
    public string DisplayName => "A Dance of Fire and Ice";
    public string Subtitle    => "Native PC · BepInEx mod";

    /// EXACT AP game string — verified directly against CLIENT_AP.cs line
    /// `session.TryConnectAndLogin("A Dance of Fire and Ice", slot, ...)`.
    public string ApWorldName => "A Dance of Fire and Ice";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "adofai.png");

    public string ThemeAccentColor => "#E84040";   // ADOFAI red/fire-and-ice accent

    public string[] GameBadges => new[] { "Steam · BepInEx mod" };

    public string Description =>
        "A Dance of Fire and Ice, the 2019 rhythm game by 7th Beat Games, played " +
        "through the ADOFAI_AP-Mod by ClaudeChibout — a BepInEx mod that bundles an " +
        "in-game Archipelago client, so the game connects to the multiworld itself " +
        "with no emulator and no bridge. Levels are unlocked as you receive Key items " +
        "from the multiworld; completing levels sends checks back. You bring your own " +
        "copy of A Dance of Fire and Ice (owned on Steam); the integration runs on " +
        "BepInEx 5 for Unity. The launcher detects your Steam install, downloads and " +
        "stages BepInEx and the Archipelago mod, and pre-fills your server / port / " +
        "slot in the mod's config file. Press M in-game to open the Archipelago menu, " +
        "enter your password (if any), and connect.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = ADOFAI_AP.dll is found anywhere under the detected/override
    /// ADOFAI install's BepInEx tree (the user may have installed by hand).
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads. The actual mod is extracted INTO the
    /// ADOFAI game root. Exposed as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "ADanceOfFireAndIce");

    /// This plugin's OWN settings sidecar.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "adofai_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The ADOFAI_AP-Mod connects to and reports to the AP server itself —
    // the launcher relays nothing. These exist for interface compatibility.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ConnectsItself / SupportsStandalone
    public bool ConnectsItself    => true;
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = FindInstalledModDll() != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch { InstalledVersion = null; }
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
        // 0. We need an ADOFAI install to deploy into.
        progress.Report((2, "Locating your A Dance of Fire and Ice installation..."));
        string? gameDir = ResolveAdofaiDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find an A Dance of Fire and Ice installation. Open this " +
                "game's Settings and pick your ADOFAI folder (the one containing " +
                "\"A Dance of Fire and Ice.exe\"), or install ADOFAI via Steam first. " +
                "The Archipelago mod is added on top of your own copy of the game.");

        // 1. Resolve latest stable mod release (pre-releases are skipped).
        progress.Report((5, "Checking the latest ADOFAI_AP-Mod release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the ADOFAI_AP-Mod download on GitHub. Check your " +
                "internet connection, or download it manually from " +
                ModRepoUrl + "/releases. The mod releases page is at " + ModRepoUrl + ".");

        // 2. Stage BepInEx 5.4.22 x64 (portable zip → extract to game root) if absent.
        bool bepOk = BepInExPresent(gameDir);
        if (!bepOk)
        {
            progress.Report((10, "Downloading BepInEx 5.4.22 x64 (Unity MONO)..."));
            await DownloadAndExtractZipToRootAsync(
                BepInExZipUrl, "BepInEx-5.4.22.zip", gameDir,
                preserveSingleRoot: false,
                pctStart: 10, pctEnd: 45, progress, ct);
        }
        else
        {
            progress.Report((45, "BepInEx already present — keeping your existing install."));
        }

        // 3. Download and extract the ADOFAI_AP-Mod zip into the game root.
        //    Per the README: "extracting it directly into your ADOFAI installation
        //    directory (the folder containing the game's executable)." The zip carries
        //    a BepInEx/ subtree that merges with the existing BepInEx install.
        progress.Report((48, $"Downloading ADOFAI_AP-Mod {version}..."));
        await DownloadAndExtractZipToRootAsync(
            zipUrl, $"adofai-ap-mod-{version}.zip", gameDir,
            preserveSingleRoot: false,
            pctStart: 48, pctEnd: 94, progress, ct);

        // 4. Stamp version in sidecar.
        WriteStampedVersion(version);
        InstalledVersion = version;

        string bepNote = bepOk
            ? "BepInEx was already installed."
            : "BepInEx 5.4.22 was installed into your ADOFAI folder.";

        progress.Report((100,
            $"A Dance of Fire and Ice Archipelago mod {version} installed. " +
            bepNote +
            " To play: launch ADOFAI from the Play button, wait for BepInEx to " +
            "initialize (first launch may take a moment), then press M in-game to " +
            "open the Archipelago menu. Your server and slot are pre-filled — enter " +
            "your password (if any) and click Connect. The launcher cannot enter the " +
            "password for you (see Settings for details)."));
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
        // Pre-fill the BepInEx config with the session's server, port, and slot so
        // the in-game 'M' menu opens with those fields already filled in. The password
        // is entered in-game (no config key exists for it in the mod — verified from
        // source). This is the best honest prefill achievable for this mod.
        TryWriteBepInExConfig(session.ServerUri, session.SlotName);
        StartAdofai();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // No AP session — just launch the game (the mod's menu won't connect without
        // a session, but the game itself plays fine standalone).
        StartAdofai();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        // The BepInEx cfg only contains server/slot (no password) — nothing sensitive
        // to scrub on stop.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself) ────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "A Dance of Fire and Ice is your own game (Steam) with the " +
                   "ADOFAI_AP-Mod by ClaudeChibout added on top via BepInEx. The " +
                   "launcher detects your Steam install, downloads BepInEx and the " +
                   "Archipelago mod automatically, and pre-fills your server / port / " +
                   "slot in the BepInEx config. The password must be entered in-game " +
                   "via the Archipelago menu (press M). No launcher can do that for you.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install ─────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "ADOFAI INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveAdofaiDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "A Dance of Fire and Ice not detected. Pick your install folder below, " +
              "or install ADOFAI via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text         = detectMsg, FontSize = 11,
            Foreground   = gameDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // BepInEx status
        bool bepOk = gameDir != null && BepInExPresent(gameDir);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = gameDir == null ? "" :
                   bepOk ? "BepInEx found in your ADOFAI folder."
                         : "BepInEx not found yet — Install on the Play tab will stage it automatically.",
            FontSize = 11, Foreground = bepOk ? success : muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        // Mod status
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                ? "ADOFAI_AP mod found: " + modDll
                : "ADOFAI_AP mod not found yet (use Install on the Play tab).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Folder override picker
        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = overrideDir ?? gameDir ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your ADOFAI install folder (contains \"A Dance of Fire and Ice.exe\"). " +
                          "Detected from Steam automatically; set it here for non-standard paths.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content     = "Select folder...",
            Width       = 120,
            Padding     = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your ADOFAI install folder (contains \"A Dance of Fire and Ice.exe\")",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                                   ? (overrideDir ?? gameDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                if (!LooksLikeAdofaiDir(picked))
                {
                    System.Windows.MessageBox.Show(
                        "That does not look like an A Dance of Fire and Ice installation. " +
                        "Pick the folder that contains \"A Dance of Fire and Ice.exe\".",
                        "Not an ADOFAI folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                SaveOverrideDir(picked);
                dirBox.Text = picked;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Steam installs are detected automatically (appid 977950). Use this " +
                   "picker for non-standard Steam library locations.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING", FontSize = 10,
            FontWeight   = System.Windows.FontWeights.SemiBold,
            Foreground   = muted,
            Margin       = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "When you click Play (AP mode), the launcher writes your server " +
                   "address, port, and slot name into the mod's BepInEx config file so " +
                   "they appear pre-filled when you press M in-game. The password must be " +
                   "typed in the in-game Archipelago menu — the mod has no config key for " +
                   "it. After entering the password (if any), click Connect.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided steps ──────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own A Dance of Fire and Ice on Steam. Install it if you have not yet. " +
                "Use the folder picker above if it was not auto-detected.",
            "2. Click Install on the Play tab. This downloads BepInEx 5.4.22 and the " +
                "ADOFAI_AP-Mod and extracts both into your ADOFAI folder automatically.",
            "3. Click Play (AP mode). The launcher pre-fills your server / port / slot " +
                "in the BepInEx config and launches the game.",
            "4. In-game: press M to open the Archipelago menu. Your server and slot are " +
                "already filled in. Enter your password (if any) and click Connect.",
            "5. On the first launch with BepInEx, the game may take a moment to initialize " +
                "BepInEx (you will see the console window appear). This is normal.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ──────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("ADOFAI_AP-Mod (GitHub) ↗",               ModRepoUrl),
            ("A Dance of Fire and Ice Setup Guide ↗",   SetupGuideUrl),
            ("A Dance of Fire and Ice AP Info ↗",       GameInfoUrl),
            ("BepInEx releases ↗",                      BepInExSite),
            ("Archipelago Official ↗",                  ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content             = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding             = new System.Windows.Thickness(0, 2, 0, 2),
                Background          = System.Windows.Media.Brushes.Transparent,
                BorderThickness     = new System.Windows.Thickness(0),
                FontSize            = 12,
                Margin              = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground          = new SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor              = System.Windows.Input.Cursors.Hand,
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
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                // Skip pre-releases in the news feed.
                if (el.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
                    continue;

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

    /// Resolve the latest STABLE mod release (pre-releases skipped).
    /// Returns (version, zip download URL) or (FallbackVersion, FallbackZipUrl).
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(CancellationToken ct)
    {
        try
        {
            // /releases endpoint: iterate to find the latest non-prerelease.
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return (FallbackVersion, FallbackZipUrl);

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.TryGetProperty("prerelease", out var pre) && pre.GetBoolean())
                    continue;
                if (el.TryGetProperty("draft", out var dr) && dr.GetBoolean())
                    continue;

                string? version = el.TryGetProperty("tag_name", out var t)
                    ? NormalizeTag(t.GetString())
                    : null;
                if (version == null) continue;

                if (el.TryGetProperty("assets", out var assets)
                    && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        if (name == null || url == null) continue;

                        // The mod asset is "ADOFAI_AP-Mod.zip"; skip the .apworld sidecar.
                        if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;

                        return (version, url);
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → fall back */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — BepInEx config prefill ──────────────────────────────

    /// Write the BepInEx config for the ADOFAI_AP mod so the in-game menu opens
    /// with server / port / slot pre-filled. The password has no config key in the
    /// mod (verified from source) — the user types it in-game.
    private void TryWriteBepInExConfig(string serverUri, string slotName)
    {
        try
        {
            string? gameDir = ResolveAdofaiDir();
            if (gameDir == null) return;

            string cfgPath = Path.Combine(gameDir, BepInExCfgRelPath);
            Directory.CreateDirectory(Path.GetDirectoryName(cfgPath)!);

            // Parse the server URI into host + port.
            string host = serverUri;
            int    port = 38281; // AP default
            try
            {
                string uri = serverUri.Contains("://")
                    ? serverUri
                    : "ws://" + serverUri;
                var u = new Uri(uri);
                host = u.Host;
                if (u.Port > 0) port = u.Port;
            }
            catch { /* leave host/port from the raw URI */ }

            // Write BepInEx INI format with [ConnectionForm] section.
            // BepInEx config uses INI format: [Section] \n Key = Value.
            var sb = new StringBuilder();
            sb.AppendLine("[ConnectionForm]");
            sb.AppendLine();
            sb.AppendLine("## IP for ConnectionForm");
            sb.AppendLine($"IP = {host}");
            sb.AppendLine();
            sb.AppendLine("## Port for ConnectionForm");
            sb.AppendLine($"Port = {port}");
            sb.AppendLine();
            sb.AppendLine("## Pseudo for ConnectionForm");
            sb.AppendLine($"pseudo = {slotName}");
            sb.AppendLine();
            sb.AppendLine("[Settings]");
            sb.AppendLine();
            sb.AppendLine("## Send location when landing on a portal");
            sb.AppendLine("SendLocationOnLandOnPortal = true");

            File.WriteAllText(cfgPath, sb.ToString(), new UTF8Encoding(false));
        }
        catch { /* best-effort; the user can still enter details in-game */ }
    }

    // ── Private helpers — ADOFAI detection ────────────────────────────────────

    private string? ResolveAdofaiDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeAdofaiDir(ov)) return ov;
        try { return DetectSteamAdofaiDir(); }
        catch { return null; }
    }

    private static bool LooksLikeAdofaiDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, AdofaiExeName))) return true;
            if (Directory.Exists(Path.Combine(dir, "A Dance of Fire and Ice_Data"))) return true;
            return false;
        }
        catch { return false; }
    }

    private static bool BepInExPresent(string gameDir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(gameDir) || !Directory.Exists(gameDir)) return false;
            // BepInEx 5 creates a "BepInEx" folder plus winhttp.dll (the doorstop proxy)
            // at the game root.
            if (Directory.Exists(Path.Combine(gameDir, "BepInEx"))) return true;
            if (File.Exists(Path.Combine(gameDir, "winhttp.dll"))) return true;
            return false;
        }
        catch { return false; }
    }

    private static string? DetectSteamAdofaiDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{AdofaiSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common    = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeAdofaiDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeAdofaiDir(conventional)) return conventional;
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

    // ── Private helpers — mod detection ──────────────────────────────────────

    private string? FindInstalledModDll()
    {
        try
        {
            string? gameDir = ResolveAdofaiDir();
            if (gameDir == null) return null;
            string pluginsDir = Path.Combine(gameDir, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(
                pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dll).Equals(ModPrimaryDll, StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartAdofai()
    {
        string? gameDir = ResolveAdofaiDir();
        string? exe     = gameDir != null ? Path.Combine(gameDir, AdofaiExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = gameDir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start A Dance of Fire and Ice.");

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

        // Fall back to Steam if we can find it.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true;
                return;
            }
            catch { }
        }

        throw new FileNotFoundException(
            "Could not find \"A Dance of Fire and Ice.exe\". Open this game's Settings " +
            "and pick your ADOFAI install folder, or install ADOFAI via Steam.",
            AdofaiExeName);
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    /// Download a zip from <paramref name="url"/> and extract its contents into
    /// <paramref name="destRoot"/>. When <paramref name="preserveSingleRoot"/> is
    /// false (the default for BepInEx + ADOFAI mod), the zip's top-level directory
    /// wrapper (if any) is stripped so its contents merge directly into destRoot.
    private async Task DownloadAndExtractZipToRootAsync(
        string url,
        string tempFileName,
        string destRoot,
        bool   preserveSingleRoot,
        int    pctStart,
        int    pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip     = Path.Combine(Path.GetTempPath(), tempFileName + "-" + Guid.NewGuid().ToString("N") + ".zip");
        string tempExtract = Path.Combine(Path.GetTempPath(), "adofai-extract-" + Guid.NewGuid().ToString("N"));
        try
        {
            int dlEnd = pctStart + (pctEnd - pctStart) * 7 / 10;
            await DownloadFileAsync(url, tempZip, $"Downloading {tempFileName}...",
                pctStart, dlEnd, progress, ct);

            progress.Report((dlEnd, $"Extracting {tempFileName}..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // Determine the source root: either the extract root, or the single
            // wrapper directory inside it (strip the wrapper when not preserving).
            string srcRoot = tempExtract;
            if (!preserveSingleRoot)
            {
                var entries = Directory.GetFileSystemEntries(tempExtract);
                if (entries.Length == 1 && Directory.Exists(entries[0]))
                    srcRoot = entries[0];
            }

            // Copy everything from srcRoot into destRoot, preserving subdirectory
            // structure (BepInEx/ subtree merges correctly this way).
            CopyDirectory(srcRoot, destRoot);
            progress.Report((pctEnd, $"Extracted {tempFileName}."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    private async Task DownloadFileAsync(
        string url,
        string destPath,
        string msg,
        int    pctStart,
        int    pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        progress.Report((pctStart, msg));
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
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

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class AdofaiSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private AdofaiSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<AdofaiSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(AdofaiSettings s)
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
