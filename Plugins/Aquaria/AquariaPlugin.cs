using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

// IMPORTANT (real project has BOTH <UseWPF>true</UseWPF> AND
// <UseWindowsForms>true</UseWindowsForms>): WPF UI types that collide with
// WinForms are FULLY QUALIFIED below (System.Windows.Controls.*,
// System.Windows.Media.*, System.Windows.Thickness, System.Windows.FontWeights,
// System.Windows.HorizontalAlignment, System.Windows.TextWrapping,
// System.Windows.MessageBox, …) to avoid CS0104 ambiguities. Do NOT add
// `using System.Windows.Controls;` / `using System.Windows.Media;` here.
using LauncherV2.Core;

namespace LauncherV2.Plugins.Aquaria;

// ═══════════════════════════════════════════════════════════════════════════════
// AquariaPlugin — install / update / launch for "Aquaria" played through the
// Aquaria Randomizer, a NATIVE "ConnectsItself" Archipelago integration (NOT a
// BizHawk / Lua emulator game): the game speaks to the AP server itself, exactly
// like Celeste 64, Meritous, Ship of Harkinian, the OpenTTD Archipelago fork, and
// APDOOM.
//
// Aquaria's ENGINE is open source (GPL — Bit-Blot / AquariaOSE), so the AP build
// is a fork of that engine with a built-in AP client. But the GAME DATA (the art,
// music, voice, scripts) is COMMERCIAL — the user owns Aquaria via Steam / itch /
// the Humble Bundle. So this plugin is a HYBRID of the two template shapes:
//   * the engine binary itself is a clean GitHub-release download (like Meritous /
//     Celeste 64), BUT
//   * it is NOT self-contained — it must be overlaid onto the user's own Aquaria
//     game folder, and it brings no assets (like Ship of Harkinian's bring-your-
//     own-ROM honesty).
// It is therefore modelled on Plugins/SoH/SoHPlugin.cs for the bring-your-own-data
// gate + Steam auto-detect, and on Plugins/Meritous/MeritousPlugin.cs for the
// clean release download + self-contained JSON sidecar (this plugin does NOT touch
// Core/SettingsStore — it keeps its data-folder setting in its own sidecar).
//
// REALITY CHECK (2026-06-14) — facts VERIFIED this session
// ─────────────────────────────────────────────────────────────────────────────
//   * THE AP WORLD: game string "Aquaria" — VERIFIED against the AP-main checkout
//     in this repo: mk64src/worlds/aquaria/__init__.py line 61
//     (AquariaWorld.game = "Aquaria"). World id "aquaria". Author Louis M / Tioui.
//
//   * THE AP FORK (randomizer engine) — VERIFIED via:
//       - the AP world's own bug_report_page in __init__.py line 23:
//         https://github.com/tioui/Aquaria_Randomizer/issues
//       - the official Archipelago "Aquaria" setup guide (the local copy at
//         mk64src/worlds/aquaria/docs/setup_en.md), which links the randomizer at
//         https://github.com/tioui/Aquaria_Randomizer/releases/latest
//     REPO: tioui/Aquaria_Randomizer.  Releases are NORMAL (non-prerelease) GitHub
//     releases, so /releases/latest works. Latest at time of writing (VERIFIED via
//     the GitHub releases API this session): tag "v1.5.2-Release" (version 1.5.2).
//     VERIFIED assets on that release:
//         "Aquaria_Randomizer-1.5.2-Windows.zip"   (the Windows build — preferred)
//         "aquaria.apworld"                         (the AP world)
//         "Aquaria_randomizer-1.5.2-wx3.0-linux-x86_64.tar.gz"  (Linux)
//         "Aquaria_randomizer-1.5.2-wx3.2-linux-x86_64.tar.gz"  (Linux)
//         "Aquaria_Randomizer-1.5.2-x86_64.AppImage"           (Linux)
//     The Windows zip is pinned as the offline fallback so a fresh install still
//     works when the GitHub API is unreachable.
//
//   * WINDOWS ZIP CONTENTS (VERIFIED, verbatim from the setup guide): the zip
//     contains aquaria_randomizer.exe, OpenAL32.dll, randomizer_files (directory),
//     SDL2.dll, usersettings.xml, wrap_oal.dll, cacert.pem. The EXE is
//     "aquaria_randomizer.exe". These files are COPIED INTO the Aquaria game
//     folder (overwriting on conflict) — the randomizer runs from inside it.
//
//   * BRING-YOUR-OWN DATA (VERIFIED, verbatim from the setup guide): "First, you
//     should copy the original Aquaria folder game. The randomizer will possibly
//     modify the game so that the original game will stop working. Copying the
//     folder will guarantee that the original game keeps on working. … Unzip the
//     Aquaria randomizer release and copy all unzipped files in the Aquaria game
//     folder." So the install model is: COPY the user's Aquaria folder, then
//     OVERLAY the randomizer files on the copy. This plugin does exactly that into
//     Games/Aquaria, so the user's ORIGINAL install is never modified (§11). The
//     mandatory Aquaria asset subdirs (VERIFIED against the AquariaOSE engine
//     README) are: data, gfx, mus, scripts, sfx, vox — used to content-validate
//     the folder the user points at.
//
//   * HOW IT CONNECTS (VERIFIED, verbatim from the setup guide): the randomizer is
//     driven by COMMAND-LINE ARGUMENTS, not a config file —
//         aquaria_randomizer.exe --name YourName --server theServer:thePort
//         aquaria_randomizer.exe --name YourName --server theServer:thePort --password thePassword
//     Running the exe with NO arguments opens an integrated launcher GUI instead.
//     There is also a local (non-multiworld) mode that takes a JSON seed file
//     (aquaria_randomizer.exe aquaria_randomized.json) — NOT used here. This plugin
//     launches with the VERIFIED --name / --server / --password flags. AP servers
//     allow one connection per slot, so — like Celeste 64 / Meritous / SoH /
//     OpenTTD / APDOOM — the launcher must NOT hold its own ApClient on the same
//     slot while the game runs: ConnectsItself = true. Because the credentials go
//     through CLI args (never written to disk by this plugin), there is no
//     plaintext-password file to scrub.
//
//   * Steam appid for Aquaria = 27530 (used to auto-suggest the user's data
//     folder). The setup guide says the game is "purchasable from most online game
//     stores" (Steam / itch / GOG / Humble) — Steam is just the auto-detect path.
//
// DEFENSIVE / UNVERIFIED DETAILS (flagged, "verify at build time"):
//   * The exact zip internal layout (whether the files sit at the zip root or in a
//     single wrapper subfolder) was not inspected offline. The extractor flattens a
//     lone wrapper subfolder so aquaria_randomizer.exe lands at the install root,
//     and ResolveGameExe() then prefers "aquaria_randomizer.exe", else any
//     "*aquaria*randomizer*" / "*randomizer*" exe (fuzzy), skipping helper exes.
//   * No data-path CLI flag is documented (the README confirms the exe must run
//     from inside the Aquaria folder). This plugin therefore OVERLAYS into a copy
//     of the data folder rather than passing a path — the verified model.
//   * The data-folder setting is kept in this plugin's OWN JSON sidecar
//     (Games/ROMs/aquaria/aquaria_launcher.json), NOT in Core/SettingsStore, so the
//     plugin stays a single self-contained source file.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class AquariaPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER = "tioui";
    private const string GITHUB_REPO  = "Aquaria_Randomizer";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";
    private const string GH_RELEASES_LATEST_URL = $"{GH_RELEASES_URL}/latest";

    /// Official Archipelago "Aquaria" setup guide.
    private const string SetupGuideUrl = "https://archipelago.gg/tutorial/Aquaria/setup_en";

    // Pinned fallback — the latest non-prerelease at time of writing, with the
    // VERIFIED asset name. Used ONLY when the GitHub API is unreachable so a fresh
    // install still works offline-of-the-API.
    private const string FallbackVersion = "1.5.2";
    private const string FallbackTag     = "v1.5.2-Release";
    private const string FallbackZipName = "Aquaria_Randomizer-1.5.2-Windows.zip";
    private static readonly string FallbackZipUrl =
        $"{RepoUrl}/releases/download/{FallbackTag}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp, written after a successful install.
    private const string VersionFileName = "aquaria_randomizer_version.dat";

    /// The VERIFIED randomizer exe name (inside the Windows zip / install root).
    private const string PreferredExeName = "aquaria_randomizer.exe";

    /// Steam — Aquaria appid 27530.
    private const string SteamAppId = "27530";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// Mandatory Aquaria asset subdirs (VERIFIED against the AquariaOSE engine
    /// README). The commercial game ships these; the open-source engine cannot.
    /// We treat a folder as a real Aquaria install when it contains the art/audio
    /// folders the engine reads at runtime.
    private static readonly string[] AquariaAssetDirs =
        { "data", "gfx", "mus", "scripts", "sfx", "vox" };

    /// Default Archipelago port (used when the server URI carries no port).
    private const int DefaultApPort = 38281;

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "aquaria";
    public string DisplayName => "Aquaria";
    public string Subtitle    => "Native PC · built-in Archipelago";

    /// EXACT AP game string — VERIFIED against worlds/aquaria/__init__.py
    /// (AquariaWorld.game = "Aquaria").
    public string ApWorldName => "Aquaria";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "aquaria.png");

    public string ThemeAccentColor => "#2A7E9A";   // deep-sea teal
    public string[] GameBadges     => new[] { "Needs Aquaria game data" };

    public string Description =>
        "Aquaria is a side-scrolling action-adventure by Bit-Blot: Naija explores a " +
        "vast underwater world, singing songs that shift her form, move the world, " +
        "and fight what lurks below. This is the Aquaria Randomizer — a fork of the " +
        "open-source Aquaria engine with a built-in Archipelago client, so songs, " +
        "forms and upgrades are shuffled into the multiworld and the game connects " +
        "to the Archipelago server itself — no emulator, no Lua bridge. The engine " +
        "is open source, but the art, music and voice are not: you supply your own " +
        "copy of Aquaria (Steam, itch, GOG or the Humble Bundle). The launcher " +
        "downloads the randomizer, overlays it onto a private copy of your game so " +
        "your original install is never touched, and launches you straight into the " +
        "multiworld.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => ResolveGameExe() != null;
    public bool    IsRunning        { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the randomizer is overlaid onto a private copy of the
    /// user's Aquaria game data. This is the PLAYABLE install (original untouched).
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Aquaria");

    /// Preferred exe (VERIFIED name). Resolution falls back to a fuzzy match.
    private string PreferredExePath => Path.Combine(GameDirectory, PreferredExeName);

    /// Where the release's aquaria.apworld is saved for the user to copy into
    /// Archipelago's custom_worlds folder.
    private string ApWorldLocalPath
    {
        get
        {
            string? name = _apWorldFileName;
            return Path.Combine(GameDirectory,
                string.IsNullOrEmpty(name) ? "aquaria.apworld" : name);
        }
    }

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore so
    /// the plugin stays a single self-contained source file). Lives under the
    /// launcher's ROM-library tree for consistency with the other native plugins.
    private string SettingsSidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                        "aquaria_launcher.json");

    private string SettingsSidecarDir => Path.GetDirectoryName(SettingsSidecarPath)!;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    /// Filename of the apworld asset seen on the resolved release (so the saved
    /// copy keeps the upstream name). null until a release is resolved.
    private string? _apWorldFileName;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The randomizer's native AP client reports checks/items/goal to the AP server
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
        // 0. The randomizer is an OVERLAY on the user's own Aquaria data — we can
        //    only build a playable install if we know where that data is.
        string? dataDir = ResolveAquariaDataDir();
        if (dataDir == null)
            throw new InvalidOperationException(
                "Aquaria's game data has not been located yet. Open this game's " +
                "Settings and point the launcher at your Aquaria folder (the one " +
                "containing the gfx, mus and data folders) — you can buy Aquaria on " +
                "Steam, itch.io, GOG or in the Humble Bundle. The launcher copies it " +
                "into its own folder, so your original install is never modified.");

        // 1. Resolve the latest release (pinned fallback when offline).
        progress.Report((2, "Checking latest Aquaria Randomizer release..."));
        var (version, zipUrl, apworldUrl, apworldName) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;
        _apWorldFileName = apworldName;

        // 2. Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"Aquaria Randomizer {version} is up to date."));
            return;
        }

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a Windows download for the Aquaria Randomizer on the " +
                "GitHub release page. Check your internet connection, or download the " +
                "build manually from " + RepoUrl + "/releases.");

        // 3. Stage a PRIVATE copy of the user's Aquaria data into GameDirectory
        //    (original never modified). This is the verified install model.
        progress.Report((4, "Copying your Aquaria game data (this can take a moment)..."));
        await Task.Run(() => CopyAquariaData(dataDir, progress, ct), ct);

        // 4. Download + overlay the randomizer files on top of the copy.
        await DownloadAndOverlayRandomizerAsync(zipUrl, version, progress, ct);

        // 5. Fetch the apworld next to the install (best effort).
        if (apworldUrl != null)
        {
            try
            {
                progress.Report((90, "Downloading the Aquaria apworld..."));
                byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                Directory.CreateDirectory(GameDirectory);
                await File.WriteAllBytesAsync(ApWorldLocalPath, apworld, ct);
                progress.Report((94, $"{Path.GetFileName(ApWorldLocalPath)} saved — copy it into Archipelago's custom_worlds folder if you generate with this build."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((94, "Could not download the apworld — get it from the GitHub release page (the stable world also ships with Archipelago)."));
            }
        }

        // 6. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"Aquaria Randomizer {version} ready. Press Play to connect — the " +
            "launcher passes your slot and server to the randomizer on the command line."));
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
                "Aquaria Randomizer is not installed. Click Install Game first " +
                "(and point the launcher at your Aquaria game data in Settings).",
                PreferredExePath);

        // VERIFIED connection path: the randomizer takes the connection on the
        // COMMAND LINE — aquaria_randomizer.exe --name <slot> --server <host:port>
        // [--password <pw>]. No config file is written by this plugin, so there is
        // no plaintext credential left on disk.
        StartGameProcess(exe, BuildApArguments(session));
        return Task.CompletedTask;
    }

    /// Aquaria (with your own data + the randomizer) is a complete game; the
    /// randomizer's own integrated launcher GUI also works without a connection.
    public bool SupportsStandalone => true;

    /// The randomizer's native in-game AP client owns the slot connection (see
    /// header). The launcher must not connect its own ApClient to the same slot
    /// while the game runs.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        string exe = ResolveGameExe()
            ?? throw new FileNotFoundException(
                "Aquaria Randomizer is not installed. Click Install Game first " +
                "(and point the launcher at your Aquaria game data in Settings).",
                PreferredExePath);

        // No AP arguments — running the exe with no args opens the randomizer's
        // own integrated launcher GUI (VERIFIED: "the easiest one is using the
        // launcher … just run the aquaria_randomizer.exe file").
        StartGameProcess(exe, arguments: null);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // Credentials are passed via CLI args, never written to disk by this
        // plugin — so there is no plaintext-password file to scrub. Defensively
        // blank a password in any AP-style config remnant just in case a local
        // mode / future build wrote one.
        ScrubPlaintextPasswordRemnants();
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The randomizer's native client receives items from the AP server
        // directly; there is nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The randomizer renders its own AP status in-game; no launcher HUD channel.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(
            System.Windows.Media.Color.FromRgb(0xD0, 0x90, 0x40));
        var panel   = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        // ── Section: Install directory ────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "INSTALL DIRECTORY", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = GameDirectory, IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Browse...", Width = 90,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select the Aquaria Randomizer install folder",
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
            Text       = IsInstalled ? "✓ Aquaria Randomizer is installed"
                                     : "Not installed (set your Aquaria data below, then Install in the Play tab)",
            FontSize   = 11, Foreground = IsInstalled ? success : muted,
            Margin     = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Aquaria game data (bring-your-own) ───────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "AQUARIA GAME DATA", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });

        var dataRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var dataBox = new System.Windows.Controls.TextBox
        {
            Text = ResolveAquariaDataDir() ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var dataStatus = new System.Windows.Controls.TextBlock
        {
            FontSize = 11, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        };
        void RefreshDataStatus()
        {
            string? d = ResolveAquariaDataDir();
            if (d != null)
            {
                dataBox.Text       = d;
                dataStatus.Text    = "✓ Aquaria game data found.";
                dataStatus.Foreground = success;
            }
            else
            {
                dataStatus.Text    = "No Aquaria game data set. Pick the folder that " +
                                     "contains the gfx, mus and data folders.";
                dataStatus.Foreground = warn;
            }
        }

        var dataBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dataBtn.Click += (_, _) =>
        {
            if (PromptForAquariaDataDir()) RefreshDataStatus();
        };
        System.Windows.Controls.DockPanel.SetDock(dataBtn, System.Windows.Controls.Dock.Right);
        dataRow.Children.Add(dataBtn);
        dataRow.Children.Add(dataBox);
        panel.Children.Add(dataRow);

        // Steam auto-detect helper button.
        var detectBtn = new System.Windows.Controls.Button
        {
            Content = "Detect Steam copy (appid 27530)",
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            Padding = new System.Windows.Thickness(8, 3, 8, 3),
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x14, 0x18, 0x28)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        detectBtn.Click += (_, _) =>
        {
            string? steam = DetectSteamAquariaDir();
            if (steam != null)
            {
                SaveDataDir(steam);
                RefreshDataStatus();
                System.Windows.MessageBox.Show(
                    $"Found your Steam copy of Aquaria:\n{steam}",
                    "Aquaria detected",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
            else
            {
                System.Windows.MessageBox.Show(
                    "Could not find a Steam copy of Aquaria automatically. Use " +
                    "\"Select folder...\" to point at your Aquaria folder (the one " +
                    "containing the gfx, mus and data folders).",
                    "Aquaria not detected",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
            }
        };
        panel.Children.Add(detectBtn);
        RefreshDataStatus();
        panel.Children.Add(dataStatus);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Aquaria's engine is open source, but its art, music and voice are " +
                   "not — you supply your own copy of the game (Steam, itch.io, GOG or " +
                   "the Humble Bundle). The launcher copies your Aquaria folder into its " +
                   "own install and overlays the randomizer on the copy, so your " +
                   "original game is never modified. Steam installs are detected " +
                   "automatically (appid 27530).",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: How it connects ──────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "ARCHIPELAGO CONNECTION", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "The Aquaria Randomizer takes your connection on the command line. " +
                   "When you press Play, the launcher runs aquaria_randomizer.exe with " +
                   "--name (your slot), --server (host:port) and --password if your room " +
                   "has one, so you connect straight away — no file to edit, and nothing " +
                   "is written to disk. Launching without a connection opens the " +
                   "randomizer's own integrated launcher instead.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        if (IsInstalled && File.Exists(ApWorldLocalPath))
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"{Path.GetFileName(ApWorldLocalPath)} is saved in the install folder — " +
                       @"copy it into your Archipelago custom_worlds folder (default: " +
                       @"C:\ProgramData\Archipelago\custom_worlds) if you generate with this build.",
                FontSize = 11, Foreground = muted,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 12),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Aquaria Randomizer (GitHub) ↗", RepoUrl),
            ("Aquaria Setup Guide ↗",         SetupGuideUrl),
            ("Archipelago Official ↗",        "https://archipelago.gg"),
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
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
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

    /// "v1.5.2-Release" → "1.5.2"; "v1.4.1" → "1.4.1"; otherwise the tag is
    /// returned trimmed. Returns null for null/blank tags. Strips a leading 'v'
    /// that decorates a digit, then drops a trailing "-Release"/"-release" suffix
    /// (this fork tags as "v<ver>-Release").
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        if (tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1]))
            tag = tag[1..];
        foreach (string suffix in new[] { "-Release", "-release", "-RELEASE" })
        {
            if (tag.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                tag = tag[..^suffix.Length];
                break;
            }
        }
        return tag;
    }

    /// Resolve the latest release: version + Windows zip asset URL + apworld asset
    /// URL + apworld filename. This fork publishes normal (non-prerelease)
    /// releases, so /releases/latest is the right endpoint. Falls back to the
    /// pinned v1.5.2 Windows direct URL when the API is unreachable.
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
                var (zip, apworld, apworldName) = PickWindowsAndApworld(assets);
                if (zip != null)
                    return (version, zip, apworld, apworldName);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback below */ }

        // Offline fallback: v1.5.2 Windows, known asset URL. No apworld direct URL
        // is pinned (AP-main ships the stable world anyway).
        return (FallbackVersion, FallbackZipUrl, null, null);
    }

    /// From a release's assets array, pick the Windows .zip (VERIFIED pattern
    /// "Aquaria_Randomizer-<ver>-Windows.zip"; match broadly on win/windows,
    /// excluding linux/AppImage/tar/source) and the aquaria apworld.
    private static (string? Zip, string? ApWorld, string? ApWorldName)
        PickWindowsAndApworld(JsonElement assets)
    {
        string? winZip = null, anyZip = null;
        string? apworld = null, apworldName = null;

        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();

            if (lower.EndsWith(".apworld") && lower.Contains("aquaria"))
            {
                apworld     = url;
                apworldName = name;
            }
            else if (lower.EndsWith(".zip") &&
                     !lower.Contains("source") &&
                     !lower.Contains("linux") &&
                     !lower.Contains("appimage") &&
                     !lower.Contains("mac") &&
                     !lower.Contains("osx") &&
                     !lower.Contains("darwin"))
            {
                anyZip ??= url;   // remember any plausible game zip
                if (lower.Contains("windows") || lower.Contains("win64") ||
                    lower.Contains("win32")   || lower.Contains("win-")  ||
                    lower.Contains("-win")    || lower.Contains("x64")    ||
                    lower.Contains("x86_64"))
                    winZip ??= url;
            }
        }

        // Prefer an explicitly Windows-tagged zip, else any non-Linux zip.
        return (winZip ?? anyZip, apworld, apworldName);
    }

    // ── Private helpers — exe resolution ──────────────────────────────────────

    /// Resolve the installed exe: prefer "aquaria_randomizer.exe", then any
    /// "*randomizer*" / "*aquaria*" exe in the install (fuzzy), skipping obvious
    /// helper/uninstaller exes. Defensive — the exact zip layout was not inspected
    /// offline.
    private string? ResolveGameExe()
    {
        if (File.Exists(PreferredExePath)) return PreferredExePath;
        if (!Directory.Exists(GameDirectory)) return null;
        try
        {
            string? fallback = null;
            foreach (string exe in Directory.EnumerateFiles(GameDirectory, "*.exe", SearchOption.AllDirectories))
            {
                string baseName = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (baseName.Contains("unins") || baseName.Contains("setup") ||
                    baseName.Contains("crash")  || baseName.Contains("aqconfig"))
                    continue;
                if (baseName.Contains("randomizer"))
                    return exe;                       // best match
                if (fallback == null && baseName.Contains("aquaria"))
                    fallback = exe;                   // weaker match (e.g. Aquaria.exe)
            }
            return fallback;
        }
        catch { /* directory vanished mid-scan */ }
        return null;
    }

    // ── Private helpers — Aquaria data folder (bring-your-own) ────────────────

    /// The user's Aquaria data folder: the override saved in this plugin's sidecar
    /// if it is still valid, else a Steam auto-detect, else null. (Auto-detect is
    /// only a suggestion — the value is not persisted until the user confirms via
    /// the Settings picker or the Detect button.)
    private string? ResolveAquariaDataDir()
    {
        string? saved = LoadSettings().AquariaDataDir;
        if (!string.IsNullOrWhiteSpace(saved) && LooksLikeAquariaDataDir(saved))
            return saved;
        return DetectSteamAquariaDir();
    }

    /// Open the folder picker, validate by content, persist to the sidecar.
    /// Returns true when a valid Aquaria folder was chosen.
    private bool PromptForAquariaDataDir()
    {
        var dlg = new OpenFolderDialog
        {
            Title            = "Select your Aquaria game folder (contains gfx, mus, data)",
            InitialDirectory = DetectSteamAquariaDir() ?? AppContext.BaseDirectory,
        };
        if (dlg.ShowDialog() != true) return false;

        string folder = dlg.FolderName;
        if (!LooksLikeAquariaDataDir(folder))
        {
            System.Windows.MessageBox.Show(
                "That folder does not look like an Aquaria install. Pick the folder " +
                "that contains the Aquaria game data — it should have the gfx, mus and " +
                "data folders inside it.",
                "Not an Aquaria folder",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return false;
        }

        SaveDataDir(folder);
        return true;
    }

    /// Content check for an Aquaria install folder: at least three of the verified
    /// mandatory asset subdirs (data, gfx, mus, scripts, sfx, vox) are present.
    /// (We require several so a random folder that merely has a "data" subdir does
    /// not pass.) The original install is only ever READ, never modified.
    private static bool LooksLikeAquariaDataDir(string? dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            int hits = 0;
            foreach (string sub in AquariaAssetDirs)
                if (Directory.Exists(Path.Combine(dir, sub))) hits++;
            // gfx + mus are the strongest commercial-asset signals; require the
            // art folder plus a couple of the others.
            return hits >= 3 && Directory.Exists(Path.Combine(dir, "gfx"));
        }
        catch { return false; }
    }

    /// Persist the chosen Aquaria data folder to this plugin's OWN sidecar.
    private void SaveDataDir(string folder)
    {
        var s = LoadSettings();
        s.AquariaDataDir = folder;
        SaveSettings(s);
    }

    /// Copy the user's Aquaria data folder into GameDirectory (a private,
    /// modifiable copy — the original is never touched). Skips an existing
    /// identical destination file so re-installs are cheap. Existing randomizer
    /// files in the destination are preserved (the overlay step rewrites them).
    private void CopyAquariaData(
        string sourceDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(GameDirectory);

        // Enumerate once so we can show coarse progress.
        var files = new List<string>(
            Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories));
        int total = files.Count;
        int done  = 0;

        foreach (string src in files)
        {
            ct.ThrowIfCancellationRequested();
            string rel = Path.GetRelativePath(sourceDir, src);
            string dst = Path.Combine(GameDirectory, rel);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                // Skip if an up-to-date copy already exists (idempotent re-install).
                if (!File.Exists(dst) ||
                    new FileInfo(dst).Length != new FileInfo(src).Length)
                {
                    File.Copy(src, dst, overwrite: true);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* skip an unreadable/locked source file; the overlay still works */ }

            done++;
            if (total > 0 && (done % 64 == 0 || done == total))
            {
                int pct = (int)(4 + 60.0 * done / total);   // 4 → ~64%
                progress.Report((pct, $"Copying your Aquaria game data... {done}/{total} files"));
            }
        }
    }

    // ── Private helpers — Steam auto-detect (Aquaria appid 27530) ─────────────

    /// True when `dir` looks like an Aquaria install AND has a Windows Aquaria exe
    /// or the asset folders. (Steam ships Aquaria.exe on Windows.)
    private static bool LooksLikeSteamAquaria(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, "Aquaria.exe"))) return true;
            return LooksLikeAquariaDataDir(dir);
        }
        catch { return false; }
    }

    /// Detect the Steam Aquaria install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_27530.acf exists → steamapps\common\<installdir>.
    private static string? DetectSteamAquariaDir()
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

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeSteamAquaria(candidate)) return candidate;
                    }
                    // Fall back to the conventional "Aquaria" folder name.
                    string conventional = Path.Combine(common, "Aquaria");
                    if (LooksLikeSteamAquaria(conventional)) return conventional;
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

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    /// Steam stores its SteamPath with forward slashes; normalize for Path APIs.
    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    /// All Steam library roots: the Steam root itself plus every "path" entry in
    /// steamapps\libraryfolders.vdf.
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

    // ── Private helpers — connection arguments (VERIFIED CLI) ─────────────────

    /// Build the VERIFIED randomizer CLI for an AP launch:
    ///   --name <slot> --server <host:port> [--password <pw>]
    /// Returns a ready-to-use argument string (each value individually quoted to
    /// survive spaces). Credentials live only in the process arguments — this
    /// plugin never writes them to disk.
    private static string BuildApArguments(ApSession session)
    {
        var (host, port) = ParseServerHostPort(session.ServerUri);
        string server = host.Contains(':') ? $"[{host}]:{port}" : $"{host}:{port}";

        var sb = new StringBuilder();
        sb.Append("--name ").Append(Quote(session.SlotName));
        sb.Append(" --server ").Append(Quote(server));
        if (!string.IsNullOrEmpty(session.Password))
            sb.Append(" --password ").Append(Quote(session.Password));
        return sb.ToString();
    }

    /// Quote a single CLI argument value for Windows process-argument parsing
    /// (wrap in double quotes, escaping any embedded quotes/backslash-runs).
    private static string Quote(string value)
    {
        if (value.Length == 0) return "\"\"";
        var sb = new StringBuilder();
        sb.Append('"');
        int backslashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { backslashes++; continue; }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                backslashes = 0;
                sb.Append('"');
                continue;
            }
            if (backslashes > 0) { sb.Append('\\', backslashes); backslashes = 0; }
            sb.Append(c);
        }
        if (backslashes > 0) sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    /// Accepts "archipelago.gg:38281", "ws://host:port", "wss://host:port", a bare
    /// hostname, and IPv6 literals (bracketed "[::1]:38281" or bare "::1").
    /// Default AP port is 38281. Returns the host WITHOUT brackets.
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
        int    port = DefaultApPort;

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

    /// Defensive: blank a "password" field in any AP-style JSON config remnant in
    /// the install (e.g. a local-mode seed file) so a plaintext room password does
    /// not outlive the session on disk. This plugin does not write such a file
    /// itself (credentials go via CLI), so this is purely a safety net.
    private void ScrubPlaintextPasswordRemnants()
    {
        try
        {
            if (!Directory.Exists(GameDirectory)) return;
            foreach (string cfg in new[]
                     {
                         Path.Combine(GameDirectory, "aquaria_randomized.json"),
                         Path.Combine(GameDirectory, "aquaria_randomizer.json"),
                     })
            {
                if (!File.Exists(cfg)) continue;
                try
                {
                    string text = File.ReadAllText(cfg);
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    var root = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text);
                    if (root == null) continue;

                    bool changed = false;
                    var outRoot = new Dictionary<string, object?>();
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
                    if (changed)
                        File.WriteAllText(cfg,
                            JsonSerializer.Serialize(outRoot, new JsonSerializerOptions { WriteIndented = true }),
                            new UTF8Encoding(false));
                }
                catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }

    /// Convert a JsonElement to a plain object so it round-trips through
    /// JsonSerializer.Serialize unchanged (used to preserve unknown keys).
    private static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt64(out long l) ? l : el.GetDouble(),
        JsonValueKind.True   => true,
        JsonValueKind.False  => false,
        JsonValueKind.Null   => null,
        _                    => el.Clone(),   // objects/arrays preserved as-is
    };

    // ── Private helpers — process ─────────────────────────────────────────────

    private void StartGameProcess(string exePath, string? arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = GameDirectory,   // the randomizer runs from inside the data folder
            UseShellExecute  = false,
        };
        if (!string.IsNullOrEmpty(arguments))
            psi.Arguments = arguments;

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start the Aquaria Randomizer.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            ScrubPlaintextPasswordRemnants();
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────
    // Kept in this plugin's OWN JSON file so it stays a single self-contained
    // source file and does NOT modify Core/SettingsStore. Holds the user's Aquaria
    // data-folder path (the only launcher-side setting this game needs).

    private sealed class AquariaSettings
    {
        /// Absolute path to the user's Aquaria game folder (the one with gfx/mus/
        /// data). Read-only source — the launcher copies from it, never into it.
        [JsonPropertyName("aquariaDataDir")]
        public string? AquariaDataDir { get; set; }
    }

    private AquariaSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<AquariaSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(AquariaSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — settings just won't persist this time */ }
    }

    // ── Private helpers — download/overlay ────────────────────────────────────

    /// Download the randomizer Windows zip and OVERLAY its files onto GameDirectory
    /// (which already holds the private copy of the user's Aquaria data). The
    /// randomizer files overwrite on conflict, exactly as the setup guide says.
    private async Task DownloadAndOverlayRandomizerAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"aquaria-randomizer-{version}-{Guid.NewGuid():N}.zip");
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"aquaria-randomizer-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((66, $"Downloading Aquaria Randomizer {version}..."));
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
                        int pct = (int)(66 + 16 * downloaded / total);   // 66 → 82%
                        progress.Report((pct, $"Downloading Aquaria Randomizer... {downloaded / 1_000_000}MB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((84, "Extracting the randomizer..."));
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            // If the zip wraps everything in a single subfolder, descend into it so
            // aquaria_randomizer.exe overlays at the install root.
            string overlayRoot = tempDir;
            if (Directory.GetFiles(tempDir).Length == 0)
            {
                string[] subdirs = Directory.GetDirectories(tempDir);
                if (subdirs.Length == 1) overlayRoot = subdirs[0];
            }

            // Overlay every randomizer file onto GameDirectory (overwrite on
            // conflict — the verified instruction).
            Directory.CreateDirectory(GameDirectory);
            foreach (string fileSrc in Directory.EnumerateFiles(overlayRoot, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested();
                string rel     = Path.GetRelativePath(overlayRoot, fileSrc);
                string fileDst = Path.Combine(GameDirectory, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                File.Copy(fileSrc, fileDst, overwrite: true);
            }

            progress.Report((88, "Randomizer overlaid onto your Aquaria copy."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
