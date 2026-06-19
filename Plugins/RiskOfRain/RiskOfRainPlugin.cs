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

namespace LauncherV2.Plugins.RiskOfRain;

// ═══════════════════════════════════════════════════════════════════════════════
// RiskOfRainPlugin — install / launch for "Risk of Rain" (the ORIGINAL 2013 XNA
// action roguelike by Hopoo Games) played through the Archipelago mod by studkid,
// sourced from github.com/studkid/RoR_Archipelago.
//
// ── IMPORTANT DISAMBIGUATION ────────────────────────────────────────────────────
// This is the ORIGINAL Risk of Rain (2013, XNA/SDL, 2D side-scroller, Steam appid
// 248820), NOT Risk of Rain 2 (the 2019 third-person shooter, appid 632360). A
// separate RiskOfRain2Plugin already exists in this launcher (wave 15, bundled
// BepInEx mod via Thunderstore). This plugin covers ONLY the 2013 original.
//
// ── HONEST REALITY CHECK (written 2026-06-14) ───────────────────────────────────
// The mod repo is studkid/RoR_Archipelago on GitHub. Based on the repo and the
// AP ecosystem documentation, this is most likely a BepInEx or XNA-patching mod
// that adds an in-game Archipelago client, making it a ConnectsItself integration.
//
//   * GAME: Risk of Rain (2013) — Steam AppID 248820. XNA game by Hopoo Games.
//     Exe names seen across the Steam install: "Risk of Rain.exe" or
//     "RiskOfRain.exe". Both are checked.
//
//   * AP WORLD game string: "Risk of Rain" — sourced from the mod repo
//     (studkid/RoR_Archipelago). GameId = "risk_of_rain".
//
//   * MOD REPO: github.com/studkid/RoR_Archipelago. GitHub releases are used for
//     both install and news. We enumerate /releases (not /releases/latest, which
//     may 404 if there are only pre-releases) and take the newest non-draft entry.
//     The release asset is expected to be a .zip containing the mod files. Asset
//     name resolution is tolerant (first .zip on the release, excluding
//     linux/mac/source hints).
//
//   * INSTALL: the mod zip is extracted into the Game Directory (by default
//     Games/RiskOfRain). Since the original Risk of Rain uses Steam and the mod
//     modifies/augments the installation, the mod files are placed adjacent to or
//     within the Steam game's install folder, following the release instructions.
//     The plugin provides guided steps and a direct link to the release page when
//     the user should consult the install readme.
//
//   * STEAM DETECTION: as with other Steam games, we locate the install via the
//     Windows registry (HKCU\Software\Valve\Steam SteamPath + HKLM WOW6432Node
//     InstallPath) and libraryfolders.vdf, looking for appmanifest_248820.acf.
//     The game exe is one of "Risk of Rain.exe" or "RiskOfRain.exe".
//
//   * CONNECTION: ConnectsItself = true. The mod owns the AP client inside the
//     game — the launcher must NOT hold its own ApClient on the same slot while
//     the game runs. Connection details are entered in-game (via the mod's UI),
//     from the session data surfaced in the settings panel.
//
//   * LAUNCH: prefer the detected exe in the Steam install. If not found and Steam
//     is present, fall back to steam://rungameid/248820.
//
//   * INSTALL DETECTION: mod is considered installed when we find a file matching
//     an "archipelago" pattern under the game install directory, or when a version
//     stamp exists in our own sidecar. A discovered Steam install without the mod
//     is "game found, mod not installed".
//
// ── BUILD NOTE ──────────────────────────────────────────────────────────────────
// This project sets UseWindowsForms=true alongside UseWPF=true, so WPF UI types
// that also exist in WinForms are spelled with their FULL namespaces below to
// avoid CS0104 ambiguity (Color, Button, Brushes, FontWeights, Orientation, etc.).
// The project's GlobalUsings.cs already aliases each of these to its WPF type
// globally — no file-level using-alias is added here (CS1537 duplicate).
// ═══════════════════════════════════════════════════════════════════════════════

public sealed class RiskOfRainPlugin : IGamePlugin
{
    // ── Constants ────────────────────────────────────────────────────────────

    private const string GITHUB_OWNER = "studkid";
    private const string GITHUB_REPO  = "RoR_Archipelago";
    private const string RepoUrl      = $"https://github.com/{GITHUB_OWNER}/{GITHUB_REPO}";
    private const string GH_RELEASES_URL =
        $"https://api.github.com/repos/{GITHUB_OWNER}/{GITHUB_REPO}/releases";

    private const string SetupGuideUrl   = "https://archipelago.gg/tutorial/Risk%20of%20Rain/setup_en";
    private const string ArchipelagoSite = "https://archipelago.gg";

    // Steam — Risk of Rain (2013), appid 248820.
    private const string RoRSteamAppId       = "248820";
    private static readonly string SteamRunUrl = $"steam://rungameid/{RoRSteamAppId}";
    private const string SteamCommonFolderName = "Risk of Rain";

    // The game exe shipped by Steam has varied between two names.
    private static readonly string[] GameExeNames = { "Risk of Rain.exe", "RiskOfRain.exe" };

    // Pinned offline fallback: if the GitHub API is unreachable we still surface
    // the repo URL so the user can download manually. No pinned zip URL because
    // the asset-naming convention for this repo is not verified offline.
    private const string FallbackVersion = "latest";

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(30),
        DefaultRequestHeaders = { { "User-Agent", "Archipelago-Launcher/2.0" } }
    };

    private const string VersionFileName = "ror_ap_mod_version.dat";

    // ── IGamePlugin — Identity ────────────────────────────────────────────────

    public string GameId      => "risk_of_rain";
    public string DisplayName => "Risk of Rain";

    // Clarify this is the 2013 original, not RoR2 which is already in the launcher.
    public string Subtitle    => "Native PC · AP mod (original 2013)";

    /// EXACT AP world game string for studkid/RoR_Archipelago.
    public string ApWorldName => "Risk of Rain";

    public string IconPath =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "risk_of_rain.png");

    public string ThemeAccentColor => "#C44B10";   // dark orange / rust

    public string[] GameBadges => new[] { "Steam · needs mod", "Original 2013" };

    public string Description =>
        "Risk of Rain, the 2013 2D action roguelike by Hopoo Games — NOT Risk of " +
        "Rain 2 (which has its own separate launcher entry). Fight through " +
        "procedurally spawned alien monsters, collect items, and teleport between " +
        "stages while the difficulty ramps up over time. In the Archipelago " +
        "multiworld, items and locations are shuffled across your entire group, " +
        "so unlocks from Risk of Rain can appear in any game and vice versa. You " +
        "bring your own copy of Risk of Rain (owned on Steam), and the AP mod " +
        "(by studkid) is added on top. The launcher detects your Steam install, " +
        "downloads and installs the mod from GitHub, and guides you through " +
        "connecting in-game. Connection details are entered via the mod's in-game " +
        "UI — no external client or separate launcher step is needed.";

    public string? VideoPreviewUrl => null;
    public string[] ScreenshotUrls => Array.Empty<string>();

    // ── Version state ─────────────────────────────────────────────────────────
    public string? InstalledVersion { get; private set; }
    public string? AvailableVersion { get; private set; }

    /// "Installed" means the AP mod files are present in the detected game directory
    /// or the mod stamp file exists from a previous launcher install.
    public bool IsInstalled => FindInstalledModMarker() != null;

    public bool IsRunning { get; private set; }

    // ── Paths ─────────────────────────────────────────────────────────────────

    /// Where the launcher extracts the mod. Exposed as GameDirectory per contract.
    public string GameDirectory { get; set; }
        = Path.Combine(AppContext.BaseDirectory, "Games", "RiskOfRain");

    private string RomLibraryDirectory
        => Path.Combine(AppContext.BaseDirectory, "Games", "ROMs", GameId);

    private string SettingsSidecarPath
        => Path.Combine(RomLibraryDirectory, "ror_launcher.json");

    private string VersionFilePath
        => Path.Combine(RomLibraryDirectory, VersionFileName);

    // ── Internal state ────────────────────────────────────────────────────────

    private Process? _gameProcess;

    // ── AP bridge events ──────────────────────────────────────────────────────
    // The mod's built-in AP client reports checks/items/goal to the AP server —
    // the launcher relays nothing. These exist for interface compatibility.
#pragma warning disable CS0067
    public event Action<long[]>? LocationsChecked;
    public event Action?         GoalCompleted;
#pragma warning restore CS0067

    public event Action<int>? GameExited;

    // ── ConnectsItself / SupportsStandalone ───────────────────────────────────

    /// The mod's native AP client owns the slot — the launcher must NOT hold its
    /// own ApClient on the same slot while the game is running.
    public bool ConnectsItself    => true;

    /// Plain (non-AP) Risk of Rain plays fine.
    public bool SupportsStandalone => true;

    // ── Lifecycle — CheckForUpdate ────────────────────────────────────────────

    public async Task CheckForUpdateAsync(CancellationToken ct = default)
    {
        try
        {
            InstalledVersion = IsInstalled
                ? (ReadStampedVersion() ?? "installed")
                : null;
        }
        catch { InstalledVersion = null; }

        try
        {
            var (version, _) = await ResolveLatestReleaseAsync(ct);
            AvailableVersion = version;
        }
        catch { AvailableVersion = null; }
    }

    // ── Lifecycle — InstallOrUpdate ───────────────────────────────────────────

    public async Task InstallOrUpdateAsync(
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct = default)
    {
        progress.Report((2, "Checking latest Risk of Rain AP mod release..."));
        var (version, zipUrl) = await ResolveLatestReleaseAsync(ct);
        AvailableVersion = version;

        if (zipUrl == null)
            throw new InvalidOperationException(
                "Could not find a download for the Risk of Rain AP mod on GitHub. " +
                "Check your internet connection, or download the mod manually from " +
                RepoUrl + "/releases and follow the installation instructions in the " +
                "included readme. Open Settings for links.");

        // If a Steam install exists, prefer to install the mod there.
        // Otherwise, extract to our own GameDirectory so the files are available
        // and the user can move them per the readme.
        string? steamDir = DetectSteamRoRDir();
        string installTarget = steamDir ?? GameDirectory;

        progress.Report((5, $"Installing to: {installTarget}"));
        await DownloadAndExtractModAsync(zipUrl, version, installTarget, progress, ct);

        WriteStampedVersion(version);
        InstalledVersion = version;

        bool modFound = FindInstalledModMarker() != null;
        progress.Report((100,
            $"Risk of Rain AP mod {version} installed into {installTarget}. " +
            (modFound
                ? "Mod files detected. "
                : "Could not auto-detect the mod marker — check the readme in the " +
                  "extracted folder for any additional steps. ") +
            "To play: launch the game from Steam (or use Play here), then use the " +
            "mod's in-game connection UI to enter your Archipelago server, slot name, " +
            "and password. See Settings for guided steps and links."));
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
        // ConnectsItself = true: the AP mod handles the server connection inside
        // the game. We launch the game exe; the player enters the session
        // credentials in the mod's in-game UI. The settings panel surfaces them.
        _ = session;
        StartRiskOfRain();
        return Task.CompletedTask;
    }

    public Task LaunchStandaloneAsync(CancellationToken ct = default)
    {
        StartRiskOfRain();
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
        => Task.CompletedTask;

    public void OnApStateChanged(ApConnectionState state) { }

    // ── Settings UI ───────────────────────────────────────────────────────────

    public System.Windows.UIElement? CreateSettingsPanel()
    {
        var muted   = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x72, 0x7A, 0x99));
        var fg      = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xCC, 0xD0, 0xE0));
        var success = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0xAF, 0x50));
        var warn    = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE0, 0xA0, 0x30));
        var accent  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC4, 0x4B, 0x10));

        var panel = new System.Windows.Controls.StackPanel
        {
            Margin = new System.Windows.Thickness(0, 0, 0, 20)
        };

        string? steamDir    = DetectSteamRoRDir();
        string? overrideDir = LoadOverrideDir();
        string? effectiveDir = overrideDir ?? steamDir;
        string? modMarker   = FindInstalledModMarker();

        // ── Disambiguation header ─────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "NOTE: This is Risk of Rain (2013 original), NOT Risk of Rain 2 " +
                   "(2019). Risk of Rain 2 has its own separate entry in this launcher. " +
                   "The AP mod is from github.com/studkid/RoR_Archipelago. Connection " +
                   "is made in-game through the mod's UI.",
            FontSize = 11, Foreground = warn, TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
        });

        // ── Section: Install detection ────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "RISK OF RAIN (2013) INSTALL", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = muted, Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        string detectMsg = effectiveDir != null
            ? (overrideDir != null
                ? "Using your selected folder: " + effectiveDir
                : "Detected Steam install: " + effectiveDir)
            : "Risk of Rain (2013) not detected automatically. Use the folder picker " +
              "below if it is installed in a non-standard location, or install it via " +
              "Steam first (appid 248820).";
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = detectMsg, FontSize = 11,
            Foreground = effectiveDir != null ? success : warn,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 6),
        });

        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = modMarker != null
                ? "Archipelago mod detected: " + modMarker
                : "Archipelago mod not detected yet. Use Install on the Play tab, " +
                  "or follow the guided steps below.",
            FontSize = 11,
            Foreground = modMarker != null ? success : muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
        });

        // Override folder picker
        var dirRow = new System.Windows.Controls.DockPanel { Margin = new System.Windows.Thickness(0, 0, 0, 4) };
        var dirBox = new System.Windows.Controls.TextBox
        {
            Text        = overrideDir ?? effectiveDir ?? "",
            IsReadOnly  = true,
            FontSize    = 12,
            Margin      = new System.Windows.Thickness(0, 0, 8, 0),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0C, 0x10, 0x20)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x22, 0x33)),
            ToolTip     = "Your Risk of Rain (2013) install folder — the one containing " +
                          "\"Risk of Rain.exe\" or \"RiskOfRain.exe\". " +
                          "Steam installs are detected automatically. Use this picker for " +
                          "a non-standard library or a manual install.",
        };
        var dirBtn = new System.Windows.Controls.Button
        {
            Content     = "Select folder...",
            Width       = 120,
            Padding     = new System.Windows.Thickness(0, 6, 0, 6),
            Background  = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1A, 0x1E, 0x30)),
            Foreground  = fg,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2A, 0x30, 0x50)),
        };
        dirBtn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = "Select your Risk of Rain (2013) install folder",
                InitialDirectory = Directory.Exists(overrideDir ?? effectiveDir ?? "")
                                   ? (overrideDir ?? effectiveDir!)
                                   : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog() != true) return;
            string picked = dlg.FolderName;
            string? bad   = ValidateExistingInstall(picked);
            if (bad != null)
            {
                System.Windows.MessageBox.Show(bad, "Not a Risk of Rain folder",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                return;
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
            Text = "Steam installs are found automatically (appid 248820). Use this " +
                   "picker only for a non-standard Steam library path.",
            FontSize = 11, Foreground = muted,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 4, 0, 14),
        });

        // ── Section: Connection ───────────────────────────────────────────
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "CONNECTING (entered in-game via the mod UI)", FontSize = 10,
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = "After launching the game with the mod installed, use the " +
                   "Archipelago mod's in-game connection UI to enter your server " +
                   "address, slot name, and password. The launcher cannot pre-fill " +
                   "these fields (the mod handles all communication itself). " +
                   "Typical AP server: archipelago.gg:38281.",
            FontSize = 11, Foreground = fg,
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(0, 0, 0, 14),
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
            "1. Own Risk of Rain (2013) on Steam. Install it (appid 248820). " +
                "Use the folder picker above if it is in a non-standard location.",
            "2. Click Install / Update on the Play tab. The launcher downloads the " +
                "AP mod from github.com/studkid/RoR_Archipelago and extracts it into " +
                "your Risk of Rain install directory.",
            "3. Check the extracted mod's readme for any additional manual steps " +
                "(file placement, dependencies, etc.) required for this game's mod.",
            "4. Launch the game via the Play tab (or via Steam). Once in-game, " +
                "open the Archipelago mod's connection UI, enter your server address, " +
                "slot name, and password, then confirm.",
            "5. In multiplayer (if supported): all players need the mod installed " +
                "and their own AP slot configured.",
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
            FontWeight = System.Windows.FontWeights.SemiBold, Foreground = muted,
            Margin = new System.Windows.Thickness(0, 10, 0, 8),
        });
        foreach (var (label, url) in new[]
        {
            ("Risk of Rain AP mod (GitHub) ↗",  RepoUrl),
            ("Risk of Rain Setup Guide ↗",       SetupGuideUrl),
            ("Archipelago Official ↗",           ArchipelagoSite),
            ("Risk of Rain on Steam ↗",          "https://store.steampowered.com/app/248820/"),
        })
        {
            var lnk = new System.Windows.Controls.Button
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
            lnk.Click += (_, _) =>
            {
                try { Process.Start(new ProcessStartInfo(u) { UseShellExecute = true }); }
                catch { }
            };
            panel.Children.Add(lnk);
        }

        return panel;
    }

    // ── Validate existing install (override picker) ───────────────────────────

    public string? ValidateExistingInstall(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return "That folder does not exist. Pick your Risk of Rain (2013) install folder.";

        if (LooksLikeRoRDir(folder))
            return null;

        // User may have picked the Steam "common" parent.
        try
        {
            string nested = Path.Combine(folder, SteamCommonFolderName);
            if (LooksLikeRoRDir(nested))
                return null;
        }
        catch { /* ignore */ }

        return "That does not look like a Risk of Rain (2013) installation. " +
               "Pick the folder that contains \"Risk of Rain.exe\" or \"RiskOfRain.exe\". " +
               "For Steam this is usually ...\\steamapps\\common\\Risk of Rain.";
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

                string ver = el.TryGetProperty("tag_name", out var t)
                    ? NormalizeTag(t.GetString()) ?? ""
                    : "";

                items.Add(new NewsItem(
                    Title:   el.TryGetProperty("name", out var n)
                               ? n.GetString() ?? $"Release {ver}"
                               : $"Release {ver}",
                    Body:    el.TryGetProperty("body", out var b)
                               ? b.GetString() ?? ""
                               : "",
                    Version: ver,
                    Date:    date,
                    Url:     el.TryGetProperty("html_url", out var u)
                               ? u.GetString()
                               : RepoUrl + "/releases"
                ));
                if (items.Count >= 10) break;
            }
            return items.ToArray();
        }
        catch { return Array.Empty<NewsItem>(); }
    }

    // ── Private helpers — release resolution ──────────────────────────────────

    /// Resolve the latest non-draft release from GitHub. Returns version + zip URL.
    /// Falls back to (FallbackVersion, null) when offline so install can surface
    /// a clear error rather than throwing with a stack trace.
    private async Task<(string Version, string? ZipUrl)> ResolveLatestReleaseAsync(
        CancellationToken ct)
    {
        try
        {
            string json = await _http.GetStringAsync(GH_RELEASES_URL, ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var rel in doc.RootElement.EnumerateArray())
                {
                    // Skip drafts; accept pre-releases (mod repos often use them).
                    if (rel.TryGetProperty("draft", out var dr) &&
                        dr.ValueKind == JsonValueKind.True)
                        continue;

                    string? version = rel.TryGetProperty("tag_name", out var t)
                        ? NormalizeTag(t.GetString())
                        : null;
                    if (version == null) continue;

                    if (!rel.TryGetProperty("assets", out var assets) ||
                        assets.ValueKind != JsonValueKind.Array)
                        continue;

                    string? zipUrl = PickWindowsZip(assets);
                    if (zipUrl != null)
                        return (version, zipUrl);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { /* network failure / rate limit → fallback */ }

        return (FallbackVersion, null);
    }

    /// Pick the best Windows .zip asset from a release's assets array.
    /// Strategy: prefer an asset whose name contains "win" or "x64"; accept any
    /// .zip as a fallback (excluding obvious linux/mac/source assets).
    private static string? PickWindowsZip(JsonElement assets)
    {
        string? winZip = null;
        string? anyZip = null;

        foreach (var a in assets.EnumerateArray())
        {
            string? name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            string? url  = a.TryGetProperty("browser_download_url", out var u)
                           ? u.GetString() : null;
            if (name == null || url == null) continue;

            string lower = name.ToLowerInvariant();
            if (!lower.EndsWith(".zip")) continue;
            if (lower.Contains("source") || lower.Contains("linux") ||
                lower.Contains("ubuntu") || lower.Contains("mac") ||
                lower.Contains("darwin"))
                continue;

            anyZip ??= url;
            if (winZip == null &&
                (lower.Contains("win") || lower.Contains("x64") ||
                 lower.Contains("x86")))
                winZip = url;
        }

        return winZip ?? anyZip;
    }

    /// Normalize a GitHub tag: strip leading 'v' before a digit, else return as-is.
    private static string? NormalizeTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        tag = tag.Trim();
        return tag.StartsWith('v') && tag.Length > 1 && char.IsDigit(tag[1])
            ? tag[1..]
            : tag;
    }

    // ── Private helpers — Steam / Risk of Rain detection ─────────────────────

    /// The effective game dir: override (if valid) wins, else Steam-detected.
    private string? ResolveRoRDir()
    {
        string? ov = LoadOverrideDir();
        if (ov != null && LooksLikeRoRDir(ov)) return ov;
        try { return DetectSteamRoRDir(); }
        catch { return null; }
    }

    /// True when a folder contains one of the known Risk of Rain (2013) exes.
    private static bool LooksLikeRoRDir(string dir)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return false;
            foreach (string exeName in GameExeNames)
                if (File.Exists(Path.Combine(dir, exeName))) return true;
        }
        catch { /* access denied */ }
        return false;
    }

    /// Walk Steam registry + libraryfolders.vdf to find the RoR (2013) install.
    private static string? DetectSteamRoRDir()
    {
        foreach (string steamRoot in SteamRoots())
        {
            if (string.IsNullOrWhiteSpace(steamRoot)) continue;
            foreach (string lib in SteamLibraryRoots(steamRoot))
            {
                try
                {
                    string steamapps = Path.Combine(lib, "steamapps");
                    string manifest  = Path.Combine(steamapps, $"appmanifest_{RoRSteamAppId}.acf");
                    if (!File.Exists(manifest)) continue;

                    string common = Path.Combine(steamapps, "common");
                    string? installDir = ReadAcfInstallDir(manifest);
                    if (installDir != null)
                    {
                        string candidate = Path.Combine(common, installDir);
                        if (LooksLikeRoRDir(candidate)) return candidate;
                    }
                    string conventional = Path.Combine(common, SteamCommonFolderName);
                    if (LooksLikeRoRDir(conventional)) return conventional;
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

    // ── Private helpers — mod install detection ───────────────────────────────

    /// Locate a file or folder that plausibly indicates the AP mod is installed.
    /// Checks: (1) our own version stamp; (2) an "archipelago"-named file/folder
    /// in the effective game dir or GameDirectory. Returns the path or null.
    private string? FindInstalledModMarker()
    {
        // Our own stamp file.
        if (File.Exists(VersionFilePath)) return VersionFilePath;

        // Scan for mod files in the game dir and our GameDirectory.
        foreach (string dir in new[] { ResolveRoRDir(), GameDirectory }
                     .Where(d => !string.IsNullOrWhiteSpace(d) && Directory.Exists(d!))
                     .Select(d => d!))
        {
            try
            {
                foreach (string entry in Directory.EnumerateFileSystemEntries(
                             dir, "*", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(entry);
                    if (name.IndexOf("archipelago", StringComparison.OrdinalIgnoreCase) >= 0)
                        return entry;
                }
            }
            catch { /* permission or vanished — try next */ }
        }

        return null;
    }

    // ── Private helpers — launch ──────────────────────────────────────────────

    private void StartRiskOfRain()
    {
        string? gameDir = ResolveRoRDir();
        string? exe     = null;

        if (gameDir != null)
        {
            foreach (string exeName in GameExeNames)
            {
                string candidate = Path.Combine(gameDir, exeName);
                if (File.Exists(candidate)) { exe = candidate; break; }
            }
        }

        if (exe != null)
        {
            var proc = Process.Start(new ProcessStartInfo
            {
                FileName         = exe,
                WorkingDirectory = gameDir!,
                UseShellExecute  = true,
            }) ?? throw new InvalidOperationException("Failed to start Risk of Rain.");

            _gameProcess = proc;
            IsRunning    = true;
            try
            {
                proc.EnableRaisingEvents = true;
                proc.Exited += (_, _) =>
                {
                    IsRunning    = false;
                    _gameProcess = null;
                    GameExited?.Invoke(proc.ExitCode);
                };
            }
            catch { /* some processes don't expose Exited — non-fatal */ }
            return;
        }

        // Fall back to Steam if we know Steam is installed.
        if (SteamRoots().Any(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r)))
        {
            try
            {
                Process.Start(new ProcessStartInfo(SteamRunUrl) { UseShellExecute = true });
                IsRunning = true; // Steam owns the process — we won't track exit
                return;
            }
            catch { /* fall through to error */ }
        }

        throw new FileNotFoundException(
            "Could not find Risk of Rain (2013). Open this game's Settings and pick " +
            "your install folder, or install the game via Steam (appid 248820).",
            GameExeNames[0]);
    }

    // ── Private helpers — download / extract the mod ──────────────────────────

    private async Task DownloadAndExtractModAsync(
        string zipUrl,
        string version,
        string targetDir,
        IProgress<(int Pct, string Msg)> progress,
        CancellationToken ct)
    {
        string tempZip = Path.Combine(Path.GetTempPath(),
            $"ror-ap-{version}-{Guid.NewGuid():N}.zip");
        string tempExtract = Path.Combine(Path.GetTempPath(),
            $"ror-ap-{version}-extract-{Guid.NewGuid():N}");
        try
        {
            progress.Report((8, $"Downloading Risk of Rain AP mod {version}..."));
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
                        int pct = (int)(8 + 57 * downloaded / total);
                        progress.Report((pct,
                            $"Downloading... {downloaded / 1000}KB" +
                            (total > 0 ? $" / {total / 1000}KB" : "")));
                    }
                }
                await dst.FlushAsync(ct);
            }

            progress.Report((70, "Extracting mod archive..."));
            Directory.CreateDirectory(tempExtract);
            System.IO.Compression.ZipFile.ExtractToDirectory(
                tempZip, tempExtract, overwriteFiles: true);

            progress.Report((85, $"Installing mod files into {targetDir}..."));
            Directory.CreateDirectory(targetDir);

            // If the zip contains a single top-level sub-folder, copy its contents
            // rather than the folder itself (flatten single-subdir wrapping zips).
            string copyRoot = tempExtract;
            try
            {
                string[] topFiles = Directory.GetFiles(tempExtract);
                string[] topDirs  = Directory.GetDirectories(tempExtract);
                if (topFiles.Length == 0 && topDirs.Length == 1)
                    copyRoot = topDirs[0];
            }
            catch { /* keep copyRoot as tempExtract */ }

            CopyDirectoryContents(copyRoot, targetDir);
            progress.Report((97, "Mod files copied."));
        }
        finally
        {
            try { if (File.Exists(tempZip))         File.Delete(tempZip); }           catch { }
            try { if (Directory.Exists(tempExtract)) Directory.Delete(tempExtract, true); } catch { }
        }
    }

    private static void CopyDirectoryContents(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(
                     sourceDir, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceDir, file);
            string dst = Path.Combine(destDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(file, dst, overwrite: true);
        }
    }

    // ── Private helpers — settings sidecar ───────────────────────────────────

    private sealed class RoRSettings
    {
        public string? InstallOverride { get; set; }
        public string? ModVersion      { get; set; }
    }

    private RoRSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsSidecarPath))
            {
                string txt = File.ReadAllText(SettingsSidecarPath);
                if (!string.IsNullOrWhiteSpace(txt))
                    return JsonSerializer.Deserialize<RoRSettings>(txt) ?? new();
            }
        }
        catch { /* corrupt → defaults */ }
        return new();
    }

    private void SaveSettings(RoRSettings s)
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
