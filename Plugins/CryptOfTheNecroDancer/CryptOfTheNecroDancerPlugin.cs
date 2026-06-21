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

namespace LauncherV2.Plugins.CryptOfTheNecroDancer;

// ═══════════════════════════════════════════════════════════════════════════════
// CryptOfTheNecroDancerPlugin — install / launch for "Crypt of the NecroDancer"
// (Brace Yourself Games, Steam appid 247080) played through the CotND-Archipelago
// mod by lastingParadox, which ships a BepInEx plugin that handles the in-game
// Archipelago connection. This is a NATIVE "ConnectsItself" integration in the
// same family as RiftOfTheNecroDancer / HollowKnight / TUNIC: the game mod
// itself speaks to the AP server — the launcher must NOT hold its own ApClient
// on the same slot while the game runs.
//
// ── VERIFIED FACTS (2026-06-15) ───────────────────────────────────────────────
//   Sources:
//     • lastingParadox/Archipelago-CotND  — the apworld (server-side)
//     • lastingParadox/CotND-Archipelago  — the in-game BepInEx mod (client-side)
//     • https://archipelago.miraheze.org/wiki/Crypt_of_the_NecroDancer
//
//   * GAME: Crypt of the NecroDancer (Steam appid 247080).
//     IMPORTANT: The Archipelago integration targets the SYNCHRONY DLC / update
//     (free DLC, required for the BepInEx mod to function). Users must have the
//     Synchrony DLC enabled.
//
//   * AP WORLD game string (from the Archipelago wiki page and world registration):
//       "Crypt of the NecroDancer"
//     GameId here: "crypt_of_the_necrodancer".
//
//   * MOD REPO (client-side, in-game): lastingParadox/CotND-Archipelago (GitHub).
//     The mod is a BepInEx 5 (Unity MONO) plugin. Releases ship a zip that
//     contains a BepInEx/ folder tree; extract it into the game directory root
//     so the plugin DLL lands under BepInEx\plugins\.
//
//   * APWORLD REPO (server-side): lastingParadox/Archipelago-CotND (GitHub).
//     The .apworld file from these releases is dropped into the Archipelago
//     server's `worlds/` folder (or custom_worlds/ depending on version).
//     This launcher does NOT install the .apworld — that is a server-side
//     step the player does once for their AP server installation.
//
//   * BepInEx 5 (Unity MONO) is NOT bundled in the mod zip.
//     The user must install BepInEx 5 first. The plugin surfaces this clearly.
//
//   * CONNECTION is performed IN-GAME via the mod's UI.
//     There is no command-line argument or config file the launcher can pre-write.
//     The settings panel exposes the current session credentials for the user to
//     copy into the in-game fields. This launcher does NOT attempt a prefill.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Crypt of the NecroDancer install via the Windows registry
//      (HKCU\Software\Valve\Steam → SteamPath, WOW6432Node Steam path) + parsing
//      steamapps\libraryfolders.vdf + locating appmanifest_247080.acf.
//      Manual install-dir override supported via a plugin-local sidecar JSON.
//   2. INSTALL/UPDATE: download the CotND-Archipelago mod zip from GitHub releases
//      and extract the BepInEx/ folder tree into the game directory. Warns if
//      BepInEx core is absent.
//   3. LAUNCH: try known exe names in the detected/override install; fall back to
//      steam://rungameid/247080. ConnectsItself = true. SupportsStandalone = true.
//
// ── BUILD NOTE ────────────────────────────────────────────────────────────────
// UseWindowsForms + UseWPF are both true in this project. All WPF UI types are
// fully qualified (System.Windows.Controls.*, System.Windows.Media.*) or imported
// via the top-level using directives. No local re-aliases (avoids CS1537).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class CryptOfTheNecroDancerPlugin : IGamePlugin
{
    // ── Constants — mod repo (lastingParadox/CotND-Archipelago) ──────────────
    private const string MOD_OWNER = "lastingParadox";
    private const string MOD_REPO  = "CotND-Archipelago";
    private const string ModRepoUrl          = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_RELEASES_URL     = $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";
    private const string GH_RELEASES_LATEST  = $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";

    // apworld repo (server-side — informational link only; launcher doesn't install it).
    private const string ApWorldRepoUrl = "https://github.com/lastingParadox/Archipelago-CotND";

    private const string BepInExUrl       = "https://github.com/BepInEx/BepInEx/releases";
    private const string ArchipelagoSite  = "https://archipelago.gg";

    // Steam — Crypt of the NecroDancer appid 247080.
    private const string SteamAppId       = "247080";
    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    /// Standard Steam common sub-folder name for this game.
    private const string SteamCommonFolderName = "Crypt of the NecroDancer";

    /// Known exe names for direct launch.
    private static readonly string[] KnownExeNames =
    {
        "Crypt of the NecroDancer.exe",
        "CryptOfTheNecroDancer.exe",
        "necrodancer.exe",
    };

    /// Pinned fallback version when the GitHub API is unreachable.
    private const string FallbackVersion = "1.0.0";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "crypt_of_the_necrodancer";
    public string DisplayName => "Crypt of the NecroDancer";
    public string Subtitle    => "Rhythm roguelike · BepInEx mod · built-in AP client";

    /// Exact AP game string registered by lastingParadox/Archipelago-CotND.
    public string ApWorldName => "Crypt of the NecroDancer";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "crypt_of_the_necrodancer.png");

    public string ThemeAccentColor => "#8B1A1A";   // deep crimson (dungeon torchlight)
    public string[] GameBadges     => new[] { "Steam · Synchrony DLC · needs mod" };

    public string Description =>
        "Crypt of the NecroDancer is Brace Yourself Games' rhythm-based roguelike dungeon " +
        "crawler where every move must be made to the beat of the music. The Archipelago " +
        "integration is provided by the CotND-Archipelago BepInEx mod (by lastingParadox), " +
        "which contains a built-in Archipelago client — so the game itself connects to the " +
        "multiworld without any emulator or Lua bridge. You bring your own copy of Crypt of " +
        "the NecroDancer (Steam); the Synchrony DLC (free) is required. BepInEx 5 (Unity " +
        "MONO) and the CotND-Archipelago mod are added on top. The launcher detects your " +
        "Steam install, stages the mod files, and guides the remaining steps. Connection " +
        "to your Archipelago server is completed in-game via the mod's UI.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── AP bridge events — unused (ConnectsItself) ────────────────────────────
    // The in-game mod handles all AP communication; the launcher never touches
    // the AP slot. These events exist only for IGamePlugin interface compliance.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" = the mod DLL is present under BepInEx\plugins in the game dir.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── ConnectsItself / SupportsStandalone ───────────────────────────────────
    public bool ConnectsItself      => true;
    public bool SupportsStandalone  => true;

    /// Not tracked — the mod owns the AP data package contract.
    public string? BuiltAgainstDataPackageChecksum => null;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Working directory for this plugin's downloads and bookkeeping.
    public string GameDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "Games", "CryptOfTheNecroDancer");

    private string SidecarDir =>
        Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SidecarPath =>
        Path.Combine(SidecarDir, "cotnd_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────
    private Process? _gameProcess;

    // ── Lifecycle — CheckForUpdateAsync ──────────────────────────────────────

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
            // CDN HEAD redirect — no REST API quota consumed.
            AvailableVersion = GitHubHelper.NormalizeTag(
                await GitHubHelper.FetchLatestTagAsync(MOD_OWNER, MOD_REPO, ct));
        }
        catch
        {
            AvailableVersion = null; // contract: never throw on network failure
        }
    }

    // ── Lifecycle — InstallOrUpdateAsync ─────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((2, "Locating your Crypt of the NecroDancer installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Crypt of the NecroDancer installation. Open this " +
                "game's Settings and pick your install folder (the folder containing " +
                "the game exe), or install the game via Steam first. The Archipelago " +
                "mod is added on top of your own copy of the game.");

        progress.Report((6, "Checking the latest CotND-Archipelago mod release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the CotND-Archipelago mod download on GitHub. Check " +
                "your internet connection, or download the zip manually from " +
                ModRepoUrl + "/releases — see Settings for the guided steps.");

        await DownloadAndExtractModAsync(zipUrl, version, gameDir, progress, ct);

        WriteStampedVersion(version);
        InstalledVersion = version;

        bool bepInExPresent =
            Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));

        progress.Report((100,
            $"Installed CotND-Archipelago {version} into your Crypt of the NecroDancer " +
            "BepInEx\\plugins folder. " +
            (bepInExPresent
                ? "BepInEx looks present. "
                : "IMPORTANT: BepInEx 5 (Unity MONO) is NOT included in this download " +
                  "and must be installed first. Download BepInEx 5 from the link in " +
                  "Settings, extract its contents into your game root, run the game " +
                  "once so BepInEx generates its config, then re-run Install. ") +
            "To play: launch the game, enter your Archipelago server details in the " +
            "mod's in-game UI, and start your run. Open Settings for guided steps."));
    }

    // ── Lifecycle — VerifyInstallAsync ────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── Lifecycle — LaunchAsync ───────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: connection credentials are entered in-game via the mod's UI.
        // There is no config file or command-line argument to pre-fill.
        // ConnectsItself = true: we must NOT hold our own ApClient on this slot.
        _ = session; // intentionally unused
        StartGame();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartGame();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself) ───────────────────────────────────

    public Task ReceiveItemsAsync(
        ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var accent  = new SolidColorBrush(Color.FromRgb(0x8B, 0x1A, 0x1A));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var linkClr = new SolidColorBrush(Color.FromRgb(0xCC, 0x66, 0x66));
        _ = accent; // suppress unused warning

        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();
        string? modDll      = FindInstalledModDll();
        bool    bepInExOk   =
            gameDir != null &&
            Directory.Exists(Path.Combine(gameDir, "BepInEx", "core"));

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text =
                "Crypt of the NecroDancer is your own game (Steam) with the " +
                "CotND-Archipelago BepInEx mod added on top. The Synchrony DLC (free) " +
                "is required. The launcher detects your Steam install and stages the " +
                "mod files. BepInEx 5 (Unity MONO) must be installed separately first " +
                "— see the guided steps below. Connection to your Archipelago server " +
                "is entered in-game via the mod's UI; this launcher cannot pre-fill it.",
            FontSize     = 11,
            Foreground   = warn,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 14),
        });

        // ── Section: Game install ─────────────────────────────────────────
        panel.Children.Add(MakeSectionHeader("CRYPT OF THE NECRODANCER INSTALL", muted));

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Game not detected. Pick your install folder below, or install " +
              "Crypt of the NecroDancer via Steam first.";

        panel.Children.Add(new TextBlock
        {
            Text         = detectMsg,
            FontSize     = 11,
            Foreground   = gameDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 6),
        });

        // BepInEx status
        panel.Children.Add(new TextBlock
        {
            Text = bepInExOk
                ? "BepInEx found (BepInEx\\core present)."
                : "BepInEx not found yet. Install BepInEx 5 (Unity MONO) into your " +
                  "game root first (see guided steps and link below).",
            FontSize     = 11,
            Foreground   = bepInExOk ? success : warn,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 4),
        });

        // Mod DLL status
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                ? "CotND-Archipelago mod found: " + modDll
                : "CotND-Archipelago mod not found in BepInEx\\plugins yet. " +
                  "Use the Install button on the Play tab, or install it by hand " +
                  "from the mod releases (link below).",
            FontSize     = 11,
            Foreground   = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 8),
        });

        // Folder picker row
        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text        = overrideDir ?? gameDir ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     =
                "Your Crypt of the NecroDancer install folder (contains the game exe). " +
                "Detected from Steam; set it here to override.",
        };
        var dirBtn = new Button
        {
            Content     = "Select folder...",
            Width       = 120,
            Padding     = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Crypt of the NecroDancer install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                                   ? (overrideDir ?? gameDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() != true) return;

            string picked = dlg.FolderName;
            // Allow picking a parent "common" folder; descend into the game sub-folder.
            if (!LooksLikeGameDir(picked))
            {
                string nested = Path.Combine(picked, SteamCommonFolderName);
                if (LooksLikeGameDir(nested)) picked = nested;
            }
            if (!LooksLikeGameDir(picked))
            {
                System.Windows.MessageBox.Show(
                    "That does not look like a Crypt of the NecroDancer installation. " +
                    "Pick the folder that contains the game exe " +
                    @"(e.g. ...\steamapps\common\Crypt of the NecroDancer).",
                    "Not a Crypt of the NecroDancer folder",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
            SaveOverrideDir(picked);
            dirBox.Text = picked;
        };
        DockPanel.SetDock(dirBtn, Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new TextBlock
        {
            Text =
                "Steam installs are detected automatically (appid 247080). " +
                "Use this picker for a non-standard Steam library or GOG install.",
            FontSize     = 11,
            Foreground   = muted,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Synchrony DLC reminder ──────────────────────────────
        panel.Children.Add(MakeSectionHeader("SYNCHRONY DLC (REQUIRED)", muted));
        panel.Children.Add(new TextBlock
        {
            Text =
                "The CotND-Archipelago mod targets the Synchrony update of Crypt of " +
                "the NecroDancer. Synchrony is a free DLC available on Steam. Make " +
                "sure it is installed and enabled. The mod will not work on the base " +
                "Legacy version of the game.",
            FontSize     = 11,
            Foreground   = fg,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Connecting ───────────────────────────────────────────
        panel.Children.Add(MakeSectionHeader("CONNECTING (entered in-game)", muted));
        panel.Children.Add(new TextBlock
        {
            Text =
                "Launch the game with BepInEx and the mod installed. The mod provides " +
                "an in-game menu to enter your Archipelago server address, port, slot " +
                "name, and password (if any). This launcher cannot pre-fill these " +
                "fields — copy your session details from the Play tab into the " +
                "in-game connection UI manually.",
            FontSize     = 11,
            Foreground   = fg,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(MakeSectionHeader("GUIDED SETUP", muted));
        foreach (string step in new[]
        {
            "1. Own Crypt of the NecroDancer on Steam, with the Synchrony DLC enabled. " +
                "Use the folder picker above if it was not detected automatically.",
            "2. Install BepInEx 5 (Unity MONO build — NOT IL2CPP and NOT the pre-release " +
                "BepInEx 6 builds). Download the BepInEx 5 zip from the link below, " +
                "extract its contents (the BepInEx/ folder + doorstop files) directly " +
                "into your Crypt of the NecroDancer install folder.",
            "3. Launch the game once so BepInEx generates its config folders, then " +
                "close it. You should now see a BepInEx\\core\\ folder in your install.",
            "4. Install the CotND-Archipelago mod: use the Install button on the Play " +
                "tab (downloads and extracts the mod zip into your game root), or " +
                "download the zip manually from the mod releases link below.",
            "5. Install the Archipelago server-side .apworld: download " +
                "Archipelago-CotND from the apworld repo (see link below) and place " +
                "it in your Archipelago server's worlds/ or custom_worlds/ folder.",
            "6. Launch the game. The mod loads via BepInEx. Enter your Archipelago " +
                "server, port, slot name, and password in the mod's in-game UI, then " +
                "start your run.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text         = step,
                FontSize     = 11,
                Foreground   = fg,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Section: Links ────────────────────────────────────────────────
        panel.Children.Add(MakeSectionHeader("LINKS", muted));
        foreach (var (label, url) in new (string, string)[]
        {
            ("CotND-Archipelago mod (GitHub) ↗",         ModRepoUrl),
            ("CotND-Archipelago mod releases ↗",         ModRepoUrl + "/releases"),
            ("Archipelago-CotND apworld (server) ↗",     ApWorldRepoUrl),
            ("Archipelago-CotND apworld releases ↗",     ApWorldRepoUrl + "/releases"),
            ("BepInEx 5 Releases (Unity MONO) ↗",        BepInExUrl),
            ("Archipelago Official ↗",                   ArchipelagoSite),
            ("Crypt of the NecroDancer on Steam ↗",
                "https://store.steampowered.com/app/247080/Crypt_of_the_NecroDancer/"),
        })
        {
            var btn = new Button
            {
                Content             = label,
                HorizontalAlignment = HorizontalAlignment.Left,
                Padding             = new Thickness(0, 2, 0, 2),
                Background          = Brushes.Transparent,
                BorderThickness     = new Thickness(0),
                FontSize            = 12,
                Margin              = new Thickness(0, 0, 0, 4),
                Foreground          = linkClr,
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
                if (el.TryGetProperty("published_at", out var d) &&
                    d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name",     out var n) ? n.GetString() ?? "" : "",
                    Body:    el.TryGetProperty("body",     out var b) ? b.GetString() ?? "" : "",
                    Version: NormalizeTag(
                        el.TryGetProperty("tag_name", out var t) ? t.GetString() : null) ?? "",
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var hu) ? hu.GetString() : null
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

    // ── Private helpers — release resolution ─────────────────────────────────

    /// "v1.2.3" → "1.2.3"; also trims whitespace.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest mod release: (version, first .zip asset URL).
    /// Falls back to a pinned URL when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)>
        ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_LATEST, ct);
            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = NormalizeTag(
                root.TryGetProperty("tag_name", out var t) ? t.GetString() : null);

            if (version != null &&
                root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
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
                    if (preferred == null &&
                        (lower.Contains("cotnd") || lower.Contains("necrodancer") ||
                         lower.Contains("archipelago")))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null) return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited */ }

        // Pinned fallback.
        string fallback =
            $"{ModRepoUrl}/releases/download/v{FallbackVersion}/CotND-Archipelago.zip";
        return (FallbackVersion, fallback);
    }

    // ── Private helpers — Steam / game detection ──────────────────────────────

    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeGameDir(ov)) return ov;
        try { return DetectSteamGameDir(); }
        catch { return null; }
    }

    private static string? DetectSteamGameDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{SteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common   = Path.Combine(steamapps, "common");
                    string? instDir = ReadAcfInstallDir(manifest);
                    if (instDir != null)
                    {
                        string candidate = Path.Combine(common, instDir);
                        if (LooksLikeGameDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeGameDir(conventional)) return conventional;
                }
                catch { /* try next library */ }
            }
        }
        return null;
    }

    private static IEnumerable<string> SteamRoots()
    {
        string? hkcu = ReadRegistryString(
            Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
        if (!string.IsNullOrWhiteSpace(hkcu)) yield return NormalizeSteamPath(hkcu);

        string? hklm = ReadRegistryString(
            Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm)) yield return NormalizeSteamPath(hklm);

        string? hklm64 = ReadRegistryString(
            Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm64)) yield return NormalizeSteamPath(hklm64);

        string? x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(x86))
            yield return Path.Combine(x86, "Steam");
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
            int open  = text.IndexOf('"', i);
            if (open  < 0) yield break;
            int close = text.IndexOf('"', open + 1);
            if (close < 0) yield break;
            yield return text.Substring(open + 1, close - open - 1)
                              .Replace("\\\\", "\\");
            i = close + 1;
        }
    }

    private static string? ReadAcfInstallDir(string acfPath)
    {
        try
        {
            string text     = File.ReadAllText(acfPath);
            const string k  = "\"installdir\"";
            int i           = text.IndexOf(k, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            i += k.Length;
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

    // ── Private helpers — game dir validation ─────────────────────────────────

    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            foreach (string exe in KnownExeNames)
                if (File.Exists(Path.Combine(dir, exe))) return true;
            // A BepInEx folder is a strong secondary signal.
            if (Directory.Exists(Path.Combine(dir, "BepInEx"))) return true;
            return false;
        }
        catch { return false; }
    }

    // ── Private helpers — mod DLL detection ──────────────────────────────────

    /// Find the CotND-Archipelago mod DLL under BepInEx\plugins (recursive).
    /// Accepts any *.dll whose name mentions "archipelago" or "cotnd".
    private string? FindInstalledModDll()
    {
        try
        {
            string? game = ResolveGameDir();
            if (game == null) return null;
            string pluginsDir = Path.Combine(game, "BepInEx", "plugins");
            if (!Directory.Exists(pluginsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(
                pluginsDir, "*.dll", SearchOption.AllDirectories))
            {
                string lower = Path.GetFileNameWithoutExtension(dll).ToLowerInvariant();
                if (lower.Contains("archipelago") || lower.Contains("cotnd"))
                    return dll;
            }

            // Sub-folder named "archipelago" that contains any DLL.
            foreach (string sub in Directory.EnumerateDirectories(
                pluginsDir, "*", SearchOption.AllDirectories))
            {
                if (!Path.GetFileName(sub).Contains("archipelago",
                        StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (Directory.EnumerateFiles(sub, "*.dll",
                            SearchOption.AllDirectories).Any())
                        return sub;
                }
                catch { }
            }
        }
        catch { }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        string? game = ResolveGameDir();

        if (game != null)
        {
            foreach (string exeName in KnownExeNames)
            {
                string exePath = Path.Combine(game, exeName);
                if (!File.Exists(exePath)) continue;

                var proc = Process.Start(new ProcessStartInfo
                {
                    FileName         = exePath,
                    WorkingDirectory = game,
                    UseShellExecute  = true,
                }) ?? throw new InvalidOperationException(
                    "Failed to start Crypt of the NecroDancer.");

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
                catch { /* non-fatal */ }
                return;
            }
        }

        // Fall back to the Steam URI when the exe is not found.
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
            "Could not find the Crypt of the NecroDancer executable. Open this game's " +
            "Settings and pick your install folder, or install the game via Steam.",
            SteamCommonFolderName);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string gameDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"cotnd-archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"cotnd-archipelago-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading CotND-Archipelago {version}..."));
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
                        progress.Report((pct,
                            $"Downloading CotND-Archipelago... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting mod package..."));
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, tempDir, overwriteFiles: true);

            progress.Report((85, "Installing mod into the game folder..."));

            // The zip ships a BepInEx/ folder tree. Extract into the game root so
            // the DLL lands in BepInEx\plugins\. If no top-level BepInEx/ is found,
            // search one level deep; last resort copies everything to the game root.
            string bepSrc = Path.Combine(tempDir, "BepInEx");
            if (Directory.Exists(bepSrc))
            {
                CopyDirectoryContents(bepSrc, Path.Combine(gameDir, "BepInEx"));
            }
            else
            {
                bool found = false;
                foreach (string sub in Directory.EnumerateDirectories(tempDir))
                {
                    string nested = Path.Combine(sub, "BepInEx");
                    if (!Directory.Exists(nested)) continue;
                    CopyDirectoryContents(nested, Path.Combine(gameDir, "BepInEx"));
                    found = true;
                    break;
                }
                if (!found) CopyDirectoryContents(tempDir, gameDir);
            }

            progress.Report((95, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))      File.Delete(tempZip); }              catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); }   catch { }
        }
    }

    private static void CopyDirectoryContents(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string rel  = Path.GetRelativePath(src, file);
            string dest = Path.Combine(dst, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    // ── Private helpers — sidecar (persists install-dir override + version stamp)

    private sealed class CotNDSidecar
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private CotNDSidecar LoadSidecar()
    {
        try
        {
            if (File.Exists(SidecarPath))
            {
                string txt = File.ReadAllText(SidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<CotNDSidecar>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSidecar(CotNDSidecar s)
    {
        try
        {
            Directory.CreateDirectory(SidecarDir);
            File.WriteAllText(SidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { }
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

    // ── Small UI factory helpers ──────────────────────────────────────────────

    private static TextBlock MakeSectionHeader(string text, Brush color)
        => new()
        {
            Text       = text,
            FontSize   = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = color,
            Margin     = new Thickness(0, 4, 0, 8),
        };
}
