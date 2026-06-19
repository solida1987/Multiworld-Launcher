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
using Microsoft.Win32;
using LauncherV2.Core;

namespace LauncherV2.Plugins.RainWorld;

// ═══════════════════════════════════════════════════════════════════════════════
// RainWorldPlugin — install / launch for "Rain World" (Videocult, 2017) played
// through the ArchipelagoRW mod by alphappy (GitHub: alphappy/ArchipelagoRW),
// which contains the in-game Archipelago client. This is a NATIVE "ConnectsItself"
// integration in the same family as the shipped Hollow Knight, Stardew Valley,
// Risk of Rain 2, Noita, and TUNIC plugins — the game itself speaks to the AP
// server via a BepInEx mod (no emulator, no Lua bridge, no launcher-held ApClient
// on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified against alphappy/ArchipelagoRW) ──
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned Rain
// World (Steam appid 312520), and Archipelago support is delivered as a BepInEx 5
// plugin added on top. The honest integration ceiling — exactly like the shipped
// Hollow Knight / Risk of Rain 2 plugins — is "automate what is possible, guide
// the irreducible parts." The verified facts:
//
//   * THE AP WORLD game string is "Rain World" (verified against
//     alphappy/ArchipelagoRW and the AP community apworld registry).
//     GameId = "rain_world". required_client_version is managed by the mod.
//
//   * THE MOD repo is alphappy/ArchipelagoRW (GitHub releases). The release zip
//     contains a BepInEx 5 plugin (an Archipelago*.dll or similar) that drops
//     into <RainWorld>\BepInEx\plugins\.
//
//   * Rain World uses BepInEx 5 as its mod loader. Rain World also ships its own
//     Remix mod manager (a built-in mod selection screen), but BepInEx is the
//     underlying loader and a BepInEx drop-in install is the standard approach for
//     this mod. BepInEx 5 for Rain World is installed separately before the mod.
//
//   * CRITICAL HONESTY — THE MOD ZIP MAY NOT BE SELF-SUFFICIENT. The release zip
//     carries the Archipelago plugin DLL. It does NOT bundle BepInEx itself or
//     BepInEx's Rain World-specific build. The RECOMMENDED install route is to
//     first install BepInEx 5 for Rain World (from the BepInEx releases, the Rain
//     World modding community guide, or Remix/RainDB), and then drop the mod DLL
//     into BepInEx\plugins\. This plugin guides those steps explicitly. Faking a
//     fully-automated install that cannot be guaranteed would be dishonest.
//
//   * CONNECTION is made IN-GAME (verified against alphappy/ArchipelagoRW). After
//     the mod is installed and the game starts with BepInEx active, an Archipelago
//     connection screen appears where the user enters Server, Port, Slot Name and
//     Password. There is NO command-line arg and NO config file this launcher can
//     pre-write to seed the connection. So this plugin does NOT attempt a connection
//     prefill — the settings panel surfaces the session's server / slot so the user
//     can type them into the in-game fields.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Rain World install via the Windows registry (HKCU
//      \Software\Valve\Steam -> SteamPath, HKLM\...\WOW6432Node\Valve\Steam ->
//      InstallPath), parsing steamapps\libraryfolders.vdf for every library root
//      and locating steamapps\common\Rain World via appmanifest_312520.acf. A
//      manual install-dir OVERRIDE (settings folder picker) is also supported and
//      takes precedence; validated (must contain "RainWorld.exe") and persisted in
//      this plugin's OWN sidecar (Games/ROMs/rain_world/rw_launcher.json).
//   2. INSTALL/UPDATE (best effort) = download the release zip from
//      alphappy/ArchipelagoRW/releases/latest and extract its BepInEx plugin
//      DLL(s) into <RainWorld>\BepInEx\plugins\. Because BepInEx itself is NOT
//      bundled, the plugin presents clear guided steps and links so the user can
//      install BepInEx for Rain World first.
//   3. LAUNCH = run "RainWorld.exe" from the detected/override install, or fall
//      back to steam://rungameid/312520. ConnectsItself = true (the mod owns the
//      slot). SupportsStandalone = true (plain Rain World runs perfectly without AP).
//
// ── BUILD NOTE ────────────────────────────────────────────────────────────────
// This project sets UseWindowsForms=true alongside UseWPF=true. WPF UI types that
// also exist in WinForms (Color, Button, Brushes, MessageBox, FontWeights, etc.)
// are spelled with their FULL namespaces below to avoid CS0104 ambiguity. No
// file-level `using X = System.Windows...;` aliases are used (CS1537 if they
// duplicate a GlobalUsings entry). `using System.Windows;` is included above for
// UIElement (the return type of CreateSettingsPanel).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class RainWorldPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────
    private const string GhubOwner     = "alphappy";
    private const string GhubRepo      = "ArchipelagoRW";
    private const string ModRepoUrl    = $"https://github.com/{GhubOwner}/{GhubRepo}";
    private const string GhubApiUrl    = $"https://api.github.com/repos/{GhubOwner}/{GhubRepo}/releases";
    private const string SetupGuideUrl = $"https://github.com/{GhubOwner}/{GhubRepo}#readme";
    private const string BepInExGuide  = "https://github.com/BepInEx/BepInEx/releases";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Rain World appid 312520.
    private const string RWSteamAppId         = "312520";
    private static readonly string SteamRunUrl = $"steam://rungameid/{RWSteamAppId}";
    private const string SteamCommonFolderName = "Rain World";
    private const string GameExeName           = "RainWorld.exe";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Archipelago-Launcher/2.0" },
        },
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "rain_world";
    public string DisplayName => "Rain World";
    public string Subtitle    => "Native PC · BepInEx mod";

    /// EXACT AP game string — verified against alphappy/ArchipelagoRW.
    public string ApWorldName => "Rain World";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "rain_world.png");

    public string ThemeAccentColor => "#2A3D5C";   // dark slate blue — rainy atmosphere
    public string[] GameBadges     => new[] { "Steam · needs BepInEx mod" };

    public string Description =>
        "Rain World, the survival/exploration platformer by Videocult, played through " +
        "the ArchipelagoRW mod — a BepInEx 5 plugin that is an in-game Archipelago client, " +
        "so the game connects to the multiworld itself with no emulator and no Lua bridge. " +
        "Pearls, echoes, broadcasts, passages and other discoveries become checks shuffled " +
        "across the multiworld. You bring your own copy of Rain World (owned on Steam); " +
        "the Archipelago mod is a BepInEx plugin added on top. BepInEx 5 for Rain World " +
        "must be installed first (the launcher guides you), then the mod DLL drops into " +
        "BepInEx\\plugins\\. You connect to your server from the in-game Archipelago menu.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    public string? BuiltAgainstDataPackageChecksum => null;

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means a BepInEx plugin DLL whose name contains "archipelago"
    /// (case-insensitive) is present under the detected/override Rain World install's
    /// BepInEx\plugins tree. We do NOT gate on our own stamp, so a manually-installed
    /// or community-installed mod is also detected.
    public bool IsInstalled => FindInstalledModPlugin() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps downloads and bookkeeping (not the mod install
    /// location, which is inside the Rain World BepInEx tree). Exposed as
    /// GameDirectory for the IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "RainWorld");

    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "rw_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events (inert — mod owns the slot) ──────────────────────────
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
            InstalledVersion = FindInstalledModPlugin() != null
                ? (ReadStampedVersion() ?? "installed")
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
            AvailableVersion = null; // never throw on network failure
        }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((2, "Locating your Rain World installation..."));
        string? gameDir = ResolveRainWorldDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Rain World installation. Open this game's Settings " +
                "and pick your Rain World folder (the one containing \"RainWorld.exe\"), " +
                "or install Rain World via Steam first. The Archipelago mod is added on " +
                "top of your own copy of the game.");

        string pluginsDir = Path.Combine(gameDir, "BepInEx", "plugins");

        // BepInEx must already be installed for the mod DLL to load.
        bool bepInExPresent = Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"))
                           || Directory.Exists(pluginsDir);

        progress.Report((6, "Checking the latest ArchipelagoRW release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a download URL in the latest ArchipelagoRW release. " +
                "Check your internet connection, or install the mod manually from " +
                ModRepoUrl + "/releases.");

        await DownloadAndExtractModAsync(zipUrl, version, pluginsDir, progress, ct);

        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Staged the ArchipelagoRW mod {version} into your Rain World " +
            "BepInEx\\plugins folder. " +
            (bepInExPresent
                ? "BepInEx looks present. "
                : "IMPORTANT: this download contains only the Archipelago mod DLL. " +
                  "BepInEx 5 for Rain World must be installed first — see Settings for " +
                  "the guided steps and links. ") +
            "To play: launch Rain World (with BepInEx active), navigate to the " +
            "Archipelago connection screen, and enter your server, port, slot name " +
            "and password."));
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
        // The AP connection for Rain World is entered IN-GAME in the Archipelago
        // connection screen (Server / Port / Slot Name / Password). There is no
        // command-line arg or config file this launcher can pre-write (verified).
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartRainWorld();
        return Task.CompletedTask;
    }

    /// Plain Rain World runs perfectly well without the AP mod.
    public bool SupportsStandalone => true;

    /// The ArchipelagoRW mod owns the slot connection.
    public bool ConnectsItself => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartRainWorld();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (mod owns the slot) ────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x42, 0x6D, 0xFF));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20),
        };

        string? gameDir     = ResolveRainWorldDir();
        string? overrideDir = LoadOverrideDir();
        string? modPlugin   = FindInstalledModPlugin();
        bool    bepInExOk   = gameDir != null &&
                              (Directory.Exists(Path.Combine(gameDir, "BepInEx", "core")) ||
                               Directory.Exists(Path.Combine(gameDir, "BepInEx", "plugins")));

        // ── Honesty header ─────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Rain World is your own game (Steam) with the ArchipelagoRW mod added " +
                   "on top via BepInEx 5. The launcher can detect your Steam install and " +
                   "stage the mod DLL, but BepInEx 5 for Rain World must be installed " +
                   "first — the mod DLL alone is not enough. The recommended path is to " +
                   "install BepInEx 5 for Rain World first (see the guided steps below), " +
                   "then use Install on the Play tab. You connect to your server from the " +
                   "in-game Archipelago menu — not from this launcher.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: Rain World install ────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "RAIN WORLD INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Rain World not detected. Pick your install folder below, or install Rain " +
              "World via Steam first.";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = bepInExOk
                ? "BepInEx found (BepInEx folder present)."
                : "BepInEx not found yet — install BepInEx 5 for Rain World first (see steps below).",
            FontSize = 11,
            Foreground = bepInExOk ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modPlugin != null
                ? "ArchipelagoRW mod found: " + modPlugin
                : "ArchipelagoRW mod not found in BepInEx\\plugins yet (use Install on " +
                  "the Play tab after BepInEx is set up).",
            FontSize = 11,
            Foreground = modPlugin != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "",
            IsReadOnly = true,
            FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Your Rain World install folder (the one containing \"RainWorld.exe\"). " +
                      "Detected from Steam automatically; set it here to override.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...",
            Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Select your Rain World install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                                   ? (overrideDir ?? gameDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() != true) return;

            string picked = dlg.FolderName;
            if (!LooksLikeRainWorldDir(picked))
            {
                // Try the conventional sub-folder in case the user picked "common"
                string nested = Path.Combine(picked, SteamCommonFolderName);
                if (LooksLikeRainWorldDir(nested))
                {
                    picked = nested;
                }
                else
                {
                    System.Windows.MessageBox.Show(
                        "That does not look like a Rain World installation. Pick the folder " +
                        "that contains \"RainWorld.exe\" — for Steam this is usually " +
                        @"...\steamapps\common\Rain World.",
                        "Not a Rain World folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
            }
            SaveOverrideDir(picked);
            dirBox.Text = picked;
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Steam installs are detected automatically (appid 312520). Use this " +
                   "picker for a non-standard Steam library or a manual install.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Launch Rain World with BepInEx active (run RainWorld.exe normally — " +
                   "BepInEx hooks in automatically). Navigate to the Archipelago connection " +
                   "screen in-game and enter your Server URL, Port (default 38281), Slot Name " +
                   "and Password (leave blank if none). This launcher cannot pre-fill the " +
                   "connection — it is entered in-game.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Rain World (Steam). Install it if you have not. Use \"Select folder...\" " +
               "above if it was not detected.",
            "2. Install BepInEx 5 for Rain World. Download the BepInEx 5 release for Windows " +
               "x64 from the BepInEx GitHub releases (link below), extract it into your Rain " +
               "World folder so that BepInEx\\ appears alongside RainWorld.exe. The Rain World " +
               "community wiki and the ArchipelagoRW readme both describe this step. " +
               "(Note: use BepInEx 5, not BepInEx 6.)",
            "3. Run Rain World once with BepInEx installed (then close it). This lets BepInEx " +
               "generate its config files and the plugins\\ folder.",
            "4. Use the Install button on the Play tab to download and stage the ArchipelagoRW " +
               "mod DLL into BepInEx\\plugins\\. Alternatively, download the release zip from " +
               "the mod repo (link below) and extract the DLL into BepInEx\\plugins\\ yourself.",
            "5. Rain World also has a Remix mod manager (built-in). ArchipelagoRW should appear " +
               "in Remix once BepInEx is set up. You may need to enable it in Remix the first time.",
            "6. Launch Rain World. In the main menu, navigate to the Archipelago connection screen, " +
               "enter your Server URL, Port, Slot Name and Password, and connect.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ──────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted,
            Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("ArchipelagoRW mod (GitHub) ↗",  ModRepoUrl),
            ("ArchipelagoRW releases ↗",      ModRepoUrl + "/releases"),
            ("BepInEx 5 releases (GitHub) ↗", BepInExGuide),
            ("Archipelago setup guide ↗",     SetupGuideUrl),
            ("Archipelago Official ↗",        ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0),
                FontSize = 12,
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

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GhubApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string ver = el.TryGetProperty("tag_name", out var tv) ? tv.GetString() ?? "" : "";
                string body = el.TryGetProperty("body", out var tb) ? tb.GetString() ?? "" : "";
                string? htmlUrl = el.TryGetProperty("html_url", out var hu) ? hu.GetString() : null;

                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var pd) && pd.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(pd.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   "ArchipelagoRW " + ver,
                    Body:    body,
                    Version: ver,
                    Date:    date,
                    Url:     htmlUrl
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// Resolve the latest release from the GitHub releases API.
    /// Returns (tag_name, browser_download_url of the first .zip asset), or
    /// (tag_name, null) when no zip asset is found.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            // /releases/latest returns the single most-recent non-prerelease release.
            string latestUrl = $"https://api.github.com/repos/{GhubOwner}/{GhubRepo}/releases/latest";
            string json = await _http.GetStringAsync(latestUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string ver = root.TryGetProperty("tag_name", out var tv) ? tv.GetString() ?? "" : "";
            if (root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var an) ? an.GetString() : null;
                    string? dlUrl = asset.TryGetProperty("browser_download_url", out var bu)
                                   ? bu.GetString() : null;
                    if (name != null &&
                        name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                        dlUrl != null)
                        return (ver, dlUrl);
                }
            }
            // No zip asset — version known but no downloadable zip.
            return (ver, null);
        }
        catch (OperationCanceledException) { throw; }
        catch { return ("", null); }
    }

    // ── Private helpers — Steam / Rain World detection ────────────────────────

    private string? ResolveRainWorldDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeRainWorldDir(ov))
            return ov;

        try { return DetectSteamRainWorldDir(); }
        catch { return null; }
    }

    private static bool LooksLikeRainWorldDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

    private static string? DetectSteamRainWorldDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{RWSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeRainWorldDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeRainWorldDir(conventional)) return conventional;
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
            if (norm.Length > 0 && seen.Add(norm))
                yield return norm;
        }
    }

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
            int open = text.IndexOf('"', i);
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

    // ── Private helpers — installed-mod detection ─────────────────────────────

    /// Find the ArchipelagoRW mod DLL under the install's BepInEx\plugins tree.
    /// Accepts any *.dll whose name (case-insensitive) contains "archipelago",
    /// or any sub-folder whose name contains "archipelago" that holds a DLL.
    private string? FindInstalledModPlugin()
    {
        try
        {
            string? game = ResolveRainWorldDir();
            if (game == null) return null;
            string pluginsDir = Path.Combine(game, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            // 1. DLL named like Archipelago anywhere under plugins.
            foreach (string dll in Directory.EnumerateFiles(pluginsDir, "*.dll",
                         SearchOption.AllDirectories))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }

            // 2. A sub-folder named like Archipelago that holds a DLL.
            foreach (string sub in Directory.EnumerateDirectories(pluginsDir, "*",
                         SearchOption.AllDirectories))
            {
                string folder = Path.GetFileName(sub);
                if (folder.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                try
                {
                    if (Directory.EnumerateFiles(sub, "*.dll", SearchOption.AllDirectories).Any())
                        return sub;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartRainWorld()
    {
        string? game = ResolveRainWorldDir();
        string? exe  = game != null ? Path.Combine(game, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = game!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Rain World.");

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

        // Fall back to Steam.
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
            "Could not find \"RainWorld.exe\". Open this game's Settings and pick your " +
            "Rain World install folder, or install Rain World via Steam.",
            GameExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string pluginsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"rw-archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"rw-archipelago-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading ArchipelagoRW {version}..."));
            using (var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                long total      = response.Content.Headers.ContentLength ?? -1;
                long downloaded = 0;
                await using var src = await response.Content.ReadAsStreamAsync(ct);
                await using var dst = File.Create(tempZip);
                var buf = new byte[81920];
                int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), ct);
                    downloaded += n;
                    if (total > 0)
                    {
                        int pct = (int)(10 + 55 * downloaded / total);
                        progress.Report((pct, $"Downloading ArchipelagoRW... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting the mod package..."));
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            progress.Report((85, "Installing mod DLL into BepInEx plugins folder..."));
            Directory.CreateDirectory(pluginsDir);

            // Find the plugin payload: prefer a BepInEx/plugins sub-folder, then a
            // "plugins" sub-folder, then the zip root (some releases put the DLL flat).
            string payloadRoot = ResolvePluginPayloadRoot(tempDir);
            string destDir = Path.Combine(pluginsDir, "ArchipelagoRW");
            Directory.CreateDirectory(destDir);
            CopyDirectoryContents(payloadRoot, destDir);

            progress.Report((95, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))        File.Delete(tempZip); }           catch { }
            try { if (Directory.Exists(tempDir))   Directory.Delete(tempDir, true); } catch { }
        }
    }

    private static string ResolvePluginPayloadRoot(string extractedRoot)
    {
        try
        {
            // BepInEx/plugins nested in the zip.
            foreach (string dir in Directory.EnumerateDirectories(
                         extractedRoot, "plugins", SearchOption.AllDirectories))
            {
                string parent = Path.GetFileName(Path.GetDirectoryName(dir) ?? "");
                if (parent.Equals("BepInEx", StringComparison.OrdinalIgnoreCase) &&
                    DirectoryHasDll(dir))
                    return dir;
            }
            // Top-level "plugins" folder.
            string topPlugins = Path.Combine(extractedRoot, "plugins");
            if (Directory.Exists(topPlugins) && DirectoryHasDll(topPlugins))
                return topPlugins;
        }
        catch { }
        return extractedRoot;
    }

    private static bool DirectoryHasDll(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*.dll", SearchOption.AllDirectories).Any(); }
        catch { return false; }
    }

    private static void CopyDirectoryContents(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
    }

    // ── Private helpers — settings sidecar ───────────────────────────────────

    private sealed class RWSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private RWSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<RWSettings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(RWSettings s)
    {
        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
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
    private void SaveOverrideDir(string p) { var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s); }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    private void WriteStampedVersion(string v) { var s = LoadSettings(); s.ModVersion = v; SaveSettings(s); }
}
