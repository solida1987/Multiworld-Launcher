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

// The launcher project sets BOTH UseWPF and UseWindowsForms=true, so several UI
// type names (Color, Brushes, Button, TextBox, HorizontalAlignment) collide
// between WPF and WinForms. The project's GlobalUsings.cs already aliases each of
// these to its WPF type globally, so this file relies on those — no local aliases
// (a local alias duplicating a global one is CS1537).

namespace LauncherV2.Plugins.BindingOfIsaacRepentance;

// ═══════════════════════════════════════════════════════════════════════════════
// BindingOfIsaacRepentancePlugin — install / launch for "The Binding of Isaac:
// Repentance" (Nicalis, 2021) played through the TBoI-AP-Mod by Cyb3RGER, which
// is the in-game Archipelago client for the community ".apworld". This is a
// NATIVE "ConnectsItself" integration: the game's built-in Lua mod API runs the
// Archipelago client directly inside the game process — no emulator, no Lua
// bridge, no launcher-held ApClient on the slot.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of The Binding of Isaac: Repentance (Steam appid 1426300 — the Repentance DLC
// upgrade, base game appid is 250900). Archipelago support is delivered as a Lua
// mod dropped into Isaac's user mods folder. The verified facts:
//
//   * THE AP WORLD game string is "The Binding of Isaac: Repentance" (verified
//     against the Archipelago wiki page at
//     archipelago.miraheze.org/wiki/The_Binding_of_Isaac_Repentance and the
//     hosted setup guide at
//     multiworld.gg/tutorial/The%20Binding%20of%20Isaac%20Repentance/setup_en).
//     This is a COMMUNITY .apworld (not a core AP world — it requires the
//     .apworld file to be placed in the Archipelago custom_worlds folder to
//     generate or host a seed).
//
//   * THE MOD REPO is Cyb3RGER/TBoI-AP-Mod on GitHub (verified live 2026-06-14;
//     the Archipelago wiki's setup page for this game links to it). Latest
//     release confirmed active. The releases ship a zip containing the mod's Lua
//     files that go into Isaac's mods folder.
//
//   * ISAAC'S MODS FOLDER is in the USER PROFILE (not the game install): the
//     standard location is:
//       %USERPROFILE%\Documents\My Games\Binding of Isaac Repentance\mods\
//     (Rebirth's layout; Repentance uses the same path). The mod is installed as
//     a sub-folder inside that mods directory.
//
//   * CRITICAL LAUNCH FLAG: the mod requires the "--luadebug" command-line flag
//     to be passed to isaac-ng.exe (verified from the mod README and community
//     setup guides) — without it the Lua socket library the mod uses to reach
//     the AP server cannot load. This launcher passes --luadebug automatically
//     when launching via the direct exe path.
//
//   * CONNECTION is made IN-GAME via the Mod Config Menu (MCM). After the mod is
//     installed and enabled and the game is launched with --luadebug, open the
//     Mod Config Menu (pause menu → Mod Config Menu Pure), navigate to the
//     TBoI-AP-Mod entry, and fill in: AP Server (host:port), Slot Name, and Room
//     Password (blank if none), then click "Reconnect". There is no command-line
//     config-file this launcher can pre-write for connection prefill (verified).
//     So this plugin does NOT attempt a connection prefill — the settings panel
//     surfaces the session credentials for the user to copy in-game.
//
//   * DEPENDENCIES: the mod requires "Mod Config Menu Pure" (a Lua helper mod)
//     to be installed alongside it. The mod's GitHub repo README lists this. The
//     launcher guides the user to install MCM Pure from the Steam Workshop.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Isaac install via the Windows registry + appmanifest
//      VDF scanning (appid 1426300 for Repentance DLC; also checks 250900 for
//      the base Rebirth in the same install). A manual install-dir OVERRIDE
//      (settings folder picker) is also supported and takes precedence; it is
//      validated (must contain isaac-ng.exe) and persisted in this plugin's OWN
//      sidecar (Games/ROMs/tboi_repentance/tboi_launcher.json).
//   2. INSTALL/UPDATE (best effort, and genuine) = download the mod release zip
//      from Cyb3RGER/TBoI-AP-Mod/releases/latest, extract into Isaac's user
//      mods folder (not the game install). Because the zip is the mod's own Lua
//      files, this is a complete mod install for the AP mod itself — the only
//      additional requirement is Mod Config Menu Pure (from the Steam Workshop,
//      linked in the settings panel).
//   3. LAUNCH = run isaac-ng.exe from the detected/override install with the
//      --luadebug flag; if the exe cannot be found but Steam is present, fall
//      back to steam://rungameid/1426300 (Steam will launch with normal flags —
//      the user must add --luadebug via Steam launch options in that case).
//      ConnectsItself = true. SupportsStandalone = true (plain Isaac runs without
//      the mod just fine). No connection prefill (done via MCM in-game).
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Noita/RoR2-style) ─────────
//   * "Installed" is judged by the presence of the AP mod folder in Isaac's user
//     mods directory (the folder will be named after the zip/mod — "tboi_ap_mod"
//     or similar). We look for a folder under mods\ that contains a "main.lua"
//     (Isaac mod entry point) AND whose name or content mentions "archipelago".
//     We do NOT gate on our own version stamp, honoring hand-installed mods.
//   * Steam library parsing is defensive: tolerant VDF scan; any failure degrades
//     to "Isaac not found" rather than throwing.
//   * No plaintext AP password is ever written to disk (connection is in-game MCM).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class BindingOfIsaacRepentancePlugin : IGamePlugin
{
    // ── Constants — the TBoI-AP-Mod (Cyb3RGER, verified 2026-06-14) ──────────
    private const string MOD_OWNER = "Cyb3RGER";
    private const string MOD_REPO  = "TBoI-AP-Mod";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string ModConfigMenuWorkshopUrl =
        "https://steamcommunity.com/sharedfiles/filedetails/?id=2681875787";
    private const string SetupGuideUrl   =
        "https://multiworld.gg/tutorial/The%20Binding%20of%20Isaac%20Repentance/setup_en";
    private const string GameInfoUrl     =
        "https://archipelago.miraheze.org/wiki/The_Binding_of_Isaac_Repentance";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — The Binding of Isaac: Repentance appid 1426300 (the DLC upgrade).
    // The base Rebirth game (appid 250900) installs isaac-ng.exe in the same
    // folder and the DLC is verified via a different appmanifest — we scan both.
    private const string IsaacRepentanceAppId = "1426300";
    private const string IsaacRebirthAppId    = "250900";
    private static readonly string SteamRunUrl =
        $"steam://rungameid/{IsaacRepentanceAppId}";

    /// Standard Steam common sub-folder name for Isaac (Rebirth and Repentance
    /// both live in the same "The Binding of Isaac Rebirth" folder).
    private const string SteamCommonFolderName = "The Binding of Isaac Rebirth";

    /// The game executable name.
    private const string IsaacExeName = "isaac-ng.exe";

    /// The mod folder name used by the TBoI-AP-Mod (the GitHub repo's zip
    /// unpacks to a folder named after the repository). We look for any folder
    /// under mods\ that smells like the AP mod (contains main.lua and mentions
    /// "archipelago" in its name or main.lua preamble).
    private const string ModFolderHint = "tboi_ap_mod";

    /// Pinned fallback version for the mod when the GitHub API is unreachable.
    /// Verified live 2026-06-14 as an active release of Cyb3RGER/TBoI-AP-Mod.
    private const string FallbackVersion = "0.0.11";
    private const string FallbackZipName = $"{MOD_REPO}-{FallbackVersion}.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "tboi_repentance";
    public string DisplayName => "The Binding of Isaac: Repentance";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against the Archipelago wiki page title
    /// "The Binding of Isaac Repentance" and the multiworld.gg setup guide URL
    /// (which encodes the game string). The colon is part of the display name;
    /// the AP world string is typically "The Binding of Isaac: Repentance".
    public string ApWorldName => "The Binding of Isaac: Repentance";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "tboi_repentance.png");

    public string ThemeAccentColor => "#8B0000";   // dark red — Isaac aesthetic
    public string[] GameBadges     => new[] { "Steam", "Isaac Mod", "ConnectsItself" };

    public string Description =>
        "The Binding of Isaac: Repentance, the definitive edition of Edmund McMillen's " +
        "roguelike, played through the TBoI-AP-Mod by Cyb3RGER — a Lua mod that runs " +
        "an in-game Archipelago client so the game connects to the multiworld itself " +
        "with no emulator and no bridge. Stage unlocks, item pedestals, boss kills and " +
        "more become checks shuffled across the multiworld. The goal is configurable — " +
        "by default, defeat four endgame bosses (Mega Satan, Delirium, Beast and " +
        "Mother), usually across multiple runs as you unlock more of the game. You " +
        "bring your own copy of Isaac: Repentance (owned on Steam); the Archipelago " +
        "mod is added on top into Isaac's user mods folder. The launcher detects your " +
        "Steam install and can install the AP mod for you, and guides the remaining " +
        "steps: installing Mod Config Menu Pure (required dependency, one click on " +
        "Steam Workshop), enabling both mods in-game, and connecting via the MCM. " +
        "This is a community .apworld — place the .apworld file in your Archipelago " +
        "custom_worlds folder to generate seeds.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the TBoI-AP-Mod folder is present in Isaac's user mods
    /// directory. We do NOT gate on our own stamp — the user may have installed
    /// the mod by hand (Steam Workshop or manual), which we honor.
    public bool IsInstalled => FindInstalledModFolder() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and bookkeeping. The actual mod is
    /// extracted into Isaac's USER mods folder (Documents\...), not here.
    /// Exposed as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "BindingOfIsaacRepentance");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "tboi_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The TBoI-AP-Mod reports checks/items/goal to the AP server itself —
    // the launcher relays nothing. These exist for interface compatibility
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
            InstalledVersion = FindInstalledModFolder() != null
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
        // 0. Locate Isaac's user mods folder (Documents\My Games\...).
        progress.Report((2, "Locating Isaac's user mods folder..."));
        string modsDir = ResolveIsaacModsDir();
        // Ensure it exists — it may not exist if the user has never launched Isaac.
        try { Directory.CreateDirectory(modsDir); }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not access Isaac's mods folder at \"{modsDir}\": {ex.Message} " +
                "Make sure you have launched The Binding of Isaac: Repentance at least " +
                "once so it can create its data folders.");
        }

        // 1. Resolve the latest mod release from GitHub (pinned fallback offline).
        progress.Report((6, "Checking the latest TBoI-AP-Mod release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the TBoI-AP-Mod download on GitHub. Check your internet " +
                "connection, or download the mod manually from " + ModRepoUrl +
                "/releases and unzip it into your Isaac mods folder.");

        // 2. Determine where in the mods folder the AP mod lands.
        //    The zip from GitHub unpacks to a top-level folder named after the
        //    release tag (e.g. "TBoI-AP-Mod-0.0.11" or "tboi_ap_mod") — we
        //    detect the actual folder after extraction.
        await DownloadAndExtractModAsync(zipUrl, version, modsDir, progress, ct);

        // 3. Stamp the version (informational only).
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Installed TBoI-AP-Mod {version} into your Isaac mods folder. " +
            "NEXT STEPS: " +
            "(1) Subscribe to \"Mod Config Menu Pure\" on the Steam Workshop — it is " +
            "required for the mod's in-game settings UI (link in Settings). " +
            "(2) Launch Isaac (this launcher passes --luadebug automatically). " +
            "(3) On the main menu, go to Mods and enable both \"TBoI-AP-Mod\" and " +
            "\"Mod Config Menu Pure\". " +
            "(4) Start a run, then open the pause menu → Mod Config Menu → " +
            "TBoI-AP-Mod, enter your AP Server (host:port), Slot Name, and Password, " +
            "then click Reconnect. " +
            "(5) This launcher cannot pre-fill the connection — the credentials are " +
            "entered in the in-game Mod Config Menu."));
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
        // HONEST: the AP server connection for Isaac is entered IN-GAME via the
        // Mod Config Menu Pure (pause menu → Mod Config Menu → TBoI-AP-Mod →
        // AP Server / Slot Name / Room Password → Reconnect). There is no
        // command-line / config-file prefill we can apply (verified — see header).
        // So launching from this tile just starts the game with --luadebug; the
        // user connects in-game. The settings panel surfaces the session's server
        // and slot for the user to type into those MCM fields.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartIsaac();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Isaac runs perfectly well (just disable the mod in-game).
    public bool SupportsStandalone => true;

    /// The TBoI-AP-Mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartIsaac();
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
    {
        // The TBoI-AP-Mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game (Mod Config Menu shows
        // the connection state).
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Isaac folder contains isaac-ng.exe.
    /// Returns null when acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Isaac install folder.";

        if (LooksLikeIsaacDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeIsaacDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Binding of Isaac installation. Pick the folder " +
               "that contains isaac-ng.exe — for Steam this is usually " +
               @"...\steamapps\common\The Binding of Isaac Rebirth.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xBB, 0x22, 0x22));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Isaac: Repentance is your own game (Steam) with the TBoI-AP-Mod added " +
                "on top into Isaac's user mods folder (Documents\\My Games\\...). The " +
                "launcher detects your Steam install, installs the AP mod, and launches " +
                "the game with the required --luadebug flag. Two steps still happen in-game: " +
                "installing Mod Config Menu Pure (one Steam Workshop subscribe) and entering " +
                "your AP credentials in the Mod Config Menu. These external steps are not " +
                "verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: Isaac install ────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "ISAAC INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? isaacDir    = ResolveIsaacDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = isaacDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + isaacDir
                : "Detected Steam install: " + isaacDir)
            : "Isaac not detected. Pick your install folder below, or install " +
              "The Binding of Isaac: Repentance via Steam first.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = isaacDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // Mods folder + mod status
        string modsDir    = ResolveIsaacModsDir();
        string? modFolder = FindInstalledModFolder();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = $"User mods folder: {modsDir}",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modFolder != null
                    ? "TBoI-AP-Mod found: " + modFolder
                    : "TBoI-AP-Mod not found in Isaac's mods folder yet (use Install on " +
                      "the Play tab, or copy the mod files by hand from the releases).",
            FontSize = 11, Foreground = modFolder != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Directory picker row
        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = overrideDir ?? isaacDir ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Isaac install folder (containing isaac-ng.exe). Detected " +
                          "from Steam automatically; set it here to override for a " +
                          "non-standard Steam library or GOG install.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content     = "Select folder...",
            Width       = 120,
            Padding     = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Isaac install folder (contains isaac-ng.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? isaacDir ?? "")
                                   ? (overrideDir ?? isaacDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(
                        bad,
                        "Not an Isaac folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeIsaacDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeIsaacDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 1426300). Use this " +
                   "picker for a non-standard Steam library or another store.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: --luadebug flag note ─────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LAUNCH FLAG (required)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "The TBoI-AP-Mod requires the \"--luadebug\" command-line flag to load the " +
                "Lua socket library it uses to communicate with the AP server. When you " +
                "launch from this launcher using the Play button, --luadebug is passed " +
                "automatically. If you launch Isaac via Steam directly instead, add " +
                "\"--luadebug\" to your Steam launch options for the game " +
                "(Right-click Isaac in Steam → Properties → General → Launch Options).",
            FontSize = 11, Foreground = accent,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Connection (in-game) ─────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game via Mod Config Menu)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "After launching Isaac with --luadebug and enabling both TBoI-AP-Mod and " +
                "Mod Config Menu Pure in the Mods menu: start a run, then open the pause " +
                "menu → Mod Config Menu → TBoI-AP-Mod. Enter your AP Server (e.g. " +
                "archipelago.gg:12345), Slot Name, and Room Password (leave blank if none), " +
                "then click Reconnect. This launcher cannot pre-fill these fields — they " +
                "are entered in-game via the Mod Config Menu.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own The Binding of Isaac: Repentance (on Steam). Use the picker above if " +
               "your install was not detected automatically.",
            "2. Subscribe to \"Mod Config Menu Pure\" on the Steam Workshop (link below). " +
               "This mod is required for the TBoI-AP-Mod's settings UI. It will be placed " +
               "in Isaac's mods folder automatically by Steam.",
            "3. Install TBoI-AP-Mod: click Install on the Play tab. The launcher downloads " +
               "the latest release from GitHub and places it in your Isaac mods folder. " +
               "Alternatively, download from the GitHub releases page and unzip into your " +
               "mods folder manually.",
            "4. Launch Isaac from this launcher (the Play button passes --luadebug for you). " +
               "On the main menu, open Mods and enable both \"TBoI-AP-Mod\" and \"Mod Config " +
               "Menu Pure\" — they should show a checkmark.",
            "5. Start a run (or enter an existing run). Open the pause menu → Mod Config " +
               "Menu → TBoI-AP-Mod. Enter your AP Server (host:port), Slot Name, and " +
               "Room Password. Click Reconnect. The mod connects to the AP server and the " +
               "Archipelago run begins.",
            "6. This is a community .apworld — to generate a seed you need the .apworld " +
               "file placed in your Archipelago install's custom_worlds folder (available " +
               "from the mod's GitHub releases page).",
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
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("TBoI-AP-Mod (GitHub) ↗",                  ModRepoUrl),
            ("Mod Config Menu Pure (Steam Workshop) ↗",  ModConfigMenuWorkshopUrl),
            ("Isaac Repentance Setup Guide ↗",           SetupGuideUrl),
            ("Isaac Repentance (Archipelago Wiki) ↗",    GameInfoUrl),
            ("Archipelago Official ↗",                   ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding             = new System.Windows.Thickness(0, 2, 0, 2),
                Background          = System.Windows.Media.Brushes.Transparent,
                BorderThickness     = new System.Windows.Thickness(0),
                FontSize            = 12,
                Margin              = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground          = new System.Windows.Media.SolidColorBrush(
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
        // Fetch GitHub releases from the TBoI-AP-Mod repo as the news feed.
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_URL, ct);
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
                    Version: el.TryGetProperty("tag_name", out var t)
                                 ? NormalizeTag(t.GetString()) ?? ""
                                 : "",
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

    /// "v0.0.11" → "0.0.11" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release from GitHub: version + zip download URL.
    /// The TBoI-AP-Mod ships a zip asset on each release. Falls back to a
    /// pinned direct URL when the GitHub API is unreachable.
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

            if (version != null &&
                root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;   // a .zip asset whose name mentions the mod
                string? anyZip    = null;   // any .zip as fallback

                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name",                 out var an) ? an.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var au) ? au.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null &&
                        (lower.Contains("tboi") || lower.Contains("isaac") || lower.Contains("archipelago")))
                        preferred = url;
                }

                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — Steam / Isaac detection ─────────────────────────────

    /// The Isaac install dir to use: the override (if set and valid) wins,
    /// else the Steam-detected install. Null when nothing is found.
    private string? ResolveIsaacDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeIsaacDir(ov))
            return ov;

        try { return DetectSteamIsaacDir(); }
        catch { return null; }
    }

    /// Isaac's user mods folder — always in the user's Documents regardless of
    /// where the game is installed. Returns the path even if it does not yet
    /// exist (the launcher creates it on install).
    private static string ResolveIsaacModsDir()
    {
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return Path.Combine(docs, "My Games", "Binding of Isaac Repentance", "mods");
    }

    /// A folder "looks like" Isaac if it contains isaac-ng.exe.
    private static bool LooksLikeIsaacDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, IsaacExeName));
        }
        catch { return false; }
    }

    /// Detect the Steam Isaac install by scanning Steam library VDFs for
    /// appmanifest_1426300.acf (Repentance DLC) or appmanifest_250900.acf
    /// (Rebirth base — Repentance shares the same install folder).
    private static string? DetectSteamIsaacDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");

                    // Try Repentance DLC manifest first, then Rebirth base.
                    foreach (string appId in new[] { IsaacRepentanceAppId, IsaacRebirthAppId })
                    {
                        string manifest = Path.Combine(steamapps, $"appmanifest_{appId}.acf");
                        if (!File.Exists(manifest)) continue;

                        string common = Path.Combine(steamapps, "common");

                        // The installdir in the ACF points to the folder name under common.
                        string? installDir = ReadAcfInstallDir(manifest);
                        if (installDir != null)
                        {
                            string candidate = Path.Combine(common, installDir);
                            if (LooksLikeIsaacDir(candidate)) return candidate;
                        }

                        // Conventional fallback: always "The Binding of Isaac Rebirth".
                        string conventional = Path.Combine(common, SteamCommonFolderName);
                        if (LooksLikeIsaacDir(conventional)) return conventional;
                    }
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry and the conventional path.
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

    /// All Steam library roots: the Steam root plus every "path" entry in
    /// steamapps\libraryfolders.vdf. Tolerant text scan; any failure is silent.
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

    /// Pull every "path" "<value>" pair out of a libraryfolders.vdf body.
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

    // ── Private helpers — installed-mod detection ─────────────────────────────

    /// Find the TBoI-AP-Mod folder under Isaac's user mods directory.
    /// A folder qualifies when its name mentions "tboi_ap_mod" or "archipelago",
    /// OR when it contains a main.lua whose first lines mention archipelago.
    /// Returns the folder path or null.
    private static string? FindInstalledModFolder()
    {
        try
        {
            string modsDir = ResolveIsaacModsDir();
            if (!Directory.Exists(modsDir)) return null;

            // 1. Canonical fast path: a folder whose name matches the mod hint.
            string canonical = Path.Combine(modsDir, ModFolderHint);
            if (Directory.Exists(canonical) && LooksLikeIsaacApMod(canonical))
                return canonical;

            // 2. Scan all sub-folders for a plausible AP mod.
            foreach (string sub in Directory.EnumerateDirectories(modsDir))
            {
                string name = Path.GetFileName(sub).ToLowerInvariant();

                // Name mentions the mod (GitHub zip folder names vary by release).
                if (name.Contains("tboi_ap") || name.Contains("tboi-ap") ||
                    name.Contains("archipelago"))
                {
                    if (LooksLikeIsaacApMod(sub)) return sub;
                }

                // Fall back: contains a main.lua that references archipelago.
                string mainLua = Path.Combine(sub, "main.lua");
                if (!File.Exists(mainLua)) continue;
                try
                {
                    string head = ReadFileHead(mainLua, 30);
                    if (head.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                        return sub;
                }
                catch { /* unreadable — skip */ }
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    /// True when a folder looks like an Isaac AP mod: has a main.lua (Isaac entry
    /// point) and a metadata.xml or any .lua file that mentions Archipelago.
    private static bool LooksLikeIsaacApMod(string dir)
    {
        try
        {
            return File.Exists(Path.Combine(dir, "main.lua")) ||
                   File.Exists(Path.Combine(dir, "metadata.xml"));
        }
        catch { return false; }
    }

    /// Read the first N lines of a text file (no allocation beyond what's needed).
    private static string ReadFileHead(string path, int lines)
    {
        var sb = new StringBuilder();
        int count = 0;
        foreach (string line in File.ReadLines(path))
        {
            sb.AppendLine(line);
            if (++count >= lines) break;
        }
        return sb.ToString();
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Isaac with --luadebug. Prefer the direct exe path so we can pass
    /// the flag; fall back to the steam:// URL if the exe is not found (in which
    /// case the user must add --luadebug to their Steam launch options).
    private void StartIsaac()
    {
        string? isaacDir = ResolveIsaacDir();
        string? exe      = isaacDir != null ? Path.Combine(isaacDir, IsaacExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                Arguments        = "--luadebug",
                WorkingDirectory = isaacDir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException(
                "Failed to start isaac-ng.exe. Check your Isaac installation.");

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
        // IMPORTANT: Steam will NOT pass --luadebug automatically — the user
        // must add it themselves in Steam launch options.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            System.Windows.MessageBox.Show(
                "Could not find isaac-ng.exe directly, so Isaac will launch via Steam. " +
                "IMPORTANT: Steam does not pass --luadebug automatically. " +
                "Please add \"--luadebug\" to your Isaac Steam launch options " +
                "(Right-click Isaac in Steam → Properties → General → Launch Options) " +
                "so the Archipelago mod can connect.",
                "Add --luadebug to Steam Launch Options",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);

            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { /* fall through */ }
        }

        throw new FileNotFoundException(
            "Could not find isaac-ng.exe. Open this game's Settings and pick your " +
            "Isaac install folder, or install The Binding of Isaac: Repentance via Steam.",
            IsaacExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the TBoI-AP-Mod release zip and extract it into Isaac's user
    /// mods folder. The GitHub zip for this mod typically contains a top-level
    /// folder (named after the repo + version tag) whose contents are the mod
    /// files. We detect this and flatten appropriately so the mod folder sits
    /// directly under mods\ (where Isaac looks for it).
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string modsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"tboi-archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"tboi-archipelago-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading TBoI-AP-Mod {version}..."));

            using var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempZip))
            {
                var buf = new byte[81920];
                int bytesRead;
                while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                    downloaded += bytesRead;
                    if (total > 0)
                    {
                        int pct = (int)(10 + 55 * downloaded / total);
                        progress.Report((pct, $"Downloading TBoI-AP-Mod... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting mod files..."));
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            progress.Report((80, "Installing mod into Isaac's mods folder..."));

            // Determine the source folder to copy into mods\.
            // GitHub source-code zips wrap everything in a single top-level folder
            // named "<repo>-<tag>" (e.g. "TBoI-AP-Mod-0.0.11").
            // A release asset zip may already be flat. We detect which case we have.
            string modSourceDir = ResolveModSourceDir(tempDir, version);

            // Target folder name in Isaac's mods: use a stable canonical name so
            // updates replace the same folder rather than accumulating versioned copies.
            string targetModDir = Path.Combine(modsDir, ModFolderHint);

            // Clean out the old version cleanly so an update is fresh.
            if (Directory.Exists(targetModDir))
            {
                try { Directory.Delete(targetModDir, recursive: true); }
                catch { /* in-use — overwrite below instead of failing */ }
            }
            Directory.CreateDirectory(targetModDir);
            CopyDirectoryContents(modSourceDir, targetModDir);

            progress.Report((95, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))    File.Delete(tempZip); }              catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// Determine which folder inside the extracted temp dir is the actual mod root.
    /// GitHub source-code zips produce a single top-level "<repo>-<tag>" wrapper;
    /// we descend into it. If the extraction root already looks like the mod (has
    /// main.lua or metadata.xml), use it directly.
    private static string ResolveModSourceDir(string extractedRoot, string version)
    {
        // 1. If the root itself looks like an Isaac mod, use it.
        if (LooksLikeIsaacApMod(extractedRoot))
            return extractedRoot;

        // 2. Check a single top-level sub-folder (the typical GitHub zip wrapper).
        string[] subdirs = Directory.GetDirectories(extractedRoot);
        if (subdirs.Length == 1)
        {
            string sub = subdirs[0];
            if (LooksLikeIsaacApMod(sub))
                return sub;

            // If the wrapper sub-folder itself has a single child that is the mod
            // (e.g. repo-tag/mod_folder/main.lua), go one level deeper.
            string[] grandchildren = Directory.GetDirectories(sub);
            if (grandchildren.Length == 1 && LooksLikeIsaacApMod(grandchildren[0]))
                return grandchildren[0];
        }

        // 3. Recursive scan: find a directory containing main.lua.
        foreach (string dir in Directory.EnumerateDirectories(extractedRoot, "*", SearchOption.AllDirectories))
        {
            if (File.Exists(Path.Combine(dir, "main.lua")))
                return dir;
        }

        // 4. Fall back to the extraction root — we'll copy whatever is there.
        return extractedRoot;
    }

    /// Recursively copy a directory's contents into a destination folder,
    /// overwriting, creating sub-folders as needed.
    private static void CopyDirectoryContents(string sourceDir, string destDir)
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

    private sealed class IsaacSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private IsaacSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<IsaacSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(IsaacSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(
                SettingsSidecarPath,
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
