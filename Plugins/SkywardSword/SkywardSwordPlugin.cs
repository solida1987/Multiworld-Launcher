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
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using LauncherV2.Core;

namespace LauncherV2.Plugins.SkywardSword;

// ═══════════════════════════════════════════════════════════════════════════════
// SkywardSwordPlugin — install / patch / launch for
// "The Legend of Zelda: Skyward Sword" via the Archipelago multiworld.
//
// ── HONEST REALITY CHECK (2026-06-16, verified against AP sources) ──────────
//
//   * AP WORLD — game string "Skyward Sword" (verified: YAML template is
//     "Skyward Sword.yaml"; multiworld.gg setup guide at
//     https://multiworld.gg/tutorial/Skyward%20Sword/setup_en).
//     GameId = "skyward_sword". This is a COMMUNITY apworld — NOT in AP main.
//     Repo: Battlecats59/SS_APWorld (https://github.com/Battlecats59/SS_APWorld).
//     The .apworld file is installed by the user into their AP worlds/ folder.
//
//   * THREE COMPONENTS — verified from the multiworld.gg setup guide and
//     Battlecats59/SS_APWorld releases page:
//
//     1. SS AP Patcher (SS_APPatcher) — a tool bundled with the SS_APWorld
//        release (download from Battlecats59/SS_APWorld releases). The user
//        provides their vanilla US Wii ISO and the .apssr seed file (generated
//        by the AP server, named "AP_{seed}_P{playerid}_{name}.apssr") to
//        produce a randomized ISO that Dolphin loads.
//
//     2. Dolphin emulator (dev version recommended) — standard GCN/Wii
//        emulator. The user installs it themselves (dolphin-emu.org). The
//        randomized ISO produced by SS AP Patcher is opened in Dolphin.
//        No specific CLI args are documented; launcher uses:
//        Dolphin.exe -b -e "<randomized_iso>" (matching the standard pattern
//        used for all Dolphin-based Zelda AP integrations).
//
//     3. Skyward Sword Client — launched via ArchipelagoLauncher.exe (the
//        standard AP Launcher bundled in C:\ProgramData\Archipelago). The
//        guide refers to it as "Skyward Sword Client" in MultiworldGG.
//        Connect command after opening: /connect {server}:{port}.
//        The client hooks Dolphin memory to track location checks and deliver
//        items. Verified client name: "Skyward Sword Client".
//        ("ArchipelagoLauncher.exe" lives in C:\ProgramData\Archipelago —
//        READ-ONLY for our launcher — we detect it but never write there.)
//
//   * ISO — North American Wii version, US 1.00:
//       Wii disc ID: SOUE01 (Region: USA)
//       First 4 bytes of the disc: "SOUE" (0x53, 0x4F, 0x55, 0x45)
//     We validate by reading the first 4 bytes. File must be .iso or .wbfs;
//     size 4–5 GB accepted (Wii ISOs are ~4.7 GB uncompressed).
//
//   * ConnectsItself = true — the Skyward Sword Client (via AP Launcher) owns
//     the AP slot. The launcher must NOT hold an ApClient on the same slot
//     while the game runs (they would kick each other off the AP server).
//     The launcher provides credential prefill only.
//
//   * SupportsStandalone = false — this integration is AP-seed-only.
//
//   * PATCH FILE — the AP server generates a .apssr seed file. This plugin
//     lets the user register their .apssr file path in Settings so it is
//     readily accessible when they open the SS AP Patcher.
//
// ── WHAT THIS PLUGIN DOES ────────────────────────────────────────────────────
//   1. SETTINGS — lets user configure:
//      - SS AP Patcher exe path (from Battlecats59/SS_APWorld releases).
//      - Vanilla ISO path (US Wii 1.00; validated for "SOUE").
//      - Randomized ISO path (output of the patcher; what Dolphin loads).
//      - Dolphin exe path (auto-detected from common paths).
//      All four paths stored in a sidecar JSON (Games/ROMs/skyward_sword/).
//   2. INSTALL — no binary to auto-download (SS AP Patcher and Dolphin are
//      user-installed separately). "Install" step verifies prerequisites and
//      instructs the user.
//   3. LAUNCH (AP):
//      a. Warn if prerequisites are missing (patcher, Dolphin, AP Launcher,
//         randomized ISO).
//      b. Launch ArchipelagoLauncher.exe with "Skyward Sword Client"
//         --connect <server> [--password <pw>].
//      c. Launch Dolphin with -b -e "<randomized_iso>".
//      Order: Client first (waits for Dolphin), then Dolphin.
//   4. News from Battlecats59/SS_APWorld releases.
//
// ── UNVERIFIED / DEFENSIVE ───────────────────────────────────────────────────
//   * SS AP Patcher CLI args: not verified. We open the GUI and instruct the
//     user to load their .apssr + vanilla ISO manually.
//   * Exact AP client name: "Skyward Sword Client" (from setup guide).
//     If the name differs in the installed apworld, the AP Launcher shows its
//     menu and the user picks the correct client there.
//   * "SOUE" as disc prefix covers US 1.00 (SOUE01). European is SOUP01,
//     Japanese is SOUJ01 — those are rejected with a clear message.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SkywardSwordPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    // Community apworld repo (Battlecats59/SS_APWorld).
    private const string SsApworldOwner   = "Battlecats59";
    private const string SsApworldRepo    = "SS_APWorld";
    private static readonly string SsApworldRepoUrl =
        $"https://github.com/{SsApworldOwner}/{SsApworldRepo}";
    private static readonly string SsApworldReleasesUrl =
        $"https://api.github.com/repos/{SsApworldOwner}/{SsApworldRepo}/releases";

    // Standard AP Launcher.
    private const string ApLauncherExeName = "ArchipelagoLauncher.exe";
    private const string ApProgramDataDir  = @"C:\ProgramData\Archipelago"; // READ-ONLY

    // The client name as listed in the AP Launcher for this apworld (verified
    // from setup guide: "Skyward Sword Client").
    private const string SsClientName = "Skyward Sword Client";

    // Reference URLs.
    private const string DolphinUrl    = "https://dolphin-emu.org/download/";
    private const string SetupGuideUrl =
        "https://multiworld.gg/tutorial/Skyward%20Sword/setup_en";
    private static readonly string ApworldPageUrl = SsApworldRepoUrl;

    // ISO validation: North American Wii disc code prefix "SOUE" (bytes 0–3).
    // US 1.00: SOUE01
    private static readonly byte[] SsDiscIdPrefix =
        new byte[] { 0x53, 0x4F, 0x55, 0x45 }; // "SOUE"

    // Wii ISOs are ~4.7 GB uncompressed. Accept 3.5 GB–5.5 GB.
    private const long MinIsoBytes = 3_500L * 1024 * 1024;
    private const long MaxIsoBytes = 5_500L * 1024 * 1024;

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    private const string VersionFileName = "ss_apworld_version.dat";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "skyward_sword";
    public string DisplayName => "The Legend of Zelda: Skyward Sword";
    public string Subtitle    => "Wii via Dolphin · Archipelago";

    /// EXACT AP game string (verified: "Skyward Sword").
    public string ApWorldName => "Skyward Sword";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "skyward_sword.png");

    public string ThemeAccentColor => "#1A4A8A"; // sky blue

    public string[] GameBadges => new[]
    {
        "Requires Wii ISO (North American)",
        "Requires Dolphin (dev)",
        "Requires AP Launcher + SS apworld",
    };

    public string Description =>
        "The Legend of Zelda: Skyward Sword is the 2011 Wii epic that reveals " +
        "the origins of the Master Sword and Hyrule, following Link and the spirit " +
        "Fi across a sky world and ancient surface dungeons. " +
        "This is the Archipelago integration: the SS AP Patcher patches your vanilla " +
        "North American Wii ISO using a .apssr seed file (generated by your AP " +
        "multiworld room), Dolphin runs the result, and the Skyward Sword Client " +
        "hooks Dolphin memory to track location checks and deliver items. " +
        "You supply your own North American US 1.00 Wii ISO; the launcher guides " +
        "you through connecting the SS AP Patcher, Dolphin, and the Archipelago Launcher.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = the user has set a valid SS AP Patcher exe path.
    public bool IsInstalled => IsPatcherConfigured();
    public bool IsRunning   { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string VersionFilePath =>
        Path.Combine(GameDirectory, VersionFileName);

    private string SettingsSidecarPath =>
        Path.Combine(GameDirectory, $"{GameId}_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _ssClientProcess;
    private Process? _dolphinProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The SS Client (via AP Launcher) owns the slot — the launcher relays nothing.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = File.Exists(VersionFilePath)
                ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim()
                : (IsPatcherConfigured() ? "configured" : null);
        }
        catch { InstalledVersion = null; }

        try
        {
            string json = await _http.GetStringAsync(SsApworldReleasesUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var first = doc.RootElement.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object &&
                    first.TryGetProperty("tag_name", out var t))
                    AvailableVersion = NormalizeTag(t.GetString());
            }
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        await Task.CompletedTask;

        progress.Report((10, "Checking prerequisites..."));

        var s = LoadSettings();
        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(s.PatcherExePath) || !File.Exists(s.PatcherExePath))
            missing.Add("SS AP Patcher (download from Battlecats59/SS_APWorld releases)");

        string? dolphin = ResolveDolphinExe(s);
        if (dolphin == null)
            missing.Add("Dolphin Emulator (dev version recommended — dolphin-emu.org)");

        string? apLauncher = FindApLauncherExe();
        if (apLauncher == null)
            missing.Add("Archipelago Launcher (install from archipelago.gg)");

        if (!string.IsNullOrWhiteSpace(s.VanillaIsoPath) && File.Exists(s.VanillaIsoPath))
        {
            string? isoErr = ValidateVanillaIso(s.VanillaIsoPath);
            if (isoErr != null)
                missing.Add("Valid North American Skyward Sword Wii ISO (see Settings)");
        }
        else
        {
            missing.Add("Skyward Sword Wii ISO — North American US 1.00 (see Settings)");
        }

        if (string.IsNullOrWhiteSpace(s.RandomizedIsoPath) ||
            !File.Exists(s.RandomizedIsoPath))
        {
            missing.Add(
                "Randomized ISO — patch your vanilla ISO with SS AP Patcher first " +
                "(you need your .apssr seed file from the AP multiworld room)");
        }

        progress.Report((50, "Checking apworld releases..."));

        string? latestApworld = null;
        try
        {
            string json = await _http.GetStringAsync(SsApworldReleasesUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var first = doc.RootElement.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object &&
                    first.TryGetProperty("tag_name", out var t))
                    latestApworld = NormalizeTag(t.GetString());
            }
        }
        catch { /* network unavailable — skip */ }

        if (latestApworld != null)
            AvailableVersion = latestApworld;

        Directory.CreateDirectory(GameDirectory);

        if (missing.Count > 0)
        {
            string missingList = string.Join("\n  • ", missing);
            progress.Report((100,
                $"Setup incomplete. Missing:\n  • {missingList}\n\n" +
                "STEPS:\n" +
                "  1. Download the SS_APWorld release zip from:\n" +
                "     " + SsApworldRepoUrl + "/releases\n" +
                "     It contains the .apworld file, YAML template, and SS AP Patcher.\n" +
                "  2. Copy the .apworld file into your Archipelago worlds/ folder.\n" +
                "  3. Set the SS AP Patcher exe path in the Settings tab.\n" +
                "  4. Install Dolphin (dev version) from dolphin-emu.org.\n" +
                "  5. Install Archipelago from archipelago.gg.\n" +
                "  6. Set your North American Wii ISO in the Settings tab.\n\n" +
                "For full setup instructions, see:\n" + SetupGuideUrl));
            return;
        }

        progress.Report((100,
            "All prerequisites found.\n\n" +
            "HOW TO PLAY:\n" +
            "  1. Generate your multiworld and download your .apssr seed file.\n" +
            "  2. Open the SS AP Patcher (Settings tab > Open Patcher).\n" +
            "     Load your vanilla US ISO and the .apssr file, configure cosmetics,\n" +
            "     then click Randomize. Set the randomized ISO path in Settings.\n" +
            "  3. Click Play — the launcher starts the Skyward Sword Client (via the\n" +
            "     Archipelago Launcher) and then Dolphin with the randomized ISO.\n" +
            "  4. In the Skyward Sword Client, type /connect {address} to connect.\n\n" +
            "Refer to the full setup guide for patching details:\n" + SetupGuideUrl));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        var s = LoadSettings();
        return IsPatcherConfigured(s)
            && ResolveDolphinExe(s) != null
            && FindApLauncherExe() != null
            && !string.IsNullOrWhiteSpace(s.VanillaIsoPath)
            && File.Exists(s.VanillaIsoPath)
            && ValidateVanillaIso(s.VanillaIsoPath) == null
            && !string.IsNullOrWhiteSpace(s.RandomizedIsoPath)
            && File.Exists(s.RandomizedIsoPath);
    }

    // ── ConnectsItself / SupportsStandalone ───────────────────────────────────

    /// The SS Client (via AP Launcher) owns the AP slot — the launcher must not
    /// hold a competing ApClient connection while the game is running.
    public bool ConnectsItself     => true;

    /// No standalone (non-AP) play through this integration.
    public bool SupportsStandalone => false;

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        var s = LoadSettings();

        // Guard: randomized ISO must be configured.
        if (string.IsNullOrWhiteSpace(s.RandomizedIsoPath) ||
            !File.Exists(s.RandomizedIsoPath))
        {
            MessageBox.Show(
                "No randomized ISO configured.\n\n" +
                "Please run the SS AP Patcher on your vanilla Wii ISO and your .apssr " +
                "seed file, then set the output path in the Settings tab.",
                "Randomized ISO Not Configured",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return Task.CompletedTask;
        }

        // Start the SS Client via ArchipelagoLauncher.exe.
        StartSsClient(session);

        // Start Dolphin with the randomized ISO.
        StartDolphin(s.RandomizedIsoPath);

        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _ssClientProcess?.Kill(entireProcessTree: true); } catch { }
        try { _dolphinProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning        = false;
        _ssClientProcess = null;
        _dolphinProcess  = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself) ───────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index,
        CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));

        var panel = new System.Windows.Controls.StackPanel
            { Margin = new Thickness(0, 0, 0, 20) };

        // ── Overview ──────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Skyward Sword Archipelago uses three components:\n" +
                "  1. SS AP Patcher (from Battlecats59/SS_APWorld releases) —\n" +
                "     patches your vanilla US Wii ISO using your .apssr seed file.\n" +
                "  2. Dolphin (dev version) — Wii emulator that runs the patched ISO.\n" +
                "  3. Skyward Sword Client — part of the Archipelago Launcher;\n" +
                "     hooks Dolphin to track checks and deliver items.\n\n" +
                "The launcher starts the Client and Dolphin for you. Patch your ISO\n" +
                "with the SS AP Patcher before each new multiworld session.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        var s = LoadSettings();

        // ── Section: SS AP Patcher ────────────────────────────────────────
        panel.Children.Add(SectionHeader("SS AP PATCHER", muted));

        bool patcherOk = IsPatcherConfigured(s);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = patcherOk
                ? "Configured: " + s.PatcherExePath
                : "Not configured. Download the SS_APWorld release zip from " +
                  SsApworldRepoUrl + "/releases, then select the SS AP Patcher exe below.",
            FontSize = 11,
            Foreground = patcherOk ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });

        // Open Patcher button (shown when configured).
        if (patcherOk)
        {
            var openBtn = new System.Windows.Controls.Button
            {
                Content             = "Open SS AP Patcher",
                Width               = 180,
                Padding             = new Thickness(0, 6, 0, 6),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Margin              = new Thickness(0, 0, 0, 6),
                Background          = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
                Foreground          = fg,
                BorderBrush         = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
            };
            string patcherCapture = s.PatcherExePath!;
            openBtn.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName         = patcherCapture,
                        WorkingDirectory = Path.GetDirectoryName(patcherCapture) ?? "",
                        UseShellExecute  = true,
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not open the SS AP Patcher:\n" + ex.Message,
                        "Launch Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            };
            panel.Children.Add(openBtn);
        }

        // Patcher exe picker row.
        var patcherRow = new System.Windows.Controls.DockPanel
            { Margin = new Thickness(0, 0, 0, 4) };
        var patcherBox = new System.Windows.Controls.TextBox
        {
            Text        = s.PatcherExePath ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Path to the SS AP Patcher executable.",
        };
        var patcherBtn = new System.Windows.Controls.Button
        {
            Content     = "Select Patcher...",
            Width       = 160,
            Padding     = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        patcherBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select SS AP Patcher executable",
                Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            var cur = LoadSettings();
            cur.PatcherExePath = dlg.FileName;
            SaveSettings(cur);
            patcherBox.Text = dlg.FileName;
        };
        System.Windows.Controls.DockPanel.SetDock(
            patcherBtn, System.Windows.Controls.Dock.Right);
        patcherRow.Children.Add(patcherBtn);
        patcherRow.Children.Add(patcherBox);
        panel.Children.Add(patcherRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "To patch the game for your AP session:\n" +
                "  1. Download your .apssr seed file from the AP multiworld room.\n" +
                "  2. Open the SS AP Patcher (above).\n" +
                "  3. Load your vanilla North American US 1.00 Wii ISO and the .apssr file.\n" +
                "  4. Configure cosmetic options, then click Randomize.\n" +
                "  5. Set the resulting randomized ISO path below.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 14),
        });

        // ── Section: Vanilla ISO ──────────────────────────────────────────
        panel.Children.Add(SectionHeader(
            "VANILLA ISO (North American Wii — US 1.00)", muted));

        string vanillaIsoPath = s.VanillaIsoPath ?? "";
        bool vanillaOk = !string.IsNullOrWhiteSpace(vanillaIsoPath)
                         && File.Exists(vanillaIsoPath)
                         && ValidateVanillaIso(vanillaIsoPath) == null;

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = vanillaOk
                ? "Validated: " + vanillaIsoPath
                : string.IsNullOrWhiteSpace(vanillaIsoPath)
                    ? "No ISO selected. Pick your North American Skyward Sword Wii ISO below."
                    : "ISO not found or failed validation: " + vanillaIsoPath,
            FontSize = 11,
            Foreground = vanillaOk ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var vanillaIsoRow = new System.Windows.Controls.DockPanel
            { Margin = new Thickness(0, 0, 0, 4) };
        var vanillaIsoBox = new System.Windows.Controls.TextBox
        {
            Text        = vanillaIsoPath,
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "North American Wii ISO (disc ID starts with \"SOUE\"). " +
                          "US 1.00 release (SOUE01). ~4.7 GB. " +
                          "Never modified — input only for the SS AP Patcher.",
        };
        var vanillaIsoBtn = new System.Windows.Controls.Button
        {
            Content     = "Select Vanilla ISO...",
            Width       = 160,
            Padding     = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        vanillaIsoBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select Skyward Sword Wii ISO (North American US 1.00)",
                Filter = "Wii ISO (*.iso;*.wbfs)|*.iso;*.wbfs|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog() != true) return;

            string? bad = ValidateVanillaIso(dlg.FileName);
            if (bad != null)
            {
                MessageBox.Show(bad, "Invalid ISO",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var cur = LoadSettings();
            cur.VanillaIsoPath = dlg.FileName;
            SaveSettings(cur);
            vanillaIsoBox.Text = dlg.FileName;
        };
        System.Windows.Controls.DockPanel.SetDock(
            vanillaIsoBtn, System.Windows.Controls.Dock.Right);
        vanillaIsoRow.Children.Add(vanillaIsoBtn);
        vanillaIsoRow.Children.Add(vanillaIsoBox);
        panel.Children.Add(vanillaIsoRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Validation: first 4 bytes must be \"SOUE\" (North American game code SOUE01). " +
                   "European is SOUP01; Japanese is SOUJ01 — those are not supported.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 14),
        });

        // ── Section: Randomized ISO ───────────────────────────────────────
        panel.Children.Add(SectionHeader(
            "RANDOMIZED ISO (output of SS AP Patcher)", muted));

        string randIsoPath = s.RandomizedIsoPath ?? "";
        bool randIsoOk = !string.IsNullOrWhiteSpace(randIsoPath)
                         && File.Exists(randIsoPath);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = randIsoOk
                ? "Configured: " + randIsoPath
                : string.IsNullOrWhiteSpace(randIsoPath)
                    ? "Not set. Run SS AP Patcher on your vanilla ISO + .apssr file, " +
                      "then select the output ISO below."
                    : "File not found: " + randIsoPath,
            FontSize = 11,
            Foreground = randIsoOk ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var randIsoRow = new System.Windows.Controls.DockPanel
            { Margin = new Thickness(0, 0, 0, 4) };
        var randIsoBox = new System.Windows.Controls.TextBox
        {
            Text        = randIsoPath,
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Path to the randomized ISO generated by SS AP Patcher. " +
                          "This is what Dolphin loads.",
        };
        var randIsoBtn = new System.Windows.Controls.Button
        {
            Content     = "Select Randomized ISO...",
            Width       = 180,
            Padding     = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        randIsoBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select randomized Skyward Sword ISO (output of SS AP Patcher)",
                Filter = "Wii ISO (*.iso;*.wbfs)|*.iso;*.wbfs|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            var cur = LoadSettings();
            cur.RandomizedIsoPath = dlg.FileName;
            SaveSettings(cur);
            randIsoBox.Text = dlg.FileName;
        };
        System.Windows.Controls.DockPanel.SetDock(
            randIsoBtn, System.Windows.Controls.Dock.Right);
        randIsoRow.Children.Add(randIsoBtn);
        randIsoRow.Children.Add(randIsoBox);
        panel.Children.Add(randIsoRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "You must re-patch and re-select this ISO for every new AP multiworld session.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 14),
        });

        // ── Section: Dolphin ──────────────────────────────────────────────
        panel.Children.Add(SectionHeader("DOLPHIN EMULATOR (dev version)", muted));

        string? detectedDolphin = s.DolphinExePath;
        if (string.IsNullOrWhiteSpace(detectedDolphin) || !File.Exists(detectedDolphin))
            detectedDolphin = DetectDolphinExe();

        bool dolphinOk = !string.IsNullOrWhiteSpace(detectedDolphin)
                         && File.Exists(detectedDolphin);
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = dolphinOk
                ? "Found: " + detectedDolphin
                : "Dolphin not found. Download the dev version from dolphin-emu.org " +
                  "and set the path below.",
            FontSize = 11,
            Foreground = dolphinOk ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });

        var dolphinRow = new System.Windows.Controls.DockPanel
            { Margin = new Thickness(0, 0, 0, 4) };
        var dolphinBox = new System.Windows.Controls.TextBox
        {
            Text        = detectedDolphin ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
        };
        var dolphinBtn = new System.Windows.Controls.Button
        {
            Content     = "Select Dolphin...",
            Width       = 130,
            Padding     = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dolphinBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select Dolphin.exe",
                Filter = "Dolphin (*.exe)|*.exe|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog() != true) return;
            if (!dlg.FileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("That does not look like a Dolphin executable.",
                    "Invalid Exe", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var cur = LoadSettings();
            cur.DolphinExePath = dlg.FileName;
            SaveSettings(cur);
            dolphinBox.Text = dlg.FileName;
        };
        System.Windows.Controls.DockPanel.SetDock(
            dolphinBtn, System.Windows.Controls.Dock.Right);
        dolphinRow.Children.Add(dolphinBtn);
        dolphinRow.Children.Add(dolphinBox);
        panel.Children.Add(dolphinRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "The dev version of Dolphin is recommended for best Wii compatibility.\n" +
                   "Common Dolphin paths:\n" +
                   "  • %LOCALAPPDATA%\\Dolphin\\Dolphin.exe\n" +
                   "  • %ProgramFiles%\\Dolphin\\Dolphin.exe\n" +
                   "  • %ProgramFiles(x86)%\\Dolphin\\Dolphin.exe\n" +
                   "Dolphin is launched with: Dolphin.exe -b -e \"<randomized ISO>\"",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 14),
        });

        // ── Section: AP Launcher ──────────────────────────────────────────
        panel.Children.Add(SectionHeader(
            "ARCHIPELAGO LAUNCHER (SKYWARD SWORD CLIENT)", muted));

        string? apLauncher = FindApLauncherExe();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = apLauncher != null
                ? "Found: " + apLauncher
                : "Archipelago Launcher not found in " + ApProgramDataDir + ".\n" +
                  "Install Archipelago from archipelago.gg and copy the Skyward Sword\n" +
                  "apworld (.apworld file) from " + SsApworldRepoUrl + "/releases\n" +
                  "into your Archipelago worlds/ folder.",
            FontSize = 11,
            Foreground = apLauncher != null ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: Links ────────────────────────────────────────────────
        panel.Children.Add(SectionHeader("LINKS", muted));
        foreach (var (label, url) in new[]
        {
            ("SS AP Setup Guide ↗",          SetupGuideUrl),
            ("SS_APWorld (GitHub) ↗",        ApworldPageUrl),
            ("Dolphin Emulator ↗",           DolphinUrl),
            ("Archipelago Official ↗",       "https://archipelago.gg"),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content             = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding             = new Thickness(0, 2, 0, 2),
                Background          = Brushes.Transparent,
                BorderThickness     = new Thickness(0),
                FontSize            = 12,
                Margin              = new Thickness(0, 0, 0, 4),
                Foreground          = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor              = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); }
                catch { }
            };
            panel.Children.Add(btn);
        }

        return panel;
    }

    private static System.Windows.Controls.TextBlock SectionHeader(
        string text, System.Windows.Media.Brush brush)
        => new System.Windows.Controls.TextBlock
        {
            Text       = text,
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = brush,
            Margin     = new Thickness(0, 8, 0, 8),
        };

    // ── News — Battlecats59/SS_APWorld releases ───────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(SsApworldReleasesUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string? tag = el.TryGetProperty("tag_name", out var t)
                    ? t.GetString() : null;
                if (tag == null) continue;

                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) &&
                    d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: NormalizeTag(tag) ?? "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start the Skyward Sword Client via ArchipelagoLauncher.exe.
    private void StartSsClient(ApSession session)
    {
        string? launcherExe = FindApLauncherExe();
        if (launcherExe == null)
        {
            MessageBox.Show(
                "Archipelago Launcher not found in " + ApProgramDataDir + ".\n\n" +
                "Please install Archipelago from https://archipelago.gg and install\n" +
                "the Skyward Sword apworld into your worlds/ folder.\n\n" +
                "Dolphin will still be launched, but location checks will not be tracked.",
                "AP Launcher Not Found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        // Args: "Skyward Sword Client" [--connect <server>] [--password <pw>]
        var args = new List<string> { $"\"{SsClientName}\"" };
        if (!string.IsNullOrWhiteSpace(session.ServerUri))
            args.Add($"--connect \"{session.ServerUri}\"");
        if (!string.IsNullOrWhiteSpace(session.Password))
            args.Add($"--password \"{session.Password}\"");

        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = launcherExe,
                Arguments        = string.Join(" ", args),
                WorkingDirectory = Path.GetDirectoryName(launcherExe) ?? ApProgramDataDir,
                UseShellExecute  = false,
            });
            if (proc != null)
            {
                _ssClientProcess = proc;
                IsRunning = true;
                try
                {
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (_, _) =>
                    {
                        IsRunning = false;
                        GameExited?.Invoke(proc.ExitCode);
                    };
                }
                catch { /* non-fatal */ }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Could not start the Archipelago Launcher:\n" + ex.Message +
                "\n\nDolphin will still be launched. Start the Skyward Sword Client " +
                "manually from the Archipelago Launcher if needed.",
                "Client Launch Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// Start Dolphin with the randomized ISO: Dolphin.exe -b -e "<isoPath>".
    private void StartDolphin(string isoPath)
    {
        var s = LoadSettings();
        string? dolphinExe = ResolveDolphinExe(s);
        if (dolphinExe == null)
        {
            MessageBox.Show(
                "Dolphin.exe not found. Please install the dev version of Dolphin from:\n" +
                DolphinUrl + "\n\nThen set the Dolphin path in Settings.",
                "Dolphin Not Found",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = dolphinExe,
                Arguments        = $"-b -e \"{isoPath}\"",
                WorkingDirectory = Path.GetDirectoryName(dolphinExe) ?? "",
                UseShellExecute  = false,
            });
            if (proc != null)
            {
                _dolphinProcess = proc;
                IsRunning = true;
                try
                {
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (_, _) =>
                    {
                        IsRunning = false;
                        GameExited?.Invoke(proc.ExitCode);
                    };
                }
                catch { /* non-fatal */ }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Could not start Dolphin:\n" + ex.Message,
                "Dolphin Launch Error",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    // ── Private helpers — Dolphin detection ──────────────────────────────────

    private static string? ResolveDolphinExe(SsSettings? s = null)
    {
        if (s != null
            && !string.IsNullOrWhiteSpace(s.DolphinExePath)
            && File.Exists(s.DolphinExePath))
            return s.DolphinExePath;
        return DetectDolphinExe();
    }

    private static string? DetectDolphinExe()
    {
        string[] exeNames = { "Dolphin.exe", "DolphinQt2.exe" };
        string local   = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string prog    = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        string[] roots =
        {
            Path.Combine(local,   "Dolphin"),
            Path.Combine(local,   "Programs", "Dolphin"),
            Path.Combine(prog,    "Dolphin"),
            Path.Combine(prog,    "Dolphin Emulator"),
            Path.Combine(progX86, "Dolphin"),
            Path.Combine(progX86, "Dolphin Emulator"),
        };

        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (string name in exeNames)
            {
                string candidate = Path.Combine(root, name);
                if (File.Exists(candidate)) return candidate;
            }
            try
            {
                foreach (string sub in Directory.EnumerateDirectories(root))
                {
                    foreach (string name in exeNames)
                    {
                        string candidate = Path.Combine(sub, name);
                        if (File.Exists(candidate)) return candidate;
                    }
                }
            }
            catch { }
        }
        return null;
    }

    // ── Private helpers — AP Launcher detection ───────────────────────────────

    private static string? FindApLauncherExe()
    {
        string candidate = Path.Combine(ApProgramDataDir, ApLauncherExeName);
        return File.Exists(candidate) ? candidate : null;
    }

    // ── Private helpers — ISO validation ─────────────────────────────────────

    /// Returns null if the vanilla ISO is acceptable; otherwise a human-readable reason.
    private static string? ValidateVanillaIso(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "No file selected.";

        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext != ".iso" && ext != ".wbfs")
            return "The file must be a Wii ISO or WBFS image (.iso or .wbfs).";

        FileInfo fi;
        try { fi = new FileInfo(path); }
        catch { return "Could not access the file. Is it on a network drive or locked?"; }

        if (!fi.Exists)
            return "File does not exist: " + path;

        if (fi.Length < MinIsoBytes || fi.Length > MaxIsoBytes)
            return $"Unexpected file size ({fi.Length / (1024 * 1024)} MB). " +
                   "A Skyward Sword Wii ISO is approximately 4,700 MB.";

        try
        {
            byte[] header = new byte[SsDiscIdPrefix.Length];
            using var f = File.OpenRead(path);
            int read = f.Read(header, 0, header.Length);
            if (read < SsDiscIdPrefix.Length)
                return "Could not read the disc header. The file may be corrupt.";

            for (int i = 0; i < SsDiscIdPrefix.Length; i++)
            {
                if (header[i] != SsDiscIdPrefix[i])
                {
                    string found = Encoding.ASCII.GetString(header);
                    return $"This does not appear to be a North American Skyward Sword " +
                           $"disc (found game code \"{found}\" — expected \"SOUE\"). " +
                           "European is SOUP01; Japanese is SOUJ01; only the North " +
                           "American release (SOUE01) is supported by SS AP Patcher.";
                }
            }
        }
        catch (Exception ex)
        {
            return "Could not read the ISO header: " + ex.Message;
        }

        return null; // valid
    }

    // ── Private helpers — settings sidecar ───────────────────────────────────

    private bool IsPatcherConfigured(SsSettings? s = null)
    {
        s ??= LoadSettings();
        return !string.IsNullOrWhiteSpace(s.PatcherExePath)
               && File.Exists(s.PatcherExePath);
    }

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..] : tag;
    }

    private sealed class SsSettings
    {
        public string? PatcherExePath    { get; set; }
        public string? VanillaIsoPath    { get; set; }
        public string? RandomizedIsoPath { get; set; }
        public string? DolphinExePath    { get; set; }
    }

    private SsSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<SsSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(SsSettings s)
    {
        try
        {
            Directory.CreateDirectory(GameDirectory);
            File.WriteAllText(
                SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }
}
