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

namespace LauncherV2.Plugins.LittleWitchNobeta;

// ═══════════════════════════════════════════════════════════════════════════════
// LittleWitchNobetaPlugin — install / launch for "Little Witch Nobeta"
// (Pupuya Games, 2022) played through the LittleWitchNobetaAP MelonLoader mod
// (github.com/danielgruethling/LittleWitchNobetaAP). This is a NATIVE
// "ConnectsItself" integration: the mod speaks to the AP multiworld itself
// with its own in-game ImGUI connection window (no emulator, no Lua bridge,
// no launcher-held ApClient on the slot).
//
// ── HONEST REALITY CHECK (2026-06-14, verified against the mod repo) ──────────
//
//   * THE AP WORLD game string is "Little Witch Nobeta" — verified against
//     ArchipelagoClient.cs: `private const string Game = "Little Witch Nobeta";`
//     in danielgruethling/LittleWitchNobetaAP. GameId = "lwn".
//
//   * THE MOD REPO is danielgruethling/LittleWitchNobetaAP. Latest verified
//     release: v0.2.2 (tag "0.2.2"), single release asset "LWNAP.zip".
//
//   * MOD LOADER: MelonLoader (NOT BepInEx). The mod is built against MelonLoader
//     and derives from MelonMod. MelonLoader mods live in
//     <GameDir>/Mods/<ModName>.dll, so the LWNAP.zip contents are extracted into
//     <GameDir>/Mods/. MelonLoader itself must already be installed in the game
//     folder — this plugin guides the user to MelonLoader's setup page.
//
//   * HOW IT CONNECTS — VIA AN IN-GAME IMGUI WINDOW. When the player creates or
//     loads a save file the mod intercepts the action, blocks movement, and
//     shows a GUI window ("Archipelago Connection") where the player types the
//     hostname, port, player name, and password. There is NO static config file
//     that this launcher can pre-write (connection data is stored inside the game
//     via MelonPreferences under UserData/, not in a plain .cfg the launcher
//     controls before launch). So this plugin does NOT attempt connection prefill;
//     the settings panel and post-install note state this honestly.
//
//   * ConnectsItself = true: the LittleWitchNobetaAP mod owns the slot connection
//     while running. The launcher must NOT hold its own ApClient on the same slot.
//     SupportsStandalone = true: the plain game runs without the mod.
//
//   * Steam AppID: 1049890 (verified from store.steampowered.com/app/1049890).
//     Steam common folder: "Little Witch Nobeta".
//     Main executable: LittleWitchNobeta.exe.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Little Witch Nobeta install via the Windows registry
//      (HKCU\Software\Valve\Steam → SteamPath and HKLM WOW6432Node path),
//      parsing steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Little Witch Nobeta via appmanifest_1049890.acf.
//      A manual install-dir OVERRIDE (settings folder picker) is also supported;
//      it is validated and persisted in this plugin's OWN sidecar
//      (Games/ROMs/lwn/lwn_launcher.json).
//   2. INSTALL/UPDATE: download LWNAP.zip from GitHub and extract it into
//      <GameDir>/Mods/. Also opens the MelonLoader setup guide (MelonLoader must
//      be installed first). Version stamped to sidecar for the tile.
//   3. LAUNCH: run LittleWitchNobeta.exe from the detected/override install; if
//      the exe cannot be found but Steam is present, fall back to
//      steam://rungameid/1049890.
//
// ── DEFENSIVE NOTES ───────────────────────────────────────────────────────────
//   * "Installed" is judged by the presence of LittleWitchNobetaAP.dll in the
//     detected/override game's Mods folder.
//   * No plaintext AP password is ever written by this plugin (connection is
//     entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class LittleWitchNobetaPlugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// Steam AppID for Little Witch Nobeta — verified 2026-06-14.
    private const string SteamAppId = "1049890";

    /// The conventional Steam install folder name.
    private const string SteamCommonFolderName = "Little Witch Nobeta";

    /// The main game executable.
    private const string GameExeName = "LittleWitchNobeta.exe";

    /// The AP mod DLL written into <GameDir>/Mods/ after install.
    private const string ModDllName = "LittleWitchNobetaAP.dll";

    // GitHub — the AP mod repo (danielgruethling/LittleWitchNobetaAP)
    private const string ModOwner           = "danielgruethling";
    private const string ModRepoName        = "LittleWitchNobetaAP";
    private const string ModRepoUrl         = $"https://github.com/{ModOwner}/{ModRepoName}";
    private const string GhReleasesLatest   = $"https://api.github.com/repos/{ModOwner}/{ModRepoName}/releases/latest";
    private const string GhReleasesAll      = $"https://api.github.com/repos/{ModOwner}/{ModRepoName}/releases";

    /// Pinned fallback when the GitHub API is unreachable. Verified live 2026-06-14.
    private const string FallbackVersion  = "0.2.2";
    private const string FallbackZipName  = "LWNAP.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackVersion}/{FallbackZipName}";

    // MelonLoader — must be installed separately by the user before the mod works.
    private const string MelonLoaderUrl       = "https://melonwiki.xyz/#/README";
    private const string MelonLoaderDownload  = "https://github.com/LavaGang/MelonLoader/releases/latest";
    private const string SetupGuideUrl        = "https://archipelago.gg/tutorial/Little%20Witch%20Nobeta/setup/en";
    private const string ArchipelagoSite      = "https://archipelago.gg";

    private static readonly string SteamRunUrl = $"steam://rungameid/{SteamAppId}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "lwn";
    public string DisplayName => "Little Witch Nobeta";
    public string Subtitle    => "Native PC · MelonLoader mod";

    /// EXACT AP game string — verified against ArchipelagoClient.cs in
    /// danielgruethling/LittleWitchNobetaAP:
    ///   `private const string Game = "Little Witch Nobeta";`
    public string ApWorldName => "Little Witch Nobeta";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "little_witch_nobeta.png");

    public string ThemeAccentColor => "#8A3FA0";   // witch purple
    public string[] GameBadges     => new[] { "Steam · needs MelonLoader" };

    public string Description =>
        "Little Witch Nobeta, the action game by Pupuya Games, played through the " +
        "LittleWitchNobetaAP MelonLoader mod (by danielgruethling). Magic spells, " +
        "boss tokens, boss souls, upgrades, and more are shuffled into the multiworld. " +
        "The mod uses MelonLoader (a Unity mod loader) and connects to the Archipelago " +
        "server through an in-game connection window that appears when you create or " +
        "load a save. You bring your own copy of Little Witch Nobeta (on Steam); the " +
        "launcher detects your install, downloads the mod, and guides the MelonLoader " +
        "setup. Connection details are entered in-game — there is no config file to " +
        "pre-fill.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the mod DLL is present in <GameDir>/Mods/.
    public bool IsInstalled => FindInstalledModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory
    {
        get => ResolveGameDir() ?? Path.Combine(AppContext.BaseDirectory, "Games", "LittleWitchNobeta");
        set { if (!string.IsNullOrWhiteSpace(value)) SaveOverrideDir(value); }
    }

    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "lwn_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // LittleWitchNobetaAP reports checks/items/goal to the AP server itself.
    // These exist for interface compatibility (ConnectsItself = true).
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
        catch { InstalledVersion = null; }

        try
        {
            var (version, _) = await ResolveLatestModAsync(ct);
            AvailableVersion = version;
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        // 0. Need the game install to place the mod into.
        progress.Report((2, "Locating your Little Witch Nobeta installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Little Witch Nobeta installation. Open this game's " +
                "Settings and pick your install folder (the one containing " +
                $"{GameExeName}), or install Little Witch Nobeta via Steam first.");

        string modsDir = Path.Combine(gameDir, "Mods");

        // 1. Resolve the latest release.
        progress.Report((6, "Checking the latest LittleWitchNobetaAP release..."));
        var (version, zipUrl) = await ResolveLatestModAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the LittleWitchNobetaAP download on GitHub. " +
                "Check your internet connection, or download the mod manually from " +
                ModRepoUrl + " and extract it into the Mods folder yourself.");

        // 2. Remind the user that MelonLoader must already be installed.
        progress.Report((10, "Opening the MelonLoader download page (required before the mod)..."));
        OpenUrl(MelonLoaderDownload);

        // 3. Download and extract the mod zip into <GameDir>/Mods/.
        await DownloadAndExtractModAsync(zipUrl, version, modsDir, progress, ct);

        // 4. Stamp version for the tile.
        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"LittleWitchNobetaAP {version} extracted into the Mods folder. " +
            "IMPORTANT: MelonLoader must be installed into your game folder first — " +
            "the MelonLoader download page was opened. If you have not installed it, " +
            "run the MelonLoader installer pointed at your Little Witch Nobeta folder. " +
            "To play: launch the game, create or load a save, and fill in the " +
            "Archipelago connection window that appears (host, port, player name, " +
            "password). This launcher cannot pre-fill those fields."));
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
        // HONEST: the AP server connection for Little Witch Nobeta is entered in
        // the IN-GAME Archipelago connection window (hostname / port / player name /
        // password). The mod stores these in MelonPreferences (UserData/ inside the
        // game folder) at runtime — there is no static config file this launcher can
        // pre-write before the game starts (verified against the mod source).
        // ConnectsItself = true: the mod owns the slot, so the launcher must NOT
        // hold its own ApClient on this slot.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartGame();
        return Task.CompletedTask;
    }

    /// Plain (non-AP) Little Witch Nobeta runs without the mod.
    public bool SupportsStandalone => true;

    /// The LittleWitchNobetaAP mod owns the slot connection (see header).
    public bool ConnectsItself => true;

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
        // No plaintext AP password is ever written by this plugin (connection
        // is entered in-game via the mod's own ImGUI window), so nothing to scrub.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert ─────────────────────────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // LittleWitchNobetaAP receives items from the AP server directly via its
        // own Archipelago.MultiClient.Net session; there is nothing for the launcher
        // to forward.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP connection status in the in-game ImGUI console.
    }

    // ── Existing-install validation (override picker) ─────────────────────────

    /// Return null when the folder is acceptable, else a short reason.
    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Little Witch Nobeta install folder.";

        if (LooksLikeGameDir(folder))
            return null;

        // Be forgiving: the user may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeGameDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return $"That does not look like a Little Witch Nobeta installation. Pick the " +
               $"folder that contains {GameExeName}.";
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Little Witch Nobeta is your own game (Steam) with the " +
                   "LittleWitchNobetaAP MelonLoader mod added on top. The launcher " +
                   "detects your Steam install and can download the mod, but MelonLoader " +
                   "itself must be installed into the game folder first (a separate step). " +
                   "Connection details are entered in the in-game Archipelago window that " +
                   "appears when you create or load a save — there is no config file to " +
                   "pre-fill. These external steps are not verified by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ──────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LITTLE WITCH NOBETA INSTALL", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 0, 0, 8),
        });

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();
        string  detectMsg   = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Little Witch Nobeta not detected. Pick your install folder below, " +
              "or install the game via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // mod DLL status
        string? modDll = FindInstalledModDll();
        panel.Children.Add(new TextBlock
        {
            Text = modDll != null
                    ? "LittleWitchNobetaAP mod found: " + modDll
                    : "LittleWitchNobetaAP mod not found in the Mods folder yet " +
                      "(use Install on the Play tab, or download it manually from " +
                      "the GitHub repo).",
            FontSize = 11,
            Foreground = modDll != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text       = overrideDir ?? gameDir ?? "",
            IsReadOnly = true, FontSize = 12,
            Margin     = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = $"Your Little Witch Nobeta install folder (the one containing " +
                      $"{GameExeName}). Detected from Steam automatically; " +
                      "set it here to override for a non-standard library path.",
        };
        var dirBtn = new Button
        {
            Content = "Select folder...", Width = 120,
            Padding = new Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title = $"Select your Little Witch Nobeta install folder (contains {GameExeName})",
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
                    MessageBox.Show(bad, "Not a Little Witch Nobeta folder",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeGameDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeGameDir(nested)) picked = nested;
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
            Text = $"Steam installs are detected automatically (appid {SteamAppId}). " +
                   "Use this picker for a non-standard Steam library.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ───────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (via the in-game Archipelago window)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "When you create or load a save in Little Witch Nobeta, the mod " +
                   "shows an \"Archipelago Connection\" window inside the game. Enter your " +
                   "hostname (e.g. archipelago.gg or localhost), port (e.g. 38281), " +
                   "player name, and password there, then click Connect. The game connects " +
                   "to the AP server directly. This launcher cannot pre-fill those fields " +
                   "(the mod stores them internally at runtime via MelonPreferences).",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ───────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Little Witch Nobeta on Steam. Install it if you have not.",
            "2. Download and run the MelonLoader installer (see Links below). " +
                "Point it at your Little Witch Nobeta install folder and click Install. " +
                "MelonLoader sets up the mod infrastructure that LittleWitchNobetaAP needs.",
            "3. Use the Install button on the Play tab to download LittleWitchNobetaAP " +
                "and extract it into the Mods folder automatically. Or download LWNAP.zip " +
                "from the GitHub repo and extract it into <GameDir>/Mods/ manually.",
            "4. Launch the game. MelonLoader will initialize on the first run (you may " +
                "see a purple MelonLoader console splash). Confirm the mod appears in the " +
                "MelonLoader mod list.",
            "5. From the main menu, create a NEW save or load an existing one. The mod " +
                "intercepts the save action and shows the Archipelago Connection window. " +
                "Type your server hostname, port, slot name, and password, then click " +
                "Connect. Once connected the game continues normally.",
            "6. Note: DeathLink, magic unlock behavior, and difficulty options are set " +
                "in the Archipelago YAML/game slot — not in a launcher config.",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
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
            ("LittleWitchNobetaAP (GitHub) ↗",        ModRepoUrl),
            ("MelonLoader download ↗",                 MelonLoaderDownload),
            ("MelonLoader wiki / setup guide ↗",       MelonLoaderUrl),
            ("Little Witch Nobeta Setup Guide (AP) ↗", SetupGuideUrl),
            ("Archipelago Official ↗",                 ArchipelagoSite),
        })
        {
            var btn = new Button
            {
                Content = label, HorizontalAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(0, 2, 0, 2), Background = Brushes.Transparent,
                BorderThickness = new Thickness(0), FontSize = 12,
                Margin = new Thickness(0, 0, 0, 4),
                Foreground = new SolidColorBrush(Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) => OpenUrl(u);
            panel.Children.Add(btn);
        }
        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GhReleasesAll, ct);
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

    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..] : tag;
    }

    private async Task<(string Version, string? ZipUrl)> ResolveLatestModAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GhReleasesLatest, ct);
            using var doc  = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t)
                ? NormalizeTag(t.GetString()) : null;

            if (version != null && root.TryGetProperty("assets", out var assets)
                                && assets.ValueKind == JsonValueKind.Array)
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
                    // "LWNAP.zip" is the verified asset name.
                    if (preferred == null && (lower.Contains("lwnap") || lower.Contains("nobeta")))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null) return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — Steam / game detection ──────────────────────────────

    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeGameDir(ov))
            return ov;

        try { return DetectSteamGameDir(); }
        catch { return null; }
    }

    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            return File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
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

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeGameDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeGameDir(conventional)) return conventional;
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

        string? hklm64 = ReadRegistryString(Registry.LocalMachine,
            @"SOFTWARE\Valve\Steam", "InstallPath");
        if (!string.IsNullOrWhiteSpace(hklm64)) yield return NormalizeSteamPath(hklm64);

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

    // ── Private helpers — installed-mod detection ─────────────────────────────

    private string? FindInstalledModDll()
    {
        try
        {
            string? game = ResolveGameDir();
            if (game == null) return null;
            string modsDir = Path.Combine(game, "Mods");
            if (!Directory.Exists(modsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(modsDir, "*.dll", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(dll).Equals(ModDllName, StringComparison.OrdinalIgnoreCase))
                    return dll;
            }
        }
        catch { /* permission / vanished */ }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        string? gameDir = ResolveGameDir();
        string? exe     = gameDir != null ? Path.Combine(gameDir, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = gameDir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Little Witch Nobeta.");

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

        // Steam fallback.
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
            $"Could not find {GameExeName}. Open this game's Settings and pick your " +
            "Little Witch Nobeta install folder, or install the game via Steam.",
            GameExeName);
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* non-fatal */ }
    }

    // ── Private helpers — self-contained settings sidecar ─────────────────────

    private sealed class LwnSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string modsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"lwn-ap-{version}-{Guid.NewGuid():N}.zip");
        try
        {
            progress.Report((15, $"Downloading LittleWitchNobetaAP {version}..."));
            using var response = await _http.GetAsync(
                zipUrl, HttpCompletionOption.ResponseHeadersRead, ct);
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
                        int pct = (int)(15 + 60 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading... {downloaded / 1000}KB / {total / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((80, "Extracting mod into the Mods folder..."));
            Directory.CreateDirectory(modsDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, modsDir, overwriteFiles: true);

            // If the zip wraps everything in a single sub-folder, flatten it so the
            // DLL sits directly in Mods/.
            if (!File.Exists(Path.Combine(modsDir, ModDllName)))
            {
                string[] subdirs = Directory.GetDirectories(modsDir);
                if (subdirs.Length == 1)
                {
                    string sub = subdirs[0];
                    foreach (string fileSrc in Directory.EnumerateFiles(sub, "*", SearchOption.AllDirectories))
                    {
                        string rel     = Path.GetRelativePath(sub, fileSrc);
                        string fileDst = Path.Combine(modsDir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
                        File.Move(fileSrc, fileDst, overwrite: true);
                    }
                    try { Directory.Delete(sub, recursive: true); } catch { }
                }
            }

            progress.Report((95, "Mod extracted."));
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { }
        }
    }

    private LwnSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<LwnSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(LwnSettings s)
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
        var s = LoadSettings(); s.InstallOverride = p; SaveSettings(s);
    }
    private string? ReadStampedVersion()
    {
        string? v = LoadSettings().ModVersion;
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }
    private void WriteStampedVersion(string v)
    {
        var s = LoadSettings(); s.ModVersion = v; SaveSettings(s);
    }
}
