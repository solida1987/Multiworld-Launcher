using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
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

namespace LauncherV2.Plugins.CrossCode;

// ═══════════════════════════════════════════════════════════════════════════════
// CrossCodePlugin — install / launch for "CrossCode" (Radical Fish Games, 2018)
// played through the CCMultiworldRandomizer mod by CodeTriangle. This is a
// NATIVE "ConnectsItself" integration: the mod itself (running inside the game)
// speaks to the Archipelago server — no emulator, no Lua bridge, no launcher-held
// ApClient on the slot.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// CrossCode is an nw.js (node-webkit) desktop application. Mods are delivered via
// CCLoader, a JavaScript modloader by CCDirectLink. The honest ceiling is "automate
// what is possible, guide the irreducible parts."
//
//   * THE AP WORLD game string is "CrossCode" — verified from the source code of
//     CCMultiworldRandomizer (src/patches/multiworld-model.ts line: `if (game ==
//     "CrossCode")`). GameId = "crosscode".
//
//   * STEAM APPID 368340 — verified against store.steampowered.com/app/368340/
//     CrossCode/. Developer: Radical Fish Games. Publisher: Deck13.
//
//   * THE MOD LOADER is CCLoader (repo CCDirectLink/CCLoader, latest v2.25.10).
//     Installed by downloading the repo's source-code zip (zipball) from the GitHub
//     release and extracting its contents directly into the CrossCode game folder,
//     overwriting package.json. After install a "ccloader" folder appears at the
//     game root and the game's modding runtime is active.
//
//   * THE AP MOD is CCMultiworldRandomizer (repo CodeTriangle/CCMultiworldRandomizer,
//     latest 0.9.2). It ships as a single .ccmod file ("CCMultiworldRandomizer-
//     0.9.2.ccmod"). A .ccmod file IS simply a ZIP archive; it is placed into the
//     "assets/mods/" folder inside the CrossCode game directory. CCLoader picks it
//     up on next game launch.
//
//   * MOD DEPENDENCIES declared in the .ccmod's ccmod.json:
//       "open-world" >= 0.5.1-pre1
//       "nax-ccuilib" >= 1.5.1
//       "ccmodmanager" >= 1.0.4
//       "font-utils"  >= 1.2.0
//     The CCLoader mod manager (ccmodmanager, which is bundled in CCLoader) can
//     install these from within the game. This launcher guides the user to the
//     in-game mod manager for dependency resolution rather than faking a one-click
//     install that cannot guarantee all dependencies are resolved correctly.
//
//   * CONNECTION CONFIG (verified against src/types/multiworld-model.ts): the mod
//     stores connection information in the CrossCode SAVE DATA (ig.vars/ig.storage),
//     not in a config file this launcher can pre-write. The connection is entered
//     IN-GAME: on the title screen, click "New Game" → Archipelago prompts for
//     server URL (e.g. "archipelago.gg:38281"), slot name, and password. For
//     continuing a save, the connection dialog appears when selecting the save slot.
//     There is NO documented command-line arg or pre-fillable config file — so this
//     plugin does NOT attempt a connection pre-fill. The settings panel shows the
//     current session credentials so the user can copy them into the in-game fields.
//
//   * CROSSCODE EXECUTABLE: nw.exe (the nw.js / node-webkit runtime that ships
//     with the game). It lives in the game root alongside package.json. There is no
//     "CrossCode.exe" wrapper on the base install; Steam simply launches nw.exe.
//
//   * CCLOADER GITHUB: There is no binary release asset attached to CCLoader releases
//     (confirmed — all releases have empty .assets[] in the GitHub API). Installation
//     is via the repo's zipball. The zipball URL pattern is:
//     https://api.github.com/repos/CCDirectLink/CCLoader/zipball/<tag>
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam CrossCode install via the Windows registry + libraryfolders
//      .vdf + appmanifest_368340.acf → steamapps\common\CrossCode\. A manual folder
//      picker override is also offered and persisted in the plugin's own sidecar
//      (Games/ROMs/crosscode/crosscode_launcher.json). Core/SettingsStore is NOT
//      modified.
//   2. INSTALL CCLoader: download the CCDirectLink/CCLoader zipball, extract its
//      contents into the CrossCode game root (overwriting package.json), creating
//      the ccloader folder and assets/mods/.
//   3. INSTALL MOD: download CCMultiworldRandomizer-<ver>.ccmod from the
//      CodeTriangle/CCMultiworldRandomizer release, place it into assets/mods/.
//      Because .ccmod files ARE zip archives, placing them there is sufficient for
//      CCLoader to recognize them. (Dependency mods must be installed via the
//      in-game CCLoader mod manager — the launcher guides this step.)
//   4. LAUNCH: run nw.exe from the detected/override install; fall back to
//      steam://rungameid/368340 when the exe is missing but Steam is present.
//      ConnectsItself = true (the mod owns the slot connection). SupportsStandalone
//      = true (CrossCode plays fine without the mod).
//
// ── DEFENSIVE / UNVERIFIED ("verify at build time", SoH/HK-style) ─────────────
//   * "Installed" = assets/mods/ contains a file matching "CCMultiworldRandomizer
//     *.ccmod" (or the unpacked mod directory mw-rando). The ccloader folder is also
//     checked as a CCLoader-present signal.
//   * Steam library VDF / ACF parsing is defensive: tolerant quoted key-value scan;
//     any failure degrades to "CrossCode not found" rather than throwing.
//   * The CCLoader zipball wraps everything in a top-level subfolder (GitHub's
//     standard zip layout: <org>-<repo>-<sha>/). The plugin unwraps this so the
//     ccloader folder and package.json land directly in the CrossCode game root.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CrossCodePlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    // The AP mod (CodeTriangle/CCMultiworldRandomizer)
    private const string MOD_OWNER = "CodeTriangle";
    private const string MOD_REPO  = "CCMultiworldRandomizer";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";
    private const string GH_MOD_RELEASES_LATEST_URL = $"{GH_MOD_RELEASES_URL}/latest";

    // CCLoader mod loader (CCDirectLink/CCLoader)
    private const string CCLOADER_OWNER   = "CCDirectLink";
    private const string CCLOADER_REPO    = "CCLoader";
    private const string CCLoaderRepoUrl  = $"https://github.com/{CCLOADER_OWNER}/{CCLOADER_REPO}";
    private const string GH_CCLOADER_RELEASES_URL =
        $"https://api.github.com/repos/{CCLOADER_OWNER}/{CCLOADER_REPO}/releases";

    // Pinned fallback versions (verified 2026-06-14)
    private const string FallbackModVersion    = "0.9.2";
    private const string FallbackModAssetName  = $"CCMultiworldRandomizer-{FallbackModVersion}.ccmod";
    private static readonly string FallbackModUrl =
        $"{ModRepoUrl}/releases/download/{FallbackModVersion}/{FallbackModAssetName}";

    // CCLoader has no binary release assets; we always use the zipball of latest tag.
    private const string FallbackCCLoaderTag = "v2.25.10/v2.14.2";
    // URL-encoded for the slash in the tag
    private static readonly string FallbackCCLoaderZipball =
        $"https://api.github.com/repos/{CCLOADER_OWNER}/{CCLOADER_REPO}/zipball/" +
        Uri.EscapeDataString(FallbackCCLoaderTag);

    private const string SetupGuideUrl =
        $"https://github.com/{MOD_OWNER}/{MOD_REPO}/wiki/Setup";
    private const string ArchipelagoSite = "https://archipelago.gg";
    private const string CCLoaderWikiUrl = "https://wiki.c2dl.info/CCLoader";
    private const string ModDiscordUrl   = "https://discord.gg/ZSWfgQdfGr";

    // Steam — CrossCode appid 368340 (verified store.steampowered.com/app/368340/)
    private const string CCSteamAppId = "368340";
    private static readonly string SteamRunUrl = $"steam://rungameid/{CCSteamAppId}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "crosscode";
    public string DisplayName => "CrossCode";
    public string Subtitle    => "Native PC · CCMultiworldRandomizer mod";

    /// EXACT AP game string — verified from CCMultiworldRandomizer source code.
    public string ApWorldName => "CrossCode";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "crosscode.png");

    public string ThemeAccentColor => "#0A6E6E";   // teal, CrossCode's signature color
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "CrossCode, Radical Fish Games' 2018 retro-style 2D Action RPG, played " +
        "through the CCMultiworldRandomizer mod — a fully-featured Archipelago " +
        "client integrated into the game. Chests, quest items, shop purchases and " +
        "more are shuffled into the multiworld, and the mod connects to the " +
        "Archipelago server directly from within the game. You bring your own copy " +
        "of CrossCode (Steam appid 368340). The mod runs on CCLoader, CrossCode's " +
        "JavaScript mod loader, which the launcher installs for you. Connection " +
        "details are entered in the in-game Archipelago login dialog.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the CCMultiworldRandomizer .ccmod (or its unpacked mod
    /// directory) is present in the detected CrossCode install's assets/mods/.
    public bool IsInstalled => FindInstalledMod() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "CrossCode");

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "crosscode_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────

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
            string? modPath = FindInstalledMod();
            if (modPath != null)
                InstalledVersion = ReadStampedVersion() ?? "installed";
            else
                InstalledVersion = null;
        }
        catch
        {
            InstalledVersion = null;
        }

        try
        {
            var (version, _) = await ResolveLatestModReleaseAsync(ct);
            AvailableVersion = version;
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
        // 0. We need a CrossCode install directory.
        progress.Report((2, "Locating your CrossCode installation..."));
        string? ccDir = ResolveCrossCodeDir();
        if (ccDir == null)
            throw new InvalidOperationException(
                "Could not find a CrossCode installation. Open this game's Settings " +
                "and pick your CrossCode folder (the one containing nw.exe and " +
                "package.json), or install CrossCode via Steam first.");

        string modsDir = Path.Combine(ccDir, "assets", "mods");
        bool ccloaderInstalled = Directory.Exists(Path.Combine(ccDir, "ccloader"));

        // 1. Install CCLoader if not present.
        if (!ccloaderInstalled)
        {
            progress.Report((5, "CCLoader not found — installing CCLoader..."));
            await InstallCCLoaderAsync(ccDir, progress, ct);
            progress.Report((40, "CCLoader installed."));
        }
        else
        {
            progress.Report((40, "CCLoader already installed — skipping."));
        }

        // 2. Resolve the latest CCMultiworldRandomizer release.
        progress.Report((42, "Checking the latest CCMultiworldRandomizer release..."));
        var (modVersion, modUrl) = await ResolveLatestModReleaseAsync(ct);
        AvailableVersion = modVersion;

        if (modUrl == null)
            throw new InvalidOperationException(
                "Could not find the CCMultiworldRandomizer download on GitHub. " +
                "Check your internet connection, or install the mod manually. " +
                "See Settings for links.");

        // 3. Download the .ccmod file and place it in assets/mods/.
        progress.Report((44, $"Downloading CCMultiworldRandomizer {modVersion}..."));
        Directory.CreateDirectory(modsDir);
        await DownloadCCModAsync(modUrl, modVersion, modsDir, progress, ct);

        // 4. Stamp version.
        WriteStampedVersion(modVersion);
        InstalledVersion = modVersion;

        progress.Report((100,
            $"CCMultiworldRandomizer {modVersion} installed into assets/mods/. " +
            "IMPORTANT: The mod requires dependency mods (open-world, nax-ccuilib, " +
            "ccmodmanager, font-utils). Launch CrossCode and use the in-game CCLoader " +
            "mod manager to install them. Then start a new game and enter your server " +
            "details in the in-game Archipelago login dialog."));
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
        // HONEST: the Archipelago connection for CrossCode is entered IN-GAME in the
        // CCMultiworldRandomizer login dialog (title screen → New Game, or selecting
        // a save slot). The mod stores it in the game's own save data and reconnects
        // automatically. There is no command-line argument or pre-fillable config file
        // (verified against the mod's source and setup guide). So we simply launch
        // the game; the settings panel shows the session credentials to copy in-game.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on this
        // slot while the in-game mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartCrossCode();
        return Task.CompletedTask;
    }

    public bool SupportsStandalone => true;
    public bool ConnectsItself     => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartCrossCode();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (mod owns the slot) ─────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Install validation (override picker) ──────────────────────────────────

    public string? ValidateCrossCodeDir(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist.";
        if (LooksLikeCrossCodeDir(folder))
            return null;
        return "That does not look like a CrossCode installation. Pick the folder " +
               "that contains nw.exe and package.json.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel
                      { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CrossCode is your own game (Steam). The CCMultiworldRandomizer " +
                   "mod and CCLoader are installed by this launcher into your CrossCode " +
                   "folder, but the mod's dependency mods must be installed via the " +
                   "in-game CCLoader mod manager after launch. You connect to your " +
                   "Archipelago server from the in-game login dialog (not pre-filled " +
                   "by this launcher).",
            FontSize = 11, Foreground = warn, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: CrossCode install ─────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CROSSCODE INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string? ccDir       = ResolveCrossCodeDir();
        string? overrideDir = LoadOverrideDir();
        bool    hasCCLoader = ccDir != null && Directory.Exists(Path.Combine(ccDir, "ccloader"));
        string? modPath     = FindInstalledMod();

        string detectMsg = ccDir != null
            ? (overrideDir != null
                ? "Detected (manual): " + ccDir
                : "Detected via Steam: " + ccDir)
            : "CrossCode not detected. Pick your install folder below, or install " +
              "CrossCode via Steam first.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = ccDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = hasCCLoader
                    ? "CCLoader: installed"
                    : "CCLoader: not found (use Install to set it up)",
            FontSize = 11,
            Foreground = hasCCLoader ? success : muted,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modPath != null
                    ? "CCMultiworldRandomizer: " + Path.GetFileName(modPath)
                    : "CCMultiworldRandomizer: not found in assets/mods/ (use Install)",
            FontSize = 11,
            Foreground = modPath != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Override folder picker
        var dirRow = new System.Windows.Controls.DockPanel
                     { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text       = overrideDir ?? ccDir ?? "",
            IsReadOnly = true, FontSize = 12,
            Margin     = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your CrossCode install folder (containing nw.exe and package.json). " +
                          "Detected from Steam automatically; set it here to override.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your CrossCode install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? ccDir ?? "")
                                   ? (overrideDir ?? ccDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateCrossCodeDir(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a CrossCode folder",
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
            Text = "Steam installs are detected automatically (appid 368340). " +
                   "Use this picker for non-standard Steam library locations.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Guided setup steps ────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "SETUP STEPS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own CrossCode on Steam (appid 368340) and install it.",
            "2. Use the Install button above. The launcher installs CCLoader and the " +
               "CCMultiworldRandomizer mod into your CrossCode folder.",
            "3. Launch CrossCode. Open the Options menu and press the mods hotkey. " +
               "In the CCLoader mod manager, install these dependencies: \"open-world\", " +
               "\"nax-ccuilib\", \"font-utils\". (ccmodmanager comes with CCLoader already.)",
            "4. Restart CrossCode after installing dependencies.",
            "5. To start an AP run: click \"New Game\" on the title screen. An Archipelago " +
               "login dialog will appear. Enter your server URL (e.g. archipelago.gg:38281), " +
               "slot name, and password. Click OK to connect and begin.",
            "6. To continue an AP run: select your save slot on the title screen. The " +
               "Archipelago login dialog will appear again. The connection info is pre-filled " +
               "from your last session.",
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
            Text = "LINKS", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("CCMultiworldRandomizer (GitHub) ↗",   ModRepoUrl),
            ("CCMultiworldRandomizer Setup Guide ↗", SetupGuideUrl),
            ("CCLoader mod loader (GitHub) ↗",       CCLoaderRepoUrl),
            ("CCLoader Wiki ↗",                      CCLoaderWikiUrl),
            ("CrossCode Archipelago Discord ↗",      ModDiscordUrl),
            ("Archipelago Official ↗",               ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background      = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0),
                FontSize = 12,
                Margin   = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor     = System.Windows.Input.Cursors.Hand,
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
                    Version: el.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "",
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

    /// Resolve the latest CCMultiworldRandomizer release: version + the .ccmod URL.
    private async Task<(string Version, string? Url)> ResolveLatestModReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? t.GetString()
                : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
            {
                string? ccmodUrl = null;
                string? anyUrl   = null;
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    anyUrl ??= url;
                    if (ccmodUrl == null &&
                        name.EndsWith(".ccmod", StringComparison.OrdinalIgnoreCase))
                        ccmodUrl = url;
                }
                string? chosen = ccmodUrl ?? anyUrl;
                if (chosen != null)
                    return (version, chosen);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable — use pinned fallback */ }

        return (FallbackModVersion, FallbackModUrl);
    }

    /// Resolve the latest CCLoader release tag for the zipball URL.
    private async Task<string> ResolveCCLoaderZipballAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_CCLOADER_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return FallbackCCLoaderZipball;

            // Take the first (latest) non-prerelease release.
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                bool pre = el.TryGetProperty("prerelease", out var p) && p.GetBoolean();
                if (pre) continue;
                string? tag = el.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                if (tag == null) continue;
                // CCLoader tags look like "v2.25.10/v2.14.2" — the zipball_url is safe to use.
                if (el.TryGetProperty("zipball_url", out var z) && z.ValueKind == JsonValueKind.String)
                {
                    string? zurl = z.GetString();
                    if (!string.IsNullOrWhiteSpace(zurl)) return zurl;
                }
                break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* network failure — use pinned fallback */ }

        return FallbackCCLoaderZipball;
    }

    // ── Private helpers — CrossCode directory detection ───────────────────────

    private string? ResolveCrossCodeDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeCrossCodeDir(ov)) return ov;
        try { return DetectSteamCrossCodeDir(); }
        catch { return null; }
    }

    private static bool LooksLikeCrossCodeDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            // CrossCode has nw.exe + package.json at the game root.
            if (File.Exists(Path.Combine(dir, "nw.exe")) &&
                File.Exists(Path.Combine(dir, "package.json"))) return true;
            // Fallback: just nw.exe (some installs may vary).
            if (File.Exists(Path.Combine(dir, "nw.exe"))) return true;
            return false;
        }
        catch { return false; }
    }

    private static string? DetectSteamCrossCodeDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{CCSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common     = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeCrossCodeDir(candidate)) return candidate;
                    }
                    // Conventional folder name on Steam
                    string conventional = Path.Combine(common, "CrossCode");
                    if (LooksLikeCrossCodeDir(conventional)) return conventional;
                }
                catch { /* try the next library */ }
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
            string raw = text.Substring(open + 1, close - open - 1);
            yield return raw.Replace("\\\\", "\\");
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

    // ── Private helpers — installed mod detection ─────────────────────────────

    /// Finds the installed CCMultiworldRandomizer .ccmod file or the unpacked
    /// "mw-rando" directory in assets/mods/. Returns the path or null.
    private string? FindInstalledMod()
    {
        try
        {
            string? cc = ResolveCrossCodeDir();
            if (cc == null) return null;
            string modsDir = Path.Combine(cc, "assets", "mods");
            if (!Directory.Exists(modsDir)) return null;

            // .ccmod file
            foreach (string f in Directory.EnumerateFiles(modsDir, "*.ccmod"))
            {
                string name = Path.GetFileName(f);
                if (name.StartsWith("CCMultiworldRandomizer", StringComparison.OrdinalIgnoreCase))
                    return f;
            }
            // Unpacked mod directory (mod id is "mw-rando" from ccmod.json)
            string unpacked = Path.Combine(modsDir, "mw-rando");
            if (Directory.Exists(unpacked)) return unpacked;
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartCrossCode()
    {
        string? cc  = ResolveCrossCodeDir();
        string? exe = cc != null ? Path.Combine(cc, "nw.exe") : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = cc!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start CrossCode.");

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

        // Fall back to Steam if we can find a Steam installation.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true;
                return;
            }
            catch { /* fall through */ }
        }

        throw new FileNotFoundException(
            "Could not find nw.exe. Open this game's Settings and pick your " +
            "CrossCode install folder, or install CrossCode via Steam.",
            "nw.exe");
    }

    // ── Private helpers — CCLoader install ────────────────────────────────────

    /// Download the CCLoader repo zipball and extract it into the CrossCode game
    /// root. The zipball wraps everything in a single top-level subfolder (GitHub
    /// standard: <org>-<repo>-<sha>/). We unwrap that so ccloader/, assets/ and
    /// package.json land directly in the game root, overwriting as needed.
    private async Task InstallCCLoaderAsync(
        string ccDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string zipballUrl = await ResolveCCLoaderZipballAsync(ct);

        string tempZip = Path.Combine(Path.GetTempPath(),
            $"ccloader-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"ccloader-extract-{Guid.NewGuid():N}");

        try
        {
            progress.Report((8, "Downloading CCLoader..."));
            using var response = await _http.GetAsync(
                zipballUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();

            long total      = response.Content.Headers.ContentLength ?? -1;
            long downloaded = 0;

            await using (var src = await response.Content.ReadAsStreamAsync(ct))
            await using (var dst = File.Create(tempZip))
            {
                var buf = new byte[81920];
                int bytesRead;
                while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                    downloaded += bytesRead;
                    if (total > 0)
                    {
                        int pct = (int)(8 + 20 * downloaded / total);
                        progress.Report((pct, $"Downloading CCLoader... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((28, "Extracting CCLoader into CrossCode folder..."));
            Directory.CreateDirectory(tempExtract);
            ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // Unwrap the single top-level GitHub subfolder if present.
            string sourceRoot = tempExtract;
            string[] topDirs  = Directory.GetDirectories(tempExtract);
            string[] topFiles = Directory.GetFiles(tempExtract);
            if (topFiles.Length == 0 && topDirs.Length == 1)
                sourceRoot = topDirs[0];

            // Copy all files from sourceRoot into ccDir, preserving subdirectory structure.
            foreach (string fileSrc in Directory.EnumerateFiles(
                sourceRoot, "*", SearchOption.AllDirectories))
            {
                string rel     = Path.GetRelativePath(sourceRoot, fileSrc);
                string fileDst = Path.Combine(ccDir, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                File.Copy(fileSrc, fileDst, overwrite: true);
            }

            progress.Report((38, "CCLoader extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip))       File.Delete(tempZip);              } catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, true); } catch { }
        }
    }

    // ── Private helpers — mod install ─────────────────────────────────────────

    /// Download the .ccmod file and place it in <ccDir>/assets/mods/.
    /// A .ccmod is a ZIP archive; placing it there is sufficient for CCLoader.
    private async Task DownloadCCModAsync(
        string ccmodUrl,
        string version,
        string modsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        // Remove any previous version of the .ccmod file.
        foreach (string old in Directory.EnumerateFiles(modsDir, "CCMultiworldRandomizer*.ccmod"))
        {
            try { File.Delete(old); } catch { }
        }

        string destFile = Path.Combine(modsDir,
            $"CCMultiworldRandomizer-{version}.ccmod");

        progress.Report((46, $"Downloading CCMultiworldRandomizer {version}..."));

        using var response = await _http.GetAsync(
            ccmodUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total      = response.Content.Headers.ContentLength ?? -1;
        long downloaded = 0;

        await using var src = await response.Content.ReadAsStreamAsync(ct);
        await using var dst = File.Create(destFile);
        {
            var buf = new byte[81920];
            int bytesRead;
            while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                downloaded += bytesRead;
                if (total > 0)
                {
                    int pct = (int)(46 + 48 * downloaded / total);
                    progress.Report((pct, $"Downloading CCMultiworldRandomizer... {downloaded / 1000}KB"));
                }
            }
            await dst.FlushAsync(ct);
        }
    }

    // ── Private helpers — sidecar settings ───────────────────────────────────

    private sealed class CCSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private CCSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<CCSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(CCSettings s)
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

    private void SaveOverrideDir(string p)
    {
        var s = LoadSettings();
        s.InstallOverride = p;
        SaveSettings(s);
    }

    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings();
        s.ModVersion = v;
        SaveSettings(s);
    }
}
