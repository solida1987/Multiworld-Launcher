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
// We also do NOT add any file-level `using X = System.Windows...;` aliases — the
// project's GlobalUsings.cs already aliases the short names, and a second local
// alias would be CS1537 (duplicate alias). Bare names or fully-qualified only.

namespace LauncherV2.Plugins.Raft;

// ═══════════════════════════════════════════════════════════════════════════════
// RaftPlugin — install-guidance / launch for "Raft" (Redbeet Interactive, 2022)
// played through the "Raftipelago" mod, the in-game Archipelago client for Raft.
// This is a NATIVE "ConnectsItself" integration in the same family as the shipped
// Hollow Knight / Subnautica / Stardew Valley plugins (and Ship of Harkinian /
// Jak) — the game itself speaks to the AP server (no emulator, no Lua bridge, no
// launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned Raft
// (Steam appid 648800), and Archipelago is a MOD added on top. The honest
// integration ceiling — exactly like the shipped Hollow Knight plugin — is
// "automate what is possible, guide the irreducible parts." For Raft the
// IRREDUCIBLE part is LARGER than for Subnautica, and the plugin says so plainly.
// The verified facts:
//
//   * THE AP WORLD game string is "Raft" (verified against
//     worlds/raft/__init__.py: `game: str = "Raft"`, class RaftWorld,
//     required_client_version = (0, 3, 4)). Raft is a CORE Archipelago world —
//     it ships INSIDE Archipelago itself ("Bundled with Archipelago"), so there
//     is NO custom apworld for the launcher to fetch or stage. GameId = "raft".
//
//   * THE MOD is "Raftipelago" by SunnyBat, distributed through the Raft Modding
//     website as an RML ".rmod" package — NOT a GitHub release. The source repo
//     is github.com/SunnyBat/Raftipelago, but it publishes NO release assets
//     (verified live 2026-06-14: "There aren't any releases here"). So unlike the
//     Subnautica/Hollow-Knight mods (which ship a downloadable GitHub zip this
//     launcher can fetch+extract), there is NO stable direct URL the launcher can
//     pull the .rmod from. Raftipelago is installed THROUGH the Raft Mod Loader
//     (one click "Install" on its raftmodding.com page, or by dropping the .rmod
//     into the Raft "mods" folder). The plugin is HONEST about this: it GUIDES the
//     RML route and detects the result, rather than faking an auto-download that
//     cannot exist for this mod.
//
//   * THE MOD LOADER is the Raft Mod Loader (RML), whose launcher is
//     "RMLLauncher.exe". CRITICAL HONESTY — Raft mods ONLY load when the game is
//     started THROUGH RML; launching Raft straight from Steam does NOT load any
//     mods (verified across the raftmodding.com guide + community guides). So this
//     plugin's AP launch prefers to start RMLLauncher.exe (so Raftipelago loads),
//     and only falls back to steam://rungameid/648800 for PLAIN, non-AP play.
//
//   * THE DEPENDENCY is "ModUtils" (also a raftmodding.com .rmod). ModUtils must
//     be installed AND loaded before Raftipelago. The guided steps name it.
//
//   * CONNECTION is made IN-GAME via the Debug Console (open with F10), typing
//     `/connect {serverAddress} {username} {password}` — e.g.
//     `/connect archipelago.gg:38281 SunnyBat`. Only the player who CREATES/LOADS
//     the Raft world connects to Archipelago; every co-op player still needs the
//     mod loaded. There is NO command-line arg and NO config file this launcher
//     can pre-write (verified against the setup guide). So this plugin does NOT
//     attempt a connection prefill — the post-launch note and the settings panel
//     surface the session's server/slot so the user can type them into the
//     console.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Raft install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, HKLM\...\WOW6432Node\Valve\Steam
//      -> InstallPath), parsing steamapps\libraryfolders.vdf for every library
//      root and locating steamapps\common\Raft via appmanifest_648800.acf. A
//      manual install-dir OVERRIDE (settings folder picker) is also supported and
//      takes precedence; it is validated (must contain Raft.exe) and persisted in
//      this plugin's OWN sidecar (Games/ROMs/raft/raft_launcher.json) —
//      Core/SettingsStore is NOT modified.
//   2. INSTALL = GUIDED (honest). Because the Raftipelago/.rmod is not a fetchable
//      GitHub asset, InstallOrUpdate does not pretend to download it. Instead it
//      verifies the prerequisites (Raft install present; RML detected) and opens
//      the Raft Mod Loader so the user can one-click install Raftipelago + ModUtils
//      from raftmodding.com — with clear numbered steps + links in Settings. If
//      the user already dropped the .rmod into the "mods" folder by hand, the
//      plugin detects and honors it.
//   3. LAUNCH = start RMLLauncher.exe (so mods load) when RML can be located;
//      otherwise start Raft.exe from the detected/override install; otherwise fall
//      back to steam://rungameid/648800 (plain play only — mods will NOT load that
//      way, stated honestly). ConnectsItself = true (the mod owns the slot — the
//      launcher must NOT hold its own ApClient on it). SupportsStandalone = true
//      (plain Raft runs perfectly without AP). No prefill (in-game console),
//      stated honestly.
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", HK/Subnautica-style) ──────
//   * "Installed" is judged by the presence of a Raftipelago ".rmod" in a detected/
//     override Raft install's "mods" folder (case-insensitive, recursive) — NOT by
//     an OUR-OWN version stamp, because the user installs through RML, which this
//     launcher honors. If no Raft install is detected, the tile reads "not
//     installed".
//   * RML's install location is not fixed by a registry key we can rely on, so the
//     plugin searches a set of common locations for RMLLauncher.exe (and accepts a
//     user override via the settings picker). If RML is not found, launch degrades
//     to Raft.exe / Steam and the guidance points at the RML download page.
//   * Steam library parsing is defensive: a tolerant VDF scan that pulls quoted
//     "path" values; any failure degrades to "Raft not found" rather than throwing.
//   * No plaintext AP password is ever written by this plugin (connection is
//     entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class RaftPlugin : IGamePlugin
{
    // ── Constants — the Raftipelago mod + Raft Mod Loader (verified 2026-06-14) ─
    private const string MOD_OWNER = "SunnyBat";
    private const string MOD_REPO  = "Raftipelago";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string RaftipelagoPageUrl = "https://www.raftmodding.com/mods/raftipelago";
    private const string ModUtilsPageUrl    = "https://www.raftmodding.com/mods/modutils";
    private const string RmlDownloadUrl     = "https://www.raftmodding.com/download";
    private const string RaftModdingSite    = "https://www.raftmodding.com";
    private const string SetupGuideUrl      = "https://archipelago.gg/tutorial/Raft/setup/en";
    private const string ArchipelagoSite    = "https://archipelago.gg";

    // Steam — Raft appid 648800.
    private const string SteamAppId = "648800";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The standard Steam install sub-folder name for Raft.
    private const string SteamCommonFolderName = "Raft";

    /// The RML mod-drop folder name inside the Raft install (lowercase per the
    /// raftmodding.com + community guides). We match case-insensitively anyway.
    private const string ModsFolderName = "mods";

    /// The Raft Mod Loader launcher executable name (verified against the
    /// raftmodding.com "Installing Raft Mod Loader" guide).
    private const string RmlLauncherExe = "RMLLauncher.exe";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "raft";
    public string DisplayName => "Raft";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/raft/__init__.py
    /// (RaftWorld.game = "Raft"; required_client_version = (0, 3, 4)). Raft is a
    /// core, bundled Archipelago world.
    public string ApWorldName => "Raft";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "raft.png");

    public string ThemeAccentColor => "#2C7DA0";   // ocean blue
    public string[] GameBadges     => new[] { "Requires Raft on Steam" };

    public string Description =>
        "Raft, Redbeet Interactive's ocean-survival game, played through the " +
        "Raftipelago mod — an in-game Archipelago client for Raft. Item recipes, " +
        "story-island frequencies and more are shuffled into the multiworld, and " +
        "the game connects to the Archipelago server itself (no emulator, no " +
        "bridge). You bring your own copy of Raft on Steam, and the Archipelago mod " +
        "is added on top with the Raft Mod Loader (RML), which also installs the " +
        "ModUtils dependency it needs. The launcher detects your Steam install and " +
        "guides the Raft Mod Loader install. You connect to your server from Raft's " +
        "in-game Debug Console (F10) with the /connect command.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the Raftipelago mod is present (a Raftipelago-named .rmod
    /// is in a detected/override Raft install's "mods" folder). We do NOT gate on
    /// our own stamp — the user installs through RML, which we honor. If no Raft
    /// install is detected, this is false.
    public bool IsInstalled => FindInstalledModFile() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps any working files. The actual mod lives in the Raft
    /// install's "mods" folder (managed by RML), not here. Exposed as
    /// GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Raft");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained file — same as Doom/Jak/HK/
    /// Subnautica). Per the brief, lives under Games/ROMs/raft/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "raft_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Raftipelago mod reports checks/items/goal to the AP server itself — the
    // launcher relays nothing. These exist for interface compatibility
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
            // The mod has no fetchable version we can compare; report "installed"
            // when a Raftipelago .rmod is detected, else not installed.
            InstalledVersion = FindInstalledModFile() != null ? "installed" : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        // Raftipelago ships through raftmodding.com (no GitHub releases), so there
        // is no reliable "available version" to surface. Leave it null rather than
        // imply an update path we cannot drive.
        AvailableVersion = null;
        await Task.CompletedTask;
    }

    // ── Lifecycle — InstallOrUpdate (GUIDED — see header) ─────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. We need a Raft install for RML to mod. Prefer an explicit override;
        //    else auto-detect the Steam install.
        progress.Report((5, "Locating your Raft installation..."));
        string? raftDir = ResolveRaftDir();
        if (raftDir == null)
            throw new InvalidOperationException(
                "Could not find a Raft installation. Open this game's Settings and " +
                "pick your Raft folder (the one containing Raft.exe), or install Raft " +
                "via Steam first. The Archipelago mod is added on top of your own copy " +
                "of the game with the Raft Mod Loader.");

        await Task.CompletedTask;

        // 1. Already installed by hand / by a previous RML run? Honor it.
        string? existing = FindInstalledModFile();
        if (existing != null)
        {
            InstalledVersion = "installed";
            progress.Report((100,
                "Raftipelago is already in your Raft \"mods\" folder (" +
                Path.GetFileName(existing) + "). To play: start Raft through the Raft " +
                "Mod Loader, then open the Debug Console (F10) and type /connect with " +
                "your server, slot and password. See Settings for the full steps."));
            return;
        }

        // 2. HONEST: Raftipelago is not a fetchable GitHub asset — it installs
        //    through the Raft Mod Loader (one-click on its raftmodding.com page) or
        //    by dropping the .rmod into the "mods" folder. We do NOT fake a
        //    download. Make sure RML is available (open its download page if not),
        //    then open the Raftipelago + ModUtils pages so the user can Install
        //    them with one click in RML.
        progress.Report((35, "Checking for the Raft Mod Loader (RML)..."));
        string? rml = ResolveRmlLauncher();

        progress.Report((60, rml != null
            ? "Raft Mod Loader found. Opening the Raftipelago mod page..."
            : "Raft Mod Loader not found. Opening the RML download page..."));

        try
        {
            if (rml == null)
                OpenUrl(RmlDownloadUrl);

            // Open the mod pages so RML's one-click "Install" buttons are a click
            // away. Best effort — never throw on a shell failure.
            OpenUrl(ModUtilsPageUrl);     // dependency first (must load before)
            OpenUrl(RaftipelagoPageUrl);
        }
        catch { /* opening pages is a convenience, not a hard requirement */ }

        progress.Report((100,
            "Raftipelago installs through the Raft Mod Loader, so this launcher opened " +
            "the pages for you. STEPS: (1) Install the Raft Mod Loader if you have not " +
            "(its page just opened if it was missing). (2) On the ModUtils page click " +
            "Install, then on the Raftipelago page click Install (ModUtils must load " +
            "before Raftipelago). (3) Or drop their .rmod files into your Raft \"mods\" " +
            "folder by hand. (4) Launch Raft from THIS launcher (it starts the Raft Mod " +
            "Loader so mods load) or open RML and click Play. (5) In game, open the " +
            "Debug Console (F10) and type /connect <server> <slot> <password>. See " +
            "Settings for links and the full guide."));
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
        // HONEST: the AP server connection for Raft is entered IN-GAME via the
        // Debug Console (F10): /connect <server> <slot> <password>. There is no
        // command-line / config prefill we can apply (verified — see header). So
        // launching from this tile just starts the game (through RML so the mod
        // loads); the user connects in-game with the session credentials (the
        // settings panel surfaces those to copy).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartRaft();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Raft runs perfectly well.
    public bool SupportsStandalone => true;

    /// The Raftipelago mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartRaft();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started Raft / RML from here. Kill what we launched.
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
        // The Raftipelago mod receives items from the AP server directly; there is
        // nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Raft folder contains Raft.exe
    /// and the Raft_Data folder. Return null when acceptable, else a short
    /// human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Raft install folder.";

        if (LooksLikeRaftDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeRaftDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Raft installation. Pick the folder that " +
               "contains Raft.exe (the Raft_Data folder is next to it). For Steam this " +
               @"is usually ...\steamapps\common\Raft.";
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
            Text = "Raft is your own game on Steam with the Raftipelago mod added on top. " +
                   "Raftipelago installs through the Raft Mod Loader (RML) from " +
                   "raftmodding.com — it is not a download this launcher can fetch " +
                   "directly, so the launcher detects your Steam install and guides the " +
                   "RML install (the buttons below open the right pages). Mods only load " +
                   "when you start Raft THROUGH the Raft Mod Loader, not straight from " +
                   "Steam. You connect to your server from Raft's in-game Debug Console " +
                   "(F10) with /connect — there is no connection file to pre-fill. These " +
                   "external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "RAFT INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? raftDir     = ResolveRaftDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = raftDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + raftDir
                : "Detected Steam install: " + raftDir)
            : "Raft not detected. Pick your install folder below, or install Raft via " +
              "Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = raftDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // mod status line
        string? modFile = FindInstalledModFile();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modFile != null
                    ? "Raftipelago mod found: " + modFile
                    : "Raftipelago not found in the \"mods\" folder yet (install it with " +
                      "the Raft Mod Loader, or drop its .rmod into the mods folder).",
            FontSize = 11, Foreground = modFile != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? raftDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Raft install folder (the one containing Raft.exe). " +
                          "Detected from Steam automatically; set it here to override a " +
                          "non-standard Steam library.",
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
                Title            = "Select your Raft install folder (contains Raft.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? raftDir ?? "")
                                   ? (overrideDir ?? raftDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Raft folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeRaftDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeRaftDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 648800). Use this " +
                   "picker for a non-standard Steam library.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Raft Mod Loader (RML) ────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "RAFT MOD LOADER (RML)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });

        string? rmlPath = ResolveRmlLauncher();
        string? rmlOverride = LoadRmlOverride();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = rmlPath != null
                    ? (rmlOverride != null
                        ? "Using your selected Raft Mod Loader: " + rmlPath
                        : "Raft Mod Loader found: " + rmlPath)
                    : "Raft Mod Loader (RMLLauncher.exe) not found. Install it from the " +
                      "download page (link below), then optionally point the launcher at " +
                      "it here.",
            FontSize = 11, Foreground = rmlPath != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        var rmlRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var rmlBox = new System.Windows.Controls.TextBox
        {
            Text = rmlOverride ?? rmlPath ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "RMLLauncher.exe — the Raft Mod Loader launcher. Launching Raft " +
                          "through this is what makes mods (Raftipelago) load. Set it here " +
                          "if the launcher cannot find it automatically.",
        };
        var rmlBtn = new System.Windows.Controls.Button
        {
            Content = "Select RML...", Width = 120, Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        rmlBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select RMLLauncher.exe (the Raft Mod Loader launcher)",
                Filter = "Raft Mod Loader (RMLLauncher.exe)|RMLLauncher.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*",
                InitialDirectory = Directory.Exists(Path.GetDirectoryName(rmlOverride ?? rmlPath ?? "") ?? "")
                                   ? Path.GetDirectoryName(rmlOverride ?? rmlPath!)!
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FileName;
                if (!File.Exists(picked))
                {
                    System.Windows.MessageBox.Show(
                        "That file does not exist. Pick RMLLauncher.exe.",
                        "Not found", System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                SaveRmlOverride(picked);
                rmlBox.Text = picked;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(rmlBtn, System.Windows.Controls.Dock.Right);
        rmlRow.Children.Add(rmlBtn);
        rmlRow.Children.Add(rmlBox);
        panel.Children.Add(rmlRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Important: launch Raft through the Raft Mod Loader (this launcher does " +
                   "that for you when RML is found). Launching straight from Steam will " +
                   "NOT load Raftipelago.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connecting (this session) ────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (typed into the in-game Debug Console — F10)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "In Raft, open the Debug Console with F10 and type:\n" +
                   "    /connect <server:port> <slot name> <password>\n" +
                   "for example:  /connect archipelago.gg:38281 SunnyBat\n" +
                   "Only the player who creates or loads the Raft world connects to " +
                   "Archipelago; every co-op player still needs the mod loaded. (This " +
                   "launcher cannot pre-fill the connection — it is entered in-game.)",
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
            "1. Own Raft on Steam. Install it if you have not. Use \"Select folder...\" " +
                "above if it was not auto-detected.",
            "2. Install the Raft Mod Loader (RML) from its download page (link below). " +
                "Then point the launcher at RMLLauncher.exe above if it was not found.",
            "3. Install the mods: on the ModUtils page click Install, then on the " +
                "Raftipelago page click Install (ModUtils must load BEFORE Raftipelago). " +
                "Use Install on the Play tab to open both pages, or the links below.",
            "4. Alternative (manual): download the ModUtils and Raftipelago .rmod files " +
                "and drop them into your Raft \"mods\" folder.",
            "5. Launch Raft from THIS launcher (it starts the Raft Mod Loader so the mods " +
                "load) — or open the Raft Mod Loader yourself and click Play.",
            "6. In game, open the Debug Console (F10) and type /connect <server> <slot> " +
                "<password> to connect to Archipelago. (Only the world host connects.)",
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
            ("Raft Mod Loader (download) ↗", RmlDownloadUrl),
            ("Raftipelago mod (RaftModding) ↗", RaftipelagoPageUrl),
            ("ModUtils dependency (RaftModding) ↗", ModUtilsPageUrl),
            ("Raftipelago source (GitHub) ↗", ModRepoUrl),
            ("Raft Setup Guide ↗", SetupGuideUrl),
            ("Archipelago Official ↗", ArchipelagoSite),
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
            btn.Click += (_, _) => OpenUrl(u);
            panel.Children.Add(btn);
        }
        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // The mod's GitHub repo has no releases (verified), but we still try the
        // releases endpoint and degrade gracefully to an empty feed. (The
        // AP-relevant news for Raft is the bundled apworld's own changes, which the
        // launcher surfaces elsewhere.)
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

    // ── Private helpers — small utilities ─────────────────────────────────────

    /// "v1.2.3" → "1.2.3" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Open a URL / shell target. Best effort — never throws to the caller.
    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* shell unavailable — ignore */ }
    }

    // ── Private helpers — Steam / Raft detection ──────────────────────────────

    /// The Raft install dir to use: the override (if set and valid) wins, else the
    /// Steam-detected install. Null when nothing is found.
    private string? ResolveRaftDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeRaftDir(ov))
            return ov;

        try { return DetectSteamRaftDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Raft if it has Raft.exe and/or the Raft_Data folder.
    private static bool LooksLikeRaftDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, "Raft.exe"))) return true;
            if (Directory.Exists(Path.Combine(dir, "Raft_Data"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam Raft install: read the Steam root from the registry, gather
    /// all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_648800.acf exists → steamapps\common\Raft.
    private static string? DetectSteamRaftDir()
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
                    // conventional "Raft" folder name.
                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeRaftDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeRaftDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry (HKCU SteamPath +
    /// HKLM WOW6432Node InstallPath + HKLM InstallPath). Both are tried; duplicates
    /// are harmless.
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

    // ── Private helpers — Raft Mod Loader (RML) detection ─────────────────────

    /// Locate RMLLauncher.exe: the user override (if set + exists) wins; else
    /// search common install locations + the Raft folder. Null if not found.
    private string? ResolveRmlLauncher()
    {
        string? ov = LoadRmlOverride();
        if (ov != null && File.Exists(ov)) return ov;

        foreach (string candidate in RmlCandidatePaths())
        {
            try { if (File.Exists(candidate)) return candidate; }
            catch { /* skip unreadable candidate */ }
        }
        return null;
    }

    /// Common locations RMLLauncher.exe is installed to. RML has no reliable
    /// registry key, so we probe the documented/observed spots: %LOCALAPPDATA%\
    /// RaftModLoader, %APPDATA%\RaftModLoader, %PROGRAMFILES%\RaftModLoader, the
    /// Raft install folder itself, and a "RaftModLoader" folder next to the Raft
    /// install. Each is yielded as a full path to RMLLauncher.exe.
    private IEnumerable<string> RmlCandidatePaths()
    {
        string?[] bases =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RaftModLoader"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RaftModLoader"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RaftModLoader"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RaftModLoader"),
        };
        foreach (string? b in bases)
            if (!string.IsNullOrWhiteSpace(b))
                yield return Path.Combine(b!, RmlLauncherExe);

        // The Raft install folder, and a sibling "RaftModLoader" folder.
        string? raft = ResolveRaftDir();
        if (raft != null)
        {
            yield return Path.Combine(raft, RmlLauncherExe);
            string? parent = Path.GetDirectoryName(raft);
            if (!string.IsNullOrWhiteSpace(parent))
                yield return Path.Combine(parent!, "RaftModLoader", RmlLauncherExe);
        }
    }

    // ── Private helpers — installed-mod detection ─────────────────────────────

    /// Find the Raftipelago mod file under the detected/override Raft install's
    /// "mods" folder (recursive, case-insensitive). Raftipelago ships as a .rmod;
    /// we match any *.rmod whose name contains "raftipelago" so a renamed/
    /// sub-foldered drop still counts. Returns the file path or null.
    private string? FindInstalledModFile()
    {
        try
        {
            string? raft = ResolveRaftDir();
            if (raft == null) return null;

            string modsDir = FindModsFolder(raft);
            if (modsDir == null || !Directory.Exists(modsDir)) return null;

            foreach (string file in Directory.EnumerateFiles(modsDir, "*.rmod", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                if (name.IndexOf("raftipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return file;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    /// Resolve the RML "mods" folder inside a Raft install, matching the folder
    /// name case-insensitively (docs use lowercase "mods", but be tolerant).
    /// Returns the conventional lowercase path if no folder exists yet.
    private static string FindModsFolder(string raftDir)
    {
        try
        {
            foreach (string sub in Directory.EnumerateDirectories(raftDir))
            {
                if (string.Equals(Path.GetFileName(sub), ModsFolderName, StringComparison.OrdinalIgnoreCase))
                    return sub;
            }
        }
        catch { /* fall through to the conventional path */ }
        return Path.Combine(raftDir, ModsFolderName);
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Raft so mods load: prefer RMLLauncher.exe (the Raft Mod Loader — the
    /// ONLY way mods load); else Raft.exe from the detected/override install; else
    /// fall back to the steam:// URL (plain play only — mods will NOT load that
    /// way). Surfaces a clear message rather than failing opaquely.
    private void StartRaft()
    {
        // 1. Preferred: launch through the Raft Mod Loader so Raftipelago loads.
        string? rml = ResolveRmlLauncher();
        if (rml != null && File.Exists(rml))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = rml,
                WorkingDirectory = Path.GetDirectoryName(rml)!,
                UseShellExecute  = true,
            });
            if (proc != null)
            {
                TrackProcess(proc);
                return;
            }
            // If RML failed to start, fall through to Raft.exe / Steam.
        }

        // 2. Fall back to Raft.exe directly. NOTE: this does NOT load mods — it is
        //    a last resort so the tile still launches the game. (Plain play.)
        string? raft = ResolveRaftDir();
        string? exe  = raft != null ? Path.Combine(raft, "Raft.exe") : null;
        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = raft!,
                UseShellExecute  = true,
            });
            if (proc != null)
            {
                TrackProcess(proc);
                return;
            }
        }

        // 3. Last resort: Steam URL (plain play; mods will not load this way).
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
            "Could not start Raft. Install the Raft Mod Loader (so mods load) and/or " +
            "open this game's Settings to pick your Raft folder, or install Raft via " +
            "Steam. See Settings for the guided steps.",
            RmlLauncherExe);
    }

    /// Wire up process tracking + exit notification for a launched process.
    private void TrackProcess(Process proc)
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
        catch { /* some processes don't expose Exited — non-fatal */ }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its launcher-side settings (the Raft install-dir override +
    // the RMLLauncher.exe override) in its OWN JSON file so it stays a single
    // self-contained source file and does not modify Core/SettingsStore. BOM-less
    // UTF-8, read-modify-write (same approach as Doom/Jak/HK/Subnautica).

    private sealed class RaftSettings
    {
        public string? InstallOverride { get; set; }
        public string? RmlOverride     { get; set; }
    }

    private RaftSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<RaftSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(RaftSettings s)
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

    private string? LoadRmlOverride()
    {
        string? p = LoadSettings().RmlOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }
    private void SaveRmlOverride(string p) { var s = LoadSettings(); s.RmlOverride = p; SaveSettings(s); }
}
