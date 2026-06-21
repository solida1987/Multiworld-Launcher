using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;
using LauncherV2.Core;

// The launcher project sets BOTH UseWPF and UseWindowsForms=true, so several UI
// type names (Color, Brushes, Button, TextBox, HorizontalAlignment) collide
// between WPF and WinForms. The project's GlobalUsings.cs already aliases each of
// these to its WPF type globally, so this file relies on those — no local aliases
// (a local alias duplicating a global one is CS1537).

namespace LauncherV2.Plugins.TotalWarWarhammer3;

// ═══════════════════════════════════════════════════════════════════════════════
// TotalWarWarhammer3Plugin — detect / install / launch for
// "Total War: WARHAMMER III" (Creative Assembly, 2022, Steam 1142710)
// played with the TWW3 Archipelago integration by jordansds.
//
// ── CONFIRMED RESEARCH (2026-06-15, verified against GitHub + wiki) ───────────
//
//   * AP WORLD REPO: jordansds/Archipelago_TWW3_Alt
//     Latest release as of research date: v0.10.5 (2026-06-14).
//     The AP game string is "Total War Warhammer III" (verified against
//     worlds/tww3/world.py: `game = "Total War Warhammer III"`).
//     GameId here = "total_war_warhammer_3".
//     This is a COMMUNITY world (not in AP-main) delivered as tww3.apworld.
//     There is also an original implementation by SinthorasRage/Archipelago_TWW3;
//     this plugin targets jordansds/Archipelago_TWW3_Alt which is the active,
//     more feature-complete fork (sphere mode, conquest mode, v0.10.5+).
//     DO NOT use the "Archipelago Randomizer (Beta)" Steam Workshop mod — it is
//     for an outdated APWorld. The correct .pack is from the GitHub releases page.
//
//   * HOW THE INTEGRATION WORKS:
//     - A .pack file (TWW3 mod format) is placed in the game's data\ folder and
//       enabled in the TWW3 mod launcher. It adds the randomizer logic in-game.
//     - The Archipelago Python client "Total War Warhammer III Client"
//       (tww3client) lives inside the AP world package and connects to the AP
//       server. It is launched via ArchipelagoLauncher.exe in the Archipelago
//       Python environment. The launcher prompts the user to select the TWW3
//       install folder the first time.
//     - The client bridges the AP server and the in-game mod by writing faction
//       selection and item data into files the game can read.
//
//   * WHY ConnectsItself = true HERE:
//     The tww3client (AP Python client) holds the AP slot connection — not TWW3
//     itself and not our C# launcher. AP servers allow one connection per slot.
//     If our launcher held a second ApClient on the same slot, they would kick
//     each other off. ConnectsItself = true prevents that.
//
//   * OUR LAUNCHER'S ROLE:
//     1. Download and install the .pack file into the TWW3 data\ folder.
//     2. Download the tww3.apworld for the user to drop into Archipelago.
//     3. On AP launch: write session credentials to a handoff file, launch
//        TWW3 via Steam, open ArchipelagoLauncher.exe so the user can click
//        "Total War Warhammer III Client" themselves.
//
//   * RELEASE ASSETS (v0.10.5):
//       - Archipelago.pack  → place in TWW3/data/
//       - tww3.apworld       → double-click to install into Archipelago
//
//   * TWW3 STEAM APP ID: 1142710
//     Conventional Steam folder: "Total War WARHAMMER III"
//
//   * ARCHIPELAGO DEFAULT INSTALL: C:\ProgramData\Archipelago\ArchipelagoLauncher.exe
//
//   * DATAPACKAGE CHECKSUM: not published for this community world; left null.
//
// ── UNVERIFIED ("verify at build time") ─────────────────────────────────────
//   * TWW3 data folder: confirmed as <install>\data\ per README and modding docs.
//   * TWW3 exe name: confirmed as "Warhammer3.exe" per README ("select folder
//     where the Warhammer.exe file is saved"). We also probe "Warhammer3.exe"
//     and "TWW3.exe" as fallbacks.
//   * MOD LAUNCHER PACK ENABLE: The user must enable Archipelago.pack in the TWW3
//     mod launcher (or WH3-Mod-Manager) — we cannot do this programmatically.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class TotalWarWarhammer3Plugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string ApWorldOwner   = "jordansds";
    private const string ApWorldRepo    = "Archipelago_TWW3_Alt";
    private const string ApWorldRepoUrl = "https://github.com/jordansds/Archipelago_TWW3_Alt";

    private const string GH_RELEASES_URL    = "https://api.github.com/repos/jordansds/Archipelago_TWW3_Alt/releases";
    private const string GH_RELEASES_LATEST = "https://api.github.com/repos/jordansds/Archipelago_TWW3_Alt/releases/latest";

    private const string ArchipelagoSetupUrl = "https://archipelago.gg/tutorial/Archipelago/setup_en";
    private const string ArchipelagoSite     = "https://archipelago.gg";
    private const string ModManagerUrl       =
        "https://github.com/Shazbot/WH3-Mod-Manager/releases/tag/v2.16.14";

    private const string Tww3SteamAppId        = "1142710";
    private const string Tww3ConventionalFolder = "Total War WARHAMMER III";

    private static readonly string SteamRunTww3Url =
        "steam://rungameid/" + Tww3SteamAppId;

    private static readonly string ArchipelagoDefaultDir =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Archipelago");
    private const string ArchipelagoLauncherExeName = "ArchipelagoLauncher.exe";

    /// Name of the .pack asset on each GitHub release.
    private const string PackAssetName    = "Archipelago.pack";
    private const string ApWorldAssetName = "tww3.apworld";

    /// Version stamp file written by this plugin after a successful install.
    private const string VersionFileName = "tww3_ap_version.dat";

    private const string FallbackVersion = "0.10.5";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(10),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } },
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "total_war_warhammer_3";
    public string DisplayName => "Total War: WARHAMMER III";
    public string Subtitle    => "Native PC · Archipelago mod";

    /// EXACT AP game string — verified against worlds/tww3/world.py.
    public string ApWorldName => "Total War Warhammer III";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "total_war_warhammer_3.png");

    public string ThemeAccentColor => "#8B1A1A";   // dark blood-red / Chaos theme
    public string[] GameBadges     => new[] { "Steam · requires Archipelago" };

    public string Description =>
        "Total War: WARHAMMER III, Creative Assembly's epic grand strategy " +
        "fantasy war game, randomized through the TWW3 Archipelago integration " +
        "by jordansds. Units, buildings, and technologies become AP items. " +
        "Conquer settlements to send checks across the multiworld. " +
        "Two modes: conquest (owning N settlements simultaneously) and sphere " +
        "(diplomatic-radius-gated settlement checks). All faction starting " +
        "positions are shuffled each seed. A .pack mod file integrates the " +
        "randomizer directly into the game; a Python AP client (in the " +
        "Archipelago launcher) bridges the server connection. " +
        "You bring your own copy of TWW3 (Steam 1142710). " +
        "This is a community world (tww3.apworld) maintained separately from " +
        "the main Archipelago release.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = the .pack mod file is present in the TWW3 data folder.
    public bool IsInstalled => IsPackPresent();

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin stores downloaded files (apworld, version stamp).
    public string GameDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "Games", "TotalWarWarhammer3");

    private string VersionFilePath    => Path.Combine(GameDirectory, VersionFileName);
    private string ApWorldLocalPath   => Path.Combine(GameDirectory, ApWorldAssetName);
    private string PackStagingPath    => Path.Combine(GameDirectory, PackAssetName);

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _tww3Process;
    private Process? _apLauncherProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The tww3client (Archipelago Python client) owns the AP slot connection.
    // These events exist for interface compatibility (ConnectsItself = true).
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
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync("jordansds", "Archipelago_TWW3_Alt", ct));
        }
        catch
        {
            AvailableVersion = null; // contract: never throw on network failure
        } // contract: never throw on network failure
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((2, "Checking latest TWW3 Archipelago release..."));
        var (version, packUrl, apworldUrl) = await ResolveLatestReleaseAsync(ct);

        // Fast path if already up to date.
        if (IsInstalled
            && File.Exists(VersionFilePath)
            && (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim() == version)
        {
            InstalledVersion = version;
            progress.Report((100, $"TWW3 Archipelago mod {version} is already up to date."));
            return;
        }

        Directory.CreateDirectory(GameDirectory);

        // Download the .pack file.
        if (packUrl != null)
        {
            progress.Report((10, "Downloading Archipelago.pack..."));
            byte[] packBytes = await DownloadWithProgressAsync(packUrl, 10, 55, progress, ct);
            await File.WriteAllBytesAsync(PackStagingPath, packBytes, ct);
            progress.Report((55, "Archipelago.pack downloaded."));

            // Install the .pack into the TWW3 data folder if the game is detected.
            string? tww3Dir = DetectTww3Dir();
            if (tww3Dir != null)
            {
                string dataDir  = Path.Combine(tww3Dir, "data");
                string packDest = Path.Combine(dataDir, PackAssetName);
                try
                {
                    Directory.CreateDirectory(dataDir);
                    File.Copy(PackStagingPath, packDest, overwrite: true);
                    progress.Report((65,
                        $"Archipelago.pack installed to {dataDir}. " +
                        "Enable it in the TWW3 mod launcher or WH3-Mod-Manager before playing."));
                }
                catch (Exception ex)
                {
                    progress.Report((65,
                        $"Could not copy Archipelago.pack to {dataDir} automatically: " +
                        $"{ex.Message}. Copy it manually from {GameDirectory}."));
                }
            }
            else
            {
                progress.Report((65,
                    "Total War: WARHAMMER III not detected — copy Archipelago.pack " +
                    $"from {GameDirectory} into your TWW3 data\\ folder manually " +
                    "(e.g. steamapps\\common\\Total War WARHAMMER III\\data\\)."));
            }
        }
        else
        {
            progress.Report((55,
                "Could not find Archipelago.pack asset on the latest release. " +
                "Download it manually from " + ApWorldRepoUrl + "/releases."));
        }

        // Download the .apworld (user must install it into Archipelago themselves;
        // we cannot write to ProgramData\Archipelago without elevation).
        if (apworldUrl != null)
        {
            try
            {
                progress.Report((75, "Downloading tww3.apworld..."));
                byte[] apworld = await _http.GetByteArrayAsync(apworldUrl, ct);
                await File.WriteAllBytesAsync(ApWorldLocalPath, apworld, ct);
                progress.Report((85,
                    $"tww3.apworld saved to {ApWorldLocalPath}. " +
                    "Double-click it to install it into the Archipelago launcher, " +
                    @"or copy it to C:\ProgramData\Archipelago\lib\worlds\ manually."));
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                progress.Report((85,
                    "Could not download tww3.apworld — get it from " +
                    ApWorldRepoUrl + "/releases."));
            }
        }

        // Stamp version.
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;
        AvailableVersion = version;

        progress.Report((100,
            $"TWW3 Archipelago mod {version} ready. " +
            "Next steps: " +
            "(1) Enable Archipelago.pack in the TWW3 mod launcher (or WH3-Mod-Manager). " +
            "(2) Double-click tww3.apworld from " + GameDirectory + " to install it. " +
            "(3) In Archipelago launcher, run 'Generate Template Options', edit the " +
            "Total War Warhammer 3.yaml in the players\\ folder, then 'Generate'. " +
            "(4) Click Play — the launcher will open TWW3 and ArchipelagoLauncher.exe. " +
            "In the Archipelago launcher click 'Total War Warhammer III Client' and " +
            "connect. See Settings for the full guided steps."));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── Lifecycle — ValidateExistingInstall ──────────────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Total War: WARHAMMER III install folder.";

        if (LooksLikeTww3Dir(folder)) return null;

        // Try the conventional child folder in case the user picked the Steam library.
        try
        {
            string nested = Path.Combine(folder, Tww3ConventionalFolder);
            if (LooksLikeTww3Dir(nested)) return null;
        }
        catch { }

        return "That does not look like a Total War: WARHAMMER III installation. " +
               "Pick the folder that contains Warhammer3.exe " +
               "(usually steamapps\\common\\Total War WARHAMMER III).";
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    /// ConnectsItself = true: the tww3client (Archipelago Python client) holds the
    /// AP slot — the launcher must NOT also connect to the same slot.
    /// We launch TWW3 and open ArchipelagoLauncher.exe for the user to click
    /// "Total War Warhammer III Client" in it.
    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        WriteCredentialsHandoff(session);
        StartTww3();

        string? apLauncher = FindArchipelagoLauncher();
        if (apLauncher != null)
        {
            try
            {
                _apLauncherProcess = Process.Start(new ProcessStartInfo
                {
                    FileName         = apLauncher,
                    WorkingDirectory = Path.GetDirectoryName(apLauncher)!,
                    UseShellExecute  = true,
                });
            }
            catch { /* non-fatal — user can open Archipelago themselves */ }
        }
        else
        {
            TryOpenUrl(ArchipelagoSetupUrl);
        }

        return Task.CompletedTask;
    }

    /// TWW3 can be played in normal campaign mode without AP.
    public bool SupportsStandalone => true;

    /// The tww3client (Archipelago Python client) owns the slot connection.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartTww3();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _tww3Process?.Kill(entireProcessTree: true); } catch { }
        try { _apLauncherProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning          = false;
        _tww3Process       = null;
        _apLauncherProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // Items are routed through the tww3client → TWW3 mod bridge.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The tww3client and in-game mod handle their own AP status.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Total War: WARHAMMER III Archipelago uses the tww3client " +
                   "(Python AP client) and a .pack mod file. The launcher installs " +
                   "both and guides you through the remaining steps — some require " +
                   "the Archipelago Python installation. " +
                   "Do NOT use the 'Archipelago Randomizer (Beta)' Steam Workshop " +
                   "mod — it targets an outdated APWorld.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: install status ────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "TOTAL WAR: WARHAMMER III INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 0, 0, 8),
        });

        string? tww3Dir     = DetectTww3Dir();
        bool    packOk      = IsPackPresent();
        bool    apworldOk   = File.Exists(ApWorldLocalPath);
        string? apLauncher  = FindArchipelagoLauncher();

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = tww3Dir != null
                ? "Total War: WARHAMMER III detected: " + tww3Dir
                : "Total War: WARHAMMER III not detected — install it on Steam (1142710).",
            FontSize = 11, Foreground = tww3Dir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = packOk
                ? "Archipelago.pack installed in TWW3 data\\ folder."
                : "Archipelago.pack not found in TWW3 data\\ folder — use Install on the Play tab.",
            FontSize = 11, Foreground = packOk ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = apworldOk
                ? $"tww3.apworld downloaded to {ApWorldLocalPath}. Double-click it to install."
                : "tww3.apworld not yet downloaded — use Install on the Play tab.",
            FontSize = 11, Foreground = apworldOk ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = apLauncher != null
                ? "Archipelago detected: " + apLauncher
                : "Archipelago not detected — install it from archipelago.gg (link below). " +
                  "The tww3client runs from within the Archipelago Python environment.",
            FontSize = 11, Foreground = apLauncher != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 4, 0, 8),
        });

        foreach (string step in new[]
        {
            "1. Own Total War: WARHAMMER III on Steam (1142710). Install it if you have not.",
            "2. Install Archipelago from archipelago.gg.",
            "3. Click Install on the Play tab: the launcher downloads Archipelago.pack " +
               "into your TWW3 data\\ folder and downloads tww3.apworld.",
            "4. Double-click tww3.apworld (saved in " + GameDirectory + ") to install " +
               "it into the Archipelago launcher. If double-click does not work, copy it " +
               @"to C:\ProgramData\Archipelago\lib\worlds\ manually.",
            "5. In ArchipelagoLauncher.exe, run 'Generate Template Options'. Find " +
               "Total War Warhammer 3.yaml in the Archipelago folder, copy it to the " +
               "players\\ sub-folder, and edit it to configure your settings.",
            "6. Enable Archipelago.pack in the TWW3 mod launcher before starting the game. " +
               "Do NOT enable the Steam Workshop 'Archipelago Randomizer (Beta)' mod.",
            "7. Click Play in this launcher. TWW3 and ArchipelagoLauncher.exe will both " +
               "open. In the Archipelago Launcher click 'Total War Warhammer III Client', " +
               "select your TWW3 install folder when prompted, and enter your server, " +
               "port, slot name, and password. Then start a new game in TWW3.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Section: links ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });

        foreach (var (label, url) in new[]
        {
            ("TWW3 Archipelago (jordansds) ↗",  ApWorldRepoUrl),
            ("WH3-Mod-Manager ↗",               ModManagerUrl),
            ("Archipelago Official ↗",           ArchipelagoSite),
            ("Archipelago Setup Guide ↗",        ArchipelagoSetupUrl),
        })
        {
            string u   = url;
            var    btn = new System.Windows.Controls.Button
            {
                Content             = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding             = new Thickness(0, 2, 0, 2),
                Background          = System.Windows.Media.Brushes.Transparent,
                BorderThickness     = new Thickness(0),
                FontSize            = 12,
                Margin              = new Thickness(0, 0, 0, 4),
                Foreground          = new System.Windows.Media.SolidColorBrush(
                                          System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor              = System.Windows.Input.Cursors.Hand,
            };
            btn.Click += (_, _) => TryOpenUrl(u);
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
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d)
                    && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: el.TryGetProperty("tag_name", out var t)
                                ? NormalizeTag(t.GetString()) ?? "" : "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — install detection ───────────────────────────────────

    /// True when Archipelago.pack is present in the TWW3 data folder.
    private static bool IsPackPresent()
    {
        try
        {
            string? tww3Dir = DetectTww3Dir();
            if (tww3Dir == null) return false;
            return File.Exists(Path.Combine(tww3Dir, "data", PackAssetName));
        }
        catch { return false; }
    }

    /// True when a folder looks like a TWW3 install.
    private static bool LooksLikeTww3Dir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            // Check for any of the known TWW3 executables.
            return File.Exists(Path.Combine(dir, "Warhammer3.exe"))
                || File.Exists(Path.Combine(dir, "TWW3.exe"))
                || Directory.Exists(Path.Combine(dir, "data"));
        }
        catch { return false; }
    }

    // ── Private helpers — Steam / install detection ───────────────────────────

    private static string? DetectTww3Dir()
    {
        try { return DetectSteamAppDir(Tww3SteamAppId, Tww3ConventionalFolder, LooksLikeTww3Dir); }
        catch { return null; }
    }

    private static string? FindArchipelagoLauncher()
    {
        string standard = Path.Combine(ArchipelagoDefaultDir, ArchipelagoLauncherExeName);
        if (File.Exists(standard)) return standard;

        try
        {
            string? regPath = ReadRegistryString(Registry.CurrentUser,
                @"Software\Archipelago", "InstallPath");
            if (!string.IsNullOrWhiteSpace(regPath))
            {
                string candidate = Path.Combine(regPath, ArchipelagoLauncherExeName);
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }

        return null;
    }

    // ── Private helpers — release resolution ─────────────────────────────────

    /// Resolve the latest release: version + Archipelago.pack URL + tww3.apworld URL.
    private async Task<(string Version, string? PackUrl, string? ApWorldUrl)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST, ct);
            using var doc = JsonDocument.Parse(json);

            string? version    = doc.RootElement.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString()) : null;
            string? packUrl    = null;
            string? apworldUrl = null;

            if (doc.RootElement.TryGetProperty("assets", out var assets)
                && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u)
                                   ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    if (packUrl == null
                        && name.EndsWith(".pack", StringComparison.OrdinalIgnoreCase))
                        packUrl = url;

                    if (apworldUrl == null
                        && name.EndsWith(".apworld", StringComparison.OrdinalIgnoreCase))
                        apworldUrl = url;
                }
            }

            return (version ?? FallbackVersion, packUrl, apworldUrl);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (FallbackVersion, null, null); }
    }

    // ── Private helpers — version normalization ───────────────────────────────

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..] : tag;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartTww3()
    {
        string? tww3Dir = DetectTww3Dir();
        if (tww3Dir != null)
        {
            foreach (string exeName in new[] { "Warhammer3.exe", "TWW3.exe" })
            {
                string candidate = Path.Combine(tww3Dir, exeName);
                if (!File.Exists(candidate)) continue;
                try
                {
                    var proc = Process.Start(new ProcessStartInfo
                    {
                        FileName         = candidate,
                        WorkingDirectory = tww3Dir,
                        UseShellExecute  = true,
                    });
                    if (proc != null)
                    {
                        _tww3Process = proc;
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
                }
                catch { }
            }
        }

        // Fallback: launch via Steam URI.
        try
        {
            Process.Start(new ProcessStartInfo(SteamRunTww3Url) { UseShellExecute = true });
            IsRunning = true; // best-effort; Steam owns the process
        }
        catch
        {
            throw new FileNotFoundException(
                "Could not find Total War: WARHAMMER III or Steam. " +
                "Install TWW3 via Steam (1142710) and make sure Steam is running.",
                "Warhammer3.exe");
        }
    }

    /// Write session credentials to a JSON handoff file so the user can easily
    /// enter them into the tww3client connection dialog.
    private static void WriteCredentialsHandoff(ApSession session)
    {
        try
        {
            string dir  = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TWW3_Archipelago");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "tww3_ap_credentials.json");
            var creds = new Dictionary<string, string>
            {
                ["server"]   = session.ServerUri,
                ["slot"]     = session.SlotName,
                ["password"] = session.Password ?? "",
                ["game"]     = session.Game,
            };
            File.WriteAllText(path,
                JsonSerializer.Serialize(creds,
                    new JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(false));
        }
        catch { /* best-effort — never block the launch */ }
    }

    // ── Private helpers — Steam detection ────────────────────────────────────

    private static string? DetectSteamAppDir(
        string appId, string conventionalFolder, Func<string, bool>? validator = null)
    {
        Func<string, bool> looksValid = validator ?? (d =>
        {
            try { return Directory.Exists(d) && Directory.EnumerateFileSystemEntries(d).Any(); }
            catch { return false; }
        });

        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{appId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common    = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (looksValid(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, conventionalFolder);
                    if (looksValid(conventional)) return conventional;
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

        string? hklm64 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm64)) yield return NormalizeSteamPath(hklm64);

        string? progX86 = SafeFolder(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    private static string NormalizeSteamPath(string p) => p.Replace('/', '\\').TrimEnd('\\');

    private static IEnumerable<string> SteamLibraryRoots(string steamRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(steamRoot)) yield return steamRoot;

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string text;
        try { text = File.ReadAllText(vdf); } catch { yield break; }

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
            if (open < 0) yield break;
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
            string text = File.ReadAllText(acfPath);
            const string key = "\"installdir\"";
            int i = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += key.Length;
            int open  = text.IndexOf('"', i);
            if (open < 0) return null;
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

    private static string? SafeFolder(Environment.SpecialFolder f)
    {
        try { return Environment.GetFolderPath(f); } catch { return null; }
    }

    // ── Private helpers — download with progress ──────────────────────────────

    private async Task<byte[]> DownloadWithProgressAsync(
        string url, int pctStart, int pctEnd,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        using var response = await _http.GetAsync(url,
            HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total      = response.Content.Headers.ContentLength ?? -1;
        long downloaded = 0;
        using var ms    = new System.IO.MemoryStream((int)Math.Max(0, total));
        await using var src = await response.Content.ReadAsStreamAsync(ct);

        var buf = new byte[81920];
        int read;
        while ((read = await src.ReadAsync(buf, ct)) > 0)
        {
            ms.Write(buf, 0, read);
            downloaded += read;
            if (total > 0)
            {
                int pct = pctStart + (int)((pctEnd - pctStart) * downloaded / total);
                progress.Report((pct,
                    $"Downloading... {downloaded / 1_000}KB / {total / 1_000}KB"));
            }
        }
        return ms.ToArray();
    }

    // ── Private helpers — misc ────────────────────────────────────────────────

    private static void TryOpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }
}
