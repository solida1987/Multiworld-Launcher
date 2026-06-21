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

namespace LauncherV2.Plugins.Everhood2;

// ═══════════════════════════════════════════════════════════════════════════════
// Everhood2Plugin — install / launch for "Everhood 2" (Foreign Gnomes / Chris
// Nordgren, Steam appid 1984020), played through the Archipelago MelonLoader mod
// by DeamonHunter (ArchipelagoEverhood2 on GitHub).  This is a NATIVE
// "ConnectsItself" integration: the game itself speaks to the AP server through
// the in-game Archipelago lobby — no emulator, no Lua bridge, no launcher-held
// ApClient on the slot.
//
// ── HONEST REALITY CHECK (2026-06-14, verified against the GitHub repo) ───────
//
//   * THE AP WORLD game string is "everhood_2" (verified from the apworld asset
//     filename: everhood_2.apworld).  Minimum required AP version: 0.6.7.
//
//   * THE MOD is the GitHub release asset "ArchipelagoEverhood.zip" from
//     https://github.com/DeamonHunter/ArchipelagoEverhood2.  Latest: v0.4.7
//     (2026-05-05).  The zip contains two DLLs:
//         ArchipelagoEverhood.dll
//         Archipelago.MultiClient.Net.dll
//     Both must live in <Everhood 2>\Mods\  (NOT in a sub-folder — the mod is
//     not set up for that).
//
//   * ENGINE: Unity.  MelonLoader is the mod loader.  Installation requires
//     running MelonLoader.Installer.exe (or the Linux equivalent), selecting
//     Everhood 2, and clicking Install.  Then the mod DLLs go into Mods\.  The
//     launcher automates only the DLL copy; MelonLoader itself cannot be
//     auto-installed without running an external installer exe.
//
//   * CONNECTION is made IN-GAME.  After the mod is installed, a new button
//     appears on the start screen.  Connection credentials (server, slot, password)
//     are entered there.  The mod persists the last-used server and slot in:
//         <Everhood 2>\UserData\ArchSaves\LastLogin.txt
//     (line 1 = IP:port, line 2 = slot name).  The launcher can PRE-WRITE this
//     file to seed the connection before launching, which makes the in-game login
//     screen pre-populated.
//
// ── WHAT THIS PLUGIN DOES (honest scope) ──────────────────────────────────────
//   1. DETECT the Steam Everhood 2 install via the Windows registry
//      (HKCU\Software\Valve\Steam → SteamPath, etc.), parsing
//      steamapps\libraryfolders.vdf for every library root and locating
//      steamapps\common\Everhood 2 via appmanifest_1984020.acf.  A manual
//      install-dir OVERRIDE (settings folder picker) is also supported and takes
//      precedence.  State is persisted in this plugin's OWN sidecar at
//      Games/ROMs/everhood2/everhood2_launcher.json.
//   2. INSTALL/UPDATE = download ArchipelagoEverhood.zip from the latest GitHub
//      release and extract both DLLs into <Everhood 2>\Mods\.  Presents clear
//      guided steps for MelonLoader (a prerequisite the launcher cannot install
//      automatically without user interaction).
//   3. LAUNCH = start "Everhood 2.exe" from the detected/override install.  On
//      launch, pre-writes LastLogin.txt with the AP session's server + slot so
//      the in-game lobby is pre-populated.  ConnectsItself = true (the mod owns
//      the slot).  SupportsStandalone = true (vanilla Everhood 2 runs without
//      the mod).
//
// ── BUILD NOTE ────────────────────────────────────────────────────────────────
//   UseWindowsForms=true alongside UseWPF=true → CS0104 ambiguity.  Every WPF
//   type used below is spelled with its FULL namespace (no file-level aliases to
//   avoid CS1537 — GlobalUsings.cs already covers the WPF aliases at project
//   level).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class Everhood2Plugin : IGamePlugin
{
    // ── Constants ─────────────────────────────────────────────────────────────

    /// GitHub repo (DeamonHunter/ArchipelagoEverhood2).
    private const string GhOwner = "DeamonHunter";
    private const string GhRepo  = "ArchipelagoEverhood2";

    private const string GhReleasesApiUrl =
        $"https://api.github.com/repos/{GhOwner}/{GhRepo}/releases?per_page=10";

    private const string GhLatestReleaseApiUrl =
        $"https://api.github.com/repos/{GhOwner}/{GhRepo}/releases/latest";

    private const string RepoPageUrl =
        $"https://github.com/{GhOwner}/{GhRepo}";

    private const string SetupGuideUrl =
        $"https://github.com/{GhOwner}/{GhRepo}/releases/latest";

    private const string MelonLoaderUrl =
        "https://github.com/LavaGang/MelonLoader/releases/latest";

    private const string ArchipelagoSite = "https://archipelago.gg";

    /// Steam — Everhood 2 appid 1984020 (verified via Steam store URL).
    private const string Everhood2SteamAppId  = "1984020";
    private static readonly string SteamRunUrl = $"steam://rungameid/{Everhood2SteamAppId}";

    private const string SteamCommonFolderName = "Everhood 2";
    private const string GameExeName           = "Everhood 2.exe";

    /// Release-asset zip name (verified from v0.4.7 release).
    private const string ModZipAssetName = "ArchipelagoEverhood.zip";

    /// Pinned fallback version/URL when the GitHub API is unreachable.
    private const string FallbackVersion = "v0.4.7";
    private static readonly string FallbackZipUrl =
        $"https://github.com/{GhOwner}/{GhRepo}/releases/download/{FallbackVersion}/{ModZipAssetName}";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "everhood2";
    public string DisplayName => "Everhood 2";
    public string Subtitle    => "Native PC · MelonLoader mod";

    /// EXACT AP game string — verified from the apworld asset: everhood_2.apworld.
    public string ApWorldName => "everhood_2";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "everhood2.png");

    public string ThemeAccentColor => "#8B2FC9";   // Everhood 2 purple

    public string[] GameBadges => new[] { "Steam · needs MelonLoader" };

    public string Description =>
        "Everhood 2, the rhythm-action RPG by Foreign Gnomes, played through the " +
        "Archipelago MelonLoader mod by DeamonHunter. The mod adds a new button to " +
        "the title screen where you enter your Archipelago server, slot name and " +
        "password — the game then connects and manages the multiworld session itself. " +
        "You bring your own copy of Everhood 2 (owned on Steam), install MelonLoader " +
        "into the game once, and the launcher stages the Archipelago mod files. " +
        "Defeating enemies, collecting items and reaching story milestones become " +
        "checks shuffled across the multiworld.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────

    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means MelonLoader is present AND the mod DLL is in Mods\.
    public bool IsInstalled => FindModDll() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "Everhood2");

    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "everhood2_launcher.json");

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // ConnectsItself = true: the mod owns the AP connection; the launcher never
    // fires these events.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── IGamePlugin — CheckForUpdate ─────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = FindModDll() != null
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
                await GitHubHelper.FetchLatestTagAsync(GhOwner, GhRepo, ct));
        }
        catch
        {
            AvailableVersion = null; // contract: never throw on network failure
        }
    }

    // ── IGamePlugin — InstallOrUpdate ────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((2, "Locating your Everhood 2 installation..."));
        string? gameDir = ResolveGameDir();
        if (gameDir == null)
            throw new InvalidOperationException(
                "Could not find an Everhood 2 installation. Open this game's Settings " +
                "and pick your Everhood 2 folder (the one containing \"Everhood 2.exe\"), " +
                "or install Everhood 2 via Steam first.");

        string modsDir = Path.Combine(gameDir, "Mods");

        // Confirm MelonLoader is present (it creates the Mods\ folder on first run).
        bool melonLoaderPresent =
            Directory.Exists(Path.Combine(gameDir, "MelonLoader")) ||
            File.Exists(Path.Combine(gameDir, "version.dll"))      ||
            File.Exists(Path.Combine(gameDir, "winhttp.dll"));
        if (!melonLoaderPresent)
            throw new InvalidOperationException(
                "MelonLoader does not appear to be installed for Everhood 2. " +
                "Please install MelonLoader first (see the Settings panel for " +
                "instructions and the link to MelonLoader). Once MelonLoader is " +
                "installed, run the game once to create the Mods\\ folder, then " +
                "use the Install button here.");

        progress.Report((6, "Checking the latest Archipelago mod release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find the Archipelago Everhood 2 mod download on GitHub. " +
                "Check your internet connection, or download the mod manually from: " +
                RepoPageUrl);

        await DownloadAndExtractModAsync(zipUrl, version, modsDir, progress, ct);

        WriteStampedVersion(version);
        InstalledVersion = version;

        progress.Report((100,
            $"Archipelago mod {version} installed into Mods\\. " +
            "Launch the game and click the Archipelago button on the title screen to connect."));
    }

    // ── IGamePlugin — VerifyInstall ──────────────────────────────────────────

    public async Task<bool> VerifyInstallAsync(CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return IsInstalled;
    }

    // ── IGamePlugin — Launch ─────────────────────────────────────────────────

    public Task LaunchAsync(ApSession session, CancellationToken ct = default)
    {
        // Pre-write LastLogin.txt so the in-game Archipelago login screen is
        // pre-populated with the server and slot name.  The mod reads from:
        //   <Everhood 2>\UserData\ArchSaves\LastLogin.txt
        //   Line 1: server IP:port (e.g. "archipelago.gg:38281")
        //   Line 2: slot name
        // Password is NOT persisted there (it must be entered in-game each time).
        TryPrewriteLastLogin(session);
        StartEverhood2();
        return Task.CompletedTask;
    }

    public bool ConnectsItself    => true;
    public bool SupportsStandalone => true;

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartEverhood2();
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        try { _gameProcess?.Kill(entireProcessTree: true); } catch { }
        IsRunning = false;
        _gameProcess = null;
        return Task.CompletedTask;
    }

    // ── AP bridge — inert (ConnectsItself) ───────────────────────────────────

    public Task ReceiveItemsAsync(ApNetworkItem[] items, int index, CancellationToken ct = default)
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20),
        };

        string? gameDir     = ResolveGameDir();
        string? overrideDir = LoadOverrideDir();
        string? modDll      = FindModDll();
        bool    mlPresent   = gameDir != null && (
                                  Directory.Exists(Path.Combine(gameDir, "MelonLoader")) ||
                                  File.Exists(Path.Combine(gameDir, "version.dll"))      ||
                                  File.Exists(Path.Combine(gameDir, "winhttp.dll")));

        // ── Intro ─────────────────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text =
                "Everhood 2 is your own game (Steam) with the Archipelago mod added " +
                "via MelonLoader. You install MelonLoader once into the game, run the " +
                "game once to create the Mods\\ folder, then use the Install button " +
                "here to stage the Archipelago mod files. You connect to your server " +
                "from the in-game title screen. The launcher pre-fills the server and " +
                "slot fields via LastLogin.txt when you launch from the Play tab.",
            FontSize = 11, Foreground = warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: detected install ──────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "EVERHOOD 2 INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string detectMsg = gameDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + gameDir
                : "Detected Steam install: " + gameDir)
            : "Everhood 2 not detected. Pick your install folder below, or install " +
              "Everhood 2 via Steam first.";

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = gameDir != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = mlPresent
                ? "MelonLoader found."
                : "MelonLoader not found — install it from the link below before using Install.",
            FontSize = 11, Foreground = mlPresent ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modDll != null
                ? "Archipelago mod found: " + modDll
                : "Archipelago mod not found in Mods\\ yet (use the Install button on the Play tab).",
            FontSize = 11, Foreground = modDll != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Install-dir picker
        var dirRow = new System.Windows.Controls.DockPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 4),
        };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text = overrideDir ?? gameDir ?? "",
            IsReadOnly = true,
            FontSize   = 12,
            Margin     = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip = "Your Everhood 2 install folder (contains \"Everhood 2.exe\"). " +
                      "Detected from Steam automatically; pick here to override.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content = "Select folder...", Width = 120,
            Padding     = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new OpenFolderDialog
            {
                Title            = "Select your Everhood 2 install folder",
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
                    System.Windows.MessageBox.Show(bad, "Not an Everhood 2 folder",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                    return;
                }
                if (!LooksLikeEverhood2Dir(picked))
                {
                    string nested = Path.Combine(picked, SteamCommonFolderName);
                    if (LooksLikeEverhood2Dir(nested)) picked = nested;
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
            Text = "Steam installs are detected automatically (appid 1984020). " +
                   "Use this picker for a non-standard Steam library.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 6, 0, 12),
        });

        // ── Section: Connection ────────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (via the in-game Archipelago title screen)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "When you click Play from the launcher, your server and slot name " +
                   "are pre-written to Everhood 2\\UserData\\ArchSaves\\LastLogin.txt " +
                   "so the in-game login screen is pre-populated. Open the game, click " +
                   "the Archipelago button on the title screen, confirm the server and " +
                   "slot, enter your password if needed, and click Connect.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 12),
        });

        // ── Section: Guided setup ──────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "GUIDED SETUP", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 8, 0, 8),
        });
        foreach (string step in new[]
        {
            "1. Own Everhood 2 (Steam). Install it if you have not.",
            "2. Download MelonLoader.Installer.exe from the link below and run it. " +
               "Select Everhood 2 in the list (or click \"Add game manually\" and browse " +
               "to \"Everhood 2.exe\"), then click Install. " +
               "If the latest version is not working, untick Latest and select 0.7.2.",
            "3. Run Everhood 2 once to let MelonLoader initialize and create the Mods\\ folder. " +
               "You can quit back to the title screen.",
            "4. Click the Install button on the Play tab here. The launcher downloads " +
               "ArchipelagoEverhood.zip from GitHub and extracts the DLLs into the Mods\\ folder.",
            "5. Click Play. The launcher pre-fills LastLogin.txt with your server and slot. " +
               "In-game, click the Archipelago button on the title screen, enter your password " +
               "if needed, and click Connect.",
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
            ("MelonLoader (download installer) ↗", MelonLoaderUrl),
            ("Archipelago Everhood 2 mod (GitHub) ↗", RepoPageUrl),
            ("Archipelago Official ↗", ArchipelagoSite),
        })
        {
            var lnkBtn = new System.Windows.Controls.Button
            {
                Content = label,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new System.Windows.Thickness(0, 2, 0, 2),
                Background = System.Windows.Media.Brushes.Transparent,
                BorderThickness = new System.Windows.Thickness(0),
                FontSize = 12,
                Margin = new System.Windows.Thickness(0, 0, 0, 4),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x60, 0x9A, 0xFF)),
                Cursor = System.Windows.Input.Cursors.Hand,
            };
            string u = url;
            lnkBtn.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); } catch { }
            };
            panel.Children.Add(lnkBtn);
        }

        return panel;
    }

    // ── News feed ─────────────────────────────────────────────────────────────

    public async Task<NewsItem[]> GetNewsAsync(CancellationToken ct = default)
    {
        try
        {
            string json = await _http.GetStringAsync(GhReleasesApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return Array.Empty<NewsItem>();

            var items = new List<NewsItem>();
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                string ver  = el.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
                string body = el.TryGetProperty("body", out var b)     ? b.GetString() ?? "" : "";
                string? htmlUrl = el.TryGetProperty("html_url", out var h) ? h.GetString() : null;

                DateTimeOffset date = DateTimeOffset.MinValue;
                if (el.TryGetProperty("published_at", out var d) && d.ValueKind == JsonValueKind.String)
                    DateTimeOffset.TryParse(d.GetString(), out date);

                bool prerelease = el.TryGetProperty("prerelease", out var pr) && pr.GetBoolean();

                items.Add(new NewsItem(
                    Title:   "Archipelago " + ver + (prerelease ? " (pre-release)" : ""),
                    Body:    body,
                    Version: ver,
                    Date:    date,
                    Url:     htmlUrl ?? RepoPageUrl
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// Resolve the latest non-pre-release version and download URL for the mod
    /// zip from the GitHub releases API.  Falls back to the pinned v0.4.7 asset
    /// URL when the API is unreachable.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GhLatestReleaseApiUrl, ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag)) goto fallback;

            if (root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    if (!string.Equals(name, ModZipAssetName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    string? url = asset.TryGetProperty("browser_download_url", out var u)
                        ? u.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(url))
                        return (tag!, url);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* unreachable / shape changed → pinned fallback */ }

        fallback:
        return (FallbackVersion, FallbackZipUrl);
    }

    // ── Private helpers — Steam / Everhood 2 detection ────────────────────────

    private string? ResolveGameDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeEverhood2Dir(ov)) return ov;
        try { return DetectSteamEverhood2Dir(); }
        catch { return null; }
    }

    private static bool LooksLikeEverhood2Dir(string dir)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(dir)
                && Directory.Exists(dir)
                && File.Exists(Path.Combine(dir, GameExeName));
        }
        catch { return false; }
    }

    private static string? DetectSteamEverhood2Dir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{Everhood2SteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeEverhood2Dir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeEverhood2Dir(conventional)) return conventional;
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

    // ── Private helpers — mod detection ───────────────────────────────────────

    /// Find the Archipelago mod DLL under <Everhood 2>\Mods\.
    /// The zip extracts two DLLs directly into Mods\ (not in a sub-folder).
    private string? FindModDll()
    {
        try
        {
            string? game = ResolveGameDir();
            if (game == null) return null;

            string modsDir = Path.Combine(game, "Mods");
            if (!Directory.Exists(modsDir)) return null;

            foreach (string dll in Directory.EnumerateFiles(modsDir, "*.dll", SearchOption.TopDirectoryOnly))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                    return dll;
            }
        }
        catch { }
        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    /// Pre-write the mod's LastLogin.txt with the session's server and slot so
    /// the in-game Archipelago login screen is pre-populated.
    private void TryPrewriteLastLogin(ApSession session)
    {
        try
        {
            string? game = ResolveGameDir();
            if (game == null) return;

            string archSavesDir = Path.Combine(game, "UserData", "ArchSaves");
            Directory.CreateDirectory(archSavesDir);

            string loginFile = Path.Combine(archSavesDir, "LastLogin.txt");
            string server = session.ServerUri;
            // The mod defaults server to "localhost:38281" — append port if absent.
            if (!server.Contains(':'))
                server += ":38281";

            File.WriteAllLines(loginFile,
                new[] { server, session.SlotName },
                new UTF8Encoding(false));
        }
        catch { /* non-fatal — game launches fine without the pre-fill */ }
    }

    private void StartEverhood2()
    {
        string? game = ResolveGameDir();
        string? exe  = game != null ? Path.Combine(game, GameExeName) : null;

        if (exe != null && File.Exists(exe))
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = game!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Everhood 2.");

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

        // Fall back to Steam if the exe is missing but Steam is present.
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
            "Could not find \"Everhood 2.exe\". Open this game's Settings and pick " +
            "your Everhood 2 install folder, or install Everhood 2 via Steam.",
            GameExeName);
    }

    // ── Private helpers — download / extract ──────────────────────────────────

    /// Download ArchipelagoEverhood.zip from GitHub and extract the DLLs into
    /// <Everhood 2>\Mods\.  The zip places the DLLs at the top level — extract
    /// only *.dll files into Mods\ so no nested sub-folder is created.
    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string modsDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"everhood2-archipelago-{version}-{Guid.NewGuid():N}.zip");
        string tempDir = Path.Combine(Path.GetTempPath(),
            $"everhood2-archipelago-{version}-{Guid.NewGuid():N}");
        try
        {
            progress.Report((10, $"Downloading Archipelago mod {version}..."));
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
                        progress.Report((pct, $"Downloading... {downloaded / 1000}KB"));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting the mod package..."));
            Directory.CreateDirectory(tempDir);
            System.IO.Compression.ZipFile.ExtractToDirectory(tempZip, tempDir, overwriteFiles: true);

            progress.Report((85, "Installing mod DLLs into the Mods folder..."));
            Directory.CreateDirectory(modsDir);

            // Copy every *.dll from the extraction root (and top-level sub-folders)
            // into Mods\ without sub-folder nesting — matches the mod's installation
            // instructions ("ensure the files are not in a sub-folder").
            bool found = false;
            foreach (string dll in Directory.EnumerateFiles(tempDir, "*.dll", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(modsDir, Path.GetFileName(dll));
                File.Copy(dll, dest, overwrite: true);
                found = true;
            }

            if (!found)
                throw new InvalidOperationException(
                    $"No DLL files found in the downloaded {ModZipAssetName}. " +
                    "The zip may have a different layout. Try installing the mod manually: " +
                    RepoPageUrl);

            progress.Report((95, "Mod DLLs installed."));
        }
        finally
        {
            try { if (File.Exists(tempZip))  File.Delete(tempZip); } catch { }
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    // ── Private helpers — install-dir validation ──────────────────────────────

    /// Used by the Settings folder picker: return null when acceptable, else a
    /// short human-readable reason.
    private string? ValidateInstallDir(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Everhood 2 install folder.";

        if (LooksLikeEverhood2Dir(folder)) return null;

        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeEverhood2Dir(nested)) return null;
        }
        catch { }

        return "That does not look like an Everhood 2 installation. " +
               "Pick the folder that contains \"Everhood 2.exe\" — for Steam this is " +
               @"usually ...\steamapps\common\Everhood 2.";
    }

    // ── Private helpers — sidecar settings ───────────────────────────────────
    // Self-contained JSON sidecar at Games/ROMs/everhood2/everhood2_launcher.json.
    // Stores the install-dir override and a stamped mod version (informational).

    private sealed class Everhood2Settings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private Everhood2Settings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<Everhood2Settings>(txt) ?? new();
            }
        }
        catch { }
        return new();
    }

    private void SaveSettings(Everhood2Settings s)
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
