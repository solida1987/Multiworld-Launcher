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

namespace LauncherV2.Plugins.AHatInTime;

// ═══════════════════════════════════════════════════════════════════════════════
// AHatInTimePlugin — install / launch for "A Hat in Time" (Gears for Breakfast,
// 2017) played through the Archipelago Steam Workshop mod. This is a NATIVE
// "ConnectsItself" integration in the same family as the shipped Hollow Knight /
// Stardew Valley / TUNIC plugins: the game speaks to the AP multiworld itself (no
// emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified against the apworld + setup guide)
// A Hat in Time is a STEAM-MOD native, but its delivery is MEANINGFULLY DIFFERENT
// from the Hollow Knight / Stardew / TUNIC trio, and this plugin says so plainly
// instead of pretending it is the same one-click shape. The honest ceiling is
// "automate what is possible, GUIDE the irreducible parts" — and for this game the
// irreducible parts are larger than usual. The verified facts (from
// worlds/ahit/__init__.py, worlds/ahit/Client.py, and worlds/ahit/docs/setup_en.md
// shipped in this repo):
//
//   * THE AP WORLD game string is "A Hat in Time" (verified against
//     worlds/ahit/__init__.py: `class HatInTimeWorld(World): ... game = "A Hat in
//     Time"`). GameId here = "ahit". A Hat in Time is a CORE Archipelago world (it
//     ships inside Archipelago itself — no custom_worlds drop needed to generate).
//
//   * THE MOD IS STEAM-WORKSHOP-ONLY — THERE IS NO GITHUB RELEASE ZIP. The setup
//     guide names exactly one mod: the "Archipelago" A Hat in Time Workshop item
//     (id 3026842601, https://steamcommunity.com/sharedfiles/filedetails/?id=
//     3026842601). A launcher CANNOT silently subscribe a user to a Workshop item
//     (that requires the Steam client / a logged-in Steam session and user
//     consent), and there is no downloadable release artifact to stage. So unlike
//     the HK/Stardew/TUNIC plugins, this plugin's "Install" does NOT download or
//     copy any mod files — it would be dishonest theatre to fake that. Instead it
//     OPENS the Workshop page and the setup guide and walks the user through
//     subscribing. This is the honest, brief-sanctioned "guide it" path for a
//     Workshop-only mod.
//
//   * IT ALSO REQUIRES A STEAM BETA BRANCH. The mod runs on the game's `tcplink`
//     beta branch (Steam → A Hat in Time → Properties → Betas → select `tcplink`),
//     NOT the default branch. This is a manual Steam step the launcher cannot
//     perform; it is called out explicitly in the guided steps.
//
//   * HOW IT CONNECTS — VIA THE BUNDLED "Archipelago AHIT Client", NOT AN IN-GAME
//     SERVER MENU. This is the biggest difference from HK/Stardew/TUNIC. The AP
//     world ships its OWN text client (worlds/ahit/__init__.py registers
//     `Component("A Hat in Time Client", "AHITClient", type=CLIENT)`; Client.py
//     runs a local websocket proxy on localhost:11311). The documented flow is:
//     run the "Archipelago AHIT Client" from the Archipelago Launcher, connect IT
//     to the AP server, then create a NEW save in the game — the game auto-connects
//     to the local client, which relays to the server. There is NO host/port/
//     password field inside the game and NO config file this launcher can
//     pre-write, so this plugin does NOT attempt a connection prefill (verified —
//     see the setup guide). The settings panel + post-install note state exactly
//     this and surface the session's server/slot for the user to type into the
//     AHIT Client. (Death Link and chat are toggled in-game via the developer
//     console: `ap_deathlink`, `ap_say` — informational only.)
//
//   * From the LAUNCHER's standpoint, ConnectsItself = true is still the correct
//     contract: the AP server allows one connection per slot, and the AHIT Client
//     owns that slot while it is running — so the launcher must NOT hold its own
//     ApClient on the same slot (it would kick / be kicked endlessly). The
//     launcher launches the game (and points the user at the AHIT Client) and
//     suppresses its own auto-reconnect for this slot.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam A Hat in Time install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\HatinTime via appmanifest_253230.acf. A manual install-dir
//      OVERRIDE (settings folder picker) is also supported and takes precedence; it
//      is validated (must contain the HatinTimeGame folder / a HatinTimeGame.exe)
//      and persisted in this plugin's OWN sidecar (Games/ROMs/ahit/
//      ahit_launcher.json) — Core/SettingsStore is NOT modified.
//   2. INSTALL/UPDATE = GUIDED ONLY (no download). The Workshop mod cannot be
//      auto-subscribed, so this opens the Workshop page + the official setup guide
//      and reports clear, numbered steps (back up saves, switch to the `tcplink`
//      beta branch, subscribe to the Workshop mod, enable the developer console).
//      Never a fake one-click.
//   3. LAUNCH = run HatinTimeGame.exe from the detected/override install (searched
//      under the HatinTimeGame\Binaries tree); if the exe cannot be found but Steam
//      is present, fall back to steam://rungameid/253230. ConnectsItself = true
//      (the AHIT Client owns the slot). SupportsStandalone = true (plain A Hat in
//      Time runs fine without AP). No connection prefill (entered in the AHIT
//      Client), stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", HK/TUNIC-style) ────────────
//   * "Installed" is judged by the presence of the Workshop mod content folder for
//     this game (steamapps\workshop\content\253230\3026842601\, non-empty) under a
//     detected Steam library — the best honest signal that the user subscribed to
//     the mod, since there is no file we place ourselves. If the game install is
//     not detected at all, the tile simply reads "not installed". (A user who set
//     a manual override but whose Workshop content lives in the default library is
//     still detected, because all Steam libraries are scanned for the content
//     folder.)
//   * Steam library / VDF / ACF parsing is defensive: hand-written tolerant scans
//     that pull quoted values; any failure degrades to "not found" rather than
//     throwing.
//   * No plaintext AP password is ever written by this plugin (the connection is
//     entered in the AHIT Client), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class AHatInTimePlugin : IGamePlugin
{
    // ── Constants — Steam Workshop mod (Workshop-only, verified 2026-06-14) ────

    /// A Hat in Time Steam application id.
    private const string SteamAppId = "253230";

    /// The "Archipelago" A Hat in Time Steam Workshop item id (the AP mod). There
    /// is NO GitHub release for this mod — it is delivered only via the Workshop.
    private const string WorkshopItemId = "3026842601";

    private const string WorkshopModUrl =
        $"https://steamcommunity.com/sharedfiles/filedetails/?id={WorkshopItemId}";

    /// The Steam store page (for users who do not yet own the game).
    private const string StorePageUrl =
        $"https://store.steampowered.com/app/{SteamAppId}/A_Hat_in_Time/";

    private const string SetupGuideUrl  = "https://archipelago.gg/tutorial/A%20Hat%20in%20Time/setup/en";
    private const string GameInfoUrl    = "https://archipelago.gg/games/A%20Hat%20in%20Time/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    /// Optional in-game map tracker (PopTracker pack) — informational link only.
    private const string PopTrackerPackUrl = "https://github.com/Mysteryem/ahit-poptracker/releases";

    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The standard Steam install sub-folder name for A Hat in Time.
    private const string SteamCommonFolderName = "HatinTime";

    /// The game-content sub-folder (where SaveData lives) inside the install.
    private const string GameContentFolderName = "HatinTimeGame";

    /// The base-game executable name (lives under HatinTimeGame\Binaries\...).
    private const string GameExeName = "HatinTimeGame.exe";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "ahit";
    public string DisplayName => "A Hat in Time";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/ahit/__init__.py
    /// (`class HatInTimeWorld(World): ... game = "A Hat in Time"`).
    public string ApWorldName => "A Hat in Time";

    /// The shipped launcher asset for this game is "a_hat_in_time.png" (present in
    /// Assets/). We point at the real file so the tile icon renders.
    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "a_hat_in_time.png");

    public string ThemeAccentColor => "#9A4FA0";   // Hat Kid's purple
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "A Hat in Time, the cute 3D platformer by Gears for Breakfast, played " +
        "through the Archipelago Steam Workshop mod — a native in-game client, so " +
        "the game connects to the multiworld itself with no emulator and no bridge " +
        "DLL. Time Pieces, Relics, Yarn, Badges and most other items are shuffled " +
        "across the multiworld, and chapter costs are randomized. You bring your " +
        "own copy of A Hat in Time (owned on Steam); the Archipelago support is a " +
        "Steam Workshop mod that runs on the game's \"tcplink\" beta branch. The " +
        "launcher detects your Steam install and guides the Workshop subscription " +
        "and beta-branch switch (Steam will not let an app subscribe you " +
        "automatically). You connect by running the \"Archipelago AHIT Client\" " +
        "from the Archipelago Launcher and creating a new save.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    // The Workshop mod has no GitHub release we can poll for a version number, and
    // Steam updates Workshop content itself. So we report a coarse "installed" /
    // null rather than inventing a version string.
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion => null;   // no pollable version source

    /// "Installed" means the A Hat in Time Workshop mod content folder is present
    /// (and non-empty) in a detected Steam library. There is no file WE place, so
    /// this is the most honest "the user subscribed to the mod" signal available.
    public bool IsInstalled => FindWorkshopModContentDir() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps any working files. Nothing is installed here (the
    /// mod is a Workshop subscription managed by Steam); exposed as GameDirectory
    /// for the IGamePlugin contract — we surface the detected GAME dir when known.
    public string GameDirectory
    {
        get => ResolveGameDir() ?? Path.Combine(AppContext.BaseDirectory, "Games", "AHatInTime");
        set { if (!string.IsNullOrWhiteSpace(value)) SaveOverrideDir(value); }
    }

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Hollow Knight /
    /// Stardew / TUNIC). Per the brief, lives under Games/ROMs/ahit/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "ahit_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Archipelago AHIT Client + the Workshop mod report checks/items/goal to
    // the AP server themselves — the launcher relays nothing. These exist for
    // interface compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        // No pollable version source for a Workshop mod. Report a coarse state.
        InstalledVersion = IsInstalled ? "installed" : null;
    }

    // ── Lifecycle — InstallOrUpdate (GUIDED — no download) ────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        await Task.CompletedTask;

        // 0. We need the game to be present for the mod to mean anything.
        progress.Report((10, "Locating your A Hat in Time installation..."));
        string? gameDir = ResolveGameDir();

        // 1. The mod is Workshop-only — we CANNOT subscribe the user from here.
        //    Open the Workshop page + setup guide and explain the manual steps.
        progress.Report((40, "Opening the Steam Workshop mod page..."));
        OpenUrl(WorkshopModUrl);
        progress.Report((60, "Opening the A Hat in Time setup guide..."));
        OpenUrl(SetupGuideUrl);

        bool modPresent = FindWorkshopModContentDir() != null;
        InstalledVersion = modPresent ? "installed" : null;

        string head = gameDir == null
            ? "A Hat in Time was not detected on Steam — install the game first " +
              "(the Steam store page was opened). "
            : (modPresent
                ? "The Archipelago Workshop mod is already subscribed. "
                : "The Steam Workshop mod page was opened — subscribe to it there. ");

        if (gameDir == null) OpenUrl(StorePageUrl);

        progress.Report((100,
            head +
            "This mod is a STEAM WORKSHOP subscription, so it cannot be installed " +
            "by this launcher automatically. To finish (see the opened setup guide): " +
            "1) BACK UP your save files; 2) in Steam, set A Hat in Time to the " +
            "\"tcplink\" beta branch (Properties → Betas); 3) subscribe to the " +
            "\"Archipelago\" Workshop mod; 4) launch the game and enable the developer " +
            "console in Game Settings. To play, run the \"Archipelago AHIT Client\" " +
            "from the Archipelago Launcher, connect it to your server, then create a " +
            "NEW save — the game connects through that client. (This launcher cannot " +
            "pre-fill the connection.)"));
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
        // HONEST: the AP server connection for A Hat in Time is entered in the
        // separate "Archipelago AHIT Client" (run from the Archipelago Launcher),
        // NOT in an in-game menu and NOT via any config file this launcher can
        // pre-write (verified — see header / setup guide). The game auto-connects
        // to that local client when a new save is created. So launching from this
        // tile just starts the game; the user connects the AHIT Client with the
        // session credentials (the settings panel + note surface those values).
        //
        // ConnectsItself = true: the AHIT Client owns the slot connection, so the
        // launcher must NOT hold its own ApClient on this slot.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartGame();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) A Hat in Time runs perfectly well.
    public bool SupportsStandalone => true;

    /// The Archipelago AHIT Client owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started the game from here. Kill what we launched.
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No plaintext AP password is ever written to disk by this plugin (the
        // connection is entered in the AHIT Client), so there is nothing to scrub.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The Archipelago AHIT Client + Workshop mod receive items from the AP
        // server directly; there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod / AHIT Client render their own AP status; no launcher HUD.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid A Hat in Time folder contains
    /// the HatinTimeGame sub-folder (or a HatinTimeGame.exe somewhere beneath it).
    /// Return null when acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your A Hat in Time install folder.";

        if (LooksLikeGameDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeGameDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like an A Hat in Time installation. Pick the folder " +
               "that contains the \"HatinTimeGame\" sub-folder (for Steam this is usually " +
               @"...\steamapps\common\HatinTime).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Unverified-offline honesty header ─────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "A Hat in Time is your own game (Steam) with the Archipelago mod " +
                   "added on top. The mod is a STEAM WORKSHOP item, so this launcher " +
                   "cannot install it for you — it opens the Workshop page and the setup " +
                   "guide and walks you through subscribing. The mod also requires the " +
                   "game's \"tcplink\" beta branch (a manual Steam step). You connect by " +
                   "running the \"Archipelago AHIT Client\" from the Archipelago Launcher " +
                   "and creating a new save; there is no in-game server menu and no " +
                   "connection file to pre-fill. These external steps are not verified by " +
                   "this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "A HAT IN TIME INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "A Hat in Time not detected. Pick your install folder below, or install " +
              "the game via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // mod (Workshop) status line
        string? modDir = FindWorkshopModContentDir();
        panel.Children.Add(new TextBlock
        {
            Text = modDir != null
                    ? "Archipelago Workshop mod subscribed: " + modDir
                    : "Archipelago Workshop mod not detected yet (subscribe to it on the " +
                      "Workshop — use Install on the Play tab to open the page).",
            FontSize = 11, Foreground = modDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your A Hat in Time install folder (the one containing the " +
                          "\"HatinTimeGame\" sub-folder). Detected from Steam automatically; " +
                          "set it here to override (non-standard Steam library).",
        };
        var dirBtn = new Button
        {
            Content = "Select folder...", Width = 120, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your A Hat in Time install folder (contains HatinTimeGame)",
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
                    MessageBox.Show(bad, "Not an A Hat in Time folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeGameDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeGameDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 253230). Use this " +
                   "picker for a non-standard Steam library.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (this session) ────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (via the Archipelago AHIT Client)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Run the \"Archipelago AHIT Client\" from the Archipelago Launcher and " +
                   "connect it to your server (host:port, e.g. archipelago.gg:38281), your " +
                   "slot name, and password (if any). Then start the game and create a NEW " +
                   "save — the game auto-connects to the running client. In-game, the " +
                   "developer console (tilde/TAB) accepts \"ap_say <message>\" for chat/" +
                   "hints and \"ap_deathlink\" to toggle Death Link. This launcher does not " +
                   "pre-fill the connection.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own A Hat in Time (on Steam). Install it if you have not. Use the picker " +
                "above if it was not detected.",
            "2. BACK UP your save files: copy everything in " +
                "...\\steamapps\\common\\HatinTime\\HatinTimeGame\\SaveData\\ somewhere safe. " +
                "Changing the game version can break existing saves.",
            "3. In Steam, right-click A Hat in Time → Properties → Betas, and select the " +
                "\"tcplink\" beta branch. Let it download.",
            "4. Subscribe to the \"Archipelago\" Steam Workshop mod (the Install button on " +
                "the Play tab opens its page). Steam will download it on next launch.",
            "5. Launch the game; in Game Settings, make sure \"Enable Developer Console\" is " +
                "checked. If a new save will not connect, open the in-game Mods menu (rocket " +
                "icon) and re-enable the Archipelago mod.",
            "6. To play: run the \"Archipelago AHIT Client\" from the Archipelago Launcher, " +
                "connect it to your server, then create a NEW save. The game connects through " +
                "the client automatically.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
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
            ("Archipelago Workshop mod (Steam) ↗", WorkshopModUrl),
            ("A Hat in Time Setup Guide ↗",        SetupGuideUrl),
            ("A Hat in Time Guide (AP) ↗",         GameInfoUrl),
            ("Map Tracker (PopTracker pack) ↗",    PopTrackerPackUrl),
            ("A Hat in Time on Steam ↗",           StorePageUrl),
            ("Archipelago Official ↗",             ArchipelagoSite),
        })
        {
            var btn = new Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => OpenUrl(u);
            panel.Children.Add(btn);
        }
        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // The Workshop mod has no GitHub releases feed to parse. Rather than invent
        // news, surface a single static pointer to the setup guide.
        await Task.CompletedTask;
        return new[]
        {
            new NewsItem(
                Title:   "A Hat in Time — Archipelago via Steam Workshop",
                Body:    "A Hat in Time uses a Steam Workshop mod and the game's \"tcplink\" " +
                         "beta branch. Subscribe to the Archipelago Workshop mod, switch to " +
                         "the tcplink branch, then connect with the Archipelago AHIT Client. " +
                         "See the setup guide for full steps.",
                Version: "",
                Date:    DateTimeOffset.MinValue,
                Url:     SetupGuideUrl)
        };
    }

    // ── Private helpers — Steam / game detection ──────────────────────────────

    /// The A Hat in Time game dir to use: the override (if set and valid) wins,
    /// else the Steam-detected install. Null when nothing is found.
    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeGameDir(ov))
            return ov;

        try { return DetectSteamGameDir(); }
        catch { return null; }
    }

    /// A folder "looks like" A Hat in Time if it contains the HatinTimeGame
    /// sub-folder, or a HatinTimeGame.exe somewhere beneath it.
    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (Directory.Exists(Path.Combine(dir, GameContentFolderName))) return true;
            return FindGameExe(dir) != null;
        }
        catch { return false; }
    }

    /// Detect the Steam A Hat in Time install: read the Steam root from the
    /// registry, gather all library roots from libraryfolders.vdf, and find the one
    /// whose appmanifest_253230.acf exists → steamapps\common\HatinTime.
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
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{SteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    // Prefer the installdir named in the manifest; fall back to the
                    // conventional "HatinTime" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeGameDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeGameDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Find the subscribed Workshop mod content folder for this game:
    /// <lib>\steamapps\workshop\content\253230\3026842601\ that exists AND contains
    /// at least one file/sub-folder. Scans EVERY Steam library (Workshop content
    /// may live in a different library than the game). Returns the path or null.
    private string? FindWorkshopModContentDir()
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
                        string content = Path.Combine(lib, "steamapps", "workshop",
                            "content", SteamAppId, WorkshopItemId);
                        if (Directory.Exists(content) &&
                            Directory.EnumerateFileSystemEntries(content).Any())
                            return content;
                    }
                    catch { /* try the next library */ }
                }
            }
        }
        catch { /* registry/file access failed */ }
        return null;
    }

    /// Find HatinTimeGame.exe beneath a game dir (it lives under
    /// HatinTimeGame\Binaries\Win64\ or \Win32\; search defensively). Returns the
    /// exe path or null.
    private static string? FindGameExe(string gameDir)
    {
        try
        {
            // Fast paths first (avoid a full recursive walk when possible).
            foreach (string rel in new[]
            {
                Path.Combine(GameContentFolderName, "Binaries", "Win64", GameExeName),
                Path.Combine(GameContentFolderName, "Binaries", "Win32", GameExeName),
                Path.Combine(GameContentFolderName, "Binaries", GameExeName),
                GameExeName,
            })
            {
                string p = Path.Combine(gameDir, rel);
                if (File.Exists(p)) return p;
            }

            // Defensive recursive fallback (bounded to the game-content folder when
            // present, else the whole dir).
            string searchRoot = Directory.Exists(Path.Combine(gameDir, GameContentFolderName))
                ? Path.Combine(gameDir, GameContentFolderName)
                : gameDir;
            foreach (string exe in Directory.EnumerateFiles(searchRoot, GameExeName, SearchOption.AllDirectories))
                return exe;
        }
        catch { /* permission / vanished */ }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath + HKLM Valve InstallPath). All tried;
    /// duplicates are harmless.
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

    /// Read the "installdir" value from an appmanifest_*.acf (same quoted-pair
    /// format as VDF). Returns null if absent.
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

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start A Hat in Time: prefer HatinTimeGame.exe in the detected/override
    /// install; if it cannot be found but Steam is present, fall back to the
    /// steam:// URL. Surfaces a clear message rather than failing opaquely.
    private void StartGame()
    {
        string? gameDir = ResolveGameDir();
        string? exe     = gameDir != null ? FindGameExe(gameDir) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start A Hat in Time.");

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

        // Fall back to Steam if we at least know Steam is installed. (Steam will
        // launch whatever branch / mod the user configured — including tcplink.)
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
            "Could not find HatinTimeGame.exe. Open this game's Settings and pick your " +
            "A Hat in Time install folder, or install the game via Steam.",
            GameExeName);
    }

    /// Open a URL / Steam deep link in the default handler; swallow failures.
    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* non-fatal */ }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side setting (the install-dir override) in its
    // OWN JSON file so it stays a single self-contained source file and does not
    // modify Core/SettingsStore. BOM-less UTF-8, read-modify-write (same approach
    // as Hollow Knight / Stardew / TUNIC).

    private sealed class AhitSettings
    {
        public string? InstallOverride { get; set; }
    }

    private AhitSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<AhitSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(AhitSettings s)
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
}
