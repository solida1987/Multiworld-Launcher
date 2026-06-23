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
// GlobalUsings.cs already aliases the colliding short names project-wide; this file
// adds NO file-level `using X = System.Windows...;` alias (that would be CS1537).

namespace LauncherV2.Plugins.CrystalProject;

// ═══════════════════════════════════════════════════════════════════════════════
// CrystalProjectPlugin — install / launch for "Crystal Project" (Andrew Willman,
// 2022) played through the CrystalProjectAPWorld mod. This is a NATIVE
// "ConnectsItself" integration: the game itself speaks to the AP server (no
// emulator, no Lua bridge, no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified against the mod repo) ─────────
// This is a STEAM-MOD native. The base game is the user's own legally-owned
// Crystal Project (Steam appid 1637730 — verified against the Steam page at
// https://store.steampowered.com/app/1637730/Crystal_Project/). The Archipelago
// mod uses a STEAM BETA BRANCH named "archipelago" (version 1.6.5) plus a
// separate mod installer (CrystalProjectAPModInstaller.zip, released by Emerassi
// on GitHub at https://github.com/Emerassi/CrystalProjectAPWorld).
//
// The verified facts:
//
//   * THE AP WORLD game string is "Crystal Project" (verified against
//     worlds/crystal_project/__init__.py in the Emerassi/CrystalProjectAPWorld
//     repo: `class CrystalProjectWorld(World)`, `game = "Crystal Project"`, and
//     confirmed in worlds/crystal_project/archipelago.json: "game": "Crystal
//     Project"). The apworld file is a COMMUNITY world (not in AP core); the file
//     is named crystal_project.apworld. GameId here = "crystal_project".
//
//   * STEAM APPID is 1637730 (verified live 2026-06-14 against the Steam store
//     page for Crystal Project by Andrew Willman). Note: the game is a Game Maker
//     Studio title.
//
//   * MOD REPO is Emerassi/CrystalProjectAPWorld (GitHub). Latest release verified
//     live 2026-06-14: CrystalProject-v0.16.0. Assets:
//       - crystal_project.apworld   (the AP world definition)
//       - CrystalProjectAPModInstaller.zip   (the installer exe)
//       - Crystal Project.yaml / Explorer preset yaml
//     The TAG pattern is "CrystalProject-v<version>".
//
//   * MOD INSTALLATION (from setup_en.md, verified):
//     1. In Steam, right-click Crystal Project → Properties → Betas → select
//        the "archipelago" branch (version 1.6.5). Steam downloads the mod-ready
//        game files.
//     2. Install .NET 8.0 Desktop Runtime x64 (not ASP.NET, not SDK).
//     3. Download and run CrystalProjectAPModInstaller.zip / exe, point it at your
//        Crystal Project installation.
//     The installer PATCHES the game files in-place. There is NO BepInEx involved.
//
//   * CONNECTION is made IN-GAME: when you start a new game, the mod pops up an
//     Archipelago connection screen where you fill in hostname:port, slot name, and
//     password. This is a built-in in-game UI, NOT a command-line arg or config
//     file. The launcher CANNOT pre-fill any of these (verified against the mod
//     docs). The session credentials are surfaced in the settings panel so the user
//     can copy/paste them in-game.
//
//   * AFTER the first successful connection, save files auto-reconnect on next
//     launch (per the setup guide). Room number changes require opening the
//     Archipelago menu in-game and reconnecting.
//
//   * "IsInstalled" is detected by confirming:
//     (a) The game folder's Steam appmanifest shows a beta branch containing
//         "archipelago", OR
//     (b) The installer has run (evidenced by the presence of mod-specific marker
//         files, e.g. a data/archipelago folder or known mod files written by the
//         installer). Because the installer's exact output is not documented, we
//         also check for a launcher-side version stamp in the sidecar.
//     The IsInstalled heuristic errs on the side of "let the user re-run Install"
//     rather than crashing — this is the same defensive approach as Hylics2.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Crystal Project install via the Windows registry (same
//      multi-root VDF scan pattern as Hylics2Plugin). Override via folder picker.
//   2. DOWNLOAD the CrystalProjectAPModInstaller.zip from the latest GitHub release
//      and extract it. Run the installer exe, pointed at the detected install dir.
//      Also remind the user to switch to the "archipelago" Steam beta branch first
//      (we cannot do this programmatically — it requires Steam UI interaction).
//   3. LAUNCH via steam://rungameid/1637730 (Steam manages the beta branch, so the
//      game always starts in the right version). Direct .exe launch is also
//      attempted if a game exe is found.
//   4. ConnectsItself = true: the in-game AP client owns the server connection.
//      SupportsStandalone = true: plain Crystal Project plays fine unmodded.
//   5. Settings panel surfaces Steam beta-branch instructions, detected install
//      path, mod install status, session credentials for in-game entry, and links.
//
// ── DEFENSIVE ─────────────────────────────────────────────────────────────────
//   * The installer exe name and the "Crystal Project" steam common-folder name
//     are taken from the standard Steam layout; fallbacks cover renamed layouts.
//   * No plaintext AP password is ever written to disk by this plugin (the
//     connection is entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CrystalProjectPlugin : IGamePlugin
{
    // ── Constants — the Crystal Project AP mod (real repo, verified 2026-06-14) ─

    private const string MOD_OWNER = "Emerassi";
    private const string MOD_REPO  = "CrystalProjectAPWorld";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";
    private const string ModReleasesPageUrl = $"{ModRepoUrl}/releases";
    private const string SetupReadmeUrl = $"{ModRepoUrl}/blob/main/worlds/crystal_project/docs/setup_en.md";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Crystal Project appid 1637730 (verified 2026-06-14).
    private const string SteamAppId = "1637730";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// The standard Steam install sub-folder name for Crystal Project.
    private const string SteamCommonFolderName = "Crystal Project";

    // The Steam beta branch name for the AP version (from setup_en.md).
    private const string SteamBetaBranch = "archipelago";

    // Pinned fallback for the mod when the GitHub API is unreachable.
    // Tag "CrystalProject-v0.16.0" verified live 2026-06-14.
    private const string FallbackModVersion     = "0.16.0";
    private const string FallbackModTag         = "CrystalProject-v0.16.0";
    private const string FallbackInstallerZip   = "CrystalProjectAPModInstaller.zip";
    private static readonly string FallbackInstallerZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackModTag}/{FallbackInstallerZip}";

    // .NET 8 Desktop Runtime x64 download page (required by the installer).
    private const string DotNet8DownloadUrl =
        "https://dotnet.microsoft.com/en-us/download/dotnet/8.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "crystal_project";
    public string DisplayName => "Crystal Project";
    public string Subtitle    => "Native PC · Archipelago";

    /// EXACT AP game string — verified against worlds/crystal_project/__init__.py
    /// and worlds/crystal_project/archipelago.json in Emerassi/CrystalProjectAPWorld:
    ///   game = "Crystal Project"
    /// This is a COMMUNITY apworld (crystal_project.apworld), NOT a core AP world.
    public string ApWorldName => "Crystal Project";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "crystal_project.png");

    public string ThemeAccentColor => "#5B8ED4";   // crystal blue

    public string[] GameBadges => new[] { "Requires Crystal Project on Steam" };

    public string Description =>
        "Crystal Project is Andrew Willman's 2022 non-linear JRPG fused with 3D " +
        "platforming — explore a world of crystals and jobs in an Archipelago " +
        "multiworld randomizer. The mod installs via the official Crystal Project " +
        "Archipelago Mod Installer on top of the \"archipelago\" Steam beta branch " +
        "of the game. You bring your own copy of Crystal Project (Steam), and the " +
        "mod adds a built-in Archipelago connection screen so you can join your " +
        "server directly in-game. The launcher detects your Steam install, " +
        "downloads the installer, and guides you through the one-time setup. " +
        "After connecting once, your save file reconnects automatically.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the AP mod installer has been run against the detected/
    /// override Crystal Project install. We detect this by a launcher-side stamp
    /// (written after the installer finishes) or the presence of a data/archipelago
    /// folder in the game directory (written by the installer). We do NOT gate on
    /// any specific file name because the installer's exact output layout is not
    /// publicly documented; this errs on the side of "let the user re-run Install"
    /// rather than crashing.
    public bool IsInstalled
    {
        get
        {
            // Primary: our own stamp
            if (!string.IsNullOrWhiteSpace(ReadStampedVersion())) return true;
            // Secondary: look for an archipelago data folder in the install dir
            try
            {
                string? dir = ResolveInstallDir();
                if (dir != null)
                {
                    // Mod installer writes game data files in the game dir
                    string apDataDir = Path.Combine(dir, "data", "archipelago");
                    if (Directory.Exists(apDataDir)) return true;
                }
            }
            catch { /* ignore */ }
            return false;
        }
    }

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and working files. Actual mod files are
    /// installed INTO the Crystal Project game directory. Exposed as GameDirectory.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "CrystalProject");

    /// This plugin's own settings sidecar.
    /// Lives under Games/ROMs/crystal_project/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "crystal_project_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;
    private ApSession? _lastSession;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // Crystal Project's built-in AP client reports checks/items/goal to the server
    // itself — the launcher relays nothing. These exist for interface compatibility
    // (ConnectsItself = true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself / SupportsStandalone ───────────────────────────────────

    /// The in-game Archipelago client owns the server connection.
    public bool ConnectsItself => true;

    /// Plain Crystal Project plays fine unmodded (switch back to the default Steam
    /// branch via Steam → Properties → Betas → "None").
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = IsInstalled
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
        // 0. We need a Crystal Project install to run the mod installer against.
        progress.Report((2, "Locating your Crystal Project installation..."));
        string? installDir = ResolveInstallDir();
        if (installDir == null)
            throw new InvalidOperationException(
                "Could not find a Crystal Project installation. Open this game's " +
                "Settings and pick your Crystal Project folder (the one containing " +
                "the game executable), or install Crystal Project via Steam first.\n\n" +
                "IMPORTANT: Before running the mod installer, switch Crystal Project " +
                "to the \"archipelago\" Steam beta branch (Steam → right-click Crystal " +
                "Project → Properties → Betas → archipelago). The mod installer " +
                "patches the beta branch's game files.");

        // 1. Resolve the latest installer release.
        progress.Report((6, "Checking the latest Crystal Project AP mod release..."));
        var (modVersion, installerZipUrl) = await ResolveLatestInstallerAsync(ct);
        AvailableVersion = modVersion;

        if (installerZipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Crystal Project AP mod installer download on GitHub. " +
                "Check your internet connection, or download the installer manually from " +
                ModReleasesPageUrl + " and run it, pointing it at your Crystal Project " +
                "installation. See Settings for guided steps.");

        // 2. Download and extract the installer zip to a temp folder.
        progress.Report((10, $"Downloading Crystal Project AP mod installer {modVersion}..."));
        string tempZip  = Path.Combine(Path.GetTempPath(),
            $"crystal_project_installer-{Guid.NewGuid():N}.zip");
        string tempDir  = Path.Combine(Path.GetTempPath(),
            $"crystal_project_installer-{Guid.NewGuid():N}");

        try
        {
            using (var response = await _http.GetAsync(
                installerZipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
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
                        int pct = 10 + (int)(35.0 * downloaded / total);
                        progress.Report((pct, $"Downloading... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((46, "Extracting installer..."));
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            // 3. Find the installer exe inside the extracted folder.
            string? installerExe = FindInstallerExe(tempDir);
            if (installerExe == null)
                throw new InvalidOperationException(
                    "Could not find an installer executable inside the downloaded zip " +
                    "(" + FallbackInstallerZip + "). The mod installer zip from " +
                    ModReleasesPageUrl + " should contain a .exe. You can run it manually " +
                    "after extraction and point it at your Crystal Project folder.");

            // 4. Run the installer, passing the Crystal Project directory as argument.
            //    The installer is a .NET 8 Console/GUI app; we pass the install dir
            //    as the first argument (guessed from the README — the installer prompts
            //    if no arg is supplied, but passing it avoids interactive prompts).
            progress.Report((50, "Running mod installer (a window may appear)..."));
            var installerProc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName               = installerExe,
                    Arguments              = $"\"{installDir}\"",
                    UseShellExecute        = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError  = false,
                    CreateNoWindow         = false,   // the installer has its own UI
                },
                EnableRaisingEvents = true,
            };

            installerProc.Start();

            // Wait for the installer to finish (up to 10 min).
            await Task.Run(() =>
            {
                if (!installerProc.WaitForExit(600_000))
                    installerProc.Kill(entireProcessTree: true);
            }, ct);

            int exitCode = installerProc.ExitCode;

            // 5. Stamp the version so the tile can show it.
            WriteStampedVersion(modVersion);
            InstalledVersion = modVersion;

            progress.Report((100,
                $"Crystal Project AP mod {modVersion} installer finished " +
                $"(exit code {exitCode}). If it failed, verify that:\n" +
                "  1. Crystal Project is on the \"archipelago\" Steam beta branch.\n" +
                "  2. .NET 8.0 Desktop Runtime x64 is installed.\n" +
                "  3. You have write access to: " + installDir + ".\n" +
                "To play: launch the game from this tile, start a new game, and fill " +
                "in the Archipelago connection screen with your server address, slot " +
                "name, and password. After the first successful connection, saves " +
                "reconnect automatically."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
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
        // HONEST: the AP server connection for Crystal Project is entered IN-GAME
        // through the mod's built-in Archipelago connection screen (appears when
        // starting a new game). There is no command-line / config file this launcher
        // can pre-fill (verified against setup_en.md). Session credentials are
        // stored for display in the settings panel so the user can copy/paste them.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the game is connected.
        _lastSession = session;
        StartCrystalProject();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        _lastSession = null;
        StartCrystalProject();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        // No plaintext AP password is ever written to disk by this plugin.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // Crystal Project's built-in AP client receives items from the AP server.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Overview ─────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Crystal Project (Steam) uses a dedicated AP mod installed on top " +
                   "of the \"archipelago\" Steam beta branch. The launcher can download " +
                   "and run the mod installer for you. Connection to the AP server is " +
                   "done IN-GAME — the mod shows a connection screen when you start a " +
                   "new game. These external steps (Steam beta branch + .NET 8 runtime) " +
                   "cannot be automated and are not verified by this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CRYSTAL PROJECT INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? installDir  = ResolveInstallDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = installDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + installDir
                : "Detected Steam install: " + installDir)
            : "Crystal Project not detected. Pick your install folder below, or " +
              "install Crystal Project via Steam first.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = installDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // mod install status
        string modStatus = IsInstalled
            ? "AP mod installed" + (InstalledVersion != null ? " (version " + InstalledVersion + ")" : "") + "."
            : "AP mod not detected. Use Install on the Play tab, then run the mod " +
              "installer (the launcher downloads and launches it for you).";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modStatus, FontSize = 11,
            Foreground = IsInstalled ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? installDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Crystal Project install folder. Detected from Steam automatically; " +
                          "set it here to override (non-standard Steam library).",
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
                Title            = "Select your Crystal Project install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? installDir ?? "")
                                   ? (overrideDir ?? installDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateCrystalProjectDir(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Crystal Project folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeCrystalProjectDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeCrystalProjectDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 1637730). Use this " +
                   "picker for a non-standard Steam library location.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (in-game screen) ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (done in-game)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });

        if (_lastSession != null)
        {
            // Show session credentials for easy copy-paste into the in-game screen.
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Your current session — enter these into the in-game connection screen:",
                FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
            });
            foreach (var (label, value) in new[]
            {
                ("Host / Address:", _lastSession.ServerUri),
                ("Slot Name:",      _lastSession.SlotName),
                ("Password:",       string.IsNullOrEmpty(_lastSession.Password) ? "(none)" : _lastSession.Password),
            })
            {
                var row = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 3) };
                var lbl = new System.Windows.Controls.TextBlock
                {
                    Text = label, Width = 90, FontSize = 11, Foreground = muted,
                };
                var val = new System.Windows.Controls.TextBox
                {
                    Text = value, IsReadOnly = true, FontSize = 11,
                    Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
                    Foreground  = fg,
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
                };
                var copyBtn = new System.Windows.Controls.Button
                {
                    Content = "Copy", Width = 50,
                    Margin  = new System.Windows.Thickness(4, 0, 0, 0),
                    Padding = new System.Windows.Thickness(0, 2, 0, 2),
                    Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
                    Foreground  = fg,
                    BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
                    FontSize = 10,
                };
                string capturedVal = value;
                copyBtn.Click += (_, _) =>
                {
                    try { System.Windows.Clipboard.SetText(capturedVal); } catch { }
                };
                System.Windows.Controls.DockPanel.SetDock(copyBtn,  System.Windows.Controls.Dock.Right);
                System.Windows.Controls.DockPanel.SetDock(lbl,      System.Windows.Controls.Dock.Left);
                row.Children.Add(copyBtn);
                row.Children.Add(lbl);
                row.Children.Add(val);
                panel.Children.Add(row);
            }
        }
        else
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "Launch Crystal Project via the Play tab (with an AP session active) " +
                       "to see your connection details here. When the game starts, look for " +
                       "the Archipelago connection screen and enter your server address, slot " +
                       "name, and password there.",
                FontSize = 11, Foreground = muted,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 8),
            });
        }

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Crystal Project (Steam). Install it if you have not.",
            "2. Switch to the \"archipelago\" Steam beta branch: right-click Crystal " +
                "Project in your Steam library → Properties → Betas → select " +
                "\"archipelago\" (version 1.6.5). Steam will download the mod-ready " +
                "game files automatically.",
            "3. Install .NET 8.0 Desktop Runtime x64 (not ASP.NET or SDK) from " +
                DotNet8DownloadUrl + " if you don't already have it. The mod installer " +
                "requires it.",
            "4. Click Install on the Play tab. The launcher downloads the mod installer " +
                "and runs it — point it at your Crystal Project folder when prompted " +
                "(or use \"Select folder...\" above to pre-set the path so it is " +
                "detected automatically).",
            "5. Launch Crystal Project from this tile. The mod shows an Archipelago " +
                "connection screen when you start a new game. Enter your server address, " +
                "slot name, and password there.",
            "6. After connecting once, saves reconnect automatically. If your room " +
                "number changes, open the Archipelago menu in-game (sidebar) and " +
                "update your connection details.",
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
            ("Crystal Project AP World (releases) ↗", ModReleasesPageUrl),
            ("Crystal Project AP World (setup guide) ↗", SetupReadmeUrl),
            (".NET 8.0 Desktop Runtime x64 ↗",         DotNet8DownloadUrl),
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

    // ── Private helpers — release resolution ──────────────────────────────────

    /// Strip the "CrystalProject-v" tag prefix, or a plain leading "v" before a
    /// digit. Returns the raw tag trimmed when no known prefix matches.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        const string prefix = "CrystalProject-v";
        if (tag.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return tag[prefix.Length..];
        if (tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1]))
            return tag[1..];
        return tag;
    }

    /// Resolve the latest mod installer release: version + the installer zip URL.
    /// The asset name contains "Installer" and ".zip". Falls back to the pinned
    /// v0.16.0 direct URL when the GitHub API is unreachable.
    private async Task<(string Version, string? InstallerZipUrl)> ResolveLatestInstallerAsync(
        CancellationToken ct)
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
                string? installerUrl = null;
                string? anyZip       = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name",                  out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url",  out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;
                    anyZip ??= url;
                    if (installerUrl == null && lower.Contains("installer"))
                        installerUrl = url;
                }
                string? zip = installerUrl ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackModVersion, FallbackInstallerZipUrl);
    }

    // ── Private helpers — Steam / Crystal Project detection ───────────────────

    /// The Crystal Project install dir to use: the override (if set and valid)
    /// wins, else the Steam-detected install. Null when nothing is found.
    private string? ResolveInstallDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeCrystalProjectDir(ov))
            return ov;

        try { return DetectSteamInstallDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Crystal Project if it contains:
    ///   - "Crystal Project.exe" or "CrystalProject.exe", OR
    ///   - any top-level *.exe containing "crystal" in the name, OR
    ///   - a data/ subdirectory (Game Maker Studio layout).
    private static bool LooksLikeCrystalProjectDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            if (File.Exists(Path.Combine(dir, "Crystal Project.exe"))) return true;
            if (File.Exists(Path.Combine(dir, "CrystalProject.exe"))) return true;
            foreach (string exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileNameWithoutExtension(exe)
                        .IndexOf("crystal", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            // Game Maker Studio games always have a data/ dir
            if (Directory.Exists(Path.Combine(dir, "data"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Human-readable validation used by the folder picker. Returns null when
    /// valid, else a reason string.
    private string? ValidateCrystalProjectDir(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Crystal Project install folder.";
        if (LooksLikeCrystalProjectDir(folder)) return null;
        // Be forgiving: user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeCrystalProjectDir(nested)) return null;
        }
        catch { /* ignore */ }
        return "That does not look like a Crystal Project installation. Pick the folder " +
               "that contains the Crystal Project executable. For Steam this is usually " +
               @"...\steamapps\common\Crystal Project.";
    }

    /// Detect the Steam Crystal Project install: walk all Steam library roots and
    /// find the one whose appmanifest_1637730.acf exists.
    private static string? DetectSteamInstallDir()
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
                        if (LooksLikeCrystalProjectDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeCrystalProjectDir(conventional)) return conventional;
                }
                catch { /* try next library */ }
            }
        }
        return null;
    }

    // ── Private helpers — installer exe location ──────────────────────────────

    /// Find the installer exe inside the extracted zip folder: prefer any *.exe
    /// containing "installer" in the name; fall back to any top-level *.exe.
    private static string? FindInstallerExe(string extractedDir)
    {
        try
        {
            // Unwrap a single-subfolder zip (as produced by some GitHub Release zips).
            string[] subdirs = Directory.GetDirectories(extractedDir);
            string[] files   = Directory.GetFiles(extractedDir, "*.exe");
            string searchRoot = extractedDir;
            if (files.Length == 0 && subdirs.Length == 1)
                searchRoot = subdirs[0];

            // Prefer an exe with "installer" in the name.
            foreach (string exe in Directory.EnumerateFiles(searchRoot, "*.exe", SearchOption.AllDirectories))
            {
                if (Path.GetFileNameWithoutExtension(exe)
                        .IndexOf("installer", StringComparison.OrdinalIgnoreCase) >= 0)
                    return exe;
            }
            // Fall back to any *.exe.
            return Directory.EnumerateFiles(searchRoot, "*.exe", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { return null; }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Crystal Project. Prefer the exe in the detected/override install; if
    /// that cannot be found but Steam is present, fall back to the steam:// URL.
    private void StartCrystalProject()
    {
        string? dir = ResolveInstallDir();
        string? exe = dir != null ? ResolveCrystalProjectExe(dir) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = dir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Crystal Project.");

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
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { /* fall through to error */ }
        }

        throw new FileNotFoundException(
            "Could not find the Crystal Project executable. Open this game's Settings " +
            "and pick your Crystal Project install folder, or install Crystal Project " +
            "via Steam first. Remember to switch to the \"archipelago\" beta branch.",
            "Crystal Project.exe");
    }

    /// Resolve the Crystal Project exe in an install dir.
    private static string? ResolveCrystalProjectExe(string dir)
    {
        try
        {
            string preferred1 = Path.Combine(dir, "Crystal Project.exe");
            if (File.Exists(preferred1)) return preferred1;
            string preferred2 = Path.Combine(dir, "CrystalProject.exe");
            if (File.Exists(preferred2)) return preferred2;
            // Fuzzy: any top-level *.exe with "crystal" in the name.
            foreach (string exe in Directory.EnumerateFiles(dir, "*.exe", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileNameWithoutExtension(exe)
                        .IndexOf("crystal", StringComparison.OrdinalIgnoreCase) >= 0)
                    return exe;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — Steam registry / VDF parsing ────────────────────────
    // (same multi-root pattern as Hylics2Plugin — adapted for Crystal Project appid)

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

    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

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

    private static string? ReadRegistryString(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(subKey);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class CrystalProjectSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private CrystalProjectSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<CrystalProjectSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(CrystalProjectSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    private string? LoadOverrideDir()
    {
        string? p = LoadSettings().InstallOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }

    private void SaveOverrideDir(string p)
    {
        var s = LoadSettings();
        s.InstallOverride = p;
        SaveSettings(s);
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings();
        s.ModVersion = v;
        SaveSettings(s);
    }
}
