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

// NOTE on type qualification (BUILD GOTCHA — verified against this repo):
// LauncherV2.csproj sets BOTH <UseWPF>true</UseWPF> AND <UseWindowsForms>true</UseWindowsForms>.
// That makes a long list of simple type names ambiguous between WPF and WinForms
// (Application, Clipboard, MessageBox, Color, Brush(es), Button, TextBox, CheckBox,
// Orientation, FontWeights, HorizontalAlignment, Cursors, Thickness, …) → CS0104.
// The repo's GlobalUsings.cs aliases the short names project-wide, but this plugin
// is written to compile EVEN WITHOUT GlobalUsings (e.g. the isolated self-verify
// build that omits it), so — exactly like Plugins/Undertale/UndertalePlugin.cs —
// every WPF UI type below is FULLY QUALIFIED (System.Windows.Controls.*,
// System.Windows.Media.*, System.Windows.MessageBox, …) and this file adds NO
// file-level `using X = System.Windows...;` alias (that would collide with the
// GlobalUsings aliases → CS1537). Bare names / fully-qualified only — never a local alias.

namespace LauncherV2.Plugins.DLCQuest;

// ═══════════════════════════════════════════════════════════════════════════════
// DLCQuestPlugin — install-guide / detect / connect-prefill / launch for "DLC Quest"
// (going Loud Games), played through its Archipelago integration "DLCQuestipelago".
//
// WHAT KIND OF INTEGRATION IS THIS? (verified online this session — see REALITY CHECK)
// ─────────────────────────────────────────────────────────────────────────────
// DLC Quest's Archipelago support is a BepInEx (.NET Framework) MOD with its own
// built-in AP client, distributed as a standalone GitHub release that INCLUDES an
// Installer. It is NOT part of AP-main (worlds/dlcquest is the world definition
// only) and it is NOT an emulator/Lua bridge. The pieces are:
//
//   * "DLCQuestipelago" — the mod + its bundled AP client (the
//     KaitoKid.ArchipelagoUtilities.Net client, the same client family the author's
//     Stardew Valley mod uses). It owns the AP slot connection itself.
//   * The player's own copy of DLC Quest (Steam appid 230050 recommended) — the base
//     game the mod patches a COPY of.
//   * A small per-install JSON file, "ArchipelagoConnectionInfo.json", at the root of
//     the modded install, which the mod READS ON STARTUP and auto-connects from.
//
// This is a GUIDED case like Undertale / Ship of Harkinian (the player must own the
// paid base game and run a one-time installer the launcher does not reproduce), BUT
// with one genuine advantage the launcher CAN deliver honestly: because the mod
// connects from a plain JSON file it reads at startup, the launcher can PREFILL that
// file with the current AP session so launching the mod actually auto-connects — no
// separate client window, no in-game typing (unlike Undertale, where the connection
// is entered into a separate client). That prefill is best-effort and DEFENSIVE: the
// exact field names come from the mod's own shipped sample + source (verified), and
// prefill never blocks the launch if it fails.
//
// HONESTY NOTE — the concrete, verified reasons the launcher does NOT do a fake
// "one-click install":
//   1. DLC Quest is PAID software (§11). The launcher must not, and does not, ship or
//      recreate the base game — the player supplies their own copy.
//   2. The OFFICIAL, documented setup is: download the DLCQuestipelago release, run
//      its Installer (it locates your DLC Quest, asks where to install the modded
//      copy, and wires up BepInEx), then run BepInEx.NET.Framework.Launcher.exe. The
//      installer is an interactive tool that copies the player's game files and sets
//      up BepInEx — reproducing that is exactly the bundled installer's job, so (like
//      SoH did not reproduce OTR generation, and Undertale did not reproduce
//      /auto_patch) this plugin does NOT fake it. It GUIDES it and links the release.
//   3. CONNECTION IS MOD-SIDE, NOT launcher-relay. The mod's own AP client holds the
//      slot. AP allows one connection per slot, so the launcher must NOT also sit on
//      the slot while the modded game runs (it would kick the mod, or be kicked).
//      Hence ConnectsItself = true and the launcher's ApClient stays off the slot.
//
// WHAT THIS PLUGIN HONESTLY DOES:
//   * DETECT the player's Steam DLC Quest install (appid 230050) via the standard
//     registry → libraryfolders.vdf → appmanifest_230050.acf → common pipeline (same
//     approach as the Stardew Valley / Undertale plugins), and detect the MODDED copy
//     (the folder containing BepInEx.NET.Framework.Launcher.exe) at a user-set folder,
//     so it can tell the player exactly where things are and light up "ready".
//   * GUIDE the irreducible steps in InstallOrUpdate / Settings (own DLC Quest on
//     Steam, download + run the DLCQuestipelago Installer once, then point this plugin
//     at the modded folder), with direct links (Steam page, mod releases, official
//     setup guide, archipelago.gg).
//   * PREFILL ArchipelagoConnectionInfo.json in the modded folder with the AP session
//     (HostUrl / Port / SlotName / Password / DeathLink) so the mod auto-connects —
//     best-effort, defensive, never blocks launch (see REALITY CHECK for the exact
//     verified schema).
//   * LAUNCH BepInEx.NET.Framework.Launcher.exe from the modded folder so the player
//     gets one-click play.
//   * NEVER claim an install exists when it does not, never modify the player's base
//     copy (§11 — the installer makes its own modded copy), and keep its one setting
//     in its OWN JSON sidecar (does not touch Core/SettingsStore).
//
// REALITY CHECK (2026-06-14) — facts verified this session:
//   * AP game string: "DLCQuest" — VERIFIED in worlds/dlcquest/__init__.py
//     (game = "DLCQuest"). World id "dlcquest". Core-verified game (added in AP 0.4.1).
//   * Mod: "DLCQuestipelago", repo https://github.com/agilbert1412/DLCQuestipelago .
//     Latest release v3.4.0 (NOT a prerelease), assets:
//       "DLCQuestipelago.v3.4.0.-.Installer.zip"  (the Installer — recommended)
//       "DLCQuestipelago.v3.4.0.-.Mod.Only.zip"   (manual, for the comfortable)
//   * Base game: DLC Quest on Steam, appid 230050
//     (store.steampowered.com/app/230050/DLC_Quest/).
//   * Connect: edit "ArchipelagoConnectionInfo.json" at the root of the modded install
//     (verified Persistency.CONNECTION_FILE = "ArchipelagoConnectionInfo.json"); the
//     mod reads it ON STARTUP (Plugin.cs: File.ReadAllText(CONNECTION_FILE) →
//     JsonConvert.DeserializeObject<DLCQuestConnectionInfo>) and auto-connects. The
//     mod's shipped "ArchipelagoConnectionInfo - Release.json" sample is EXACTLY:
//       { "HostUrl": "archipelago.gg", "Port": 38281, "SlotName": "SlotName",
//         "DeathLink": false, "Password": "" }
//     (Newtonsoft, PascalCase keys, matching ArchipelagoConnectionInfo's
//     HostUrl/Port/SlotName/DeathLink/Password.) 3.4.0 added optional fields
//     (TeleportToSpawnKey/EnableEnergyLink/GiftingPreference) that DEFAULT when
//     absent, so writing just the 5 core fields is safe and forward-compatible.
//   * Launch: run "BepInEx.NET.Framework.Launcher.exe" (or the installer-made desktop
//     shortcut); the setup guide states it "should automatically connect".
//
// DEFENSIVE / UNVERIFIED DETAILS (flagged, "verify at build time"):
//   * MODDED-FOLDER LAYOUT: the launcher exe name "BepInEx.NET.Framework.Launcher.exe"
//     is verified from the setup guide, but the exact folder the installer creates is
//     user-chosen, so this plugin asks the user to point at it (Browse) rather than
//     guessing. ResolveLauncherExe() prefers that exact exe name, else any
//     "*BepInEx*Launcher*.exe", else a lone non-helper launcher exe.
//   * CONNECTION-FILE LOCATION: verified to be the install ROOT (the commented-out
//     alternative in Persistency.cs is BepInEx\plugins\DLCQuestipelago\). To be safe,
//     prefill writes the root file AND, if that plugins sub-path exists, mirrors it
//     there too — both best-effort.
//   * IsInstalled is true ONLY when the modded launcher exe is found in the resolved
//     modded folder; having only the Steam BASE game (unmodded) does NOT flip it true.
//   * The launcher cannot verify the user actually ran the Installer beyond "is there
//     a BepInEx launcher exe in the folder you pointed me at"; Settings is explicit
//     about the remaining manual steps.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DLCQuestPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    /// DLC Quest's Steam application id (VERIFIED —
    /// store.steampowered.com/app/230050/DLC_Quest/ and the "magic number" 230050
    /// used throughout the AP setup guide).
    private const string SteamAppId = "230050";

    /// Standard Steam install sub-folder name (steamapps\common\DLC Quest).
    private const string SteamCommonFolderName = "DLC Quest";

    /// The BepInEx (.NET Framework) launcher exe the modded game is started with
    /// (VERIFIED from the setup guide: "Run BepInEx.NET.Framework.Launcher.exe").
    private const string PreferredLauncherExe = "BepInEx.NET.Framework.Launcher.exe";

    /// The mod's connection file, read on startup and auto-connected from
    /// (VERIFIED — Persistency.CONNECTION_FILE = "ArchipelagoConnectionInfo.json",
    /// at the root of the modded install).
    private const string ConnectionFileName = "ArchipelagoConnectionInfo.json";

    /// Official Archipelago "DLCQuest" setup guide.
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/DLCQuest/setup_en";

    /// DLC Quest on Steam (the base game the player must own).
    private const string SteamStoreUrl =
        "https://store.steampowered.com/app/230050/DLC_Quest/";

    /// The DLCQuestipelago mod — releases (Installer + Mod-Only).
    private const string ModRepoUrl =
        "https://github.com/agilbert1412/DLCQuestipelago";
    private const string ModReleasesUrl =
        "https://github.com/agilbert1412/DLCQuestipelago/releases";

    /// GitHub releases API for the mod repo (news feed source).
    private const string ModReleasesApiUrl =
        "https://api.github.com/repos/agilbert1412/DLCQuestipelago/releases";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(2),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "dlcquest";
    public string DisplayName => "DLC Quest";
    public string Subtitle    => "Native PC · Archipelago";

    /// EXACT AP game string — VERIFIED against worlds/dlcquest/__init__.py
    /// (game = "DLCQuest").
    public string ApWorldName => "DLCQuest";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "dlcquest.png");

    public string ThemeAccentColor => "#3A7D2C";   // DLC-Quest coin/grass green
    public string[] GameBadges     => new[] { "Requires DLC Quest on Steam" };

    public string Description =>
        "DLC Quest is going Loud Games' satirical platformer that locks your moves and " +
        "the game's own features behind in-game \"DLC\" packs you buy with coins — a " +
        "parody of paid downloadable content. This is the Archipelago integration via " +
        "the DLCQuestipelago mod: the titular DLC packs (and optionally the coins " +
        "themselves) are shuffled into the multiworld, and the mod's built-in client " +
        "connects to the Archipelago server itself — no emulator, no Lua bridge. You " +
        "bring your own copy of DLC Quest (recommended on Steam); the mod's bundled " +
        "Installer patches a separate copy once. The launcher detects your install, " +
        "guides the one-time setup, prefills the connection, and launches the modded " +
        "game for you.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    // The DLCQuestipelago install is created by the mod's own Installer into a
    // user-chosen folder; there is no version stamp this plugin writes itself when
    // the user runs that external installer, so InstalledVersion stays null and the
    // news feed surfaces the mod's GitHub release versions instead. (This mirrors
    // the Undertale plugin's honesty stance for installer-driven integrations.)
    public string? InstalledVersion => null;
    public string? AvailableVersion => null;

    /// Installed == the modded BepInEx launcher exe is present in the resolved
    /// modded folder (the Steam base game alone is NOT the modded copy).
    public bool IsInstalled => ResolveLauncherExe() != null;
    public bool IsRunning   { get; private set; }

    /// Empty string when not installed (interface contract). Reports the resolved
    /// modded folder when known, else "".
    public string GameDirectory => ResolveModdedDir() ?? "";

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained source file — same approach as the
    /// Undertale / Doom / Saving Princess plugins). BOM-less UTF-8.
    private string SettingsSidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                        "dlcquest_launcher.json");

    private string SettingsSidecarDir => Path.GetDirectoryName(SettingsSidecarPath)!;

    /// User-set override pointing at the MODDED DLCQuestipelago folder (the one the
    /// Installer created, containing BepInEx.NET.Framework.Launcher.exe). Optional;
    /// when unset the plugin only knows about the Steam base install.
    private string? _overrideModdedDir;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The DLCQuestipelago mod's built-in client owns the AP slot and reports
    // checks/items/goal to the server itself. The launcher relays nothing. These
    // exist only for interface compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Constructor — restore a previously chosen modded folder ───────────────

    public DLCQuestPlugin()
    {
        try
        {
            string? saved = LoadSettings().ModdedDir;
            if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
                _overrideModdedDir = saved;
        }
        catch { /* fall back to detection only */ }
    }

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────
    // No independent version stamp to compare (the modded copy is produced by the
    // mod's own Installer). Contract: never throw on network failure.

    public Task CheckForUpdateAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────
    // HONEST guided setup. There is nothing for the launcher to silently install
    // here (DLC Quest is paid, and the modded copy is made by the mod's interactive
    // Installer). So this reports the current detection state and the exact
    // remaining manual steps with links — it never fabricates an install.

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((10, "Checking for your Steam copy of DLC Quest..."));

        string? steamDir  = DetectSteamInstallDir();
        string? moddedDir = ResolveModdedDir();
        bool    modded    = ResolveLauncherExe() != null;

        if (modded)
        {
            progress.Report((100,
                $"DLCQuestipelago found at \"{moddedDir}\". Press Play to launch it — " +
                "the launcher prefills ArchipelagoConnectionInfo.json so the mod " +
                "auto-connects. See the Setup Guide link in Settings."));
            return Task.CompletedTask;
        }

        // Not modded yet — give the most specific guidance we can.
        if (!string.IsNullOrEmpty(steamDir))
        {
            progress.Report((100,
                "Steam DLC Quest detected at \"" + steamDir + "\", but the modded " +
                "DLCQuestipelago copy was not found yet. To finish setup: download the " +
                "DLCQuestipelago release (pick the Installer), run it once — it locates " +
                "your DLC Quest and creates a modded copy where you choose — then point " +
                "this plugin at that modded folder in Settings. See the Setup Guide and " +
                "Mod Releases links in Settings."));
        }
        else
        {
            progress.Report((100,
                "DLC Quest was not detected via Steam. 1) Own DLC Quest (recommended on " +
                "Steam, appid 230050). 2) Download the DLCQuestipelago release and run " +
                "its Installer once to create the modded copy. 3) Point this plugin at " +
                "that modded folder in Settings. See the Setup Guide and Mod Releases " +
                "links in Settings."));
        }
        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public Task<bool> VerifyInstallAsync(CancellationToken ct = default)
        => Task.FromResult(IsInstalled);

    // ── AutoMod-style validation of a user-picked modded folder ───────────────

    /// The user located the folder the Installer created (the DLCQuestipelago modded
    /// copy). Accept it only when it contains the BepInEx launcher exe. Returns null
    /// when acceptable, else a short reason so they can pick again.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick the modded DLC Quest folder that " +
                   "the DLCQuestipelago Installer created.";

        try
        {
            if (FindLauncherExeIn(folder) != null)
                return null;
        }
        catch
        {
            return "Could not read that folder. Pick a different one and try again.";
        }

        return "That folder does not contain the modded game launcher " +
               "(\"" + PreferredLauncherExe + "\"). Run the DLCQuestipelago Installer " +
               "first, then pick the modded folder it created.";
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────
    // Best effort: prefill the connection file (so the mod auto-connects) then open
    // the modded game launcher so the player gets one-click play. The mod's own
    // client holds the AP slot (see header), so we do NOT hold an ApClient on the
    // slot (ConnectsItself = true). Prefill NEVER blocks the launch. If no launcher
    // exe is found, fail with honest guidance.

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // VERIFIED connection path: write ArchipelagoConnectionInfo.json with the AP
        // session; the mod reads it on startup and auto-connects. Defensive — wrapped
        // so a write failure never stops the launch (the player can still edit the
        // file by hand, per the setup guide).
        TryPrefillConnectionInfo(session);
        LaunchModdedGame();
        return Task.CompletedTask;
    }

    /// DLC Quest is a complete game — plain (non-AP) play is possible. (Launching the
    /// modded copy is still AP-oriented, but it runs; the standalone path below
    /// launches it without writing any connection data.)
    public bool SupportsStandalone => true;

    /// The DLCQuestipelago mod's built-in client owns the AP slot connection (see
    /// header). The launcher must NOT connect its own ApClient to the same slot while
    /// the modded game runs, or they would kick each other off.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        // No connection prefill here — just open the game. (If the user wants plain
        // DLC Quest with no multiworld, they can clear/ignore the connection file.)
        LaunchModdedGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;

        // SCRUB the password we wrote into the connection file on launch: rewrite the
        // file with an empty password so a plaintext secret does not linger on disk.
        // Best-effort, never throws.
        TryScrubConnectionPassword();
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The mod's built-in client receives items from the AP server directly and
        // applies them in-game; there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own connection state (BepInEx console / in-game); no
        // launcher HUD channel into the game.
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
            Text = "DLC Quest's Archipelago support is the DLCQuestipelago mod (a " +
                   "BepInEx mod with its own built-in client), not a one-click " +
                   "launcher install. You bring your own copy of DLC Quest (recommended " +
                   "on Steam); the mod's bundled Installer patches a separate copy once, " +
                   "and the mod connects to the multiworld itself. The launcher detects " +
                   "your install, guides the setup, prefills the connection file, and " +
                   "launches the modded game. Details below were verified against the " +
                   "official setup guide and the mod's source, not byte-for-byte offline.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: Steam base game (detected) ───────────────────────────
        string? steamDir = DetectSteamInstallDir();
        panel.Children.Add(SectionHeader("STEAM DLC QUEST (BASE GAME)", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = steamDir != null
                ? "✓ Detected via Steam (appid " + SteamAppId + "):\n" + steamDir
                : "Not detected via Steam. Own DLC Quest on Steam (appid " +
                  SteamAppId + "), or you can still mod it manually if you own it " +
                  "elsewhere.",
            FontSize = 11, Foreground = steamDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Modded DLCQuestipelago copy (override) ───────────────
        panel.Children.Add(SectionHeader("MODDED (DLCQUESTIPELAGO) FOLDER", muted));

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = _overrideModdedDir ?? ResolveModdedDir() ?? "",
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
            bool ok = ResolveLauncherExe() != null;
            statusText.Text = ok
                ? "✓ Modded DLC Quest found here — ready to launch."
                : "Modded game not found here yet. Run the DLCQuestipelago Installer, " +
                  "then point this at the modded folder it created (the one with " +
                  PreferredLauncherExe + ").";
            statusText.Foreground = ok ? success : muted;
        }
        RefreshStatus();

        dirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select your modded DLC Quest (DLCQuestipelago) folder",
                InitialDirectory = Directory.Exists(_overrideModdedDir ?? "")
                    ? _overrideModdedDir!
                    : (steamDir != null && Directory.Exists(steamDir)
                        ? steamDir
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
                        "No modded game found here",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (go != MessageBoxResult.Yes) return;
                }
                _overrideModdedDir = picked;
                dirBox.Text = picked;
                SaveModdedDir(picked);
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
                "1) Own DLC Quest (recommended on Steam, appid " + SteamAppId + ").\n" +
                "2) Download the DLCQuestipelago release and run the Installer once. It " +
                "finds your DLC Quest, lets you choose where to install the modded copy, " +
                "and sets up BepInEx. (Or use \"Mod Only\" if you are comfortable editing " +
                "files manually.)\n" +
                "3) Point the \"Modded (DLCQuestipelago) folder\" above at that modded " +
                "folder (the one containing " + PreferredLauncherExe + ").\n\n" +
                "To play: press Play here. The launcher writes your server, port and slot " +
                "name into ArchipelagoConnectionInfo.json in that folder and starts " +
                PreferredLauncherExe + " — the mod reads the file on startup and " +
                "auto-connects. (You can also edit that file by hand, as the setup guide " +
                "describes. The launcher does not connect to your slot itself — the mod " +
                "does.)",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("DLC Quest on Steam ↗",        SteamStoreUrl),
            ("DLCQuestipelago Releases ↗",  ModReleasesUrl),
            ("DLCQuest Setup Guide ↗",      SetupGuideUrl),
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
            Text = text, FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        };

    // ── News feed ─────────────────────────────────────────────────────────────
    // The most honest "news" is the DLCQuestipelago release stream (the mod + its
    // client come from there). Never throws — empty on failure.

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(ModReleasesApiUrl, ct);
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

    /// "v3.4.0" → "3.4.0"; leading 'v' stripped only when it decorates a digit.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    // ── Private helpers — connection prefill (DEFENSIVE) ──────────────────────

    /// Write ArchipelagoConnectionInfo.json so the mod auto-connects on startup.
    /// VERIFIED schema (the mod's shipped Release sample + ArchipelagoConnectionInfo
    /// source): PascalCase keys HostUrl/Port/SlotName/DeathLink/Password, deserialized
    /// with Newtonsoft. Optional 3.4.0 fields default when absent, so we write only the
    /// five core fields (forward-compatible). Written at the install ROOT (verified
    /// location) and mirrored into BepInEx\plugins\DLCQuestipelago\ only if that path
    /// already exists (the commented-out alternative in the mod). Best-effort —
    /// NEVER throws into the launch.
    private void TryPrefillConnectionInfo(ApSession session)
    {
        try
        {
            string? dir = ResolveModdedDir();
            if (dir == null || !Directory.Exists(dir)) return;

            var (host, port) = ParseServerHostPort(session.ServerUri);

            // Preserve DeathLink if the user already set it in an existing file
            // (we only own host/port/slot/password). Default false otherwise.
            bool deathLink = ReadExistingDeathLink(Path.Combine(dir, ConnectionFileName));

            string json = BuildConnectionJson(host, port, session.SlotName,
                                              session.Password ?? "", deathLink);

            // Root file (verified location).
            WriteAllTextNoBom(Path.Combine(dir, ConnectionFileName), json);

            // Mirror into the BepInEx plugins sub-path ONLY if it already exists.
            string pluginsPath = Path.Combine(dir, "BepInEx", "plugins",
                                              "DLCQuestipelago", ConnectionFileName);
            string? pluginsDir = Path.GetDirectoryName(pluginsPath);
            if (pluginsDir != null && Directory.Exists(pluginsDir))
                WriteAllTextNoBom(pluginsPath, json);
        }
        catch { /* prefill is a convenience — the user can edit the file by hand */ }
    }

    /// Build the exact JSON the mod expects. Keys are written in the verified
    /// PascalCase shape; values are JSON-escaped. We hand-build (rather than rely on a
    /// serializer's naming policy) so the casing is guaranteed to match the mod.
    private static string BuildConnectionJson(
        string host, int port, string slot, string password, bool deathLink)
    {
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("    \"HostUrl\": ").Append(JsonStr(host)).Append(",\n");
        sb.Append("    \"Port\": ").Append(port).Append(",\n");
        sb.Append("    \"SlotName\": ").Append(JsonStr(slot)).Append(",\n");
        sb.Append("    \"DeathLink\": ").Append(deathLink ? "true" : "false").Append(",\n");
        sb.Append("    \"Password\": ").Append(JsonStr(password)).Append("\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// Read an existing connection file's DeathLink flag (so prefill does not stomp a
    /// user's choice). Defaults to false when absent/unreadable.
    private static bool ReadExistingDeathLink(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            string txt = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(txt)) return false;
            using var doc = JsonDocument.Parse(txt);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("DeathLink", out var dl))
            {
                if (dl.ValueKind == JsonValueKind.True)  return true;
                if (dl.ValueKind == JsonValueKind.False) return false;
            }
        }
        catch { /* malformed → default */ }
        return false;
    }

    /// On stop, rewrite the connection file with an empty Password so a plaintext
    /// secret does not linger on disk between sessions. Preserves the other fields.
    /// Best-effort, never throws.
    private void TryScrubConnectionPassword()
    {
        try
        {
            string? dir = ResolveModdedDir();
            if (dir == null) return;

            foreach (string path in new[]
            {
                Path.Combine(dir, ConnectionFileName),
                Path.Combine(dir, "BepInEx", "plugins", "DLCQuestipelago", ConnectionFileName),
            })
            {
                if (!File.Exists(path)) continue;
                ScrubPasswordInFile(path);
            }
        }
        catch { /* scrub is best-effort */ }
    }

    private static void ScrubPasswordInFile(string path)
    {
        try
        {
            string txt = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(txt)) return;

            using var doc = JsonDocument.Parse(txt);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return;

            string  host = GetStr(doc.RootElement, "HostUrl", "archipelago.gg");
            int     port = GetInt(doc.RootElement, "Port", 38281);
            string  slot = GetStr(doc.RootElement, "SlotName", "");
            bool    dl   = doc.RootElement.TryGetProperty("DeathLink", out var d)
                           && d.ValueKind == JsonValueKind.True;

            WriteAllTextNoBom(path, BuildConnectionJson(host, port, slot, "", dl));
        }
        catch { /* leave the file as-is if we cannot safely rewrite it */ }
    }

    private static string GetStr(JsonElement obj, string name, string fallback)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? fallback
            : fallback;

    private static int GetInt(JsonElement obj, string name, int fallback)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
                                              && v.TryGetInt32(out int i)
            ? i
            : fallback;

    /// Minimal JSON string literal (quotes + the standard escapes). Avoids pulling a
    /// serializer in just for a couple of values, and guarantees the exact output.
    private static string JsonStr(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':  sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b");  break;
                case '\f': sb.Append("\\f");  break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                    else          sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static void WriteAllTextNoBom(string path, string contents)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, contents, new UTF8Encoding(false));
    }

    /// Accepts "archipelago.gg:38281", "ws://host:port", "wss://host:port", a bare
    /// hostname, and IPv6 literals (bracketed "[::1]:38281" or bare "::1"). Default AP
    /// port is 38281. (Same parser as the Doom plugin.)
    private static (string Host, int Port) ParseServerHostPort(string serverUri)
    {
        string s = (serverUri ?? "").Trim();
        foreach (string prefix in new[] { "wss://", "ws://", "archipelago://" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..];
                break;
            }
        }

        int slash = s.IndexOf('/');
        if (slash >= 0) s = s[..slash];

        string host = s;
        int    port = 38281;

        int colonCount = 0;
        foreach (char c in s) if (c == ':') colonCount++;

        if (s.StartsWith('['))
        {
            int close = s.IndexOf(']');
            if (close > 0)
            {
                host = s[1..close];
                string rest = s[(close + 1)..];
                if (rest.StartsWith(':') &&
                    int.TryParse(rest[1..], out int p6) && p6 > 0 && p6 <= 65535)
                    port = p6;
            }
        }
        else if (colonCount > 1)
        {
            host = s; // bare IPv6 literal — no port can be carried this way
        }
        else
        {
            int colon = s.LastIndexOf(':');
            if (colon > 0 && int.TryParse(s[(colon + 1)..], out int p) && p > 0 && p <= 65535)
            {
                host = s[..colon];
                port = p;
            }
        }

        if (host.Length == 0) host = "archipelago.gg";
        return (host, port);
    }

    // ── Private helpers — modded-folder / exe resolution ──────────────────────

    /// The resolved MODDED DLCQuestipelago folder: the user override if it still
    /// exists, else null. (We do NOT auto-resolve the Steam base folder here — that
    /// copy is not the modded one.)
    private string? ResolveModdedDir()
    {
        if (!string.IsNullOrWhiteSpace(_overrideModdedDir) &&
            Directory.Exists(_overrideModdedDir))
            return _overrideModdedDir;
        return null;
    }

    /// The modded BepInEx launcher exe inside the resolved modded folder, or null.
    private string? ResolveLauncherExe()
    {
        string? dir = ResolveModdedDir();
        if (dir == null) return null;
        try { return FindLauncherExeIn(dir); }
        catch { return null; }
    }

    /// Find the modded game launcher in `dir`: prefer the exact
    /// "BepInEx.NET.Framework.Launcher.exe", else any "*BepInEx*Launcher*.exe", else
    /// (last resort) a lone non-helper "*launcher*.exe". Searches the folder root and
    /// one level down (the installer's chosen folder usually holds the exe at root).
    private static string? FindLauncherExeIn(string dir)
    {
        if (!Directory.Exists(dir)) return null;

        string preferred = Path.Combine(dir, PreferredLauncherExe);
        if (File.Exists(preferred)) return preferred;

        string[] exes;
        try { exes = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly); }
        catch { return null; }

        // Exact name match anywhere at root already handled; try BepInEx+Launcher.
        foreach (string exe in exes)
        {
            string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
            if (IsHelperExe(name)) continue;
            if (name.Contains("bepinex") && name.Contains("launcher")) return exe;
        }

        // Fuzzy: any launcher exe.
        foreach (string exe in exes)
        {
            string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
            if (IsHelperExe(name)) continue;
            if (name.Contains("launcher")) return exe;
        }

        // One level down (some installers nest the game in a sub-folder).
        try
        {
            foreach (string sub in Directory.GetDirectories(dir))
            {
                string nested = Path.Combine(sub, PreferredLauncherExe);
                if (File.Exists(nested)) return nested;
            }
        }
        catch { /* ignore */ }

        return null;
    }

    /// Names that are NOT the modded game launcher (uninstaller, setup, the mod's own
    /// installer exe, common helpers).
    private static bool IsHelperExe(string nameLowerNoExt)
        => nameLowerNoExt.Contains("unins")     ||
           nameLowerNoExt.Contains("setup")     ||
           nameLowerNoExt.Contains("installer") ||
           nameLowerNoExt.Contains("crash")     ||
           nameLowerNoExt.Contains("vcredist")  ||
           nameLowerNoExt.Contains("dxsetup");

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Launch the modded BepInEx launcher exe. If none is resolved, throw with honest
    /// guidance (so the UI surfaces the real next step, not an opaque crash).
    private void LaunchModdedGame()
    {
        string? exe = ResolveLauncherExe();
        if (exe == null)
            throw new FileNotFoundException(
                "Modded DLC Quest not found. Run the DLCQuestipelago Installer once " +
                "(it makes a modded copy where you choose), then point this plugin at " +
                "that folder in Settings — the one containing " + PreferredLauncherExe +
                ".",
                Path.Combine(ResolveModdedDir() ?? "", PreferredLauncherExe));

        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exe,
            // The mod reads ArchipelagoConnectionInfo.json relative to its working
            // directory; the modded-folder ROOT is where it lives (verified), so run
            // from there regardless of where the exe was resolved.
            WorkingDirectory = ResolveModdedDir() ?? Path.GetDirectoryName(exe) ?? "",
            UseShellExecute  = false,
        }) ?? throw new InvalidOperationException("Failed to start DLC Quest.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — Steam detection (adapted from the Undertale plugin) ──

    /// Best-effort Steam auto-detection of the DLC Quest BASE install:
    ///   1. Steam root from registry (HKCU SteamPath, then HKLM InstallPath).
    ///   2. Library roots from steamapps\libraryfolders.vdf (+ the Steam root).
    ///   3. appmanifest_230050.acf → "installdir" → steamapps\common\<installdir>.
    ///   4. Fall back to steamapps\common\DLC Quest.
    /// Returns the dir (validated to look like DLC Quest) or null. Never throws.
    private static string? DetectSteamInstallDir()
    {
        try
        {
            foreach (string steamRoot in EnumerateSteamRoots())
            {
                foreach (string lib in EnumerateSteamLibraries(steamRoot))
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    if (!Directory.Exists(steamapps)) continue;

                    string acf = Path.Combine(steamapps, $"appmanifest_{SteamAppId}.acf");
                    if (File.Exists(acf))
                    {
                        string? installDirName = ReadVdfValue(acf, "installdir");
                        if (!string.IsNullOrWhiteSpace(installDirName))
                        {
                            string candidate = Path.Combine(steamapps, "common", installDirName!);
                            if (LooksLikeDlcQuest(candidate)) return candidate;
                        }
                    }

                    string std = Path.Combine(steamapps, "common", SteamCommonFolderName);
                    if (LooksLikeDlcQuest(std)) return std;
                }
            }
        }
        catch { /* registry/file access failed — null */ }
        return null;
    }

    /// Candidate Steam root folders from the registry (HKCU first, then HKLM
    /// WOW6432Node, then HKLM), de-duplicated. Empty if none / non-Windows.
    private static IEnumerable<string> EnumerateSteamRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string? root in new[]
        {
            ReadRegistryString(Microsoft.Win32.Registry.CurrentUser,
                               @"Software\Valve\Steam", "SteamPath"),
            ReadRegistryString(Microsoft.Win32.Registry.LocalMachine,
                               @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"),
            ReadRegistryString(Microsoft.Win32.Registry.LocalMachine,
                               @"SOFTWARE\Valve\Steam", "InstallPath"),
        })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            string norm = root!.Replace('/', '\\');
            if (Directory.Exists(norm) && seen.Add(norm))
                yield return norm;
        }
    }

    /// All Steam library roots: the Steam root itself plus every "path" entry in
    /// steamapps\libraryfolders.vdf. De-duplicated.
    private static IEnumerable<string> EnumerateSteamLibraries(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (seen.Add(steamRoot))
            yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string[] lines;
        try { lines = File.ReadAllLines(vdf); }
        catch { yield break; }

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            string? val = TryReadQuotedKeyValue(line, "path");
            if (val == null)
                val = TryReadLastQuotedAbsolutePath(line);
            if (string.IsNullOrWhiteSpace(val)) continue;

            string norm = val!.Replace(@"\\", @"\").Replace('/', '\\');
            if (Directory.Exists(norm) && seen.Add(norm))
                yield return norm;
        }
    }

    /// True when a folder looks like a (base) DLC Quest install. The game's exe is
    /// "DLC Quest.exe"; we also accept the folder simply existing with any exe (DLC
    /// Quest is an XNA/MonoGame title and the exe naming can vary across stores), so
    /// the check stays permissive — the user / installer is the authority.
    private static bool LooksLikeDlcQuest(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, "DLC Quest.exe"))) return true;
            if (File.Exists(Path.Combine(dir, "DLCQuest.exe")))  return true;
            // Permissive fallback: a folder named like DLC Quest that holds an exe.
            foreach (string _ in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
                return true;
        }
        catch { /* ignore */ }
        return false;
    }

    /// Read a string value from the registry, swallowing all failures.
    private static string? ReadRegistryString(
        Microsoft.Win32.RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using var key = hive.OpenSubKey(subKey);
            return key?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    /// Read a top-level quoted value for `key` from a Valve KeyValues text file
    /// (e.g. appmanifest .acf: `"installdir"  "DLC Quest"`). First match or null.
    private static string? ReadVdfValue(string path, string key)
    {
        try
        {
            foreach (string raw in File.ReadAllLines(path))
            {
                string? val = TryReadQuotedKeyValue(raw.Trim(), key);
                if (val != null) return val;
            }
        }
        catch { /* unreadable — null */ }
        return null;
    }

    /// Parse a single `"key"   "value"` KeyValues line. Returns the value when the
    /// line's key matches `key` (case-insensitive), else null.
    private static string? TryReadQuotedKeyValue(string line, string key)
    {
        int k0 = line.IndexOf('"');
        if (k0 < 0) return null;
        int k1 = line.IndexOf('"', k0 + 1);
        if (k1 < 0) return null;
        string foundKey = line.Substring(k0 + 1, k1 - k0 - 1);
        if (!string.Equals(foundKey, key, StringComparison.OrdinalIgnoreCase))
            return null;

        int v0 = line.IndexOf('"', k1 + 1);
        if (v0 < 0) return null;
        int v1 = line.IndexOf('"', v0 + 1);
        if (v1 < 0) return null;
        return line.Substring(v0 + 1, v1 - v0 - 1);
    }

    /// For older libraryfolders.vdf shapes, pull the last quoted token from a line
    /// and return it only if it looks like an absolute path. Null otherwise.
    private static string? TryReadLastQuotedAbsolutePath(string line)
    {
        int end = line.LastIndexOf('"');
        if (end <= 0) return null;
        int start = line.LastIndexOf('"', end - 1);
        if (start < 0) return null;
        string token = line.Substring(start + 1, end - start - 1);
        bool looksAbsolute =
            (token.Length >= 3 && token[1] == ':' && (token[2] == '\\' || token[2] == '/')) ||
            token.StartsWith('/') || token.Contains(@":\\");
        return looksAbsolute ? token : null;
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // Kept in this plugin's OWN JSON file (does not modify Core/SettingsStore) so the
    // plugin stays a single self-contained source file. BOM-less UTF-8.

    private sealed class DLCQuestSettings
    {
        /// The modded DLCQuestipelago folder the user pointed us at, so the chosen
        /// directory survives across launcher restarts.
        public string? ModdedDir { get; set; }
    }

    private DLCQuestSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<DLCQuestSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(DLCQuestSettings s)
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

    private void SaveModdedDir(string dir)
    {
        var s = LoadSettings();
        s.ModdedDir = dir;
        SaveSettings(s);
    }
}
