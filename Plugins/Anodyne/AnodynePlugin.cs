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
// GlobalUsings.cs already aliases the colliding short names project-wide — so this
// file must NOT add any file-level `using X = System.Windows...;` alias (CS1537).

namespace LauncherV2.Plugins.Anodyne;

// ═══════════════════════════════════════════════════════════════════════════════
// AnodynePlugin — install / launch for "Anodyne" (Sean Han Tani / Analgesic
// Productions, 2013 — the "AnodyneSharp" C# port) played through the
// AnodyneArchipelagoClient mod by SephDB, which bundles an in-game Archipelago
// client. This is a NATIVE "ConnectsItself" integration — the game itself speaks
// to the AP server with no emulator and no launcher-held ApClient on the slot.
//
// The apworld is maintained separately at PixieCatSupreme/ArchipelagoAno, which
// releases anodyne.apworld alongside a Universal Tracker pack.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online) ───────────────────────
//
//   * THE AP WORLD game string is "Anodyne" (verified live 2026-06-14 against
//     PixieCatSupreme/ArchipelagoAno worlds/anodyne/__init__.py:
//     `class AnodyneLocation(Location): game = "Anodyne"` and
//     worlds/anodyne/archipelago.json: `"game": "Anodyne"`,
//     world_version: "0.6.3", minimum_ap_version: "0.6.4").
//     GameId here = "anodyne".
//
//   * ITCH.IO ONLY. The setup guide (PixieCatSupreme/ArchipelagoAno docs/setup_en.md,
//     verified 2026-06-14) states: "The Anodyne Archipelago Client currently only
//     supports the itch.io version of the game. The Steam version may be supported
//     in the future." The itch.io game is "AnodyneSharp" by PixieCatSupreme
//     (https://pixiecatsupreme.itch.io/anodyne-sharp). The executable is
//     AnodyneSharp.exe. Steam is NOT supported — there is no Steam AppID to use.
//
//   * THE MOD repo is SephDB/AnodyneArchipelagoClient (verified live 2026-06-14).
//     Latest release: v0.7.0, single asset "AnodyneArchipelago.zip" (~441 KB).
//     The mod targets AnodyneSharp (the C# port, based on MonoGame/OpenGL).
//
//   * INSTALLATION: unzip AnodyneArchipelago.zip into a folder called "Mods" next
//     to AnodyneSharp.exe (create the folder if absent). This is a standard
//     "MagicaCloth"-style mod drop — nothing more. The primary detection file is
//     AnodyneArchipelago.dll under the Mods folder.
//
//   * CONNECTION is entered IN-GAME (verified against the official setup guide and
//     mod README): launch the game, select "Archipelago" from the main menu, and
//     enter Server Address, Port, Name, and optionally Password on the in-game
//     connection screen. No command-line args, no external config file — the launcher
//     cannot pre-fill the connection details. The settings panel surfaces the session
//     host / slot / port for the user to type in. The mod remembers the last nine
//     unique connections.
//
//   * THE apworld (anodyne.apworld) comes from PixieCatSupreme/ArchipelagoAno
//     releases. The launcher's news feed shows those release notes.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the AnodyneSharp install by checking a persisted user-set folder
//      (no registry or Steam scan — itch.io installs have no standard registry
//      entry). The Settings panel provides a folder picker and stores the result
//      in this plugin's own sidecar (Games/ROMs/anodyne/anodyne_launcher.json).
//   2. INSTALL/UPDATE = download AnodyneArchipelago.zip from SephDB's latest
//      release and extract it into <AnodyneSharp>/Mods/ (creating the folder if
//      absent). Existing files are overwritten; siblings preserved. A version stamp
//      is saved in the sidecar.
//   3. LAUNCH = run AnodyneSharp.exe from the configured install. ConnectsItself =
//      true (the mod holds the AP slot). SupportsStandalone = true (the game runs
//      fine without AP — just don't choose "Archipelago" on the main menu).
//      No connection prefill (entered in-game), stated honestly.
//   4. NEWS = apworld releases from PixieCatSupreme/ArchipelagoAno (the authoritative
//      AP-world source for this game).
//
// ── DEFENSIVE NOTES ───────────────────────────────────────────────────────────
//   * "Installed" is judged by the presence of AnodyneArchipelago.dll under <install>/Mods/.
//   * No plaintext AP password is ever written to disk by this plugin — the
//     connection is entered in-game.
//   * All network calls are best-effort; failure degrades gracefully.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class AnodynePlugin : IGamePlugin
{
    // ── Constants — AnodyneArchipelagoClient (SephDB, verified 2026-06-14) ────
    private const string ClientOwner   = "SephDB";
    private const string ClientRepo    = "AnodyneArchipelagoClient";
    private const string ClientRepoUrl = $"https://github.com/{ClientOwner}/{ClientRepo}";
    private const string GH_Client_Latest =
        $"https://api.github.com/repos/{ClientOwner}/{ClientRepo}/releases/latest";
    private const string GH_Client_Releases =
        $"https://api.github.com/repos/{ClientOwner}/{ClientRepo}/releases";

    // ── Constants — apworld (PixieCatSupreme/ArchipelagoAno, verified 2026-06-14) ──
    private const string ApWorldOwner      = "PixieCatSupreme";
    private const string ApWorldRepo       = "ArchipelagoAno";
    private const string ApWorldRepoUrl    = $"https://github.com/{ApWorldOwner}/{ApWorldRepo}";
    private const string GH_ApWorld_Releases =
        $"https://api.github.com/repos/{ApWorldOwner}/{ApWorldRepo}/releases";

    // itch.io page — the ONLY supported distribution.
    private const string ItchIoUrl = "https://pixiecatsupreme.itch.io/anodyne-sharp";

    // Setup guide + info links.
    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Anodyne/setup_en";
    private const string GameInfoUrl     = "https://archipelago.gg/games/Anodyne/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    /// The AnodyneSharp executable name.
    private const string AnodyneExeName = "AnodyneSharp.exe";

    /// Primary DLL placed in <install>/Mods/ by the client mod (verified against
    /// the SephDB/AnodyneArchipelagoClient project structure).
    private const string ModPrimaryDll = "AnodyneArchipelago.dll";

    /// The Mods subfolder relative to AnodyneSharp.exe.
    private const string ModsFolderName = "Mods";

    /// Pinned fallback if GitHub API is unreachable. v0.7.0 verified 2026-06-14.
    private const string FallbackVersion = "0.7.0";
    private const string FallbackZipName = "AnodyneArchipelago.zip";
    private static readonly string FallbackZipUrl =
        $"https://github.com/{ClientOwner}/{ClientRepo}/releases/download/v{FallbackVersion}/{FallbackZipName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "anodyne";
    public string DisplayName => "Anodyne";
    public string Subtitle    => "Native PC · Archipelago mod (itch.io)";

    /// EXACT AP game string — verified against PixieCatSupreme/ArchipelagoAno
    /// worlds/anodyne/archipelago.json (`"game": "Anodyne"`, world_version 0.6.3)
    /// and worlds/anodyne/__init__.py (`game = "Anodyne"` on both
    /// AnodyneLocation and AnodyneItem).
    public string ApWorldName => "Anodyne";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "anodyne.png");

    /// Muted teal-grey that echoes the game's pixel palette.
    public string ThemeAccentColor => "#4E7A8C";

    public string[] GameBadges =>
        new[] { "Requires AnodyneSharp (itch.io)", "ConnectsItself", "itch.io only" };

    public string Description =>
        "Anodyne is a 2013 indie action-adventure by Sean Han Tani (Analgesic Productions), " +
        "reimagined as AnodyneSharp — a faithful C# port released on itch.io. The " +
        "AnodyneArchipelagoClient mod by SephDB adds in-game Archipelago support, so the " +
        "game connects to the multiworld server directly from its main menu (no emulator, " +
        "no bridge). Chests and collectibles across Anodyne's dream-like world are " +
        "shuffled into the multiworld. You need your own copy of AnodyneSharp from itch.io; " +
        "the launcher installs the mod into it and guides you through the rest. Connection " +
        "details (server, slot, password) are entered in-game by choosing \"Archipelago\" " +
        "on the main menu. NOTE: only the itch.io version is supported — the Steam version " +
        "of Anodyne is not yet compatible with this mod.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means AnodyneArchipelago.dll is present under <install>/Mods/.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ConnectsItself = true: the mod holds the AP slot. SupportsStandalone = true:
    // vanilla Anodyne plays fine — just don't choose Archipelago on the main menu.
    public bool ConnectsItself    => true;
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Working directory for downloads and staging. The actual mod is extracted
    /// INTO the AnodyneSharp/Mods/ folder. Exposed as GameDirectory.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Anodyne");

    /// Plugin sidecar path (per-brief: Games/ROMs/anodyne/anodyne_launcher.json).
    private string SidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SidecarPath
        => Path.Combine(SidecarDir, "anodyne_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events — inert (ConnectsItself = true) ─────────────────────
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
            InstalledVersion = FindInstalledModDll() != null
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch { InstalledVersion = null; }

        try
        {
            var (version, _) = await ResolveLatestClientAsync(ct);
            AvailableVersion = version;
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. We need an AnodyneSharp install to drop the mod into.
        progress.Report((2, "Locating your AnodyneSharp installation..."));
        string? anoDir = ResolveAnodyneDir();
        if (anoDir == null)
            throw new InvalidOperationException(
                "Could not find an AnodyneSharp installation. Open this game's Settings " +
                "and pick your AnodyneSharp folder (the one containing AnodyneSharp.exe). " +
                "AnodyneSharp is available from itch.io: " + ItchIoUrl + ". " +
                "Note: the Steam version of Anodyne is not supported by the AP mod.");

        // 1. Resolve the latest client release.
        progress.Report((6, "Checking the latest AnodyneArchipelagoClient release..."));
        var (version, zipUrl) = await ResolveLatestClientAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the AnodyneArchipelago.zip download on GitHub. " +
                "Check your internet connection, or download the mod manually from " +
                ClientRepoUrl + "/releases and unzip it into your " + ModsFolderName + " folder.");

        // 2. Download + extract into <AnodyneSharp>/Mods/.
        string modsDir = Path.Combine(anoDir, ModsFolderName);
        await DownloadAndExtractModAsync(zipUrl, version, modsDir, progress, ct);

        // 3. Stamp the version in the sidecar.
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool dllOk = FindInstalledModDll() != null;
        progress.Report((100,
            $"Installed AnodyneArchipelagoClient {version} into your Mods folder" +
            (dllOk ? "." : " (verify the files landed).") +
            " To play: launch AnodyneSharp, choose \"Archipelago\" on the main menu, " +
            "enter your Server Address, Port, Name (slot name), and optional Password, " +
            "then select Connect. The mod remembers your last nine connections. " +
            "This launcher cannot pre-fill the connection — it is entered in-game."));
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
        // HONEST: the AP connection for Anodyne is entered on the in-game main menu
        // ("Archipelago" option → server address / port / name / optional password).
        // There is no documented command-line or config-file prefill mechanism
        // (verified against the official setup guide and mod README). So launching
        // from this tile just starts the game; the user connects in-game.
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this slot.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartAnodyne();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartAnodyne();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        // No plaintext AP password is ever written to disk by this plugin —
        // the connection is entered in-game — so there is nothing to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (mod owns the connection, ConnectsItself = true) ────

    public Task ReceiveItemsAsync(
        ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;   // The mod receives items from AP directly.

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
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Anodyne (AnodyneSharp, the C# port) is your own game from itch.io with " +
                   "the AnodyneArchipelagoClient mod dropped into it. Only the itch.io " +
                   "version is supported — the Steam Anodyne is not yet compatible with " +
                   "this mod. The launcher can install the mod files, but you must own " +
                   "AnodyneSharp on itch.io. Connection details are entered in-game.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: install location ─────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "ANODYNE SHARP INSTALL (itch.io)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? anoDir      = ResolveAnodyneDir();
        string? overrideDir = LoadOverrideDir();
        string detectMsg = anoDir != null
            ? "Using selected folder: " + anoDir
            : "AnodyneSharp not found. Download it from itch.io (link below) then " +
              "use the folder picker to point this launcher at it.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = anoDir != null ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        string? modDll = FindInstalledModDll();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                    ? "AnodyneArchipelagoClient mod found: " + modDll
                    : "AnodyneArchipelagoClient mod not found in Mods folder yet. " +
                      "Use Install on the Play tab after pointing at your AnodyneSharp folder.",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? anoDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your AnodyneSharp install folder (the one containing AnodyneSharp.exe). " +
                          "itch.io installs go wherever you chose; use this picker to tell the launcher.",
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
                Title = "Select your AnodyneSharp install folder (contains AnodyneSharp.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? anoDir ?? "")
                                   ? (overrideDir ?? anoDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateAnodyneDir(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not an AnodyneSharp folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
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
            Text = "itch.io installs have no registry entry so the launcher cannot detect " +
                   "AnodyneSharp automatically. Use this picker once to set the path. " +
                   "The folder must contain AnodyneSharp.exe.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: connecting in-game ───────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Launch AnodyneSharp and choose \"Archipelago\" from the main menu. " +
                   "Enter your Server Address (e.g. archipelago.gg), Port (e.g. 38281), " +
                   "Name (your slot name), and Password if the server requires one. Then " +
                   "select Connect. The mod remembers your last nine unique connections, so " +
                   "you can switch between multiworlds easily. To continue an earlier game, " +
                   "re-enter the same connection details. This launcher cannot pre-fill the " +
                   "connection — it is entered in-game.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: guided setup steps ───────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Purchase and download AnodyneSharp from itch.io (link below). Extract it " +
                "to any folder on your PC.",
            "2. Click \"Select folder...\" above and pick the folder containing AnodyneSharp.exe.",
            "3. Click Install on the Play tab. The launcher will download " +
                "AnodyneArchipelago.zip from SephDB's releases and extract it into the " +
                "Mods subfolder next to AnodyneSharp.exe (creating it if absent).",
            "4. Launch AnodyneSharp. Choose \"Archipelago\" on the main menu.",
            "5. Enter your Server Address, Port, Name (slot name), and Password (if any). " +
                "Select Connect. The game now connects to the multiworld.",
            "6. To play vanilla Anodyne without AP, simply do not choose Archipelago — " +
                "the mod does not affect normal play.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("AnodyneSharp on itch.io (buy) ↗",                     ItchIoUrl),
            ("AnodyneArchipelagoClient (mod, GitHub) ↗",            ClientRepoUrl),
            ("ArchipelagoAno (apworld source, GitHub) ↗",           ApWorldRepoUrl),
            ("Anodyne Setup Guide (Archipelago) ↗",                  SetupGuideUrl),
            ("Anodyne Info (Archipelago) ↗",                         GameInfoUrl),
            ("Archipelago Official ↗",                               ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0), FontSize = 12,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = accent,
                Cursor = System.Windows.Input.Cursors.Hand,
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

    // ── News feed — apworld releases (PixieCatSupreme/ArchipelagoAno) ─────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_ApWorld_Releases, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var dp) && dp.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(dp.GetString(), out date);

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

    /// "v0.7.0" → "0.7.0"; trim leading 'v' only when followed by a digit.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest AnodyneArchipelagoClient release: version + zip URL.
    /// Prefers the "AnodyneArchipelago.zip" asset; falls back to the first .zip;
    /// pinned v0.7.0 direct URL when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestClientAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_Client_Latest, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;
                string? anyZip    = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;
                    anyZip ??= url;
                    if (preferred == null && lower.Contains("anodyne"))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — install detection ───────────────────────────────────

    /// The AnodyneSharp dir to use: user override wins, else null (no auto-detect —
    /// itch.io installs have no standard registry entry).
    private string? ResolveAnodyneDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeAnodyneDir(ov))
            return ov;
        return null;
    }

    /// A folder "looks like" AnodyneSharp if it contains AnodyneSharp.exe.
    private static bool LooksLikeAnodyneDir(string dir)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(dir) &&
                   Directory.Exists(dir) &&
                   File.Exists(Path.Combine(dir, AnodyneExeName));
        }
        catch { return false; }
    }

    /// Returns null if OK, else a short human-readable reason.
    private static string? ValidateAnodyneDir(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your AnodyneSharp install folder.";
        if (!File.Exists(Path.Combine(folder, AnodyneExeName)))
            return $"That folder does not contain {AnodyneExeName}. Pick the folder " +
                   "that contains AnodyneSharp.exe (wherever you extracted your itch.io download).";
        return null;
    }

    /// Find AnodyneArchipelago.dll under <install>/Mods/ (recursive, case-insensitive).
    private string? FindInstalledModDll()
    {
        try
        {
            string? dir = ResolveAnodyneDir();
            if (dir == null) return null;
            string modsDir = Path.Combine(dir, ModsFolderName);
            if (!Directory.Exists(modsDir)) return null;
            foreach (string dll in Directory.EnumerateFiles(modsDir, "*.dll", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dll).Equals(ModPrimaryDll, StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartAnodyne()
    {
        string? dir = ResolveAnodyneDir();
        string? exe = dir != null ? Path.Combine(dir, AnodyneExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = dir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start AnodyneSharp.");

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
            catch { /* non-fatal if Exited not available */ }
            return;
        }

        throw new FileNotFoundException(
            "Could not find AnodyneSharp.exe. Open this game's Settings and pick your " +
            "AnodyneSharp install folder. Download AnodyneSharp from " + ItchIoUrl + ".",
            AnodyneExeName);
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    /// Download AnodyneArchipelago.zip and extract it into <AnodyneSharp>/Mods/.
    /// The zip's contents are extracted directly into the Mods folder; any existing
    /// mod files are overwritten and siblings are preserved.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string modsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"anodyne-archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"anodyne-archipelago-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading AnodyneArchipelagoClient {version}..."));
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
                        int pct = (int)(10 + 60 * downloaded / total);
                        progress.Report((pct, $"Downloading... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Extracting into Mods folder..."));
            Directory.CreateDirectory(modsDir);
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // If the whole tree is wrapped in a single sub-folder, descend into it.
            string mergeRoot = tempExtract;
            {
                string[] subdirs = Directory.GetDirectories(mergeRoot);
                string[] files   = Directory.GetFiles(mergeRoot);
                if (subdirs.Length == 1 && files.Length == 0)
                {
                    // Check if sub-folder contains the DLL directly.
                    if (Directory.GetFiles(subdirs[0], "*.dll").Length > 0 ||
                        Directory.GetDirectories(subdirs[0]).Length > 0)
                    {
                        mergeRoot = subdirs[0];
                    }
                }
            }

            MergeDirectory(mergeRoot, modsDir);
            progress.Report((90, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Recursively copy a directory tree INTO an existing destination, overwriting
    /// individual files but preserving unrelated siblings (so other mods in Mods\
    /// are not disturbed).
    private static void MergeDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            try { File.Copy(file, dst, overwrite: true); }
            catch { /* locked file (game open?) — skip, user can retry with game closed */ }
        }
    }

    // ── Private helpers — sidecar (install override + version stamp) ──────────

    private sealed class AnodyneSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private AnodyneSettings LoadSidecar()
    {
        try
        {
            if (File.Exists(SidecarPath))
            {
                string txt = File.ReadAllText(SidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<AnodyneSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSidecar(AnodyneSettings s)
    {
        try
        {
            Directory.CreateDirectory(SidecarDir);
            File.WriteAllText(SidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    private string? LoadOverrideDir()
    {
        string? p = LoadSidecar().InstallOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }
    private void SaveOverrideDir(string p)
    {
        var s = LoadSidecar();
        s.InstallOverride = p;
        SaveSidecar(s);
    }
    private string? ReadStampedVersion()
    {
        string? v = LoadSidecar().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    private void WriteStampedVersion(string v)
    {
        var s = LoadSidecar();
        s.ModVersion = v;
        SaveSidecar(s);
    }
}
