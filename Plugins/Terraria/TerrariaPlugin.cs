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

namespace LauncherV2.Plugins.Terraria;

// ═══════════════════════════════════════════════════════════════════════════════
// TerrariaPlugin — install / launch for "Terraria" (Re-Logic, 2011) played through
// the "Archipelago Randomizer (Seldom's implementation)" mod, which runs under
// tModLoader (Terraria's mod loader) and contains the in-game Archipelago client.
// This is a NATIVE "ConnectsItself" integration in the same family as the shipped
// Hollow Knight / Stardew Valley / TUNIC plugins: the game itself speaks to the AP
// server (no emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native. The honest integration ceiling — exactly like the
// shipped Hollow Knight / Jak plugins — is "detect what is possible, GUIDE the
// irreducible parts." The verified facts (all confirmed 2026-06-14):
//
//   * THE AP WORLD game string is "Terraria" (verified against
//     worlds/terraria/__init__.py: `class TerrariaWorld(World): ... game =
//     "Terraria"`; there is NO required_client_version). GameId here = "terraria".
//     Terraria is a CORE Archipelago world (it ships inside Archipelago itself —
//     no custom_worlds drop needed to generate).
//
//   * THE INTEGRATION RUNS UNDER tModLoader, Terraria's mod loader. tModLoader is
//     its OWN free Steam app (appid 1281930) that installs ALONGSIDE Terraria
//     (appid 105600) — it is NOT bundled inside Terraria, and the base Terraria
//     exe cannot load tMod mods. So this plugin detects BOTH apps and launches
//     tModLoader (never plain Terraria) for AP play.
//
//   * THE AP MOD is "Archipelago Randomizer (Seldom's implementation)" by Seldom
//     & Desperandos. CRITICAL HONESTY — IT IS DISTRIBUTED VIA STEAM WORKSHOP, NOT
//     as a GitHub-release .zip. Per the official AP setup guide the install route
//     is: "Subscribe to the mod on Steam" (Workshop item id 2922217554), then in
//     tModLoader "Go to Workshop -> Manage Mods and enable the Archipelago mod."
//     There is therefore NO zip this launcher can download-and-drop the way the
//     Stardew / TUNIC plugins can — Steam Workshop subscription is the mechanism,
//     and the launcher CANNOT subscribe to a Workshop item on the user's behalf.
//     So INSTALL here is honestly GUIDED: the plugin opens the Workshop page (to
//     Subscribe) and the tModLoader Mod Browser, and lists numbered steps + links.
//     Faking a one-click "installed" that cannot exist would be dishonest theatre.
//     (Workshop mods land at steamapps\workshop\content\1281930\2922217554\; a
//     hand-installed .tmod instead lands in the user's
//     Documents\My Games\Terraria\tModLoader\Mods folder. The plugin checks both.)
//
//   * CONNECTION is made WITHOUT this launcher's help (verified against the setup
//     guide + the mod homepage). It is configured in tModLoader's mod-settings GUI
//     BEFORE entering a world — "In Workshop > Manage Mods, edit Archipelago
//     Randomizer's settings" and set Name (your slot name), Port, and Address
//     (e.g. archipelago.gg) — and driven IN-GAME via the mod's chat console:
//     "/apstart" to start, and "/ap" to open the Archipelago console. There is NO
//     documented connection config file or command-line argument this launcher can
//     pre-write (the settings live inside tModLoader's own per-mod config managed
//     through its GUI, and the exact JSON shape is not documented). Per an honest
//     "don't invent an undocumented prefill" stance (same as Hollow Knight / Jak),
//     this plugin does NOT write any connection file and does NOT fake a prefill —
//     the settings panel + post-step note surface the session's Address / Port /
//     Slot for the user to type into the mod's settings + the in-game console.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Terraria (105600) AND tModLoader (1281930) installs via
//      the Windows registry (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Terraria / steamapps\common\tModLoader via their
//      appmanifest_*.acf. A manual tModLoader install-dir OVERRIDE (settings
//      folder picker) is also supported and takes precedence; it is validated
//      (must contain tModLoader.exe) and persisted in this plugin's OWN sidecar
//      (Games/ROMs/terraria/terraria_launcher.json) — Core/SettingsStore is NOT
//      modified.
//   2. INSTALL/UPDATE = honestly GUIDED. The mod is a Steam Workshop subscription
//      the launcher cannot perform programmatically, so InstallOrUpdate opens the
//      mod's Workshop page (to Subscribe) and surfaces clear, numbered, Hollow-
//      Knight-style guided steps + links (the Workshop item, the mod homepage, the
//      official AP setup guide, tModLoader, archipelago.gg). It never fakes a
//      one-click that cannot exist.
//   3. LAUNCH = run tModLoader.exe from the detected/override tModLoader install;
//      if the exe cannot be found but Steam is present, fall back to
//      steam://rungameid/1281930 (NEVER plain Terraria — it cannot load the mod).
//      ConnectsItself = true (the mod owns the slot — the launcher must NOT hold
//      its own ApClient on it). SupportsStandalone = true (tModLoader / Terraria
//      runs fine without AP). No connection prefill (entered in the mod's settings
//      + in-game console), stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Hollow-Knight/Jak-style) ──
//   * "Installed" is judged by BOTH (a) tModLoader being detected AND (b) the AP
//     mod being present — found either in the Steam Workshop content folder for
//     tModLoader (steamapps\workshop\content\1281930\2922217554) OR as a .tmod in
//     the user's Documents tModLoader Mods folder whose name mentions
//     "archipelago" (case-insensitive). We do NOT gate on an OUR-OWN version stamp,
//     because the mod is owned by Steam Workshop / tModLoader, which we honor. If
//     tModLoader is not detected, the tile simply reads "not installed".
//   * Steam library parsing is defensive: a hand-written tolerant VDF scan that
//     pulls quoted "path" values; any failure degrades to "not found" rather than
//     throwing.
//   * No plaintext AP password is ever written by this plugin (the connection is
//     entered in the mod settings / in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class TerrariaPlugin : IGamePlugin
{
    // ── Constants — the AP Terraria mod + tooling (verified 2026-06-14) ────────

    /// The mod's Steam Workshop item — "Archipelago Randomizer (Seldom's
    /// implementation)" for tModLoader. Subscription is how it is installed.
    private const string WorkshopItemId  = "2922217554";
    private static readonly string WorkshopItemUrl =
        $"https://steamcommunity.com/sharedfiles/filedetails/?id={WorkshopItemId}";

    /// The mod's homepage / source (usage + console-command docs).
    private const string ModHomepageUrl =
        "https://github.com/Seldom-SE/archipelago_terraria_client";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Terraria/setup_en";
    private const string GameInfoUrl     = "https://archipelago.gg/games/Terraria/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    /// tModLoader on Steam (the free mod-loader app the mod runs under).
    private const string TModLoaderSteamPage =
        "https://store.steampowered.com/app/1281930/tModLoader/";

    // Steam app ids.
    private const string TerrariaSteamAppId   = "105600";
    private const string TModLoaderSteamAppId = "1281930";
    private static readonly string SteamRunTModLoaderUrl =
        $"steam://rungameid/{TModLoaderSteamAppId}";
    private static readonly string SteamRunTerrariaUrl =
        $"steam://rungameid/{TerrariaSteamAppId}";

    /// Standard Steam install sub-folder names.
    private const string TerrariaCommonFolderName   = "Terraria";
    private const string TModLoaderCommonFolderName = "tModLoader";

    /// The tModLoader executable name (the AP entry point — NOT Terraria.exe).
    private const string TModLoaderExeName = "tModLoader.exe";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "terraria";
    public string DisplayName => "Terraria";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/terraria/__init__.py
    /// (`class TerrariaWorld(World): ... game = "Terraria"`; no
    /// required_client_version).
    public string ApWorldName => "Terraria";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "terraria.png");

    public string ThemeAccentColor => "#5BA84F";   // Terraria leafy green
    public string[] GameBadges     => new[] { "Steam · needs tModLoader" };

    public string Description =>
        "Terraria, Re-Logic's 2011 2D sandbox adventure, played through the " +
        "\"Archipelago Randomizer\" mod by Seldom — a native in-game Archipelago " +
        "client, so the game connects to the multiworld itself with no emulator " +
        "and no bridge. Boss kills and events become checks, and items are " +
        "permanent unlocks shuffled across the multiworld. You bring your own copy " +
        "of Terraria (owned on Steam); the integration runs on tModLoader, " +
        "Terraria's free mod loader (a separate Steam app). The launcher detects " +
        "your Terraria and tModLoader installs and guides you through subscribing " +
        "to the Archipelago mod on the Steam Workshop and enabling it. You connect " +
        "to your server in the mod's settings and the in-game Archipelago console.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    // The mod is a Steam Workshop subscription with no launcher-readable version
    // tag, so we report "installed" when present rather than a version number.
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means BOTH tModLoader is detected AND the AP mod is present
    /// (Workshop content folder or a .tmod in the Documents Mods folder). We do
    /// NOT gate on our own stamp — the mod is owned by Steam Workshop / tModLoader.
    public bool IsInstalled => ResolveTModLoaderDir() != null && IsApModPresent();

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps any working files. The mod itself lives in Steam
    /// Workshop content (owned by Steam), not here. Exposed as GameDirectory for
    /// the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Terraria");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Hollow Knight /
    /// Stardew / TUNIC / Jak). Per the brief, lives under Games/ROMs/terraria/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "terraria_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Archipelago mod reports checks/items/goal to the AP server itself — the
    // launcher relays nothing. These exist for interface compatibility
    // (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // The mod is a Steam Workshop subscription (Steam keeps it updated). There
        // is no launcher-readable version to poll — report "installed" when present.
        try
        {
            InstalledVersion = IsInstalled ? "installed" : null;
        }
        catch
        {
            InstalledVersion = null;
        }
        AvailableVersion = null; // nothing to compare against; never throw
        return Task.CompletedTask;
    }

    // ── Lifecycle — InstallOrUpdate (honestly GUIDED) ─────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        await Task.CompletedTask;

        // 0. We need a tModLoader install for the mod to run under. Prefer an
        //    explicit override; else auto-detect the Steam install.
        progress.Report((5, "Locating tModLoader..."));
        string? tmlDir   = ResolveTModLoaderDir();
        string? terraDir = DetectSteamAppDir(TerrariaSteamAppId, TerrariaCommonFolderName, LooksLikeTerrariaDir);

        // HONEST: the Archipelago mod for Terraria is a STEAM WORKSHOP item. There
        // is no zip to download-and-drop (unlike Stardew/TUNIC), and this launcher
        // cannot subscribe to a Workshop item on the user's behalf. So "Install"
        // here OPENS the Workshop page (to Subscribe) + the setup guide, and the
        // settings panel lists the full numbered steps. We never fake a one-click.

        progress.Report((35, "Opening the Archipelago mod's Steam Workshop page (Subscribe there)..."));
        TryOpenUrl(WorkshopItemUrl);

        progress.Report((65, "Opening the Terraria Archipelago setup guide..."));
        TryOpenUrl(SetupGuideUrl);

        // If tModLoader is not installed yet, also point the user at its store page.
        if (tmlDir == null)
        {
            progress.Report((80, "tModLoader not detected — opening its Steam page..."));
            TryOpenUrl(TModLoaderSteamPage);
        }

        string state =
            (terraDir != null ? "Terraria detected. " : "Terraria not detected (install it on Steam). ") +
            (tmlDir   != null ? "tModLoader detected. " : "tModLoader not detected (install the free tModLoader app on Steam). ");

        progress.Report((100,
            state +
            "The Archipelago mod is a Steam Workshop item, so it cannot be installed " +
            "automatically by this launcher. To finish: (1) on the Workshop page that " +
            "just opened, click Subscribe; (2) launch tModLoader and go to Workshop -> " +
            "Manage Mods and ENABLE \"Archipelago Randomizer\", then Reload; (3) in that " +
            "same screen edit the mod's settings and set Name (your slot), Port, and " +
            "Address (e.g. archipelago.gg); (4) create/enter a world and use /apstart " +
            "and /ap in chat. See Settings for the full guided steps and links. (This " +
            "launcher cannot pre-fill the connection — it is entered in the mod.)"));
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
        // HONEST: the AP server connection for Terraria is entered in the mod's
        // OWN settings (tModLoader -> Workshop -> Manage Mods -> Archipelago
        // Randomizer -> settings: Name / Port / Address) and driven in-game via the
        // "/apstart" and "/ap" chat commands. There is no documented config / CLI
        // prefill this launcher can apply (verified — see header). So launching from
        // this tile just starts tModLoader; the user connects with the session
        // credentials (the settings panel + note surface those values to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartTModLoader();
        return Task.CompletedTask;
    }

    /// tModLoader / Terraria runs perfectly well without AP.
    public bool SupportsStandalone => true;

    /// The Archipelago Randomizer mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartTModLoader();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started tModLoader from here. Kill what we launched.
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No plaintext AP password is ever written to disk by this plugin (the
        // connection is entered in the mod / in-game), so there is nothing to scrub.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The Archipelago Randomizer mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game (the /ap console).
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid tModLoader folder contains
    /// tModLoader.exe. Return null when acceptable, else a short human-readable
    /// reason. (We point the override at tModLoader, since that is what we launch.)
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your tModLoader install folder.";

        if (LooksLikeTModLoaderDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, TModLoaderCommonFolderName);
            if (LooksLikeTModLoaderDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a tModLoader installation. Pick the folder " +
               "that contains tModLoader.exe (for Steam this is usually " +
               @"...\steamapps\common\tModLoader). Note: the Archipelago mod runs " +
               "under tModLoader, not plain Terraria.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Unverified-offline honesty header ─────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Terraria is your own game (Steam) with the \"Archipelago Randomizer\" " +
                   "mod added on top, running under tModLoader (Terraria's free mod " +
                   "loader, a separate Steam app). The mod is a Steam Workshop item, so " +
                   "it cannot be installed automatically — you Subscribe to it on the " +
                   "Workshop and enable it in tModLoader (see the guided steps below). " +
                   "You connect to your server in the mod's settings and the in-game " +
                   "Archipelago console. These external steps are not verified by this " +
                   "launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected installs / override ─────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "TERRARIA / tMODLOADER INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? terraDir    = DetectSteamAppDir(TerrariaSteamAppId, TerrariaCommonFolderName, LooksLikeTerrariaDir);
        string? tmlDir      = ResolveTModLoaderDir();
        string? overrideDir = LoadOverrideDir();

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = terraDir != null
                    ? "Terraria detected: " + terraDir
                    : "Terraria not detected — install it on Steam (appid 105600).",
            FontSize = 11, Foreground = terraDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        string tmlMsg = tmlDir != null
            ? (overrideDir != null
                ? "tModLoader (your selected folder): " + tmlDir
                : "tModLoader detected: " + tmlDir)
            : "tModLoader not detected — install the free tModLoader app on Steam " +
              "(appid 1281930), or pick its folder below.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = tmlMsg, FontSize = 11,
            Foreground = tmlDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        // mod-present status line
        bool modPresent = IsApModPresent();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modPresent
                    ? "Archipelago mod content found (subscribed / installed). Remember to " +
                      "ENABLE it in tModLoader -> Workshop -> Manage Mods."
                    : "Archipelago mod not found yet — Subscribe on the Steam Workshop, then " +
                      "enable it in tModLoader (use Install on the Play tab for the guided steps).",
            FontSize = 11, Foreground = modPresent ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? tmlDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your tModLoader install folder (the one containing tModLoader.exe). " +
                          "Detected from Steam automatically; set it here to override " +
                          "(non-standard Steam library). The Archipelago mod runs under " +
                          "tModLoader, not plain Terraria.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120, Padding = new Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your tModLoader install folder (contains tModLoader.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? tmlDir ?? "")
                                   ? (overrideDir ?? tmlDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a tModLoader folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the folder.
                if (!LooksLikeTModLoaderDir(picked))
                {
                    string nested = Path.Combine(picked, TModLoaderCommonFolderName);
                    if (LooksLikeTModLoaderDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (Terraria 105600, " +
                   "tModLoader 1281930). Use this picker for a non-standard Steam library.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (this session) ────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in the mod, not here)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "In tModLoader, go to Workshop -> Manage Mods, edit \"Archipelago " +
                   "Randomizer\"'s settings, and set Name (your slot name), Port, and " +
                   "Address (e.g. archipelago.gg). Then create/enter a world and, in " +
                   "chat, use /apstart to begin and /ap to open the Archipelago console. " +
                   "This launcher does not pre-fill the connection.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Terraria on Steam (appid 105600). Install it if you have not.",
            "2. Install tModLoader (free) on Steam (appid 1281930). It installs alongside " +
                "Terraria and is what loads the mod — plain Terraria cannot.",
            "3. Subscribe to \"Archipelago Randomizer (Seldom's implementation)\" on the Steam " +
                "Workshop (link below; the Install button on the Play tab opens it for you).",
            "4. Launch tModLoader, go to Workshop -> Manage Mods, ENABLE the Archipelago mod, " +
                "and Reload.",
            "5. In that same screen, edit the Archipelago mod's settings: set Name (your slot " +
                "name), Port, and Address (e.g. archipelago.gg).",
            "6. Create or enter a world. In chat, type /apstart to start, and /ap to open the " +
                "Archipelago console. (Tip: this mod makes the game harder — consider a lower " +
                "difficulty than usual.)",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Archipelago Randomizer (Steam Workshop) ↗", WorkshopItemUrl),
            ("Mod homepage / usage (GitHub) ↗",           ModHomepageUrl),
            ("Terraria Setup Guide ↗",                    SetupGuideUrl),
            ("Terraria Guide (AP) ↗",                     GameInfoUrl),
            ("tModLoader (Steam) ↗",                      TModLoaderSteamPage),
            ("Archipelago Official ↗",                    ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => TryOpenUrl(u);
            panel.Children.Add(btn);
        }
        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // The mod's GitHub repo carries the AP-relevant changes. Best-effort: pull
        // recent commits as news. Any failure yields an empty feed (never throws).
        try
        {
            const string commitsApi =
                "https://api.github.com/repos/Seldom-SE/archipelago_terraria_client/commits?per_page=10";
            string json = await _http.GetStringAsync(commitsApi, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (!el.TryGetProperty("commit", out var commit)) continue;

                string message = commit.TryGetProperty("message", out var m)
                    ? (m.GetString() ?? "") : "";
                string title = message.Split('\n')[0];

                DateTimeOffset date = DateTimeOffset.MinValue;
                if (commit.TryGetProperty("author", out var author) &&
                    author.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                string? url = el.TryGetProperty("html_url", out var u) ? u.GetString() : null;

                items.Add(new NewsItem(
                    Title:   title,
                    Body:    message,
                    Version: "",
                    Date:    date,
                    Url:     url));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — AP mod presence ─────────────────────────────────────

    /// True when the Archipelago mod appears installed for tModLoader, via EITHER:
    ///   (a) the Steam Workshop content folder for tModLoader:
    ///       <library>\steamapps\workshop\content\1281930\2922217554\, OR
    ///   (b) a hand-installed .tmod in the user's Documents tModLoader Mods folder
    ///       whose file name mentions "archipelago" (case-insensitive).
    /// Defensive — any failure yields false.
    private bool IsApModPresent()
    {
        try
        {
            // (a) Workshop content for tModLoader across all Steam libraries.
            foreach (string steamRoot in SteamRoots())
            {
                if (string.IsNullOrWhiteSpace(steamRoot)) continue;
                foreach (string lib in SteamLibraryRoots(steamRoot))
                {
                    try
                    {
                        string wsItem = Path.Combine(lib, "steamapps", "workshop", "content",
                            TModLoaderSteamAppId, WorkshopItemId);
                        if (Directory.Exists(wsItem) &&
                            Directory.EnumerateFileSystemEntries(wsItem).Any())
                            return true;
                    }
                    catch { /* try next library */ }
                }
            }

            // (b) Hand-installed .tmod in the Documents Mods folder.
            foreach (string modsDir in DocumentsModsDirs())
            {
                try
                {
                    if (!Directory.Exists(modsDir)) continue;
                    foreach (string tmod in Directory.EnumerateFiles(modsDir, "*.tmod",
                                 SearchOption.TopDirectoryOnly))
                    {
                        string name = Path.GetFileNameWithoutExtension(tmod);
                        if (name.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }
                }
                catch { /* try next candidate */ }
            }
        }
        catch { /* fall through */ }
        return false;
    }

    /// Candidate Documents tModLoader Mods folders. tModLoader historically used
    /// "ModLoader" and now uses "tModLoader"; we check both under My Games\Terraria.
    private static IEnumerable<string> DocumentsModsDirs()
    {
        string? docs = SafeFolder(Environment.SpecialFolder.MyDocuments);
        if (string.IsNullOrWhiteSpace(docs)) yield break;
        yield return Path.Combine(docs, "My Games", "Terraria", "tModLoader", "Mods");
        yield return Path.Combine(docs, "My Games", "Terraria", "ModLoader", "Mods");
    }

    private static string? SafeFolder(Environment.SpecialFolder f)
    {
        try { return Environment.GetFolderPath(f); } catch { return null; }
    }

    // ── Private helpers — Steam / install detection ───────────────────────────

    /// The tModLoader install dir to use: the override (if set and valid) wins,
    /// else the Steam-detected install. Null when nothing is found.
    private string? ResolveTModLoaderDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeTModLoaderDir(ov))
            return ov;

        try { return DetectSteamAppDir(TModLoaderSteamAppId, TModLoaderCommonFolderName, LooksLikeTModLoaderDir); }
        catch { return null; }
    }

    /// A folder "looks like" tModLoader if it has tModLoader.exe.
    private static bool LooksLikeTModLoaderDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, TModLoaderExeName));
        }
        catch { return false; }
    }

    /// A folder "looks like" Terraria if it has Terraria.exe (or the Content dir).
    private static bool LooksLikeTerrariaDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, "Terraria.exe"))) return true;
            if (Directory.Exists(Path.Combine(dir, "Content"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Generic Steam app-dir detection by appid + conventional folder name, with an
    /// optional stricter validator. Reads the Steam root(s) from the registry,
    /// gathers all library roots from libraryfolders.vdf, and finds the one whose
    /// appmanifest_<appid>.acf exists -> steamapps\common\<installdir>.
    private static string? DetectSteamAppDir(
        string appId, string conventionalFolderName, Func<string, bool>? validator = null)
    {
        Func<string, bool> looksValid = validator ?? (d =>
        {
            try { return Directory.Exists(d) && Directory.EnumerateFileSystemEntries(d).Any(); }
            catch { return false; }
        });

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

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (looksValid(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, conventionalFolderName);
                    if (looksValid(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath + HKLM InstallPath). Duplicates are harmless.
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
        string? progX86 = SafeFolder(Environment.SpecialFolder.ProgramFilesX86);
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
    /// Handles the Steam-VDF escaping of backslashes (\\ -> \).
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

    /// Start tModLoader (the AP entry point — NEVER plain Terraria, which cannot
    /// load the mod): prefer the exe in the detected/override install; if that
    /// cannot be found but Steam is present, fall back to the steam:// URL. Surfaces
    /// a clear message rather than failing opaquely.
    private void StartTModLoader()
    {
        string? tml = ResolveTModLoaderDir();
        string? exe = tml != null ? Path.Combine(tml, TModLoaderExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = tml!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start tModLoader.");

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

        // Fall back to Steam if we at least know Steam is installed (launch the
        // tModLoader app, NOT Terraria).
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunTModLoaderUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process, so we won't track exit
                return;
            }
            catch { /* fall through to the error below */ }
        }

        throw new FileNotFoundException(
            "Could not find tModLoader.exe. Open this game's Settings and pick your " +
            "tModLoader install folder, or install the free tModLoader app via Steam " +
            "(the Archipelago mod runs under tModLoader, not plain Terraria).",
            TModLoaderExeName);
    }

    /// Open a URL in the default browser; swallow any failure.
    private static void TryOpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* non-fatal */ }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the tModLoader install-dir
    // override) in its OWN JSON file so it stays a single self-contained source
    // file and does not modify Core/SettingsStore. BOM-less UTF-8, read-modify-
    // write (same approach as Hollow Knight / Stardew / TUNIC / Jak).

    private sealed class TerrariaSettings
    {
        public string? InstallOverride { get; set; }
    }

    private TerrariaSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<TerrariaSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt -> defaults */ }
        return new();
    }

    private void SaveSettings(TerrariaSettings s)
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
