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
using LauncherV2.Core;

// NOTE: WPF UI types are FULLY QUALIFIED throughout this file (System.Windows.*).
// The real launcher project sets <UseWindowsForms>true</UseWindowsForms> alongside
// WPF, so bare names Button / TextBox / Color / Brushes / MessageBox /
// FontWeights / Orientation / Clipboard collide between System.Windows.Forms and
// System.Windows[.Controls/.Media] (CS0104). Qualifying every UI type avoids that.
// GlobalUsings.cs already aliases the colliding short names project-wide — so this
// file must NOT add any file-level `using X = System.Windows...;` alias (CS1537).

namespace LauncherV2.Plugins.CaveStory;

// ═══════════════════════════════════════════════════════════════════════════════
// CaveStoryPlugin — install / launch for Cave Story played through the
// kl3cks7r/CaveStoryArchipelago apworld, which ships a Kivy-based Cave Story
// Client inside the Archipelago Launcher. The client patches the cave-story-
// randomizer project's pre-edited game files (freeware Doukutsu.exe or the
// CSTweaked variant) and then launches the game. This is a NATIVE "ConnectsItself"
// integration: the AP Client (inside the official Archipelago Launcher) owns the
// AP server connection; the launcher tile manages setup and launches the AP Launcher.
//
// ── VERIFIED FACTS (2026-06-14) ──────────────────────────────────────────────
//
//   * THE AP WORLD game string is "Cave Story" — verified live 2026-06-14 against
//     kl3cks7r/CaveStoryArchipelago __init__.py:
//     `class CaveStoryWorld(World): ... game = "Cave Story"`.
//     This is an EXTERNAL apworld (not in AP main). The apworld release asset is
//     "cave_story.apworld", latest tag 0.6.7 (verified 2026-06-14). The apworld
//     must be installed into the user's Archipelago custom_worlds folder before
//     generating a game.
//
//   * THE GAME itself is Cave Story Freeware (original freeware PC release by
//     Pixel/Studio Pixel) — NOT Cave Story+ (Steam appid 200900, paid). The AP
//     integration is built on top of the freeware game via the cave-story-randomizer
//     patcher project (duncathan/periwinkle9). Specifically supported:
//       - Freeware: pre_edited_cs/freeware/Doukutsu.exe
//       - Cave Story Tweaked: pre_edited_cs/tweaked/CSTweaked.exe
//     Cave Story+ is NOT supported by this apworld.
//
//   * THE PATCHER PROJECT is cave-story-randomizer/cave-story-randomizer (tag 2.4.3,
//     verified 2026-06-14). The AP Client (Cave Story Client, Kivy) downloads its
//     zipball from GitHub and uses it to patch the game. The user must point the
//     AP Client at the cave-story-randomizer root folder (the one that contains
//     pre_edited_cs). The AP Client's hosts.yaml key is "cave_story_settings.game_dir".
//
//   * THE AP CLIENT is the "Cave Story Client" component registered inside the
//     kl3cks7r apworld (LauncherComponents entry "CaveStoryClient"). It runs under
//     the official Archipelago Launcher (ArchipelagoLauncher.exe). Workflow:
//       1. Install the apworld (cave_story.apworld) into custom_worlds.
//       2. Set game_dir in hosts.yaml to point at the cave-story-randomizer root.
//       3. Connect to the AP server VIA THE CAVE STORY CLIENT before launching the
//          game (client patches the files then launches Doukutsu.exe or CSTweaked.exe).
//
//   * CONNECTION is made VIA THE AP LAUNCHER'S Cave Story Client. The client
//     (Protocol.py) speaks a custom binary packet protocol to the game over a
//     local TCP socket (RCON-style). The user enters the AP server address/port/
//     slot-name/password into the Cave Story Client's Kivy UI, then presses Launch.
//     The Cave Story Client patches the game files with item locations, launches the
//     game .exe, and then relays the AP connection into it. This launcher therefore
//     does NOT hold its own AP client on this slot (ConnectsItself = true).
//
// ── WHAT THIS LAUNCHER TILE DOES (honest scope) ──────────────────────────────
//   1. DOWNLOAD the latest cave_story.apworld from kl3cks7r/CaveStoryArchipelago
//      releases and install it into the user's AP custom_worlds folder
//      (C:\ProgramData\Archipelago\custom_worlds or the user-set AP data dir).
//      Per the security constraint, this launcher NEVER writes to C:\ProgramData\
//      Archipelago except to drop the .apworld into custom_worlds (which is the
//      standard supported mechanism for third-party apworlds).
//   2. DOWNLOAD the cave-story-randomizer zipball from GitHub and extract it to the
//      plugin's own sidecar dir (Games/ROMs/cave_story/cave_story_randomizer/) so
//      the user can point the AP Cave Story Client's game_dir at it.
//   3. SHOW a settings panel that surfaces the cave-story-randomizer path (for
//      pasting into the AP Client), the AP custom_worlds path, and guided steps.
//   4. LAUNCH: open the official Archipelago Launcher so the user can start the
//      Cave Story Client from it and connect. (SupportsStandalone = true — the user
//      can also just run Doukutsu.exe directly for a non-AP playthrough.)
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CaveStoryPlugin : IGamePlugin
{
    // ── External repos (verified 2026-06-14) ─────────────────────────────────
    private const string ApWorldOwner       = "kl3cks7r";
    private const string ApWorldRepo        = "CaveStoryArchipelago";
    private const string ApWorldAssetName   = "cave_story.apworld";
    private const string FallbackApWorldTag = "0.6.7";

    private static readonly string ApWorldReleasesApiUrl =
        $"https://api.github.com/repos/{ApWorldOwner}/{ApWorldRepo}/releases/latest";
    private static readonly string ApWorldReleasesUrl =
        $"https://github.com/{ApWorldOwner}/{ApWorldRepo}/releases";

    private const string PatcherOwner     = "cave-story-randomizer";
    private const string PatcherRepo      = "cave-story-randomizer";
    private const string FallbackPatchTag = "2.4.3";

    private static readonly string PatcherReleasesApiUrl =
        $"https://api.github.com/repos/{PatcherOwner}/{PatcherRepo}/releases/latest";

    // Publicly browsable documentation / resources
    private static readonly string ApWorldGitHubUrl  =
        $"https://github.com/{ApWorldOwner}/{ApWorldRepo}";
    private static readonly string PatcherGitHubUrl  =
        $"https://github.com/{PatcherOwner}/{PatcherRepo}";
    private const string CaveStoryFreewareUrl =
        "https://www.cavestory.org/download/cave-story.php";
    private const string SetupGuideUrl =
        $"https://github.com/{ApWorldOwner}/{ApWorldRepo}/blob/main/docs/setup_en.md";
    private const string ArchipelagoDownloadUrl = "https://github.com/ArchipelagoMW/Archipelago/releases/latest";
    private const string ArchipelagoSiteUrl     = "https://archipelago.gg";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "cave_story";
    public string DisplayName => "Cave Story";
    public string Subtitle    => "Native PC · Cave Story Client (AP Launcher)";

    /// Exact AP game string — verified against kl3cks7r/CaveStoryArchipelago
    /// __init__.py: `class CaveStoryWorld(World): ... game = "Cave Story"`.
    public string ApWorldName => "Cave Story";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "cave_story.png");

    public string ThemeAccentColor => "#D44A2A"; // The Polar Star's red glow

    public string[] GameBadges => new[]
    {
        "Freeware (cavestory.org)",
        "External apworld",
        "Requires Archipelago Launcher",
    };

    public string Description =>
        "Cave Story, Pixel's beloved 2004 freeware action-platformer, played through " +
        "the Cave Story Archipelago integration by kl3cks7r. The game's items are " +
        "shuffled across the multiworld — weapons, life capsules, trade items, and key " +
        "items like the Booster all enter the pool. The integration works on top of the " +
        "original freeware (Doukutsu.exe) or Cave Story Tweaked, patched by the cave-" +
        "story-randomizer project (duncathan, periwinkle9). Connection to the AP server " +
        "is made via the Cave Story Client inside the official Archipelago Launcher, " +
        "which patches the game files and then launches the game. Cave Story itself is " +
        "free to download from cavestory.org (original Japanese freeware, PC). Cave " +
        "Story+ on Steam is NOT supported by this integration.";

    public string? VideoPreviewUrl  => null;
    public string[] ScreenshotUrls  => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means:
    ///   1. The cave_story.apworld is in the AP custom_worlds folder (AP side), AND
    ///   2. The cave-story-randomizer folder exists in our sidecar dir.
    public bool IsInstalled => ApWorldIsInstalled() && PatcherIsExtracted();

    public bool IsRunning { get; private set; }

    public bool ConnectsItself     => true;  // Cave Story Client in AP Launcher owns the slot
    public bool SupportsStandalone => true;  // Doukutsu.exe runs fine without AP

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Plugin working dir — where we keep the extracted patcher and downloads.
    public string GameDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", "cave_story");

    /// The cave-story-randomizer extracted root. This is the path the user must
    /// enter as game_dir in the Archipelago Cave Story Client / hosts.yaml.
    private string PatcherRootDir
        => Path.Combine(GameDirectory, "cave_story_randomizer");

    /// Sidecar JSON for this plugin (version stamps + override path).
    private string SidecarPath
        => Path.Combine(GameDirectory, "cave_story_launcher.json");

    /// The standard AP custom_worlds folder (Windows: %ProgramData%\Archipelago\...).
    /// We write ONLY cave_story.apworld here (the supported third-party apworld
    /// mechanism). Per security constraints, no other writes to ProgramData\Archipelago.
    private static string ApCustomWorldsDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Archipelago", "custom_worlds");

    /// Path where cave_story.apworld lands after install.
    private string ApWorldInstalledPath =>
        Path.Combine(ApCustomWorldsDir, ApWorldAssetName);

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The Cave Story Client (in the AP Launcher) speaks to the AP server directly.
    // The launcher tile relays nothing. These events exist for interface compatibility.
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
            // Installed version = what we stamped in the sidecar during the last
            // Install (informational; the real gate is apworld + patcher presence).
            InstalledVersion = IsInstalled ? (ReadStampedVersion() ?? "installed") : null;
        }
        catch { InstalledVersion = null; }

        try
        {
            var (version, _) = await ResolveLatestApWorldAsync(ct);
            AvailableVersion = version;
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // Step 1: Resolve the latest apworld release.
        progress.Report((2, "Checking the latest Cave Story apworld release..."));
        var (apWorldVersion, apWorldUrl) = await ResolveLatestApWorldAsync(ct);
        AvailableVersion = apWorldVersion;

        if (apWorldUrl == null)
            throw new InvalidOperationException(
                "Could not find the Cave Story apworld download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                ApWorldReleasesUrl + " and drop it into your AP custom_worlds folder: " +
                ApCustomWorldsDir);

        // Step 2: Download + install the apworld into AP custom_worlds.
        progress.Report((5, $"Downloading {ApWorldAssetName} {apWorldVersion}..."));
        await DownloadApWorldAsync(apWorldUrl, apWorldVersion, progress, ct);

        // Step 3: Download + extract the cave-story-randomizer patcher.
        progress.Report((55, "Downloading the cave-story-randomizer patcher..."));
        var (patcherVersion, patcherZipUrl) = await ResolveLatestPatcherAsync(ct);
        if (patcherZipUrl != null)
            await DownloadAndExtractPatcherAsync(patcherZipUrl, patcherVersion, progress, ct);
        else
            progress.Report((90, "Could not download the cave-story-randomizer patcher. See setup guide."));

        // Step 4: Stamp version.
        WriteStampedVersion(apWorldVersion);
        InstalledVersion = apWorldVersion;

        progress.Report((100,
            $"Cave Story apworld {apWorldVersion} installed. " +
            "Next steps: (1) Open the Archipelago Launcher and launch the Cave Story Client. " +
            "(2) Set the game_dir in the client or hosts.yaml to: " + PatcherRootDir + " " +
            "(this is where pre_edited_cs lives). (3) Connect to your AP server, then press " +
            "Launch Cave Story in the client. The client patches the files and starts the game."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    /// Launch the official Archipelago Launcher so the user can open the Cave Story
    /// Client from it. ConnectsItself = true: that client owns the AP slot connection.
    /// We do NOT hold our own ApClient on this slot.
    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        _ = session; // The Cave Story Client handles AP connection; no prefill mechanism.
        LaunchArchipelagoLauncher();
        return Task.CompletedTask;
    }

    /// Standalone: launch Doukutsu.exe directly (no AP, just the game).
    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        string doukutsuExe = Path.Combine(PatcherRootDir, "pre_edited_cs", "freeware", "Doukutsu.exe");
        if (!File.Exists(doukutsuExe))
        {
            // Try Cave Story Tweaked instead.
            string tweakedExe = Path.Combine(PatcherRootDir, "pre_edited_cs", "tweaked", "CSTweaked.exe");
            if (File.Exists(tweakedExe))
            {
                LaunchExe(tweakedExe, Path.GetDirectoryName(tweakedExe)!);
                return Task.CompletedTask;
            }

            throw new FileNotFoundException(
                "Could not find Doukutsu.exe or CSTweaked.exe under pre_edited_cs. " +
                "Use Install to download the cave-story-randomizer patcher first, then " +
                "connect via the Cave Story Client in the Archipelago Launcher at least " +
                "once (it extracts the game binaries into pre_edited_cs).",
                "Doukutsu.exe");
        }

        LaunchExe(doukutsuExe, Path.GetDirectoryName(doukutsuExe)!);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (Cave Story Client owns the slot) ──────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD4, 0x4A, 0x2A));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var linkFg  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));
        var bgDark  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20));
        var border  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33));

        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Cave Story integration uses the kl3cks7r/CaveStoryArchipelago external " +
                "apworld and the cave-story-randomizer patcher. The AP connection is made " +
                "via the Cave Story Client inside the official Archipelago Launcher. Cave " +
                "Story+ (Steam) is NOT supported — this uses the free freeware version " +
                "(Doukutsu.exe) or Cave Story Tweaked (CSTweaked.exe).",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: apworld status ───────────────────────────────────────
        panel.Children.Add(MakeSectionHeader("APWORLD (cave_story.apworld)", muted));

        bool apInstalled = ApWorldIsInstalled();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = apInstalled
                ? "Installed: " + ApWorldInstalledPath
                : "Not found in AP custom_worlds. Use Install on the Play tab.",
            FontSize = 11, Foreground = apInstalled ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "AP custom_worlds folder: " + ApCustomWorldsDir,
            FontSize = 10, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: patcher / game_dir ───────────────────────────────────
        panel.Children.Add(MakeSectionHeader("CAVE STORY RANDOMIZER PATCHER (game_dir)", muted));

        bool patcherPresent = PatcherIsExtracted();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = patcherPresent
                ? "Patcher found: " + PatcherRootDir
                : "Patcher not found at " + PatcherRootDir +
                  ". Use Install to download it.",
            FontSize = 11, Foreground = patcherPresent ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // Copy game_dir path button
        var pathRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var pathBox = new System.Windows.Controls.TextBox
        {
            Text = PatcherRootDir, IsReadOnly = true, FontSize = 11,
            Background  = bgDark,
            Foreground  = fg,
            BorderBrush = border,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            ToolTip = "Enter this path as game_dir in the Archipelago Cave Story Client " +
                      "(or in hosts.yaml under cave_story_settings.game_dir). " +
                      "This is the folder that contains pre_edited_cs.",
        };
        var copyBtn = new System.Windows.Controls.Button
        {
            Content = "Copy path", Width = 90, Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = border,
        };
        copyBtn.Click += (_, _) =>
        {
            try { System.Windows.Clipboard.SetText(PatcherRootDir); } catch { }
        };
        System.Windows.Controls.DockPanel.SetDock(copyBtn, System.Windows.Controls.Dock.Right);
        pathRow.Children.Add(copyBtn);
        pathRow.Children.Add(pathBox);
        panel.Children.Add(pathRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Set this path as game_dir in the Cave Story Client (or in hosts.yaml " +
                   "under cave_story_settings.game_dir). This is the cave-story-randomizer " +
                   "root folder — it must contain the pre_edited_cs directory.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Archipelago Launcher status ──────────────────────────
        panel.Children.Add(MakeSectionHeader("ARCHIPELAGO LAUNCHER", muted));

        string? apLauncherPath = FindArchipelagoLauncher();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = apLauncherPath != null
                ? "Found: " + apLauncherPath
                : "Not found. Download and install the official Archipelago Launcher " +
                  "(link below) to use the Cave Story Client.",
            FontSize = 11,
            Foreground = apLauncherPath != null ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(MakeSectionHeader("GUIDED SETUP", muted));
        foreach (string step in new[]
        {
            "1. Cave Story is FREE. Download the original freeware (Doukutsu.exe) from " +
                "cavestory.org (link below). You do NOT need Cave Story+ (Steam) — the AP " +
                "integration uses the freeware version.",

            "2. Use the Install button on the Play tab. This downloads cave_story.apworld " +
                "into your AP custom_worlds folder and downloads the cave-story-randomizer " +
                "patcher into the path shown above.",

            "3. Install the official Archipelago Launcher from archipelago.gg (link below) " +
                "if you have not already. The Cave Story Client lives inside it.",

            "4. Set game_dir: In the Archipelago Launcher, open hosts.yaml (or it may be " +
                "prompted by the Cave Story Client itself). Set cave_story_settings.game_dir " +
                "to the path shown in the CAVE STORY RANDOMIZER PATCHER section above " +
                "(use the Copy Path button). This is the folder that contains pre_edited_cs.",

            "5. Generate a Cave Story game via the Archipelago website or local AP install, " +
                "using the cave_story.apworld. Host the resulting multiworld on an AP server.",

            "6. In the Archipelago Launcher, open the Cave Story Client. Enter your AP " +
                "server address, slot name, and password. The client patches the game files " +
                "and then launches Cave Story. Play!",

            "7. Optional: choose Cave Story Tweaked (CSTweaked.exe) in the Cave Story " +
                "Client for widescreen, 60 fps, QoL options, and Linux support.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Section: Links ────────────────────────────────────────────────
        panel.Children.Add(MakeSectionHeader("LINKS", muted));

        foreach (var (label, url) in new[]
        {
            ("Cave Story Freeware Download (cavestory.org) ↗",     CaveStoryFreewareUrl),
            ("Cave Story Archipelago apworld (GitHub) ↗",          ApWorldGitHubUrl),
            ("cave-story-randomizer patcher (GitHub) ↗",           PatcherGitHubUrl),
            ("Cave Story AP Setup Guide ↗",                        SetupGuideUrl),
            ("Archipelago Launcher Download ↗",                    ArchipelagoDownloadUrl),
            ("Archipelago Official ↗",                             ArchipelagoSiteUrl),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
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
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); }
                catch { }
            };
            panel.Children.Add(btn);
        }

        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        // News = apworld releases (most AP-relevant changes).
        try
        {
            string releasesUrl =
                $"https://api.github.com/repos/{ApWorldOwner}/{ApWorldRepo}/releases";
            string json = await _http.GetStringAsync(releasesUrl, ct);
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
                    Url:     el.TryGetProperty("html_url", out var hu) ? hu.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// Resolve the latest cave_story.apworld release: (version, download_url).
    /// Falls back to the pinned tag when the API is unreachable.
    private async Task<(string Version, string? Url)> ResolveLatestApWorldAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(ApWorldReleasesApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? tag = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString()) : null;

            if (tag != null && root.TryGetProperty("assets", out var assets)
                            && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name",                   out var n) ? n.GetString()  : null;
                    string? url  = a.TryGetProperty("browser_download_url",   out var u) ? u.GetString()  : null;
                    if (name != null && url != null &&
                        name.Equals(ApWorldAssetName, StringComparison.OrdinalIgnoreCase))
                        return (tag, url);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API down → pinned fallback */ }

        // Pinned fallback — direct download URL for the verified 0.6.7 release.
        string fallbackUrl =
            $"https://github.com/{ApWorldOwner}/{ApWorldRepo}/releases/download/" +
            $"{FallbackApWorldTag}/{ApWorldAssetName}";
        return (FallbackApWorldTag, fallbackUrl);
    }

    /// Resolve the latest cave-story-randomizer zipball: (version, zipball_url).
    /// The patcher project publishes no binary assets — we use the GitHub zipball.
    private async Task<(string Version, string? Url)> ResolveLatestPatcherAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(PatcherReleasesApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? tag = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString()) : null;
            string? zipball = root.TryGetProperty("zipball_url", out var z)
                ? z.GetString() : null;

            if (tag != null && zipball != null)
                return (tag, zipball);
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through to pinned */ }

        string fallback =
            $"https://api.github.com/repos/{PatcherOwner}/{PatcherRepo}/zipball/{FallbackPatchTag}";
        return (FallbackPatchTag, fallback);
    }

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    // ── Private helpers — download / install apworld ──────────────────────────

    private async Task DownloadApWorldAsync(
        string url, string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(ApCustomWorldsDir);
        string destPath = ApWorldInstalledPath;
        string tempPath = destPath + ".tmp";
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;
            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tempPath);
            var buf = new byte[65536];
            int read;
            while ((read = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, read), ct);
                downloaded += read;
                if (total > 0)
                {
                    int pct = (int)(5 + 45 * downloaded / total);
                    progress.Report((pct, $"Downloading cave_story.apworld... {downloaded / 1000} KB"));
                }
            }
            await dst.FlushAsync(ct);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }

        try { if (File.Exists(destPath)) File.Delete(destPath); } catch { }
        File.Move(tempPath, destPath);
        progress.Report((52, $"cave_story.apworld {version} installed into AP custom_worlds."));
    }

    // ── Private helpers — download / extract patcher ──────────────────────────

    private async Task DownloadAndExtractPatcherAsync(
        string zipballUrl, string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"cavestory-randomizer-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"cavestory-randomizer-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((56, $"Downloading cave-story-randomizer {version}..."));
            using (var response = await _http.GetAsync(
                zipballUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long total      = response.Content.Headers.ContentLength ?? -1;
                long downloaded = 0;
                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tempZip);
                var buf = new byte[65536];
                int read;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    downloaded += read;
                    if (total > 0)
                    {
                        int pct = (int)(56 + 30 * downloaded / total);
                        progress.Report((pct, $"Downloading patcher... {downloaded / 1000} KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((88, "Extracting the cave-story-randomizer patcher..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // GitHub zipballs wrap all contents in a single top-level folder
            // (e.g. cave-story-randomizer-cave-story-randomizer-<sha>/). Flatten it.
            string sourceRoot = tempExtract;
            string[] subdirs = Directory.GetDirectories(tempExtract);
            if (subdirs.Length == 1 && Directory.GetFiles(tempExtract).Length == 0)
                sourceRoot = subdirs[0];

            // Move to our sidecar PatcherRootDir (overwrite — it is safe to replace).
            if (Directory.Exists(PatcherRootDir))
            {
                try { Directory.Delete(PatcherRootDir, recursive: true); } catch { }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(PatcherRootDir)!);
            Directory.Move(sourceRoot, PatcherRootDir);

            progress.Report((95, $"cave-story-randomizer {version} extracted to {PatcherRootDir}."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); }          catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    // ── Private helpers — status checks ──────────────────────────────────────

    private bool ApWorldIsInstalled()
    {
        try { return File.Exists(ApWorldInstalledPath); }
        catch { return false; }
    }

    /// The patcher is considered "extracted" when the pre_edited_cs directory is
    /// present under the patcher root. (The cave-story-randomizer game binaries are
    /// placed there when the Cave Story Client patches for the first time, so on a
    /// fresh extract the folder may not exist yet — we check for the patcher root
    /// itself to signal "downloaded" and let the client populate pre_edited_cs.)
    private bool PatcherIsExtracted()
    {
        try { return Directory.Exists(PatcherRootDir); }
        catch { return false; }
    }

    // ── Private helpers — Archipelago Launcher detection ─────────────────────

    /// Attempt to find ArchipelagoLauncher.exe from the standard install locations.
    private static string? FindArchipelagoLauncher()
    {
        // Standard install paths for the Archipelago Launcher on Windows.
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Archipelago", "ArchipelagoLauncher.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Archipelago", "ArchipelagoLauncher.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Archipelago", "ArchipelagoLauncher.exe"),
        };
        foreach (string c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }

    /// Launch the Archipelago Launcher so the user can open the Cave Story Client.
    private void LaunchArchipelagoLauncher()
    {
        string? launcher = FindArchipelagoLauncher();
        if (launcher != null)
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = launcher,
                WorkingDirectory = Path.GetDirectoryName(launcher)!,
                UseShellExecute  = true,
            });
            if (proc != null)
            {
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
                catch { }
            }
            return;
        }

        // Launcher not found — open the download page so the user can get it.
        try
        {
            Process.Start(new ProcessStartInfo(ArchipelagoDownloadUrl) { UseShellExecute = true });
        }
        catch { }

        throw new FileNotFoundException(
            "Could not find ArchipelagoLauncher.exe. Install the official Archipelago " +
            "Launcher from archipelago.gg — the Cave Story Client runs inside it. " +
            "A browser tab has been opened for you.",
            "ArchipelagoLauncher.exe");
    }

    /// Helper: launch an executable and track the process.
    private void LaunchExe(string exePath, string workDir)
    {
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = workDir,
            UseShellExecute  = true,
        }) ?? throw new InvalidOperationException($"Failed to start {Path.GetFileName(exePath)}.");
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
        catch { }
    }

    // ── Private helpers — settings UI ─────────────────────────────────────────

    private static System.Windows.Controls.TextBlock MakeSectionHeader(
        string text,
        System.Windows.Media.Brush foreground)
        => new()
        {
            Text = text, FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = foreground,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        };

    // ── Private helpers — sidecar (version stamp) ─────────────────────────────

    private sealed class CsSettings
    {
        public string? ApWorldVersion { get; set; }
    }

    private CsSettings LoadSidecar()
    {
        try
        {
            if (File.Exists(SidecarPath))
            {
                string txt = File.ReadAllText(SidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<CsSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSidecar(CsSettings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SidecarPath)!);
            File.WriteAllText(SidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { }
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSidecar().ApWorldVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSidecar();
        s.ApWorldVersion = v;
        SaveSidecar(s);
    }
}
