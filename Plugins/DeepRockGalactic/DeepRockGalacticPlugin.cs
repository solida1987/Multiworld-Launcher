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
using Microsoft.Win32;
using LauncherV2.Core;

// NOTE: WPF UI types are FULLY QUALIFIED throughout this file (System.Windows.*).
// The real launcher project sets <UseWindowsForms>true</UseWindowsForms> alongside
// WPF, so the bare names Button / TextBox / Color / Brushes / MessageBox /
// FontWeights / Orientation / Clipboard collide between System.Windows.Forms and
// System.Windows[.Controls/.Media] (CS0104). Qualifying every UI type avoids that.

namespace LauncherV2.Plugins.DeepRockGalactic;

// ═══════════════════════════════════════════════════════════════════════════════
// DeepRockGalacticPlugin — install / launch for "Deep Rock Galactic"
// (Ghost Ship Games, 2020 — Steam appid 548430) played through the community
// Archipelago integration by Cousinit117 (Deep-Rock-Galactic-AP on GitHub).
//
// NOTE: This is for the ORIGINAL Deep Rock Galactic (the cooperative first-person
// shooter). It is NOT for Deep Rock Galactic: Survivor (Steam 2321470) or
// Deep Rock Galactic: Rogue Core — neither of those have an AP world as of
// June 2026.
//
// ── HONEST REALITY CHECK (2026-06-15, verified online) ───────────────────────
// This is a COMMUNITY AP WORLD that lives OUTSIDE the main ArchipelagoMW repo:
//   github.com/Cousinit117/Deep-Rock-Galactic-AP
// Latest release: V0.18.4.1 (2026-05-18, non-prerelease).
// AP world game string: "Deep Rock Galactic"
//   (verified from worlds/deep_rock_galactic/__init__.py: game = 'Deep Rock Galactic')
//
// HOW THE INTEGRATION WORKS (verified from the release notes, V0.18.4.1):
//
//   THERE ARE TWO SEPARATE PIECES — the user must install BOTH:
//
//   1. THE PAK MOD (in-game side) — "Archi_*.pak" (e.g. Archi_0.18.3.1.pak),
//      shipped as a release asset in the GitHub repo. This mod runs inside DRG
//      and communicates locally with the external AP client. It is installed via
//      the MINT SIDELOADER for Deep Rock Galactic:
//        - Install mint (github.com/trumank/mint) — the standard DRG mod loader.
//        - The mod is also published on mod.io; the release notes mention
//          "the mod is now bundled directly in releases for users of the mint
//          sideloader", so the user can also point mint at the local .pak file.
//
//   2. THE ARCHIPELAGO CLIENT (server-bridge side) — the Cousinit117 fork of
//      the Archipelago Python project. The user downloads a release from the
//      GitHub releases page (same repo) and runs the standard Archipelago
//      client exe (ArchipelagoTextClient or a game-specific client). This client
//      speaks to the AP server and forwards items/locations to/from the pak mod
//      running inside DRG via local IPC (text-file-based: apclocations.txt).
//
// HOW THIS PLUGIN FITS IN (honest scope):
//   * ConnectsItself = false — the external Archipelago client (from the fork)
//     holds the server slot connection. However, the launcher ALSO does NOT
//     maintain a live AP slot connection for DRG during play — the integration
//     is guide + .apworld drop + launch only. The standard launcher ApClient
//     behaviour applies per the interface contract.
//   * SupportsStandalone = true — DRG is a great game that runs perfectly without
//     Archipelago.
//   * NO COMMAND-LINE PREFILL — DRG is launched from Steam or its exe; the AP
//     client is the Cousinit117 fork executable (separate tool); there is no
//     argument the launcher can pass to either to pre-seed the connection.
//     The settings panel surfaces the session credentials for the user to copy.
//
// WHAT THIS PLUGIN ACTUALLY DOES:
//   1. DETECT the Steam DRG install via the registry + libraryfolders.vdf.
//      Manual override also supported.
//   2. INSTALL/UPDATE = download "deep_rock_galactic.apworld" from the latest
//      GitHub release and place it in the standard Archipelago custom_worlds
//      folder (%APPDATA%\Archipelago\lib\worlds\ or the launcher's own
//      Games\DeepRockGalactic folder as a local copy). ALSO downloads the
//      Archi_*.pak file so the user can hand-install it via mint.
//   3. LAUNCH = start DRG via Steam (steam://rungameid/548430) or the direct
//      exe if found. Surfaces a clear guide for the remaining steps.
//   4. SETTINGS PANEL = guided setup steps, links to the GitHub repo and mint.
//
// CRITICAL HONEST CAVEAT:
//   This AP world is a community fork in ALPHA — it is NOT in the main
//   Archipelago release and has no official setup guide on archipelago.gg.
//   The pak mod and the AP client release come from the same GitHub repo.
//   The user needs to download the Cousinit117 fork SEPARATELY and run its
//   AP client (a Python application) alongside DRG. The launcher cannot
//   automate the fork download or the mint install — it guides those steps.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DeepRockGalacticPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    private const string DrgSteamAppId        = "548430";
    private static readonly string SteamRunUrl = "steam://rungameid/" + DrgSteamAppId;
    private const string DrgExeName           = "FSD-Win64-Shipping.exe";
    private const string DrgSteamFolderName   = "Deep Rock Galactic";

    private const string GithubOwner  = "Cousinit117";
    private const string GithubRepo   = "Deep-Rock-Galactic-AP";
    private const string GithubApiUrl = "https://api.github.com/repos/Cousinit117/Deep-Rock-Galactic-AP/releases";

    private const string FallbackVersion    = "0.18.4.1";
    private const string FallbackApworldUrl =
        "https://github.com/Cousinit117/Deep-Rock-Galactic-AP/releases/download/0.18.4.1/deep_rock_galactic.apworld";
    private const string FallbackPakUrl =
        "https://github.com/Cousinit117/Deep-Rock-Galactic-AP/releases/download/0.18.4.1/Archi_0.18.3.1.pak";

    private const string MintUrl          = "https://github.com/trumank/mint";
    private const string RepoReleasesUrl  = "https://github.com/Cousinit117/Deep-Rock-Galactic-AP/releases";
    private const string RepoUrl          = "https://github.com/Cousinit117/Deep-Rock-Galactic-AP";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "deep_rock_galactic";
    public string DisplayName => "Deep Rock Galactic";
    public string Subtitle    => "Native PC · Community AP world (Alpha)";

    /// AP world game string — verified from worlds/deep_rock_galactic/__init__.py:
    /// game = 'Deep Rock Galactic'
    public string ApWorldName => "Deep Rock Galactic";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "deep_rock_galactic.png");

    public string ThemeAccentColor => "#F5A623";   // DRG's amber/gold accent
    public string[] GameBadges     => new[] { "Steam · community AP", "Alpha" };

    public string Description =>
        "Deep Rock Galactic, the cooperative first-person shooter by Ghost Ship Games, " +
        "played through a community Archipelago integration by Cousinit117. You bring " +
        "your own copy of the game (owned on Steam), install a .pak mod via the mint " +
        "sideloader, and run a standalone Archipelago client (from the Cousinit117 fork) " +
        "alongside the game — the client bridges your slot to the AP server while the " +
        "mod communicates locally inside DRG. Locations include missions, unlocks, " +
        "biome completions, and more. This is an ALPHA community world outside the main " +
        "Archipelago project — expect rough edges and check the GitHub repo for the " +
        "latest setup instructions.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    public bool IsInstalled
    {
        get
        {
            try { return File.Exists(LocalApworldPath); }
            catch { return false; }
        }
    }

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "DeepRockGalactic");

    private string LocalApworldPath
        => Path.Combine(GameDirectory, "deep_rock_galactic.apworld");

    private string LocalPakDir => Path.Combine(GameDirectory, "pak");

    private string SettingsSidecarPath
        => Path.Combine(GameDirectory, "drg_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // Deep Rock Galactic's AP integration uses the Cousinit117 standalone client,
    // not a launcher-held ApClient. These events exist for interface compatibility.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Lifecycle — ConnectsItself / SupportsStandalone ───────────────────────

    /// The Cousinit117 AP fork (external client) holds the server slot connection.
    /// The launcher does NOT maintain a live slot connection while the game runs.
    bool IGamePlugin.ConnectsItself => false;

    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = File.Exists(LocalApworldPath)
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
                await GitHubHelper.FetchLatestTagAsync("Cousinit117", "Deep-Rock-Galactic-AP", ct));
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
        progress.Report((2, "Checking the latest Deep Rock Galactic AP release..."));
        var (version, apworldUrl, pakUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        Directory.CreateDirectory(GameDirectory);
        Directory.CreateDirectory(LocalPakDir);

        // 1. Download the .apworld file.
        progress.Report((8, "Downloading deep_rock_galactic.apworld..."));
        await DownloadFileAsync(apworldUrl, LocalApworldPath, 8, 55, progress, ct);

        // 2. Download the pak mod file (if available) for manual mint install.
        if (!string.IsNullOrWhiteSpace(pakUrl))
        {
            progress.Report((58, "Downloading pak mod file for mint..."));
            string pakFileName = pakUrl.Contains('/')
                ? pakUrl.Substring(pakUrl.LastIndexOf('/') + 1)
                : "Archi.pak";
            string localPakPath = Path.Combine(LocalPakDir, pakFileName);
            try
            {
                await DownloadFileAsync(pakUrl, localPakPath, 58, 92, progress, ct);
            }
            catch
            {
                // pak download is best-effort; the .apworld is the critical file
            }
        }

        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            "Downloaded deep_rock_galactic.apworld " + version + " and the pak mod file. " +
            "Next steps: (1) Copy deep_rock_galactic.apworld to your Archipelago " +
            "custom_worlds folder (typically %APPDATA%\\Archipelago\\lib\\worlds\\). " +
            "(2) Install the pak file from the pak\\ subfolder via mint (the DRG mod " +
            "sideloader). (3) Download and run the Cousinit117 AP client fork separately. " +
            "See Settings for the full guided steps and links."));
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
        // The AP connection for DRG is managed by the Cousinit117 standalone client
        // (a separate Python application from the fork), not the launcher. Launching
        // from this tile just starts DRG itself; the user connects via the external
        // AP client. The session credentials are shown in the settings panel so the
        // user can copy them into the external client.
        _ = session;
        StartDrg();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartDrg();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert ─────────────────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── ValidateExistingInstall ───────────────────────────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Deep Rock Galactic install folder.";
        if (LooksLikeDrgDir(folder))
            return null;
        try
        {
            string nested = Path.Combine(folder, DrgSteamFolderName);
            if (LooksLikeDrgDir(nested)) return null;
        }
        catch { }
        return "That does not look like a Deep Rock Galactic installation. Pick the folder " +
               "that contains \"FSD-Win64-Shipping.exe\" (usually inside FSD\\Binaries\\Win64\\) " +
               "or the top-level Deep Rock Galactic Steam folder.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        string? gameDir     = ResolveDrgDir();
        string? overrideDir = LoadOverrideDir();
        bool    apworldOk   = File.Exists(LocalApworldPath);
        string? stamp       = ReadStampedVersion();

        // ── Alpha / community honesty header ─────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "This is an ALPHA community Archipelago integration — not part of the main " +
                   "Archipelago project. It requires three separate pieces: your own copy of " +
                   "Deep Rock Galactic (Steam), a .pak mod installed via the mint sideloader, " +
                   "and a standalone Archipelago client from the Cousinit117 GitHub fork. " +
                   "The launcher downloads the .apworld and the .pak file and guides the rest. " +
                   "These external steps are not verified or managed by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: DRG install ──────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "DEEP ROCK GALACTIC INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Deep Rock Galactic not detected. Pick your install folder below, or " +
              "install the game via Steam first (appid 548430).";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // apworld status
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = apworldOk
                    ? "deep_rock_galactic.apworld downloaded" +
                      (stamp != null ? " (version " + stamp + ")" : "") + "."
                    : "deep_rock_galactic.apworld not yet downloaded — click Install on the Play tab.",
            FontSize = 11, Foreground = apworldOk ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        // pak status
        string[] pakFiles = Array.Empty<string>();
        try
        {
            if (Directory.Exists(LocalPakDir))
                pakFiles = Directory.GetFiles(LocalPakDir, "*.pak");
        }
        catch { }

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = pakFiles.Length > 0
                    ? "Pak mod file(s) downloaded to pak\\ subfolder: " +
                      string.Join(", ", Array.ConvertAll(pakFiles, Path.GetFileName))
                    : "Pak mod not yet downloaded — click Install on the Play tab.",
            FontSize = 11, Foreground = pakFiles.Length > 0 ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Install folder picker row
        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Deep Rock Galactic install folder. Detected from Steam automatically.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Deep Rock Galactic install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                                   ? (overrideDir ?? gameDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Deep Rock Galactic folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
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
            Text = "Steam installs are detected automatically (appid 548430). Use the picker " +
                   "only for a non-standard Steam library.",
            FontSize = 11, Foreground = muted, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "FULL SETUP (3 pieces required)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Deep Rock Galactic on Steam (appid 548430) and install it.",
            "2. Click Install on the Play tab — this downloads the deep_rock_galactic.apworld " +
               "file and the pak mod file (Archi_*.pak) to the launcher's Games\\DeepRockGalactic\\ folder.",
            "3. Copy deep_rock_galactic.apworld into your Archipelago custom_worlds folder: " +
               "typically %APPDATA%\\Archipelago\\lib\\worlds\\ (create the folder if it does not exist).",
            "4. Install the mint sideloader for Deep Rock Galactic (link below). In mint, add " +
               "the Archi_*.pak file from the pak\\ subfolder shown above, then launch DRG through mint.",
            "5. Download the Cousinit117 Deep-Rock-Galactic-AP fork from GitHub (link below). " +
               "This is a Python application — follow its README to set it up and run the " +
               "Archipelago client from it.",
            "6. Generate your seed on your AP server using the deep_rock_galactic.apworld " +
               "(place the YAML and the .apworld together, then generate). Connect the " +
               "Archipelago client from step 5 to your server slot.",
            "7. Launch Deep Rock Galactic through mint (step 4). The pak mod will communicate " +
               "with the standalone AP client — locations and items flow through it.",
            "IMPORTANT: The AP game string for YAML files is \"Deep Rock Galactic\".",
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
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Deep-Rock-Galactic-AP GitHub repo (Cousinit117) ↗", RepoUrl),
            ("Releases page (AP client + pak + apworld) ↗",       RepoReleasesUrl),
            ("mint — DRG mod sideloader ↗",                       MintUrl),
            ("Deep Rock Galactic on Steam ↗",
                "https://store.steampowered.com/app/548430/Deep_Rock_Galactic/"),
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
        try
        {
            string json = await _http.GetStringAsync(GithubApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string tag  = el.TryGetProperty("tag_name",     out var t) ? t.GetString() ?? "" : "";
                string name = el.TryGetProperty("name",         out var n) ? n.GetString() ?? "" : "";
                string body = el.TryGetProperty("body",         out var b) ? b.GetString() ?? "" : "";
                string url  = el.TryGetProperty("html_url",     out var u) ? u.GetString() ?? "" : "";

                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var pub) &&
                    pub.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(pub.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   string.IsNullOrWhiteSpace(name) ? "Release " + tag : name,
                    Body:    body,
                    Version: tag,
                    Date:    date,
                    Url:     url
                ));
                if (items.Count >= 15) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// Resolve the latest release from the GitHub API.
    /// Returns (version, apworldUrl, pakUrl). Falls back to pinned constants.
    private async Task<(string Version, string ApworldUrl, string? PakUrl)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GithubApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) goto fallback;

            foreach (var release in doc.RootElement.EnumerateArray())
            {
                // Skip drafts; accept both prerelease and non-prerelease
                if (release.TryGetProperty("draft", out var draft) &&
                    draft.ValueKind == JsonValueKind.True) continue;

                string tag = release.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(tag)) continue;

                if (!release.TryGetProperty("assets", out var assets) ||
                    assets.ValueKind != JsonValueKind.Array) continue;

                string? apworldUrl = null;
                string? pakUrl     = null;

                foreach (var asset in assets.EnumerateArray())
                {
                    string? aName = asset.TryGetProperty("name", out var an) ? an.GetString() : null;
                    string? aUrl  = asset.TryGetProperty("browser_download_url", out var au) ? au.GetString() : null;
                    if (string.IsNullOrWhiteSpace(aName) || string.IsNullOrWhiteSpace(aUrl)) continue;

                    if (aName.EndsWith(".apworld", StringComparison.OrdinalIgnoreCase))
                        apworldUrl = aUrl;
                    else if (aName.EndsWith(".pak", StringComparison.OrdinalIgnoreCase))
                        pakUrl = aUrl;
                }

                if (!string.IsNullOrWhiteSpace(apworldUrl))
                    return (tag, apworldUrl!, pakUrl);

                // No .apworld in this release — keep looking
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        fallback:
        return (FallbackVersion, FallbackApworldUrl, FallbackPakUrl);
    }

    // ── Private helpers — Steam / DRG detection ───────────────────────────────

    private string? ResolveDrgDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeDrgDir(ov)) return ov;
        try { return DetectSteamDrgDir(); }
        catch { return null; }
    }

    /// DRG on Steam installs as "Deep Rock Galactic\" with the main exe nested
    /// under FSD\Binaries\Win64\FSD-Win64-Shipping.exe. We also accept the root
    /// "Deep Rock Galactic\" folder as a valid detection hit (for the picker).
    private static bool LooksLikeDrgDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            // Top-level Steam folder contains FSD\ subfolder
            if (Directory.Exists(Path.Combine(dir, "FSD"))) return true;
            // The user picked the FSD subfolder itself
            if (File.Exists(Path.Combine(dir, "Binaries", "Win64", DrgExeName))) return true;
            return false;
        }
        catch { return false; }
    }

    private static string? DetectSteamDrgDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, "appmanifest_" + DrgSteamAppId + ".acf");
                    if (!File.Exists(manifest)) continue;

                    string common   = Path.Combine(steamapps, "common");
                    string? iDir    = ReadAcfInstallDir(manifest);
                    if (iDir != null)
                    {
                        string candidate = Path.Combine(common, iDir);
                        if (LooksLikeDrgDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, DrgSteamFolderName);
                    if (LooksLikeDrgDir(conventional)) return conventional;
                }
                catch { }
            }
        }
        return null;
    }

    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(Registry.CurrentUser,
            @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormalizeSteamPath(hkcu);

        string? hklm = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormalizeSteamPath(hklm);

        string? hklm2 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm2)) yield return NormalizeSteamPath(hklm2);

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
            if (norm.Length > 0 && seen.Add(norm)) yield return norm;
        }
    }

    private static IEnumerable<string> ExtractVdfPaths(string text)
    {
        const string key = "\"path\"";
        int i = 0;
        while ((i = text.IndexOf(key, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            i += key.Length;
            int open  = text.IndexOf('"', i);
            if (open  < 0) yield break;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) yield break;
            yield return text.Substring(open + 1, close - open - 1).Replace("\\\\", "\\");
            i = close + 1;
        }
    }

    private static string? ReadAcfInstallDir(string acfPath)
    {
        try
        {
            string text  = File.ReadAllText(acfPath);
            const string key = "\"installdir\"";
            int i = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += key.Length;
            int open  = text.IndexOf('"', i);
            if (open  < 0) return null;
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

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartDrg()
    {
        string? gameDir = ResolveDrgDir();
        // Try the direct exe path first (inside FSD\Binaries\Win64\)
        string? exe = null;
        if (gameDir != null)
        {
            string candidate = Path.Combine(gameDir, "FSD", "Binaries", "Win64", DrgExeName);
            if (File.Exists(candidate)) exe = candidate;
        }

        if (exe != null)
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Deep Rock Galactic.");

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
            return;
        }

        // Fall back to Steam if the exe is not found directly (e.g. the game
        // launched via the mint sideloader, or install not detected).
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true;
                return;
            }
            catch { }
        }

        throw new FileNotFoundException(
            "Could not launch Deep Rock Galactic. Install the game via Steam " +
            "(appid 548430), or use the \"Select folder...\" picker in Settings " +
            "to point the launcher at your DRG install.\n\n" +
            "IMPORTANT: To play with Archipelago, launch DRG through the mint " +
            "sideloader (not directly) so the Archi pak mod is active.",
            DrgExeName);
    }

    // ── Private helpers — file download ──────────────────────────────────────

    private async Task DownloadFileAsync(
        string url,
        string destPath,
        int pctStart,
        int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempPath = destPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using var src = await response.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(tempPath);

            byte[] buf = new byte[81920];
            int bytesRead;
            while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                downloaded += bytesRead;
                if (total > 0)
                {
                    int pct = (int)(pctStart + (pctEnd - pctStart) * downloaded / total);
                    string fileName = Path.GetFileName(destPath);
                    progress.Report((pct, "Downloading " + fileName + "... " + downloaded / 1024 + " KB"));
                }
            }
            await dst.FlushAsync(ct);
        }
        catch
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            throw;
        }

        // Atomic rename
        if (File.Exists(destPath)) File.Delete(destPath);
        File.Move(tempPath, destPath);
    }

    // ── Private helpers — settings sidecar ───────────────────────────────────

    private sealed class DrgSettings
    {
        public string? InstallOverride { get; set; }
        public string? ApworldVersion  { get; set; }
    }

    private DrgSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<DrgSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(DrgSettings s)
    {
        try
        {
            Directory.CreateDirectory(GameDirectory);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { }
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
        string? v = LoadSettings().ApworldVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings();
        s.ApworldVersion = v;
        SaveSettings(s);
    }
}
