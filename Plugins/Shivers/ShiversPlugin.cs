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
// Orientation, FontWeights, HorizontalAlignment, Cursors, Thickness …). To avoid
// CS0104 this file deliberately does NOT do `using System.Windows.Controls;` /
// `using System.Windows.Media;` — every WPF UI type below is written fully qualified
// (System.Windows.Controls.*, System.Windows.Media.*, System.Windows.MessageBox, …).
// The project's GlobalUsings.cs already aliases the short names; this file does NOT
// add a file-level `using X = System.Windows...;` alias (that would be CS1537), it
// just qualifies in full so it is correct with or without those global aliases.

namespace LauncherV2.Plugins.Shivers;

// ═══════════════════════════════════════════════════════════════════════════════
// ShiversPlugin — install / detect / launch for "Shivers" (Sierra, 1995) played
// through the Shivers Randomizer Client + ScummVM. This is a NATIVE-PC
// "ConnectsItself" integration (NOT a BizHawk / Lua emulator game).
//
// WHAT KIND OF INTEGRATION IS THIS? (verified this session — see REALITY CHECK)
// ─────────────────────────────────────────────────────────────────────────────
// Shivers is a 1995 Sierra first-person point-and-click horror game built on the
// SCI-32 engine. It is played in Archipelago like this (verified against the
// official AP "Shivers" setup guide, archipelago.gg/tutorial/Shivers/setup/en):
//
//   * ScummVM (>= 2.7.0, official build, NOT a fork) runs the actual game. The
//     player adds their OWN copy of Shivers to ScummVM ("Add Game...", point it
//     at the Shivers data folder) and presses Start to play it.
//   * The "Shivers Randomizer" — a separate Windows (.NET/WPF) GUI application
//     from github.com/Shivers-Randomizer/Shivers-Randomizer — is the Archipelago
//     bridge. The player runs it ALONGSIDE the running game, clicks "Attach",
//     clicks "Archipelago", types the AP server address / slot name / password,
//     and clicks "Connect". The Randomizer attaches to the running ScummVM/Shivers
//     process and is what actually talks to the AP server — it holds the slot.
//
// HONESTY NOTE — this is a GUIDED case, like Undertale/Ship of Harkinian, and the
// connection here is CLIENT-RELAY (entered in the Randomizer's own UI), NOT in-game
// and NOT launcher-relay. Concrete, verified reasons the launcher does NOT fake a
// connection and does NOT hold its own ApClient on the slot:
//
//   1. ScummVM does not bundle with the Randomizer and is not shipped here —
//      it is the player's own official ScummVM install, and Shivers itself is the
//      player's own paid copy (GOG / Nightdive re-release / original disc). The
//      launcher must not, and does not, ship or recreate either.
//   2. The AP connection is typed INTO THE SHIVERS RANDOMIZER (Attach →
//      Archipelago → address/slot/password → Connect). There are no AP connection
//      command-line arguments on ScummVM or on the Randomizer that the launcher
//      can reliably prefill, so prefill here would be guesswork that could be
//      WRONG; we instead launch the Randomizer (one click toward play) and the
//      player connects in its UI. The Randomizer holds the slot, so the launcher's
//      own ApClient must NOT also sit on it (they would kick each other off) —
//      hence ConnectsItself = true.
//
// WHAT THIS PLUGIN DOES (V2.0)
// ────────────────────────────
//   1. Installs/updates the Shivers Randomizer from its GitHub releases (latest
//      tag, e.g. "RandomizerV2.7.5"; the tag is NOT plain semver, so the version
//      is parsed out of the tag name). Single Windows .zip asset, flattened so the
//      exe lands in the install folder. (Models Doom1993Plugin's install engine.)
//   2. Lets the user point at their OWN Shivers game-data folder. Detection is by
//      CONTENT (§11): a Shivers SCI-32 data folder contains "resmap.000" and
//      "ressci.000" (the SCI resource map + volume ScummVM's SCI engine keys on).
//      The launcher stores the location and NEVER modifies the original install.
//   3. On AP launch: launches the Shivers Randomizer (so the player gets one click
//      toward playing) and surfaces the exact remaining manual steps (start ScummVM
//      + Shivers, then Attach → Archipelago → Connect in the Randomizer). It does
//      NOT pass a connection (entered in the Randomizer UI) and does NOT hold an
//      ApClient on the slot. Plain (non-AP) launch just opens the Randomizer.
//   4. ConnectsItself semantics — the launcher tracks the Randomizer's process
//      lifetime only; no pipes, no Lua. The AP server side sees the Randomizer
//      connect.
//
// REALITY CHECK (2026-06-14) — facts verified this session:
//   * AP game string: "Shivers" — VERIFIED in worlds/shivers/__init__.py
//     (ShiversWorld, game = "Shivers"). World id "shivers".
//   * Randomizer repo: github.com/Shivers-Randomizer/Shivers-Randomizer. Latest
//     non-prerelease tag "RandomizerV2.7.5" (2025-12-18), single asset
//     "Shivers.Randomizer.V2.7.5.zip" (~74 MB). The repo is a .NET/WPF app
//     ("Shivers Randomizer.csproj", App.xaml) → built exe "Shivers Randomizer.exe".
//   * ScummVM requirement: official ScummVM >= 2.7.0 (NO fork), SCI-32 engine
//     plays Shivers (DOS original + Nightdive/GOG/Steam re-release supported).
//   * Setup flow + connection: start ScummVM and Shivers, run the Randomizer,
//     Attach → Archipelago → enter server address / slot name / password →
//     Connect, then New Game in Shivers. (Setup guide, authors GodlFire /
//     Cynbel_Terreus.)
//   * Shivers data signature: SCI-32 game dirs contain resmap.000 + ressci.000
//     (ScummVM SCI detection keys on resmap.%03d / ressci.%03d).
//
// DEFENSIVE / UNVERIFIED DETAILS (flagged, "verify at build time"):
//   * EXE NAME inside the zip was not inspected byte-for-byte offline.
//     ResolveRandomizerExe() prefers "Shivers Randomizer.exe", then any
//     "*shivers*"/"*randomizer*" exe in the install tree, EXCLUDING the bundled
//     "Shivers Multiplayer Server" exe and uninstallers/helpers.
//   * The launcher cannot prefill the AP connection (it is typed in the Randomizer
//     UI) and cannot verify the player completed Attach/Connect — the Settings
//     panel is explicit about the remaining manual steps.
//   * One launcher-side setting (the Shivers data-folder path) is stored in this
//     plugin's OWN JSON sidecar (Games/ROMs/shivers/shivers_launcher.json) rather
//     than Core/SettingsStore — this plugin is added as a single self-contained
//     file and deliberately does not modify shared launcher types (same approach
//     as the Doom / Undertale / Saving Princess plugins).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ShiversPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER = "Shivers-Randomizer";
    private const string GITHUB_REPO  = "Shivers-Randomizer";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";

    /// Official Archipelago "Shivers" setup guide.
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Shivers/setup/en";

    /// Official ScummVM downloads (>= 2.7.0 required — NO fork).
    private const string ScummVmDownloadsUrl = "https://www.scummvm.org/downloads/";

    /// Shivers on GOG (the player's own copy — Nightdive re-release).
    private const string GogStoreUrl = "https://www.gog.com/en/game/shivers";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp, written after a successful install.
    private const string VersionFileName = "shivers_randomizer_version.dat";

    /// Preferred Randomizer exe name (from the repo's csproj name). Resolution
    /// falls back to a fuzzy match (see header — not verified byte-for-byte).
    private const string PreferredExeName = "Shivers Randomizer.exe";

    /// Shivers SCI-32 data-folder signature files (ScummVM SCI engine keys on
    /// these — resmap.%03d / ressci.%03d). Used for bring-your-own-asset by
    /// content (§11). Either index 000 or 001 may be present depending on dump.
    private static readonly string[] ShiversSignatureFiles =
        { "resmap.000", "ressci.000", "resmap.001", "ressci.001" };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "shivers";
    public string DisplayName => "Shivers";
    public string Subtitle    => "Native PC · Archipelago (ScummVM)";

    /// EXACT AP game string — VERIFIED against worlds/shivers/__init__.py
    /// (ShiversWorld, game = "Shivers").
    public string ApWorldName => "Shivers";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "shivers.png");

    public string ThemeAccentColor => "#5A2E7A";   // museum-of-the-macabre purple
    public string[] GameBadges     => new[] { "Requires Shivers game data", "ScummVM" };

    public string Description =>
        "Shivers is Sierra's 1995 first-person point-and-click horror game: trapped " +
        "inside the eerie Mausoleum of the Mind, you solve puzzles, free the malevolent " +
        "Ixupi spirits one by one, and try to escape before they drain your life. This " +
        "is the official Archipelago integration, which shuffles the game's items and " +
        "progression into the multiworld. You bring your own copy of Shivers (GOG, the " +
        "Nightdive re-release, or the original CD) and play it in ScummVM 2.7.0 or " +
        "later; the separate Shivers Randomizer client attaches to the running game and " +
        "connects to the multiworld. The launcher installs the Randomizer, remembers " +
        "your game-data folder, and launches the Randomizer for you.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// Installed == the Shivers Randomizer client exe is present.
    public bool IsInstalled => ResolveRandomizerExe() != null;
    public bool IsRunning   { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the Shivers Randomizer client is installed.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "ShiversRandomizer");

    private string PreferredExePath => Path.Combine(GameDirectory, PreferredExeName);

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained source file — same approach as the
    /// Doom / Undertale plugins). BOM-less UTF-8.
    private string SettingsSidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                        "shivers_launcher.json");

    private string SettingsSidecarDir => Path.GetDirectoryName(SettingsSidecarPath)!;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    /// The user-chosen Shivers game-data folder (their own copy). Optional; only
    /// used to light up "ready" and to guide ScummVM setup. Restored on construct.
    private string? _shiversDataDir;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Shivers Randomizer owns the AP slot and reports checks/items/goal to the
    // server itself (it attaches to the running game). The launcher relays nothing.
    // These exist only for interface compatibility (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Constructor — restore a previously chosen Shivers data folder ─────────

    public ShiversPlugin()
    {
        try
        {
            string? saved = LoadSettings().ShiversDataDir;
            if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
                _shiversDataDir = saved;
        }
        catch { /* fall back to no remembered folder */ }
    }

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = File.Exists(VersionFilePath) && IsInstalled
                ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim()
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
                await GitHubHelper.FetchLatestTagAsync(GITHUB_OWNER, GITHUB_REPO, ct));
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
        // 1. Resolve the latest release.
        progress.Report((2, "Checking latest Shivers Randomizer release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        // 2. Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"Shivers Randomizer {version} is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a Windows download for the Shivers Randomizer on the " +
                "GitHub release page. Check your internet connection, or download the " +
                "build manually from " + RepoUrl + "/releases.");

        // 3. Download + extract the client.
        await DownloadAndExtractGameAsync(zipUrl, version, progress, ct);

        // 4. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"Shivers Randomizer {version} ready. You also need ScummVM 2.7.0+ and your " +
            "own copy of Shivers — point this plugin at your Shivers game folder in " +
            "Settings, then press Play."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── AutoMod-style validation of a user-picked Shivers data folder ─────────

    /// The user located their own Shivers game-data folder. Accept it only when it
    /// looks like a Shivers SCI-32 install (resmap.000/ressci.000 present). Returns
    /// null when acceptable, else a short reason so they can pick again. (This is
    /// the Shivers DATA folder, not the Randomizer install — the launcher never
    /// modifies it, §11.)
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick the folder that contains your " +
                   "Shivers game files (the one you add to ScummVM).";

        try
        {
            if (LooksLikeShiversData(folder)) return null;
        }
        catch
        {
            return "Could not read that folder. Pick a different one and try again.";
        }

        return "That folder does not look like a Shivers game install (it has no " +
               "resmap.000 / ressci.000 resource files). Pick the folder that holds " +
               "your Shivers game data — for the GOG version this is usually " +
               @"GOG Galaxy\Games\Shivers.";
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────
    // Best effort: open the Shivers Randomizer client so the player gets one click
    // toward playing. The AP connection is entered in the Randomizer's own UI
    // (Attach → Archipelago → address/slot/password → Connect), so we do NOT pass
    // connection args and do NOT hold an ApClient on the slot (ConnectsItself).

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        _ = session; // connection is made in the Randomizer UI, not via args here
        LaunchRandomizer();
        return Task.CompletedTask;
    }

    /// Shivers is a complete game — plain (non-AP) play is possible (run it in
    /// ScummVM; launching the Randomizer without connecting simply does nothing).
    public bool SupportsStandalone => true;

    /// The Shivers Randomizer owns the AP slot connection (see header). The launcher
    /// must NOT connect its own ApClient to the same slot while the Randomizer runs,
    /// or they would kick each other off.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        LaunchRandomizer();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No password/args are passed by this plugin (the connection is entered in
        // the Randomizer UI), so there is no plaintext credential to scrub from disk.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The Shivers Randomizer receives items from the AP server directly and
        // relays them into the running game; there is nothing for the launcher to
        // forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The Shivers Randomizer renders its own connection state; no launcher HUD
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
            Text = "Shivers is played in ScummVM (2.7.0 or later) using your own copy of " +
                   "the game; the separate Shivers Randomizer client attaches to the running " +
                   "game and connects to the multiworld. The launcher installs the Randomizer, " +
                   "remembers your Shivers game folder, and can launch the Randomizer — but you " +
                   "connect from inside the Randomizer (Attach → Archipelago → Connect), " +
                   "not here.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: Randomizer install directory ─────────────────────────
        panel.Children.Add(SectionHeader("SHIVERS RANDOMIZER (CLIENT) INSTALL DIRECTORY", muted));

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = GameDirectory, IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
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
        dirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select Shivers Randomizer install folder",
                InitialDirectory = Directory.Exists(GameDirectory) ? GameDirectory
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = IsInstalled ? "✓ Shivers Randomizer is installed"
                                     : "Not installed (click Install in the Play tab)",
            FontSize   = 11, Foreground = IsInstalled ? success : muted,
            Margin     = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Shivers game-data folder (the player's own copy) ─────
        panel.Children.Add(SectionHeader("YOUR SHIVERS GAME FOLDER", muted));

        var gameRow = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var gameBox = new System.Windows.Controls.TextBox
        {
            Text = _shiversDataDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var gameBtn = new System.Windows.Controls.Button
        {
            Content = "Browse...", Width = 90, Padding = new Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };

        var gameStatus = new System.Windows.Controls.TextBlock
        {
            FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        };
        void RefreshGameStatus()
        {
            bool ok = !string.IsNullOrEmpty(_shiversDataDir) &&
                      Directory.Exists(_shiversDataDir) &&
                      LooksLikeShiversData(_shiversDataDir!);
            gameStatus.Text = ok
                ? "✓ Looks like a Shivers game folder — add this same folder to " +
                  "ScummVM (Add Game...) to play."
                : "Point this at your own Shivers game folder (the one with resmap.000 / " +
                  "ressci.000 — for GOG, usually GOG Galaxy\\Games\\Shivers). The " +
                  "launcher never modifies it.";
            gameStatus.Foreground = ok ? success : muted;
        }
        RefreshGameStatus();

        gameBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select your Shivers game folder",
                InitialDirectory = Directory.Exists(_shiversDataDir ?? "")
                    ? _shiversDataDir!
                    : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    var go = System.Windows.MessageBox.Show(
                        bad + "\n\nUse this folder anyway?",
                        "That folder does not look like Shivers",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (go != MessageBoxResult.Yes) return;
                }
                _shiversDataDir = picked;
                gameBox.Text = picked;
                SaveShiversDataDir(picked);
                RefreshGameStatus();
            }
        };
        System.Windows.Controls.DockPanel.SetDock(gameBtn, System.Windows.Controls.Dock.Right);
        gameRow.Children.Add(gameBtn);
        gameRow.Children.Add(gameBox);
        panel.Children.Add(gameRow);
        panel.Children.Add(gameStatus);

        // ── Section: setup + connection steps ─────────────────────────────
        panel.Children.Add(SectionHeader("SETUP & CONNECTION", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "1) Install ScummVM 2.7.0 or later (link below).\n" +
                "2) Get your own copy of Shivers (GOG / the Nightdive re-release, or copy " +
                "your original CD to a folder).\n" +
                "3) In ScummVM, click \"Add Game...\" and pick your Shivers game folder. " +
                "Point the box above at the same folder.\n" +
                "4) Install the Shivers Randomizer here (Play tab).\n\n" +
                "To play: in ScummVM, highlight Shivers and press Start. Then press Play here " +
                "(it opens the Shivers Randomizer). In the Randomizer click \"Attach\", then " +
                "\"Archipelago\", enter the Archipelago server address, your slot name, and " +
                "password, and click \"Connect\". Finally click \"New Game\" in Shivers. " +
                "(The connection is entered in the Randomizer — the launcher does not " +
                "connect to your slot itself.)",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Shivers Randomizer (GitHub) ↗", RepoUrl),
            ("ScummVM Downloads ↗",           ScummVmDownloadsUrl),
            ("Shivers on GOG ↗",              GogStoreUrl),
            ("Shivers Setup Guide ↗",         SetupGuideUrl),
            ("Archipelago Official ↗",        "https://archipelago.gg"),
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

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
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

    // ── Private helpers — release resolution ──────────────────────────────────

    /// Parse the version out of a Shivers Randomizer tag. The tags are NOT plain
    /// semver — they look like "RandomizerV2.7.5" — so strip a leading
    /// "Randomizer"/"v" decoration and keep the numeric remainder. A bare "vX.Y"
    /// loses only the 'v'. Returns null for null/blank tags; otherwise never blank.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string s = tag.Trim();

        // Drop a leading "Randomizer" word if present (case-insensitive).
        const string prefix = "Randomizer";
        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            s = s[prefix.Length..];

        // Drop a single leading 'v'/'V' that decorates a digit.
        if (s.Length > 1 && (s[0] == 'v' || s[0] == 'V') && char.IsDigit(s[1]))
            s = s[1..];

        s = s.Trim();
        return s.Length == 0 ? tag.Trim() : s; // never return blank
    }

    /// Resolve the latest release: version + Windows zip asset URL. Enumerates
    /// /releases (newest first) and takes the first non-draft release that has a
    /// downloadable Windows .zip — robust whether or not releases are flagged
    /// prerelease. Returns (version, null) when the API is reachable but no usable
    /// asset is found; the caller surfaces an honest error.
    private async Task<(string Version, string? ZipUrl)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                if (rel.TryGetProperty("draft", out var dr) &&
                    dr.ValueKind == JsonValueKind.True)
                    continue;

                string? version = rel.TryGetProperty("tag_name", out var t)
                    ? NormalizeTag(t.GetString())
                    : null;
                if (version == null) continue;

                if (rel.TryGetProperty("assets", out var assets) &&
                    assets.ValueKind == JsonValueKind.Array)
                {
                    string? zip = PickWindowsZip(assets);
                    if (zip != null)
                        return (version, zip);
                }
            }

            // API reachable, releases present, but none had a usable Windows zip:
            // report the newest tag with no URL so the caller errors honestly.
            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                string? version = rel.TryGetProperty("tag_name", out var t)
                    ? NormalizeTag(t.GetString())
                    : null;
                if (version != null) return (version, null);
            }
        }

        throw new InvalidOperationException("No Shivers Randomizer releases were found.");
    }

    /// From a release's assets array, pick the Windows .zip (the Randomizer ships a
    /// single Windows zip, e.g. "Shivers.Randomizer.V2.7.5.zip"). Excludes source
    /// archives. Prefers an asset whose name mentions shivers/randomizer; otherwise
    /// takes the first plausible .zip.
    private static string? PickWindowsZip(JsonElement assets)
    {
        string? preferred = null, anyZip = null;

        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();
            if (!lower.EndsWith(".zip")) continue;
            if (lower.Contains("source")) continue;
            if (lower.Contains("linux") || lower.Contains("mac") || lower.Contains("darwin")) continue;

            anyZip ??= url;
            if (preferred == null &&
                (lower.Contains("shivers") || lower.Contains("randomizer")))
                preferred = url;
        }
        return preferred ?? anyZip;
    }

    // ── Private helpers — exe resolution ──────────────────────────────────────

    /// Resolve the installed Randomizer exe: prefer "Shivers Randomizer.exe", then
    /// any "*shivers*"/"*randomizer*" exe in the install tree — EXCLUDING the
    /// bundled "Shivers Multiplayer Server" exe and uninstallers/helpers. Defensive
    /// (the exact zip contents were not inspected byte-for-byte offline).
    private string? ResolveRandomizerExe()
    {
        if (File.Exists(PreferredExePath)) return PreferredExePath;
        if (!Directory.Exists(GameDirectory)) return null;
        try
        {
            string? fuzzy = null;
            foreach (string exe in Directory.EnumerateFiles(GameDirectory, "*.exe", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (IsHelperExe(name)) continue;
                if (name.Contains("server")) continue;   // the multiplayer server exe
                if (name.Contains("randomizer") && name.Contains("shivers"))
                    return exe;                            // best fuzzy match
                if (fuzzy == null && (name.Contains("randomizer") || name.Contains("shivers")))
                    fuzzy = exe;
            }
            return fuzzy;
        }
        catch { /* directory vanished mid-scan */ }
        return null;
    }

    /// Names that are NOT the runnable client (uninstaller, helpers).
    private static bool IsHelperExe(string nameLowerNoExt)
        => nameLowerNoExt.Contains("unins")    ||
           nameLowerNoExt.Contains("setup")    ||
           nameLowerNoExt.Contains("crash")    ||
           nameLowerNoExt.Contains("vcredist") ||
           nameLowerNoExt.Contains("dxsetup");

    // ── Private helpers — Shivers data detection (bring-your-own, by content) ──

    /// True when a folder looks like a Shivers SCI-32 game install — it contains at
    /// least one of the SCI resource map/volume files (resmap.000 + ressci.000, or
    /// the .001 variant). Case-insensitive (Directory enumeration is fine on
    /// Windows, but we also probe explicit names). Never throws.
    private static bool LooksLikeShiversData(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;

            // Fast path: explicit known names.
            foreach (string sig in ShiversSignatureFiles)
                if (File.Exists(Path.Combine(dir, sig))) return true;

            // Case-insensitive fallback (some dumps use upper-case names).
            var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string f in Directory.EnumerateFiles(dir, "*.0*", SearchOption.TopDirectoryOnly))
                present.Add(Path.GetFileName(f));

            // Need a resource MAP and a resource VOLUME (same index) to be a real
            // SCI game dir.
            bool hasMap = present.Contains("resmap.000") || present.Contains("resmap.001");
            bool hasVol = present.Contains("ressci.000") || present.Contains("ressci.001");
            return hasMap && hasVol;
        }
        catch { return false; }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Launch the Shivers Randomizer client. If it is not installed, throw with
    /// honest guidance so the UI surfaces the real next step (install it), rather
    /// than an opaque failure.
    private void LaunchRandomizer()
    {
        string? exe = ResolveRandomizerExe();
        if (exe == null)
            throw new FileNotFoundException(
                "The Shivers Randomizer is not installed. Click Install Game first. " +
                "You also need ScummVM 2.7.0+ and your own copy of Shivers (see Settings).",
                PreferredExePath);

        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exe,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? GameDirectory,
            UseShellExecute  = false,
        }) ?? throw new InvalidOperationException("Failed to start the Shivers Randomizer.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // Kept in this plugin's OWN JSON file (does not modify Core/SettingsStore).
    // BOM-less UTF-8.

    private sealed class ShiversSettings
    {
        /// The user's own Shivers game-data folder (the one they add to ScummVM),
        /// so it survives across launcher restarts.
        public string? ShiversDataDir { get; set; }
    }

    private ShiversSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<ShiversSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(ShiversSettings s)
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

    private void SaveShiversDataDir(string dir)
    {
        var s = LoadSettings();
        s.ShiversDataDir = dir;
        SaveSettings(s);
    }

    // ── Private helpers — download/extract ────────────────────────────────────

    private async Task DownloadAndExtractGameAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"shivers-randomizer-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading Shivers Randomizer {version}..."));
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
                        int pct = (int)(5 + 60 * downloaded / total);
                        progress.Report((pct, $"Downloading Shivers Randomizer... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting..."));
            Directory.CreateDirectory(GameDirectory);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, GameDirectory, overwriteFiles: true);

            // Release zips often contain a single top-level sub-folder — flatten it
            // so the exe lands directly in GameDirectory. (ResolveRandomizerExe
            // scans subdirectories too, so only flatten when the extract is a lone
            // wrapper folder with nothing runnable at the root.)
            if (ResolveRandomizerExe() == null && Directory.GetFiles(GameDirectory).Length == 0)
            {
                string[] subdirs = Directory.GetDirectories(GameDirectory);
                if (subdirs.Length == 1)
                {
                    string sub = subdirs[0];
                    foreach (string fileSrc in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
                    {
                        string rel     = Path.GetRelativePath(sub, fileSrc);
                        string fileDst = Path.Combine(GameDirectory, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                        File.Move(fileSrc, fileDst, overwrite: true);
                    }
                    Directory.Delete(sub, recursive: true);
                }
            }

            progress.Report((80, "Client files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }
}
