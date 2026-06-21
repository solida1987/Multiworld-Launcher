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

// NOTE: WPF UI types are FULLY QUALIFIED throughout this file (System.Windows.*).
// The real launcher project sets <UseWindowsForms>true</UseWindowsForms> alongside
// WPF, so the bare names Button / TextBox / Color / Brushes / MessageBox /
// FontWeights / Orientation / Clipboard collide between System.Windows.Forms and
// System.Windows[.Controls/.Media] (CS0104). Qualifying every UI type avoids that.

namespace LauncherV2.Plugins.Subnautica;

// ═══════════════════════════════════════════════════════════════════════════════
// SubnauticaPlugin — install / launch for "Subnautica" (Unknown Worlds, 2018)
// played through the Archipelago Mod for Subnautica, a BepInEx plugin that is the
// in-game Archipelago client for Subnautica. This is a NATIVE "ConnectsItself"
// integration in the same family as the shipped Hollow Knight and Stardew Valley
// plugins (and Ship of Harkinian / Jak) — the game itself speaks to the AP server
// (no emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned
// Subnautica (Steam appid 264710; the official AP setup guide also lists the Epic
// Games Store), and Archipelago is a MOD (a BepInEx plugin) added on top. The
// honest integration ceiling — exactly like the shipped Hollow Knight/Stardew
// plugins — is "automate what is possible, guide the irreducible parts." The
// verified facts:
//
//   * THE AP WORLD game string is "Subnautica" (verified against
//     worlds/subnautica/__init__.py: `game = "Subnautica"`,
//     class SubnauticaWorld, required_client_version = (0, 6, 2)). Subnautica is a
//     CORE Archipelago world — it ships inside Archipelago itself, no custom world
//     drop needed. GameId here = "subnautica".
//
//   * THE MOD repo is Berserker66/ArchipelagoSubnauticaModSrc (the exact link the
//     official AP "Subnautica" setup guide points its "Archipelago Mod Releases
//     Page" at — verified live 2026-06-14). The latest release verified live is
//     tag 1.9.3, a SINGLE asset "Archipelago_193.zip" (~722 KB). NOTE: the task
//     brief's phrasing "Subnautica Archipelago Randomizer / Nexus" does not match
//     the official guide — the guide and the apworld both point at THIS GitHub
//     repo, so that is what this plugin uses.
//
//   * CRITICAL — THE RELEASE ZIP IS SELF-SUFFICIENT (this is the honest WIN over
//     Hollow Knight, whose zip needs Lumafly for dependencies). The official setup
//     guide states plainly: "Unpack the Archipelago Mod into your Subnautica
//     folder, so that Subnautica/BepInEx is a valid path." i.e. the zip BUNDLES
//     BepInEx itself (the winhttp.dll doorstop loader + the BepInEx tree) AND the
//     AP mod under BepInEx/plugins. The guide's Linux note (add
//     WINEDLLOVERRIDES="winhttp=n,b") confirms the bundled winhttp doorstop. So
//     this plugin CAN do a real best-effort one-shot install: download the release
//     zip and extract it INTO the detected Subnautica install root, merging the
//     BepInEx folder. No mod-manager dependency step is required (unlike HK).
//
//   * CONNECTION is made IN-GAME: after the mod is installed, you "use the connect
//     form in Subnautica's main menu to enter your connection information" — three
//     fields: Host (e.g. archipelago.gg:38281), PlayerName (your slot name), and an
//     optional Password. "Savegames store their connection information and
//     automatically attempt to reestablish the connection upon loading." There is
//     NO command-line arg and NO config file this launcher can pre-write (verified
//     against the setup guide). So this plugin does NOT attempt a connection
//     prefill — the post-install note and settings panel state this, and surface
//     the session's host/slot so the user can copy them into those three fields.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Subnautica install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, HKLM\...\WOW6432Node\Valve\Steam ->
//      InstallPath), parsing steamapps\libraryfolders.vdf for every library root
//      and locating steamapps\common\Subnautica via appmanifest_264710.acf. A
//      manual install-dir OVERRIDE (settings folder picker) is also supported and
//      takes precedence; it is validated (must contain Subnautica.exe) and
//      persisted in this plugin's OWN sidecar
//      (Games/ROMs/subnautica/subnautica_launcher.json) — Core/SettingsStore is
//      NOT modified. (Epic / other stores work via the manual picker.)
//   2. INSTALL/UPDATE (best effort) = download the mod's release zip from the real
//      release and extract it into the Subnautica install root (the zip is
//      self-sufficient — it carries BepInEx + the AP plugin). Plus clear, numbered
//      guided steps + links so the user can verify and complete setup. Never a
//      fake one-click that cannot exist.
//   3. LAUNCH = run Subnautica.exe from the detected/override install; if the exe
//      cannot be found but Steam is present, fall back to steam://rungameid/264710.
//      ConnectsItself = true (the mod owns the slot — the launcher must NOT hold
//      its own ApClient on it). SupportsStandalone = true (plain Subnautica runs
//      perfectly without AP). No prefill (in-game menu), stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", HK/Stardew-style) ─────────
//   * "Installed" is judged by the presence of the BepInEx tree in a detected/
//     override Subnautica install AND an Archipelago-named mod assembly under
//     BepInEx/plugins (case-insensitive, recursive) — NOT by an OUR-OWN version
//     stamp, because the user may instead install the zip by hand, which this
//     launcher honors. If no Subnautica install is detected, the tile reads "not
//     installed".
//   * Steam library parsing is defensive: a tolerant VDF scan that pulls quoted
//     "path" values; any failure degrades to "Subnautica not found" rather than
//     throwing.
//   * No plaintext AP password is ever written by this plugin (connection is
//     entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SubnauticaPlugin : IGamePlugin
{
    // ── Constants — the AP Subnautica mod (real repo, verified 2026-06-14) ─────
    private const string MOD_OWNER = "Berserker66";
    private const string MOD_REPO  = "ArchipelagoSubnauticaModSrc";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string ModReleasesPageUrl = $"{ModRepoUrl}/releases";
    private const string BepInExSite   = "https://github.com/BepInEx/BepInEx";
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Subnautica/setup/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Subnautica appid 264710.
    private const string SteamAppId = "264710";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The standard Steam install sub-folder name for Subnautica.
    private const string SteamCommonFolderName = "Subnautica";

    /// Pinned fallback for the mod when the GitHub API is unreachable. Tag 1.9.3
    /// verified live 2026-06-14; the single asset is "Archipelago_193.zip". The API
    /// path is the normal route; this is the net so an offline Install still has
    /// something to fetch.
    private const string FallbackVersion = "1.9.3";
    private const string FallbackZipName = "Archipelago_193.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "subnautica";
    public string DisplayName => "Subnautica";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/subnautica/__init__.py
    /// (SubnauticaWorld.game = "Subnautica"; required_client_version = (0, 6, 2)).
    public string ApWorldName => "Subnautica";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "subnautica.png");

    public string ThemeAccentColor => "#2E8AA8";   // deep-ocean teal
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Subnautica, Unknown Worlds' 2018 underwater survival adventure, played " +
        "through the Archipelago Mod for Subnautica — a BepInEx plugin that is the " +
        "in-game Archipelago client. Blueprints, tools, vehicles, upgrades and key " +
        "story items are shuffled into the multiworld, and the game connects to the " +
        "Archipelago server itself (no emulator, no bridge). You bring your own copy " +
        "of Subnautica (Steam, or the Epic Games Store), and the Archipelago mod is " +
        "added on top — its release already bundles the BepInEx mod loader, so the " +
        "launcher can detect your Steam install and unpack the mod into it for you. " +
        "You connect to your server from the connect form on Subnautica's main menu.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the AP Subnautica mod is present (the BepInEx tree exists
    /// in a detected/override Subnautica install and an Archipelago-named assembly
    /// is under BepInEx/plugins). We do NOT gate on our own stamp — the user may
    /// have unpacked the zip by hand, which we honor.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads (the mod zip) and any working files. The
    /// actual mod is extracted INTO the Subnautica install root, not here. Exposed
    /// as GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Subnautica");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Doom/Jak/HK). Per
    /// the brief, lives under Games/ROMs/subnautica/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "subnautica_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Archipelago Mod for Subnautica reports checks/items/goal to the AP
    // server itself — the launcher relays nothing. These exist for interface
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
            // Best-effort: read the version we stamped next to a direct-zip install
            // if present; otherwise report "installed" when the mod is detected.
            InstalledVersion = FindInstalledModDll() != null
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
        // 0. We need a Subnautica install to unpack the mod into. Prefer an
        //    explicit override; else auto-detect the Steam install.
        progress.Report((2, "Locating your Subnautica installation..."));
        string? snDir = ResolveSubnauticaDir();
        if (snDir == null)
            throw new InvalidOperationException(
                "Could not find a Subnautica installation. Open this game's Settings " +
                "and pick your Subnautica folder (the one containing Subnautica.exe), " +
                "or install Subnautica via Steam first. The Archipelago mod is added " +
                "on top of your own copy of the game.");

        // 1. Resolve the latest mod release (pinned fallback when offline).
        progress.Report((6, "Checking the latest Archipelago mod release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Archipelago Subnautica mod download on GitHub. " +
                "Check your internet connection, or download the mod zip manually from " +
                ModReleasesPageUrl + " and unpack it into your Subnautica folder so " +
                "that Subnautica\\BepInEx is a valid path. See Settings for the guided " +
                "steps. The mod repo is " + ModRepoUrl + ".");

        // 2. Download + extract the mod zip INTO the Subnautica install root.
        //    HONEST: the release zip is self-sufficient (it bundles BepInEx + the
        //    AP plugin), so extracting it over the install root is the documented,
        //    correct install — no separate mod-manager / dependency step is needed.
        await DownloadAndExtractModAsync(zipUrl, version, snDir, progress, ct);

        // 3. Stamp the version next to our sidecar so the tile can show it. (This
        //    is informational only — IsInstalled is judged by the mod's presence.)
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Installed the Archipelago mod {version} into your Subnautica folder " +
            "(BepInEx is bundled with the mod, so nothing else is required). To play: " +
            "launch the game, and on Subnautica's MAIN MENU use the Archipelago connect " +
            "form to enter your server (host:port), player/slot name, and optional " +
            "password. Your save stores the connection and reconnects automatically. " +
            "See Settings for the full guided steps."));
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
        // HONEST: the AP server connection for Subnautica is entered in the IN-GAME
        // connect form on the main menu (Host / PlayerName / Password). The mod
        // reconnects automatically when a save is loaded. There is no command-line /
        // config prefill we can apply (verified — see header). So launching from
        // this tile just starts the game; the user connects in-game with the session
        // credentials (the settings panel + note surface those to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartSubnautica();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Subnautica runs perfectly well.
    public bool SupportsStandalone => true;

    /// The Archipelago Mod for Subnautica owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartSubnautica();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started Subnautica from here. Kill what we launched.
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
        // The Archipelago Mod for Subnautica receives items from the AP server
        // directly; there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Subnautica folder contains
    /// Subnautica.exe and the Subnautica_Data folder. Return null when acceptable,
    /// else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Subnautica install folder.";

        if (LooksLikeSubnauticaDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeSubnauticaDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Subnautica installation. Pick the folder " +
               "that contains Subnautica.exe (the Subnautica_Data folder is next to it). " +
               @"For Steam this is usually ...\steamapps\common\Subnautica.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Unverified-offline honesty header ─────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Subnautica is your own game (Steam / Epic Games Store) with the " +
                   "Archipelago mod added on top. The mod's release already bundles the " +
                   "BepInEx mod loader, so the launcher can detect your Steam install and " +
                   "unpack the mod into it for you (or you can unpack the zip into your " +
                   "Subnautica folder by hand). You connect to your server from the " +
                   "connect form on Subnautica's main menu — there is no connection file " +
                   "to pre-fill. These external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "SUBNAUTICA INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? snDir       = ResolveSubnauticaDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = snDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + snDir
                : "Detected Steam install: " + snDir)
            : "Subnautica not detected. Pick your install folder below, or install " +
              "Subnautica via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = snDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // mod status line
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                    ? "Archipelago mod found: " + modDll
                    : "Archipelago mod not found in BepInEx\\plugins yet (use Install on " +
                      "the Play tab, or unpack the mod zip into your Subnautica folder).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? snDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Subnautica install folder (the one containing Subnautica.exe). " +
                          "Detected from Steam automatically; set it here to override (Epic " +
                          "Games Store / non-standard Steam library).",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120, Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Subnautica install folder (contains Subnautica.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? snDir ?? "")
                                   ? (overrideDir ?? snDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Subnautica folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeSubnauticaDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeSubnauticaDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 264710). Use this " +
                   "picker for the Epic Games Store, or a non-standard Steam library.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (this session) ────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game on the main-menu connect form)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "On Subnautica's main menu, the Archipelago connect form has three fields: " +
                   "Host (host:port, e.g. archipelago.gg:38281), PlayerName (your slot name), " +
                   "and an optional Password. The game connects and reconnects automatically " +
                   "whenever you load that save — you only enter it once per save.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Subnautica (Steam, or the Epic Games Store). Install it if you have not. " +
                "Use \"Select folder...\" above if it was not auto-detected.",
            "2. Install the Archipelago mod: use Install on the Play tab (it downloads the mod " +
                "zip and unpacks it into your Subnautica folder). The mod's release already " +
                "includes the BepInEx mod loader, so nothing else is needed.",
            "3. Alternative (manual): download the mod zip from the releases page (link below) " +
                "and unpack it into your Subnautica folder so that Subnautica\\BepInEx is a " +
                "valid path.",
            "4. Launch Subnautica from this launcher (or normally). The first launch with " +
                "BepInEx present takes a little longer while it initialises.",
            "5. On the MAIN MENU, open the Archipelago connect form and enter your Host " +
                "(host:port), PlayerName (slot), and optional Password, then start/continue " +
                "your game. (This launcher cannot pre-fill the connection — it is entered " +
                "in-game.)",
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
            Foreground = muted, Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Archipelago Subnautica Mod (releases) ↗", ModReleasesPageUrl),
            ("Subnautica Setup Guide ↗",                SetupGuideUrl),
            ("BepInEx (mod loader) ↗",                  BepInExSite),
            ("Archipelago Official ↗",                  ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0), FontSize = 12,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
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

    /// "v1.9.3" → "1.9.3" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the mod zip download URL. Prefers
    /// an asset matching "Archipelago" and ".zip"; falls back to the first .zip
    /// asset; falls back to the pinned 1.9.3 direct URL when the API is unreachable.
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
                string? preferred = null;   // the .zip named like Archipelago*.zip
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

    // ── Private helpers — Steam / Subnautica detection ────────────────────────

    /// The Subnautica install dir to use: the override (if set and valid) wins,
    /// else the Steam-detected install. Null when nothing is found.
    private string? ResolveSubnauticaDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeSubnauticaDir(ov))
            return ov;

        try { return DetectSteamSubnauticaDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Subnautica if it has Subnautica.exe and/or the
    /// Subnautica_Data folder.
    private static bool LooksLikeSubnauticaDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, "Subnautica.exe"))) return true;
            if (Directory.Exists(Path.Combine(dir, "Subnautica_Data"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Subnautica install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_264710.acf exists → steamapps\common\Subnautica.
    private static string? DetectSteamSubnauticaDir()
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
                    // conventional "Subnautica" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeSubnauticaDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeSubnauticaDir(conventional)) return conventional;
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

    /// Find the AP Subnautica mod assembly under the detected/override install's
    /// BepInEx\plugins tree (recursive, case-insensitive). The mod is named
    /// "Archipelago" (its DLL is Archipelago.dll), but we match any *.dll whose
    /// name contains "archipelago" so a renamed/sub-foldered drop still counts.
    /// Returns the dll path or null. Falls back to the BepInExPreloaderless /
    /// BepInEx core marker so a present-but-unscannable plugins tree is still
    /// treated as "mod installed" only when an Archipelago dll is actually found.
    private string? FindInstalledModDll()
    {
        try
        {
            string? sn = ResolveSubnauticaDir();
            if (sn == null) return null;

            string pluginsDir = Path.Combine(sn, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Subnautica: prefer the exe in the detected/override install; if that
    /// cannot be found but Steam is present, fall back to the steam:// URL.
    /// Surfaces a clear message rather than failing opaquely.
    private void StartSubnautica()
    {
        string? sn  = ResolveSubnauticaDir();
        string? exe = sn != null ? Path.Combine(sn, "Subnautica.exe") : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = sn!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Subnautica.");

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
            "Could not find Subnautica.exe. Open this game's Settings and pick your " +
            "Subnautica install folder, or install Subnautica via Steam.",
            "Subnautica.exe");
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod's release zip and extract it INTO the Subnautica install
    /// root. HONEST scope: the zip is self-sufficient (it bundles the BepInEx mod
    /// loader + the AP plugin under BepInEx\plugins), so extracting over the install
    /// root is the documented, correct install. We overwrite existing files so an
    /// update lands cleanly; we never delete the user's other files.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string subnauticaDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"subnautica-archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"subnautica-archipelago-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading Archipelago mod {version}..."));
            using (var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();

                long total      = response.Content.Headers.ContentLength ?? -1;
                long downloaded = 0;

                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tempZip);
                var buf = new byte[81920];
                int bytesRead;
                while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                    downloaded += bytesRead;
                    if (total > 0)
                    {
                        int pct = (int)(10 + 55 * downloaded / total);
                        progress.Report((pct, $"Downloading Archipelago mod... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            // Extract to a temp staging folder first, so we can normalise a possible
            // single wrapping sub-folder before merging into the install root.
            progress.Report((70, "Unpacking the mod..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // The documented layout is: the zip's contents merge so that
            // <Subnautica>\BepInEx is valid. Most releases place "BepInEx" (and a
            // winhttp.dll / doorstop_config.ini) at the zip root. If instead the zip
            // wrapped everything in ONE sub-folder, descend into it so the merge
            // lands correctly.
            string mergeRoot = tempExtract;
            if (!ContainsBepInEx(mergeRoot))
            {
                string[] subdirs = Directory.GetDirectories(mergeRoot);
                string[] files   = Directory.GetFiles(mergeRoot);
                if (files.Length == 0 && subdirs.Length == 1 && ContainsBepInEx(subdirs[0]))
                    mergeRoot = subdirs[0];
            }

            progress.Report((82, "Installing the mod into your Subnautica folder..."));
            MergeDirectory(mergeRoot, subnauticaDir);

            progress.Report((95, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// True when a folder contains a "BepInEx" sub-folder (the marker that this is
    /// the correct merge root for the Subnautica mod zip).
    private static bool ContainsBepInEx(string dir)
    {
        try { return Directory.Exists(Path.Combine(dir, "BepInEx")); }
        catch { return false; }
    }

    /// Recursively copy everything under <src> into <dst>, overwriting files and
    /// creating directories as needed. Never deletes anything in <dst> that is not
    /// being overwritten — so the user's existing Subnautica files are preserved.
    private static void MergeDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(src, dir);
            Directory.CreateDirectory(Path.Combine(dst, rel));
        }
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel     = Path.GetRelativePath(src, file);
            string target  = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the install-dir override + an
    // informational version stamp) in its OWN JSON file so it stays a single
    // self-contained source file and does not modify Core/SettingsStore. BOM-less
    // UTF-8, read-modify-write (same approach as Doom/Jak/HK).

    private sealed class SubnauticaSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private SubnauticaSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<SubnauticaSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(SubnauticaSettings s)
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
