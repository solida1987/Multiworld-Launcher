using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

// IMPORTANT (real project has <UseWindowsForms>true</UseWindowsForms>):
// WPF UI types that collide with WinForms are FULLY QUALIFIED below
// (System.Windows.Controls.*, System.Windows.Media.*, System.Windows.Thickness,
// System.Windows.FontWeights, System.Windows.HorizontalAlignment,
// System.Windows.TextWrapping, …) to avoid CS0104 ambiguities. Do NOT add
// `using System.Windows.Controls;` / `using System.Windows.Media;` here.
using LauncherV2.Core;

namespace LauncherV2.Plugins.Zork;

// ═══════════════════════════════════════════════════════════════════════════════
// ZorkPlugin — install / update / launch for "Zork: Grand Inquisitor" via the
// SerpentAI Archipelago fork. This is a NATIVE "ConnectsItself" integration
// (NOT a BizHawk / Lua emulator game): the AP client built into the SerpentAI
// Archipelago fork speaks to the AP server itself, exactly like the OpenTTD
// Archipelago fork, APDOOM, and Celeste 64.
//
// REALITY CHECK (2026-06-16) — facts verified against catalog.json + research docs
// ─────────────────────────────────────────────────────────────────────────────
//   * AP WORLD + GAME STRING: "Zork: Grand Inquisitor" — verified against
//     catalog.json (ap_world_name field, line 14075) and the AP game page at
//     https://archipelago.gg/games/Zork%3A%20Grand%20Inquisitor. The world folder
//     in the SerpentAI fork is worlds/zork_grand_inquisitor/.
//
//   * FORK: This is NOT in AP-main. The Zork world lives in the SerpentAI
//     Archipelago fork at https://github.com/SerpentAI/Archipelago. The fork
//     ships releases tagged "Zork Grand Inquisitor - 1.1.0" (latest verified
//     visible on the releases page: Mar 1, 2024, by contributor "nbrochu").
//     The releases page is filtered with ?q=zork in the canonical install URL:
//       https://github.com/SerpentAI/Archipelago/releases?q=zork&expanded=true
//     The release tag pattern observed is "Zork Grand Inquisitor - 1.1.0".
//
//   * ASSET GATE: Zork: Grand Inquisitor (1997, Activision/Infocom) is a
//     COMMERCIAL game — it is NOT freely distributable. The player MUST own a
//     legal copy of the game (available on GOG or Steam, or from their own
//     physical media). The AP client / fork handles connecting to the AP server,
//     but the original game data files must be provided by the player. This plugin
//     is honest about this requirement and does NOT attempt to download game assets.
//
//   * HOW IT CONNECTS: The SerpentAI fork's Zork Grand Inquisitor client connects
//     to the Archipelago server itself. The exact connection mechanism (config file,
//     command-line arguments, or in-game menu) was not fully verified from the
//     setup docs during this session. This plugin uses a command-line approach as
//     its best-effort default (common for SerpentAI fork clients), with a clear
//     in-settings note directing the player to the setup guide if this is wrong
//     for their release version.
//
//   * INSTALL STRATEGY: The catalog marks this as "external_client". The releases
//     page uses a query filter (?q=zork), which means there is no /releases/latest
//     redirect that cleanly resolves to a Zork asset — the API must scan all
//     releases for Zork-tagged ones. This plugin attempts to do so, with a pinned
//     fallback URL for the known v1.1.0 release.
//
// DEFENSIVE / UNVERIFIED DETAILS:
//   * The exact Windows asset name inside the Zork Grand Inquisitor release was
//     not inspected offline. Asset picking uses broad heuristics (zip/exe,
//     excluding linux/mac/source).
//   * The exact connection command-line flags were not verified from source. If the
//     client uses a config file instead, the player will need to set it up manually
//     per the SerpentAI fork's setup guide. This is clearly documented in the
//     settings panel.
//   * The SerpentAI fork is a full AP fork — the client EXE is the AP launcher/
//     client itself, not a standalone game EXE. The player may need to select the
//     Zork game within the fork's client UI rather than having it launch directly.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ZorkPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER = "SerpentAI";
    private const string GITHUB_REPO  = "Archipelago";
    // The releases list filtered for Zork (verified canonical URL from catalog):
    private const string ReleasesFilteredUrl =
        "https://github.com/SerpentAI/Archipelago/releases?q=zork&expanded=true";
    // API: list all releases, then filter locally for the Zork tag.
    private const string GH_RELEASES_URL =
        "https://api.github.com/repos/SerpentAI/Archipelago/releases";

    // Setup guide (SerpentAI fork — no official AP-main setup guide exists for
    // this world since it is not in AP-main):
    private const string SetupGuideUrl =
        "https://archipelago.gg/games/Zork%3A%20Grand%20Inquisitor";

    // AP game page:
    private const string ApGamePageUrl =
        "https://archipelago.gg/games/Zork%3A%20Grand%20Inquisitor";

    // GOG purchase page for the commercial game asset:
    private const string GogUrl =
        "https://www.gog.com/game/zork_grand_inquisitor";

    // Pinned fallback — the known latest release at time of writing (Mar 1, 2024),
    // tag pattern "Zork Grand Inquisitor - 1.1.0". The exact Windows asset name
    // was not verified offline; we use a best-effort heuristic. If this URL 404s,
    // the install directs the player to the releases page to download manually.
    private const string FallbackVersion = "1.1.0";
    private static readonly string FallbackReleasesPageUrl = ReleasesFilteredUrl;

    // Default Archipelago port:
    private const int DefaultApPort = 38281;

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    /// Installed-version stamp, written after a successful install.
    private const string VersionFileName = "zork_gqi_ap_version.dat";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "zork_grand_inquisitor";
    public string DisplayName => "Zork: Grand Inquisitor";
    public string Subtitle    => "Native PC · SerpentAI Archipelago fork";

    /// EXACT AP game string — verified against catalog.json (ap_world_name) and
    /// the Archipelago game page. The world folder is zork_grand_inquisitor.
    public string ApWorldName => "Zork: Grand Inquisitor";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "zork_grand_inquisitor.png");

    public string ThemeAccentColor => "#3A7A3C";   // dungeon moss green

    public string[] GameBadges => new[]
    {
        "Paid game required",
        "SerpentAI fork"
    };

    public string Description =>
        "Zork: Grand Inquisitor (1997, Activision/Infocom) is a classic point-and-click " +
        "adventure set in the Great Underground Empire of Zork. The Archipelago " +
        "integration runs via the SerpentAI Archipelago fork, which ships its own " +
        "built-in multiworld client — the game connects to the Archipelago server " +
        "itself with no emulator or Lua bridge. " +
        "IMPORTANT: You must own a legal copy of Zork: Grand Inquisitor (available on " +
        "GOG or Steam). This plugin installs the SerpentAI AP client; the game data " +
        "files must be provided from your own copy. See the settings panel for setup " +
        "instructions.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }
    public bool    IsInstalled      => ResolveClientExe() != null;
    public bool    IsRunning        { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Root folder where the SerpentAI AP client for Zork GI is installed.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Zork");

    private string VersionFilePath => Path.Combine(GameDirectory, VersionFileName);

    private string SettingsSidecarPath
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId,
                        "zork_gqi_launcher.json");

    private string SettingsSidecarDir => Path.GetDirectoryName(SettingsSidecarPath)!;

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The SerpentAI AP client for Zork GI reports checks/items/goal to the AP
    // server itself — the launcher relays nothing. These exist for interface
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
            var (version, _) = await ResolveLatestReleaseAsync(ct);
            AvailableVersion = version;
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
        progress.Report((2, "Checking for Zork: Grand Inquisitor AP client release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        // Already current? (idempotent fast path)
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"Zork: Grand Inquisitor AP client {version} is up to date."));
            return;
        }

        if (zipUrl == null)
        {
            // No auto-installable asset was found. Direct the player to download manually.
            progress.Report((100,
                "Automatic download is not available. Please download the Zork: Grand " +
                "Inquisitor AP client manually from the SerpentAI Archipelago releases " +
                "page, then extract it into: " + GameDirectory));
            throw new InvalidOperationException(
                "Could not find a Windows download for the Zork: Grand Inquisitor AP " +
                "client on the SerpentAI Archipelago releases page. Download it manually " +
                "from: " + FallbackReleasesPageUrl + "\n\n" +
                "Extract the downloaded zip into: " + GameDirectory);
        }

        await DownloadAndExtractClientAsync(zipUrl, version, progress, ct);

        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"Zork: Grand Inquisitor AP client {version} installed. " +
            "Ensure your game data files (from your legal copy of Zork: Grand " +
            "Inquisitor) are accessible, then press Play to connect to Archipelago."));
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
        string exe = ResolveClientExe()
            ?? throw new FileNotFoundException(
                "The Zork: Grand Inquisitor AP client is not installed. " +
                "Click Install Game first.",
                Path.Combine(GameDirectory, "ArchipelagoClient.exe"));

        var (host, port) = ParseServerHostPort(session.ServerUri);

        // Best-effort command-line connection. The exact flags used by the
        // SerpentAI fork's client were not verified from source during this session.
        // Common pattern for SerpentAI fork clients: pass server/port/slot/password
        // as command-line arguments. If the client uses a different mechanism
        // (config file, in-client UI), the player will need to enter the details
        // manually — this is documented in the settings panel.
        var args = new System.Text.StringBuilder();
        args.Append($"--server {host}:{port}");
        args.Append($" --name \"{session.SlotName}\"");
        if (!string.IsNullOrEmpty(session.Password))
            args.Append($" --password \"{session.Password}\"");

        try
        {
            StartClientProcess(exe, args.ToString());
        }
        catch
        {
            // If the command-line args cause the process to refuse to start
            // (e.g. the client ignores unknown flags), try launching without args
            // so the player can connect manually in the client UI.
            StartClientProcess(exe, string.Empty);
        }

        return Task.CompletedTask;
    }

    /// Zork: Grand Inquisitor is a commercial game — standalone play without the
    /// Archipelago integration still requires the original game's installer/launcher,
    /// which is outside the scope of this plugin. SupportsStandalone is false.
    public bool SupportsStandalone => false;

    /// The SerpentAI AP client owns the slot connection itself.
    /// The launcher must not connect its own ApClient to the same slot while the
    /// client runs.
    public bool ConnectsItself => true;

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The SerpentAI AP client receives items from the AP server directly;
        // there is nothing to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The SerpentAI AP client manages its own AP connection state display.
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
            System.Windows.Media.Color.FromRgb(0xFF, 0xA0, 0x30));
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
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select Zork: Grand Inquisitor AP client folder",
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
            Text       = IsInstalled
                ? "✓ Zork: Grand Inquisitor AP client is installed"
                : "Not installed (click Install in the Play tab)",
            FontSize   = 11, Foreground = IsInstalled ? success : muted,
            Margin     = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Game data requirement (IMPORTANT) ────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GAME DATA REQUIRED", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = warn, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Zork: Grand Inquisitor is a commercial game. You MUST own a legal " +
                   "copy to play (available on GOG or Steam). The AP client installed " +
                   "by this launcher handles the Archipelago connection, but the original " +
                   "game data files must come from your own copy of the game. " +
                   "Follow the SerpentAI fork's setup guide to point the AP client at " +
                   "your game installation.",
            FontSize = 11, Foreground = warn,
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
            Text = "This is a SerpentAI Archipelago fork integration — the AP client " +
                   "connects to the Archipelago server itself with no emulator or Lua " +
                   "bridge (ConnectsItself = true). The launcher attempts to pass your " +
                   "connection details (server, slot, password) via command-line arguments " +
                   "when you press Play. If the client uses a config file or in-client UI " +
                   "instead, enter your connection details there after launch. " +
                   "See the SerpentAI fork setup guide for the exact connection steps.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("SerpentAI Archipelago Releases (Zork) ↗", ReleasesFilteredUrl),
            ("Zork: Grand Inquisitor AP Game Page ↗",   ApGamePageUrl),
            ("Buy on GOG ↗",                            GogUrl),
            ("Archipelago Official ↗",                  "https://archipelago.gg"),
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
        // The SerpentAI Archipelago releases page uses a filtered query URL, not
        // a single JSON feed. The GitHub API endpoint for all releases is large;
        // return the latest Zork-tagged releases (up to 5) as news items.
        try
        {
            // Fetch multiple pages if needed — limit to the first page (30 items)
            // and filter for Zork-tagged releases.
            string json = await _http.GetStringAsync(GH_RELEASES_URL + "?per_page=100", ct);
            using var doc  = JsonDocument.Parse(json);
            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string? tagName = el.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                if (tagName == null) continue;
                if (!tagName.Contains("Zork", StringComparison.OrdinalIgnoreCase) &&
                    !tagName.Contains("zork", StringComparison.OrdinalIgnoreCase))
                    continue;

                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? tagName : tagName,
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: NormalizeZorkTag(tagName),
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (items.Count >= 5) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// Normalises a Zork release tag like "Zork Grand Inquisitor - 1.1.0"
    /// to just "1.1.0". Returns the trimmed tag if no version number is found.
    private static string NormalizeZorkTag(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return "";
        tag = tag.Trim();
        // Pattern: anything ending in " - X.Y.Z" or "vX.Y.Z"
        int dash = tag.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dash >= 0 && dash < tag.Length - 3)
        {
            string ver = tag[(dash + 3)..].Trim();
            if (ver.Length > 0 && (char.IsDigit(ver[0]) || (ver[0] == 'v' && ver.Length > 1)))
                return ver.TrimStart('v');
        }
        return tag;
    }

    /// Resolve the latest Zork-tagged release from the SerpentAI Archipelago
    /// releases list. Returns version string + Windows zip URL. Falls back to
    /// a known pinned version with a null URL (manual download required) when
    /// the API is unreachable or no Windows asset is found.
    private async Task<(string Version, string? ZipUrl)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            // Fetch the releases list (may be large; per_page=100 to get more).
            // The SerpentAI fork has many games, so scan for Zork-tagged entries.
            string json = await _http.GetStringAsync(GH_RELEASES_URL + "?per_page=100", ct);
            using var doc = JsonDocument.Parse(json);

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string? tagName = el.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                if (tagName == null) continue;
                if (!tagName.Contains("Zork", StringComparison.OrdinalIgnoreCase))
                    continue;

                string version = NormalizeZorkTag(tagName);
                if (el.TryGetProperty("assets", out var assets)
                    && assets.ValueKind == JsonValueKind.Array)
                {
                    string? zip = PickWindowsZip(assets);
                    if (zip != null)
                        return (version, zip);
                    // Assets exist but no Windows zip found — still report the version.
                    return (version, null);
                }
                return (version, null);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback below */ }

        // Offline/API fallback: known version, no direct asset URL.
        return (FallbackVersion, null);
    }

    /// From a release's assets array, pick the Windows .zip or .exe, excluding
    /// Linux/Mac/source/android variants.
    private static string? PickWindowsZip(JsonElement assets)
    {
        string? win64 = null, win32 = null, anyWinZip = null, anyZip = null;

        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();

            if (!lower.EndsWith(".zip") && !lower.EndsWith(".exe")) continue;

            if (lower.Contains("source")  ||
                lower.Contains("linux")   ||
                lower.Contains("ubuntu")  ||
                lower.Contains("mac")     ||
                lower.Contains("osx")     ||
                lower.Contains("darwin")  ||
                lower.Contains("android") ||
                lower.Contains("web"))
                continue;

            anyZip ??= url;
            if (lower.Contains("win64") || lower.Contains("win-x64") || lower.Contains("x86_64"))
                win64 ??= url;
            else if (lower.Contains("win32") || lower.Contains("win-x86"))
                win32 ??= url;
            else if (lower.Contains("win") || lower.Contains("x64"))
                anyWinZip ??= url;
        }

        return win64 ?? win32 ?? anyWinZip ?? anyZip;
    }

    // ── Private helpers — exe resolution ──────────────────────────────────────

    /// Resolve the installed AP client exe. The SerpentAI fork's AP client exe
    /// may be named "ArchipelagoClient.exe", "ArchipelagoLauncher.exe", or similar.
    /// Falls back to any non-uninstaller exe in the install folder.
    private string? ResolveClientExe()
    {
        // Check common SerpentAI fork exe names first.
        foreach (string candidate in new[]
        {
            "ArchipelagoClient.exe",
            "ArchipelagoLauncher.exe",
            "Archipelago.exe",
            "ZorkGrandInquisitor.exe",
            "Zork.exe",
        })
        {
            string p = Path.Combine(GameDirectory, candidate);
            if (File.Exists(p)) return p;
        }

        if (!Directory.Exists(GameDirectory)) return null;
        try
        {
            string? firstExe = null;
            foreach (string exe in Directory.EnumerateFiles(
                GameDirectory, "*.exe", SearchOption.AllDirectories))
            {
                string lower = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
                if (lower.Contains("unins") || lower.Contains("setup") ||
                    lower.Contains("crash") || lower.Contains("redist"))
                    continue;
                firstExe ??= exe;
                if (lower.Contains("archipelago") || lower.Contains("zork"))
                    return exe;
            }
            return firstExe;
        }
        catch { }
        return null;
    }

    // ── Private helpers — process ─────────────────────────────────────────────

    private void StartClientProcess(string exePath, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName         = exePath,
            WorkingDirectory = GameDirectory,
            UseShellExecute  = false,
        };
        if (!string.IsNullOrEmpty(args))
            psi.Arguments = args;

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException(
                "Failed to start the Zork: Grand Inquisitor AP client.");

        _gameProcess = proc;
        IsRunning    = true;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) =>
        {
            IsRunning = false;
            GameExited?.Invoke(proc.ExitCode);
        };
    }

    // ── Private helpers — download/extract ────────────────────────────────────

    private async Task DownloadAndExtractClientAsync(
        string zipUrl,
        string version,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        bool isExe = zipUrl.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        string ext = isExe ? ".exe" : ".zip";
        string tempFile = Path.Combine(Path.GetTempPath(),
            $"zork-gqi-ap-{version}-{Guid.NewGuid():N}{ext}");
        try
        {
            progress.Report((5, $"Downloading Zork: Grand Inquisitor AP client {version}..."));
            using var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempFile))
            {
                var buf = new byte[81920];
                int bytesRead;
                while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                    downloaded += bytesRead;
                    if (total > 0)
                    {
                        int pct = (int)(5 + 70 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading... {downloaded / 1_000_000}MB" +
                            (total > 0 ? $" / {total / 1_000_000}MB" : "")));
                    }
                }
                await dst.FlushAsync(ct);
            }

            Directory.CreateDirectory(GameDirectory);

            if (isExe)
            {
                // Self-contained EXE — copy directly to GameDirectory.
                progress.Report((80, "Copying AP client executable..."));
                string destExe = Path.Combine(GameDirectory, Path.GetFileName(tempFile));
                File.Copy(tempFile, destExe, overwrite: true);
            }
            else
            {
                // ZIP — extract, then flatten if there is a single wrapper subfolder.
                progress.Report((80, "Extracting..."));
                System.IO.Compression.ZipFile.ExtractToDirectory(
                    tempFile, GameDirectory, overwriteFiles: true);

                if (Directory.GetFiles(GameDirectory).Length == 0)
                {
                    string[] subdirs = Directory.GetDirectories(GameDirectory);
                    if (subdirs.Length == 1)
                    {
                        string sub = subdirs[0];
                        foreach (string src2 in Directory.EnumerateFiles(
                            sub, "*", SearchOption.AllDirectories))
                        {
                            string rel = Path.GetRelativePath(sub, src2);
                            string dst2 = Path.Combine(GameDirectory, rel);
                            Directory.CreateDirectory(Path.GetDirectoryName(dst2)!);
                            File.Move(src2, dst2, overwrite: true);
                        }
                        Directory.Delete(sub, recursive: true);
                    }
                }
            }

            progress.Report((95, "AP client extracted."));
        }
        finally
        {
            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
        }
    }

    // ── Private helpers — connection URI parsing ──────────────────────────────

    /// Accepts "archipelago.gg:38281", "ws://host:port", "wss://host:port",
    /// a bare hostname, and IPv6 literals. Returns (host, port).
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
            host = s;
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

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class ZorkSettings
    {
        // Placeholder for future launcher-side options.
    }

    private ZorkSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<ZorkSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(ZorkSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(false));
        }
        catch { }
    }
}
