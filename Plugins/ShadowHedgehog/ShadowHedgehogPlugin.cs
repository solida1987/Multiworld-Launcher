using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LauncherV2.Core;

// NOTE: WPF UI types are FULLY QUALIFIED throughout this file (System.Windows.*).
// The real launcher project sets <UseWindowsForms>true</UseWindowsForms> alongside
// WPF, so the bare names Button / TextBox / Color / Brushes / MessageBox /
// FontWeights / Orientation collide between System.Windows.Forms and
// System.Windows[.Controls/.Media] (CS0104). Qualifying every UI type avoids that.
// We also do NOT add any file-level `using X = System.Windows...;` aliases — the
// project's GlobalUsings.cs already aliases the short names, and a second local
// alias would be CS1537 (duplicate alias). Bare names or fully-qualified only.
using System.Windows;

namespace LauncherV2.Plugins.ShadowHedgehog;

// ═══════════════════════════════════════════════════════════════════════════════
// ShadowHedgehogPlugin — install / launch for "Shadow The Hedgehog" (SEGA /
// Sonic Team, GameCube 2005, NTSC-U), played through the Archipelago randomiser
// maintained by choatix at:
//   https://github.com/choatix/Archipelago  (branch: shadow-cp)
//
// ── HOW THIS INTEGRATION WORKS ───────────────────────────────────────────────
// This is a DOLPHIN-EMULATOR integration. The base game is the user's own legal
// NTSC-U GameCube ISO (game IDs: GUPE8P / GUPR8P / GUPX8P). There is no Steam
// release — the launcher cannot install or manage Dolphin or the ISO on the
// user's behalf. What the launcher CAN do:
//
//   1. Download the latest shadow_the_hedgehog.apworld from choatix/Archipelago
//      releases and place it in the correct Archipelago "custom_worlds" folder
//      so the Archipelago launcher can generate the game.
//
//   2. Pre-launch Dolphin with the user-specified ISO path so the user does not
//      have to locate it manually every session.
//
//   3. Surface all the setup steps clearly in the Settings panel.
//
// ── CONNECTIVITY: ConnectsItself = true ──────────────────────────────────────
// The randomiser ships a dedicated "Shadow The Hedgehog Client" inside
// choatix's Archipelago fork. That client connects to the AP server directly
// via dolphin_memory_engine (live GameCube RAM). This is NOT a launcher-held
// ApClient slot — the Archipelago launcher holds the connection while Dolphin
// runs. The launcher must NOT open a competing ApClient on the same slot;
// ConnectsItself = true enforces that contract.
//
// ── WHAT THE USER NEEDS ──────────────────────────────────────────────────────
//   1. Dolphin Emulator (2409+), with "Emulated Memory Size Override" disabled.
//   2. An NTSC-U Shadow The Hedgehog GameCube ISO (GUPE8P or variants above).
//   3. choatix's Archipelago fork (or the stock AP launcher + our apworld file).
//   4. The shadow_the_hedgehog.apworld file installed in AP's custom_worlds dir.
//
// ── INSTALL / UPDATE ──────────────────────────────────────────────────────────
// InstallOrUpdateAsync downloads the apworld from choatix/Archipelago releases
// (asset: shadow_the_hedgehog.apworld) and writes it to the user-configured
// Archipelago "custom_worlds" folder. It also records the installed version tag
// in a local stamp file so subsequent checks detect up-to-date state.
//
// ── LAUNCH ────────────────────────────────────────────────────────────────────
// LaunchAsync pre-fills nothing to the Archipelago client (the user fills AP
// credentials in the "Shadow The Hedgehog Client" from choatix's AP launcher).
// If the user has configured a Dolphin exe path and ISO path, the plugin
// launches Dolphin with the ISO so both programs open together. Otherwise it
// opens the Dolphin install folder in Explorer so the user can start it.
//
// ── RELEASES (VERIFIED 2026-06-16) ──────────────────────────────────────────
// Latest: shadow-0.4.8 — asset "shadow_the_hedgehog.apworld"
// Repo:   https://github.com/choatix/Archipelago/releases  (tag prefix: shadow-)
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ShadowHedgehogPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER       = "choatix";
    private const string GITHUB_REPO        = "Archipelago";
    private const string RELEASE_TAG_PREFIX = "shadow-";
    private const string APWORLD_ASSET_NAME = "shadow_the_hedgehog.apworld";
    private const string FallbackVersion    = "0.4.8";

    // GitHub API endpoints.
    private static readonly string GH_RELEASES_URL =
        "https://api.github.com/repos/choatix/Archipelago/releases";

    // Version stamp filename kept alongside the installed apworld.
    private const string VersionStampFileName = "shadow_the_hedgehog_ap_version.dat";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(15),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } },
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId        => "shadow_the_hedgehog";
    public string DisplayName   => "Shadow The Hedgehog";
    public string GameDirectory => string.IsNullOrEmpty(IsoPath) ? string.Empty : Path.GetDirectoryName(IsoPath) ?? string.Empty;
    public string Subtitle    => "GameCube · Dolphin · Archipelago";
    public string ApWorldName => "Shadow The Hedgehog";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "shadow_the_hedgehog.png");

    public string ThemeAccentColor => "#2A0A2E";   // dark purple, Shadow's aesthetic
    public string[] GameBadges     => new[] { "GameCube", "Dolphin", "ConnectsItself" };

    public string Description =>
        "Shadow The Hedgehog (2005, GameCube) Archipelago randomizer by choatix. " +
        "Play through 23 stages as the Ultimate Lifeform, with stage paths, weapons, " +
        "vehicles, and abilities shuffled across the multiworld. Requires a legal " +
        "NTSC-U GameCube ISO, Dolphin Emulator (2409+), and choatix's Archipelago " +
        "fork (or the stock Archipelago launcher with the apworld installed). The " +
        "launcher downloads and installs the apworld file automatically.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => DetectApWorldInstalled();
    public bool    IsRunning        { get; private set; }

    // ── Plugin flags ─────────────────────────────────────────────────────────
    // ConnectsItself: the "Shadow The Hedgehog Client" in choatix's AP fork
    // owns the slot connection. The launcher must NOT hold a competing ApClient.
    public bool ConnectsItself  => true;
    public bool SupportsStandalone => true;   // user can just open Dolphin directly
    public string? BuiltAgainstDataPackageChecksum => null;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// The Archipelago "custom_worlds" folder where the apworld is installed.
    /// The user can override this in Settings; it defaults to the standard
    /// location relative to a default AP install.
    public string ApWorldDirectory { get; set; }
        = DefaultApWorldDirectory();

    /// Full path to the installed apworld file.
    private string ApWorldFilePath =>
        Path.Combine(ApWorldDirectory, APWORLD_ASSET_NAME);

    /// Version stamp next to the apworld.
    private string VersionStampPath =>
        Path.Combine(ApWorldDirectory, VersionStampFileName);

    /// Path to the Dolphin executable (set by the user in Settings).
    public string DolphinExePath  { get; set; } = "";

    /// Path to the Shadow The Hedgehog NTSC-U ISO (set by the user in Settings).
    public string IsoPath         { get; set; } = "";

    /// Plugin settings sidecar — one JSON file, plugin-private.
    private string SettingsSidecarPath =>
        Path.Combine(AppContext.BaseDirectory, "Games", "Config", "shadow_the_hedgehog_settings.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _dolphinProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The AP client in choatix's fork owns the slot — nothing is forwarded here.
    // All three events are grouped under a single #pragma to satisfy CS0067.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
    public event Action<int>?    GameExited;
#pragma warning restore CS0067

    // ── Constructor ───────────────────────────────────────────────────────────

    public ShadowHedgehogPlugin()
    {
        LoadSettings();
    }

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        // Read installed version stamp.
        try
        {
            InstalledVersion = File.Exists(VersionStampPath) && DetectApWorldInstalled()
                ? (await File.ReadAllTextAsync(VersionStampPath, ct)).Trim()
                : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        // Fetch latest release tag from GitHub.
        try
        {
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(GITHUB_OWNER, GITHUB_REPO, ct));
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
        progress.Report((2, "Checking latest Shadow The Hedgehog Archipelago release..."));

        var (version, apworldUrl, _) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        // Already current?
        if (DetectApWorldInstalled()
            && File.Exists(VersionStampPath)
            && (await File.ReadAllTextAsync(VersionStampPath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100,
                "Shadow The Hedgehog Archipelago " + version + " is already up to date."));
            return;
        }

        if (apworldUrl == null)
            throw new InvalidOperationException(
                "Could not find shadow_the_hedgehog.apworld on the GitHub release page. " +
                "Check your internet connection or download manually from " +
                "https://github.com/choatix/Archipelago/releases");

        // Download apworld.
        progress.Report((5, "Downloading shadow_the_hedgehog.apworld " + version + "..."));
        Directory.CreateDirectory(ApWorldDirectory);

        string tempPath = Path.Combine(Path.GetTempPath(),
            "shadow_the_hedgehog_" + Guid.NewGuid().ToString("N") + ".apworld");
        try
        {
            using var response = await _http.GetAsync(
                apworldUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempPath))
            {
                var buf = new byte[65536];
                int bytesRead;
                while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                    downloaded += bytesRead;
                    if (total > 0)
                    {
                        int pct = (int)(5 + 80 * downloaded / total);
                        progress.Report((pct,
                            "Downloading... " + (downloaded / 1024) + " KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            // Move into place (overwrite any existing apworld).
            progress.Report((88, "Installing apworld..."));
            File.Move(tempPath, ApWorldFilePath, overwrite: true);
            tempPath = "";   // moved — don't delete

            // Write version stamp.
            await File.WriteAllTextAsync(VersionStampPath, version, ct);
            InstalledVersion = version;

            progress.Report((100,
                "shadow_the_hedgehog.apworld " + version + " installed to: " +
                ApWorldDirectory + ". " +
                "Open the Archipelago launcher and generate your game, then launch " +
                "'Shadow The Hedgehog Client' from the AP launcher while Dolphin runs."));
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return DetectApWorldInstalled();
    }

    // ── Lifecycle — Launch (AP) ───────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // ConnectsItself = true: the user connects via the "Shadow The Hedgehog
        // Client" in choatix's Archipelago fork. We just open Dolphin with the
        // ISO so everything is running when they switch to the AP launcher.
        StartDolphin();
        return Task.CompletedTask;
    }

    // ── Lifecycle — Launch (standalone) ──────────────────────────────────────

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartDolphin();
        return Task.CompletedTask;
    }

    // ── Lifecycle — Stop ──────────────────────────────────────────────────────

    public Task StopAsync()
    {
        try { _dolphinProcess?.Kill(entireProcessTree: true); }
        catch { }
        IsRunning = false;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index,
                                  CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var accent  = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x8A, 0x2B, 0xE2));
        var success = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(
                          System.Windows.Media.Color.FromRgb(0xFF, 0xB3, 0x00));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20),
        };

        // ── Section: apworld directory ────────────────────────────────────
        panel.Children.Add(MakeSectionHeader("APWORLD DIRECTORY", muted));

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "The launcher will install shadow_the_hedgehog.apworld here. " +
                "This should be the 'custom_worlds' subfolder inside your Archipelago " +
                "installation (e.g. C:\\Archipelago\\custom_worlds\\).",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var apworldDirBox = new System.Windows.Controls.TextBox
        {
            Text        = ApWorldDirectory,
            IsReadOnly  = true,
            FontSize    = 11,
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            Margin      = new System.Windows.Thickness(0, 0, 0, 4),
        };

        var apworldDirRow = new System.Windows.Controls.DockPanel
        { Margin = new System.Windows.Thickness(0, 0, 0, 4) };

        var apworldBrowseBtn = MakeButton("Browse...", 90, fg);
        apworldBrowseBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select Archipelago custom_worlds folder",
                InitialDirectory = Directory.Exists(ApWorldDirectory)
                                       ? ApWorldDirectory : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                ApWorldDirectory   = dlg.FolderName;
                apworldDirBox.Text = dlg.FolderName;
                SaveSettings();
            }
        };
        System.Windows.Controls.DockPanel.SetDock(apworldBrowseBtn,
            System.Windows.Controls.Dock.Right);
        apworldDirRow.Children.Add(apworldBrowseBtn);
        apworldDirRow.Children.Add(apworldDirBox);
        panel.Children.Add(apworldDirRow);

        // apworld installed status.
        bool apworldOk = DetectApWorldInstalled();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text       = apworldOk
                ? "shadow_the_hedgehog.apworld is installed"
                : "Apworld not found — click Install in the Play tab",
            FontSize   = 11,
            Foreground = apworldOk ? success : warn,
            Margin     = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Dolphin executable ───────────────────────────────────
        panel.Children.Add(MakeSectionHeader("DOLPHIN EMULATOR", muted));

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Path to Dolphin.exe. The launcher opens Dolphin with your ISO " +
                "when you click Play. Dolphin 2409 or newer is required. " +
                "Memory Size Override must be DISABLED in Dolphin's settings.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dolphinBox = MakeReadOnlyTextBox(DolphinExePath, fg);
        var dolphinRow = new System.Windows.Controls.DockPanel
        { Margin = new System.Windows.Thickness(0, 0, 0, 4) };

        var dolphinBtn = MakeButton("Browse...", 90, fg);
        dolphinBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select Dolphin.exe",
                Filter = "Dolphin Emulator|Dolphin.exe;dolphin.exe|All executables|*.exe",
                InitialDirectory = File.Exists(DolphinExePath)
                    ? Path.GetDirectoryName(DolphinExePath) ?? AppContext.BaseDirectory
                    : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                DolphinExePath = dlg.FileName;
                dolphinBox.Text = dlg.FileName;
                SaveSettings();
            }
        };
        System.Windows.Controls.DockPanel.SetDock(dolphinBtn,
            System.Windows.Controls.Dock.Right);
        dolphinRow.Children.Add(dolphinBtn);
        dolphinRow.Children.Add(dolphinBox);
        panel.Children.Add(dolphinRow);

        bool dolphinOk = !string.IsNullOrWhiteSpace(DolphinExePath)
                         && File.Exists(DolphinExePath);
        panel.Children.Add(StatusLine(
            dolphinOk ? "Dolphin located" : "Dolphin not set — click Browse above",
            dolphinOk, success, warn));

        // ── Section: ISO path ─────────────────────────────────────────────
        panel.Children.Add(MakeSectionHeader("SHADOW THE HEDGEHOG ISO", muted));

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Your NTSC-U Shadow The Hedgehog GameCube ISO. " +
                "Game IDs GUPE8P, GUPR8P (Reloaded), and GUPX8P are all supported. " +
                "The Archipelago community cannot provide this — use your own disc.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var isoBox = MakeReadOnlyTextBox(IsoPath, fg);
        var isoRow = new System.Windows.Controls.DockPanel
        { Margin = new System.Windows.Thickness(0, 0, 0, 4) };

        var isoBtn = MakeButton("Browse...", 90, fg);
        isoBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title  = "Select Shadow The Hedgehog ISO",
                Filter = "GameCube ISO|*.iso;*.gcm;*.gcz;*.rvz|All files|*.*",
                InitialDirectory = File.Exists(IsoPath)
                    ? Path.GetDirectoryName(IsoPath) ?? AppContext.BaseDirectory
                    : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                IsoPath     = dlg.FileName;
                isoBox.Text = dlg.FileName;
                SaveSettings();
            }
        };
        System.Windows.Controls.DockPanel.SetDock(isoBtn,
            System.Windows.Controls.Dock.Right);
        isoRow.Children.Add(isoBtn);
        isoRow.Children.Add(isoBox);
        panel.Children.Add(isoRow);

        bool isoOk = !string.IsNullOrWhiteSpace(IsoPath) && File.Exists(IsoPath);
        panel.Children.Add(StatusLine(
            isoOk ? "ISO located" : "ISO not set — click Browse above",
            isoOk, success, warn));

        // ── Section: Setup steps ──────────────────────────────────────────
        panel.Children.Add(MakeSectionHeader("SETUP STEPS", muted));

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "1. Click Install in the Play tab to download shadow_the_hedgehog.apworld.\n" +
                "2. Install choatix's Archipelago fork (or the stock AP launcher).\n" +
                "   The apworld is placed in the custom_worlds folder above automatically.\n" +
                "3. Generate your multiworld using the Archipelago launcher.\n" +
                "4. Set Dolphin.exe and your ISO paths above.\n" +
                "5. In Dolphin: Dolphin → Config → Advanced → uncheck\n" +
                "   'Enable Emulated Memory Size Override'.\n" +
                "6. Click Play here — Dolphin opens with the ISO automatically.\n" +
                "7. In the Archipelago launcher, select 'Shadow The Hedgehog Client'\n" +
                "   and enter your server, slot, and password. Press Connect.\n" +
                "8. Do NOT load an existing save file — start fresh each multiworld.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin       = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Links ────────────────────────────────────────────────
        panel.Children.Add(MakeSectionHeader("LINKS", muted));

        foreach (var (label, url) in new[]
        {
            ("Shadow The Hedgehog Archipelago Releases (choatix) ↗",
             "https://github.com/choatix/Archipelago/releases"),
            ("Setup Guide ↗",
             "https://github.com/choatix/Archipelago/blob/shadow-cp/worlds/shadow_the_hedgehog/docs/setup_en.md"),
            ("Dolphin Emulator ↗", "https://dolphin-emu.org/"),
            ("Archipelago Official ↗", "https://archipelago.gg"),
        })
        {
            panel.Children.Add(MakeLinkButton(label, url));
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
                // Only include shadow- tagged releases.
                string? tagName = el.TryGetProperty("tag_name", out var t)
                    ? t.GetString() : null;
                if (tagName == null ||
                    !tagName.StartsWith(RELEASE_TAG_PREFIX, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Skip drafts.
                if (el.TryGetProperty("draft", out var dr) &&
                    dr.ValueKind == JsonValueKind.True)
                    continue;

                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) &&
                    d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : tagName,
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: NormalizeTag(tagName) ?? tagName,
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

    // ── Private helpers — detection ───────────────────────────────────────────

    /// True when the apworld file exists in ApWorldDirectory.
    private bool DetectApWorldInstalled()
    {
        try
        {
            return !string.IsNullOrWhiteSpace(ApWorldDirectory)
                   && File.Exists(ApWorldFilePath);
        }
        catch { return false; }
    }

    // ── Private helpers — GitHub release resolution ───────────────────────────

    /// Resolve the latest shadow-* release from choatix/Archipelago.
    /// Returns (version, apworldDownloadUrl, null).
    private async Task<(string Version, string? ApWorldUrl, object? _)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        // choatix/Archipelago uses per-game release tags (shadow-X.Y.Z), so we
        // enumerate all releases and pick the newest shadow- tag.
        try
        {
            // Try up to the first 3 pages of releases (30 each).
            for (int page = 1; page <= 3; page++)
            {
                string url = GH_RELEASES_URL + "?per_page=30&page=" + page;
                string json = await _http.GetStringAsync(url, ct);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.ValueKind != JsonValueKind.Array) break;

                bool anyOnPage = false;
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    anyOnPage = true;

                    string? tag = rel.TryGetProperty("tag_name", out var t)
                        ? t.GetString() : null;
                    if (tag == null ||
                        !tag.StartsWith(RELEASE_TAG_PREFIX, StringComparison.OrdinalIgnoreCase))
                        continue;

                    // Skip drafts.
                    if (rel.TryGetProperty("draft", out var dr) &&
                        dr.ValueKind == JsonValueKind.True)
                        continue;

                    string? version = NormalizeTag(tag);
                    if (version == null) continue;

                    // Find shadow_the_hedgehog.apworld asset.
                    if (!rel.TryGetProperty("assets", out var assets) ||
                        assets.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var asset in assets.EnumerateArray())
                    {
                        string? assetName = asset.TryGetProperty("name", out var an)
                            ? an.GetString() : null;
                        string? dlUrl = asset.TryGetProperty("browser_download_url", out var u)
                            ? u.GetString() : null;

                        if (assetName != null && dlUrl != null &&
                            assetName.Equals(APWORLD_ASSET_NAME,
                                             StringComparison.OrdinalIgnoreCase))
                        {
                            return (version, dlUrl, null);
                        }
                    }
                }

                if (!anyOnPage) break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* fall through to pinned fallback */ }

        return (FallbackVersion, null, null);
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartDolphin()
    {
        // If Dolphin exe and ISO are both configured, open the ISO directly.
        if (!string.IsNullOrWhiteSpace(DolphinExePath) && File.Exists(DolphinExePath)
            && !string.IsNullOrWhiteSpace(IsoPath)      && File.Exists(IsoPath))
        {
            var psi = new ProcessStartInfo
            {
                FileName         = DolphinExePath,
                // --exec opens the ISO immediately in Dolphin's batch mode.
                Arguments        = "--exec=\"" + IsoPath + "\"",
                WorkingDirectory = Path.GetDirectoryName(DolphinExePath) ?? "",
                UseShellExecute  = false,
            };

            var proc = Process.Start(psi);
            if (proc != null)
            {
                _dolphinProcess = proc;
                IsRunning       = true;
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) => { IsRunning = false; };
            }
            return;
        }

        // Dolphin configured but no ISO — open Dolphin without a game.
        if (!string.IsNullOrWhiteSpace(DolphinExePath) && File.Exists(DolphinExePath))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName        = DolphinExePath,
                UseShellExecute = true,
            });
            if (proc != null)
            {
                _dolphinProcess = proc;
                IsRunning       = true;
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) => { IsRunning = false; };
            }
            return;
        }

        // Nothing configured — open Dolphin website so the user can install it.
        Process.Start(new ProcessStartInfo
        {
            FileName        = "https://dolphin-emu.org/download/",
            UseShellExecute = true,
        });
    }

    // ── Private helpers — tag normalization ───────────────────────────────────

    /// "shadow-0.4.8" → "0.4.8".  Strips the "shadow-" prefix, then strips
    /// a leading 'v' when followed by a digit.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        if (tag.StartsWith(RELEASE_TAG_PREFIX, StringComparison.OrdinalIgnoreCase))
            tag = tag[RELEASE_TAG_PREFIX.Length..];
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    // ── Private helpers — default apworld directory ───────────────────────────

    /// Best-guess default for the Archipelago custom_worlds folder.
    /// Tries common Archipelago install locations in priority order.
    private static string DefaultApWorldDirectory()
    {
        // 1. Standard Archipelago installer default on Windows.
        string program = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string candidate1 = Path.Combine(program, "Archipelago", "custom_worlds");
        if (Directory.Exists(candidate1)) return candidate1;

        // 2. ProgramData (AP also ships here occasionally).
        string programData = Environment.GetFolderPath(
            Environment.SpecialFolder.CommonApplicationData);
        string candidate2 = Path.Combine(programData, "Archipelago", "custom_worlds");
        if (Directory.Exists(candidate2)) return candidate2;

        // 3. LocalAppData.
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string candidate3 = Path.Combine(local, "Archipelago", "custom_worlds");
        if (Directory.Exists(candidate3)) return candidate3;

        // 4. Return the Program Files candidate even if it doesn't exist yet —
        //    the install step will create it.
        return candidate1;
    }

    // ── Private helpers — settings persistence ────────────────────────────────

    private sealed class PluginSettings
    {
        public string? ApWorldDirectory { get; set; }
        public string? DolphinExePath   { get; set; }
        public string? IsoPath          { get; set; }
    }

    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(SettingsSidecarPath)) return;
            string txt = File.ReadAllText(SettingsSidecarPath);
            if (string.IsNullOrWhiteSpace(txt)) return;
            var s = JsonSerializer.Deserialize<PluginSettings>(txt);
            if (s == null) return;
            if (!string.IsNullOrWhiteSpace(s.ApWorldDirectory))
                ApWorldDirectory = s.ApWorldDirectory;
            if (!string.IsNullOrWhiteSpace(s.DolphinExePath))
                DolphinExePath = s.DolphinExePath;
            if (!string.IsNullOrWhiteSpace(s.IsoPath))
                IsoPath = s.IsoPath;
        }
        catch { /* non-essential */ }
    }

    private void SaveSettings()
    {
        try
        {
            string dir = Path.GetDirectoryName(SettingsSidecarPath)!;
            Directory.CreateDirectory(dir);
            var s = new PluginSettings
            {
                ApWorldDirectory = ApWorldDirectory,
                DolphinExePath   = DolphinExePath,
                IsoPath          = IsoPath,
            };
            File.WriteAllText(
                SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(false));
        }
        catch { /* non-essential */ }
    }

    // ── Private helpers — UI factory methods ──────────────────────────────────

    private static System.Windows.Controls.TextBlock MakeSectionHeader(
        string text,
        System.Windows.Media.Brush foreground)
    {
        return new System.Windows.Controls.TextBlock
        {
            Text       = text,
            FontSize   = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = foreground,
            Margin     = new System.Windows.Thickness(0, 12, 0, 8),
        };
    }

    private static System.Windows.Controls.TextBox MakeReadOnlyTextBox(
        string text,
        System.Windows.Media.Brush fg)
    {
        return new System.Windows.Controls.TextBox
        {
            Text        = text,
            IsReadOnly  = true,
            FontSize    = 11,
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
        };
    }

    private static System.Windows.Controls.Button MakeButton(
        string content, double width,
        System.Windows.Media.Brush fg)
    {
        return new System.Windows.Controls.Button
        {
            Content     = content,
            Width       = width,
            Padding     = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                              System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
            Margin      = new System.Windows.Thickness(4, 0, 0, 0),
        };
    }

    private static System.Windows.Controls.TextBlock StatusLine(
        string text, bool good,
        System.Windows.Media.Brush okBrush,
        System.Windows.Media.Brush warnBrush)
    {
        return new System.Windows.Controls.TextBlock
        {
            Text       = (good ? "✓ " : "⚠ ") + text,
            FontSize   = 11,
            Foreground = good ? okBrush : warnBrush,
            Margin     = new System.Windows.Thickness(0, 4, 0, 8),
        };
    }

    private static System.Windows.Controls.Button MakeLinkButton(string label, string url)
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
            Foreground          = new System.Windows.Media.SolidColorBrush(
                                      System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
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
        return btn;
    }
}
