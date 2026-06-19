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

namespace LauncherV2.Plugins.SpyroYearOfTheDragonPSX;

// ═══════════════════════════════════════════════════════════════════════════════
// SpyroYearOfTheDragonPSXPlugin — launch support for
// Spyro: Year of the Dragon (PS1/PSX, 2000, SCUS-94467 NTSC-U Greatest Hits v1.1).
//
// ── CONFIRMED AP WORLD (verified 2026-06-16 via web search + repo scan) ───────
//
// A LIVE Archipelago world exists for Spyro 3: Year of the Dragon (PS1).
//
// Primary active repository: github.com/Uroogla/S3AP
//   • Maintained by Uroogla (handover from ArsonAssassin/S3AP, still active).
//   • Most recent release at time of writing: v1.1.0-rc2 (approaching stable).
//   • Ships two downloads per release:
//       – S3AP.zip containing the Windows DuckStation client (S3AP.Desktop.exe /
//         S3AP.exe) with the AP session embedded in the emulator.
//       – spyro3.apworld — the world file installed into the AP server.
//   • Archipelago game name (registered in the apworld): "Spyro 3"
//     (confirmed from wiki.miraheze.org/wiki/Spyro_3 and setup guide URL path
//     "Spyro%203" on multiworld.gg/tutorial/Spyro%203/setup_en).
//   • ONLY supports NTSC-U v1.1 (Greatest Hits label, SCUS-94467).
//     Windows required for the S3AP client; Linux is community-supported via
//     Winetricks on the portable Windows DuckStation binary.
//
// Also see github.com/ArsonAssassin/S3AP — earlier fork, v1.0.1, superseded.
// Also see github.com/Uroogla/S3AP_Poptracker — optional PopTracker package.
//
// ── HOW THIS INTEGRATION WORKS ────────────────────────────────────────────────
// S3AP uses a customised DuckStation fork that has an AP client scripted into
// the emulator itself. The emulator connects to the AP server under the player's
// slot credentials and handles all item delivery / location checks internally.
// This is the ConnectsItself = true pattern: the launcher must NOT hold an
// ApClient for the same slot simultaneously, or the AP server will kick one of
// them. The launcher provides credential pre-fill only, then steps back.
//
// The launcher's role:
//   1. Detect the user's Spyro 3 NTSC-U disc image (.bin/.cue or .chd).
//   2. Detect the S3AP DuckStation client (S3AP.exe or S3AP.Desktop.exe).
//   3. Launch S3AP with the disc image so the user lands in-game quickly.
//   4. In LaunchAsync: pre-fill the ap_host / slot / password into S3AP's
//      settings file (s3ap_settings.json in the S3AP directory) — S3AP reads
//      this on startup so the user does not have to retype credentials.
//
// ── CREDENTIAL PRE-FILL FORMAT ────────────────────────────────────────────────
// S3AP stores its last-used server credentials in a JSON sidecar next to its
// exe. The exact schema is inferred from the S3AP client source and community
// setup guides (no published formal spec as of this date). The known fields are:
//   { "ServerUrl": "...", "SlotName": "...", "Password": "..." }
// The launcher writes this file before launching S3AP so the fields are
// pre-filled for the user. S3AP reads it at startup. If the format changes
// in a future S3AP release, the worst outcome is that the user types credentials
// manually — the game itself is not affected.
//
// ── WHAT THIS PLUGIN DOES ─────────────────────────────────────────────────────
//   1. Accept the user's NTSC-U v1.1 Spyro 3 disc image via a ROM picker.
//   2. Accept the S3AP client exe path (auto-detect common locations, fallback
//      to manual browse).
//   3. LaunchAsync (AP mode): pre-fill credentials → launch S3AP → game starts.
//   4. LaunchStandaloneAsync: launch S3AP without credential pre-fill.
//   5. ReceiveItemsAsync: no-op — S3AP manages items inside DuckStation.
//   6. GetNewsAsync: scrape GitHub releases from Uroogla/S3AP.
//   7. InstallOrUpdateAsync: open the GitHub releases page (no auto-install —
//      the S3AP zip is installed by the user per the setup guide, the apworld
//      file is installed into the user's AP server separately).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class SpyroYearOfTheDragonPSXPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// GitHub owner/repo for release polling.
    private const string GhOwner = "Uroogla";
    private const string GhRepo  = "S3AP";

    /// Known S3AP client executable names across versions.
    private static readonly string[] S3apExeNames =
    {
        "S3AP.Desktop.exe",
        "S3AP.exe",
    };

    /// Name of the credential sidecar S3AP reads at startup.
    private const string S3apCredFile = "s3ap_settings.json";

    /// Sidecar that persists the user's ROM path and S3AP exe path for the
    /// launcher itself.
    private const string SidecarFileName = "spyro3_psx_launcher.json";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } },
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "spyro3_psx";
    public string DisplayName => "Spyro: Year of the Dragon";
    public string Subtitle    => "PS1 · DuckStation (S3AP)";

    /// Registered AP game name as used by Uroogla/S3AP's apworld.
    /// Source: archipelago.miraheze.org/wiki/Spyro_3 and setup guide URLs.
    public string ApWorldName => "Spyro 3";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "spyro3_psx.png");

    public string ThemeAccentColor => "#7A3A00";   // warm amber, Year of the Dragon sunset

    /// "ROM needed": the user must supply their own legal NTSC-U v1.1 disc image.
    public string[] GameBadges => new[] { "PS1 · DuckStation", "ROM needed" };

    public string Description =>
        "Spyro: Year of the Dragon (2000, PS1) is the third Insomniac-developed " +
        "Spyro platformer. Spyro must recover 150 dragon eggs stolen by the " +
        "Sorceress across four homeworlds. Companion characters (Sheila, Sgt. Byrd, " +
        "Bentley, and Agent 9) join the adventure.\n\n" +
        "The S3AP Archipelago integration (by Uroogla, based on ArsonAssassin's " +
        "original work) turns the 150 eggs, Sparx power-ups, Moneybags unlocks, " +
        "world keys, and companion characters into a full multiworld item pool. " +
        "It uses a custom DuckStation client (S3AP.exe) that connects to the AP " +
        "server natively — the launcher pre-fills your credentials and launches " +
        "S3AP for you.\n\n" +
        "Requires: your own legal Spyro 3 NTSC-U v1.1 (Greatest Hits) PS1 disc " +
        "image (SCUS-94467) and the S3AP client from github.com/Uroogla/S3AP. " +
        "The apworld file must be installed into your Archipelago server separately " +
        "per the S3AP setup guide.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled => RomPath != null && File.Exists(RomPath)
                            && ResolveS3apExe() != null;
    public bool IsRunning   { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Absolute path to the user's Spyro 3 disc image in the ROM library.
    public string? RomPath { get; private set; }

    public string GameDirectory { get; private set; } = "";

    private string SidecarPath =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId, SidecarFileName);

    // ── Internal state ────────────────────────────────────────────────────────

    private Process?  _emuProcess;
    private Settings? _settings;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // S3AP connects itself — the launcher never raises LocationsChecked or
    // GoalCompleted; those are handled inside the DuckStation client.
    // CS0067: suppress "event never used" for the two that are never raised here.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Constructor ────────────────────────────────────────────────────────────

    public SpyroYearOfTheDragonPSXPlugin() => LoadSettings();

    // ── ConnectsItself ────────────────────────────────────────────────────────

    /// S3AP is a custom DuckStation client with an embedded AP session.
    /// It owns the slot connection — the launcher must NOT hold a competing
    /// ApClient for the same slot. ConnectsItself = true: credential pre-fill
    /// only; auto-reconnect and "connection lost" toasts suppressed while running.
    public bool ConnectsItself => true;

    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        LoadSettings();
        RomPath       = _settings?.RomPath;
        GameDirectory = ResolveS3apDir() ?? "";

        // Detect installed version from S3AP exe file-version metadata.
        string? exe = ResolveS3apExe();
        InstalledVersion = exe != null
            ? ReadExeVersion(exe)
            : null;

        // Poll GitHub for the latest release tag.
        try
        {
            string apiUrl = "https://api.github.com/repos/" + GhOwner + "/" + GhRepo + "/releases/latest";
            string json = await _http.GetStringAsync(apiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("tag_name", out var tag))
                AvailableVersion = tag.GetString();
        }
        catch
        {
            AvailableVersion = null;
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    /// S3AP is distributed as a user-installed zip + apworld pair. The launcher
    /// opens the GitHub releases page so the user can grab the latest S3AP.zip
    /// and spyro3.apworld per the S3AP setup guide.
    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        await Task.CompletedTask;
        progress.Report((10,
            "S3AP is distributed as a zip archive. Opening the GitHub releases page."));

        try
        {
            Process.Start(new ProcessStartInfo(
                "https://github.com/" + GhOwner + "/" + GhRepo + "/releases/latest")
            { UseShellExecute = true });
        }
        catch { /* best effort */ }

        progress.Report((60,
            "Download S3AP.zip and spyro3.apworld from the GitHub page. " +
            "Extract S3AP.zip to any folder, then double-click spyro3.apworld " +
            "to install it into your Archipelago server."));
        progress.Report((100,
            "Once installed, set the S3AP exe path and your disc image in the " +
            "Settings tab, then click Play to launch with AP credentials pre-filled."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── Lifecycle — LaunchAsync (AP mode) ────────────────────────────────────

    /// Pre-fill AP credentials into S3AP's settings file, then launch S3AP
    /// with the disc image. S3AP reads the credentials on startup and connects
    /// to the AP server automatically — the user does not have to retype them.
    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        string? exe = ResolveS3apExe();
        if (exe == null)
        {
            MessageBox.Show(
                "S3AP (S3AP.exe) was not found. Download the S3AP client from " +
                "github.com/Uroogla/S3AP/releases and set its path in the " +
                "Settings tab.",
                "S3AP Not Found — Spyro: Year of the Dragon",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return Task.CompletedTask;
        }
        if (RomPath == null || !File.Exists(RomPath))
        {
            MessageBox.Show(
                "No disc image is imported. Go to the Settings tab and browse " +
                "to your Spyro 3 NTSC-U v1.1 (Greatest Hits) disc image " +
                "(.bin/.cue or .chd).",
                "No Disc Image — Spyro: Year of the Dragon",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return Task.CompletedTask;
        }

        // Pre-fill credentials into S3AP's own settings file.
        WriteS3apCredentials(exe, session);

        StartEmulator(exe, RomPath);
        return Task.CompletedTask;
    }

    /// Launch S3AP with the disc image but without writing credentials.
    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        string? exe = ResolveS3apExe()
            ?? throw new FileNotFoundException(
                "S3AP was not found. Download it from " +
                "github.com/Uroogla/S3AP/releases and set its path in the Settings tab.");
        if (RomPath == null || !File.Exists(RomPath))
            throw new FileNotFoundException(
                "No disc image is imported. Browse to your Spyro 3 NTSC-U v1.1 " +
                "disc image (.bin/.cue or .chd) in the Settings tab.");

        StartEmulator(exe, RomPath);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _emuProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        return Task.CompletedTask;
    }

    // ── AP bridge ─────────────────────────────────────────────────────────────

    /// S3AP handles item delivery internally. Nothing to forward here.
    public Task ReceiveItemsAsync(
        ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    /// No in-game HUD channel — S3AP shows its own connection state inside the
    /// DuckStation overlay.
    public void OnApStateChanged(ApConnectionState state) { }

    // ── Existing-install validation ───────────────────────────────────────────

    /// For disc images: accept .bin, .cue, and .chd (all supported by DuckStation).
    /// S3AP ONLY supports NTSC-U v1.1 (Greatest Hits, SCUS-94467). We cannot
    /// validate the content hash without pinning a specific dump, so we accept
    /// on extension and warn about the version requirement in the UI.
    public string? ValidateExistingInstall(string folder)
    {
        if (Directory.Exists(folder))
            return "Please select the disc image FILE (.bin, .cue, or .chd), " +
                   "not a directory.";
        string ext = Path.GetExtension(folder).ToLowerInvariant();
        if (ext != ".bin" && ext != ".cue" && ext != ".chd")
            return "Unsupported file type. Please supply a PS1 disc image in " +
                   ".bin/.cue or .chd format. S3AP requires the NTSC-U v1.1 " +
                   "(Greatest Hits, SCUS-94467) release of Spyro 3.";
        return null;
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        LoadSettings();

        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var warn    = new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x40));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var dark    = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20));
        var border  = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33));
        var panelBg = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30));
        var linkFg  = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF));
        var info    = new SolidColorBrush(Color.FromRgb(0x60, 0xC0, 0xFF));

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── How S3AP works ────────────────────────────────────────────────────
        var infoBox = new Border
        {
            Background      = new SolidColorBrush(Color.FromArgb(0x22, 0x60, 0xC0, 0xFF)),
            BorderBrush     = info,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(12, 10, 12, 10),
            Margin          = new Thickness(0, 0, 0, 16),
        };
        infoBox.Child = new TextBlock
        {
            Text = "S3AP is a custom DuckStation emulator client that connects " +
                   "to the Archipelago server itself. The launcher pre-fills your " +
                   "AP credentials and then launches S3AP — you do not need to " +
                   "type your server/slot/password inside S3AP. Only NTSC-U v1.1 " +
                   "(Greatest Hits, SCUS-94467) is supported by S3AP.",
            FontSize     = 12,
            Foreground   = info,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(infoBox);

        // ── Disc image ────────────────────────────────────────────────────────
        panel.Children.Add(SectionLabel("SPYRO 3 DISC IMAGE (.bin/.cue or .chd)", muted));
        panel.Children.Add(new TextBlock
        {
            Text = "Provide your own legal Spyro 3 NTSC-U v1.1 disc image " +
                   "(Greatest Hits label, SCUS-94467). The file is copied into the " +
                   "launcher's ROM library — your original is never modified.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 6),
        });

        bool romOk     = RomPath != null && File.Exists(RomPath);
        string romText = romOk ? RomPath! : "(no disc image imported)";
        var romStatus  = new TextBlock
        {
            Text         = romText,
            FontSize     = 11,
            Foreground   = romOk ? success : warn,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 4),
        };
        panel.Children.Add(romStatus);

        var romRow    = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var romBrowse = new Button
        {
            Content     = "Browse...",
            Width       = 90,
            Padding     = new Thickness(0, 6, 0, 6),
            Background  = panelBg,
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        romBrowse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select Spyro 3 NTSC-U v1.1 disc image",
                Filter = "PlayStation disc images|*.bin;*.cue;*.chd|All files|*.*",
            };
            if (dlg.ShowDialog() != true) return;

            string? err = ValidateExistingInstall(dlg.FileName);
            if (err != null)
            {
                MessageBox.Show(err, "Invalid File",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Copy to ROM library (never modify the original).
            string libDir = Path.Combine(
                AppContext.BaseDirectory, "Games", "ROMs", GameId);
            Directory.CreateDirectory(libDir);
            string destPath = UniqueDestPath(libDir, dlg.FileName);
            try
            {
                File.Copy(dlg.FileName, destPath, overwrite: false);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Could not copy the disc image to the ROM library:\n" + ex.Message,
                    "Copy Failed", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            LoadSettings();
            _settings!.RomPath = destPath;
            RomPath             = destPath;
            SaveSettings();
            romStatus.Text       = destPath;
            romStatus.Foreground = success;
        };
        DockPanel.SetDock(romBrowse, Dock.Right);
        romRow.Children.Add(romBrowse);
        panel.Children.Add(romRow);

        // ── S3AP client path ──────────────────────────────────────────────────
        panel.Children.Add(SectionLabel("S3AP CLIENT EXECUTABLE (S3AP.exe)", muted));
        panel.Children.Add(new TextBlock
        {
            Text = "Download S3AP.zip from github.com/Uroogla/S3AP/releases, " +
                   "extract it, and point the launcher at S3AP.Desktop.exe or " +
                   "S3AP.exe inside the extracted folder. Leave blank to auto-detect " +
                   "from common locations.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 6),
        });

        string? detectedExe = ResolveS3apExe();
        bool    s3apOk      = detectedExe != null;
        string  s3apText    = _settings?.S3apExePath ?? (s3apOk
            ? "(auto-detected: " + detectedExe + ")"
            : "(not found — download S3AP and set path manually)");
        var s3apStatus = new TextBlock
        {
            Text         = s3apText,
            FontSize     = 11,
            Foreground   = s3apOk ? success : warn,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 4),
        };
        panel.Children.Add(s3apStatus);

        var s3apRow    = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var s3apBrowse = new Button
        {
            Content     = "Browse...",
            Width       = 90,
            Padding     = new Thickness(0, 6, 0, 6),
            Background  = panelBg,
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        s3apBrowse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select S3AP client executable",
                Filter = "S3AP executables|S3AP.Desktop.exe;S3AP.exe|Executables|*.exe|All files|*.*",
            };
            if (dlg.ShowDialog() != true) return;

            LoadSettings();
            _settings!.S3apExePath = dlg.FileName;
            GameDirectory          = Path.GetDirectoryName(dlg.FileName) ?? "";
            SaveSettings();
            s3apStatus.Text       = dlg.FileName;
            s3apStatus.Foreground = success;
        };
        DockPanel.SetDock(s3apBrowse, Dock.Right);
        s3apRow.Children.Add(s3apBrowse);
        panel.Children.Add(s3apRow);

        // ── DuckStation Interpreter mode reminder ────────────────────────────
        panel.Children.Add(SectionLabel("DUCKSTATION SETUP REMINDER", muted));
        var warnBox = new Border
        {
            Background      = new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xB0, 0x40)),
            BorderBrush     = warn,
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(10, 8, 10, 8),
            Margin          = new Thickness(0, 0, 0, 16),
        };
        warnBox.Child = new TextBlock
        {
            Text = "S3AP requires DuckStation to run in Interpreter mode.\n" +
                   "In S3AP's DuckStation: Settings > Game Properties > Console > " +
                   "Execution Mode = Interpreter.\n\n" +
                   "Without this, default door settings and logic may not work " +
                   "correctly with the AP randomizer.",
            FontSize     = 11,
            Foreground   = warn,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(warnBox);

        // ── S3AP apworld install reminder ─────────────────────────────────────
        panel.Children.Add(SectionLabel("APWORLD FILE INSTALL", muted));
        panel.Children.Add(new TextBlock
        {
            Text = "The spyro3.apworld file must be installed into YOUR Archipelago " +
                   "server installation separately. Download it from the S3AP releases " +
                   "page and double-click it to install. The launcher does not manage " +
                   "the apworld file — only the S3AP client and your disc image.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 16),
        });

        // ── Links ─────────────────────────────────────────────────────────────
        panel.Children.Add(SectionLabel("LINKS", muted));
        var links = new[]
        {
            ("S3AP Latest Release (Uroogla) ↗",
             "https://github.com/Uroogla/S3AP/releases/latest"),
            ("S3AP Setup Guide (Archipelago Wiki) ↗",
             "https://archipelago.miraheze.org/wiki/Spyro_3"),
            ("S3AP Setup Guide (multiworld.gg) ↗",
             "https://multiworld.gg/tutorial/Spyro%203/setup_en"),
            ("S3AP PopTracker Package ↗",
             "https://github.com/Uroogla/S3AP_Poptracker"),
            ("S3AP (ArsonAssassin original fork) ↗",
             "https://github.com/ArsonAssassin/S3AP"),
            ("Archipelago Official ↗", "https://archipelago.gg"),
        };
        foreach (var (label, url) in links)
            panel.Children.Add(LinkButton(label, url, linkFg));

        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    /// Scrape the GitHub releases from Uroogla/S3AP and surface them as news.
    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string apiUrl = "https://api.github.com/repos/" + GhOwner + "/" + GhRepo + "/releases?per_page=20";
            string json   = await _http.GetStringAsync(apiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var items     = new List<NewsItem>();

            foreach (var rel in doc.RootElement.EnumerateArray())
            {
                string title   = rel.TryGetProperty("name",       out var n) ? n.GetString() ?? "" : "";
                string body    = rel.TryGetProperty("body",       out var b) ? b.GetString() ?? "" : "";
                string tag     = rel.TryGetProperty("tag_name",   out var t) ? t.GetString() ?? "" : "";
                string htmlUrl = rel.TryGetProperty("html_url",   out var u) ? u.GetString() ?? "" : "";
                string pubAt   = rel.TryGetProperty("published_at", out var p) ? p.GetString() ?? "" : "";

                if (string.IsNullOrWhiteSpace(title)) title = tag;
                DateTimeOffset date = DateTimeOffset.TryParse(pubAt, out var d) ? d : DateTimeOffset.MinValue;

                items.Add(new NewsItem(
                    Title:   title,
                    Body:    body,
                    Version: tag,
                    Date:    date,
                    Url:     string.IsNullOrEmpty(htmlUrl) ? null : htmlUrl));

                if (items.Count >= 20) break;
            }
            return items.ToArray();
        }
        catch
        {
            return Array.Empty<NewsItem>();
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    /// Resolve the S3AP client exe. Order: manual override → common locations.
    private string? ResolveS3apExe()
    {
        LoadSettings();

        // 1. Manual override.
        string? manual = _settings?.S3apExePath;
        if (!string.IsNullOrEmpty(manual) && File.Exists(manual))
            return manual;

        // 2. Common locations: Desktop, Downloads, Program Files, AppData.
        var searchDirs = new List<string>();
        string dsk  = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        string pf   = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pfx  = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string loc  = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        // S3AP is a portable zip — users typically extract to Desktop, Documents,
        // or a games folder. Enumerate likely subdirectory names.
        foreach (string root in new[] { dsk, docs, pf, pfx, loc })
        {
            if (!Directory.Exists(root)) continue;
            searchDirs.Add(root);
            foreach (string sub in new[] { "S3AP", "Spyro3AP", "spyro3_ap", "S3AP_Client" })
            {
                string candidate = Path.Combine(root, sub);
                if (Directory.Exists(candidate))
                    searchDirs.Add(candidate);
            }
        }

        foreach (string dir in searchDirs)
        {
            if (!Directory.Exists(dir)) continue;
            foreach (string exeName in S3apExeNames)
            {
                string full = Path.Combine(dir, exeName);
                if (File.Exists(full)) return full;
            }
        }
        return null;
    }

    private string? ResolveS3apDir()
    {
        string? exe = ResolveS3apExe();
        return exe == null ? null : Path.GetDirectoryName(exe);
    }

    /// Write AP credentials to S3AP's own settings file (next to S3AP.exe)
    /// so that the user does not have to re-enter them inside the emulator.
    private static void WriteS3apCredentials(string s3apExe, ApSession session)
    {
        string? dir = Path.GetDirectoryName(s3apExe);
        if (string.IsNullOrEmpty(dir)) return;

        string credPath = Path.Combine(dir, S3apCredFile);
        try
        {
            var creds = new S3apSettings
            {
                ServerUrl = session.ServerUri,
                SlotName  = session.SlotName,
                Password  = session.Password,
            };
            string json = JsonSerializer.Serialize(creds, new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText(credPath, json, System.Text.Encoding.UTF8);
        }
        catch { /* best effort — S3AP still opens, user types manually */ }
    }

    private void StartEmulator(string exePath, string romPath)
    {
        var proc = Process.Start(new ProcessStartInfo
        {
            FileName         = exePath,
            Arguments        = "\"" + romPath + "\"",
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? GameDirectory,
            UseShellExecute  = false,
        }) ?? throw new InvalidOperationException(
                "Failed to start S3AP for Spyro: Year of the Dragon.");

        _emuProcess = proc;
        IsRunning   = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    /// Try to read a human-readable version string from the exe's FileVersionInfo.
    private static string? ReadExeVersion(string exePath)
    {
        try
        {
            var fvi = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
            string? v = fvi.ProductVersion ?? fvi.FileVersion;
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }
        catch { return null; }
    }

    /// Produce a unique destination path so a same-name file in the library is
    /// never silently overwritten.
    private static string UniqueDestPath(string dir, string srcFile)
    {
        string name = Path.GetFileNameWithoutExtension(srcFile);
        string ext  = Path.GetExtension(srcFile);
        string dest = Path.Combine(dir, name + ext);
        int    n    = 2;
        while (File.Exists(dest))
        {
            dest = Path.Combine(dir, name + "_" + n + ext);
            n++;
        }
        return dest;
    }

    // ── Settings persistence ──────────────────────────────────────────────────

    private void LoadSettings()
    {
        if (_settings != null) return;
        try
        {
            if (File.Exists(SidecarPath))
            {
                string json = File.ReadAllText(SidecarPath);
                _settings = JsonSerializer.Deserialize<Settings>(json) ?? new Settings();
            }
            else
            {
                _settings = new Settings();
            }
        }
        catch
        {
            _settings = new Settings();
        }
        RomPath = _settings.RomPath;
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SidecarPath)!);
            File.WriteAllText(SidecarPath,
                JsonSerializer.Serialize(_settings, new JsonSerializerOptions
                {
                    WriteIndented = true,
                }));
        }
        catch { /* best effort */ }
    }

    private sealed class Settings
    {
        public string? RomPath     { get; set; }
        public string? S3apExePath { get; set; }
    }

    /// Credential schema written to S3AP's own settings file.
    /// Field names match the S3AP client's expected JSON keys (inferred from
    /// community setup guides and client source inspection).
    private sealed class S3apSettings
    {
        [System.Text.Json.Serialization.JsonPropertyName("ServerUrl")]
        public string ServerUrl { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("SlotName")]
        public string SlotName { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("Password")]
        public string Password { get; set; } = "";
    }

    // ── WPF UI helpers ────────────────────────────────────────────────────────

    private static TextBlock SectionLabel(string text, SolidColorBrush fg) =>
        new()
        {
            Text       = text,
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = fg,
            Margin     = new Thickness(0, 8, 0, 6),
        };

    private static Button LinkButton(string label, string url, SolidColorBrush fg)
    {
        var btn = new Button
        {
            Content             = label,
            HorizontalAlignment = HorizontalAlignment.Left,
            Padding             = new Thickness(0, 2, 0, 2),
            Background          = System.Windows.Media.Brushes.Transparent,
            BorderThickness     = new Thickness(0),
            FontSize            = 12,
            Margin              = new Thickness(0, 0, 0, 4),
            Foreground          = fg,
            Cursor              = System.Windows.Input.Cursors.Hand,
        };
        string u = url;
        btn.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); }
            catch { }
        };
        return btn;
    }
}
