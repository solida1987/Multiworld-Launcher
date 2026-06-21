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

namespace LauncherV2.Plugins.Noita;

// ═══════════════════════════════════════════════════════════════════════════════
// NoitaPlugin — install / launch for "Noita" (Nolla Games, 2020) played through
// the NoitaArchipelago mod by DaftBrit, which contains the in-game Archipelago
// client. This is a NATIVE "ConnectsItself" integration in the same family as the
// shipped Hollow Knight / Stardew Valley / TUNIC / Jak plugins: the game itself
// speaks to the AP server (no emulator, no Lua bridge, no launcher-held ApClient
// on the slot — Noita's own Lua mod system carries the AP client).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Noita (Steam appid 881100), and Archipelago support is delivered as a Noita
// mod added on top. The honest integration ceiling — exactly like the shipped
// Hollow Knight / TUNIC plugins — is "automate what is possible, guide the
// irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "Noita" (verified against
//     worlds/noita/__init__.py: `class NoitaWorld(World): ... game = "Noita"`).
//     GameId here = "noita". Noita is a CORE Archipelago world (it ships inside
//     Archipelago itself — no custom_worlds / .apworld drop needed to generate or
//     to host). The world declares no required_client_version.
//
//   * THE MOD repo is DaftBrit/NoitaArchipelago (verified live 2026-06-14 — it is
//     the source the official AP "Noita" setup guide links to:
//     github.com/DaftBrit/NoitaArchipelago/releases/latest). The latest release
//     (1.5.0) ships a SINGLE asset, "archipelago.zip", whose contents are FLAT and
//     go DIRECTLY into a "mods/archipelago" folder inside the Noita install (the
//     guide: "Create a folder called `archipelago` ... place all files from within
//     the zip folder directly into the `archipelago` folder"). So the zip is the
//     mod's own files; there is no nested top folder to descend into. THE ZIP IS
//     SELF-SUFFICIENT — it bundles the external libraries the mod needs to talk to
//     the AP server, so unlike Hollow Knight there is no separate dependency mod
//     manager to run. This makes the launcher's drop-into-mods install a genuine,
//     complete mod install (not a partial stage) — the only thing left to the user
//     is the in-game enable + connect, which no launcher can do for them.
//
//   * NO SEPARATE EXTERNAL CLIENT. Unlike some AP games, Noita does NOT need a
//     "Noita Client" launched from the Archipelago release — the mod handles all
//     Archipelago communication itself from inside the game (verified against the
//     setup guide + the mod). So this plugin neither downloads nor launches any
//     external client; it launches the game and the mod does the rest.
//
//   * CRITICAL NOITA-SPECIFIC STEP — "UNSAFE MODS" MUST BE ENABLED IN-GAME. Noita
//     gates mods that load native/external libraries behind an "Unsafe mods"
//     toggle, and the AP mod uses such libraries to reach the server. The guide is
//     explicit: "In order to enable the mod you will first need to toggle Unsafe
//     mods from Disabled to Allowed. This is required, as some external libraries
//     are used in the mod in order to communicate with the Archipelago server."
//     This is a per-game safety toggle Noita deliberately requires the human to
//     flip — the launcher cannot (and should not) flip it. The plugin surfaces it
//     prominently in the guided steps and the post-install note.
//
//   * CONNECTION is made IN-GAME (verified against the official setup guide): after
//     the mod is in mods/archipelago and enabled (with Unsafe mods allowed), open
//     Options -> Mod Settings, expand the Archipelago dropdown, and type Server /
//     Port / Slot / Password into the in-game fields; then start a NEW run. A small
//     Archipelago logo / "Connected to Archipelago server" confirms the link. There
//     is NO command-line arg and NO config file this launcher can pre-write
//     (verified). So this plugin does NOT attempt a connection prefill — the
//     post-install note and settings panel state this and surface the session's
//     server / slot for the user to type into those fields.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Noita install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Noita via appmanifest_881100.acf. A manual install-dir
//      OVERRIDE (settings folder picker) is also supported and takes precedence;
//      it is validated (must contain noita.exe / a mods folder) and persisted in
//      this plugin's OWN sidecar (Games/ROMs/noita/noita_launcher.json) — Core/
//      SettingsStore is NOT modified.
//   2. INSTALL/UPDATE (best effort, and complete) = download the mod's
//      "archipelago.zip" from the real release and extract its (flat) contents into
//      <Noita>/mods/archipelago/. Because the zip carries the mod's own
//      dependencies, this is a full install — the plugin then presents clear,
//      numbered, Hollow-Knight-style guided steps + links (mod repo, the official
//      AP setup guide, archipelago.gg), the most important of which is enabling
//      "Unsafe mods" and the Archipelago mod in-game. Never a fake one-click for
//      the in-game parts no launcher can perform.
//   3. LAUNCH = run noita.exe from the detected/override install; if the exe cannot
//      be found but Steam is present, fall back to steam://rungameid/881100.
//      ConnectsItself = true (the mod owns the slot — the launcher must NOT hold
//      its own ApClient on it). SupportsStandalone = true (plain Noita runs fine
//      without AP). No connection prefill (entered in-game), stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Hollow-Knight/Jak-style) ──
//   * "Installed" is judged by the presence of the mod's "archipelago" folder under
//     a detected/override Noita install's mods tree (specifically a mods/archipelago
//     folder that contains a mod.xml or init.lua — case-insensitive) — NOT by an
//     OUR-OWN version stamp, because the user may instead install the mod by hand,
//     which this launcher should honor. If no Noita install is detected, the tile
//     simply reads "not installed".
//   * Steam library parsing is defensive: a hand-written tolerant VDF scan that
//     pulls quoted "path" values; any failure degrades to "Noita not found" rather
//     than throwing.
//   * No plaintext AP password is ever written by this plugin (the connection is
//     entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class NoitaPlugin : IGamePlugin
{
    // ── Constants — the NoitaArchipelago mod (real repo, verified 2026-06-14) ──
    private const string MOD_OWNER = "DaftBrit";
    private const string MOD_REPO  = "NoitaArchipelago";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Noita/setup/en";
    private const string GameInfoUrl     = "https://archipelago.gg/games/Noita/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Noita appid 881100.
    private const string NoitaSteamAppId = "881100";
    private static readonly string SteamRunUrl = $"steam://rungameid/{NoitaSteamAppId}";

    /// The standard Steam install sub-folder name for Noita.
    private const string SteamCommonFolderName = "Noita";

    /// The base-game executable name.
    private const string NoitaExeName = "noita.exe";

    /// The mod folder placed under <Noita>/mods/ (verified: the AP mod's files go
    /// directly into a folder named "archipelago").
    private const string ModFolderName = "archipelago";

    /// Pinned fallback for the mod when the GitHub API is unreachable. 1.5.0
    /// verified live 2026-06-14; the single asset is "archipelago.zip".
    private const string FallbackVersion = "1.5.0";
    private const string FallbackZipName = "archipelago.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "noita";
    public string DisplayName => "Noita";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/noita/__init__.py
    /// (`class NoitaWorld(World): ... game = "Noita"`). Noita is a core AP world.
    public string ApWorldName => "Noita";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "noita.png");

    public string ThemeAccentColor => "#9A6A2A";   // alchemical brass / sandcave
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Noita, Nolla Games' 2020 roguelite where every pixel is simulated, played " +
        "through the NoitaArchipelago mod by DaftBrit — which bundles an in-game " +
        "Archipelago client, so the game connects to the multiworld itself with no " +
        "emulator and no bridge. Spells, wands, perks, Holy Mountain shops, orbs " +
        "and more become checks shuffled across the multiworld. You bring your own " +
        "copy of Noita (owned on Steam); the Archipelago mod is added on top into " +
        "Noita's own mods folder. The launcher detects your Steam install and can " +
        "install the Archipelago mod into it for you, and guides the rest — most " +
        "importantly enabling \"Unsafe mods\" in-game, which Noita requires for the " +
        "mod to reach the server. You connect to your server from the in-game mod " +
        "settings.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the NoitaArchipelago mod folder is present in a detected/
    /// override Noita install's mods tree. (We do NOT gate on our own stamp — the
    /// user may have installed the mod by hand, which we honor.)
    public bool IsInstalled => FindInstalledModFolder() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads (the mod zip) and any working files. The
    /// actual mod is extracted INTO the Noita install's mods folder, not here.
    /// Exposed as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Noita");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Hollow Knight /
    /// Stardew / TUNIC / Jak). Per the brief, lives under Games/ROMs/noita/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "noita_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The NoitaArchipelago mod reports checks/items/goal to the AP server itself —
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
            // Best-effort: read the version we stamped next to a direct install if
            // present; otherwise report "installed" when the mod folder exists.
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
        // 0. We need a Noita install to drop the mod into. Prefer an explicit
        //    override; else auto-detect the Steam install.
        progress.Report((2, "Locating your Noita installation..."));
        string? noitaDir = ResolveNoitaDir();
        if (noitaDir == null)
            throw new InvalidOperationException(
                "Could not find a Noita installation. Open this game's Settings and " +
                "pick your Noita folder (the one containing noita.exe), or install " +
                "Noita via Steam first. The Archipelago mod is added on top of your " +
                "own copy of the game.");

        // The mod lives in <Noita>/mods/archipelago/.
        string modsDir   = Path.Combine(noitaDir, "mods");
        string apModDir  = Path.Combine(modsDir, ModFolderName);

        // 1. Resolve the latest mod release (pinned fallback).
        progress.Report((6, "Checking the latest NoitaArchipelago release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the NoitaArchipelago mod download on GitHub. Check " +
                "your internet connection, or download it manually from " +
                ModRepoUrl + "/releases — see Settings for the guided steps. The mod " +
                "repo is " + ModRepoUrl + ".");

        // 2. Download + extract the mod's (flat) files INTO <Noita>/mods/archipelago/.
        //    HONEST: the zip is self-sufficient (it bundles the external libraries
        //    the mod needs), so this is a COMPLETE mod install — the only remaining
        //    steps are in-game (enable Unsafe mods + the mod, then connect), which
        //    no launcher can perform.
        await DownloadAndExtractModAsync(zipUrl, version, apModDir, progress, ct);

        // 3. Stamp the version next to our sidecar so the tile can show it. (This is
        //    informational only — IsInstalled is judged by the mod folder's presence.)
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Installed the NoitaArchipelago mod {version} into your Noita mods\\archipelago " +
            "folder. IMPORTANT: in Noita you must enable \"Unsafe mods\" (Disabled -> Allowed) " +
            "and then enable the Archipelago mod — this is required because the mod uses " +
            "external libraries to reach the server. Then: Options -> Mod Settings -> " +
            "Archipelago, enter your Server / Port / Slot / Password, and start a NEW run. " +
            "(This launcher cannot pre-fill the connection or flip the Unsafe-mods toggle — " +
            "those are done in-game. Open Settings for the guided steps and links.)"));
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
        // HONEST: the AP server connection for Noita is entered in the IN-GAME mod
        // settings (Options -> Mod Settings -> Archipelago -> Server / Port / Slot /
        // Password), and the mod can only run once "Unsafe mods" + the mod are
        // enabled in the Mods menu. There is no command-line / config prefill this
        // launcher can apply (verified — see header). So launching from this tile
        // just starts the game; the user enables + connects in-game with the session
        // credentials (the settings panel + note surface those values to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartNoita();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Noita runs perfectly well.
    public bool SupportsStandalone => true;

    /// The NoitaArchipelago mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartNoita();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started Noita from here. Kill what we launched.
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No plaintext AP password is ever written to disk by this plugin (the
        // connection is entered in-game), so there is nothing to scrub.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The NoitaArchipelago mod receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game ("Connected to Archipelago
        // server" + the Archipelago logo).
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Noita folder contains noita.exe
    /// (the mods folder is created next to it). Return null when acceptable, else a
    /// short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Noita install folder.";

        if (LooksLikeNoitaDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeNoitaDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Noita installation. Pick the folder that " +
               "contains noita.exe (for Steam this is usually " +
               @"...\steamapps\common\Noita).";
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
            Text = "Noita is your own game (Steam) with the NoitaArchipelago mod added " +
                   "on top into Noita's own mods folder. The launcher detects your Steam " +
                   "install and can install the Archipelago mod files into it for you. " +
                   "Two things are still done in-game and cannot be automated: you must " +
                   "enable \"Unsafe mods\" and the Archipelago mod in the Mods menu " +
                   "(required for the mod to reach the server), and you connect to your " +
                   "server from the in-game mod settings. These external steps are not " +
                   "verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "NOITA INSTALL", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? noitaDir    = ResolveNoitaDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = noitaDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + noitaDir
                : "Detected Steam install: " + noitaDir)
            : "Noita not detected. Pick your install folder below, or install Noita " +
              "via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = noitaDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // mod status line
        string? modFolder = FindInstalledModFolder();
        panel.Children.Add(new TextBlock
        {
            Text = modFolder != null
                    ? "Archipelago mod found: " + modFolder
                    : "Archipelago mod not found in your mods folder yet (use Install on " +
                      "the Play tab, or install it by hand from the mod releases).",
            FontSize = 11, Foreground = modFolder != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? noitaDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Noita install folder (the one containing noita.exe). " +
                          "Detected from Steam automatically; set it here to override " +
                          "(non-standard Steam library, or another store).",
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
                Title            = "Select your Noita install folder (contains noita.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? noitaDir ?? "")
                                   ? (overrideDir ?? noitaDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Noita folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeNoitaDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeNoitaDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 881100). Use this " +
                   "picker for a non-standard Steam library or another store.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (this session) ────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "In Noita's Mods menu, first toggle \"Unsafe mods\" to Allowed, then enable " +
                   "the Archipelago mod (it should show an [x]). Then open Options -> Mod " +
                   "Settings, expand the Archipelago dropdown, and enter Server, Port, Slot, " +
                   "and Password (if any). Start a NEW run; you should see \"Connected to " +
                   "Archipelago server\". This launcher does not pre-fill the connection.",
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
            "1. Own Noita (on Steam). Install it if you have not. Use the picker above if it " +
                "was not detected.",
            "2. Install the Archipelago mod: the Install button on the Play tab downloads it " +
                "and drops its files into your Noita mods\\archipelago folder, or do it by hand " +
                "from the mod releases (link below) — create a folder \"archipelago\" inside " +
                "Noita's \"mods\" folder and unzip the files directly into it.",
            "3. Launch Noita and open the Mods menu. Toggle \"Unsafe mods\" from Disabled to " +
                "Allowed (REQUIRED — the mod uses external libraries to talk to the server), " +
                "then enable the Archipelago mod so it shows [x]. Apply / restart if asked.",
            "4. To play: open Options -> Mod Settings, expand the Archipelago dropdown, and " +
                "enter your Server, Port, Slot, and Password. Start a NEW run.",
            "5. Confirm you see \"Connected to Archipelago server\" (and the Archipelago logo) " +
                "in-game. (This launcher cannot pre-fill the connection — it is entered in-game.)",
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
            ("NoitaArchipelago (GitHub) ↗",  ModRepoUrl),
            ("Noita Setup Guide ↗",          SetupGuideUrl),
            ("Noita Guide (AP) ↗",           GameInfoUrl),
            ("Archipelago Official ↗",       ArchipelagoSite),
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
            btn.Click += (_, _) => { try { Process.Start(
                new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
            panel.Children.Add(btn);
        }
        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // Mod releases are the AP-relevant news for this game.
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_URL, ct);
            using var doc  = JsonDocument.Parse(json);
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

    /// "v1.5.0" → "1.5.0" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the "archipelago.zip" download URL.
    /// Falls back to the pinned 1.5.0 direct URL when the API is unreachable.
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

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;   // the .zip named like archipelago*.zip
                string? anyZip    = null;   // any .zip
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null && lower.Contains("archipelago"))
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

    // ── Private helpers — Steam / Noita detection ─────────────────────────────

    /// The Noita install dir to use: the override (if set and valid) wins, else the
    /// Steam-detected install. Null when nothing is found.
    private string? ResolveNoitaDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeNoitaDir(ov))
            return ov;

        try { return DetectSteamNoitaDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Noita if it has noita.exe (the data wak files /
    /// the mods folder are next to it).
    private static bool LooksLikeNoitaDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, NoitaExeName))) return true;
            // Defensive secondary signal: Noita ships a "data" wak alongside the exe
            // and a "mods" folder for user mods.
            if (File.Exists(Path.Combine(dir, "data.wak")) &&
                Directory.Exists(Path.Combine(dir, "mods"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Noita install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_881100.acf exists → steamapps\common\Noita.
    private static string? DetectSteamNoitaDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{NoitaSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    // Prefer the installdir named in the manifest; fall back to the
                    // conventional "Noita" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeNoitaDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeNoitaDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath). Both are tried; duplicates are harmless.
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

    // ── Private helpers — installed-mod detection ─────────────────────────────

    /// Find the NoitaArchipelago mod folder under the detected/override Noita
    /// install's mods tree. The mod lives at mods/archipelago and contains a
    /// mod.xml + init.lua. Be forgiving: accept the canonical "archipelago" folder
    /// if it holds either of those, else any mods sub-folder whose mod.xml mentions
    /// Archipelago. Returns the folder path or null.
    private string? FindInstalledModFolder()
    {
        try
        {
            string? noita = ResolveNoitaDir();
            if (noita == null) return null;
            string modsDir = Path.Combine(noita, "mods");
            if (!Directory.Exists(modsDir)) return null;

            // Canonical fast path: mods/archipelago with a recognizable mod file.
            string canonical = Path.Combine(modsDir, ModFolderName);
            if (Directory.Exists(canonical) && LooksLikeApModFolder(canonical))
                return canonical;

            // Defensive scan: a mods sub-folder whose mod.xml mentions Archipelago.
            foreach (string sub in Directory.EnumerateDirectories(modsDir))
            {
                string name = Path.GetFileName(sub);
                if (name.Equals(ModFolderName, StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(sub))
                    return sub; // exact-name folder, even if files look unusual

                string modXml = Path.Combine(sub, "mod.xml");
                if (!File.Exists(modXml)) continue;
                try
                {
                    string xml = File.ReadAllText(modXml);
                    if (xml.Contains("archipelago", StringComparison.OrdinalIgnoreCase))
                        return sub;
                }
                catch { /* unreadable — skip */ }
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    /// True when a folder looks like the AP mod's folder: it carries a Noita mod
    /// manifest (mod.xml) or the mod entry point (init.lua).
    private static bool LooksLikeApModFolder(string dir)
    {
        try
        {
            return File.Exists(Path.Combine(dir, "mod.xml")) ||
                   File.Exists(Path.Combine(dir, "init.lua"));
        }
        catch { return false; }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Noita: prefer the exe in the detected/override install; if that cannot
    /// be found but Steam is present, fall back to the steam:// URL. Surfaces a
    /// clear message rather than failing opaquely.
    private void StartNoita()
    {
        string? noita = ResolveNoitaDir();
        string? exe   = noita != null ? Path.Combine(noita, NoitaExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = noita!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Noita.");

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
            "Could not find noita.exe. Open this game's Settings and pick your Noita " +
            "install folder, or install Noita via Steam.",
            NoitaExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod's archipelago.zip and extract its (flat) contents into
    /// <Noita>/mods/archipelago/. Honest scope: the zip is self-sufficient (bundles
    /// the external libraries the mod needs), so this is a complete mod install. A
    /// stale mod folder is replaced cleanly so an update is clean. Defensive against
    /// a zip that nests a single "archipelago" sub-folder.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string apModDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"noita-archipelago-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((10, $"Downloading NoitaArchipelago mod {version}..."));
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
                        int pct = (int)(10 + 60 * downloaded / total);
                        progress.Report((pct, $"Downloading NoitaArchipelago mod... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Installing the mod into your Noita mods folder..."));

            // Replace any existing archipelago mod folder so an update is clean.
            if (Directory.Exists(apModDir))
            {
                try { Directory.Delete(apModDir, recursive: true); } catch { /* in-use — overwrite below */ }
            }
            Directory.CreateDirectory(apModDir);

            // The release zip is flat (verified): extract straight into mods\archipelago.
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, apModDir, overwriteFiles: true);

            // Defensive: if the zip wrapped everything in a single sub-folder (e.g.
            // an "archipelago" folder), flatten it so the mod files sit directly in
            // mods\archipelago (where Noita expects mod.xml / init.lua).
            if (!LooksLikeApModFolder(apModDir))
            {
                string[] subdirs = Directory.GetDirectories(apModDir);
                if (subdirs.Length == 1 && LooksLikeApModFolder(subdirs[0]))
                {
                    string sub = subdirs[0];
                    foreach (string fileSrc in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
                    {
                        string rel     = Path.GetRelativePath(sub, fileSrc);
                        string fileDst = Path.Combine(apModDir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                        File.Move(fileSrc, fileDst, overwrite: true);
                    }
                    try { Directory.Delete(sub, recursive: true); } catch { }
                }
            }

            progress.Report((90, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the install-dir override + an
    // informational version stamp) in its OWN JSON file so it stays a single
    // self-contained source file and does not modify Core/SettingsStore. BOM-less
    // UTF-8, read-modify-write (same approach as Hollow Knight / Stardew / TUNIC /
    // Jak).

    private sealed class NoitaSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private NoitaSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<NoitaSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(NoitaSettings s)
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

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    private void WriteStampedVersion(string v) { var s = LoadSettings(); s.ModVersion = v; SaveSettings(s); }
}
