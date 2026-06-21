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
using System.Windows.Media;
using LauncherV2.Core;

namespace LauncherV2.Plugins.SlyCooper;

// ═══════════════════════════════════════════════════════════════════════════════
// SlyCooperPlugin — install / launch support for "Sly Cooper and the Thievius
// Raccoonus" played through the sly1 Archipelago world
// (github.com/hoppel16/ArchipelagoBranchSly1).
//
// ── HONEST REALITY CHECK (2026-06-15, verified against hoppel16 repo + wiki) ──
//
// The AP world is a COMMUNITY FORK of Archipelago — the game integration lives
// in the sly1 subtree of hoppel16/ArchipelagoBranchSly1. As of the latest
// release (v0.3.3-alpha) the world is distributed as a standalone sly1.apworld
// file that the user installs into their Archipelago installation by
// double-clicking it, exactly like any other community apworld.
//
// KEY FACTS:
//
//   * EMULATOR: PCSX2 2.2.0+ (the free PS2 emulator).  The AP integration
//     communicates with PCSX2 via PINE (Plugin Interface for Native Emulation),
//     PCSX2's built-in shared-memory IPC channel.  PINE must be enabled in PCSX2:
//       Settings > System > Advanced > PINE Settings > Enable, Slot = 28011.
//     PCSX2 2.2.0 ships PINE; the user selects their ISO at startup as usual.
//
//   * THE AP CLIENT is the Python client bundled INSIDE the sly1.apworld /
//     ArchipelagoBranchSly1 fork.  After installing the apworld, the
//     Archipelago Launcher shows a "Sly 1 Client" entry.  That client connects
//     to both PCSX2 (PINE slot 28011) and the AP server.  THE AP SERVER
//     CONNECTION IS ENTERED IN THAT CLIENT — there is no command-line or config-
//     file prefill available from this launcher (verified: the setup doc at
//     worlds/sly1/docs/setup_en.md shows manual entry in the client).
//     ConnectsItself = true: the launcher MUST NOT hold its own ApClient on
//     this slot while the Sly 1 Client is running.
//
//   * GAME DATA is BRING-YOUR-OWN: the user supplies a legally-obtained NTSC
//     PS2 disc of Sly Cooper and the Thievius Raccoonus, dumped to an ISO.
//     Both the black-label (NTSC 1.0) and Greatest Hits (NTSC 1.1) releases
//     are supported.  This plugin copies the user's ISO into its own ROM
//     library (§11 — original never modified) and launches PCSX2 with it.
//
//   * THE AP WORLD game string is "Sly Cooper and the Thievius Raccoonus"
//     (verified against worlds/sly1/docs/setup_en.md, the Archipelago wiki
//     page for Sly 1, and the SlyMods wiki entry).  The apworld file is
//     named sly1.apworld.
//
//   * APWORLD INSTALL is MANUAL: the user downloads sly1.apworld from the
//     hoppel16 releases page and double-clicks it.  This launcher cannot
//     install another team's apworld into the user's Archipelago data folder
//     automatically (the apworld system has no documented programmatic API for
//     external tools).  The settings panel provides a direct download link and
//     clear instructions.
//
//   * PCSX2 INSTALL is also BRING-YOUR-OWN: the user installs PCSX2 2.2.0+
//     from pcsx2.net.  This plugin detects a PCSX2 install via the common
//     locations and a user-supplied path.  The launcher can open PCSX2 with the
//     user's ISO pre-loaded as a convenience; PINE must be enabled manually the
//     first time (the settings panel explains exactly how).
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. ISO = bring-your-own picker; validated loosely by size (a Sly 1 PS2 ISO
//      is ~1.2–3.0 GB for a single-layer DVD).  Copied into the launcher's own
//      ROM library (original untouched).
//   2. PCSX2 = user-installed; this plugin scans common install locations and
//      accepts a user-supplied path.  Not downloaded by this launcher.
//   3. LAUNCH = opens PCSX2 with the user's ISO pre-loaded.  The user then
//      opens the Archipelago Launcher and runs the "Sly 1 Client" to connect.
//      ConnectsItself = true.  SupportsStandalone = true (PCSX2 + ISO plays
//      vanilla Sly 1 perfectly without AP).
//   4. VERSION CHECK = polls hoppel16/ArchipelagoBranchSly1 for the latest
//      release tag.  InstalledVersion is read from our own sidecar stamp (we
//      cannot query the user's installed apworld version programmatically).
//   5. INSTALL: "Install" here means "download the latest sly1.apworld from
//      the releases page" — the launcher saves it next to the ISO in the ROM
//      library and opens the containing folder so the user can double-click to
//      install it.  (We do NOT auto-install into Archipelago's data folder.)
//
// ── DEFENSIVE ─────────────────────────────────────────────────────────────────
//   * IsInstalled = true when the user has picked an ISO (we cannot verify the
//     apworld install from here, so the ISO presence is the practical gate).
//   * All network calls swallow non-cancellation exceptions (contract: never
//     throw on network failure in CheckForUpdateAsync).
//   * One launcher-side setting block (ISO path + PCSX2 path) is stored in
//     this plugin's OWN JSON sidecar, not in Core/SettingsStore.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SlyCooperPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string GH_OWNER = "hoppel16";
    private const string GH_REPO  = "ArchipelagoBranchSly1";

    private const string ReleasesApiUrl  = $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases/latest";
    private const string ReleasesPageUrl = $"https://github.com/{GH_OWNER}/{GH_REPO}/releases";
    private const string SetupGuideUrl   =
        $"https://github.com/{GH_OWNER}/{GH_REPO}/blob/main/worlds/sly1/docs/setup_en.md";
    private const string SlymModsWikiUrl = "https://slymods.info/wiki/Mod:Archipelago";
    private const string ArchipelagoWikiUrl =
        "https://archipelago.miraheze.org/wiki/Sly_Cooper_and_the_Thievius_Raccoonus";
    private const string Pcsx2Url = "https://pcsx2.net/";

    /// Pinned fallback version when the GitHub API is unreachable.
    private const string FallbackVersion = "0.3.3-alpha";

    private const string VersionFileName  = "sly1_version.dat";
    private const string SettingsFileName = "sly1_launcher.json";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(15),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// PS2 ISO extensions accepted by PCSX2.
    private static readonly string[] IsoExtensions = { ".iso", ".bin", ".img" };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "slycooper";
    public string DisplayName => "Sly Cooper and the Thievius Raccoonus";
    public string Subtitle    => "PS2 Emulation (PCSX2 + PINE)";

    /// EXACT AP world game string — verified against the setup doc, the
    /// Archipelago wiki page for Sly 1, and the SlyMods wiki.
    public string ApWorldName => "Sly Cooper and the Thievius Raccoonus";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "slycooper.png");

    public string ThemeAccentColor => "#1A5C94";   // Sly's signature blue hat / cel-shaded blues

    public string[] GameBadges => new[] { "Requires PS2 ISO", "Requires PCSX2 2.2.0+" };

    public string Description =>
        "Sly Cooper and the Thievius Raccoonus, the 2002 Sucker Punch stealth-" +
        "platformer, played with an Archipelago randomizer via PCSX2 emulation. " +
        "The Fiendish Five have scattered the pages of the Thievius Raccoonus " +
        "across the multiworld, and Sly must collect them to challenge Clockwerk — " +
        "a classic MacGuffin hunt through cel-shaded levels of bottles, keys, " +
        "vaults, and more. The integration runs through PCSX2's built-in PINE " +
        "channel (slot 28011); the Sly 1 Client in your Archipelago Launcher " +
        "connects to PCSX2 and to the AP server. You must supply your own " +
        "legally-obtained NTSC PS2 disc of Sly 1 (black-label or Greatest Hits).";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = the user has picked an ISO (we can't verify the apworld
    /// installation path from outside Archipelago).
    public bool IsInstalled => File.Exists(VersionFilePath) ||
                               !string.IsNullOrEmpty(LoadSettings().IsoPath ?? "");

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "Games", "SlyCooper");

    private string VersionFilePath    => Path.Combine(GameDirectory, VersionFileName);
    private string SettingsSidecarPath => Path.Combine(GameDirectory, SettingsFileName);

    private string RomLibraryDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _pcsx2Process;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Sly 1 Client (the Archipelago-bundled PINE client) communicates with
    // the AP server directly.  These events exist for interface compatibility.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself / Standalone ───────────────────────────────────────────

    /// The Sly 1 Client (bundled Archipelago Python client) holds the AP slot —
    /// the launcher MUST NOT connect on the same slot while the game is running.
    public bool ConnectsItself => true;

    /// Playing vanilla Sly 1 in PCSX2 without AP is fully supported.
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Read our own stamp for InstalledVersion.
        try
        {
            var s = LoadSettings();
            InstalledVersion = File.Exists(VersionFilePath)
                ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim()
                : (!string.IsNullOrEmpty(s.IsoPath) ? "installed" : null);
        }
        catch
        {
            InstalledVersion = null;
        }

        // Poll GitHub for the latest release.
        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(GH_OWNER, GH_REPO, ct));
        }
        catch
        {
            AvailableVersion = null; // contract: never throw on network failure
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    /// "Install" for Sly 1 means:
    ///   1. Download the latest sly1.apworld from the hoppel16 releases page
    ///      into GameDirectory (so the user can double-click to install it into
    ///      Archipelago).
    ///   2. Open the folder containing the downloaded apworld.
    ///
    /// We do NOT auto-install into Archipelago's custom_worlds folder: that
    /// folder path depends on the user's Archipelago installation and OS account,
    /// and the apworld system has no documented external-tool API.  The note on
    /// the progress message and the settings panel both make this clear.
    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((2, "Checking latest sly1.apworld release..."));

        var (version, apworldUrl) = await ResolveLatestApworldAsync(ct);
        AvailableVersion = version;

        if (apworldUrl == null)
            throw new InvalidOperationException(
                "Could not find a sly1.apworld asset in the latest release on GitHub. " +
                "Download it manually from: " + ReleasesPageUrl);

        // Download the apworld file.
        progress.Report((5, $"Downloading sly1.apworld {version}..."));
        Directory.CreateDirectory(GameDirectory);

        string apworldDst = Path.Combine(GameDirectory, "sly1.apworld");
        using var response = await _http.GetAsync(
            apworldUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total      = response.Content.Headers.ContentLength ?? -1;
        long downloaded = 0;

        await using (var src    = await response.Content.ReadAsStreamAsync(ct))
        await using (var outFs  = File.Create(apworldDst))
        {
            var buf = new byte[81920];
            int bytesRead;
            while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
            {
                await outFs.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                downloaded += bytesRead;
                if (total > 0)
                {
                    int pct = (int)(5 + 80 * downloaded / total);
                    progress.Report((pct, $"Downloading sly1.apworld... {downloaded / 1024}KB"));
                }
            }
            await outFs.FlushAsync(ct);
        }

        // Stamp our version file.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        // Open the containing folder so the user can double-click the apworld.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName        = "explorer.exe",
                Arguments       = $"/select,\"{apworldDst}\"",
                UseShellExecute = true,
            });
        }
        catch { /* non-fatal — folder open is a convenience only */ }

        progress.Report((100,
            $"sly1.apworld {version} downloaded to {GameDirectory}. " +
            "Double-click sly1.apworld in the opened folder to install it into " +
            "Archipelago. Then pick your Sly 1 ISO in Settings if you have not already. " +
            "Enable PINE in PCSX2 (Settings > System > Advanced > PINE Settings > Enable, " +
            "Slot = 28011) before your first session."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        // Best-effort: check the ISO is still present at the saved path.
        string? iso = LoadSettings().IsoPath;
        return !string.IsNullOrEmpty(iso) && File.Exists(iso);
    }

    // ── Lifecycle — ValidateExistingInstall ───────────────────────────────────

    // Not used for Sly 1 (we manage the ISO ourselves via the picker).

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    /// Launch PCSX2 pre-loaded with the user's Sly 1 ISO.
    /// ConnectsItself = true: the AP server connection is made inside the
    /// "Sly 1 Client" in the Archipelago Launcher, NOT here.
    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        _ = session; // no prefill mechanism exists for PINE-based games
        LaunchPcsx2();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        LaunchPcsx2();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _pcsx2Process?.Kill(entireProcessTree: true); } catch { }
        IsRunning      = false;
        _pcsx2Process  = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The Sly 1 Client receives and forwards items via PINE — nothing for
        // this launcher to relay.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The Sly 1 Client shows its own connection status in the AP text client.
    }

    // ── Settings UI ──────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        // ── Honest integration header ──────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Sly Cooper uses PCSX2 + PINE for its AP integration. The launcher " +
                   "opens PCSX2 with your ISO; you then open the \"Sly 1 Client\" in the " +
                   "Archipelago Launcher to connect to both PCSX2 and the AP server. " +
                   "The apworld is installed separately (double-click the downloaded file). " +
                   "These external steps are not verified by this launcher.",
            FontSize     = 11,
            Foreground   = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: sly1.apworld ─────────────────────────────────────────
        AddSectionHeader(panel, "SLY 1 APWORLD", muted);

        string? apworldPath = File.Exists(Path.Combine(GameDirectory, "sly1.apworld"))
            ? Path.Combine(GameDirectory, "sly1.apworld") : null;

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = apworldPath != null
                       ? "sly1.apworld downloaded: " + apworldPath
                       : (File.Exists(VersionFilePath)
                           ? "sly1.apworld was downloaded — check " + GameDirectory + " and double-click it to install."
                           : "Not yet downloaded. Click Install in the Play tab to download sly1.apworld."),
            FontSize     = 11,
            Foreground   = apworldPath != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 6),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After downloading, double-click sly1.apworld to install it into your " +
                   "Archipelago installation. The Archipelago Launcher will then show a " +
                   "\"Sly 1 Client\" entry — use that to connect to your AP server.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: PCSX2 ────────────────────────────────────────────────
        AddSectionHeader(panel, "PCSX2 2.2.0+ PATH", muted);

        var settings = LoadSettings();
        string? pcsx2Exe = settings.Pcsx2ExePath;

        // Auto-detect if not set.
        if (string.IsNullOrEmpty(pcsx2Exe) || !File.Exists(pcsx2Exe))
            pcsx2Exe = ResolvePcsx2Exe();

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = pcsx2Exe != null
                       ? "PCSX2 found: " + pcsx2Exe
                       : "PCSX2 not found. Install it from pcsx2.net and pick the exe below.",
            FontSize     = 11,
            Foreground   = pcsx2Exe != null ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 6),
        });

        var pcsx2Row = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var pcsx2Box = new System.Windows.Controls.TextBox
        {
            Text        = settings.Pcsx2ExePath ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var pcsx2Btn = new System.Windows.Controls.Button
        {
            Content     = "Browse...",
            Width       = 90,
            Padding     = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        pcsx2Btn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select pcsx2.exe or pcsx2-qtx64.exe",
                Filter = "PCSX2 executable (pcsx2*.exe)|pcsx2*.exe|All executables (*.exe)|*.exe",
            };
            if (dlg.ShowDialog() != true) return;
            var s2 = LoadSettings();
            s2.Pcsx2ExePath = dlg.FileName;
            SaveSettings(s2);
            pcsx2Box.Text = dlg.FileName;
        };
        System.Windows.Controls.DockPanel.SetDock(pcsx2Btn, System.Windows.Controls.Dock.Right);
        pcsx2Row.Children.Add(pcsx2Btn);
        pcsx2Row.Children.Add(pcsx2Box);
        panel.Children.Add(pcsx2Row);

        // PINE note
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "IMPORTANT — enable PINE in PCSX2 before your first session: " +
                   "Settings > System > Advanced > PINE Settings > Enable, Slot = 28011. " +
                   "The Sly 1 Client connects to PCSX2 on that slot.",
            FontSize     = 11,
            Foreground   = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 8, 0, 12),
        });

        // ── Section: Sly 1 ISO ────────────────────────────────────────────
        AddSectionHeader(panel, "SLY 1 PS2 ISO (NTSC)", muted);

        var settings3  = LoadSettings();
        var isoRow     = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4)
        };
        var isoBox = new System.Windows.Controls.TextBox
        {
            Text        = settings3.IsoPath ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var isoBtn = new System.Windows.Controls.Button
        {
            Content     = "Select ISO...",
            Width       = 110,
            Padding     = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        isoBtn.Click += (_, _) =>
        {
            if (PromptForIsoFile())
                isoBox.Text = LoadSettings().IsoPath ?? "";
        };
        System.Windows.Controls.DockPanel.SetDock(isoBtn, System.Windows.Controls.Dock.Right);
        isoRow.Children.Add(isoBtn);
        isoRow.Children.Add(isoBox);
        panel.Children.Add(isoRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Both the black-label (NTSC 1.0) and Greatest Hits (NTSC 1.1) North " +
                   "American releases are supported. The launcher copies your ISO into its " +
                   "own folder — your original file is never modified. PCSX2 plays directly " +
                   "from this copy.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        AddSectionHeader(panel, "GUIDED SETUP", muted);

        foreach (string step in new[]
        {
            "1. Install PCSX2 2.2.0+ from pcsx2.net and set its path above.",
            "2. Enable PINE: in PCSX2, go to Settings > System > Advanced > PINE Settings, " +
               "check Enable, and set Slot to 28011.",
            "3. Pick your Sly 1 NTSC PS2 ISO above.",
            "4. Install Archipelago 0.5.0+ from archipelago.gg if you have not already.",
            "5. Click Install on the Play tab to download sly1.apworld. Double-click the " +
               "downloaded file to install it into Archipelago.",
            "6. To play: click Play on this tile to open PCSX2 with Sly 1. In PCSX2, start " +
               "the disc. Then open the Archipelago Launcher and run the \"Sly 1 Client\". " +
               "Enter your AP server address and slot name in that client and click Connect.",
            "Tip: you do NOT need to do anything special in-game for the AP connection — " +
               "the Sly 1 Client reads and writes game state through PCSX2's PINE channel.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text         = step,
                FontSize     = 11,
                Foreground   = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin       = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        AddSectionHeader(panel, "LINKS", muted);

        foreach (var (label, url) in new[]
        {
            ("sly1 APWorld — setup guide (GitHub) ->",  SetupGuideUrl),
            ("sly1 APWorld — releases page (GitHub) ->", ReleasesPageUrl),
            ("SlyMods wiki — Archipelago mod ->",       SlymModsWikiUrl),
            ("Archipelago wiki — Sly 1 ->",             ArchipelagoWikiUrl),
            ("PCSX2 (pcsx2.net) ->",                    Pcsx2Url),
            ("Archipelago Official ->",                  "https://archipelago.gg"),
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
                Foreground          = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor              = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo(u) { UseShellExecute = true });
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
            // Pull from the hoppel16 repo releases; they carry the changelogs.
            string allReleasesUrl =
                $"https://api.github.com/repos/{GH_OWNER}/{GH_REPO}/releases";
            string json = await _http.GetStringAsync(allReleasesUrl, ct);
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

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Poll GitHub for the latest release tag name (no asset needed here).
    private async Task<string> FetchLatestVersionAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(ReleasesApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tag_name", out var t))
                return NormalizeTag(t.GetString()) ?? FallbackVersion;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* rate-limited / offline */ }
        return FallbackVersion;
    }

    /// Resolve latest release: version string + direct sly1.apworld download URL.
    private async Task<(string Version, string? ApworldUrl)> ResolveLatestApworldAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(ReleasesApiUrl, ct);
            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString()) : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? url = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? dl   = a.TryGetProperty("browser_download_url", out var du) ? du.GetString() : null;
                    if (name == null || dl == null) continue;

                    if (name.EndsWith(".apworld", StringComparison.OrdinalIgnoreCase)
                        && name.Contains("sly", StringComparison.OrdinalIgnoreCase))
                    {
                        url = dl;
                        break;
                    }
                }
                if (url != null)
                    return (version, url);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → fallback below */ }

        return (FallbackVersion, null);
    }

    // ── Private helpers — PCSX2 exe resolution ───────────────────────────────

    /// Scan common PCSX2 install locations and return the exe path, or null.
    private static string? ResolvePcsx2Exe()
    {
        foreach (string candidate in CandidatePcsx2Paths())
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<string> CandidatePcsx2Paths()
    {
        string? prog   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string? local  = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // Qt-based build (PCSX2 1.7+/2.x) — the shipped exe name on Windows.
        string[] exeNames = { "pcsx2-qtx64.exe", "pcsx2-qt.exe", "pcsx2.exe" };

        // Standard installer targets.
#pragma warning disable CS8600
        foreach (string root in new[]
        {
            prog    == null ? null : Path.Combine(prog,    "PCSX2"),
            progX86 == null ? null : Path.Combine(progX86, "PCSX2"),
            local   == null ? null : Path.Combine(local,   "PCSX2"),
        })
        {
            if (root == null || !Directory.Exists(root)) continue;
            foreach (string name in exeNames)
            {
                string full = Path.Combine(root, name);
                if (File.Exists(full)) yield return full;
            }
        }
#pragma warning restore CS8600

        // Portable / user-placed installs on common drive roots.
        foreach (string drive in new[] { @"C:\", @"D:\", @"E:\" })
        {
            foreach (string folder in new[] { "PCSX2", "Emulators\\PCSX2", "Games\\PCSX2" })
            {
                string root = Path.Combine(drive, folder);
                if (!Directory.Exists(root)) continue;
                foreach (string name in exeNames)
                {
                    string full = Path.Combine(root, name);
                    if (File.Exists(full)) yield return full;
                }
            }
        }
    }

    // ── Private helpers — PCSX2 launch ───────────────────────────────────────

    private void LaunchPcsx2()
    {
        var s    = LoadSettings();
        string? iso  = s.IsoPath;
        string? exe  = !string.IsNullOrEmpty(s.Pcsx2ExePath) && File.Exists(s.Pcsx2ExePath)
                        ? s.Pcsx2ExePath
                        : ResolvePcsx2Exe();

        if (exe == null)
            throw new FileNotFoundException(
                "PCSX2 was not found. Install it from pcsx2.net and pick its exe in Settings.",
                "pcsx2-qtx64.exe");

        if (string.IsNullOrEmpty(iso) || !File.Exists(iso))
            throw new FileNotFoundException(
                "No Sly 1 ISO is configured. Pick your PS2 disc image in Settings.",
                "sly1.iso");

        // PCSX2 2.x Qt build: pass the ISO directly on the command line so it
        // boots immediately.  --nogui keeps it from showing the game browser
        // before the disc loads.  The PINE channel is activated by PCSX2 once
        // the game is running (the Sly 1 Client detects PCSX2 on slot 28011).
        var psi = new ProcessStartInfo
        {
            FileName        = exe,
            Arguments       = $"--nogui \"{iso}\"",
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            UseShellExecute  = false,
        };

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start PCSX2.");

        _pcsx2Process = proc;
        IsRunning     = true;
        try
        {
            proc.EnableRaisingEvents = true;
            proc.Exited += (_, _) =>
            {
                IsRunning     = false;
                GameExited?.Invoke(proc.ExitCode);
            };
        }
        catch { /* shell-launched: Exited may not fire — non-fatal */ }
    }

    // ── Private helpers — ISO (bring-your-own) ───────────────────────────────

    /// Open the ISO picker, validate by size, copy into ROM library (§11 — the
    /// original is never modified), and persist the copy's path.
    private bool PromptForIsoFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title  = "Select your Sly Cooper and the Thievius Raccoonus PS2 ISO (NTSC)",
            Filter = "Disc image (*.iso;*.bin;*.img)|*.iso;*.bin;*.img|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog() != true) return false;

        string? bad = ValidateSlyIso(dlg.FileName);
        if (bad != null)
        {
            System.Windows.MessageBox.Show(bad,
                "Not a valid Sly Cooper PS2 ISO",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return false;
        }

        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
            string dst = Path.Combine(RomLibraryDirectory, Path.GetFileName(dlg.FileName));
            File.Copy(dlg.FileName, dst, overwrite: true);
            var s = LoadSettings();
            s.IsoPath = dst;
            SaveSettings(s);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Could not copy the ISO into the launcher library:\n{ex.Message}\n\n" +
                "Nothing was changed — your original file is untouched.",
                "ISO import failed",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            return false;
        }
        return true;
    }

    /// Loose validation: accepted extension + plausible PS2 single-layer DVD
    /// size (700 MB – 4.9 GB).  PCSX2 is the authoritative validator.
    /// Returns null when acceptable, else a short human-readable reason.
    private static string? ValidateSlyIso(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (Array.IndexOf(IsoExtensions, ext) < 0)
            return "That file is not a recognised disc image. Expected a .iso, .bin, or .img file.";

        try
        {
            long len = new FileInfo(path).Length;
            const long min = 700L * 1024 * 1024;    // ~700 MB floor
            const long max = 4_900L * 1024 * 1024;  // ~4.9 GB ceiling (single-layer PS2 DVD)
            if (len < min)
                return "That file is too small to be a PS2 disc image (expected at least ~700 MB). " +
                       "Make sure it is a full ISO dump of your Sly Cooper disc.";
            if (len > max)
                return "That file is larger than a single-layer PS2 disc image. " +
                       "Make sure you picked your Sly Cooper ISO, not something else.";
        }
        catch
        {
            return "Could not read that file. Try again with a different ISO.";
        }
        return null;
    }

    // ── Private helpers — UI ─────────────────────────────────────────────────

    private static void AddSectionHeader(
        System.Windows.Controls.StackPanel panel,
        string title,
        SolidColorBrush muted)
    {
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = title,
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin     = new System.Windows.Thickness(0, 8, 0, 8),
        });
    }

    // ── Private helpers — self-contained settings sidecar ────────────────────

    private sealed class SlySettings
    {
        public string? IsoPath     { get; set; }
        public string? Pcsx2ExePath { get; set; }
    }

    private SlySettings LoadSettings()
    {
        try
        {
            Directory.CreateDirectory(GameDirectory);
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<SlySettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(SlySettings s)
    {
        try
        {
            Directory.CreateDirectory(GameDirectory);
            File.WriteAllText(
                SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));   // BOM-less UTF-8
        }
        catch { /* non-fatal — setting just won't persist this session */ }
    }
}
