using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using LauncherV2.Core;

// NOTE on type qualification (BUILD GOTCHA):
// This project sets BOTH <UseWPF>true</UseWPF> and <UseWindowsForms>true</UseWindowsForms>.
// That makes a long list of simple type names ambiguous between WPF and WinForms
// (Clipboard, MessageBox, Application, Color, Brush(es), Button, TextBox, CheckBox,
// Orientation, FontWeights, HorizontalAlignment, Cursors, …). To avoid CS0104 this
// file deliberately does NOT do `using System.Windows.Controls;` /
// `using System.Windows.Media;` — every WPF UI type below is written fully qualified
// (System.Windows.Controls.*, System.Windows.Media.*, System.Windows.MessageBox, …).
// It also does NOT add any `using X = System.Windows...;` file-level alias (that would
// be CS1537 — GlobalUsings.cs already aliases the colliding short names project-wide).

namespace LauncherV2.Plugins.StarCraft2;

// ═══════════════════════════════════════════════════════════════════════════════
// StarCraft2Plugin — detect / guide / launch for "StarCraft II" (Blizzard) played
// through its OFFICIAL Archipelago integration (AP-main worlds/sc2).
//
// WHAT KIND OF INTEGRATION IS THIS? (verified this session — see REALITY CHECK)
// ─────────────────────────────────────────────────────────────────────────────
// StarCraft II's Archipelago support is part of AP-MAIN itself (worlds/sc2), NOT a
// third-party mod repo with a one-click download. The moving pieces are:
//
//   * The "Starcraft 2 Client" — a client SHIPPED INSIDE the Archipelago release
//     the player already has (registered as Component("Starcraft 2 Client", ...);
//     when AP is installed via the Windows installer it lands as a frozen exe,
//     "ArchipelagoStarcraft2Client.exe", next to ArchipelagoLauncher.exe). This
//     client owns the AP slot connection AND drives the running StarCraft II game
//     via Blizzard's SC2 API.
//   * The AP SC2 Maps + Data files — the client downloads these itself with its
//     own  /download_data  command, INTO the player's StarCraft II Maps folder
//     (Documents\StarCraft II\Maps). The launcher does NOT reproduce that.
//   * The player's own copy of StarCraft II (free from Blizzard / Battle.net) —
//     the base game. The launcher must not, and does not, ship or recreate it.
//
// HONESTY NOTE — this is a GUIDED case (like Undertale / Ship of Harkinian), and
// like Undertale the AP slot is held by a SEPARATE long-running tool (the
// "Starcraft 2 Client"), not by the game process. The concrete, verified reasons
// the launcher canNOT do a fake "one-click install":
//
//   1. There is NO redistributable runnable game and NO standalone mod zip to
//      download. The client ships *inside the Archipelago install* the player runs;
//      the base game is Blizzard's free StarCraft II via Battle.net.
//   2. The OFFICIAL, documented setup (the AP "StarCraft 2" setup guide + the
//      bundled client) is: install StarCraft II + Archipelago, run
//      ArchipelagoStarcraft2Client.exe, then type  /download_data  ONCE so the
//      client installs the AP Maps/Data into the SC2 Maps folder. Reproducing that
//      (drive the SC2 API, fetch + place maps) is exactly the bundled client's job
//      — so, like SoH did not reproduce OTR generation, this plugin does NOT fake it.
//   3. CONNECTION IS CLIENT-RELAY, NOT in-game and NOT launcher-relay. The player
//      connects by typing  /connect <server:port>  (then slot name, then password)
//      INTO THE STARCRAFT 2 CLIENT's Archipelago tab — there are no connection
//      command-line args on a game exe, and the launcher's own ApClient must NOT
//      also sit on the slot (the SC2 Client would be kicked, or kick us). Hence
//      ConnectsItself = true.
//
// WHAT THIS PLUGIN HONESTLY DOES:
//   * DETECT StarCraft II (registry InstallLocation under Blizzard's uninstall keys,
//     plus the player's  Documents\StarCraft II  user folder) so it can tell the
//     player whether the base game / Maps folder is present.
//   * LOCATE the bundled "ArchipelagoStarcraft2Client.exe" by scanning the player's
//     Archipelago install dir — resolved READ-ONLY from common locations
//     (%ProgramData%\Archipelago, %LocalAppData%\Archipelago, the AP registry
//     install path, PATH/known launchers) or a user-set Browse override.
//   * GUIDE the irreducible steps in InstallOrUpdate / Settings (install StarCraft
//     II free from Blizzard, install Archipelago, run the client + /download_data
//     once, then /connect from the client), with direct links (SC2 site, AP
//     releases, official setup guide, archipelago.gg).
//   * LAUNCH ArchipelagoStarcraft2Client.exe when located (one-click open of the
//     client that owns the connection); it can NOT prefill the connection because
//     the connection is entered into that client, which the launcher does not own.
//   * NEVER claim an install exists when it does not, NEVER write/create/modify
//     anything under the player's Archipelago install (C:\ProgramData\Archipelago
//     is treated as READ-ONLY — we only READ it to find the client), and keep its
//     one setting in its OWN JSON sidecar (does not touch Core/SettingsStore).
//
// REALITY CHECK (2026-06-14) — facts verified this session against AP-main
// (the installed sc2.apworld at C:\ProgramData\Archipelago\lib\worlds\sc2.apworld
// was extracted and inspected):
//   * AP game string: "Starcraft 2" — VERIFIED in worlds/sc2/archipelago.json
//     ({"game": "Starcraft 2", ...}) and the world __init__. World id "sc2".
//   * Client component display name: "Starcraft 2 Client" (VERIFIED in the world
//     __init__ / client). The Windows installer freezes it as
//     "ArchipelagoStarcraft2Client.exe" (VERIFIED in the official setup guide,
//     worlds/sc2/docs/setup_en.md step 2: "Run ArchipelagoStarcraft2Client.exe").
//   * Maps/Data install: the client's  /download_data  command "will automatically
//     install the Maps and Data files needed to play StarCraft 2 Archipelago"
//     (VERIFIED, setup_en.md step 3) — into the SC2 Maps folder. The launcher does
//     not do this; the client does.
//   * Connect: in the client's Archipelago tab type  /connect <server IP> , then
//     the slot name, then the password if any (VERIFIED, setup_en.md "How do I join
//     a MultiWorld game?"). The client holds the slot and drives the game.
//   * Base game: StarCraft II, free, https://starcraft2.com/en-us/ (VERIFIED,
//     setup_en.md "Required Software"). Install dir tracked by Battle.net; user
//     content lives under  Documents\StarCraft II .
//
// DEFENSIVE / UNVERIFIED DETAILS (flagged, "verify at build time"):
//   * Frozen client exe name: "ArchipelagoStarcraft2Client.exe" is the documented
//     name; the bundled-exe convention in a real AP install is
//     Archipelago<FrozenName>Client.exe (e.g. ArchipelagoUndertaleClient.exe was
//     present in the inspected install; the SC2 one may be absent until the user's
//     AP release bundles it). ResolveClientExe() prefers that exact name and falls
//     back to any "*starcraft2*client*.exe" / "*sc2*client*.exe" in the AP dir, and
//     as a last resort offers ArchipelagoLauncher.exe (which can open the client by
//     name). It never writes into the AP dir.
//   * StarCraft II registry key path / value names vary by installer version; the
//     detector tries several Blizzard uninstall keys and the Documents folder, and
//     treats "found the Documents\StarCraft II folder" as a soft positive. SC2
//     detection is INFORMATIONAL only — the client (not the launcher) is what
//     actually needs to find and drive the game.
//   * IsInstalled here means "the bundled Starcraft 2 Client exe was located", i.e.
//     the launcher can open the thing that owns the connection. It is NOT a claim
//     that StarCraft II or the AP Maps are installed (the Settings panel reports
//     those separately and honestly).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class StarCraft2Plugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    /// The frozen bundled client exe name (VERIFIED — setup_en.md step 2:
    /// "Run ArchipelagoStarcraft2Client.exe").
    private const string PreferredClientExe = "ArchipelagoStarcraft2Client.exe";

    /// The generic AP launcher exe; it can open the client by component name
    /// ("Starcraft 2 Client") and is the last-resort launch target.
    private const string ApLauncherExe = "ArchipelagoLauncher.exe";

    /// The component display name used to ask ArchipelagoLauncher.exe to open the
    /// SC2 client (VERIFIED — setup_en.md: the `"Starcraft 2 Client"` launch arg).
    private const string ClientComponentName = "Starcraft 2 Client";

    /// The user content / Maps folder name under the user's Documents.
    private const string Sc2DocumentsFolderName = "StarCraft II";

    /// Official Archipelago "StarCraft 2" setup guide.
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Starcraft%202/setup/en";

    /// StarCraft II — free from Blizzard (the base game the player must own/install).
    private const string Sc2StoreUrl = "https://starcraft2.com/en-us/";

    /// Archipelago releases — the Starcraft 2 Client ships inside this download.
    private const string ApReleasesUrl =
        "https://github.com/ArchipelagoMW/Archipelago/releases";

    /// GitHub releases API for the AP repo (news feed source).
    private const string ApReleasesApiUrl =
        "https://api.github.com/repos/ArchipelagoMW/Archipelago/releases";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "sc2";
    public string DisplayName => "StarCraft II";
    public string Subtitle    => "Native PC · Archipelago";

    /// EXACT AP game string — VERIFIED against worlds/sc2/archipelago.json
    /// ({"game": "Starcraft 2", ...}). Note the lowercase 'c' and the Arabic '2'.
    public string ApWorldName => "Starcraft 2";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "sc2.png");

    public string ThemeAccentColor => "#1E5A9E";   // Terran command-console blue
    public string[] GameBadges     => new[] { "Requires StarCraft II + Archipelago" };

    public string Description =>
        "StarCraft II is Blizzard's acclaimed real-time strategy game, free to " +
        "download from Blizzard. This is the official Archipelago integration " +
        "(part of Archipelago itself), which shuffles your campaign unit, upgrade " +
        "and ability unlocks across all four campaigns into the multiworld. You " +
        "bring your own free copy of StarCraft II; Archipelago's bundled " +
        "\"Starcraft 2 Client\" downloads the AP maps into your StarCraft II Maps " +
        "folder (its /download_data command) and connects to the multiworld while " +
        "you play. The launcher detects StarCraft II, locates the bundled client, " +
        "guides the one-time setup, and can launch the client for you.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    // SC2's AP integration is versioned by the Archipelago release the user runs
    // (the client ships inside it), not by a standalone mod tag. There is no
    // independent version stamp this plugin can author honestly, so these stay null
    // (the news feed surfaces AP release versions instead).
    public string? InstalledVersion => null;
    public string? AvailableVersion => null;

    /// Installed == the bundled "Starcraft 2 Client" exe was located (i.e. the
    /// launcher can open the tool that owns the AP connection). This is NOT a claim
    /// that StarCraft II or the AP Maps are installed — those are reported
    /// separately in Settings.
    public bool IsInstalled => ResolveClientExe() != null;
    public bool IsRunning   { get; private set; }

    /// Empty string when not installed (interface contract). Reports the resolved
    /// Archipelago install dir (where the client lives) when known, else "".
    public string GameDirectory => ResolveApInstallDir() ?? "";

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained source file — same approach as the
    /// Undertale / Doom / Celeste 64 plugins). BOM-less UTF-8.
    private string SettingsSidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                        "starcraft2_launcher.json");

    private string SettingsSidecarDir => Path.GetDirectoryName(SettingsSidecarPath)!;

    /// User-set override pointing at the player's Archipelago INSTALL FOLDER (the
    /// one containing ArchipelagoStarcraft2Client.exe / ArchipelagoLauncher.exe).
    /// Optional; when unset the plugin auto-resolves from common locations.
    /// IMPORTANT: this folder is treated as READ-ONLY — the plugin only READS it to
    /// find the client and never writes/creates/modifies anything inside it.
    private string? _overrideApDir;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _clientProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The external Starcraft 2 Client owns the AP slot and relays checks/items/goal
    // to the server itself (and drives the SC2 game via Blizzard's API). The
    // launcher relays nothing. These exist only for interface compatibility
    // (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Constructor — restore a previously chosen AP install folder ───────────

    public StarCraft2Plugin()
    {
        try
        {
            string? saved = LoadSettings().ApDir;
            if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
                _overrideApDir = saved;
        }
        catch { /* fall back to auto-resolution only */ }
    }

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────
    // No independent version to compare (the client ships inside the player's AP
    // install). Contract: never throw on network failure.

    public Task CheckForUpdateAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────
    // HONEST guided setup. There is nothing for the launcher to download here (the
    // client lives inside the player's Archipelago install, the AP maps are fetched
    // by the client's /download_data, and the base game is the player's free
    // StarCraft II). So this reports the current detection state and the exact
    // remaining manual steps — it never fabricates an install and never writes to
    // the Archipelago folder.

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((10, "Looking for StarCraft II and the Archipelago client..."));

        bool    sc2     = DetectStarCraft2() != null;
        string? apDir   = ResolveApInstallDir();
        string? client  = ResolveClientExe();

        if (client != null)
        {
            progress.Report((100,
                "Found the Starcraft 2 Client (\"" + Path.GetFileName(client) + "\") in " +
                "your Archipelago install. Press Play to open it. The FIRST time, type " +
                "/download_data in the client to install the AP maps into your StarCraft " +
                "II Maps folder, then /connect <server:port> (and your slot name) to join. " +
                (sc2 ? "" : "StarCraft II was not detected yet — install it free from " +
                            "Blizzard (see the link in Settings). ") +
                "See the Setup Guide link in Settings."));
            return Task.CompletedTask;
        }

        // Client not located — give the most specific guidance we can.
        if (!string.IsNullOrEmpty(apDir))
        {
            progress.Report((100,
                "Archipelago was found at \"" + apDir + "\", but the Starcraft 2 Client " +
                "exe was not located there. Make sure you have a recent Archipelago " +
                "release (the client, \"" + PreferredClientExe + "\", is bundled by the " +
                "Archipelago installer). You can also point this plugin at your " +
                "Archipelago folder in Settings. " +
                (sc2 ? "" : "Also install StarCraft II free from Blizzard. ") +
                "See the Setup Guide link in Settings."));
        }
        else
        {
            progress.Report((100,
                "Archipelago was not detected. 1) Install StarCraft II (free) from " +
                "Blizzard. 2) Install the latest Archipelago release — it bundles the " +
                "\"" + PreferredClientExe + "\". 3) Point this plugin at your Archipelago " +
                "folder in Settings (or install it to the default location). Then press " +
                "Play, run /download_data once, and /connect from the client. See the " +
                "Setup Guide link in Settings."));
        }
        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public Task<bool> VerifyInstallAsync(CancellationToken ct = default)
        => Task.FromResult(IsInstalled);

    // ── AutoMod-style validation of a user-picked Archipelago folder ──────────

    /// The user located their Archipelago INSTALL folder (the one containing the
    /// bundled clients). Accept it only when it actually contains the SC2 client or
    /// the generic AP launcher. Returns null when acceptable, else a short reason so
    /// they can pick again. READ-ONLY: only inspects the folder, never writes to it.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Archipelago install folder " +
                   "(the one containing ArchipelagoLauncher.exe / " + PreferredClientExe + ").";

        try
        {
            if (FindClientExeIn(folder) != null) return null;
        }
        catch
        {
            return "Could not read that folder. Pick a different one and try again.";
        }

        return "That folder does not contain the Starcraft 2 Client (\"" +
               PreferredClientExe + "\") or ArchipelagoLauncher.exe. Pick your " +
               "Archipelago install folder, and make sure you have a recent Archipelago " +
               "release (the client is bundled by its installer).";
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────
    // Best effort: open the bundled Starcraft 2 Client so the player gets one-click
    // launch of the tool that owns the connection. The connection itself is entered
    // into that client (see header), so we do NOT pass connection args and we do NOT
    // hold an ApClient on the slot (ConnectsItself = true). If the client is not
    // located, fail with honest guidance.

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        _ = session; // connection is made inside the Starcraft 2 Client, not via args
        LaunchClient();
        return Task.CompletedTask;
    }

    /// StarCraft II is a complete game playable without Archipelago (via Battle.net).
    /// "Launch Standalone" here opens the SC2 site / Battle.net rather than the AP
    /// client, since the base game is launched through Blizzard's launcher.
    public bool SupportsStandalone => true;

    /// The external Starcraft 2 Client owns the AP slot connection (see header). The
    /// launcher must NOT connect its own ApClient to the same slot while the client
    /// runs, or they would kick each other off.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // The base game is launched via Blizzard / Battle.net, not by us. Best
        // effort: try a Battle.net SC2 launch URI; fall back to the SC2 website.
        if (!TryOpenUrl("battlenet://S2") && !TryOpenUrl("blizzard://S2"))
            TryOpenUrl(Sc2StoreUrl);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // Kill ONLY the client process tree we launched — never the user's AP server
        // and never the StarCraft II game itself.
        try { _clientProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No password/args are passed by this plugin (the connection is entered in
        // the Starcraft 2 Client), so there is nothing sensitive to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The Starcraft 2 Client receives items from the AP server directly and
        // applies them to the running game; there is nothing for the launcher to
        // forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The Starcraft 2 Client renders its own connection state; no launcher HUD
        // channel into the game.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD0, 0xA0, 0x40));

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "StarCraft II's Archipelago support is part of Archipelago itself, " +
                   "not a one-click mod. You bring your own free copy of StarCraft II; " +
                   "Archipelago's bundled \"Starcraft 2 Client\" downloads the AP maps " +
                   "(its /download_data command) and connects to the multiworld while " +
                   "you play. The launcher detects your install, locates the bundled " +
                   "client, guides setup, and can launch the client — but you connect " +
                   "from the client, not here. The launcher only READS your Archipelago " +
                   "folder to find the client; it never modifies it.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: StarCraft II (detected) ──────────────────────────────
        string? sc2Dir = DetectStarCraft2();
        panel.Children.Add(SectionHeader("STARCRAFT II (BASE GAME)", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = sc2Dir != null
                ? "✓ Detected:\n" + sc2Dir
                : "Not detected. Install StarCraft II free from Blizzard (link below). " +
                  "It is launched through Battle.net; the Starcraft 2 Client drives it " +
                  "once the maps are installed.",
            FontSize = 11, Foreground = sc2Dir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Archipelago install (override) ───────────────────────
        panel.Children.Add(SectionHeader("ARCHIPELAGO INSTALL FOLDER (HOLDS THE CLIENT)", muted));

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = _overrideApDir ?? ResolveApInstallDir() ?? "",
            IsReadOnly = true, FontSize = 12, Margin = new Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Browse...", Width = 90, Padding = new Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };

        var statusText = new System.Windows.Controls.TextBlock
        {
            FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        };
        void RefreshStatus()
        {
            string? client = ResolveClientExe();
            statusText.Text = client != null
                ? "✓ Found \"" + Path.GetFileName(client) + "\" — ready to launch the client."
                : "Starcraft 2 Client not found here yet. Point this at your Archipelago " +
                  "folder (the one with ArchipelagoLauncher.exe), and make sure you have " +
                  "a recent Archipelago release.";
            statusText.Foreground = client != null ? success : muted;
        }
        RefreshStatus();

        dirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select your Archipelago install folder (read-only)",
                InitialDirectory = Directory.Exists(_overrideApDir ?? "")
                    ? _overrideApDir!
                    : (ResolveApInstallDir() is string ap && Directory.Exists(ap)
                        ? ap
                        : AppContext.BaseDirectory),
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    var go = System.Windows.MessageBox.Show(
                        bad + "\n\nUse this folder anyway?",
                        "No Starcraft 2 Client found here",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (go != MessageBoxResult.Yes) return;
                }
                _overrideApDir = picked;
                dirBox.Text = picked;
                SaveApDir(picked);
                RefreshStatus();
            }
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);
        panel.Children.Add(statusText);

        // ── Section: setup + connection steps ─────────────────────────────
        panel.Children.Add(SectionHeader("SETUP & CONNECTION", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "1) Install StarCraft II (free) from Blizzard and the latest Archipelago " +
                "release (it bundles \"" + PreferredClientExe + "\").\n" +
                "2) Press Play here (or run the client) and type\n" +
                "      /download_data\n" +
                "   once. The client installs the AP maps into your StarCraft II Maps " +
                "folder (Documents\\StarCraft II\\Maps).\n" +
                "3) In the client's Archipelago tab type\n" +
                "      /connect <server:port>\n" +
                "   then enter your SLOT NAME (and password if any) when prompted.\n\n" +
                "The client connects to the multiworld and drives StarCraft II to play " +
                "your missions. (The connection is entered in the Starcraft 2 Client — " +
                "the launcher does not connect to your slot itself, and it never writes " +
                "to your Archipelago folder.)",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("StarCraft II (free) ↗",       Sc2StoreUrl),
            ("StarCraft 2 Setup Guide ↗",   SetupGuideUrl),
            ("Archipelago Releases ↗",      ApReleasesUrl),
            ("Archipelago Official ↗",      "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => TryOpenUrl(u);
            panel.Children.Add(btn);
        }
        return panel;
    }

    private static System.Windows.Controls.TextBlock SectionHeader(
        string text, System.Windows.Media.Brush muted)
        => new System.Windows.Controls.TextBlock
        {
            Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        };

    // ── News feed ─────────────────────────────────────────────────────────────
    // SC2's integration ships with the Archipelago release, so the most honest
    // "news" is the AP release stream (the Starcraft 2 Client comes from there).
    // Never throws — empty on failure.

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(ApReleasesApiUrl, ct);
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
                    Version: el.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — Archipelago install / client resolution (READ-ONLY) ──

    /// Resolve the player's Archipelago INSTALL folder (where the bundled clients
    /// live): the user override if it still exists, else the first of several
    /// common, READ-ONLY locations that contains a client / the AP launcher. Never
    /// writes to or creates any of these folders. Returns null when none found.
    private string? ResolveApInstallDir()
    {
        // 1. User override.
        if (!string.IsNullOrWhiteSpace(_overrideApDir) && Directory.Exists(_overrideApDir))
            return _overrideApDir;

        // 2. Common install locations + registry, in priority order. We accept a
        //    folder only when it actually holds a client/launcher exe so we don't
        //    point at an empty directory.
        foreach (string dir in EnumerateCandidateApDirs())
        {
            try
            {
                if (Directory.Exists(dir) && FindClientExeIn(dir) != null)
                    return dir;
            }
            catch { /* unreadable candidate — skip */ }
        }
        return null;
    }

    /// Candidate Archipelago install directories (READ-ONLY), de-duplicated, in
    /// priority order: %ProgramData%\Archipelago, %LocalAppData%\Archipelago, the
    /// AP uninstall-registry InstallLocation, and the folder of any running/known
    /// ArchipelagoLauncher.exe on PATH. We never create these.
    private static IEnumerable<string> EnumerateCandidateApDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string? dir in new[]
        {
            // The Windows installer's default (the inspected install was here).
            SafeCombine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Archipelago"),
            SafeCombine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Archipelago"),
            SafeCombine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Archipelago"),
            SafeCombine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Archipelago"),
            // Registry: Archipelago's uninstall key records its InstallLocation.
            ReadApInstallLocationFromRegistry(),
        })
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string norm = dir!.Replace('/', '\\').TrimEnd('\\');
            if (norm.Length > 0 && seen.Add(norm))
                yield return norm;
        }
    }

    /// Archipelago's uninstall registry key (HKLM/HKCU, 64- and 32-bit views)
    /// records "InstallLocation". Best effort, never throws. READ-ONLY.
    private static string? ReadApInstallLocationFromRegistry()
    {
        // The AP installer (Inno Setup) writes an Uninstall key; the exact key name
        // varies, so scan the standard Uninstall roots for a Publisher/DisplayName
        // that looks like Archipelago and return its InstallLocation.
        foreach (var (hive, subPath) in new[]
        {
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        })
        {
            try
            {
                using var root = hive.OpenSubKey(subPath);
                if (root == null) continue;
                foreach (string sub in root.GetSubKeyNames())
                {
                    try
                    {
                        using var k = root.OpenSubKey(sub);
                        if (k == null) continue;
                        string disp = (k.GetValue("DisplayName") as string ?? "");
                        string pub  = (k.GetValue("Publisher")   as string ?? "");
                        if (disp.IndexOf("Archipelago", StringComparison.OrdinalIgnoreCase) < 0 &&
                            pub.IndexOf("Archipelago",  StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        string loc = (k.GetValue("InstallLocation") as string ?? "");
                        if (!string.IsNullOrWhiteSpace(loc)) return loc;
                    }
                    catch { /* skip this sub-key */ }
                }
            }
            catch { /* hive/key unavailable — skip */ }
        }
        return null;
    }

    /// The resolved bundled client exe, or null. READ-ONLY discovery.
    private string? ResolveClientExe()
    {
        string? dir = ResolveApInstallDir();
        if (dir == null) return null;
        try { return FindClientExeIn(dir); }
        catch { return null; }
    }

    /// Find the SC2 client (or fallback launcher) exe in `dir`: prefer the exact
    /// "ArchipelagoStarcraft2Client.exe", else a fuzzy "*starcraft2*client*"/
    /// "*sc2*client*" exe, else (last resort) ArchipelagoLauncher.exe (which can
    /// open the client by name). READ-ONLY — only enumerates, never writes.
    private static string? FindClientExeIn(string dir)
    {
        if (!Directory.Exists(dir)) return null;

        string preferred = Path.Combine(dir, PreferredClientExe);
        if (File.Exists(preferred)) return preferred;

        string[] exes;
        try { exes = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly); }
        catch { return null; }

        // Fuzzy SC2 client exe.
        foreach (string exe in exes)
        {
            string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
            bool isClient = name.Contains("client");
            bool isSc2    = name.Contains("starcraft2") || name.Contains("starcraft 2") ||
                            name.Contains("sc2");
            if (isClient && isSc2) return exe;
        }

        // Last resort: the generic AP launcher (opens the client by component name).
        string launcher = Path.Combine(dir, ApLauncherExe);
        if (File.Exists(launcher)) return launcher;

        return null;
    }

    /// True when the resolved exe is the generic AP launcher (so LaunchClient knows
    /// to pass the component name) rather than the dedicated SC2 client.
    private static bool IsApLauncher(string exePath)
        => string.Equals(Path.GetFileName(exePath), ApLauncherExe,
                          StringComparison.OrdinalIgnoreCase);

    // ── Private helpers — StarCraft II detection (informational) ──────────────

    /// Best-effort detection of StarCraft II. Returns a representative path (the
    /// install dir, or the user's Documents\StarCraft II folder as a soft positive)
    /// or null. INFORMATIONAL only — the client, not the launcher, drives the game.
    /// Never throws. Treats nothing as writable.
    private static string? DetectStarCraft2()
    {
        // 1. Registry: Blizzard records the install under its uninstall keys. The
        //    value names/locations vary by version, so scan for a StarCraft II
        //    DisplayName and read InstallLocation.
        try
        {
            string? install = ReadStarCraft2InstallFromRegistry();
            if (!string.IsNullOrWhiteSpace(install) && Directory.Exists(install))
                return install;
        }
        catch { /* fall through to Documents */ }

        // 2. The user content folder (Maps live under here). Its presence is a soft
        //    positive — SC2 creates Documents\StarCraft II on first run.
        try
        {
            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string sc2  = Path.Combine(docs, Sc2DocumentsFolderName);
            if (Directory.Exists(sc2)) return sc2;
        }
        catch { /* no Documents — null */ }

        return null;
    }

    /// Scan the standard uninstall registry roots for a StarCraft II entry and
    /// return its InstallLocation. Best effort, READ-ONLY, never throws.
    private static string? ReadStarCraft2InstallFromRegistry()
    {
        foreach (var (hive, subPath) in new[]
        {
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
        })
        {
            try
            {
                using var root = hive.OpenSubKey(subPath);
                if (root == null) continue;
                foreach (string sub in root.GetSubKeyNames())
                {
                    try
                    {
                        using var k = root.OpenSubKey(sub);
                        if (k == null) continue;
                        string disp = (k.GetValue("DisplayName") as string ?? "");
                        // Match "StarCraft II" but not the editor or other SC entries
                        // we don't care about; the base game's key is enough.
                        if (disp.IndexOf("StarCraft II", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        string loc = (k.GetValue("InstallLocation") as string ?? "");
                        if (!string.IsNullOrWhiteSpace(loc)) return loc;
                    }
                    catch { /* skip this sub-key */ }
                }
            }
            catch { /* hive/key unavailable — skip */ }
        }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Launch the bundled Starcraft 2 Client. If only the generic AP launcher was
    /// resolved, pass the component name so it opens the SC2 client. If nothing was
    /// located, throw with honest guidance (so the UI surfaces the real next step).
    /// Sets WorkingDirectory to the AP folder but NEVER writes into it.
    private void LaunchClient()
    {
        string? exe = ResolveClientExe();
        if (exe == null)
            throw new FileNotFoundException(
                "The Starcraft 2 Client was not found. Install the latest Archipelago " +
                "release (it bundles \"" + PreferredClientExe + "\") and StarCraft II " +
                "(free from Blizzard), then point this plugin at your Archipelago folder " +
                "in Settings. The first time, run /download_data in the client and " +
                "/connect from there.",
                PreferredClientExe);

        var psi = new ProcessStartInfo
        {
            FileName         = exe,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            UseShellExecute  = false,
        };
        // If we only found ArchipelagoLauncher.exe, ask it to open the SC2 client by
        // its component name (the documented launch argument).
        if (IsApLauncher(exe))
            psi.Arguments = Quote(ClientComponentName);

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the Starcraft 2 Client.");

        _clientProcess = proc;
        IsRunning      = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    /// Open a URL / URI with the shell. Returns true on apparent success. Never
    /// throws. Used for links and the Battle.net standalone-launch attempt.
    private static bool TryOpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }

    /// Quote an argument for a Windows command line (wrap in double quotes and
    /// escape embedded quotes). Plain tokens are returned unquoted.
    private static string Quote(string value)
    {
        if (string.IsNullOrEmpty(value)) return "\"\"";
        bool needs = value.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0;
        if (!needs) return value;
        return "\"" + value.Replace("\"", "\\\"") + "\"";
    }

    /// Path.Combine that never throws (returns null on bad/empty inputs) so the
    /// candidate enumerator can stay a clean iterator.
    private static string? SafeCombine(string? a, string b)
    {
        if (string.IsNullOrWhiteSpace(a)) return null;
        try { return Path.Combine(a, b); }
        catch { return null; }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // Kept in this plugin's OWN JSON file (does not modify Core/SettingsStore).
    // BOM-less UTF-8. This is the launcher's own folder, NOT the AP install dir.

    private sealed class StarCraft2Settings
    {
        /// The Archipelago install folder the user pointed us at (so the chosen
        /// directory survives across launcher restarts). READ-ONLY at use time.
        public string? ApDir { get; set; }
    }

    private StarCraft2Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<StarCraft2Settings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(StarCraft2Settings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — the setting just won't persist this time */ }
    }

    private void SaveApDir(string dir)
    {
        var s = LoadSettings();
        s.ApDir = dir;
        SaveSettings(s);
    }
}
