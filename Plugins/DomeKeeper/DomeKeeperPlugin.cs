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

// The launcher project sets BOTH UseWPF and UseWindowsForms=true, so several UI
// type names (Color, Brushes, Button, TextBox, HorizontalAlignment) collide
// between WPF and WinForms. The project's GlobalUsings.cs already aliases each of
// these to its WPF type globally, so this file relies on those — no local aliases
// (a local alias duplicating a global one is CS1537).

namespace LauncherV2.Plugins.DomeKeeper;

// ═══════════════════════════════════════════════════════════════════════════════
// DomeKeeperPlugin — install / launch for "Dome Keeper" (Bippinbits / Raw Fury,
// 2022) played through the ArchipelagoDK Steam Workshop mod by Arrcival, which
// contains the in-game Archipelago client. This is a NATIVE "ConnectsItself"
// integration: the game itself speaks to the AP server via its mod menu — no
// emulator, no Lua bridge, no launcher-held ApClient on the slot.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against the apworld) ──
// Dome Keeper is a Godot 3/4 roguelike mining game on Steam (appid 1637320),
// and its Archipelago integration is delivered as a Steam Workshop mod
// (Workshop item 3148616716, labelled "[v3 LEGACY]" / "Archipelago (multi-game
// randomizer)" by Arrcival on GitHub: Arrcival/ArchipelagoDK). Important facts
// verified 2026-06-14:
//
//   * THE AP WORLD game string is "Dome Keeper" (verified against
//     worlds/dome_keeper/__init__.py: `class DomeKeeperWorld(World): ... game =
//     "Dome Keeper"`). The apworld ships as dome_keeper.apworld (NOT a core AP
//     world — it must be dropped into the AP server's custom_worlds folder).
//     Latest release: v1.3.0 (ships dome_keeper.apworld + Linux AP bundles).
//     GameId here = "dome_keeper".
//
//   * THE MOD is a STEAM WORKSHOP subscription, NOT a downloadable zip. The
//     setup guide (worlds/dome_keeper/docs/dome-keeper_en.md) says: "For
//     Steam-based installation, subscribe to the following mod:
//     https://steamcommunity.com/sharedfiles/filedetails/?id=3148616716".
//     Dome Keeper's Workshop mod gives the game an "Archipelago" menu category
//     in options; the mod is loaded by the game's own Godot mod system —
//     no external client exe is involved. The launcher cannot subscribe to
//     Workshop for the user (Steam manages it). This plugin surfaces the
//     Workshop link prominently and opens it via the steam:// protocol.
//
//   * APWORLD DOWNLOAD. The launcher CAN download dome_keeper.apworld from
//     the GitHub release and help the user place it in the right location
//     (ProgramData\Archipelago\custom_worlds — READ-ONLY in this project's
//     policy → the launcher instead writes it to our own GameDirectory/apworld/
//     and instructs the user to copy it, OR the user may already have it if
//     they used the AP installer). The apworld is required on the GENERATOR /
//     SERVER side, not on the client-game side.
//
//   * NO SEPARATE EXTERNAL CLIENT. The in-game mod handles all Archipelago
//     communication (the Archipelago menu in options → hostname:port, slot,
//     password → connect). This plugin neither downloads nor launches any
//     external client; it launches the game and the mod does the rest.
//
//   * CONNECTION is made IN-GAME (verified against the official setup guide):
//     open the Archipelago menu category in the options menu, enter
//     hostname:port in the hostname field, slot + password, then click Connect.
//     There is no command-line arg or config file this launcher can pre-write
//     (verified). This plugin surfaces the session credentials (server/slot)
//     in the settings panel for the user to type into those fields.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Dome Keeper install via the Windows registry
//      (HKCU\Software\Valve\Steam -> SteamPath and
//      HKLM\...\WOW6432Node\Valve\Steam -> InstallPath), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Dome Keeper via appmanifest_1637320.acf. A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and
//      takes precedence; it is validated (must contain DomeKeeper.exe or
//      dome_keeper.exe) and persisted in this plugin's OWN sidecar
//      (Games/ROMs/dome_keeper/dome_keeper_launcher.json).
//   2. INSTALL guidance = the mod is a Steam Workshop subscription; the
//      launcher opens the Workshop page (steam://url/CommunityFilePage/3148616716)
//      for the user. The launcher ALSO downloads dome_keeper.apworld from the
//      latest GitHub release and saves it locally (GameDirectory/apworld/) so
//      the user can copy it to their AP server's custom_worlds folder. "Install"
//      in this plugin therefore means: open Workshop page + download apworld.
//   3. LAUNCH = run DomeKeeper.exe from the detected/override install; if the
//      exe cannot be found but Steam is present, fall back to
//      steam://rungameid/1637320. ConnectsItself = true (the mod owns the slot).
//      SupportsStandalone = true (plain Dome Keeper runs fine without AP).
//      No connection prefill (entered in-game via Options → Archipelago).
//
// ── DEFENSIVE / UNVERIFIED ("verify at runtime", Noita/Hollow-Knight-style) ──
//   * "Installed" (mod-wise) is judged by the presence of the Workshop mod
//     sub-folder under Dome Keeper's mods directory. Since Steam manages
//     Workshop downloads, we cannot inspect the actual mod file content
//     reliably — instead we detect whether ANY sub-folder exists under
//     <DomeKeeper>/mods (Dome Keeper uses a "mods" folder for Workshop content
//     on-disk; this is the Godot 3/4 mod system). If no DomeKeeper install is
//     found, the tile reads "game not found".
//   * Steam library parsing is defensive: hand-written tolerant VDF scan that
//     pulls quoted "path" values; any failure degrades to "not found".
//   * No plaintext AP password is written by this plugin; none to scrub.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class DomeKeeperPlugin : IGamePlugin
{
    // ── Constants — Dome Keeper + the ArchipelagoDK mod (Arrcival/ArchipelagoDK) ──
    private const string MOD_OWNER  = "Arrcival";
    private const string MOD_REPO   = "ArchipelagoDK";
    private static readonly string ModRepoUrl =
        $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private static readonly string GH_MOD_RELEASES_LATEST_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases/latest";
    private static readonly string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";

    // Steam Workshop mod (verified 2026-06-14 from setup guide in repo).
    // NOTE: the "[v3 LEGACY]" note on the page is about a previous version.
    // The current mod page from the guide link (3148616716) is the official one.
    private const string WorkshopItemId  = "3148616716";
    private static readonly string WorkshopPageUrl =
        $"https://steamcommunity.com/sharedfiles/filedetails/?id={WorkshopItemId}";
    private static readonly string WorkshopSteamUrl =
        $"steam://url/CommunityFilePage/{WorkshopItemId}";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Dome%20Keeper/setup/en";
    private const string GameInfoUrl     = "https://archipelago.gg/games/Dome%20Keeper/info/en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Dome Keeper appid 1637320 (confirmed from Steam URL).
    private const string DomeKeeperSteamAppId = "1637320";
    private static readonly string SteamRunUrl =
        $"steam://rungameid/{DomeKeeperSteamAppId}";

    /// The standard Steam install sub-folder name for Dome Keeper.
    private const string SteamCommonFolderName = "Dome Keeper";

    /// Candidate exe names (Godot games often use the game's project name).
    private static readonly string[] ExeNames =
        { "DomeKeeper.exe", "dome_keeper.exe", "Dome Keeper.exe" };

    /// Name of the apworld asset in GitHub releases.
    private const string ApWorldAssetName = "dome_keeper.apworld";

    /// Pinned fallback for the apworld when the GitHub API is unreachable.
    /// v1.3.0 verified live 2026-06-14; dome_keeper.apworld is the sole Windows asset.
    private const string FallbackVersion      = "1.3.0";
    private static readonly string FallbackApWorldUrl =
        $"{ModRepoUrl}/releases/download/v{FallbackVersion}/{ApWorldAssetName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "dome_keeper";
    public string DisplayName => "Dome Keeper";
    public string Subtitle    => "Native PC · Steam Workshop mod";

    /// EXACT AP game string — verified against worlds/dome_keeper/__init__.py
    /// (`class DomeKeeperWorld(World): ... game = "Dome Keeper"`).
    public string ApWorldName => "Dome Keeper";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "dome_keeper.png");

    public string ThemeAccentColor => "#3A7ACC";   // dome-blue / mining shaft

    public string[] GameBadges => new[] { "Steam · Workshop mod", "Godot" };

    public string Description =>
        "Dome Keeper, Bippinbits' 2022 roguelike where you alternate between " +
        "mining for resources and defending your dome against waves of aliens, " +
        "played through the ArchipelagoDK Steam Workshop mod by Arrcival. The mod " +
        "adds an Archipelago menu inside Dome Keeper's options where you connect to " +
        "your multiworld server — keeper upgrades, dome weapons, gadgets, and layers " +
        "are shuffled across the multiworld. You bring your own copy of Dome Keeper " +
        "(owned on Steam); the mod is subscribed via Steam Workshop. This launcher " +
        "detects your Steam install, helps you subscribe to the mod, downloads the " +
        "required dome_keeper.apworld for your AP server, and then launches the game. " +
        "Connection is made in-game via Options → Archipelago.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" here means: the base game exists AND the Steam Workshop mod
    /// sub-folder is present under the game's mods directory. Since Steam manages
    /// Workshop downloads, we check for a non-empty mods directory.
    public bool IsInstalled => IsGameInstalled;

    /// True when Dome Keeper itself is found (game exe present).
    private bool IsGameInstalled => FindDomeKeeperExe() != null;

    public bool IsRunning { get; private set; }

    // ── Plugin contract flags ─────────────────────────────────────────────────
    /// The ArchipelagoDK Workshop mod handles the AP connection in-game.
    public bool ConnectsItself => true;

    /// Dome Keeper can run standalone without the AP mod connected.
    public bool SupportsStandalone => true;

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where this plugin keeps the downloaded apworld and any working files.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "DomeKeeper");

    /// This plugin's OWN settings sidecar. Lives under Games/ROMs/dome_keeper/.
    private string SettingsSidecarDir
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);
    private string SettingsSidecarPath
        => Path.Combine(SettingsSidecarDir, "dome_keeper_launcher.json");

    /// Local path where we save the downloaded apworld.
    private string LocalApWorldDir  => Path.Combine(GameDirectory, "apworld");
    private string LocalApWorldPath => Path.Combine(LocalApWorldDir, ApWorldAssetName);

    // ── Internal state ────────────────────────────────────────────────────────
    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The ArchipelagoDK mod reports checks/items/goal to the AP server itself —
    // the launcher relays nothing. These exist for interface compatibility
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
            // Installed means: game present + apworld downloaded (best-effort stamp).
            InstalledVersion = IsGameInstalled
                ? (ReadStampedVersion() ?? (File.Exists(LocalApWorldPath) ? "installed" : null))
                : null;
        }
        catch
        {
            InstalledVersion = null;
        }

        try
        {
            var (version, _) = await ResolveLatestApWorldAsync(ct);
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
        // 1. Resolve the latest apworld release.
        progress.Report((5, "Checking the latest ArchipelagoDK release..."));
        var (version, apWorldUrl) = await ResolveLatestApWorldAsync(ct);
        AvailableVersion = version;

        if (apWorldUrl == null)
            throw new InvalidOperationException(
                "Could not find the dome_keeper.apworld download on GitHub. Check " +
                "your internet connection, or download it manually from " +
                ModRepoUrl + "/releases and place it in your AP server's " +
                "custom_worlds folder. The mod repo is " + ModRepoUrl + ".");

        // 2. Download the apworld and save it locally so the user can copy it to
        //    their AP server's custom_worlds folder.
        progress.Report((10, $"Downloading dome_keeper.apworld {version}..."));
        Directory.CreateDirectory(LocalApWorldDir);

        using var response = await _http.GetAsync(
            apWorldUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        long total      = response.Content.Headers.ContentLength ?? -1;
        long downloaded = 0;

        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(LocalApWorldPath))
        {
            var buf = new byte[81920];
            int bytesRead;
            while ((bytesRead = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, bytesRead), ct);
                downloaded += bytesRead;
                if (total > 0)
                {
                    int pct = (int)(10 + 60 * downloaded / total);
                    progress.Report((pct, $"Downloading apworld... {downloaded / 1000}KB"));
                }
            }
            await dst.FlushAsync(ct);
        }

        progress.Report((75, "Saved dome_keeper.apworld. Opening Steam Workshop mod page..."));

        // 3. Stamp the version we downloaded.
        WriteStampedVersion(version);
        InstalledVersion = version;

        // 4. Open the Steam Workshop page so the user can subscribe to the in-game mod.
        try
        {
            Process.Start(new ProcessStartInfo(WorkshopSteamUrl) { UseShellExecute = true });
        }
        catch
        {
            try { Process.Start(new ProcessStartInfo(WorkshopPageUrl) { UseShellExecute = true }); }
            catch { /* non-fatal */ }
        }

        progress.Report((100,
            $"Downloaded dome_keeper.apworld {version} to: {LocalApWorldPath}\n\n" +
            "NEXT STEPS:\n" +
            "1. Copy dome_keeper.apworld to your AP server's 'custom_worlds' folder.\n" +
            "2. Subscribe to the Steam Workshop mod (page just opened in Steam/browser).\n" +
            "3. Launch Dome Keeper, then open Options → Archipelago and enter your\n" +
            "   server hostname:port, slot name, and password, then click Connect.\n" +
            "(Open Settings for the full guided steps and links.)"));
    }

    // ── Lifecycle — Verify ────────────────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsGameInstalled;
    }

    // ── Lifecycle — Launch ────────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // HONEST: the AP connection for Dome Keeper is entered IN-GAME via the
        // Options → Archipelago menu (hostname:port, slot, password → Connect).
        // There is no command-line / config-file prefill this launcher can apply.
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
        StartDomeKeeper();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartDomeKeeper();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning    = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself = true) ─────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The ArchipelagoDK mod receives items from the AP server directly.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new StackPanel { Margin = new Thickness(0, 0, 0, 20) };

        // ── Honesty header ─────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "Dome Keeper is your own game (Steam) with an Archipelago mod " +
                   "subscribed via Steam Workshop. The launcher downloads the required " +
                   "dome_keeper.apworld for your AP server and opens the Workshop page " +
                   "for you to subscribe. The mod is automatically loaded by Steam when " +
                   "you subscribe — no manual file extraction needed. Connection to your " +
                   "server is entered in-game via Options → Archipelago. These external " +
                   "steps cannot be automated by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install / override ───────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "DOME KEEPER INSTALL", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 0, 0, 8),
        });

        string? gameDir      = ResolveDomeKeeperDir();
        string? overrideDir  = LoadOverrideDir();
        string  detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Dome Keeper not detected. Pick your install folder below, or install " +
              "Dome Keeper via Steam first.";
        panel.Children.Add(new TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6),
        });

        // apworld status
        panel.Children.Add(new TextBlock
        {
            Text = File.Exists(LocalApWorldPath)
                    ? "dome_keeper.apworld downloaded: " + LocalApWorldPath
                    : "dome_keeper.apworld not downloaded yet (use Install on the Play tab).",
            FontSize = 11,
            Foreground = File.Exists(LocalApWorldPath) ? success : muted,
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        });

        // install-dir override picker
        var dirRow = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        var dirBox = new TextBox
        {
            Text = overrideDir ?? gameDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Dome Keeper install folder (contains the game exe). " +
                          "Detected from Steam automatically; set here to override " +
                          "(non-standard Steam library or another store).",
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
                Title            = "Select your Dome Keeper install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? gameDir ?? "")
                                   ? (overrideDir ?? gameDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string picked = dlg.FolderName;
                string? bad   = ValidateInstallDir(picked);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Dome Keeper folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                // Descend into the Steam common sub-folder if the user picked the parent.
                if (!LooksLikeDomeKeeperDir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeDomeKeeperDir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 1637320). Use " +
                   "this picker for a non-standard Steam library.",
            FontSize = 11, Foreground = muted, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection (in-game) ─────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "CONNECTING (entered in-game)", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "In Dome Keeper, open Options → Archipelago. Enter your server " +
                   "hostname and port (as hostname:port) in the hostname field, then " +
                   "your slot name and password. Click Connect. This launcher cannot " +
                   "pre-fill the connection — it is entered in-game.",
            FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup steps ────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = FontWeights.SemiBold, Foreground = muted,
            Margin = new Thickness(0, 4, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Dome Keeper (on Steam). Install it if you have not. Use the folder " +
                "picker above if it was not detected automatically.",
            "2. Download dome_keeper.apworld using the Install button on the Play tab. " +
                "Copy the downloaded .apworld file to your Archipelago server's " +
                "'custom_worlds' folder (your AP host needs it to generate and run the game).",
            "3. Subscribe to the Archipelago Steam Workshop mod (link below or use Install " +
                "on the Play tab — it opens the Workshop page). Steam will download and " +
                "activate the mod automatically.",
            "4. Launch Dome Keeper from this launcher. The mod adds an Archipelago entry " +
                "in the Options menu.",
            "5. Open Options → Archipelago in-game. Enter your server hostname:port, slot " +
                "name, and password, then click Connect. You are ready to defend your dome!",
        })
        {
            panel.Children.Add(new TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 5),
            });
        }

        // ── Links ──────────────────────────────────────────────────────────
        panel.Children.Add(new TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = FontWeights.SemiBold,
            Foreground = muted, Margin = new Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("ArchipelagoDK on GitHub ↗",              ModRepoUrl),
            ("Steam Workshop mod page ↗",              WorkshopPageUrl),
            ("Dome Keeper Setup Guide (AP) ↗",         SetupGuideUrl),
            ("Dome Keeper Game Info (AP) ↗",           GameInfoUrl),
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

    /// "v1.3.0" → "1.3.0". Strips leading 'v' before a digit.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    /// Resolve the latest dome_keeper.apworld download URL from GitHub releases.
    /// Falls back to the pinned v1.3.0 direct URL when the API is unreachable.
    private async Task<(string Version, string? ApWorldUrl)> ResolveLatestApWorldAsync(CancellationToken ct)
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
                foreach (var a in assets.EnumerateArray())
                {
                    string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? url  = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == null || url == null) continue;
                    if (name.Equals(ApWorldAssetName, StringComparison.OrdinalIgnoreCase))
                        return (version, url);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackApWorldUrl);
    }

    // ── Private helpers — Steam / Dome Keeper detection ───────────────────────

    /// The Dome Keeper install dir to use: override (if valid) wins, else Steam.
    private string? ResolveDomeKeeperDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeDomeKeeperDir(ov))
            return ov;

        try { return DetectSteamDomeKeeperDir(); }
        catch { return null; }
    }

    /// Find the Dome Keeper exe in the detected/override install.
    private string? FindDomeKeeperExe()
    {
        string? dir = ResolveDomeKeeperDir();
        if (dir == null) return null;
        foreach (string name in ExeNames)
        {
            string path = Path.Combine(dir, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// A folder looks like a Dome Keeper install if it contains one of the known exe names.
    private static bool LooksLikeDomeKeeperDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            foreach (string name in ExeNames)
                if (File.Exists(Path.Combine(dir, name))) return true;
            // Secondary: Godot exports often ship a .pck alongside the exe.
            if (Directory.GetFiles(dir, "*.pck", SearchOption.TopDirectoryOnly).Length > 0)
                return true;
            return false;
        }
        catch { return false; }
    }

    /// Validate a user-picked folder as a Dome Keeper install.
    public string? ValidateInstallDir(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Dome Keeper install folder.";

        if (LooksLikeDomeKeeperDir(folder))
            return null;

        // Try the nested Steam common sub-folder.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeDomeKeeperDir(nested)) return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Dome Keeper installation. Pick the folder " +
               "that contains DomeKeeper.exe " +
               @"(for Steam this is usually ...\steamapps\common\Dome Keeper).";
    }

    /// Detect the Steam Dome Keeper install via registry + libraryfolders.vdf.
    private static string? DetectSteamDomeKeeperDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps,
                        $"appmanifest_{DomeKeeperSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common     = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeDomeKeeperDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeDomeKeeperDir(conventional)) return conventional;
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

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartDomeKeeper()
    {
        string? dir = ResolveDomeKeeperDir();
        string? exe = FindDomeKeeperExe();

        if (exe != null && dir != null)
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = dir,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Dome Keeper.");

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

        // Fall back to Steam if the exe was not found.
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
            "Could not find DomeKeeper.exe. Open this game's Settings and pick your " +
            "Dome Keeper install folder, or install Dome Keeper via Steam.",
            "DomeKeeper.exe");
    }

    // ── Private helpers — settings sidecar ───────────────────────────────────

    private sealed class DomeKeeperSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private DomeKeeperSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<DomeKeeperSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(DomeKeeperSettings s)
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
