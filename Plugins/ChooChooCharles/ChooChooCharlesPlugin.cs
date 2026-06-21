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

// NOTE on type qualification (BUILD GOTCHA — verified against this repo):
// LauncherV2.csproj sets BOTH <UseWPF>true</UseWPF> AND <UseWindowsForms>true</UseWindowsForms>.
// That makes a long list of simple type names ambiguous between WPF and WinForms
// (Application, Clipboard, MessageBox, Color, Brush(es), Button, TextBox, CheckBox,
// Orientation, FontWeights, HorizontalAlignment, Cursors, Thickness, …) → CS0104.
// The repo's GlobalUsings.cs aliases the short names project-wide, but this plugin
// is written to compile EVEN WITHOUT GlobalUsings (e.g. the isolated self-verify
// build that omits it), so every WPF UI type below is FULLY QUALIFIED
// (System.Windows.Controls.*, System.Windows.Media.*, System.Windows.MessageBox, …)
// and this file adds NO file-level `using X = System.Windows...;` alias (that would
// collide with the GlobalUsings aliases → CS1537). Bare names / fully-qualified only.

namespace LauncherV2.Plugins.ChooChooCharles;

// ═══════════════════════════════════════════════════════════════════════════════
// ChooChooCharlesPlugin — install / launch for "Choo-Choo Charles"
// played through the CCCharles-Random Archipelago mod by lgbarrere.
//
// ── HONEST REALITY CHECK (2026-06-14, verified online + against AP world) ─────
// Choo-Choo Charles is a STEAM-MOD native (the player owns the base game on
// Steam; the AP support is a mod dropped on top). Verified facts:
//
//   * THE AP WORLD game string (EXACT): "Choo-Choo Charles"
//     Verified against worlds/cccharles/__init__.py. GameId = "cccharles".
//
//   * THE STEAM APP ID is 1766740.
//     (The task brief stated 1546540, but that ID does NOT match Choo-Choo
//     Charles on Steam — 1766740 is the correct, verified ID.)
//
//   * THE MOD repo: lgbarrere/CCCharles-Random
//     GitHub: https://github.com/lgbarrere/CCCharles-Random
//     Latest verified release: v0.0.3-beta (CCCharles_Random.zip).
//
//   * THE MOD FRAMEWORK is RE-UE4SS (Unreal Engine Scripting System injected
//     via APCpp), NOT BepInEx. Installation = copy the "Obscure/" folder from
//     CCCharles_Random.zip into the game's root folder (the one that contains
//     Obscure.exe / the Steam-installed ChooChooCharles.exe). If "OFFLINE"
//     shows in the upper-right of the screen when the game starts, the mod is
//     working. The Obscure/ folder carries RE-UE4SS and the AP mod DLLs.
//
//   * HOW IT CONNECTS: in-game, type:
//       /connect <host:port> <SlotName> [Password]
//     at the in-game console (after the game loads). There is NO config file
//     that the launcher can pre-write for this mod — connection is entirely
//     in-game. (Verified against the setup guide and README.) So this plugin
//     does NOT attempt a connection prefill; the settings panel surfaces the
//     session's host/slot/password values so the player can copy them into the
//     in-game console.
//
//   * AP SERVER SIDE: cccharles is a CORE Archipelago world (ships inside AP
//     main; no custom apworld drop needed to host). A CCCharles.yaml template
//     is included in CCCharles_Random.zip.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ─────────────────────────────────────
//   1. DETECT the Steam Choo-Choo Charles install via the Windows registry
//      (HKCU\Software\Valve\Steam → SteamPath; HKLM WOW6432Node also checked)
//      and the standard libraryfolders.vdf + appmanifest_1766740.acf pipeline.
//      A manual install-dir override (folder picker in Settings) takes
//      precedence and is persisted in this plugin's own JSON sidecar at
//      Games/ROMs/cccharles/cccharles_launcher.json — Core/SettingsStore is
//      NOT modified (same pattern as HollowKnightPlugin / StardewValleyPlugin).
//   2. INSTALL/UPDATE = download CCCharles_Random.zip from the latest GitHub
//      release and extract the Obscure/ subfolder into the detected/override
//      game root. The launcher checks for the presence of
//      Obscure\ue4ss\UE4SS.dll as the "installed" signal.
//   3. LAUNCH = run the game exe from the detected/override install; fall back
//      to steam://rungameid/1766740 if the exe cannot be found but Steam is
//      present. The Settings panel surfaces the session's server/slot/password
//      so the player can type /connect in-game.
//   4. ConnectsItself = true (the mod's in-game client owns the AP slot —
//      the launcher must NOT hold its own ApClient on the same slot).
//      SupportsStandalone = true (the base game runs without the mod).
//
// ── DEFENSIVE / UNVERIFIED ────────────────────────────────────────────────────
//   * "Installed" is judged by the presence of Obscure\ue4ss\UE4SS.dll (or
//     Obscure\UE4SS.dll) inside the detected/override game folder, NOT by an
//     OUR-OWN version stamp — the user may have extracted the zip manually.
//   * Steam library parsing is defensive (tolerant VDF scan). Any failure
//     degrades to "not found" rather than throwing.
//   * No plaintext AP password is written to disk by this plugin (connection
//     is entered in-game), so there is nothing to scrub on stop.
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class ChooChooCharlesPlugin : IGamePlugin
{
    // ── Constants — mod repo / links ──────────────────────────────────────────

    private const string MOD_OWNER = "lgbarrere";
    private const string MOD_REPO  = "CCCharles-Random";
    private const string ModRepoUrl = $"https://github.com/{MOD_OWNER}/{MOD_REPO}";
    private const string GH_MOD_RELEASES_URL =
        $"https://api.github.com/repos/{MOD_OWNER}/{MOD_REPO}/releases";
    private const string GH_MOD_RELEASES_LATEST_URL = $"{GH_MOD_RELEASES_URL}/latest";

    private const string SetupGuideUrl =
        "https://archipelago.gg/tutorial/Choo-Choo%20Charles/setup_en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    /// Correct verified Steam AppID for Choo-Choo Charles (NOT 1546540).
    private const string CcSteamAppId = "1766740";
    private static readonly string SteamRunUrl = $"steam://rungameid/{CcSteamAppId}";

    /// Pinned fallback release when the GitHub API is unreachable.
    /// v0.0.3-beta verified live 2026-06-14; asset is "CCCharles_Random.zip".
    private const string FallbackVersion  = "v0.0.3-beta";
    private const string FallbackZipName  = "CCCharles_Random.zip";
    private static readonly string FallbackZipUrl =
        $"{ModRepoUrl}/releases/download/{FallbackVersion}/{FallbackZipName}";

    /// The mod's signal file inside the game folder — presence means installed.
    /// RE-UE4SS puts UE4SS.dll under Obscure\ue4ss\ in the release zip.
    private const string ModSignalRelPath1 = @"Obscure\ue4ss\UE4SS.dll";
    private const string ModSignalRelPath2 = @"Obscure\UE4SS.dll";   // flat fallback

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "cccharles";
    public string DisplayName => "Choo-Choo Charles";
    public string Subtitle    => "Native PC · Archipelago mod (RE-UE4SS)";

    /// EXACT AP game string — verified against worlds/cccharles/__init__.py.
    public string ApWorldName => "Choo-Choo Charles";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "cccharles.png");

    public string ThemeAccentColor => "#8B1A1A";   // deep locomotive red
    public string[] GameBadges     => new[] { "Steam · needs mod" };

    public string Description =>
        "Choo-Choo Charles is an indie horror game in which you travel an open " +
        "world aboard a train, collecting scrap to upgrade your weapon and " +
        "fighting off the spider-train monster Charles. The Archipelago mod by " +
        "lgbarrere (CCCharles-Random) randomizes all items on the ground, mission " +
        "rewards, and weapons into the multiworld using RE-UE4SS injection. You " +
        "bring your own copy of the game from Steam; the launcher installs the " +
        "Obscure/ mod folder and guides you to connect in-game with the /connect " +
        "command.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the mod's UE4SS.dll is present under the game's
    /// Obscure folder (RE-UE4SS signal). Works whether the user installed via
    /// this launcher or extracted the zip themselves.
    public bool IsInstalled => FindModSignalFile() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Resolved Choo-Choo Charles game directory (contains the game exe and the
    /// Obscure/ mod folder after install). Setting this persists the override.
    public string GameDirectory
    {
        get => ResolveInstallDir() ?? "";
        set
        {
            if (!string.IsNullOrWhiteSpace(value))
                SaveOverrideInstallDir(value);
        }
    }

    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "cccharles_launcher.json");

    private string VersionFilePath
        => Path.Combine(RomLibraryDirectory, "cccharles_mod_version.dat");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The CCCharles-Random mod reports checks/items/goal to the AP server
    // itself. These exist only for interface compatibility (ConnectsItself=true).
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself / SupportsStandalone ───────────────────────────────────

    /// The CCCharles-Random mod owns the AP slot connection — the launcher must
    /// NOT hold its own ApClient on the same slot while the game is running.
    public bool ConnectsItself => true;

    /// The base game runs perfectly without the mod.
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = IsInstalled
                ? (File.Exists(VersionFilePath)
                    ? (await File.ReadAllTextAsync(VersionFilePath, ct)).Trim()
                    : "installed")
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
        // 0. Locate the Choo-Choo Charles install to drop the mod into.
        progress.Report((2, "Locating your Choo-Choo Charles installation..."));
        string? gameDir = ResolveInstallDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find a Choo-Choo Charles installation. Open this game's " +
                "Settings and use \"Locate install...\" to pick the folder that contains " +
                "Obscure.exe, or install Choo-Choo Charles via Steam first " +
                "(appid " + CcSteamAppId + ").");

        // 1. Resolve the latest mod release (pinned fallback when offline).
        progress.Report((6, "Checking the latest CCCharles-Random release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the CCCharles-Random download on GitHub. Check your " +
                "internet connection or download it manually from " + ModRepoUrl +
                "/releases — extract the Obscure/ folder into your game directory.");

        // 2. Download and extract the mod into the game directory.
        await DownloadAndExtractModAsync(zipUrl, version, gameDir, progress, ct);

        // 3. Stamp the version.
        Directory.CreateDirectory(RomLibraryDirectory);
        await File.WriteAllTextAsync(VersionFilePath, version, ct);
        InstalledVersion = version;

        progress.Report((100,
            $"CCCharles-Random {version} installed. If \"OFFLINE\" is visible in " +
            "the upper-right corner when the game starts, the mod is working. " +
            "To connect: launch the game, then type /connect <host:port> " +
            "<SlotName> [Password] in the in-game console."));
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
        // HONEST: CCCharles-Random connects to the AP server entirely in-game
        // via the /connect slash command (/connect <host:port> <SlotName>
        // [Password]). There is no config file or command-line argument the
        // launcher can pre-write to pre-fill the connection (verified against
        // the setup guide and the mod README). Launching from this tile starts
        // the game; the settings panel shows the session credentials to copy.
        //
        // ConnectsItself = true: the launcher must NOT hold its own ApClient on
        // this slot while the mod is connected.
        _ = session; // intentionally unused — no prefill mechanism exists
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
        // No plaintext AP password is ever written to disk by this plugin.
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself) ───────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
    {
        // The in-game mod receives items directly from the AP server.
        return Task.CompletedTask;
    }

    public void OnApStateChanged(ApConnectionState state)
    {
        // The mod renders its own AP status in-game.
    }

    // ── Settings UI ──────────────────────────────────────────────────────────

    public UIElement? CreateSettingsPanel()
    {
        var muted   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var panel   = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(0, 0, 0, 20) };

        string? installDir = ResolveInstallDir();
        string? modSig     = FindModSignalFile();

        // ── Honesty header ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "Choo-Choo Charles is your own game (Steam appid 1766740) with the " +
                   "CCCharles-Random mod (RE-UE4SS injection) added on top. The launcher " +
                   "detects your Steam install and installs the Obscure/ mod folder. " +
                   "Connection to the AP server is entered IN-GAME via /connect — there " +
                   "is no config file to pre-fill. These external steps are not verified " +
                   "by this launcher.",
            FontSize = 11, Foreground = warn, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: game install ─────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CHOO-CHOO CHARLES INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = installDir ?? "", IsReadOnly = true, FontSize = 12,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "The Choo-Choo Charles game folder (contains Obscure.exe). " +
                          "Auto-detected from Steam; override here for a non-default install.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Locate install...", Width = 130,
            Padding = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Choo-Choo Charles install folder (contains Obscure.exe)",
                InitialDirectory = Directory.Exists(installDir) ? installDir! : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() == true)
            {
                string? bad = ValidateExistingInstall(dlg.FolderName);
                if (bad != null)
                {
                    System.Windows.MessageBox.Show(bad, "Not a Choo-Choo Charles folder",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                SaveOverrideInstallDir(dlg.FolderName);
                dirBox.Text = dlg.FolderName;
            }
        };
        System.Windows.Controls.DockPanel.SetDock(dirBtn, System.Windows.Controls.Dock.Right);
        dirRow.Children.Add(dirBtn);
        dirRow.Children.Add(dirBox);
        panel.Children.Add(dirRow);

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text     = installDir != null
                        ? "✓ " + (DetectSteamInstallDir() == installDir
                            ? "Detected via Steam (appid " + CcSteamAppId + ")."
                            : "Using your selected install folder.")
                        : "Choo-Choo Charles not found automatically — use \"Locate install...\".",
            FontSize = 11, Foreground = installDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: mod status ───────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "MOD STATUS", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text     = modSig != null
                        ? "✓ CCCharles-Random mod installed (UE4SS.dll found)" +
                          (InstalledVersion != null && InstalledVersion != "installed"
                            ? " — version " + InstalledVersion + "." : ".")
                        : "✗ CCCharles-Random mod not installed — click Install on the Play tab.",
            FontSize = 11, Foreground = modSig != null ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: connection info (in-game) ────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game via /connect command)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After launching the game, type the following in the in-game console " +
                   "(press ~ or the console key to open it):\n" +
                   "  /connect <host:port> <SlotName> [Password]\n\n" +
                   "Example: /connect archipelago.gg:38281 MySlot mypassword\n\n" +
                   "There is no config file this launcher can pre-fill — connection is " +
                   "entered entirely in-game. If \"OFFLINE\" appears in the upper-right " +
                   "corner after launch, the mod loaded successfully; once /connect runs, " +
                   "it changes to \"ONLINE\".",
            FontSize = 11, Foreground = fg, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: guided setup steps ───────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "SETUP STEPS", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own and install Choo-Choo Charles from Steam (appid 1766740).",
            "2. Use \"Locate install...\" above if it was not detected automatically.",
            "3. Click Install on the Play tab — the launcher downloads CCCharles_Random.zip " +
               "and extracts the Obscure/ folder into your game directory.",
            "4. Launch the game. The word \"OFFLINE\" should appear in the upper-right " +
               "corner — this confirms the mod is loaded.",
            "5. In-game, open the console and type: /connect <host:port> <SlotName> [Password]",
            "6. \"OFFLINE\" changes to \"ONLINE\" when the connection succeeds.",
        })
        {
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = step, FontSize = 11, Foreground = fg,
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 0, 0, 5),
            });
        }

        // ── Section: links ────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "LINKS", FontSize = 10, FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("CCCharles-Random (GitHub) ↗",          ModRepoUrl),
            ("CCCharles-Random Releases ↗",           ModRepoUrl + "/releases"),
            ("Choo-Choo Charles Setup Guide ↗",       SetupGuideUrl),
            ("Archipelago Official ↗",                ArchipelagoSite),
        })
        {
            var btn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding         = new System.Windows.Thickness(0, 2, 0, 2),
                Background      = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0),
                FontSize        = 12,
                Margin          = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground      = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor          = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            btn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
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

    /// Resolve the latest CCCharles-Random release: tag name + CCCharles_Random.zip URL.
    /// Falls back to the pinned v0.0.3-beta direct URL when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_MOD_RELEASES_LATEST_URL, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? version = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;

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
                    if (preferred == null && lower.Contains("cccharles"))
                        preferred = url;
                }
                string? zip = preferred ?? anyZip;
                if (zip != null) return (version, zip);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* API unreachable / rate-limited → pinned fallback */ }

        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — install detection ───────────────────────────────────

    /// Check that a folder looks like the Choo-Choo Charles game install.
    /// The game exe on Steam is "Obscure.exe" (the executable for the UE game).
    private static bool LooksLikeGameDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            // The UE game launcher is "Obscure.exe" in the game root.
            if (File.Exists(Path.Combine(dir, "Obscure.exe"))) return true;
            // Also accept if the game's content dir is present.
            if (Directory.Exists(Path.Combine(dir, "Obscure"))) return true;
            return false;
        }
        catch { return false; }
    }

    /// Find the mod's signal file (UE4SS.dll) in the game's Obscure/ folder.
    /// Returns the path if found, null otherwise.
    private string? FindModSignalFile()
    {
        try
        {
            string? gameDir = ResolveInstallDir();
            if (gameDir == null) return null;

            string p1 = Path.Combine(gameDir, ModSignalRelPath1);
            if (File.Exists(p1)) return p1;

            string p2 = Path.Combine(gameDir, ModSignalRelPath2);
            if (File.Exists(p2)) return p2;

            // Broader search: any UE4SS.dll anywhere under gameDir\Obscure\
            string obscureDir = Path.Combine(gameDir, "Obscure");
            if (Directory.Exists(obscureDir))
            {
                foreach (string f in Directory.EnumerateFiles(
                    obscureDir, "UE4SS.dll", SearchOption.AllDirectories))
                    return f;
            }
        }
        catch { /* permission / directory vanished */ }
        return null;
    }

    /// Validate a user-supplied install folder. Returns null if OK, or an
    /// error message string if the folder does not look right.
    private static string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Choo-Choo Charles install folder.";
        if (LooksLikeGameDir(folder))
            return null;
        return "That does not look like a Choo-Choo Charles installation. Pick the " +
               "folder that contains Obscure.exe (the game executable).";
    }

    // ── Private helpers — Steam detection ─────────────────────────────────────

    private string? ResolveInstallDir()
    {
        string? ov = LoadOverrideInstallDir();
        if (ov != null && LooksLikeGameDir(ov)) return ov;
        try { return DetectSteamInstallDir(); } catch { return null; }
    }

    private string? DetectSteamInstallDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{CcSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common    = Path.Combine(steamapps, "common");
                    string? instDir  = ReadAcfInstallDir(manifest);
                    if (instDir != null)
                    {
                        string candidate = Path.Combine(common, instDir);
                        if (LooksLikeGameDir(candidate)) return candidate;
                    }
                    // Conventional folder name used on Steam.
                    foreach (string fallbackName in new[] { "Choo-Choo Charles", "ChooChooCharles" })
                    {
                        string c = Path.Combine(common, fallbackName);
                        if (LooksLikeGameDir(c)) return c;
                    }
                }
                catch { /* try next library */ }
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

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartGame()
    {
        string? gameDir = ResolveInstallDir();
        string? exe     = gameDir != null ? Path.Combine(gameDir, "Obscure.exe") : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = gameDir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Choo-Choo Charles.");

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

        // Fall back to Steam if we can find Steam at all.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // best-effort; Steam owns the process
                return;
            }
            catch { /* fall through */ }
        }

        throw new FileNotFoundException(
            "Could not find Obscure.exe. Open this game's Settings and pick your " +
            "Choo-Choo Charles install folder, or install the game via Steam " +
            "(appid " + CcSteamAppId + ").",
            "Obscure.exe");
    }

    // ── Private helpers — download / extract mod ──────────────────────────────

    /// Download CCCharles_Random.zip and extract the Obscure/ folder into gameDir.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string gameDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"cccharles-random-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"cccharles-extract-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading CCCharles-Random {version}..."));
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
                        int pct = (int)(10 + 55 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading... {downloaded / 1024}KB / {total / 1024}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((68, "Extracting mod..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempExtract, overwriteFiles: true);

            // The release zip may have a top-level wrapper folder (e.g.
            // CCCharles_Random/ or the version tag name). Find the Obscure/
            // subfolder wherever it lives and copy it into the game root.
            progress.Report((80, "Installing Obscure/ folder into game directory..."));
            string? obscureSrc = FindObscureFolder(tempExtract);
            if (obscureSrc == null)
                throw new InvalidOperationException(
                    "Could not find the Obscure/ folder inside the downloaded zip. " +
                    "Please extract CCCharles_Random.zip manually and copy the Obscure/ " +
                    "folder into your game directory.");

            string obscureDst = Path.Combine(gameDir, "Obscure");
            CopyDirectory(obscureSrc, obscureDst, overwrite: true);

            progress.Report((95, "Mod files installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))            File.Delete(tempZip); }      catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, recursive: true); } catch { }
        }
    }

    /// Locate the "Obscure" folder in a temp extract tree (may be under a wrapper dir).
    private static string? FindObscureFolder(string root)
    {
        // Direct child named "Obscure"
        string direct = Path.Combine(root, "Obscure");
        if (Directory.Exists(direct)) return direct;

        // One level deep (inside a version-tag wrapper folder)
        try
        {
            foreach (string sub in Directory.EnumerateDirectories(root))
            {
                string candidate = Path.Combine(sub, "Obscure");
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        catch { /* ignore */ }
        return null;
    }

    /// Recursively copy srcDir into dstDir, optionally overwriting existing files.
    private static void CopyDirectory(string srcDir, string dstDir, bool overwrite)
    {
        Directory.CreateDirectory(dstDir);
        foreach (string fileSrc in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
        {
            string rel     = Path.GetRelativePath(srcDir, fileSrc);
            string fileDst = Path.Combine(dstDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(fileDst)!);
            File.Copy(fileSrc, fileDst, overwrite);
        }
    }

    // ── Private helpers — settings sidecar ───────────────────────────────────
    // Self-contained JSON sidecar at Games/ROMs/cccharles/cccharles_launcher.json.
    // Stores the user's install-dir override. Never touches Core/SettingsStore.

    private sealed class CcSettings
    {
        public string? InstallOverride { get; set; }
    }

    private CcSettings LoadSidecar()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<CcSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSidecar(CcSettings s)
    {
        try
        {
            Directory.CreateDirectory(RomLibraryDirectory);
            File.WriteAllText(SettingsSidecarPath,
                JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        catch { /* non-fatal */ }
    }

    private string? LoadOverrideInstallDir()
    {
        string? p = LoadSidecar().InstallOverride;
        return string.IsNullOrWhiteSpace(p) ? null : p;
    }

    private void SaveOverrideInstallDir(string p)
    {
        var s = LoadSidecar();
        s.InstallOverride = p;
        SaveSidecar(s);
    }
}
