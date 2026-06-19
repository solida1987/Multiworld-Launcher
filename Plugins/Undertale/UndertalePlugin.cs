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

namespace LauncherV2.Plugins.Undertale;

// ═══════════════════════════════════════════════════════════════════════════════
// UndertalePlugin — install-guide / detect / launch for "Undertale" (Toby Fox)
// played through its OFFICIAL Archipelago integration.
//
// WHAT KIND OF INTEGRATION IS THIS? (verified this session — see REALITY CHECK)
// ─────────────────────────────────────────────────────────────────────────────
// Undertale's Archipelago support is part of AP-MAIN itself (worlds/undertale),
// NOT a third-party mod repo with a one-click download. The pieces are:
//
//   * The "Undertale Client" — a Python client SHIPPED INSIDE the Archipelago
//     release the player already has (registered as
//     Component("Undertale Client", "UndertaleClient"); the source lives at the
//     Archipelago repo root, UndertaleClient.py). It owns the AP slot connection.
//   * A bsdiff4 patch (worlds/undertale/data/patch.bsdiff) that the client applies
//     to a COPY of the player's own Steam Undertale data.win.
//   * The player's own Steam copy of Undertale (appid 391540) — the base game.
//
// HONESTY NOTE — this is a GUIDED case (like Saving Princess / Ship of Harkinian),
// and in fact MORE guided than Saving Princess, because the AP slot here is held by
// a SEPARATE long-running tool (the Undertale Client), not by the game process. The
// concrete, verified reasons the launcher canNOT do a fake "one-click install":
//
//   1. There is NO redistributable runnable game and NO standalone mod zip to
//      download. The patch ships *inside the Archipelago install* the player runs,
//      and it is applied to the player's OWN Steam data.win. (Undertale is paid
//      software on Steam — the launcher must not, and does not, ship or recreate it.)
//   2. The OFFICIAL, documented setup (the AP "Undertale" setup guide + the bundled
//      Undertale Client) is: start the "Undertale Client" from the Archipelago
//      folder, run  /auto_patch <Steam Undertale dir>  ONCE. The client copies the
//      Steam game files (everything except steam_api.dll) into
//      <Archipelago user dir>\Undertale\ and bsdiff4-patches data.win in place.
//      Reproducing that (find the Steam dir, copy files, apply a bsdiff4 patch that
//      lives inside the AP install) is exactly the bundled client's job — so, like
//      SoH did not reproduce OTR generation, this plugin does NOT fake it.
//   3. CONNECTION IS CLIENT-RELAY, NOT in-game and NOT launcher-relay. The patched
//      GameMaker game communicates through save-data files under
//      %localappdata%\UNDERTALE; the *Undertale Client* watches those files and is
//      what actually talks to the AP server. The player connects by typing
//      "host:port" then their slot name INTO THE UNDERTALE CLIENT (its top text box,
//      then the bottom box) — there are no connection command-line args on the game
//      exe, and the launcher's own ApClient must NOT also sit on the slot (the
//      Undertale Client would be kicked, or kick us). Hence ConnectsItself = true.
//
// WHAT THIS PLUGIN HONESTLY DOES:
//   * DETECT the player's Steam Undertale install (appid 391540) via the same
//     registry → libraryfolders.vdf → appmanifest_391540.acf → common pipeline used
//     by the Stardew Valley plugin, and detect the PATCHED copy at
//     <override>\Undertale\ (or a user-set folder), so it can tell the player exactly
//     where things are and light up "ready".
//   * GUIDE the irreducible steps in InstallOrUpdate / Settings (get Undertale on
//     Steam, run the Undertale Client's /auto_patch once, then run BOTH the patched
//     game and the Undertale Client to play), with direct links (Steam page, AP
//     releases, official setup guide, archipelago.gg).
//   * LAUNCH the patched UNDERTALE.exe from the detected/override patched folder when
//     present (so the player at least gets the game open with one click); it can NOT
//     prefill the connection because the connection is entered into the separate
//     Undertale Client, which the launcher does not own.
//   * NEVER claim an install exists when it does not, never modify the player's Steam
//     copy (§11), and keep its one setting in its OWN JSON sidecar (does not touch
//     Core/SettingsStore).
//
// REALITY CHECK (2026-06-14) — facts verified this session against AP-main:
//   * AP game string: "Undertale" — VERIFIED in worlds/undertale/__init__.py
//     (game = "Undertale") AND UndertaleClient.py (self.game = "Undertale").
//     World id "undertale".
//   * Client component: Component("Undertale Client", "UndertaleClient") in
//     worlds/undertale/__init__.py. Client source = repo-root UndertaleClient.py.
//   * /auto_patch: UndertaleClient.py copies every file from the Steam dir EXCEPT
//     steam_api.dll into Utils.user_path("Undertale", ...) then bsdiff4.patch()es
//     data.win using undertale.data_path("patch.bsdiff"). Default Steam dir tried:
//     C:\Program Files (x86)\Steam\steamapps\common\Undertale and the Program Files
//     variant. Requires data.win present in the source dir.
//   * Connect: setup guide — run BOTH the patched game (from the Archipelago folder)
//     and its client; in the client's TOP text box type host:port (e.g.
//     archipelago.gg:38281), then enter the slot name in the BOTTOM box. The game
//     relays via %localappdata%\UNDERTALE save files; the client holds the slot.
//
// DEFENSIVE / UNVERIFIED DETAILS (flagged, "verify at build time"):
//   * Patched game exe name: Undertale's GameMaker runtime is "UNDERTALE.exe" in the
//     Steam install; /auto_patch copies the whole folder (so the same exe name lands
//     in <AP>\Undertale\UNDERTALE.exe). ResolvePatchedExe() prefers "UNDERTALE.exe"
//     and falls back to any "*undertale*.exe" / single runner exe, excluding the
//     Steam shim. The exact copied exe name was taken from the GameMaker runtime
//     convention + /auto_patch's "copy whole folder" behaviour, not inspected
//     byte-for-byte offline.
//   * IsInstalled is true ONLY when a patched, runnable game exe is found in the
//     resolved patched folder — having only the Steam base game (unpatched) does NOT
//     flip it true, because that copy is not the Archipelago one.
//   * The launcher cannot verify the user actually ran /auto_patch beyond "is there a
//     game exe in the patched folder you pointed me at"; the Settings panel is
//     explicit about the remaining manual steps.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class UndertalePlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    /// Undertale's Steam application id (VERIFIED — store.steampowered.com/app/391540
    /// and used as the "magic number" 391540 throughout the AP setup guide / client).
    private const string SteamAppId = "391540";

    /// Standard Steam install sub-folder name (steamapps\common\Undertale).
    private const string SteamCommonFolderName = "Undertale";

    /// The GameMaker runtime exe name for Undertale (preferred patched exe).
    private const string PreferredExeName = "UNDERTALE.exe";

    /// Official Archipelago "Undertale" setup guide.
    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Undertale/setup/en";

    /// Undertale on Steam (the base game the player must own).
    private const string SteamStoreUrl =
        "https://store.steampowered.com/app/391540";

    /// Archipelago releases — the Undertale Client ships inside this download.
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

    public string GameId      => "undertale";
    public string DisplayName => "Undertale";
    public string Subtitle    => "Native PC · Archipelago";

    /// EXACT AP game string — VERIFIED against worlds/undertale/__init__.py
    /// (game = "Undertale") and UndertaleClient.py (self.game = "Undertale").
    public string ApWorldName => "Undertale";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "undertale.png");

    public string ThemeAccentColor => "#C02020";   // SAVE-point red
    public string[] GameBadges     => new[] { "Steam · needs patch" };

    public string Description =>
        "Undertale is Toby Fox's acclaimed RPG where every choice you make matters: " +
        "you can fight the monsters of the Underground, spare every one of them, or " +
        "anything in between, and the world remembers. This is the official " +
        "Archipelago integration, which shuffles your key items, weapons and armor " +
        "into the multiworld. You bring your own copy of Undertale (owned on Steam); " +
        "Archipelago's bundled \"Undertale Client\" patches a separate copy of the " +
        "game once, and that client connects to the multiworld while you play. The " +
        "launcher detects your Steam install, guides the one-time patch, and can " +
        "launch the patched game for you.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    // Undertale's AP integration is versioned by the Archipelago release the user
    // runs (the patch ships inside it), not by a standalone mod tag. There is no
    // independent version stamp this plugin can author honestly, so these stay null
    // (the news feed surfaces AP release versions instead).
    public string? InstalledVersion => null;
    public string? AvailableVersion => null;

    /// Installed == a PATCHED, runnable Undertale exe is present in the resolved
    /// patched folder (the Steam base game alone is NOT the Archipelago copy).
    public bool IsInstalled => ResolvePatchedExe() != null;
    public bool IsRunning   { get; private set; }

    /// Empty string when not installed (interface contract). Reports the resolved
    /// patched folder when known, else "".
    public string GameDirectory => ResolvePatchedDir() ?? "";

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained source file — same approach as the
    /// Saving Princess / Doom / Celeste 64 plugins). BOM-less UTF-8.
    private string SettingsSidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                        "undertale_launcher.json");

    private string SettingsSidecarDir => Path.GetDirectoryName(SettingsSidecarPath)!;

    /// User-set override pointing at the PATCHED Archipelago Undertale folder (the
    /// one created by /auto_patch, e.g. <Archipelago user dir>\Undertale). Optional;
    /// when unset the plugin only knows about the Steam base install.
    private string? _overridePatchedDir;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The external Undertale Client owns the AP slot and relays checks/items/goal to
    // the server itself (via the game's %localappdata%\UNDERTALE save files). The
    // launcher relays nothing. These exist only for interface compatibility
    // (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Constructor — restore a previously chosen patched folder ──────────────

    public UndertalePlugin()
    {
        try
        {
            string? saved = LoadSettings().PatchedDir;
            if (!string.IsNullOrWhiteSpace(saved) && Directory.Exists(saved))
                _overridePatchedDir = saved;
        }
        catch { /* fall back to detection only */ }
    }

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────
    // No independent version to compare (the patch ships inside the player's AP
    // install). Contract: never throw on network failure.

    public Task CheckForUpdateAsync(CancellationToken ct = default)
        => Task.CompletedTask;

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────
    // HONEST guided setup. There is nothing for the launcher to download here (the
    // patch + client live inside the player's Archipelago install, and the base game
    // is the player's paid Steam copy). So this reports the current detection state
    // and the exact remaining manual steps — it never fabricates an install.

    public Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((10, "Checking for your Steam copy of Undertale..."));

        string? steamDir   = DetectSteamInstallDir();
        string? patchedDir = ResolvePatchedDir();
        bool    patched    = ResolvePatchedExe() != null;

        if (patched)
        {
            progress.Report((100,
                $"Patched Undertale found at \"{patchedDir}\". Press Play to launch it, " +
                "then connect from Archipelago's \"Undertale Client\" (type host:port, " +
                "then your slot name). See the Setup Guide link in Settings."));
            return Task.CompletedTask;
        }

        // Not patched yet — give the most specific guidance we can.
        if (!string.IsNullOrEmpty(steamDir))
        {
            progress.Report((100,
                "Steam Undertale detected at \"" + steamDir + "\", but the Archipelago " +
                "(patched) copy was not found yet. To finish setup: open Archipelago, " +
                "start the \"Undertale Client\", and run  /auto_patch \"" + steamDir +
                "\"  once. It creates a patched copy in your Archipelago folder under " +
                "\\Undertale. Then point this plugin at that folder in Settings. See the " +
                "Setup Guide link in Settings."));
        }
        else
        {
            progress.Report((100,
                "Undertale was not detected via Steam. 1) Install Undertale on Steam " +
                "(appid 391540). 2) In Archipelago, start the \"Undertale Client\" and run " +
                "  /auto_patch <your Steam Undertale folder>  once to create the patched " +
                "copy. 3) Point this plugin at that patched \\Undertale folder in Settings. " +
                "See the Setup Guide link in Settings."));
        }
        return Task.CompletedTask;
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public Task<bool> VerifyInstallAsync(CancellationToken ct = default)
        => Task.FromResult(IsInstalled);

    // ── AutoMod-style validation of a user-picked patched folder ──────────────

    /// The user located the folder /auto_patch created (the Archipelago Undertale
    /// copy). Accept it only when it contains a runnable Undertale exe. Returns null
    /// when acceptable, else a short reason so they can pick again.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick the \\Undertale folder that " +
                   "Archipelago's \"Undertale Client\" created with /auto_patch.";

        try
        {
            if (FindPatchedExeIn(folder) != null)
                return null;
        }
        catch
        {
            return "Could not read that folder. Pick a different one and try again.";
        }

        return "That folder does not contain a runnable Undertale game " +
               "(\"UNDERTALE.exe\"). In Archipelago, run the \"Undertale Client\" and " +
               "use /auto_patch first, then pick the \\Undertale folder it created.";
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────
    // Best effort: open the PATCHED game so the player gets one-click launch. The
    // connection itself is entered into the separate Undertale Client (see header),
    // so we do NOT pass connection args and we do NOT hold an ApClient on the slot
    // (ConnectsItself = true). If no patched exe is found, fail with honest guidance.

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        _ = session; // connection is made in the Undertale Client, not via args here
        LaunchPatchedGame();
        return Task.CompletedTask;
    }

    /// Undertale is a complete game — plain (non-AP) play is possible. (Launching
    /// the patched copy without the client running simply means nothing connects.)
    public bool SupportsStandalone => true;

    /// The external Undertale Client owns the AP slot connection (see header). The
    /// launcher must NOT connect its own ApClient to the same slot while the game /
    /// client run, or they would kick each other off.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        LaunchPatchedGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No password/args are passed by this plugin (the connection is entered in
        // the Undertale Client), so there is nothing sensitive to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The Undertale Client receives items from the AP server directly and relays
        // them into the game; there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The Undertale Client renders its own connection state; no launcher HUD
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
            Text = "Undertale's Archipelago support is part of Archipelago itself, " +
                   "not a one-click mod. You bring your own Steam copy of Undertale; " +
                   "Archipelago's bundled \"Undertale Client\" patches a separate copy " +
                   "(via /auto_patch) and that client connects to the multiworld while " +
                   "you play. The launcher detects your install, guides the patch, and " +
                   "can launch the patched game — but you connect from the Undertale " +
                   "Client, not here. Some details below were verified against the " +
                   "official setup guide and the AP client, not byte-for-byte offline.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: Steam base game (detected) ───────────────────────────
        string? steamDir = DetectSteamInstallDir();
        panel.Children.Add(SectionHeader("STEAM UNDERTALE (BASE GAME)", muted));
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = steamDir != null
                ? "✓ Detected via Steam (appid " + SteamAppId + "):\n" + steamDir
                : "Not detected via Steam. Install Undertale on Steam (appid " +
                  SteamAppId + "), or you can still patch manually if you own it " +
                  "elsewhere.",
            FontSize = 11, Foreground = steamDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Patched Archipelago copy (override) ──────────────────
        panel.Children.Add(SectionHeader("ARCHIPELAGO (PATCHED) UNDERTALE FOLDER", muted));

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = _overridePatchedDir ?? ResolvePatchedDir() ?? "",
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
            bool ok = ResolvePatchedExe() != null;
            statusText.Text = ok
                ? "✓ Patched Undertale found here — ready to launch."
                : "Patched game not found here yet. Run Archipelago's \"Undertale " +
                  "Client\" and use /auto_patch, then point this at the \\Undertale " +
                  "folder it created.";
            statusText.Foreground = ok ? success : muted;
        }
        RefreshStatus();

        dirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select your Archipelago (patched) Undertale folder",
                InitialDirectory = Directory.Exists(_overridePatchedDir ?? "")
                    ? _overridePatchedDir!
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
                        "No patched game found here",
                        MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (go != MessageBoxResult.Yes) return;
                }
                _overridePatchedDir = picked;
                dirBox.Text = picked;
                SavePatchedDir(picked);
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
                "1) Own and install Undertale on Steam (appid " + SteamAppId + ").\n" +
                "2) Open Archipelago, start the \"Undertale Client\", and run\n" +
                "      /auto_patch <your Steam Undertale folder>\n" +
                "   once. It copies the game into your Archipelago folder under " +
                "\\Undertale and patches it. (Redo this after you update Archipelago.)\n" +
                "3) Point the \"Archipelago (patched) Undertale folder\" above at that " +
                "\\Undertale folder.\n\n" +
                "To play: press Play here (or launch the patched game) AND start the " +
                "\"Undertale Client\". In the client's TOP text box type your server as " +
                "host:port (e.g. archipelago.gg:38281), then enter your SLOT NAME in the " +
                "bottom box. The client connects to the multiworld and relays everything " +
                "to the game. (The connection is entered in the Undertale Client — the " +
                "launcher does not connect to your slot itself.)",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("Undertale on Steam ↗",        SteamStoreUrl),
            ("Undertale Setup Guide ↗",     SetupGuideUrl),
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
    // Undertale's integration ships with the Archipelago release, so the most
    // honest "news" is the AP release stream (the Undertale Client + patch come
    // from there). Never throws — empty on failure.

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

    // ── Private helpers — patched-folder / exe resolution ─────────────────────

    /// The resolved PATCHED Undertale folder: the user override if it still exists,
    /// else null. (We do NOT auto-resolve the Steam base folder here — that copy is
    /// not the Archipelago/patched one.)
    private string? ResolvePatchedDir()
    {
        if (!string.IsNullOrWhiteSpace(_overridePatchedDir) &&
            Directory.Exists(_overridePatchedDir))
            return _overridePatchedDir;
        return null;
    }

    /// The runnable PATCHED Undertale exe inside the resolved patched folder, or null.
    private string? ResolvePatchedExe()
    {
        string? dir = ResolvePatchedDir();
        if (dir == null) return null;
        try { return FindPatchedExeIn(dir); }
        catch { return null; }
    }

    /// Find a runnable Undertale exe in `dir`: prefer UNDERTALE.exe, else a fuzzy
    /// "*undertale*.exe", else (last resort) a lone non-helper .exe. Excludes the
    /// Steam shim / uninstallers / common helpers.
    private static string? FindPatchedExeIn(string dir)
    {
        if (!Directory.Exists(dir)) return null;

        string preferred = Path.Combine(dir, PreferredExeName);
        if (File.Exists(preferred)) return preferred;

        string[] exes;
        try { exes = Directory.GetFiles(dir, "*.exe", SearchOption.TopDirectoryOnly); }
        catch { return null; }

        // Fuzzy "undertale" exe.
        foreach (string exe in exes)
        {
            string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
            if (IsHelperExe(name)) continue;
            if (name.Contains("undertale")) return exe;
        }

        // Last resort: a single non-helper exe in the folder.
        string? only = null;
        int count = 0;
        foreach (string exe in exes)
        {
            string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
            if (IsHelperExe(name)) continue;
            only = exe; count++;
        }
        return count == 1 ? only : null;
    }

    /// Names that are NOT the runnable game (uninstaller, helpers, the Steam shim).
    private static bool IsHelperExe(string nameLowerNoExt)
        => nameLowerNoExt.Contains("unins")  ||
           nameLowerNoExt.Contains("setup")  ||
           nameLowerNoExt.Contains("crash")  ||
           nameLowerNoExt.Contains("steam")  ||
           nameLowerNoExt.Contains("vcredist") ||
           nameLowerNoExt.Contains("dxsetup");

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Launch the patched Undertale exe. If no patched exe is resolved, throw with
    /// honest guidance (so the UI surfaces the real next step, not an opaque crash).
    private void LaunchPatchedGame()
    {
        string? exe = ResolvePatchedExe();
        if (exe == null)
            throw new FileNotFoundException(
                "Patched Undertale not found. In Archipelago, start the \"Undertale " +
                "Client\" and run /auto_patch <your Steam Undertale folder> once, then " +
                "point this plugin at the \\Undertale folder it creates (Settings). " +
                "Remember to also start the Undertale Client and connect there.",
                Path.Combine(ResolvePatchedDir() ?? "", PreferredExeName));

        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exe,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            UseShellExecute  = false,
        }) ?? throw new InvalidOperationException("Failed to start Undertale.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — Steam detection (adapted from the Stardew plugin) ────

    /// Best-effort Steam auto-detection of the Undertale BASE install:
    ///   1. Steam root from registry (HKCU SteamPath, then HKLM InstallPath).
    ///   2. Library roots from steamapps\libraryfolders.vdf (+ the Steam root).
    ///   3. appmanifest_391540.acf → "installdir" → steamapps\common\<installdir>.
    ///   4. Fall back to steamapps\common\Undertale.
    /// Returns the dir (validated to contain data.win or UNDERTALE.exe) or null.
    /// Never throws.
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
                            if (LooksLikeUndertale(candidate)) return candidate;
                        }
                    }

                    string std = Path.Combine(steamapps, "common", SteamCommonFolderName);
                    if (LooksLikeUndertale(std)) return std;
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

    /// True when a folder looks like a (base) Undertale install. The GameMaker game
    /// ships data.win + UNDERTALE.exe; /auto_patch requires data.win in the source.
    private static bool LooksLikeUndertale(string dir)
    {
        try
        {
            return Directory.Exists(dir) &&
                   (File.Exists(Path.Combine(dir, "data.win")) ||
                    File.Exists(Path.Combine(dir, PreferredExeName)));
        }
        catch { return false; }
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
    /// (e.g. appmanifest .acf: `"installdir"  "Undertale"`). First match or null.
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
    // Kept in this plugin's OWN JSON file (does not modify Core/SettingsStore).
    // BOM-less UTF-8.

    private sealed class UndertaleSettings
    {
        /// The patched Archipelago Undertale folder the user pointed us at, so the
        /// chosen directory survives across launcher restarts.
        public string? PatchedDir { get; set; }
    }

    private UndertaleSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<UndertaleSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(UndertaleSettings s)
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

    private void SavePatchedDir(string dir)
    {
        var s = LoadSettings();
        s.PatchedDir = dir;
        SaveSettings(s);
    }
}
