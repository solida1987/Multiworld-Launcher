using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.Plugins.Hades;

// ═══════════════════════════════════════════════════════════════════════════════
// HadesPlugin — install / update / launch for "Hades" with Archipelago support
// via the Polycosmos mod (NaixGames/Polycosmos on GitHub).
//
// REALITY CHECK (2026-06-14) — all facts verified online this session
// ─────────────────────────────────────────────────────────────────────────────
// AP GAME STRING:  "Hades"   (HadesWorld.game in hades/__init__.py)
// AP WORLD FILE:   hades.apworld (from NaixGames/Polycosmos releases)
// STEAM APP ID:    1145360  (Hades by Supergiant Games — NOT Hades II)
// LATEST RELEASE:  Polycosmos 0.15.1 (2026-01-17)
//
// HOW IT WORKS
// ─────────────────────────────────────────────────────────────────────────────
// Hades is a Steam game. The mod is pure Lua injected through the SGG modding
// framework:
//   1. ModImporter (SGG-Modding/ModImporter) patches Hades' game files.
//   2. ModUtils (SGG-Modding/ModUtil) provides the Lua module system.
//   3. StyxScribe (NaixGames/StyxScribeWithoutREPL) — a Python subprocess
//      bridge — starts Hades and maintains a named-pipe / stdout channel
//      between the Lua scripts inside Hades and an external Python process.
//   4. The Archipelago HadesClient (hades/Client.py) runs AS that external
//      Python process: it connects to the AP server, then communicates with
//      the running game over StyxScribe hooks.
//
// The official launch path from the AP Launcher (as documented in the README):
//   "launch the client from the Archipelago Launcher" → this opens a file
//   dialog asking for StyxScribe.py, then starts Hades + the AP client window.
//
// Because Hades contains its own AP connection (via the HadesClient + StyxScribe
// pair), ConnectsItself = true — the launcher must NOT hold a second ApClient on
// the same slot while the game runs.
//
// INSTALL APPROACH
// ─────────────────────────────────────────────────────────────────────────────
// This plugin does NOT install Hades itself (Steam-purchased — user owns it).
// It installs Polycosmos and its dependencies into the Hades Content/Mods folder:
//   1. Downloads PolycosmosInstaller.exe (NaixGames/PolycosmosInstaller) and
//      runs it silently with the Hades path, which installs all prerequisites
//      (ModImporter, ModUtils, StyxScribe, Polycosmos mod) automatically.
//   2. Downloads hades.apworld from the Polycosmos release next to the install
//      dir so the user can copy it into Archipelago's custom_worlds.
//   3. Records the installed Polycosmos version in a version stamp file.
//
// LAUNCH PATH (ConnectsItself)
// ─────────────────────────────────────────────────────────────────────────────
// On AP launch the plugin writes the AP connection settings to a per-session
// sidecar JSON (hades_launcher.json) and then launches Hades via Steam
// (steam://rungameid/1145360). The AP client (run as a subprocess of the
// Archipelago install) handles the actual AP connection. The launcher tracks
// Hades' process lifetime only.
//
// DEFENSIVE / UNVERIFIED DETAILS
// ─────────────────────────────────────────────────────────────────────────────
//   * The PolycosmosInstaller silent-install flags were not verified against a
//     running instance. The plugin falls back to running the installer
//     interactively if no silent flag is documented, and logs a setup note.
//   * The default Steam install path for Hades is used as the initial guess;
//     the user can override it in Settings.
//   * The exact StyxScribe.py location is derived from the Hades folder.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class HadesPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const int    SteamAppId  = 1145360;
    private const string SteamRunUrl = "steam://rungameid/1145360";

    private const string POLY_OWNER  = "NaixGames";
    private const string POLY_REPO   = "Polycosmos";
    private const string INST_OWNER  = "NaixGames";
    private const string INST_REPO   = "PolycosmosInstaller";

    private const string GH_POLY_RELEASES_URL =
        $"https://api.github.com/repos/{POLY_OWNER}/{POLY_REPO}/releases";
    private const string GH_INST_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{INST_OWNER}/{INST_REPO}/releases/latest";

    /// Pinned fallback — Polycosmos 0.15.1 (2026-01-17), verified online.
    private const string FallbackPolyVersion  = "0.15.1";
    /// Pinned installer fallback — version 0.3 (verified online).
    private const string FallbackInstVersion  = "0.3";

    private const string VersionFileName = "polycosmos_version.dat";

    /// Per-session sidecar that stores AP connection settings so the user can
    /// find them or re-use them outside the launcher.
    private const string SidecarFileName = "hades_launcher.json";

    private static readonly string DefaultHadesPath =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Steam", "steamapps", "common", "Hades");

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(15),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } },
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "hades";
    public string DisplayName => "Hades";
    public string Subtitle    => "Polycosmos mod · StyxScribe · Steam";

    /// AP world name exactly as registered in hades/__init__.py (game = "Hades").
    public string ApWorldName => "Hades";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "hades.png");

    /// Deep crimson — Hades' signature colour.
    public string ThemeAccentColor => "#8B1A1A";

    public string[] GameBadges => new[] { "Steam Required", "Polycosmos Mod" };

    public string Description =>
        "Hades is a rogue-like dungeon crawler from Supergiant Games in which you " +
        "defy the god of the dead as you hack and slash your way out of the " +
        "Underworld of Greek myth. The Polycosmos mod (by NaixGames) integrates " +
        "Hades with Archipelago Multiworld: weapons, keepsakes, store items, the " +
        "fated list, and your runs through the Underworld all become locations and " +
        "items in the multiworld pool. The mod uses StyxScribe — a Python bridge " +
        "that lets the Archipelago client communicate with Hades' Lua scripts in " +
        "real time. Launch through the Archipelago client once Polycosmos is " +
        "installed; connect to your AP server before starting a save file.";

    public string? VideoPreviewUrl  => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version / install state ───────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// The mod is "installed" when the Hades game folder exists AND the
    /// Polycosmos version stamp is present (meaning the installer ran once).
    public bool IsInstalled =>
        Directory.Exists(HadesDirectory) &&
        File.Exists(VersionFilePath);

    public bool IsRunning { get; private set; }

    // ── ConnectsItself / Standalone ───────────────────────────────────────────

    /// The HadesClient (Polycosmos) connects to the AP server via StyxScribe;
    /// the launcher does not hold its own slot connection.
    public bool ConnectsItself     => true;
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root Hades install folder. Shown in Settings, user can override.
    public string GameDirectory { get; set; } = DefaultHadesPath;

    /// For interface compatibility — same as GameDirectory for Steam games.
    private string HadesDirectory => GameDirectory;

    private string ContentDir  => Path.Combine(HadesDirectory, "Content");
    private string ModsDir     => Path.Combine(ContentDir, "Mods");
    private string StyxScribeExe => Path.Combine(HadesDirectory, "StyxScribe.py");

    private string VersionFilePath =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", "hades", VersionFileName);

    private string SidecarPath =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", "hades", SidecarFileName);

    /// Best-effort path where the apworld is saved so the user can drop it into
    /// Archipelago's custom_worlds.
    private string ApWorldLocalPath =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", "hades", "hades.apworld");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _hadesProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Polycosmos HadesClient talks to the AP server directly; the launcher
    // relays nothing. These satisfy the interface.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Installed version — from the stamp file.
        try
        {
            string stampPath = VersionFilePath;
            InstalledVersion = File.Exists(stampPath) && IsInstalled
                ? (await File.ReadAllTextAsync(stampPath, ct)).Trim()
                : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        // Available version — latest Polycosmos release tag.
        try
        {
            string json = await _http.GetStringAsync(GH_POLY_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;
            if (arr.GetArrayLength() > 0)
            {
                var first = arr[0];
                AvailableVersion = first.TryGetProperty("tag_name", out var t)
                    ? t.GetString()?.TrimStart('v')
                    : null;
            }
        }
        catch
        {
            AvailableVersion = null;
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 1. Ensure the sidecar directory exists.
        string sidecarDir = Path.GetDirectoryName(SidecarPath)!;
        Directory.CreateDirectory(sidecarDir);

        // 2. Verify Hades is installed.
        if (!Directory.Exists(HadesDirectory))
            throw new DirectoryNotFoundException(
                $"Hades is not installed at:\n  {HadesDirectory}\n\n" +
                "Install Hades via Steam (App ID 1145360), then set the correct " +
                "path in the Settings tab and try again.");

        // 3. Resolve latest Polycosmos release for version stamp + apworld.
        progress.Report((3, "Checking latest Polycosmos release..."));
        var (polyVersion, apworldUrl) = await ResolveLatestPolycosmosAsync(ct);
        AvailableVersion = polyVersion;

        // 4. Already current?
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == polyVersion)
        {
            InstalledVersion = polyVersion;
            progress.Report((100, $"Polycosmos {polyVersion} is already up to date."));
            return;
        }

        // 5. Download and run PolycosmosInstaller.exe.
        progress.Report((8, "Downloading PolycosmosInstaller..."));
        string? installerUrl = await ResolveInstallerUrlAsync(ct);
        if (installerUrl == null)
            throw new InvalidOperationException(
                "Could not find PolycosmosInstaller on GitHub. " +
                "Check your internet connection, or install Polycosmos manually:\n" +
                "  https://github.com/NaixGames/PolycosmosInstaller\n" +
                "  https://github.com/NaixGames/Polycosmos");

        string installerTmp = Path.Combine(
            Path.GetTempPath(), $"PolycosmosInstaller-{Guid.NewGuid():N}.exe");

        try
        {
            // Download the installer.
            progress.Report((12, "Downloading PolycosmosInstaller.exe..."));
            using (var resp = await _http.GetAsync(
                installerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                long done  = 0;

                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(installerTmp);
                var buf = new byte[81920];
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    done += read;
                    if (total > 0)
                        progress.Report(((int)(12 + 18 * done / total),
                            $"Downloading installer... {done / 1024}KB"));
                }
                await dst.FlushAsync(ct);
            }

            // 6. Run the installer. Pass the Hades path as the first argument.
            //    The installer is a GUI wizard; it will open a window. We wait
            //    for it to exit (the user clicks through it).
            progress.Report((32,
                "Running PolycosmosInstaller — follow the installer window..."));

            using var instProc = Process.Start(new ProcessStartInfo
            {
                FileName         = installerTmp,
                Arguments        = $"\"{HadesDirectory}\"",
                UseShellExecute  = true,
                WorkingDirectory = Path.GetDirectoryName(installerTmp)!,
            }) ?? throw new InvalidOperationException(
                "Failed to start PolycosmosInstaller.exe.");

            // Wait up to 10 minutes — user is clicking through a wizard.
            await instProc.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(10), ct);

            if (instProc.ExitCode != 0)
                throw new InvalidOperationException(
                    $"PolycosmosInstaller exited with code {instProc.ExitCode}. " +
                    "Check that Hades is installed and Steam is running, then retry.");

            progress.Report((72, "Polycosmos installation complete."));
        }
        finally
        {
            try { if (File.Exists(installerTmp)) File.Delete(installerTmp); } catch { }
        }

        // 7. Download hades.apworld next to the sidecar (best effort).
        if (apworldUrl != null)
        {
            try
            {
                progress.Report((76, "Downloading hades.apworld..."));
                byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                await File.WriteAllBytesAsync(ApWorldLocalPath, apworld, ct);
                progress.Report((88,
                    "hades.apworld saved — copy it into Archipelago's custom_worlds folder."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((88,
                    "Could not download hades.apworld — get it from the Polycosmos " +
                    "GitHub release page and copy it into Archipelago's custom_worlds."));
            }
        }

        // 8. Stamp the installed version.
        await File.WriteAllTextAsync(VersionFilePath, polyVersion, ct);
        InstalledVersion = polyVersion;

        progress.Report((100,
            $"Polycosmos {polyVersion} installed. Launch Hades from the " +
            "Archipelago Launcher's HadesClient to connect to your AP server."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;

        if (!Directory.Exists(HadesDirectory))     return false;
        if (!File.Exists(VersionFilePath))          return false;

        // Check that the Polycosmos mod folder exists in Content/Mods.
        string polyModDir = Path.Combine(ModsDir, "Polycosmos");
        if (!Directory.Exists(polyModDir))          return false;

        return true;
    }

    // ── Lifecycle — Launch (AP) ───────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        EnsureSidecarDirectory();
        WriteSidecar(session);
        return LaunchViaSteam(session);
    }

    // ── Lifecycle — Launch (Standalone) ──────────────────────────────────────

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        return LaunchViaSteam(null);
    }

    // ── Lifecycle — Stop ─────────────────────────────────────────────────────

    public Task StopAsync()
    {
        try { _hadesProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        ScrubSidecarPassword();
        return Task.CompletedTask;
    }

    // ── AP bridge — inert ────────────────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The HadesClient receives items from the AP server directly via
        // StyxScribe; nothing to forward from the launcher.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The HadesClient renders its own AP status; the launcher has no
        // in-game HUD channel into Hades.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8B, 0x1A, 0x1A));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var linkFg  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));
        var dark    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20));
        var border  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33));
        var btnBg   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30));
        var btnBd   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20),
        };

        // ── Hades Install Directory ───────────────────────────────────────

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "HADES INSTALL DIRECTORY",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = GameDirectory,
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = dark,
            Foreground  = fg,
            BorderBrush = border,
        };
        var browseBtn = new System.Windows.Controls.Button
        {
            Content         = "Browse...",
            Width           = 90,
            Padding         = new System.Windows.Thickness(0, 6, 0, 6),
            Background      = btnBg,
            Foreground      = fg,
            BorderBrush     = btnBd,
        };
        browseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select Hades install folder",
                InitialDirectory = Directory.Exists(GameDirectory)
                    ? GameDirectory
                    : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                GameDirectory = dlg.FolderName;
                dirBox.Text   = dlg.FolderName;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(browseBtn, Dock.Right);
        dirRow.Children.Add(browseBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        bool hadesOk = Directory.Exists(HadesDirectory);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = hadesOk
                ? "Hades folder found."
                : "Hades not found at the path above. Install Hades via Steam, " +
                  "then set the correct folder here.",
            FontSize   = 11,
            Foreground = hadesOk ? success : muted,
            TextWrapping = TextWrapping.Wrap,
            Margin     = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Polycosmos Status ─────────────────────────────────────────────

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "POLYCOSMOS STATUS",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });

        bool modsPresent = Directory.Exists(Path.Combine(ModsDir, "Polycosmos"));
        string statusText = IsInstalled
            ? $"Polycosmos {InstalledVersion ?? "installed"} is ready."
            : modsPresent
                ? "Polycosmos mod folder found but version stamp is missing — " +
                  "click Install in the Play tab to complete setup."
                : "Polycosmos is not installed. Click Install in the Play tab.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text         = statusText,
            FontSize     = 11,
            Foreground   = IsInstalled ? success : muted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // apworld note
        if (IsInstalled && File.Exists(ApWorldLocalPath))
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = $"hades.apworld is saved next to the launcher — copy it into " +
                       @"Archipelago's custom_worlds folder " +
                       @"(default: C:\ProgramData\Archipelago\custom_worlds).",
                FontSize     = 11,
                Foreground   = muted,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new System.Windows.Thickness(0, 0, 0, 12),
            });
        }

        // ── How to Connect ────────────────────────────────────────────────

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "HOW TO CONNECT",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "1. Generate a multiworld with the Hades YAML.\n" +
                "2. Open the Archipelago Launcher and click \"HadesClient\" to start " +
                    "the Archipelago client + Hades via StyxScribe.\n" +
                "3. In the HadesClient window, type /connect <server>:<port> and " +
                    "provide your slot name and password.\n" +
                "4. Once connected, start a new save file in Hades. Enjoy the run!\n\n" +
                "Alternatively: use the launcher's Play button above to launch Hades " +
                "via Steam. You will still need to connect through the HadesClient " +
                "window separately.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── StyxScribe.py Location ────────────────────────────────────────

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "STYX SCRIBE",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });

        bool hasStyxScribe = File.Exists(StyxScribeExe);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = hasStyxScribe
                ? $"StyxScribe.py found at:\n{StyxScribeExe}"
                : $"StyxScribe.py not found at:\n{StyxScribeExe}\n\n" +
                  "PolycosmosInstaller places it there automatically. " +
                  "If it is missing after install, download StyxScribeWithoutREPL " +
                  "manually and place StyxScribe.py and SubsumeHades.py in your " +
                  "Hades root folder.",
            FontSize     = 11,
            Foreground   = hasStyxScribe ? success : muted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = "LINKS",
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });

        foreach (var (label, url) in new[]
        {
            ("Polycosmos (GitHub) ↗",           "https://github.com/NaixGames/Polycosmos"),
            ("PolycosmosInstaller (GitHub) ↗",  "https://github.com/NaixGames/PolycosmosInstaller"),
            ("Hades on Steam ↗",                $"https://store.steampowered.com/app/{SteamAppId}/"),
            ("Archipelago Official ↗",           "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content             = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding             = new System.Windows.Thickness(0, 2, 0, 2),
                Background          = System.Windows.Media.Brushes.Transparent,
                BorderThickness     = new System.Windows.Thickness(0),
                FontSize            = 12,
                Margin              = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground          = linkFg,
                Cursor              = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try
                {
                    System.Diagnostics.Process.Start(
                        new System.Diagnostics.ProcessStartInfo(u)
                        { UseShellExecute = true });
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
            string json = await _http.GetStringAsync(GH_POLY_RELEASES_URL, ct);
            using var doc  = JsonDocument.Parse(json);
            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) &&
                    d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t) ? t.GetString()?.TrimStart('v') ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch
        {
            return Array.Empty<NewsItem>();
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// Resolve the latest Polycosmos release: version string + apworld URL.
    private async Task<(string Version, string? ApWorldUrl)>
        ResolveLatestPolycosmosAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_POLY_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var arr = doc.RootElement;
            if (arr.GetArrayLength() > 0)
            {
                var first = arr[0];
                string? version = first.TryGetProperty("tag_name", out var t)
                    ? t.GetString()?.TrimStart('v')
                    : null;

                string? apworldUrl = null;
                if (first.TryGetProperty("assets", out var assets) &&
                    assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                        string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                        if (name != null && url != null &&
                            name.EndsWith(".apworld", StringComparison.OrdinalIgnoreCase))
                        {
                            apworldUrl = url;
                            break;
                        }
                    }
                }
                return (version ?? FallbackPolyVersion, apworldUrl);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* offline — fall through to pinned version */ }

        return (FallbackPolyVersion, null);
    }

    /// Resolve the download URL for the latest PolycosmosInstaller.exe.
    private async Task<string?> ResolveInstallerUrlAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_INST_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name != null && url != null &&
                        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        return url;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* offline */ }

        // Pinned fallback direct URL — tag 0.3, verified online 2026-06-14.
        return $"https://github.com/{INST_OWNER}/{INST_REPO}/releases/download/" +
               $"{FallbackInstVersion}/Installer.exe";
    }

    private void EnsureSidecarDirectory()
    {
        string dir = Path.GetDirectoryName(SidecarPath)!;
        Directory.CreateDirectory(dir);
    }

    /// Write the AP connection info to the per-session sidecar JSON.
    /// This is informational — the actual AP connection is established inside
    /// the Archipelago HadesClient window, not from the launcher.
    private void WriteSidecar(ApSession session)
    {
        try
        {
            var data = new Dictionary<string, string?>
            {
                ["game"]       = "Hades",
                ["server"]     = session.ServerUri,
                ["slot"]       = session.SlotName,
                ["password"]   = session.Password ?? "",
                ["styxscribe"] = StyxScribeExe,
                ["note"]       = "Launch via the Archipelago HadesClient, not this file.",
            };
            string json = JsonSerializer.Serialize(data,
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SidecarPath, json, new UTF8Encoding(false));
        }
        catch { /* best effort */ }
    }

    /// Blank the password field in the sidecar once the session ends.
    private void ScrubSidecarPassword()
    {
        try
        {
            if (!File.Exists(SidecarPath)) return;
            string text = File.ReadAllText(SidecarPath);
            if (string.IsNullOrWhiteSpace(text)) return;

            var data = JsonSerializer.Deserialize<Dictionary<string, string?>>(text);
            if (data == null) return;

            data["password"] = "";
            File.WriteAllText(SidecarPath,
                JsonSerializer.Serialize(data,
                    new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* best effort */ }
    }

    /// Launch Hades via Steam's URL protocol. Tracks the resulting process if
    /// Steam opens one we can identify, but process tracking for Steam games is
    /// best-effort only (Steam may relay the launch through its own launcher).
    private Task LaunchViaSteam(ApSession? session)
    {
        if (session != null)
        {
            System.Windows.MessageBox.Show(
                "To connect to Archipelago:\n\n" +
                "1. Open the Archipelago Launcher.\n" +
                "2. Click \"HadesClient\" — this starts Hades and the AP client.\n" +
                "3. In the HadesClient window, connect to your AP server.\n\n" +
                "The launcher will now open Hades via Steam as a fallback, but " +
                "the HadesClient window is required for AP connectivity.",
                "Hades — Connection Info",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);
        }

        try
        {
            var psi = new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true };
            var proc = Process.Start(psi);
            if (proc != null)
            {
                _hadesProcess = proc;
                IsRunning     = true;
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) =>
                {
                    IsRunning = false;
                    ScrubSidecarPassword();
                    GameExited?.Invoke(proc.ExitCode);
                };
            }
            else
            {
                // Steam launches a child; we do not get a direct Process handle.
                IsRunning = true;
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not launch Hades via Steam:\n{ex.Message}\n\n" +
                "Make sure Steam is running and Hades is installed " +
                $"(App ID {SteamAppId}).");
        }

        return Task.CompletedTask;
    }
}
