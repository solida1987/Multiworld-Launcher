using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

// NOTE on type qualification (BUILD GOTCHA — CS0104 / CS1537):
// The real launcher project sets BOTH <UseWPF>true</UseWPF> and
// <UseWindowsForms>true</UseWindowsForms>. That makes a long list of simple type
// names ambiguous between WPF and WinForms (Clipboard, MessageBox, Application,
// Color, Brush(es), Button, TextBox, CheckBox, Orientation, FontWeights,
// HorizontalAlignment, Cursors, Thickness, OpenFileDialog, …). To avoid CS0104
// this file deliberately does NOT do `using System.Windows;` /
// `using System.Windows.Controls;` / `using System.Windows.Media;` — every WPF UI
// type below is written fully qualified (System.Windows.Controls.*,
// System.Windows.Media.*, System.Windows.MessageBox, …). GlobalUsings.cs already
// aliases the colliding short names for the main build, so this file also does NOT
// declare any file-level `using X = System.Windows...;` alias (that would be
// CS1537, a duplicate alias).

namespace LauncherV2.Plugins.OldSchoolRunescape;

// ═══════════════════════════════════════════════════════════════════════════════
// OldSchoolRunescapePlugin — detect / install-guide / launch for "Old School
// RuneScape" (Jagex) played through the Archipelago Randomizer RuneLite plugin by
// digiholic. This is a NATIVE "ConnectsItself" integration in the same family as
// the shipped Lingo / Subnautica / Hollow Knight / Stardew Valley plugins — the
// RuneLite client (via the plugin) speaks to the AP server itself (no emulator, no
// Lua bridge, no launcher-held ApClient on the slot).
//
// HONESTY NOTE — this is a GUIDED case, and a deliberately MORE guided one than the
// Steam-mod games, because the client here is a JAVA RuneLite plugin installed from
// the RuneLite Plugin Hub. Reliable deep automation of a Plugin-Hub install is not
// possible from a .NET launcher (RuneLite manages its own plugin downloads, signing
// and config internally, and the install is a one-click action inside RuneLite's own
// Plugin Hub UI). So this plugin DETECTS RuneLite, GUIDES the in-client Plugin Hub
// install, LAUNCHES RuneLite, and the player CONNECTS from the plugin's own panel
// in-client. It never fabricates an install it cannot perform.
//
// ── REALITY CHECK (2026-06-14, verified online + against AP-main) ──────────────
//
//   * THE AP WORLD game string is "Old School Runescape" — VERIFIED against
//     worlds/osrs/__init__.py (OSRSWorld.game = "Old School Runescape"). OSRS is a
//     CORE Archipelago world: it ships INSIDE Archipelago itself (worlds/osrs), so
//     NO custom .apworld drop is needed. World id "osrs"; the official web tutorial
//     ("setup/en") is authored by digiholic. GameId here = "osrs".
//
//   * THE CLIENT IS A RUNELITE PLUGIN, NOT a standalone exe and NOT a mod injected
//     into the game. RuneLite is the popular third-party Java OSRS client
//     (https://runelite.net/). The AP integration is published by digiholic as the
//     "Archipelago" / "Archipelago Randomizer" plugin and is distributed through the
//     RUNELITE PLUGIN HUB (verified — its README: "install this plugin through the
//     Runelite plugin hub", and the official AP setup guide lists it as the
//     "Archipelago Plugin", https://github.com/digiholic/osrs-archipelago). The
//     setup guide ALSO requires the companion "Region Locker" plugin (only the
//     "Region Locker" plugin itself is required; its optional GPU sub-plugin is not).
//     There is NO sideloaded jar in the documented path — the Plugin Hub is the
//     install mechanism, which the launcher cannot drive programmatically.
//
//   * CONNECTION IS MADE IN-CLIENT (verified — plugin README + setup guide). The
//     plugin's Config panel exposes: Server Address, Server Port (default 38281),
//     Slot Name, and Server Password (plus a "Display AP Messages in Chat" toggle
//     and an "Auto Reconnect on Login For" field that is left BLANK — it is filled
//     with the character name you first connect with). You then open the
//     "Archipelago" side panel in RuneLite and press Connect while logged in to a
//     game world. There is NO command-line arg and NO config file this .NET launcher
//     can pre-write into RuneLite's Java/plugin config — so this plugin does NOT
//     attempt a connection prefill. The settings panel and post-launch note surface
//     the session's host/port/slot so the user can copy them into those in-client
//     fields. ConnectsItself = true: the launcher must NOT hold its own ApClient on
//     the slot while the RuneLite plugin is connected.
//
//   * ACCOUNT / BASE GAME: OSRS is FREE to play; RuneLite is free. The randomizer
//     "assumes you are playing on a newly created f2p Ironman account" (verified
//     setup guide), so the player creates a fresh F2P Ironman before connecting.
//     The launcher never ships or recreates Jagex's game or RuneLite — it only
//     detects and launches an existing RuneLite install and links out to get both.
//
// ── WHAT THIS PLUGIN HONESTLY DOES ─────────────────────────────────────────────
//   1. DETECT RuneLite on Windows by its well-known install/exe locations:
//        - the launcher exe at %LOCALAPPDATA%\RuneLite\RuneLite.exe (the standard
//          per-user install), with Program Files variants as fallbacks;
//        - the RuneLite user/config dir at %USERPROFILE%\.runelite (proves RuneLite
//          has been run, even if the exe lives somewhere non-standard);
//        - an optional user-set OVERRIDE pointing at RuneLite.exe (validated, must
//          be a runelite-named exe), persisted in this plugin's OWN sidecar
//          (Games/ROMs/osrs/osrs_launcher.json) — Core/SettingsStore is NOT modified.
//      "Installed" here means "RuneLite is present" (the AP client is a Plugin-Hub
//      plugin the launcher cannot positively verify is enabled from outside).
//   2. GUIDE the irreducible steps in InstallOrUpdate / Settings: get RuneLite, then
//      inside RuneLite open the Plugin Hub and add the "Archipelago" plugin and the
//      "Region Locker" plugin, create an F2P Ironman, and connect from the plugin's
//      in-client panel — with direct links (RuneLite, the plugin repo, the official
//      AP setup guide, archipelago.gg).
//   3. LAUNCH RuneLite (resolved exe) best-effort so the player gets one click; it
//      can NOT prefill the connection (Java client / Plugin-Hub config). The note
//      surfaces the session host/port/slot to copy into the plugin's Config fields.
//   4. NEVER claim the AP plugin is installed when it cannot be verified, never
//      modify the RuneLite install (§11), and keep its one setting in its OWN JSON
//      sidecar (does not touch Core/SettingsStore).
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", Lingo/Subnautica-style) ────
//   * RuneLite ships through several channels (the official .exe installer to
//     %LOCALAPPDATA%\RuneLite, the Jagex Launcher, and Java/jar runs). The exe
//     resolver prefers %LOCALAPPDATA%\RuneLite\RuneLite.exe and falls back to
//     Program Files locations and a "*runelite*.exe" scan; if no exe is found but
//     %USERPROFILE%\.runelite exists (RuneLite has been run via another channel),
//     the launcher still reports "found" and the user launches RuneLite their usual
//     way. The exact Jagex-Launcher exe layout was not inspected offline, so the
//     Jagex case degrades to guidance rather than a hard launch claim.
//   * Whether the AP plugin + Region Locker are actually installed/enabled inside
//     RuneLite CANNOT be verified from outside RuneLite (Plugin Hub state lives in
//     RuneLite's signed plugin store / config), so the Settings panel is explicit
//     that those Plugin-Hub steps are done in-client and are not verified here.
//   * No plaintext AP password is ever written to disk by this plugin (the
//     connection is entered in-client), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class OldSchoolRunescapePlugin : IGamePlugin
{
    // ── Constants — RuneLite (the Java OSRS client) ────────────────────────────

    /// The RuneLite launcher exe name (the standard Windows install).
    private const string PreferredExeName = "RuneLite.exe";

    /// The RuneLite per-user config / data dir name (under %USERPROFILE%). Its
    /// presence proves RuneLite has been run even when the exe is non-standard.
    private const string RuneLiteUserDirName = ".runelite";

    // ── Constants — the AP OSRS RuneLite plugin (real source, verified) ────────

    /// digiholic's RuneLite plugin repo ("Archipelago" / "Archipelago Randomizer").
    private const string PluginRepoUrl  = "https://github.com/digiholic/osrs-archipelago";

    /// Official Archipelago "Old School Runescape" resources.
    private const string SetupGuideUrl  =
        "https://archipelago.gg/tutorial/Old%20School%20Runescape/setup_en";
    private const string PlayerOptionsUrl =
        "https://archipelago.gg/games/Old%20School%20Runescape/player-options";
    private const string GameInfoUrl =
        "https://archipelago.gg/games/Old%20School%20Runescape/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    /// RuneLite — the client the AP plugin runs inside.
    private const string RuneLiteSite        = "https://runelite.net/";
    private const string RuneLitePluginHubUrl =
        "https://github.com/runelite/plugin-hub";

    /// GitHub releases API for the plugin repo (news feed source — best effort).
    private const string PluginReleasesApiUrl =
        "https://api.github.com/repos/digiholic/osrs-archipelago/releases";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "osrs";
    public string DisplayName => "Old School RuneScape";
    public string Subtitle    => "Native PC · Archipelago (RuneLite)";

    /// EXACT AP game string — VERIFIED against worlds/osrs/__init__.py
    /// (OSRSWorld.game = "Old School Runescape"). OSRS ships inside Archipelago, so
    /// no custom .apworld drop is required.
    public string ApWorldName => "Old School Runescape";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "osrs.png");

    public string ThemeAccentColor => "#C8A032";   // OSRS gold / parchment hue
    public string[] GameBadges     => new[] { "Requires RuneLite" };

    public string Description =>
        "Old School RuneScape is Jagex's classic MMORPG — the 2007-era RuneScape, " +
        "kept alive and free to play. This is the Old School Runescape Randomizer, " +
        "an Archipelago integration delivered as a plugin for RuneLite (the popular " +
        "free third-party OSRS client). Skills, areas and unlocks are shuffled into " +
        "the multiworld, and RuneLite connects to the Archipelago server itself from " +
        "the plugin's in-client panel — no emulator, no bridge. OSRS and RuneLite " +
        "are both free: you install RuneLite, add the \"Archipelago\" and \"Region " +
        "Locker\" plugins from the RuneLite Plugin Hub, make a new free-to-play " +
        "Ironman account, and connect to your server from the Archipelago panel " +
        "(server address, port, slot name and password). The launcher detects " +
        "RuneLite, guides the plugin setup, and can launch RuneLite for you.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    // The AP integration is a RuneLite Plugin-Hub plugin: RuneLite manages and
    // versions it internally, and the launcher cannot positively read that version
    // from outside RuneLite. There is no independent version stamp this plugin can
    // author honestly, so these stay null (the news feed surfaces plugin-repo
    // release versions instead).
    public string? InstalledVersion => null;
    public string? AvailableVersion => null;

    /// "Installed" == RuneLite is present (the AP client is a Plugin-Hub plugin we
    /// cannot positively verify from outside RuneLite — the Settings panel makes the
    /// remaining in-client steps explicit).
    public bool IsInstalled => ResolveRuneLiteExe() != null || RuneLiteUserDirExists();

    public bool IsRunning { get; private set; }

    /// Reports the resolved RuneLite folder when known, else "" (interface contract).
    public string GameDirectory
    {
        get
        {
            string? exe = ResolveRuneLiteExe();
            if (exe != null) return Path.GetDirectoryName(exe) ?? "";
            string userDir = RuneLiteUserDir;
            return Directory.Exists(userDir) ? userDir : "";
        }
    }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained source file — same approach as the
    /// Lingo / Subnautica / Undertale plugins). BOM-less UTF-8, read-modify-write.
    /// Per the convention, lives under Games/ROMs/osrs/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "osrs_launcher.json");

    /// %USERPROFILE%\.runelite — RuneLite's per-user config/data dir.
    private static string RuneLiteUserDir
    {
        get
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(profile, RuneLiteUserDirName);
        }
    }

    /// User-set override pointing at RuneLite.exe (for non-standard installs).
    /// Optional; when unset the plugin auto-detects.
    private string? _overrideExe;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The OSRS Archipelago RuneLite plugin reports checks/items/goal to the AP
    // server itself — the launcher relays nothing. These exist only for interface
    // compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Constructor — restore a previously chosen RuneLite.exe override ────────

    public OldSchoolRunescapePlugin()
    {
        try
        {
            string? saved = LoadSettings().RuneLiteExe;
            if (!string.IsNullOrWhiteSpace(saved) && File.Exists(saved))
                _overrideExe = saved;
        }
        catch { /* fall back to detection only */ }
    }

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────
    // No independent version to compare (the AP client is a RuneLite Plugin-Hub
    // plugin RuneLite manages internally). Contract: never throw on network failure.

    public Task CheckForUpdateAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────
    // HONEST guided setup. There is nothing for the launcher to download here: OSRS
    // and RuneLite are free third-party software (the player installs RuneLite from
    // runelite.net), and the AP client is a RuneLite Plugin-Hub plugin that RuneLite
    // installs with one click inside its own UI. So this reports the current
    // detection state and the exact remaining in-client steps — it never fabricates
    // an install.

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((10, "Checking for RuneLite..."));

        string? exe     = ResolveRuneLiteExe();
        bool    userDir = RuneLiteUserDirExists();

        if (exe != null || userDir)
        {
            string where = exe != null
                ? $"RuneLite was detected at \"{exe}\". "
                : $"RuneLite's data folder was found (\"{RuneLiteUserDir}\"), so RuneLite " +
                  "is installed. ";

            progress.Report((100,
                where +
                "To finish setup, do these steps INSIDE RuneLite (the launcher cannot do " +
                "them for you): 1) open the Plugin Hub (wrench icon -> plug icon) and add " +
                "the \"Archipelago\" plugin and the \"Region Locker\" plugin. 2) Create a " +
                "new free-to-play Ironman account. 3) In the Archipelago plugin's Config, " +
                "enter your server address, port, slot name and password (leave \"Auto " +
                "Reconnect on Login For\" blank). 4) Log in to a world, open the Archipelago " +
                "side panel, and press Connect. See the Setup Guide link in Settings."));
            return Task.CompletedTask;
        }

        // RuneLite not detected — link out and give the exact steps.
        progress.Report((100,
            "RuneLite was not detected. Old School RuneScape's Archipelago integration " +
            "runs as a plugin inside RuneLite (a free third-party OSRS client). 1) Install " +
            "RuneLite from runelite.net (link in Settings). 2) Run RuneLite once, then open " +
            "the Plugin Hub (wrench icon -> plug icon) and add the \"Archipelago\" plugin " +
            "and the \"Region Locker\" plugin. 3) Create a free-to-play Ironman account. " +
            "4) In the Archipelago plugin's Config enter your server address, port, slot " +
            "name and password, then open the Archipelago side panel and press Connect. If " +
            "RuneLite is installed in a non-standard place, set its location in Settings. " +
            "See the Setup Guide link in Settings."));
        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public Task<bool> VerifyInstallAsync(CancellationToken ct = default)
        => Task.FromResult(IsInstalled);

    // ── AutoMod-style validation of a user-picked RuneLite location ────────────

    /// The user located their RuneLite install. Accept it when the folder contains a
    /// runelite-named exe (or IS such an exe's folder). Returns null when acceptable,
    /// else a short reason so they can pick again.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick the folder that contains " +
                   "RuneLite.exe (usually %LOCALAPPDATA%\\RuneLite).";

        try
        {
            if (FindRuneLiteExeIn(folder) != null)
                return null;
        }
        catch
        {
            return "Could not read that folder. Pick a different one and try again.";
        }

        return "That folder does not contain RuneLite.exe. Pick your RuneLite install " +
               "folder (usually %LOCALAPPDATA%\\RuneLite), or install RuneLite from " +
               "runelite.net first.";
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────
    // Best effort: open RuneLite so the player gets one-click launch. The AP
    // connection itself is entered into the plugin's in-client Config / Archipelago
    // panel (see header), so we do NOT pass connection args and we do NOT hold an
    // ApClient on the slot (ConnectsItself = true). If no RuneLite exe is found we
    // fail with honest guidance.

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: the AP server connection for OSRS is entered IN-CLIENT (RuneLite ->
        // Archipelago plugin Config: server address / port / slot name / password,
        // then the Archipelago side panel -> Connect). RuneLite is a Java client and
        // the plugin's settings live in RuneLite's own config, so there is no
        // command-line / config prefill this .NET launcher can apply (verified — see
        // header). Launching just starts RuneLite; the user connects in-client with
        // the session credentials (the settings panel surfaces those).
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the RuneLite plugin is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartRuneLite();
        return Task.CompletedTask;
    }

    /// OSRS via RuneLite runs perfectly well without Archipelago (it is just the
    /// normal game client). Launching without the plugin connected simply means
    /// nothing connects to a multiworld.
    public bool SupportsStandalone => true;

    /// The OSRS Archipelago RuneLite plugin owns the slot connection (see header).
    /// The launcher must NOT connect its own ApClient to the same slot while the
    /// plugin is connected.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartRuneLite();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        // We only ever started RuneLite from here. Kill what we launched (NOT the AP
        // server).
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No plaintext AP password is ever written to disk by this plugin (the
        // connection is entered in-client), so there is nothing to scrub.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The OSRS Archipelago RuneLite plugin receives items from the AP server
        // directly; there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The RuneLite plugin renders its own AP connection state in its in-client
        // panel; no launcher HUD channel into RuneLite.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));

        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Old School RuneScape's Archipelago support is a plugin for RuneLite " +
                   "(a free third-party OSRS client), installed from the RuneLite Plugin " +
                   "Hub — not a one-click download here. The launcher detects RuneLite and " +
                   "can launch it, but you add the \"Archipelago\" and \"Region Locker\" " +
                   "plugins and connect to your server INSIDE RuneLite (the plugin's panel) " +
                   "— there is no connection file the launcher can pre-fill. These in-client " +
                   "steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: RuneLite detection / override ────────────────────────
        panel.Children.Add(SectionHeader("RUNELITE (THE OSRS CLIENT)", muted));

        string? exe     = ResolveRuneLiteExe();
        bool    userDir = RuneLiteUserDirExists();
        string  detectMsg = exe != null
            ? "✓ Detected RuneLite:\n" + exe
            : (userDir
                ? "✓ RuneLite data folder found (RuneLite is installed):\n" + RuneLiteUserDir +
                  "\nIf you want one-click launch, set RuneLite.exe below."
                : "RuneLite not detected. Install it from runelite.net, or set its " +
                  "location below if it is installed in a non-standard place.");
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = (exe != null || userDir) ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var fileRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var fileBox = new System.Windows.Controls.TextBox
        {
            Text = _overrideExe ?? exe ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your RuneLite.exe (usually %LOCALAPPDATA%\\RuneLite\\RuneLite.exe). " +
                          "Detected automatically; set it here to override (non-standard " +
                          "install or the Jagex Launcher).",
        };
        var fileBtn = new System.Windows.Controls.Button
        {
            Content = "Select RuneLite.exe...", Width = 160, Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        var statusText = new System.Windows.Controls.TextBlock
        {
            FontSize = 11, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        };
        void RefreshStatus()
        {
            bool ok = ResolveRuneLiteExe() != null;
            statusText.Text = ok
                ? "✓ RuneLite.exe set — ready to launch."
                : (RuneLiteUserDirExists()
                    ? "RuneLite is installed but no exe is set for one-click launch. Pick " +
                      "RuneLite.exe, or just launch RuneLite your usual way."
                    : "RuneLite.exe not set yet. Install RuneLite from runelite.net, then " +
                      "pick RuneLite.exe here.");
            statusText.Foreground = ok ? success : muted;
        }
        RefreshStatus();

        fileBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select RuneLite.exe",
                Filter = "RuneLite (RuneLite.exe)|RuneLite.exe|Executables (*.exe)|*.exe|All files (*.*)|*.*",
                InitialDirectory = GuessRuneLiteInitialDir(),
            };
            if (dlg.ShowDialog() == true)
            {
                string picked    = dlg.FileName;
                string? pickedDir = Path.GetDirectoryName(picked);
                string? bad      = pickedDir != null ? ValidateExistingInstall(pickedDir) : "Invalid path.";
                if (bad != null && FindRuneLiteExeIn(pickedDir ?? "") == null)
                {
                    var go = System.Windows.MessageBox.Show(
                        bad + "\n\nUse this file anyway?",
                        "Not a RuneLite folder",
                        System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
                    if (go != System.Windows.MessageBoxResult.Yes) return;
                }
                _overrideExe = picked;
                fileBox.Text = picked;
                SaveRuneLiteExe(picked);
                RefreshStatus();
            }
        };
        System.Windows.Controls.DockPanel.SetDock(fileBtn, System.Windows.Controls.Dock.Right);
        fileRow.Children.Add(fileBtn);
        fileRow.Children.Add(fileBox);
        panel.Children.Add(fileRow);
        panel.Children.Add(statusText);

        // ── Section: add the AP plugin (in-client) ────────────────────────
        panel.Children.Add(SectionHeader("ADD THE ARCHIPELAGO PLUGIN (IN RUNELITE)", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Inside RuneLite, click the wrench icon, then the plug icon to open the " +
                   "Plugin Hub. Install the \"Archipelago\" plugin and the \"Region Locker\" " +
                   "plugin (only the \"Region Locker\" plugin itself is required; its GPU " +
                   "sub-plugin is optional and can clash with other GPU plugins). The " +
                   "launcher cannot do this for you — RuneLite manages Plugin-Hub installs " +
                   "in its own UI.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Connection (entered in-client) ───────────────────────
        panel.Children.Add(SectionHeader("CONNECTING (entered in RuneLite)", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "In the Archipelago plugin's Config, enter your Server Address (e.g. " +
                   "archipelago.gg), Server Port (e.g. 38281), Slot Name, and Server " +
                   "Password (if any). Leave \"Auto Reconnect on Login For\" blank — it " +
                   "fills with the character name you first connect with. Then log in to a " +
                   "game world, open the Archipelago side panel, and press Connect. The " +
                   "launcher cannot pre-fill these — they are entered in RuneLite.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(SectionHeader("GUIDED SETUP", muted));
        foreach (string step in new[]
        {
            "1. Install RuneLite (free) from runelite.net. Run it once. Use \"Select " +
                "RuneLite.exe...\" above if it was not auto-detected.",
            "2. In RuneLite, open the Plugin Hub (wrench icon -> plug icon) and add the " +
                "\"Archipelago\" plugin and the \"Region Locker\" plugin.",
            "3. Create a new free-to-play Ironman account (the randomizer assumes a fresh " +
                "F2P Ironman).",
            "4. In the Archipelago plugin's Config, enter your server address, port, slot " +
                "name and password (leave \"Auto Reconnect on Login For\" blank).",
            "5. Launch RuneLite (from here or normally), log in to a game world, open the " +
                "Archipelago side panel, and press Connect.",
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
        foreach (var (label, url) in new[]
        {
            ("RuneLite (download) ↗",               RuneLiteSite),
            ("Archipelago plugin (GitHub) ↗",       PluginRepoUrl),
            ("Old School Runescape Setup Guide ↗",  SetupGuideUrl),
            ("Player Options (YAML) ↗",             PlayerOptionsUrl),
            ("Game Info ↗",                          GameInfoUrl),
            ("RuneLite Plugin Hub ↗",                RuneLitePluginHubUrl),
            ("Archipelago Official ↗",               ArchipelagoSite),
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

    private static System.Windows.Controls.TextBlock SectionHeader(
        string text, System.Windows.Media.Brush muted)
        => new System.Windows.Controls.TextBlock
        {
            Text = text, FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        };

    // ── News feed ─────────────────────────────────────────────────────────────
    // The AP integration is a RuneLite Plugin-Hub plugin; the most honest "news" is
    // the plugin repo's GitHub release stream. Never throws — empty on failure.

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(PluginReleasesApiUrl, ct);
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

    // ── Private helpers — RuneLite detection ───────────────────────────────────

    /// The RuneLite.exe to use: the override (if set and still present) wins, else
    /// the well-known install locations. Null when no exe is found (RuneLite may
    /// still be installed via another channel — see RuneLiteUserDirExists()).
    private string? ResolveRuneLiteExe()
    {
        if (!string.IsNullOrWhiteSpace(_overrideExe) && File.Exists(_overrideExe))
            return _overrideExe;

        try
        {
            foreach (string dir in CandidateRuneLiteDirs())
            {
                string? found = FindRuneLiteExeIn(dir);
                if (found != null) return found;
            }
        }
        catch { /* file access failed — null */ }
        return null;
    }

    /// Well-known RuneLite install folders, most-likely first:
    ///   1. %LOCALAPPDATA%\RuneLite  (the standard per-user install)
    ///   2. %ProgramFiles%\RuneLite, %ProgramFiles(x86)%\RuneLite (alt installs)
    /// De-duplicated; only existing folders are yielded.
    private static IEnumerable<string> CandidateRuneLiteDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string? baseDir in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        })
        {
            if (string.IsNullOrWhiteSpace(baseDir)) continue;
            string dir = Path.Combine(baseDir, "RuneLite");
            if (Directory.Exists(dir) && seen.Add(dir))
                yield return dir;
        }
    }

    /// Find RuneLite.exe in `dir`: prefer the exact "RuneLite.exe", else a fuzzy
    /// "*runelite*.exe" (excluding obvious helpers/uninstallers). Top-level only —
    /// RuneLite's launcher exe sits at the install root.
    private static string? FindRuneLiteExeIn(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return null;

        string preferred = Path.Combine(dir, PreferredExeName);
        if (File.Exists(preferred)) return preferred;

        string[] exes;
        try { exes = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly); }
        catch { return null; }

        foreach (string exe in exes)
        {
            string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
            if (IsHelperExe(name)) continue;
            if (name.Contains("runelite")) return exe;
        }
        return null;
    }

    /// Names that are NOT the runnable client (uninstaller, installer, helpers).
    private static bool IsHelperExe(string nameLowerNoExt)
        => nameLowerNoExt.Contains("unins")    ||
           nameLowerNoExt.Contains("setup")    ||
           nameLowerNoExt.Contains("install")  ||
           nameLowerNoExt.Contains("crash")    ||
           nameLowerNoExt.Contains("vcredist") ||
           nameLowerNoExt.Contains("dxsetup");

    /// True when %USERPROFILE%\.runelite exists — proof RuneLite has been run, even
    /// if its exe lives somewhere this plugin does not scan (e.g. Jagex Launcher).
    private static bool RuneLiteUserDirExists()
    {
        try { return Directory.Exists(RuneLiteUserDir); }
        catch { return false; }
    }

    /// A sensible InitialDirectory for the RuneLite.exe picker.
    private static string GuessRuneLiteInitialDir()
    {
        foreach (string dir in CandidateRuneLiteDirs())
            return dir;
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(local) ? AppContext.BaseDirectory : local;
    }

    // ── Private helpers — launch ───────────────────────────────────────────────

    /// Start RuneLite from the resolved exe. If no exe is found but RuneLite's data
    /// folder exists (installed via another channel), surface honest guidance rather
    /// than failing opaquely; if RuneLite is not found at all, throw with the next
    /// step.
    private void StartRuneLite()
    {
        string? exe = ResolveRuneLiteExe();

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start RuneLite.");

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

        if (RuneLiteUserDirExists())
            throw new FileNotFoundException(
                "RuneLite is installed (its data folder was found) but the launcher could " +
                "not locate RuneLite.exe for one-click launch. Start RuneLite your usual " +
                "way (or the Jagex Launcher), or set RuneLite.exe in this game's Settings. " +
                "Then add the \"Archipelago\" and \"Region Locker\" plugins from the Plugin " +
                "Hub and connect from the Archipelago panel.",
                PreferredExeName);

        throw new FileNotFoundException(
            "RuneLite was not found. Old School RuneScape's Archipelago integration runs " +
            "as a plugin inside RuneLite (free, from runelite.net). Install RuneLite, then " +
            "set its location in this game's Settings if needed. After that, add the " +
            "\"Archipelago\" and \"Region Locker\" plugins from the RuneLite Plugin Hub and " +
            "connect from the Archipelago panel.",
            PreferredExeName);
    }

    // ── Private helpers — self-contained settings sidecar ──────────────────────
    // This plugin keeps its one launcher-side setting (the RuneLite.exe override) in
    // its OWN JSON file so it stays a single self-contained source file and does not
    // modify Core/SettingsStore. BOM-less UTF-8, read-modify-write (same approach as
    // Lingo/Subnautica/Doom/Undertale).

    private sealed class OsrsSettings
    {
        /// The RuneLite.exe the user pointed us at (for non-standard installs), so it
        /// survives launcher restarts.
        public string? RuneLiteExe { get; set; }
    }

    private OsrsSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<OsrsSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(OsrsSettings s)
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

    private void SaveRuneLiteExe(string exe)
    {
        var s = LoadSettings();
        s.RuneLiteExe = exe;
        SaveSettings(s);
    }
}
