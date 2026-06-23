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
using System.Windows.Controls;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Faxanadu;

// ═══════════════════════════════════════════════════════════════════════════════
// FaxanaduPlugin — install / update / launch for "Faxanadu" (NES, 1989, Hudson/
// Nintendo) played through DAXANADU — a custom-made NES emulator by Daivuk that
// has a BUILT-IN Archipelago client. This is a NATIVE "ConnectsItself"
// integration (NOT a BizHawk / Lua emulator game): Daxanadu speaks to the AP
// server itself from its own in-game ARCHIPELAGO menu, exactly like Ship of
// Harkinian and APDOOM. It is modelled on Plugins/Doom/Doom1993Plugin.cs and
// Plugins/SoH/SoHPlugin.cs — same shape: GitHub-release install/update, bring-
// your-own ROM, ConnectsItself = true, settings in a self-contained JSON sidecar
// (so this plugin does NOT modify Core/SettingsStore).
//
// REALITY CHECK (2026-06-14) — facts verified online this session
// ─────────────────────────────────────────────────────────────────────────────
//   * REPO:    Daivuk/Daxanadu (https://github.com/Daivuk/Daxanadu) — the custom
//              NES emulator written specifically for AP Faxanadu (same author who
//              wrote the faxanadu apworld). Windows-only.
//   * RELEASE: latest tag "0.3.3" (2025-08-09), single asset
//              "Daxanadu_0_3_3.zip" (~3.3 MB). NOTE: the asset name is just
//              "Daxanadu_<ver>.zip" — it carries NO "win"/"x64" token (the build
//              is Windows-only, so there is exactly one zip per release). Older
//              releases (0.3.0 and earlier) ALSO bundled "faxanadu.apworld" and
//              "Faxanadu.yaml" as separate assets; 0.3.3 ships only the zip
//              (the world is in AP-main now). The resolver therefore matches the
//              game zip by "daxanadu*.zip"/lone-zip rather than a win pattern,
//              and fetches an apworld asset only if the resolved release has one.
//   * EXE:     "Daxanadu.exe" — VERIFIED (AP setup guide + Daxanadu setup steps).
//
// HOW IT CONNECTS (VERIFIED against the official Archipelago "Faxanadu" setup
// guide, https://archipelago.gg/tutorial/Faxanadu/setup_en, and the Daxanadu
// README):
//   Connection is done from an IN-GAME MENU — there are NO command-line args and
//   NO documented config file. The verified steps are:
//       "Launch Daxanadu.exe. From the Main menu, go to the `ARCHIPELAGO` menu.
//        Enter the server's address, slot name, and password. Then select PLAY."
//   This is the same "the game's own UI owns the slot" model as Ship of
//   Harkinian. AP allows one connection per slot, so the launcher must NOT hold
//   its own ApClient on the same slot while Daxanadu runs (the two would kick
//   each other off forever) — hence ConnectsItself = true. The launcher launches
//   with credential prefill ONLY (best effort) and suppresses its auto-reconnect
//   + "connection lost" toast while the game runs.
//
// GAME DATA — BRING-YOUR-OWN-ROM (§11):
//   Daxanadu ships NO commercial game data. The player supplies their own
//   Faxanadu NES ROM. VERIFIED requirement (README + setup guide): the ROM must
//   be the English (U) version, named EXACTLY "Faxanadu (U).nes", placed in the
//   same folder as Daxanadu.exe ("To run the game, you will need to put the
//   Faxanadu rom file into the same folder as Daxanadu" / "Copy your rom
//   `Faxanadu (U).nes` into the newly extracted folder"). This plugin lets the
//   user pick their ROM, validates it by CONTENT (the iNES magic — first 4 bytes
//   "NES\x1a" = 4E 45 53 1A — plus a plausible size), copies it into the
//   launcher's own ROM library (Games/ROMs/faxanadu/, original NEVER modified,
//   §11), and stages a copy named exactly "Faxanadu (U).nes" next to Daxanadu.exe
//   so the emulator finds it.
//   ROM facts: Faxanadu (U) is a standard NES cartridge dump. The common headered
//   dump is ~131,088 B (128 KB PRG + 16-byte iNES header, CHR-RAM); some dumps
//   that carry CHR-ROM are a bit larger. We accept a loose 96 KB – 1 MB window so
//   the common (U) and (U) (Rev A) dumps all pass and we do NOT gatekeep on a
//   single MD5 — Daxanadu itself is the authoritative validator at load (it
//   refuses anything but the English ROM).
//
// THE AP WORLD:
//   game string "Faxanadu" — VERIFIED (worlds/faxanadu/__init__.py →
//   FaxanaduWorld.game = "Faxanadu"; world authored by Daivuk). AP-main bundles
//   the stable world, so fetching the release's apworld is best-effort only.
//
// DEFENSIVE / UNVERIFIED DETAILS (flagged, "verify at build time"):
//   * CONNECTION PREFILL: because the documented connection path is an in-game
//     menu (no CLI, no documented config), there is nothing official to prefill.
//     We DEFENSIVELY drop a small "archipelago.json" next to Daxanadu.exe holding
//     the session host/port/slot/password under several plausible key spellings,
//     in case a future Daxanadu build reads it to pre-fill its ARCHIPELAGO menu.
//     If Daxanadu ignores the file (current behaviour), nothing breaks — the
//     player simply types the three values into the in-game menu once. The write
//     is best effort and NEVER blocks the launch. The post-install note is honest
//     about this ("you connect from Daxanadu's in-game ARCHIPELAGO menu").
//   * RELEASE ASSET NAME varies only by version ("Daxanadu_<ver>.zip"); resolved
//     by pattern, not a fixed string, with a pinned 0.3.3 direct-URL fallback for
//     when the GitHub API is unreachable.
//   * One launcher-side setting (the ROM path) is stored in this plugin's OWN
//     JSON sidecar (Games/ROMs/faxanadu/faxanadu_launcher.json) rather than in
//     Core/SettingsStore — this plugin is added as a single self-contained file
//     and deliberately does not modify shared launcher types (same choice as the
//     DOOM plugin).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class FaxanaduPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER = "Daivuk";
    private const string GITHUB_REPO  = "Daxanadu";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"{GH_RELEASES_URL}/latest";

    /// Official Archipelago Faxanadu setup guide.
    private const string SetupGuideUrl = "https://archipelago.gg/tutorial/Faxanadu/setup_en";

    /// Pinned fallback release used when the GitHub API is unreachable.
    /// "0.3.3" (2025-08-09); asset name + direct URL verified 2026-06-14.
    private const string FallbackVersion = "0.3.3";
    private const string FallbackZipName = "Daxanadu_0_3_3.zip";
    private static readonly string FallbackZipUrl =
        $"{RepoUrl}/releases/download/{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp, written after a successful install.
    private const string VersionFileName = "daxanadu_version.dat";

    /// EXACT ROM filename Daxanadu expects next to Daxanadu.exe (verified — the
    /// emulator only accepts the English ROM under this precise name).
    private const string StagedRomName = "Faxanadu (U).nes";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "faxanadu";
    public string DisplayName => "Faxanadu";
    public string Subtitle    => "Native PC · built-in Archipelago";

    /// EXACT AP game string — verified against worlds/faxanadu/__init__.py
    /// (FaxanaduWorld.game = "Faxanadu").
    public string ApWorldName => "Faxanadu";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "faxanadu.png");

    public string ThemeAccentColor => "#6E3B1E";   // Eolis brown / "dirt" theme

    public string[] GameBadges => new[] { "Requires Faxanadu ROM" };

    public string Description =>
        "Faxanadu (NES, 1989) played through Daxanadu — a custom NES emulator with " +
        "a built-in Archipelago client, written by the same author as the Faxanadu " +
        "apworld. Weapons, armor, magic, keys, and gold are shuffled into the " +
        "multiworld, and Daxanadu connects to the Archipelago server itself from " +
        "its own in-game ARCHIPELAGO menu — no separate emulator, no Lua bridge. " +
        "Daxanadu ships no game data: you must supply your own English Faxanadu NES " +
        "ROM, named \"Faxanadu (U).nes\". The launcher copies your ROM next to " +
        "Daxanadu (your original file is never modified); you then enter the server " +
        "address, slot name, and password in Daxanadu's ARCHIPELAGO menu and press " +
        "PLAY.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => ResolveGameExe() != null;
    public bool    IsRunning        { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where Daxanadu is installed.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Faxanadu");

    /// Preferred exe name (verified). Resolution falls back to a fuzzy match.
    private string PreferredExePath => Path.Combine(GameDirectory, "Daxanadu.exe");

    /// Where the release's faxanadu apworld is saved (when a release carries one)
    /// for the user to copy into Archipelago's custom_worlds folder.
    private string ApWorldLocalPath
    {
        get
        {
            string? name = _apWorldFileName;
            return Path.Combine(GameDirectory,
                string.IsNullOrEmpty(name) ? "faxanadu.apworld" : name);
        }
    }

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// The launcher's own ROM-library copy of the user's Faxanadu ROM (§11).
    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    /// Where the ROM must live next to the exe for Daxanadu to find it.
    private string StagedRomPath => Path.Combine(GameDirectory, StagedRomName);

    /// DEFENSIVE connection-prefill sidecar dropped next to Daxanadu.exe (see
    /// header). Not a documented Daxanadu file — harmless if it ignores it.
    private string ApConfigPath => Path.Combine(GameDirectory, "archipelago.json");

    /// This plugin's OWN settings sidecar (see header — kept out of the shared
    /// SettingsStore so the plugin is one self-contained source file).
    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "faxanadu_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    /// Filename of the apworld asset seen on the resolved release (so the saved
    /// copy keeps the upstream name). null until a release with one is resolved.
    private string? _apWorldFileName;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // Daxanadu's native AP client reports checks/items/goal to the AP server
    // itself — the launcher relays nothing. These exist for interface
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
        // 1. Resolve the latest release (pinned fallback when offline).
        progress.Report((2, "Checking latest Daxanadu release..."));
        var (version, zipUrl, apworldUrl, apworldName) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;
        _apWorldFileName = apworldName;

        // 2. Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"Daxanadu {version} is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a Windows download for Daxanadu on the GitHub " +
                "release page. Check your internet connection, or download the " +
                "build manually from " + RepoUrl + "/releases.");

        // 3. Download + extract the build.
        await DownloadAndExtractGameAsync(zipUrl, version, progress, ct);

        // 4. Fetch the apworld next to the install IF this release carries one
        //    (best effort; AP-main already bundles the stable world).
        if (apworldUrl != null)
        {
            try
            {
                progress.Report((85, "Downloading the Faxanadu apworld..."));
                byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                await File.WriteAllBytesAsync(ApWorldLocalPath, apworld, ct);
                progress.Report((92, $"{Path.GetFileName(ApWorldLocalPath)} saved — copy it into Archipelago's custom_worlds folder if you generate with it."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((92, "Could not download the apworld — the stable Faxanadu world also ships with Archipelago."));
            }
        }

        // 5. Stage the user's ROM next to the exe if they already picked one.
        StageRomForGame();

        // 6. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"Daxanadu {version} ready. Pick your \"Faxanadu (U).nes\" ROM in " +
            "Settings if you have not already, then press Play and connect from " +
            "Daxanadu's in-game ARCHIPELAGO menu."));
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
        string exe = ResolveGameExe()
            ?? throw new FileNotFoundException(
                "Daxanadu is not installed. Click Install Game first.",
                PreferredExePath);

        // Make sure the ROM is staged next to the exe (Daxanadu needs
        // "Faxanadu (U).nes" beside Daxanadu.exe).
        StageRomForGame();

        // DEFENSIVE prefill only: the documented connection path is Daxanadu's
        // own in-game ARCHIPELAGO menu (no CLI args, no documented config), so
        // there is nothing official to pass. We drop a best-guess archipelago.json
        // next to the exe in case a future build reads it; if not, the player
        // types the values into the in-game menu once. Never blocks the launch.
        try { WriteApConnectionConfig(session); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }

        StartGameProcess(exe);
        return Task.CompletedTask;
    }

    /// Daxanadu boots Faxanadu directly to its own menu; plain (non-AP) play is
    /// possible (the player just doesn't pick the ARCHIPELAGO menu), so a
    /// "Launch Standalone" button is offered.
    public bool SupportsStandalone => true;

    /// Daxanadu's native in-game AP client owns the slot connection (see header).
    /// The launcher must not connect its own ApClient to the same slot while the
    /// game runs.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        string exe = ResolveGameExe()
            ?? throw new FileNotFoundException(
                "Daxanadu is not installed. Click Install Game first.",
                PreferredExePath);

        // No connection prefill — the player simply doesn't open the in-game
        // ARCHIPELAGO menu (or connects manually later).
        StageRomForGame();
        StartGameProcess(exe);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        ScrubApConnectionPassword();   // plaintext credentials die with the session
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // Daxanadu's native client receives items from the AP server directly;
        // there is nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // Daxanadu renders its own AP status in-game; no launcher HUD channel.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Section: Install directory ────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "INSTALL DIRECTORY", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
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
                Title            = "Select Daxanadu install folder",
                InitialDirectory = Directory.Exists(GameDirectory) ? GameDirectory
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
            }
        };
        DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new TextBlock
        {
            Text       = IsInstalled ? "✓ Daxanadu is installed"
                                     : "Not installed (click Install in the Play tab)",
            FontSize   = 11, Foreground = IsInstalled ? success : muted,
            Margin     = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Faxanadu ROM ─────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "FAXANADU ROM", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });

        var romRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var romBox = new System.Windows.Controls.TextBox
        {
            Text = LoadRomPath() ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var romBtn = new System.Windows.Controls.Button
        {
            Content = "Select ROM...", Width = 110, Padding = new Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        romBtn.Click += (_, _) =>
        {
            if (PromptForRomFile())
                romBox.Text = LoadRomPath() ?? "";
        };
        DockPanel.SetDock(romBtn, Dock.Right);
        romRow.Children.Add(romBtn);
        romRow.Children.Add(romBox);
        panel.Children.Add(romRow);

        panel.Children.Add(new TextBlock
        {
            Text = "Daxanadu needs your own English Faxanadu NES ROM. It must be the " +
                   "US version — the launcher copies it into its own folder and stages " +
                   "a copy named exactly \"Faxanadu (U).nes\" next to Daxanadu.exe, which " +
                   "is the precise name the emulator looks for. Your original file is " +
                   "never modified.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        if (IsInstalled && File.Exists(ApWorldLocalPath))
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"{Path.GetFileName(ApWorldLocalPath)} is saved in the install folder — " +
                       @"copy it into your Archipelago custom_worlds folder (default: " +
                       @"C:\ProgramData\Archipelago\custom_worlds) if you generate with this build.",
                FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
            });
        }

        // ── How to connect ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "HOW TO CONNECT", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Daxanadu has its own built-in Archipelago client. Press Play, then in " +
                   "Daxanadu open the ARCHIPELAGO menu from the main menu, enter the " +
                   "server address, slot name, and password, and select PLAY. The " +
                   "launcher does not hold the slot while Daxanadu is running.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 8, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Daxanadu (GitHub) ↗",          RepoUrl),
            ("Faxanadu Setup Guide ↗",        SetupGuideUrl),
            ("Archipelago Official ↗",        "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2), Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12, Margin = new Thickness(0, 0, 0, 4),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => { try { System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
            panel.Children.Add(btn);
        }
        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc  = JsonDocument.Parse(json);
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

    /// "v0.3.3" / "0.3.3" → trimmed, leading 'v' stripped only when it decorates
    /// a digit. Returns null for null/blank tags.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest release: version + Windows zip asset URL + apworld
    /// asset URL + apworld filename. Daxanadu is Windows-only with exactly one zip
    /// per release ("Daxanadu_<ver>.zip"), so we match the game zip by name
    /// pattern / lone-zip rather than a win/x64 token. Older releases also bundle
    /// "faxanadu.apworld"; newer ones don't. Falls back to the pinned 0.3.3 direct
    /// URL when the API is unreachable.
    private async Task<(string Version, string? ZipUrl, string? ApWorldUrl, string? ApWorldName)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                var (zip, apworld, apworldName) = PickGameZipAndApworld(assets);
                if (zip != null)
                    return (version, zip, apworld, apworldName);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback below */ }

        // Offline fallback: pinned 0.3.3 zip (known asset URL). No apworld direct
        // URL pinned (AP-main ships the stable world anyway).
        return (FallbackVersion, FallbackZipUrl, null, null);
    }

    /// From a release's assets array, pick the Daxanadu game zip and the faxanadu
    /// apworld (if present). The game zip is "Daxanadu_<ver>.zip" — match a
    /// "daxanadu*.zip", else any lone non-source zip (defensive). Excludes the
    /// apworld and yaml side-assets.
    private static (string? Zip, string? ApWorld, string? ApWorldName)
        PickGameZipAndApworld(JsonElement assets)
    {
        string? zip = null, apworld = null, apworldName = null;
        string? anyZip = null;

        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();

            if (lower.EndsWith(".apworld"))
            {
                apworld     = url;
                apworldName = name;
            }
            else if (lower.EndsWith(".zip") && !lower.Contains("source"))
            {
                anyZip ??= url;                       // remember any plausible zip
                if (zip == null && lower.Contains("daxanadu"))
                    zip = url;                        // the game build
            }
        }

        zip ??= anyZip;                               // lone-zip fallback
        return (zip, apworld, apworldName);
    }

    // ── Private helpers — exe resolution ──────────────────────────────────────

    /// Resolve the installed exe: prefer "Daxanadu.exe", else any "*daxanadu*"
    /// exe in the install tree. Defensive — skips obvious helper exes.
    private string? ResolveGameExe()
    {
        if (File.Exists(PreferredExePath)) return PreferredExePath;
        if (!Directory.Exists(GameDirectory)) return null;
        try
        {
            foreach (string exe in Directory.EnumerateFiles(GameDirectory, "*.exe", SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (name.Contains("unins") || name.Contains("setup")) continue;
                if (name.Contains("daxanadu"))
                    return exe;
            }
        }
        catch { /* directory vanished mid-scan */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGameProcess(string exePath)
    {
        // Make sure the user's ROM is staged for Daxanadu before it boots.
        StageRomForGame();

        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = GameDirectory,
            UseShellExecute  = false,
        }) ?? throw new InvalidOperationException("Failed to start Daxanadu.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            ScrubApConnectionPassword();   // session over — blank password fields
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — ROM (bring-your-own) ────────────────────────────────

    /// Open the ROM picker, validate by CONTENT (iNES magic + plausible size),
    /// copy into the launcher's own ROM library (§11 — original never touched),
    /// persist the COPY's path, and stage it as "Faxanadu (U).nes" next to the exe
    /// if the game is installed. Returns true when a ROM was imported.
    private bool PromptForRomFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Select your Faxanadu (U) NES ROM",
            Filter = "NES ROM (*.nes)|*.nes|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return false;

        string? bad = ValidateFaxanaduRom(dlg.FileName);
        if (bad != null)
        {
            System.Windows.MessageBox.Show(bad, "Not a valid Faxanadu ROM",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return false;
        }

        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
            string dst = Path.Combine(RomLibraryDirectory, Path.GetFileName(dlg.FileName));
            File.Copy(dlg.FileName, dst, overwrite: true);
            SaveRomPath(dst);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Could not copy the ROM into the launcher library:\n{ex.Message}\n\n" +
                "Nothing was changed — your original file is untouched.",
                "ROM import failed", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return false;
        }

        StageRomForGame();
        return true;
    }

    /// Content check for a Faxanadu NES ROM: the first 4 bytes must be the iNES
    /// magic "NES\x1a" (4E 45 53 1A), and the size must be in a loose 96 KB – 1 MB
    /// window. Faxanadu (U) is a standard NES cartridge dump (~131,088 B headered:
    /// 128 KB PRG + 16-byte header, CHR-RAM; some dumps carry CHR-ROM and are a
    /// little larger). We accept loosely and do NOT gatekeep on a single MD5 —
    /// Daxanadu itself is the authoritative validator at load and refuses anything
    /// but the English ROM. Returns null when acceptable, else a short reason.
    private static string? ValidateFaxanaduRom(string path)
    {
        try
        {
            long len = new FileInfo(path).Length;
            const long min = 96L  * 1024;          // smaller than any real NES dump
            const long max = 1024L * 1024;         // generous upper bound
            if (len < min)
                return "That file is too small to be a Faxanadu NES ROM. Pick your " +
                       "\"Faxanadu (U).nes\" dump (about 128 KB).";
            if (len > max)
                return "That file is too large to be a Faxanadu NES ROM. Pick your " +
                       "\"Faxanadu (U).nes\" dump, not an archive or a different game.";

            using var fs = File.OpenRead(path);
            Span<byte> magic = stackalloc byte[4];
            if (fs.Read(magic) < 4)
                return "Could not read that file. Pick a different ROM and try again.";

            // iNES magic "NES\x1a" = 0x4E 0x45 0x53 0x1A.
            bool isINes = magic[0] == 0x4E && magic[1] == 0x45 &&
                          magic[2] == 0x53 && magic[3] == 0x1A;
            if (!isINes)
                return "That file is not an iNES ROM (its header is not \"NES\\x1a\"). " +
                       "Pick your \"Faxanadu (U).nes\" dump (the English NES version).";
        }
        catch
        {
            return "Could not read that file. Pick a different ROM and try again.";
        }
        return null;
    }

    /// Copy the user's library ROM to "Faxanadu (U).nes" next to the exe so
    /// Daxanadu finds it under the exact name it expects. Best effort — never
    /// throws into a launch/install.
    private void StageRomForGame()
    {
        try
        {
            string? lib = LoadRomPath();
            if (string.IsNullOrEmpty(lib) || !File.Exists(lib)) return;
            if (!Directory.Exists(GameDirectory)) return;

            // Only re-copy when missing or changed (cheap length compare).
            if (File.Exists(StagedRomPath))
            {
                try
                {
                    if (new FileInfo(StagedRomPath).Length == new FileInfo(lib).Length)
                        return;
                }
                catch { /* fall through and re-copy */ }
            }
            File.Copy(lib, StagedRomPath, overwrite: true);
        }
        catch { /* staging is a convenience — Daxanadu shows an error if missing */ }
    }

    // ── Private helpers — defensive connection prefill ────────────────────────

    /// DEFENSIVE only (see header): the documented connection path is Daxanadu's
    /// own in-game ARCHIPELAGO menu, so there is nothing official to prefill. We
    /// write a small archipelago.json next to the exe holding host/port/slot/
    /// password under several plausible key spellings, in case a future build
    /// reads it. BOM-less UTF-8. If Daxanadu ignores it, nothing breaks.
    private void WriteApConnectionConfig(ApSession session)
    {
        var (host, port) = ParseServerHostPort(session.ServerUri);
        Directory.CreateDirectory(GameDirectory);

        var doc = new Dictionary<string, object?>
        {
            ["server"]   = $"{host}:{port}",
            ["host"]     = host,
            ["port"]     = port,
            ["address"]  = $"{host}:{port}",
            ["slot"]     = session.SlotName,
            ["name"]     = session.SlotName,
            ["player"]   = session.SlotName,
            ["password"] = session.Password ?? "",
            ["game"]     = "Faxanadu",
        };

        File.WriteAllText(ApConfigPath,
            JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    /// Blank the password in archipelago.json once the session ends — the
    /// plaintext room password should not outlive the session on disk. Best
    /// effort; the next AP launch rewrites the file anyway.
    private void ScrubApConnectionPassword()
    {
        try
        {
            if (!File.Exists(ApConfigPath)) return;
            string text = File.ReadAllText(ApConfigPath);
            if (string.IsNullOrWhiteSpace(text)) return;

            var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text);
            if (root == null) return;

            var outRoot = new Dictionary<string, object?>();
            bool changed = false;
            foreach (var kv in root)
            {
                if (kv.Key.Contains("password", StringComparison.OrdinalIgnoreCase) &&
                    kv.Value.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrEmpty(kv.Value.GetString()))
                {
                    outRoot[kv.Key] = "";
                    changed = true;
                }
                else
                {
                    outRoot[kv.Key] = JsonElementToObject(kv.Value);
                }
            }
            if (!changed) return;

            File.WriteAllText(ApConfigPath,
                JsonSerializer.Serialize(outRoot, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* best effort — the next AP launch overwrites the file anyway */ }
    }

    /// Convert a JsonElement to a plain object so it round-trips through
    /// JsonSerializer.Serialize unchanged.
    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out long l) ? l : el.GetDouble(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.Null   => null,
        _                    => el.Clone(),
    };

    /// Accepts "archipelago.gg:38281", "ws://host:port", "wss://host:port", a
    /// bare hostname, and IPv6 literals (bracketed "[::1]:38281" or bare "::1").
    /// Default AP port is 38281.
    private static (string Host, int Port) ParseServerHostPort(string serverUri)
    {
        string s = serverUri.Trim();
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

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // This plugin keeps its one launcher-side setting (ROM path) in its OWN JSON
    // file so it stays a single self-contained source file and does not modify
    // Core/SettingsStore. BOM-less UTF-8.

    private sealed class FaxanaduSettings
    {
        public string? RomPath { get; set; }
    }

    private FaxanaduSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<FaxanaduSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(FaxanaduSettings s)
    {
        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — settings just won't persist this time */ }
    }

    private string? LoadRomPath()         => LoadSettings().RomPath;
    private void    SaveRomPath(string p) { var s = LoadSettings(); s.RomPath = p; SaveSettings(s); }

    // ── Private helpers — download/extract ────────────────────────────────────

    private async Task DownloadAndExtractGameAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"daxanadu-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((5, $"Downloading Daxanadu {version}..."));
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
                        progress.Report((pct, $"Downloading Daxanadu... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting..."));
            Directory.CreateDirectory(GameDirectory);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, GameDirectory, overwriteFiles: true);

            // Release zips often contain a single top-level sub-folder — flatten
            // it so the exe lands directly in GameDirectory. (ResolveGameExe scans
            // subdirectories too, so only flatten when nothing is at the root.)
            if (Directory.GetFiles(GameDirectory).Length == 0)
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

            progress.Report((80, "Game files extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }
}
