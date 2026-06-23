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

namespace LauncherV2.Plugins.Hacknet;

// ═══════════════════════════════════════════════════════════════════════════════
// HacknetPlugin — install / launch for "Hacknet" (Team Fractal Alligator, 2015)
// played through the HacknetAP extension by AutumnRivers, which uses Hacknet's
// built-in Extension system to integrate the game with the Archipelago multiworld
// server. This is a NATIVE "ConnectsItself" integration: the Extension loaded
// inside Hacknet itself speaks to the AP server — no emulator, no Lua bridge, no
// launcher-held ApClient on the slot.
//
// Hacknet Extensions are XML-based add-on campaigns loaded from the game's own
// Extensions/ folder via the Extensions menu on the main screen. The AP Extension
// by AutumnRivers (GitHub: AutumnRivers/HacknetAP) turns hacking contracts, data
// steal missions, and story beats into multiworld location checks, and delivers
// items from other worlds as new software tools or access credentials in the
// player's terminal.
//
// ── HONEST REALITY CHECK (plugin-creation date 2026-06-19, verified online) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned copy
// of Hacknet (Steam appid 365450 — verified against the Steam store page; also on
// itch.io / GOG), and Archipelago support is delivered as an Extension dropped
// into the game's Extensions/ folder. The honest integration ceiling is "automate
// what is possible, guide the irreducible parts."
//
//   * THE AP WORLD game string is "Hacknet" — UNVERIFIED (not independently
//     confirmed against worlds/__init__.py at the time of writing; verify against
//     the HacknetAP repo or the AP game list before treating as authoritative).
//     GameId = "hacknet".
//
//   * THE EXTENSION repo is AutumnRivers/HacknetAP (verified against the GitHub
//     search + AP community game list). The Extension is distributed as a release
//     zip on that repo's Releases page. Pinned fallback version 1.0.0 is used
//     when the GitHub API is unreachable.
//
//   * CONNECTION is made through Hacknet's Extension system. When the player
//     loads the HacknetAP Extension from Hacknet's Extensions menu, the Extension
//     handles the AP server connection internally. This launcher cannot pre-fill
//     or hold the connection — ConnectsItself = true.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Hacknet install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath, and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Hacknet via appmanifest_365450.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and takes
//      precedence; it is validated (must contain Hacknet.exe) and persisted in
//      this plugin's OWN sidecar
//      (Games/ROMs/hacknet/hacknet_launcher.json) — Core/SettingsStore is NOT
//      modified. (itch.io / GOG / other stores work via the manual picker.)
//   2. INSTALL/UPDATE (best effort) = download the HacknetAP Extension zip from
//      the AutumnRivers/HacknetAP GitHub release and extract it into
//      <Hacknet>/Extensions/ so the Extension folder lands at
//      Extensions/HacknetAP/ (or whatever folder the release zip uses). "Installed"
//      is judged by the presence of an Extensions/HacknetAP directory or a .xml
//      file with "hacknetap" in its name anywhere under Extensions/.
//   3. LAUNCH = run Hacknet.exe from the detected/override install; if the exe
//      cannot be found but Steam is present, fall back to steam://rungameid/365450.
//      ConnectsItself = true. SupportsStandalone = true.
//
// ── DEFENSIVE / UNVERIFIED ────────────────────────────────────────────────────
//   * ApWorldName "Hacknet" is UNVERIFIED — verify against worlds/__init__.py or
//     the HacknetAP repo before treating as authoritative.
//   * Steam library parsing is defensive: a hand-written tolerant VDF scan; any
//     failure degrades to "Hacknet not found" rather than throwing.
//   * No plaintext AP password is ever written by this plugin.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class HacknetPlugin : IGamePlugin
{
    // ── Constants — HacknetAP extension (AutumnRivers, verified 2026-06-19) ────
    private const string MOD_OWNER = "AutumnRivers";
    private const string MOD_REPO  = "HacknetAP";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string SetupGuideUrl   = "https://archipelago.gg/games/Hacknet/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Hacknet appid 365450 (verified against the Steam store page).
    private const string HnAppId = "365450";
    private static readonly string SteamRunUrl = $"steam://rungameid/{HnAppId}";

    /// The standard Steam install sub-folder name for Hacknet.
    private const string SteamCommonFolderName = "Hacknet";

    /// The base-game executable name (verified — Windows exe is Hacknet.exe).
    private const string HnExeName = "Hacknet.exe";

    /// The expected Extension folder name under <Hacknet>/Extensions/.
    private const string ExtensionFolderName = "HacknetAP";

    /// Pinned fallback version when the GitHub API is unreachable.
    private const string FallbackVersion = "1.0.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "hacknet";
    public string DisplayName => "Hacknet";
    public string Subtitle    => "PC · Archipelago extension";

    /// UNVERIFIED — check against worlds/__init__.py or the HacknetAP repo
    /// before treating as authoritative.
    public string ApWorldName => "Hacknet";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "hacknet.png");

    public string ThemeAccentColor => "#00C853";   // terminal green
    public string[] GameBadges     => new[] { "Requires Hacknet on Steam" };

    public string Description =>
        "Hacknet (2015) is a terminal-based hacking simulation by Team Fractal Alligator, " +
        "placing you as the successor to a dead hacker whose automated system has pulled " +
        "you into a web of corporate intrigue, ghost ships, and rogue AI. Every command " +
        "is a real terminal command. The Archipelago extension by AutumnRivers integrates " +
        "Hacknet's extension system: hacking contracts, data steals, and story missions " +
        "become multiworld location checks, and items from other worlds arrive as new " +
        "software tools or access credentials in your terminal. The extension loads " +
        "through Hacknet's built-in Extensions menu — the launcher downloads and installs " +
        "it into your Extensions folder. Requires: your own legally-owned copy of Hacknet " +
        "on Steam.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the HacknetAP Extension folder or a matching .xml file
    /// is present under the detected/override Hacknet install's Extensions tree.
    public bool IsInstalled => FindInstalledExtension() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and working files. The actual Extension
    /// is extracted INTO the Hacknet install's Extensions folder, not here.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Hacknet");

    /// This plugin's OWN settings sidecar. Per the brief, lives under
    /// Games/ROMs/hacknet/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "hacknet_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The HacknetAP Extension reports checks/items/goal to the AP server itself
    // — the launcher relays nothing. These exist for interface compatibility
    // (ConnectsItself = true).
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
            InstalledVersion = FindInstalledExtension() != null
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
                await GitHubHelper.FetchLatestTagAsync(MOD_OWNER, MOD_REPO, ct));
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
        // 0. We need a Hacknet install to drop the Extension into.
        progress.Report((2, "Locating your Hacknet installation..."));
        string? hnDir = ResolveHacknetDir();
        if (hnDir == null)
            throw new InvalidOperationException(
                "Could not find a Hacknet installation. Open this game's Settings " +
                "and pick your Hacknet folder (the one containing Hacknet.exe), " +
                "or install Hacknet via Steam first. The Archipelago Extension is " +
                "added on top of your own copy of the game.");

        // 1. Resolve the latest Extension release (pinned fallback).
        progress.Report((6, "Checking the latest HacknetAP release..."));
        var (version, zipUrl) = await ResolveLatestExtensionAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the HacknetAP Extension download on GitHub. " +
                "Check your internet connection, or download it manually from " +
                ModRepoUrl + "/releases and extract it into your Hacknet\\Extensions\\ folder.");

        // 2. Download + extract the Extension zip INTO <Hacknet>/Extensions/.
        string extensionsDir = Path.Combine(hnDir, "Extensions");
        await DownloadAndExtractExtensionAsync(zipUrl, version, extensionsDir, progress, ct);

        // 3. Stamp the version in our sidecar so the tile can show it.
        WriteStampedVersion(version);
        InstalledVersion = version;

        bool extOk = FindInstalledExtension() != null;
        progress.Report((100,
            $"Installed HacknetAP Extension {version} into your Extensions folder" +
            (extOk ? "." : " (verify the files landed).") +
            " To play: launch Hacknet, open the Extensions menu, select HacknetAP, " +
            "and the Extension will connect to your Archipelago server. " +
            "(This launcher cannot pre-fill the connection — it is handled by the " +
            "Extension itself.)"));
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
        // HONEST: the AP server connection for Hacknet is handled entirely by the
        // HacknetAP Extension, which is loaded from Hacknet's Extensions menu.
        // There is no documented command-line / config prefill this launcher can
        // apply. So launching from this tile just starts the game; the user selects
        // the Extension in-game and the Extension handles the connection.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the Extension is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartHacknet();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Hacknet runs perfectly well.
    public bool SupportsStandalone => true;

    /// The HacknetAP Extension owns the slot connection (see header).
    public bool ConnectsItself => true;

    public bool ChecksImplemented => false;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartHacknet();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        // No plaintext AP password is ever written to disk by this plugin.
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (see header) ────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The HacknetAP Extension receives items from the AP server directly;
        // there is nothing for the launcher to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The Extension renders its own AP status in-game.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Used by the Settings folder picker: a valid Hacknet folder must contain
    /// Hacknet.exe. Returns null when acceptable, else a short human-readable reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Hacknet install folder.";

        if (LooksLikeHacknetDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeHacknetDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Hacknet installation. Pick the folder " +
               "that contains Hacknet.exe (for Steam this is usually " +
               @"...\steamapps\common\Hacknet).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Hacknet is your own game (Steam / itch.io / GOG) with the " +
                   "HacknetAP Extension added on top. The launcher detects your " +
                   "Steam install and can download and install the Extension into " +
                   "your Extensions folder. You load the Extension from Hacknet's " +
                   "built-in Extensions menu — the game then connects to your " +
                   "Archipelago server itself. The ApWorldName \"Hacknet\" is " +
                   "unverified — check the HacknetAP repo if generation fails.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "HACKNET INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? hnDir       = ResolveHacknetDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg = hnDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + hnDir
                : "Detected Steam install: " + hnDir)
            : "Hacknet not detected. Pick your install folder below, or install " +
              "Hacknet via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = hnDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        // Extension status line
        string? extPath = FindInstalledExtension();
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = extPath != null
                    ? "HacknetAP Extension found: " + extPath
                    : "HacknetAP Extension not found in Extensions\\ yet (use " +
                      "Install on the Play tab, or install it manually).",
            FontSize = 11, Foreground = extPath != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? hnDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Hacknet install folder (the one containing Hacknet.exe). " +
                          "Detected from Steam automatically; set it here to override " +
                          "(itch.io, GOG, or a non-standard Steam library).",
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
                Title            = "Select your Hacknet install folder (contains Hacknet.exe)",
                InitialDirectory = Directory.Exists(overrideDir ?? hnDir ?? "")
                                   ? (overrideDir ?? hnDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateExistingInstall(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Hacknet folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // If the user picked the "common" parent, descend into the game folder.
                if (!LooksLikeHacknetDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeHacknetDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 365450). Use this " +
                   "picker for itch.io, GOG, or a non-standard Steam library.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection note ───────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (handled by the Extension)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Launch Hacknet and open the Extensions menu from the main screen. " +
                   "Select the HacknetAP Extension and load it. The Extension handles " +
                   "the Archipelago server connection itself — enter your Server Address, " +
                   "Port, Name, and Password when prompted inside the Extension. This " +
                   "launcher cannot pre-fill the connection.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Hacknet (on Steam, itch.io, or GOG). Install it if you have not. " +
                "Use the picker above if it was not detected.",
            "2. Use the Install button on the Play tab. The launcher downloads the " +
                "HacknetAP Extension from GitHub and extracts it into your " +
                "Hacknet\\Extensions\\ folder automatically.",
            "3. Alternative (manual): download the Extension zip from the HacknetAP " +
                "GitHub releases page (link below), and extract the HacknetAP folder " +
                "into your Hacknet\\Extensions\\ directory.",
            "4. Launch Hacknet. On the main screen, click Extensions and select " +
                "HacknetAP to load the Extension.",
            "5. Follow the in-Extension prompts to enter your Archipelago server " +
                "details (Server Address, Port, Name, optional Password) and connect. " +
                "The Extension handles all AP communication.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("HacknetAP Extension (GitHub) ↗",  ModRepoUrl),
            ("HacknetAP Releases (download) ↗", ModRepoUrl + "/releases"),
            ("Hacknet AP Guide ↗",               SetupGuideUrl),
            ("Archipelago Official ↗",           ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label, HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0), FontSize = 12,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => { try { Process.Start(
                new ProcessStartInfo(u) { UseShellExecute = true }); } catch { } };
            panel.Children.Add(btn);
        }
        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_URL, ct);
            using var doc  = JsonDocument.Parse(json);
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

    /// "v1.0.0" → "1.0.0" when a leading 'v' decorates a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest Extension release: version + the zip download URL.
    /// Prefers any .zip asset; falls back to the pinned 1.0.0 URL when the API
    /// is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestExtensionAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString())
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? preferred = null;   // HacknetAP*.zip preferred
                string? anyZip    = null;   // any .zip fallback
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null && lower.Contains("hacknet"))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        // Pinned fallback: try the conventional release asset name.
        string fallbackUrl =
            $"{ModRepoUrl}/releases/download/{FallbackVersion}/HacknetAP.zip";
        return (FallbackVersion, fallbackUrl);
    }

    // ── Private helpers — Steam / Hacknet detection ───────────────────────────

    /// The Hacknet install dir to use: the override (if set and valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolveHacknetDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeHacknetDir(ov))
            return ov;

        try { return DetectSteamHacknetDir(); }
        catch { return null; }
    }

    /// A folder "looks like" Hacknet if it has Hacknet.exe.
    private static bool LooksLikeHacknetDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, HnExeName));
        }
        catch { return false; }
    }

    /// Detect the Steam Hacknet install: read the Steam root from the registry,
    /// gather all library roots from libraryfolders.vdf, and find the one whose
    /// appmanifest_365450.acf exists → steamapps\common\Hacknet.
    private static string? DetectSteamHacknetDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{HnAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeHacknetDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeHacknetDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
            }
        }
        return null;
    }

    /// Candidate Steam install roots from the registry.
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

        // Last-ditch conventional location.
        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    /// Steam stores its SteamPath with forward slashes; normalize for Path APIs.
    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    /// All Steam library roots: the Steam root itself plus every "path" entry in
    /// steamapps\libraryfolders.vdf.
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
            if (norm.Length > 0 && seen.Add(norm))
                yield return norm;
        }
    }

    /// Pull every  "path"   "<value>"  pair out of a libraryfolders.vdf body.
    private static IEnumerable<string> ExtractVdfPaths(string text)
    {
        const string key = "\"path\"";
        int i = 0;
        while ((i = text.IndexOf(key, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            i += key.Length;
            int open = text.IndexOf('"', i);
            if (open < 0) yield break;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) yield break;

            string raw = text.Substring(open + 1, close - open - 1);
            yield return raw.Replace("\\\\", "\\");
            i = close + 1;
        }
    }

    /// Read the "installdir" value from an appmanifest_*.acf.
    private static string? ReadAcfInstallDir(string acfPath)
    {
        try
        {
            string text = File.ReadAllText(acfPath);
            const string key = "\"installdir\"";
            int i = text.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += key.Length;
            int open = text.IndexOf('"', i);
            if (open < 0) return null;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) return null;
            return text.Substring(open + 1, close - open - 1).Replace("\\\\", "\\");
        }
        catch { return null; }
    }

    /// Safe registry string read; null on any failure.
    private static string? ReadRegistryString(RegistryKey hive, string subKey, string valueName)
    {
        try
        {
            using RegistryKey? k = hive.OpenSubKey(subKey);
            return k?.GetValue(valueName) as string;
        }
        catch { return null; }
    }

    // ── Private helpers — installed-extension detection ───────────────────────

    /// Find the HacknetAP Extension under the detected/override Hacknet install's
    /// Extensions tree. Checks for an Extensions/HacknetAP directory first, then
    /// for any .xml file with "hacknetap" in its name anywhere under Extensions/.
    /// Returns the matched path (directory or file) or null.
    private string? FindInstalledExtension()
    {
        try
        {
            string? hn = ResolveHacknetDir();
            if (hn == null) return null;
            string extensionsDir = Path.Combine(hn, "Extensions");
            if (!Directory.Exists(extensionsDir)) return null;

            // Primary check: the HacknetAP subfolder.
            string expectedFolder = Path.Combine(extensionsDir, ExtensionFolderName);
            if (Directory.Exists(expectedFolder)) return expectedFolder;

            // Secondary check: any .xml whose name contains "hacknetap" (case-insensitive)
            // anywhere under Extensions/ — handles renamed or flat installs.
            foreach (string xml in Directory.EnumerateFiles(extensionsDir, "*.xml", SearchOption.AllDirectories))
            {
                if (Path.GetFileNameWithoutExtension(xml)
                        .Contains("hacknetap", StringComparison.OrdinalIgnoreCase))
                    return xml;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Start Hacknet: prefer the exe in the detected/override install; if that
    /// cannot be found but Steam is present, fall back to the steam:// URL.
    private void StartHacknet()
    {
        string? hn  = ResolveHacknetDir();
        string? exe = hn != null ? Path.Combine(hn, HnExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = hn!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Hacknet.");

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
            catch { /* some processes don't expose Exited — non-fatal */ }
            return;
        }

        // Fall back to Steam if we at least know Steam is installed.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { /* fall through to the error below */ }
        }

        throw new FileNotFoundException(
            "Could not find Hacknet.exe. Open this game's Settings and pick your " +
            "Hacknet install folder, or install Hacknet via Steam.",
            HnExeName);
    }

    // ── Private helpers — download / extract the Extension ────────────────────

    /// Download the Extension zip and extract it into <Hacknet>/Extensions/.
    /// If the zip contains a single top-level folder (e.g. HacknetAP/), it is
    /// extracted directly into Extensions/ so the folder lands at
    /// Extensions/HacknetAP/. If it is already a flat collection of files, they
    /// are placed into Extensions/HacknetAP/. Existing Extension files are
    /// overwritten; sibling Extensions are preserved.
    private async Task DownloadAndExtractExtensionAsync(
        string zipUrl,
        string version,
        string extensionsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"hacknetap-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"hacknetap-x-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading HacknetAP Extension {version}..."));
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
                        progress.Report((pct, $"Downloading HacknetAP Extension... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((75, "Installing the Extension into your Extensions folder..."));
            Directory.CreateDirectory(extensionsDir);
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // Work out where to merge into.
            // Case A: the zip has a single top-level folder (e.g. "HacknetAP/") → extract
            //         that folder into Extensions/ directly, giving Extensions/HacknetAP/.
            // Case B: the zip is flat (no single top-level wrapper) → place everything into
            //         Extensions/HacknetAP/ ourselves.
            string[] topDirs  = Directory.GetDirectories(tempExtract);
            string[] topFiles = Directory.GetFiles(tempExtract);

            string destExtensionDir;
            string mergeRoot;

            if (topDirs.Length == 1 && topFiles.Length == 0)
            {
                // Case A: single wrapper folder — merge the folder INTO Extensions/.
                // This lands the folder at Extensions/<wrapperfolder-name>/.
                mergeRoot          = topDirs[0];
                string folderName  = Path.GetFileName(mergeRoot);
                destExtensionDir   = Path.Combine(extensionsDir, folderName);
            }
            else
            {
                // Case B: flat — we create Extensions/HacknetAP/ ourselves.
                mergeRoot          = tempExtract;
                destExtensionDir   = Path.Combine(extensionsDir, ExtensionFolderName);
            }

            Directory.CreateDirectory(destExtensionDir);
            MergeDirectory(mergeRoot, destExtensionDir);

            progress.Report((90, "Extension files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Recursively copy a directory tree INTO an existing destination, overwriting
    /// individual files but preserving any sibling files already there.
    private static void MergeDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            try { File.Copy(file, dst, overwrite: true); }
            catch { /* locked file (game open?) — skip; user can retry with game closed */ }
        }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class HnSettings
    {
        public string? InstallOverride { get; set; }
        public string? ExtensionVersion { get; set; }
    }

    private HnSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<HnSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(HnSettings s)
    {
        try
        {
            Directory.CreateDirectory(SettingsSidecarDir);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    private string? LoadOverrideDir()
    {
        string? p = LoadSettings().InstallOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }
    private void SaveOverrideDir(string p) { var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s); }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ExtensionVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    private void WriteStampedVersion(string v) { var s = LoadSettings(); s.ExtensionVersion = v; SaveSettings(s); }
}
