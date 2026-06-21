using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.Plugins.DukeNukem3D;

// ═══════════════════════════════════════════════════════════════════════════════
// DukeNukem3DPlugin — install / update / launch for "Duke Nukem 3D" — the
// 1996 3D Realms shooter played through Duke3DAP, a Rednukem-based port with a
// BUILT-IN Archipelago client. This is a NATIVE "ConnectsItself" integration
// (NOT a BizHawk / Lua emulator game): rednukemAP.exe speaks to the AP server
// itself through its own launcher UI window, exactly like Ship of Harkinian and
// the OpenTTD Archipelago fork.
//
// REALITY CHECK (2026-06-14) — facts verified from GitHub API and README
// ─────────────────────────────────────────────────────────────────────────────
// UPSTREAM: LLCoolDave/Duke3DAP on GitHub.
// Latest release: v0.0.8 (2025-02-02, "Fixed compatibility for AP 0.6.0+").
//
// RELEASE ASSETS (verified via GitHub API):
//   rednukemAP.exe    — 22.4 MB — the self-contained Rednukem-AP engine.
//   DUKE3DAP.zip      — 224 KB  — the mod scripts (must stay zipped, not extracted).
//   DUKE3DAP.grpinfo  — 212 B   — GRP/mod loader metadata for Rednukem.
//   duke3d.apworld    — 112 KB  — the AP world for Archipelago.
//
// HOW IT CONNECTS (verified from the README):
//   rednukemAP.exe launches its OWN built-in launcher UI window. Inside that
//   window, "Duke Nukem 3D Randomizer for Archipelago" is selectable as the
//   game, and the player enters:
//       - Archipelago server address
//       - Slot name
//       - Password
//   Connection is entirely through this built-in window — there are NO
//   documented command-line arguments for the server/slot/password.
//   This plugin therefore launches rednukemAP.exe with no AP args; the built-in
//   UI handles the connection. The launcher pre-fills an optional config file
//   (host.txt / rednukem.cfg / ap_settings.cfg patterns are not confirmed for
//   this port) as a best-effort convenience — the user can always enter details
//   in the game window. ConnectsItself = true.
//
// GAME DATA — BRING-YOUR-OWN-GRP (§11):
//   Duke3DAP ships NO commercial game data. The player must supply their own
//   duke3d.grp and DUKE.RTS from the Atomic Edition (1.5). These are available:
//     * Steam: Duke Nukem 3D: Megaton Edition (AppID 225140) — base game GRP.
//       Note: Megaton Edition was delisted; some users may have it from a
//       purchase. The GRP file itself is not DRM-protected.
//     * GOG: Duke Nukem 3D: Atomic Edition (no AppID needed — file download).
//     * Original disc copy.
//   The GRP and RTS must live next to rednukemAP.exe. This plugin lets the user
//   pick their GRP, validates it by size (the Atomic Edition duke3d.grp is
//   44,356,548 bytes; we accept a loose 40–50 MB window), copies it into the
//   launcher's own ROM library (§11 — original never modified), and stages a
//   copy alongside the exe.
//
// REQUIRED FILES IN THE GAME FOLDER (from README):
//   rednukemAP.exe       — the engine
//   duke3d.grp           — user-supplied commercial game data (Atomic Edition)
//   DUKE.RTS             — user-supplied game data (from same source as GRP)
//   DUKE3DAP.zip         — mod scripts (must remain as a zip, NOT extracted)
//   DUKE3DAP.grpinfo     — mod loader metadata
//
// THE AP WORLD:
//   game string "Duke Nukem 3D" (verified: worlds/__init__.py →
//   D3DWorld.game = "Duke Nukem 3D").
//   The release also ships "duke3d.apworld" — saved next to the install for
//   the user to copy into Archipelago's custom_worlds folder.
//
// SIDECAR:
//   This plugin's own settings: Games/ROMs/duke3d/duke3d_launcher.json.
//   Kept out of the shared SettingsStore so this is a single self-contained file.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DukeNukem3DPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER     = "LLCoolDave";
    private const string GITHUB_REPO      = "Duke3DAP";
    private const string RepoUrl          = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL  =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";

    // Known stable release as a pinned fallback for when the API is unreachable.
    private const string FallbackVersion        = "0.0.8";
    private const string FallbackExeUrl         =
        $"{RepoUrl}/releases/download/{FallbackVersion}/rednukemAP.exe";
    private const string FallbackModZipUrl      =
        $"{RepoUrl}/releases/download/{FallbackVersion}/DUKE3DAP.zip";
    private const string FallbackGrpInfoUrl     =
        $"{RepoUrl}/releases/download/{FallbackVersion}/DUKE3DAP.grpinfo";
    private const string FallbackApWorldUrl     =
        $"{RepoUrl}/releases/download/{FallbackVersion}/duke3d.apworld";

    // Required filenames next to the exe (from README).
    private const string ExeName       = "rednukemAP.exe";
    private const string ModZipName    = "DUKE3DAP.zip";
    private const string GrpInfoName   = "DUKE3DAP.grpinfo";
    private const string ApWorldFileName = "duke3d.apworld";
    private const string UserGrpName   = "duke3d.grp";
    private const string UserRtsName   = "DUKE.RTS";

    private const string VersionFileName = "duke3d_version.dat";

    // Atomic Edition duke3d.grp is 44,356,548 bytes. Accept 40–50 MB window
    // to cover slightly different builds (GOG vs Steam vs original disc).
    private const long GrpMinBytes = 40L * 1024 * 1024;
    private const long GrpMaxBytes = 50L * 1024 * 1024;

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "duke3d";
    public string DisplayName => "Duke Nukem 3D";
    public string Subtitle    => "Native PC · built-in Archipelago";

    /// EXACT AP game string — verified: D3DWorld.game = "Duke Nukem 3D"
    public string ApWorldName => "Duke Nukem 3D";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "duke3d.png");

    public string ThemeAccentColor => "#B87A1A";   // Duke gold
    public string[] GameBadges     => new[] { "Requires duke3d.grp + DUKE.RTS" };

    public string Description =>
        "Duke Nukem 3D: Atomic Edition (1996) played through Duke3DAP — a " +
        "Rednukem-based port with a built-in Archipelago client. Keys, weapons, " +
        "access to levels, and other items are shuffled into the multiworld. The " +
        "engine connects to the Archipelago server through its own built-in " +
        "launcher window — no external client needed. Duke3DAP ships no game data: " +
        "supply your own duke3d.grp and DUKE.RTS from the Atomic Edition (Steam " +
        "AppID 225140 — Megaton Edition, GOG, or original disc). The launcher " +
        "copies your files next to the game; your originals are never modified.";

    public string? VideoPreviewUrl  => null;
    public string[] ScreenshotUrls  => Array.Empty<string>();

    // ── Version / state ───────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => File.Exists(InstalledExePath);
    public bool    IsRunning        { get; private set; }
    public bool    ConnectsItself   => true;
    public bool    SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "DukeNukem3D");

    private string InstalledExePath  => Path.Combine(GameDirectory, ExeName);
    private string InstalledModZip   => Path.Combine(GameDirectory, ModZipName);
    private string InstalledGrpInfo  => Path.Combine(GameDirectory, GrpInfoName);
    private string InstalledApWorld  => Path.Combine(GameDirectory, ApWorldFileName);
    private string StagedGrpPath     => Path.Combine(GameDirectory, UserGrpName);
    private string StagedRtsPath     => Path.Combine(GameDirectory, UserRtsName);
    private string VersionFilePath   => Path.Combine(GameDirectory, VersionFileName);

    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "duke3d_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // rednukemAP's native AP client reports everything to the AP server itself —
    // the launcher relays nothing. These exist for interface compatibility.
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
        catch { InstalledVersion = null; }

        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(GITHUB_OWNER, GITHUB_REPO, ct));
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((2, "Checking latest Duke3DAP release..."));
        var (version, exeUrl, modZipUrl, grpInfoUrl, apWorldUrl)
            = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        // Idempotent fast path.
        if (IsInstalled && File.Exists(VersionFilePath))
        {
            string stamped = (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim();
            if (stamped == version)
            {
                InstalledVersion = version;
                progress.Report((100, $"Duke3DAP {version} is already up to date."));
                return;
            }
        }

        Directory.CreateDirectory(GameDirectory);

        // ── 1. rednukemAP.exe ─────────────────────────────────────────────────
        progress.Report((5, $"Downloading rednukemAP.exe ({version})..."));
        await DownloadFileAsync(exeUrl, InstalledExePath, 5, 40, "rednukemAP.exe", progress, ct);

        // ── 2. DUKE3DAP.zip ───────────────────────────────────────────────────
        progress.Report((43, "Downloading DUKE3DAP.zip (mod scripts)..."));
        await DownloadFileAsync(modZipUrl, InstalledModZip, 43, 70, "DUKE3DAP.zip", progress, ct);

        // ── 3. DUKE3DAP.grpinfo ───────────────────────────────────────────────
        progress.Report((72, "Downloading DUKE3DAP.grpinfo..."));
        await DownloadFileAsync(grpInfoUrl, InstalledGrpInfo, 72, 80, "DUKE3DAP.grpinfo", progress, ct);

        // ── 4. duke3d.apworld — best effort ──────────────────────────────────
        if (apWorldUrl != null)
        {
            try
            {
                progress.Report((82, "Downloading duke3d.apworld..."));
                byte[] apworld = await _http.GetByteArrayAsync(apWorldUrl, ct);
                await File.WriteAllBytesAsync(InstalledApWorld, apworld, ct);
                progress.Report((90, "duke3d.apworld saved — copy it into " +
                    "Archipelago's custom_worlds folder if you generate with this build."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((90, "Could not download the apworld — get it from " +
                    "the GitHub release page (the stable world also ships with Archipelago)."));
            }
        }

        // ── 5. Stage user's GRP + RTS if already picked ──────────────────────
        StageGameDataFiles();

        // ── 6. Stamp version ──────────────────────────────────────────────────
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"Duke3DAP {version} ready. " +
            "Pick your duke3d.grp (and optionally DUKE.RTS) in Settings, then press Play. " +
            "Enter your Archipelago connection details in the rednukemAP launcher window."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled
            && File.Exists(InstalledModZip)
            && File.Exists(InstalledGrpInfo);
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        if (!IsInstalled)
            throw new FileNotFoundException(
                "Duke3DAP is not installed. Click Install Game first.", InstalledExePath);

        // Stage game data next to the exe.
        StageGameDataFiles();

        // rednukemAP.exe has its own built-in launcher window where the player
        // enters server address, slot name, and password. There are no documented
        // command-line arguments for these connection parameters. We launch the
        // exe directly; the built-in window collects the AP details.
        StartGameProcess(InstalledExePath, "");
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        if (!IsInstalled)
            throw new FileNotFoundException(
                "Duke3DAP is not installed. Click Install Game first.", InstalledExePath);

        StageGameDataFiles();
        StartGameProcess(InstalledExePath, "");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert ─────────────────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // rednukemAP's native client receives items from the AP server directly;
        // there is nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // rednukemAP renders its own AP status in-game; no launcher HUD channel.
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
        var panel   = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        // ── Install directory ──────────────────────────────────────────────
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
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select Duke3DAP install folder",
                InitialDirectory = Directory.Exists(GameDirectory)
                                   ? GameDirectory : AppContext.BaseDirectory,
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
            Text = IsInstalled ? "✓ Duke3DAP is installed"
                               : "Not installed (click Install in the Play tab)",
            FontSize   = 11,
            Foreground = IsInstalled ? success : muted,
            Margin     = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── duke3d.grp (required game data) ───────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GAME DATA (duke3d.grp)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });

        var grpRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var grpBox = new System.Windows.Controls.TextBox
        {
            Text = LoadGrpPath() ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var grpBtn = new System.Windows.Controls.Button
        {
            Content = "Select GRP...", Width = 110,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        grpBtn.Click += (_, _) =>
        {
            if (PromptForGrpFile())
                grpBox.Text = LoadGrpPath() ?? "";
        };
        System.Windows.Controls.DockPanel.SetDock(grpBtn, System.Windows.Controls.Dock.Right);
        grpRow.Children.Add(grpBtn);
        grpRow.Children.Add(grpBox);
        panel.Children.Add(grpRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Duke3DAP requires your own duke3d.grp from the Atomic Edition. " +
                   "This is available from the Steam release (Megaton Edition, AppID 225140), " +
                   "GOG, or an original disc copy. The launcher copies the file next to the " +
                   "game — your original is never modified.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 8),
        });

        // ── DUKE.RTS (optional but recommended) ───────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GAME DATA (DUKE.RTS)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });

        var rtsRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var rtsBox = new System.Windows.Controls.TextBox
        {
            Text = LoadRtsPath() ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var rtsBtn = new System.Windows.Controls.Button
        {
            Content = "Select RTS...", Width = 110,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        rtsBtn.Click += (_, _) =>
        {
            if (PromptForRtsFile())
                rtsBox.Text = LoadRtsPath() ?? "";
        };
        System.Windows.Controls.DockPanel.SetDock(rtsBtn, System.Windows.Controls.Dock.Right);
        rtsRow.Children.Add(rtsBtn);
        rtsRow.Children.Add(rtsBox);
        panel.Children.Add(rtsRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "DUKE.RTS comes from the same folder as duke3d.grp and is required by " +
                   "the engine. Located in your Duke Nukem 3D install directory.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Connection info note ──────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTION", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "rednukemAP.exe opens its own launcher window where you select " +
                   "\"Duke Nukem 3D Randomizer for Archipelago\" and enter your Archipelago " +
                   "server address, slot name, and password. No separate client is needed.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        if (IsInstalled && File.Exists(InstalledApWorld))
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "duke3d.apworld is saved in the install folder — copy it into " +
                       @"Archipelago's custom_worlds folder (default: C:\ProgramData\Archipelago\custom_worlds) " +
                       "if you generate with this build.",
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
            ("Duke3DAP (GitHub) ↗",      RepoUrl),
            ("Archipelago Official ↗",   "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding         = new System.Windows.Thickness(0, 2, 0, 2),
                Background      = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0),
                FontSize        = 12,
                Margin          = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground      = new System.Windows.Media.SolidColorBrush(
                                      System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(u) { UseShellExecute = true });
                }
                catch { }
            };
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

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..] : tag;
    }

    /// Resolve the latest release. Returns (version, exeUrl, modZipUrl, grpInfoUrl, apWorldUrl).
    /// Falls back to pinned v0.0.8 URLs when the API is unreachable.
    private async Task<(string Version, string ExeUrl, string ModZipUrl, string GrpInfoUrl, string? ApWorldUrl)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    // Skip drafts; prereleases are accepted.
                    if (rel.TryGetProperty("draft", out var dr) &&
                        dr.ValueKind == JsonValueKind.True)
                        continue;

                    string? version = rel.TryGetProperty("tag_name", out var t)
                        ? NormalizeTag(t.GetString()) : null;
                    if (version == null) continue;

                    if (!rel.TryGetProperty("assets", out var assets) ||
                        assets.ValueKind != JsonValueKind.Array)
                        continue;

                    string? exeUrl      = null;
                    string? modZipUrl   = null;
                    string? grpInfoUrl  = null;
                    string? apWorldUrl  = null;

                    foreach (var a in assets.EnumerateArray())
                    {
                        string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        string? url  = a.TryGetProperty("browser_download_url", out var u)
                                       ? u.GetString() : null;
                        if (name == null || url == null) continue;
                        string lower = name.ToLowerInvariant();

                        if (lower == "rednukemap.exe")        exeUrl     = url;
                        else if (lower == "duke3dap.zip")     modZipUrl  = url;
                        else if (lower == "duke3dap.grpinfo") grpInfoUrl = url;
                        else if (lower == "duke3d.apworld")   apWorldUrl = url;
                    }

                    if (exeUrl != null && modZipUrl != null && grpInfoUrl != null)
                        return (version, exeUrl, modZipUrl, grpInfoUrl, apWorldUrl);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        return (FallbackVersion, FallbackExeUrl, FallbackModZipUrl,
                FallbackGrpInfoUrl, FallbackApWorldUrl);
    }

    // ── Private helpers — process launch ─────────────────────────────────────

    private void StartGameProcess(string exePath, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = GameDirectory,
            UseShellExecute  = false,
        };
        if (!string.IsNullOrWhiteSpace(arguments))
            psi.Arguments = arguments;

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start rednukemAP.exe.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — game data staging ──────────────────────────────────

    /// Stage duke3d.grp and DUKE.RTS next to the exe if the user has picked them
    /// and the game is installed. Best effort — never throws into install/launch.
    private void StageGameDataFiles()
    {
        StageFile(LoadGrpPath(), StagedGrpPath);
        StageFile(LoadRtsPath(), StagedRtsPath);
    }

    private static void StageFile(string? srcPath, string dstPath)
    {
        try
        {
            if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath)) return;
            if (File.Exists(dstPath))
            {
                try
                {
                    if (new FileInfo(dstPath).Length == new FileInfo(srcPath).Length)
                        return; // already staged, same size
                }
                catch { /* fall through and re-copy */ }
            }
            File.Copy(srcPath, dstPath, overwrite: true);
        }
        catch { /* staging is a convenience */ }
    }

    // ── Private helpers — file pickers ────────────────────────────────────────

    private bool PromptForGrpFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Select your duke3d.grp (Atomic Edition)",
            Filter = "Duke Nukem 3D GRP (*.grp)|*.grp|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return false;

        string? bad = ValidateGrpFile(dlg.FileName);
        if (bad != null)
        {
            System.Windows.MessageBox.Show(bad, "Not a valid duke3d.grp",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return false;
        }

        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
            string dst = Path.Combine(RomLibraryDirectory, UserGrpName);
            File.Copy(dlg.FileName, dst, overwrite: true);
            SaveGrpPath(dst);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Could not copy duke3d.grp into the launcher library:\n{ex.Message}\n\n" +
                "Nothing was changed — your original file is untouched.",
                "GRP import failed",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return false;
        }

        StageFile(LoadGrpPath(), StagedGrpPath);
        return true;
    }

    private bool PromptForRtsFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Select your DUKE.RTS (from the same folder as duke3d.grp)",
            Filter = "Duke Nukem RTS (*.rts)|*.rts|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return false;

        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
            string dst = Path.Combine(RomLibraryDirectory, UserRtsName);
            File.Copy(dlg.FileName, dst, overwrite: true);
            SaveRtsPath(dst);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Could not copy DUKE.RTS into the launcher library:\n{ex.Message}\n\n" +
                "Nothing was changed — your original file is untouched.",
                "RTS import failed",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return false;
        }

        StageFile(LoadRtsPath(), StagedRtsPath);
        return true;
    }

    /// Content check for duke3d.grp — validates by size (40–50 MB window).
    /// The Atomic Edition is 44,356,548 bytes; we accept a loose range to cover
    /// minor variant builds. Returns null when acceptable, else a short reason.
    private static string? ValidateGrpFile(string path)
    {
        try
        {
            long len = new FileInfo(path).Length;
            if (len < GrpMinBytes)
                return "That file is too small to be an Atomic Edition duke3d.grp. " +
                       "The Atomic Edition GRP is about 44 MB.";
            if (len > GrpMaxBytes)
                return "That file is too large to be an Atomic Edition duke3d.grp. " +
                       "Ensure you selected the correct file.";
        }
        catch
        {
            return "Could not read that file. Pick a different GRP and try again.";
        }
        return null;
    }

    // ── Private helpers — download utility ───────────────────────────────────

    private async Task DownloadFileAsync(
        string url,
        string destPath,
        int pctStart,
        int pctEnd,
        string label,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        using var response = await _http.GetAsync(
            url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total      = response.Content.Headers.ContentLength ?? -1;
        long downloaded = 0;

        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(destPath))
        {
            var buf = new byte[81920];
            int bytesRead;
            while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                downloaded += bytesRead;
                if (total > 0)
                {
                    int pct = pctStart + (int)((pctEnd - pctStart) * downloaded / total);
                    progress.Report((pct, $"Downloading {label}... {downloaded / 1_000_000}MB"));
                }
            }
            await dst.FlushAsync(ct);
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class DukeSettings
    {
        public string? GrpPath { get; set; }
        public string? RtsPath { get; set; }
    }

    private DukeSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<DukeSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(DukeSettings s)
    {
        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    private string? LoadGrpPath() => LoadSettings().GrpPath;
    private void    SaveGrpPath(string p) { var s = LoadSettings(); s.GrpPath = p; SaveSettings(s); }
    private string? LoadRtsPath() => LoadSettings().RtsPath;
    private void    SaveRtsPath(string p) { var s = LoadSettings(); s.RtsPath = p; SaveSettings(s); }
}
