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

namespace LauncherV2.Plugins.PowerwashSimulator;

// ═══════════════════════════════════════════════════════════════════════════════
// PowerwashSimulatorPlugin — install / launch for "PowerWash Simulator"
// (FuturLab, 2022) played through the PowerwashSimAP mod by SWCreeperKing,
// which is a BepInEx plugin that provides an in-game Archipelago client and
// menu. This is a NATIVE "ConnectsItself" integration in the same family as
// the shipped Hollow Knight / TUNIC / Stardew Valley / Noita / BRC plugins:
// the game itself speaks to the AP server (no emulator, no Lua bridge, no
// launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified against the mod repo) ──────────
// This is a STEAM-MOD native: the BASE GAME is the user's own legally-owned
// copy of PowerWash Simulator (Steam appid 1290000), and Archipelago support
// is delivered as a BepInEx 5 mod added on top. The honest integration
// ceiling — exactly like the shipped Hollow Knight / Noita / BRC plugins —
// is "automate what is possible, guide the irreducible parts." The verified
// facts:
//
//   * THE AP WORLD game string is "PowerWash Simulator" (verified against the
//     mod repo SWCreeperKing/PowerwashSimAP). GameId here = "powerwash_simulator".
//
//   * THE MOD repo is SWCreeperKing/PowerwashSimAP (GitHub). Releases ship a
//     zip whose BepInEx/ contents are extracted into the PowerWash Simulator
//     game directory. The primary marker of an installed mod is the presence of
//     "BepInEx\plugins\PowerwashSimAP.dll" under the Steam game directory.
//
//   * FRAMEWORK: BepInEx 5 (Unity game). The mod is a BepInEx plugin. BepInEx
//     itself is a SEPARATE prerequisite not bundled with the mod zip. The plugin
//     can stage the mod's BepInEx/ payload, but BepInEx must already be present
//     in the game directory (winhttp.dll + BepInEx\ folder) for the plugin to
//     load. The installer checks for BepInEx and guides the user if absent.
//
//   * CONNECTION is FULLY IN-GAME (ConnectsItself = true). The mod provides an
//     in-game Archipelago menu/UI for entering the server address, port, slot
//     name, and password. There is NO config file and NO command-line argument
//     this launcher can pre-write to seed the connection. So this plugin does
//     NOT attempt a connection prefill — the settings panel surfaces the session
//     credentials for the user to type into the in-game menu.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam PowerWash Simulator install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath and HKLM WOW6432Node equivalent),
//      parsing steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\PowerWash Simulator via appmanifest_1290000.acf. A
//      manual install-dir OVERRIDE (settings folder picker) is also supported
//      and takes precedence; it is validated (must contain
//      "PowerWashSimulator.exe") and persisted in this plugin's OWN sidecar
//      (Games/ROMs/powerwash_simulator/pws_launcher.json) — Core/SettingsStore
//      is NOT modified.
//   2. INSTALL/UPDATE (best effort) = download the latest release zip from
//      SWCreeperKing/PowerwashSimAP/releases (GitHub API), extract the BepInEx/
//      contents into the Steam game directory. If BepInEx is not yet present,
//      the plugin presents clear guided steps (install BepInEx 5 first), links
//      to the BepInEx releases page, and links to the mod repo. Never a fake
//      one-click that silently hides missing prerequisites.
//   3. LAUNCH = run "PowerWashSimulator.exe" from the detected/override install;
//      if the exe cannot be found but Steam is present, fall back to
//      steam://rungameid/1290000. ConnectsItself = true (the mod owns the slot —
//      the launcher must NOT hold its own ApClient on it). SupportsStandalone =
//      true (plain PowerWash Simulator runs fine without AP). No connection
//      prefill (entered in-game), stated honestly.
//
// ── BUILD NOTE ────────────────────────────────────────────────────────────────
//   This project sets UseWindowsForms=true alongside UseWPF=true, so WPF UI
//   types that also exist in WinForms (Color, Button, Brushes, etc.) are
//   resolved via the project's GlobalUsings aliases (System.Windows.* imported
//   globally). No local file-level aliases are added here (a local alias
//   duplicating a global one is CS1537). The file uses the short names (Color,
//   SolidColorBrush, Button, etc.) that the global aliases make available.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class PowerwashSimulatorPlugin : IGamePlugin
{
    // ── Constants — the PowerwashSimAP mod (GitHub, verified 2026-06-14) ───────
    private const string MOD_OWNER = "SWCreeperKing";
    private const string MOD_REPO  = "PowerwashSimAP";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    private const string BepInExSite     = "https://github.com/BepInEx/BepInEx/releases";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — PowerWash Simulator appid 1290000.
    private const string PwsSteamAppId = "1290000";
    private static readonly string SteamRunUrl = $"steam://rungameid/{PwsSteamAppId}";

    /// The standard Steam install sub-folder name.
    private const string SteamCommonFolderName = "PowerWash Simulator";

    /// The base-game executable name (Unity, Windows).
    private const string PwsExeName = "PowerWashSimulator.exe";

    /// The DLL that marks the mod as installed (BepInEx\plugins\PowerwashSimAP.dll).
    private const string ModDllName = "PowerwashSimAP.dll";

    /// Pinned fallback version when the GitHub API is unreachable.
    private const string FallbackVersion = "1.0.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "powerwash_simulator";
    public string DisplayName => "PowerWash Simulator";
    public string Subtitle    => "Native PC · BepInEx mod";

    /// EXACT AP game string — verified against SWCreeperKing/PowerwashSimAP.
    public string ApWorldName => "PowerWash Simulator";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "powerwash_simulator.png");

    public string ThemeAccentColor => "#4AC8E8";   // clean sky / soapy water blue
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "PowerWash Simulator, FuturLab's satisfying 2022 cleaning game, played through " +
        "the PowerwashSimAP mod by SWCreeperKing — a BepInEx plugin that provides an " +
        "in-game Archipelago client and menu. Jobs, equipment, locations and items are " +
        "shuffled across the multiworld. You bring your own copy of PowerWash Simulator " +
        "(owned on Steam); the Archipelago mod is added on top via BepInEx 5. The " +
        "launcher detects your Steam install and can stage the mod files into it. " +
        "BepInEx itself must be installed first (the Install button guides you). You " +
        "connect to your Archipelago server from the in-game mod menu.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means BepInEx\plugins\PowerwashSimAP.dll exists in the
    /// detected/override game directory. We do NOT gate on our own version stamp
    /// — the user may have installed the mod by hand, which we honor.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps its bookkeeping. The actual mod is extracted INTO
    /// the Steam game directory, not here. Exposed as GameDirectory per the
    /// IGamePlugin contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "PowerwashSimulator");

    /// This plugin's OWN settings sidecar (kept out of the shared SettingsStore
    /// so the plugin stays a single self-contained file).
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "pws_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The mod handles all AP communication in-game. These exist for interface
    // compatibility only (ConnectsItself = true).
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
        catch
        {
            InstalledVersion = null;
        }

        try
        {
            var (version, _) = await ResolveLatestModAsync(ct);
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
        progress.Report((2, "Locating your PowerWash Simulator installation..."));
        string? gameDir = ResolvePwsDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a PowerWash Simulator installation. Open this game's " +
                "Settings and pick your PowerWash Simulator folder (the one containing " +
                "\"PowerWashSimulator.exe\"), or install PowerWash Simulator via Steam first. " +
                "The Archipelago mod is added on top of your own copy of the game.");

        // Check for BepInEx — a SEPARATE prerequisite not bundled with the mod zip.
        bool bepInExPresent = BepInExPresent(gameDir);
        string pluginsDir   = Path.Combine(gameDir, "BepInEx", "plugins");

        progress.Report((6, "Checking the latest PowerwashSimAP release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the PowerwashSimAP mod download on GitHub. Check your " +
                "internet connection, or download it manually from " + ModRepoUrl +
                "/releases — see Settings for the guided steps.");

        // Download + extract the mod zip INTO the game directory (the zip ships
        // BepInEx/ content that maps directly onto the game root).
        await DownloadAndExtractModAsync(zipUrl, version, gameDir, progress, ct);

        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Staged the PowerwashSimAP mod {version} into your PowerWash Simulator folder. " +
            (bepInExPresent
                ? "BepInEx is already present. "
                : "IMPORTANT: BepInEx was NOT detected in your game folder. " +
                  "You must install BepInEx 5 (Unity Mono x64) first — " +
                  "download it from the BepInEx releases page (see Settings) and extract it " +
                  "into your PowerWash Simulator game folder, then run the game once so " +
                  "BepInEx sets itself up. ") +
            "To play: launch the game (modded), open the in-game Archipelago menu, and " +
            "enter your server address, port, slot name, and password (if any)."));
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
        // HONEST: the AP server connection is entered IN-GAME via the mod's
        // Archipelago menu (server address / port / slot name / password). There
        // is no command-line arg and no config file this launcher can pre-write
        // (verified — see header). ConnectsItself = true: the launcher must NOT
        // hold its own ApClient on this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartPws();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) PowerWash Simulator runs perfectly well.
    public bool SupportsStandalone => true;

    /// The mod owns the slot connection (see header).
    public bool ConnectsItself => true;

    public string? BuiltAgainstDataPackageChecksum => null;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartPws();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert ────────────────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Return null when <folder> is acceptable; return a short human-readable
    /// reason when it is not a valid PowerWash Simulator install.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your PowerWash Simulator install folder.";

        if (LooksLikePwsDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikePwsDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a PowerWash Simulator installation. Pick the folder " +
               "that contains \"PowerWashSimulator.exe\" (for Steam this is usually " +
               @"...\steamapps\common\PowerWash Simulator).";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new SolidColorBrush(Color.FromRgb(0x4A, 0xC8, 0xE8)); // sky blue
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        string? gameDir     = ResolvePwsDir();
        string? overrideDir = LoadOverrideDir();
        string? modDll      = FindInstalledModDll();
        bool    bepInExOk   = gameDir != null && BepInExPresent(gameDir);

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "PowerWash Simulator is your own game (Steam) with the PowerwashSimAP " +
                   "mod added on top via BepInEx 5. The launcher detects your Steam install " +
                   "and can stage the mod files into it. BepInEx 5 is a separate prerequisite " +
                   "that must be installed first (see the guided steps below). You connect to " +
                   "your Archipelago server from the in-game mod menu. These external steps " +
                   "are not verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "POWERWASH SIMULATOR INSTALL", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 0, 0, 8),
        });

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "PowerWash Simulator not detected. Pick your install folder below, or " +
              "install PowerWash Simulator via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // BepInEx status
        panel.Children.Add(new TextBlock
        {
            Text = bepInExOk
                    ? "BepInEx found in your game folder."
                    : "BepInEx not found yet — install BepInEx 5 (Unity Mono x64) from the " +
                      "BepInEx releases page (link below) and extract it into your PowerWash " +
                      "Simulator folder, then run the game once so BepInEx sets itself up.",
            FontSize = 11, Foreground = bepInExOk ? success : warn,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4),
        });

        // Mod DLL status
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                    ? "PowerwashSimAP mod found: " + modDll
                    : "PowerwashSimAP mod not found yet (use Install on the Play tab, or " +
                      "install it manually from the mod releases).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // Folder override row
        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your PowerWash Simulator install folder (contains " +
                          "\"PowerWashSimulator.exe\"). Detected from Steam automatically; " +
                          "set it here to override (non-standard Steam library, etc.).",
        };
        var dirBtn = new Button
        {
            Content = "Select folder...", Width = 120, Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your PowerWash Simulator install folder",
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
                    MessageBox.Show(bad, "Not a PowerWash Simulator folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikePwsDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikePwsDir(nested)) picked = nested;
                }
                SaveOverrideDir(picked);
                dirBox.Text = picked;
            }
        };
        DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new TextBlock
        {
            Text = "Steam installs are detected automatically (appid 1290000). Use this " +
                   "picker for a non-standard Steam library.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (in-game) ─────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (entered in-game via the mod menu)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Launch the game (with BepInEx and the mod installed). Open the " +
                   "Archipelago menu provided by the mod and enter your server address, " +
                   "port, slot name, and password (if any). This launcher does not pre-fill " +
                   "the connection — it is entered in-game.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own PowerWash Simulator (on Steam). Install it if you have not. Use the picker " +
                "above if it was not detected automatically.",
            "2. Install BepInEx 5 (Unity Mono x64) into your PowerWash Simulator folder: " +
                "download it from the BepInEx releases page (link below), then extract the zip " +
                "directly into your game folder (so winhttp.dll sits next to " +
                "PowerWashSimulator.exe). Do NOT use BepInEx 6 pre-releases.",
            "3. Launch PowerWash Simulator ONCE so BepInEx creates its configuration files, " +
                "then close the game.",
            "4. Install the PowerwashSimAP mod: the Install button on the Play tab downloads " +
                "the latest release and extracts its BepInEx/ contents into your game folder, " +
                "or do it by hand from the mod releases (link below).",
            "5. To play: launch the game. Open the in-game Archipelago menu and enter your " +
                "server address, port, slot name, and password (if any) to connect.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ─────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("PowerwashSimAP (GitHub) ↗",      ModRepoUrl),
            ("PowerWash Simulator (Steam) ↗",  "https://store.steampowered.com/app/1290000"),
            ("BepInEx releases ↗",             BepInExSite),
            ("Archipelago Official ↗",         ArchipelagoSite),
        })
        {
            var btn = new Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xC8, 0xE8)),
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
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_URL, ct);
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
                    Url:     el.TryGetProperty("html_url", out var u) ? u.GetString() : null
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// "v1.2.3" → "1.2.3" when a leading 'v' precedes a digit; else trimmed.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: version + the mod zip download URL.
    /// Prefers a .zip asset; falls back to the pinned fallback version when the
    /// GitHub API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(CancellationToken ct)
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
                string? preferred = null;   // zip that mentions the mod name
                string? anyZip    = null;   // any .zip as fallback
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;

                    string lower = name.ToLowerInvariant();
                    if (!lower.EndsWith(".zip")) continue;

                    anyZip ??= url;
                    if (preferred == null &&
                        (lower.Contains("powerwash") || lower.Contains("archipelago")))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null)
                    return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        // Pinned fallback: point at the releases page; installer will surface this.
        return (FallbackVersion, null);
    }

    // ── Private helpers — Steam / game detection ──────────────────────────────

    /// The game install dir to use: the override (if set and valid) wins, else
    /// the Steam-detected install. Null when nothing is found.
    private string? ResolvePwsDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikePwsDir(ov))
            return ov;

        try { return DetectSteamPwsDir(); }
        catch { return null; }
    }

    /// A folder "looks like" PowerWash Simulator if it contains the game exe.
    private static bool LooksLikePwsDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, PwsExeName));
        }
        catch { return false; }
    }

    /// True when BepInEx appears to be installed in the game folder.
    /// BepInEx 5 (Mono) drops a "BepInEx" folder plus a winhttp.dll proxy.
    private static bool BepInExPresent(string gameDir)
    {
        try
        {
            if (!Directory.Exists(gameDir)) return false;
            if (Directory.Exists(Path.Combine(gameDir, "BepInEx"))) return true;
            if (File.Exists(Path.Combine(gameDir, "winhttp.dll")))  return true;
            return false;
        }
        catch { return false; }
    }

    /// Detect the Steam PowerWash Simulator install via the registry and VDF.
    private static string? DetectSteamPwsDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{PwsSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikePwsDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikePwsDir(conventional)) return conventional;
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

        string? progX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(progX86))
            yield return Path.Combine(progX86, "Steam");
    }

    private static string NormalizeSteamPath(string p)
        => p.Replace('/', '\\').TrimEnd('\\');

    /// All Steam library roots from the Steam root + libraryfolders.vdf.
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

    /// Pull every "path" "<value>" pair from a libraryfolders.vdf body.
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

    /// Read the "installdir" value from an appmanifest_*.acf. Returns null if absent.
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

    /// Find BepInEx\plugins\PowerwashSimAP.dll under the detected/override
    /// game directory (recursive, case-insensitive). Returns the path or null.
    private string? FindInstalledModDll()
    {
        try
        {
            string? game = ResolvePwsDir();
            if (game == null) return null;
            string pluginsDir = Path.Combine(game, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dll).Equals(ModDllName, StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartPws()
    {
        string? game = ResolvePwsDir();
        string? exe  = game != null ? Path.Combine(game, PwsExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = game!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start PowerWash Simulator.");

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

        // Fall back to Steam.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true;
                return;
            }
            catch { /* fall through to error */ }
        }

        throw new FileNotFoundException(
            "Could not find \"PowerWashSimulator.exe\". Open this game's Settings and pick " +
            "your PowerWash Simulator install folder, or install it via Steam.",
            PwsExeName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    /// Download the mod release zip and extract its BepInEx/ contents into the
    /// game directory. The zip layout from SWCreeperKing/PowerwashSimAP ships
    /// BepInEx/ content; if the zip has a top-level wrapper folder we flatten it.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string gameDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"pws-archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"pws-archipelago-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading PowerwashSimAP {version}..."));
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
                        int pct = (int)(10 + 55 * downloaded / total);
                        progress.Report((pct, $"Downloading PowerwashSimAP... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting mod package..."));
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            progress.Report((82, "Copying mod files into your PowerWash Simulator folder..."));

            // If the zip contains a single top-level folder (a common pattern),
            // use that as the source root; otherwise use the extraction root.
            string sourceRoot = ResolveSourceRoot(tempDir);

            // Copy the BepInEx/ subfolder (and any other top-level assets) into
            // the game directory, mirroring the tree.
            CopyDirectoryContents(sourceRoot, gameDir);

            progress.Report((95, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))  File.Delete(tempZip); }          catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    /// If the extraction root contains exactly one subdirectory and no files at
    /// its top level (a single wrapper folder pattern), return that subdirectory
    /// as the effective source root; otherwise return the extraction root as-is.
    private static string ResolveSourceRoot(string extractedRoot)
    {
        try
        {
            string[] files = Directory.GetFiles(extractedRoot);
            string[] dirs  = Directory.GetDirectories(extractedRoot);
            if (files.Length == 0 && dirs.Length == 1)
                return dirs[0];
        }
        catch { /* ignore — use root */ }
        return extractedRoot;
    }

    /// Recursively copy a directory's contents into a destination folder
    /// (overwriting), creating subdirectories as needed.
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

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class PwsSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private PwsSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<PwsSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(PwsSettings s)
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
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    private void WriteStampedVersion(string v) { var s = LoadSettings(); s.ModVersion = v; SaveSettings(s); }
}
